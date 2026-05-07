using System.Text;
using System.Text.Json;
using AmGatewayCloud.EdgeGateway.Configuration;
using AmGatewayCloud.EdgeGateway.Models;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace AmGatewayCloud.EdgeGateway.Services;

/// <summary>
/// RabbitMQ 转发器：将 DataBatch 序列化后发送到 Topic Exchange。
/// 支持断线检测、指数退避重连、路由键特殊字符转义。
/// </summary>
public sealed class RabbitMqForwarder : IAsyncDisposable
{
    private readonly RabbitMqConfig _config;
    private readonly string _queueName;
    private readonly ILogger<RabbitMqForwarder> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private volatile bool _isOnline;
    private int _reconnectAttempt;
    private int _consecutiveFailures;
    private const int OfflineThreshold = 3;

    private readonly CancellationTokenSource _cts = new();
    private Task? _reconnectLoop;
    private readonly List<Func<Task>> _onlineCallbacks = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public bool IsOnline => _isOnline;

    public RabbitMqForwarder(IOptions<EdgeGatewayConfig> config, ILogger<RabbitMqForwarder> logger)
    {
        _config = config.Value.RabbitMq;
        _queueName = _config.QueueName.Replace("{factoryId}", config.Value.FactoryId);
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
    }

    public void OnOnline(Func<Task> callback)
    {
        _onlineCallbacks.Add(callback);
    }

    public async Task<bool> ForwardAsync(DataBatch batch, WatermarkTracker? watermarkTracker, CancellationToken ct = default)
    {
        if (!_isOnline)
        {
            return false;
        }

        try
        {
            await EnsureConnectedAsync(ct);

            if (_channel == null || !_isOnline)
            {
                RecordFailure();
                return false;
            }

            var payload = JsonSerializer.Serialize(batch, JsonOpts);
            var body = Encoding.UTF8.GetBytes(payload);
            var routingKey = BuildRoutingKey(batch);

            var properties = _channel.CreateBasicProperties();
            properties.ContentType = "application/json";
            properties.DeliveryMode = 2; // persistent

            _channel.BasicPublish(
                exchange: _config.Exchange,
                routingKey: routingKey,
                basicProperties: properties,
                body: body);

            RecordSuccess();
            watermarkTracker?.UpdateWatermark(batch.Timestamp, batch.BatchId);
            _logger.LogDebug("RabbitMQ forwarded: {RoutingKey}, batch={BatchId}", routingKey, batch.BatchId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RabbitMQ forward failed for batch {BatchId}", batch.BatchId);
            RecordFailure();
            return false;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _cts.Cancel();

        if (_reconnectLoop != null)
        {
            try { await _reconnectLoop; } catch { }
        }

        try { _channel?.Close(); } catch { }
        try { _connection?.Close(); } catch { }
        _channel?.Dispose();
        _connection?.Dispose();

        _logger.LogInformation("RabbitMQ forwarder stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
        _connectLock.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_isOnline && _connection?.IsOpen == true && _channel?.IsOpen == true)
            return;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_isOnline && _connection?.IsOpen == true && _channel?.IsOpen == true)
                return;

            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = _config.HostName,
                    Port = _config.Port,
                    VirtualHost = _config.VirtualHost,
                    UserName = _config.Username,
                    Password = _config.Password,
                    AutomaticRecoveryEnabled = false, // 我们自己管理重连
                    TopologyRecoveryEnabled = false
                };

                if (_config.UseSsl)
                {
                    factory.Ssl.Enabled = true;
                }

                _connection = factory.CreateConnection();
                _channel = _connection.CreateModel();

                _channel.ExchangeDeclare(_config.Exchange, ExchangeType.Topic, durable: true);

                _channel.QueueDeclare(_queueName, durable: true, exclusive: false, autoDelete: false);
                _channel.QueueBind(_queueName, _config.Exchange, routingKey: "#");
                _logger.LogInformation("RabbitMQ queue declared: {QueueName} -> {Exchange} (#)", _queueName, _config.Exchange);

                _isOnline = true;
                _reconnectAttempt = 0;
                _consecutiveFailures = 0;
                _logger.LogInformation("RabbitMQ connected to {HostName}:{Port}, exchange={Exchange}",
                    _config.HostName, _config.Port, _config.Exchange);

                // 触发 online 回调（如 ReplayService）
                foreach (var callback in _onlineCallbacks.ToList())
                {
                    try { await callback(); } catch { /* 忽略回调异常 */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RabbitMQ connect failed to {HostName}:{Port}",
                    _config.HostName, _config.Port);
                _isOnline = false;

                if (_reconnectLoop == null)
                {
                    _reconnectLoop = ReconnectLoopAsync(_cts.Token);
                }
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _reconnectAttempt++;
                var delayMs = CalculateReconnectDelay(_reconnectAttempt);
                _logger.LogWarning("RabbitMQ reconnecting in {DelayMs}ms (attempt {Attempt})",
                    delayMs, _reconnectAttempt);

                try { await Task.Delay(delayMs, ct); }
                catch (OperationCanceledException) { break; }

                try
                {
                    await EnsureConnectedAsync(ct);
                    if (_isOnline)
                    {
                        _logger.LogInformation("RabbitMQ reconnected after {Attempt} attempts", _reconnectAttempt);
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* 继续重试 */ }
            }
        }
        finally
        {
            _reconnectLoop = null;
        }
    }

    private void RecordSuccess()
    {
        _consecutiveFailures = 0;
    }

    private void RecordFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= OfflineThreshold)
        {
            _isOnline = false;
            _logger.LogWarning("RabbitMQ marked offline after {Count} consecutive failures", OfflineThreshold);

            if (_reconnectLoop == null)
            {
                _reconnectLoop = ReconnectLoopAsync(_cts.Token);
            }
        }
    }

    private int CalculateReconnectDelay(int attempt)
    {
        var delay = _config.ReconnectDelayMs * (1 << Math.Min(attempt - 1, 6));
        return Math.Min(delay, _config.MaxReconnectDelayMs);
    }

    /// <summary>
    /// 构造路由键，对特殊字符进行转义（RabbitMQ Topic 中 . * # 是保留字符）
    /// </summary>
    private string BuildRoutingKey(DataBatch batch)
    {
        var template = _config.RoutingKeyTemplate;
        return template
            .Replace("{factoryId}", EscapeRoutingKey(batch.FactoryId))
            .Replace("{workshopId}", EscapeRoutingKey(batch.WorkshopId))
            .Replace("{deviceId}", EscapeRoutingKey(batch.DeviceId))
            .Replace("{protocol}", EscapeRoutingKey(batch.Protocol));
    }

    private static string EscapeRoutingKey(string value)
    {
        return value.Replace(".", "_").Replace("*", "_").Replace("#", "_");
    }
}
