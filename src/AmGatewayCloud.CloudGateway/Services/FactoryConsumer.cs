using System.Text;
using System.Text.Json;
using AmGatewayCloud.CloudGateway.Configuration;
using AmGatewayCloud.CloudGateway.Models;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AmGatewayCloud.CloudGateway.Services;

public class FactoryConsumer : BackgroundService
{
    private readonly string _factoryId;
    private readonly string _queueName;
    private readonly RabbitMqConfig _rabbitConfig;
    private readonly MessageDeduplicator _deduplicator;
    private readonly TimescaleDbWriter _timescaleWriter;
    private readonly PostgreSqlDeviceStore _deviceStore;
    private readonly AuditLogService _auditLog;
    private readonly ConsumerHealthTracker _healthTracker;
    private readonly ILogger<FactoryConsumer> _logger;

    private IConnection? _connection;
    private IModel? _channel;
    private int _consecutiveFailures = 0;
    private volatile bool _circuitOpen = false;
    private const int CircuitBreakerThreshold = 5;
    private const int MaxRetryCount = 3;
    private const int MaxMessageSize = 1024 * 1024; // 1MB

    public FactoryConsumer(
        FactoryConsumerConfig config,
        RabbitMqConfig rabbitConfig,
        MessageDeduplicator deduplicator,
        TimescaleDbWriter timescaleWriter,
        PostgreSqlDeviceStore deviceStore,
        AuditLogService auditLog,
        ConsumerHealthTracker healthTracker,
        ILogger<FactoryConsumer> logger)
    {
        _factoryId = config.FactoryId;
        _queueName = config.QueueName;
        _rabbitConfig = rabbitConfig;
        _deduplicator = deduplicator;
        _timescaleWriter = timescaleWriter;
        _deviceStore = deviceStore;
        _auditLog = auditLog;
        _healthTracker = healthTracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndConsumeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Factory:{FactoryId}] Consumer crashed, reconnecting...", _factoryId);
                _healthTracker.SetOffline(_factoryId);
                await Task.Delay(_rabbitConfig.ReconnectDelayMs, ct);
            }
        }
    }

    private async Task ConnectAndConsumeAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = _rabbitConfig.HostName,
            Port = _rabbitConfig.Port,
            UserName = _rabbitConfig.Username,
            Password = _rabbitConfig.Password,
            VirtualHost = _rabbitConfig.VirtualHost,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        if (_rabbitConfig.UseSsl)
            factory.Ssl.Enabled = true;

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.BasicQos(0, _rabbitConfig.PrefetchCount, false);

        // 声明 DLX 和 DLQ
        var dlxExchange = $"dlx.{_queueName}";
        var dlqName = $"dlq.{_queueName}";
        _channel.ExchangeDeclare(dlxExchange, ExchangeType.Topic, durable: true);
        _channel.QueueDeclare(dlqName, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(dlqName, dlxExchange, routingKey: $"dlx.{_queueName}");

        // 声明主队列（带 DLX）
        var arguments = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = dlxExchange,
            ["x-dead-letter-routing-key"] = $"dlx.{_queueName}"
        };
        _channel.QueueDeclare(_queueName, durable: true, exclusive: false, autoDelete: false, arguments);

        var consumer = new EventingBasicConsumer(_channel);
        consumer.Received += (_, ea) => _ = HandleMessageAsync(ea, ct);

        _channel.BasicConsume(_queueName, autoAck: false, consumer);
        _healthTracker.SetOnline(_factoryId);
        _logger.LogInformation("[Factory:{FactoryId}] Consumer started: queue={QueueName}, prefetch={Prefetch}",
            _factoryId, _queueName, _rabbitConfig.PrefetchCount);

        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task HandleMessageAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var deliveryTag = ea.DeliveryTag;

        // 1. 超大消息检测
        if (ea.Body.Length > MaxMessageSize)
        {
            _logger.LogError("[Factory:{FactoryId}] Message too large: {Size} bytes", _factoryId, ea.Body.Length);
            await _auditLog.LogAsync(new AuditLog
            {
                FactoryId = _factoryId,
                Reason = $"Message too large: {ea.Body.Length} bytes",
                RawPayloadPreview = Convert.ToBase64String(ea.Body.Slice(0, Math.Min(200, ea.Body.Length)).ToArray()),
                ReceivedAt = DateTimeOffset.UtcNow
            }, ct);
            _channel!.BasicAck(deliveryTag, multiple: false);
            return;
        }

        // 2. 反序列化
        DataBatch? batch;
        try
        {
            var body = Encoding.UTF8.GetString(ea.Body.ToArray());
            batch = JsonSerializer.Deserialize<DataBatch>(body);
            if (batch == null)
            {
                throw new JsonException("Deserialized batch is null");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Factory:{FactoryId}] Failed to deserialize message", _factoryId);
            await _auditLog.LogAsync(new AuditLog
            {
                FactoryId = _factoryId,
                Reason = $"Deserialization failed: {ex.Message}",
                RawPayloadPreview = Convert.ToBase64String(ea.Body.Slice(0, Math.Min(200, ea.Body.Length)).ToArray()),
                ReceivedAt = DateTimeOffset.UtcNow
            }, ct);
            _channel!.BasicAck(deliveryTag, multiple: false);
            return;
        }

        // 3. 去重检查
        if (_deduplicator.IsDuplicate(batch.BatchId))
        {
            _channel!.BasicAck(deliveryTag, multiple: false);
            return;
        }

        // 4. 熔断检查
        if (_circuitOpen)
        {
            _logger.LogWarning("[Factory:{FactoryId}] Circuit breaker open, NACK message", _factoryId);
            _channel!.BasicNack(deliveryTag, multiple: false, requeue: true);
            return;
        }

        // 5. 重试计数（通过 x-death 头）
        var retryCount = 0;
        if (ea.BasicProperties.Headers?.TryGetValue("x-death", out var xDeath) == true && xDeath is List<object> deaths)
        {
            retryCount = deaths.Count;
        }

        if (retryCount >= MaxRetryCount)
        {
            _logger.LogError("[Factory:{FactoryId}] Message exceeded max retries ({Count}), routing to DLQ", _factoryId, retryCount);
            _channel!.BasicNack(deliveryTag, multiple: false, requeue: false); // 进入 DLQ
            return;
        }

        // 6. 处理消息
        try
        {
            await ProcessBatchAsync(batch, ct);

            // 7. Flush 成功后 ACK
            await _timescaleWriter.FlushAsync(ct);
            _channel!.BasicAck(deliveryTag, multiple: false);

            // 重置失败计数
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _healthTracker.RecordSuccess(_factoryId);
        }
        catch (WriteException ex) when (ex.Kind == WriteErrorKind.Permanent)
        {
            _logger.LogError(ex, "[Factory:{FactoryId}] Permanent error, ACKing message to prevent infinite retry", _factoryId);
            await _auditLog.LogAsync(new AuditLog
            {
                FactoryId = _factoryId,
                BatchId = batch.BatchId.ToString(),
                Reason = $"Permanent error: {ex.Message}",
                ReceivedAt = DateTimeOffset.UtcNow
            }, ct);
            _channel!.BasicAck(deliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Factory:{FactoryId}] Transient error, NACK and requeue", _factoryId);

            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (failures >= CircuitBreakerThreshold)
            {
                OpenCircuitBreaker();
            }

            _channel!.BasicNack(deliveryTag, multiple: false, requeue: true);
            _healthTracker.RecordFailure(_factoryId);
        }
    }

    private async Task ProcessBatchAsync(DataBatch batch, CancellationToken ct)
    {
        var tsTask = _timescaleWriter.WriteBatchAsync(batch, ct);
        var pgTask = _deviceStore.EnsureDeviceAsync(batch, ct);

        await Task.WhenAll(tsTask, pgTask);
        await _deviceStore.UpdateLastSeenAsync(batch.DeviceId, batch.Timestamp, ct);
    }

    private void OpenCircuitBreaker()
    {
        _circuitOpen = true;
        _logger.LogCritical("[Factory:{FactoryId}] Circuit breaker OPEN", _factoryId);

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            _circuitOpen = false;
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _logger.LogInformation("[Factory:{FactoryId}] Circuit breaker CLOSED", _factoryId);
        });
    }

    public override void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
        base.Dispose();
    }
}
