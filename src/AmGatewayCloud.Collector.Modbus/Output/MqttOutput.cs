using System.Text;
using System.Text.Json;
using AmGatewayCloud.Collector.Modbus.Configuration;
using AmGatewayCloud.Collector.Modbus.Models;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace AmGatewayCloud.Collector.Modbus.Output;

/// <summary>
/// MQTT 数据输出通道：将 DataPoint 批量序列化为 JSON 发布到 MQTT Broker。
/// 
/// 设计要点：
/// - 懒连接：首次写入时自动连接
/// - 指数退避重连：5s → 10s → 20s → 40s → 60s 上限
/// - 断线期间数据静默丢弃，不阻塞采集主循环
/// - 批量打包：WriteBatchAsync 将整批数据合并为一条 JSON 消息
/// - Topic 格式：{TopicPrefix}/modbus/{DeviceId}
/// </summary>
public class MqttOutput : IDataOutput, IAsyncDisposable
{
    private readonly MqttConfig _config;
    private readonly string _deviceId;
    private readonly string _factoryId;
    private readonly string _workshopId;
    private readonly string? _tenantId;
    private readonly string _protocol = "modbus";
    private readonly ILogger<MqttOutput> _logger;
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly string _topic;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private volatile bool _connected;
    private int _reconnectAttempt;
    private bool _disposed;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectLoop;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public MqttOutput(
        IOptions<CollectorConfig> config,
        ILogger<MqttOutput> logger)
    {
        _config = config.Value.Mqtt;
        _deviceId = config.Value.DeviceId;
        _factoryId = config.Value.FactoryId;
        _workshopId = config.Value.WorkshopId;
        _tenantId = config.Value.TenantId;
        _logger = logger;

        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_config.Broker, _config.Port)
            .WithClientId($"{_config.ClientId}-{_deviceId}")
            .WithCleanStart(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (_config.UseTls)
        {
            builder.WithTlsOptions(tls =>
            {
                tls.UseTls();
            });
        }

        if (!string.IsNullOrEmpty(_config.Username))
            builder.WithCredentials(_config.Username, _config.Password ?? string.Empty);

        _options = builder.Build();

        _topic = $"{_config.TopicPrefix}/modbus/{_deviceId}".ToLowerInvariant();

        // 注册断线事件
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public Task WriteAsync(DataPoint point, CancellationToken ct)
        => WriteBatchAsync([point], ct);

    public async Task WriteBatchAsync(IEnumerable<DataPoint> points, CancellationToken ct)
    {
        if (_disposed) return;

        var pointList = points.ToList();
        if (pointList.Count == 0) return;

        try
        {
            await EnsureConnectedAsync(ct);

            if (!_connected) return; // 连接失败，静默丢弃

            var payload = SerializeBatch(pointList);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(_topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _client.PublishAsync(message, ct);
        }
        catch (Exception ex)
        {
            // 发布失败，静默丢弃，不阻塞采集
            _logger.LogWarning(ex, "MQTT publish failed for {DeviceId}, {Count} points discarded",
                _deviceId, pointList.Count);
            _connected = false;
        }
    }

    /// <summary>确保 MQTT 连接可用（懒连接 + 自动重连）</summary>
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connected) return;

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_connected || _disposed) return;

            try
            {
                await _client.ConnectAsync(_options, ct);
                _connected = true;
                _reconnectAttempt = 0;
                _logger.LogInformation("MQTT connected to {Broker}:{Port}, Topic: {Topic}",
                    _config.Broker, _config.Port, _topic);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT connect failed to {Broker}:{Port}",
                    _config.Broker, _config.Port);
                _connected = false;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>断线回调：启动指数退避重连循环</summary>
    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (_disposed) return Task.CompletedTask;

        _connected = false;

        // 已有重连循环在运行，无需重复启动
        if (_reconnectLoop != null) return Task.CompletedTask;

        _reconnectCts = new CancellationTokenSource();
        _reconnectLoop = ReconnectLoopAsync(_reconnectCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>指数退避重连循环：持续重试直到连接成功或被取消</summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _reconnectAttempt++;
                var delayMs = CalculateReconnectDelay(_reconnectAttempt);
                _logger.LogWarning(
                    "MQTT disconnected from {Broker}:{Port}, reconnecting in {DelayMs}ms (attempt {Attempt})",
                    _config.Broker, _config.Port, delayMs, _reconnectAttempt);

                try { await Task.Delay(delayMs, ct); }
                catch (OperationCanceledException) { break; }

                try
                {
                    await EnsureConnectedAsync(ct);
                    if (_connected)
                    {
                        _logger.LogInformation("MQTT reconnected to {Broker}:{Port} after {Attempt} attempts",
                            _config.Broker, _config.Port, _reconnectAttempt);
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // 重连失败，下一轮继续
                }
            }
        }
        finally
        {
            _reconnectLoop = null;
        }
    }

    /// <summary>指数退避：base * 2^(attempt-1)，上限 MaxReconnectDelayMs</summary>
    private int CalculateReconnectDelay(int attempt)
    {
        var delay = _config.ReconnectDelayMs * (1 << Math.Min(attempt - 1, 6));
        return Math.Min(delay, _config.MaxReconnectDelayMs);
    }

    /// <summary>将批量 DataPoint 序列化为 MQTT payload JSON</summary>
    private byte[] SerializeBatch(List<DataPoint> points)
    {
        var batch = new MqttBatch
        {
            BatchId = Guid.NewGuid(),
            TenantId = _tenantId,
            FactoryId = _factoryId,
            WorkshopId = _workshopId,
            DeviceId = _deviceId,
            Protocol = _protocol,
            Timestamp = DateTimeOffset.UtcNow,
            Points = points.Select(p => new MqttDataPoint
            {
                Tag = p.Tag,
                Value = p.Value,
                ValueType = p.ValueType,
                Quality = MapQuality(p.Quality),
                Timestamp = p.Timestamp,
                GroupName = p.Properties?.TryGetValue("GroupName", out var gn) == true
                    ? gn.ToString() : null
            }).ToList()
        };

        var json = JsonSerializer.Serialize(batch, JsonOpts);
        return Encoding.UTF8.GetBytes(json);
    }

    private static string MapQuality(DataQuality quality) => quality switch
    {
        DataQuality.Unknown => "Uncertain",
        _ => quality.ToString()
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _client.DisconnectedAsync -= OnDisconnectedAsync;

        // 停止重连循环
        _reconnectCts?.Cancel();

        try
        {
            if (_connected)
                await _client.DisconnectAsync();
        }
        catch { }

        // 等待重连循环退出
        if (_reconnectLoop != null)
        {
            try { await _reconnectLoop; } catch { }
        }
        _reconnectCts?.Dispose();

        _client.Dispose();
        _connectLock.Dispose();

        _logger.LogInformation("MQTT output disposed for {DeviceId}", _deviceId);
    }

    // --- 内部序列化模型 ---

    private sealed class MqttBatch
    {
        public Guid BatchId { get; set; }
        public string? TenantId { get; set; }
        public string FactoryId { get; set; } = string.Empty;
        public string WorkshopId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string Protocol { get; set; } = string.Empty;
        public DateTimeOffset Timestamp { get; set; }
        public List<MqttDataPoint> Points { get; set; } = [];
    }

    private sealed class MqttDataPoint
    {
        public string Tag { get; set; } = string.Empty;
        public object? Value { get; set; }
        public string? ValueType { get; set; }
        public string Quality { get; set; } = "Good";
        public DateTime Timestamp { get; set; }
        public string? GroupName { get; set; }
    }
}
