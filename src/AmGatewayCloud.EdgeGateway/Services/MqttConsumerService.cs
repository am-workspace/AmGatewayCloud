using System.Text;
using System.Text.Json;
using AmGatewayCloud.EdgeGateway.Configuration;
using AmGatewayCloud.EdgeGateway.Models;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Implementations;

namespace AmGatewayCloud.EdgeGateway.Services;

/// <summary>
/// MQTT 消费者服务：订阅局域网 MQTT，接收采集器数据，反序列化后分发。
/// Step 7 完整版：ACK 时机控制（InfluxDB 成功后 ACK）、消息大小限制、错误处理。
/// </summary>
public class MqttConsumerService : BackgroundService
{
    private readonly EdgeGatewayConfig _config;
    private readonly ILogger<MqttConsumerService> _logger;
    private readonly InfluxDbWriter _influxWriter;
    private readonly RabbitMqForwarder _rabbitForwarder;
    private readonly WatermarkTracker _watermarkTracker;
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private volatile bool _connected;
    private int _reconnectAttempt;
    private bool _disposed;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectLoop;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MqttConsumerService(
        IOptions<EdgeGatewayConfig> config,
        InfluxDbWriter influxWriter,
        RabbitMqForwarder rabbitForwarder,
        WatermarkTracker watermarkTracker,
        ILogger<MqttConsumerService> logger)
    {
        _config = config.Value;
        _influxWriter = influxWriter;
        _rabbitForwarder = rabbitForwarder;
        _watermarkTracker = watermarkTracker;
        _logger = logger;

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_config.Mqtt.Broker, _config.Mqtt.Port)
            .WithClientId($"{_config.Mqtt.ClientId}-{_config.HubId}")
            .WithCleanStart(_config.Mqtt.CleanSession)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_config.Mqtt.KeepAliveSeconds));

        if (_config.Mqtt.UseTls)
            builder.WithTlsOptions(tls => tls.UseTls());

        if (!string.IsNullOrEmpty(_config.Mqtt.Username))
            builder.WithCredentials(_config.Mqtt.Username, _config.Mqtt.Password ?? string.Empty);

        _options = builder.Build();

        _client.DisconnectedAsync += OnDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var topicFilter = _config.Mqtt.UseSharedSubscription
            ? $"$share/{_config.Mqtt.SharedGroup}/{_config.Mqtt.TopicFilter.TrimStart('$').TrimStart('/')}".Replace("//", "/")
            : _config.Mqtt.TopicFilter;

        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f =>
            {
                f.WithTopic(topicFilter);
                f.WithQualityOfServiceLevel((MqttQualityOfServiceLevel)_config.Mqtt.QoS);
            })
            .Build();

        await _client.SubscribeAsync(subscribeOptions, ct);
        _logger.LogInformation("MQTT subscribed to {TopicFilter} (shared={Shared})",
            topicFilter, _config.Mqtt.UseSharedSubscription);

        // 保持运行直到取消
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        const int MaxPayloadSize = 1024 * 1024; // 1MB

        try
        {
            // 消息大小限制
            if (e.ApplicationMessage.PayloadSegment.Count > MaxPayloadSize)
            {
                _logger.LogError("MQTT payload too large: {Size} bytes, dropping", e.ApplicationMessage.PayloadSegment.Count);
                e.ProcessingFailed = false; // ACK 但不处理
                return;
            }

            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            var batch = JsonSerializer.Deserialize<DataBatch>(payload, JsonOpts);

            if (batch == null)
            {
                _logger.LogWarning("Failed to deserialize MQTT message from topic {Topic}", e.ApplicationMessage.Topic);
                e.ProcessingFailed = false; // ACK 无效数据
                return;
            }

            batch.ReceivedAt = DateTimeOffset.UtcNow;

            // 时钟漂移检测
            var drift = batch.ReceivedAt - batch.Timestamp;
            if (Math.Abs(drift.TotalMinutes) > 5)
            {
                _logger.LogWarning("Clock drift detected: device={DeviceId} drift={Drift:F1}min",
                    batch.DeviceId, drift.TotalMinutes);
            }

            _logger.LogInformation(
                "[batch] device={DeviceId} protocol={Protocol} points={Count} topic={Topic}",
                batch.DeviceId, batch.Protocol, batch.Points.Count, e.ApplicationMessage.Topic);

            // 核心原则：InfluxDB 写入成功后才能 ACK MQTT
            // 因为 InfluxDB 是本地落盘，是数据不丢的底线
            await _influxWriter.WriteBatchAsync(batch, CancellationToken.None);

            // InfluxDB 成功 → ACK MQTT
            e.ProcessingFailed = false;

            // RabbitMQ 转发异步执行，失败不阻塞 ACK
            _ = Task.Run(async () =>
            {
                try
                {
                    await _rabbitForwarder.ForwardAsync(batch, _watermarkTracker, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RabbitMQ forward failed for batch {BatchId}, will retry on replay", batch.BatchId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing MQTT message from topic {Topic}", e.ApplicationMessage.Topic);
            // 处理异常 → 不 ACK，让 MQTT Broker 重发
            e.ProcessingFailed = true;
        }
    }

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
                _logger.LogInformation("MQTT connected to {Broker}:{Port}", _config.Mqtt.Broker, _config.Mqtt.Port);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT connect failed to {Broker}:{Port}", _config.Mqtt.Broker, _config.Mqtt.Port);
                _connected = false;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (_disposed) return Task.CompletedTask;

        _connected = false;
        if (_reconnectLoop != null) return Task.CompletedTask;

        _reconnectCts = new CancellationTokenSource();
        _reconnectLoop = ReconnectLoopAsync(_reconnectCts.Token);
        return Task.CompletedTask;
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _reconnectAttempt++;
                var delayMs = CalculateReconnectDelay(_reconnectAttempt);
                _logger.LogWarning("MQTT disconnected, reconnecting in {DelayMs}ms (attempt {Attempt})",
                    delayMs, _reconnectAttempt);

                try { await Task.Delay(delayMs, ct); }
                catch (OperationCanceledException) { break; }

                try
                {
                    await EnsureConnectedAsync(ct);
                    if (_connected)
                    {
                        // 重新订阅
                        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                            .WithTopicFilter(f =>
                            {
                                f.WithTopic(_config.Mqtt.TopicFilter);
                                f.WithQualityOfServiceLevel((MqttQualityOfServiceLevel)_config.Mqtt.QoS);
                            })
                            .Build();
                        await _client.SubscribeAsync(subscribeOptions, ct);
                        _logger.LogInformation("MQTT reconnected and resubscribed after {Attempt} attempts", _reconnectAttempt);
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* 重连失败，继续下一轮 */ }
            }
        }
        finally
        {
            _reconnectLoop = null;
        }
    }

    private int CalculateReconnectDelay(int attempt)
    {
        var delay = 5000 * (1 << Math.Min(attempt - 1, 6));
        return Math.Min(delay, 60000);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _disposed = true;
        _reconnectCts?.Cancel();

        if (_reconnectLoop != null)
        {
            try { await _reconnectLoop; } catch { }
        }

        if (_connected)
        {
            try { await _client.DisconnectAsync(); } catch { }
        }

        _client.Dispose();
        _connectLock.Dispose();
        _reconnectCts?.Dispose();

        _logger.LogInformation("MQTT consumer stopped");
        await base.StopAsync(ct);
    }
}
