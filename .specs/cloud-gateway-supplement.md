# AmGatewayCloud.CloudGateway — 方案补充

> 基于 `cloud-gateway.md` 与 `roadmap.md` 的审查，补充异常/边界条件和优化建议。

---

## 1. 数据可靠性：ACK 时机与 TimescaleDbWriter 缓冲刷新的矛盾

### 1.1 问题

当前方案的关键原则是 **"先写数据库，后 ACK RabbitMQ"**，但 `TimescaleDbWriter` 设计了 `BatchSize=1000` + `FlushIntervalMs=5000` 的内存缓冲机制。

如果 Consumer 收到消息后调用 `WriteBatchAsync` 只是把数据丢进内存队列就返回，然后据此 ACK，那么：
- **进程崩溃时缓冲区内数据会丢失**（RabbitMQ 已认为消息被成功消费）。
- 违背"先写数据库，后 ACK"的设计原则。

### 1.2 建议

明确 ACK 时机：**必须等 `FlushAsync()` 真正成功返回后才能 ACK**。

对于每个工厂的 Consumer，采用**小批量同步 flush + 批量 ACK** 模式：

```csharp
public class FactoryConsumer : BackgroundService
{
    private readonly TimescaleDbWriter _timescaleWriter;
    private readonly PostgreSqlDeviceStore _deviceStore;
    private readonly IModel _channel;
    private readonly List<ulong> _pendingDeliveryTags = [];
    private readonly object _lock = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var consumer = new EventingBasicConsumer(_channel);
        consumer.Received += async (_, ea) =>
        {
            var batch = Deserialize(ea.Body);

            // 1. 去重检查
            if (_deduplicator.IsDuplicate(batch.BatchId))
            {
                _channel.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            // 2. 并行写入两个数据库（无事务关联）
            var tsTask = _timescaleWriter.WriteBatchAsync(batch, ct);
            var pgTask = _deviceStore.EnsureDeviceAsync(batch, ct);

            try
            {
                await Task.WhenAll(tsTask, pgTask);

                // 3. 写入成功 → 缓冲 DeliveryTag，等待批量 ACK
                lock (_lock)
                {
                    _pendingDeliveryTags.Add(ea.DeliveryTag);
                }

                // 4. 触发 flush（如果是同步模式，这里会立即刷盘）
                await _timescaleWriter.FlushAsync(ct);

                // 5. Flush 成功后，批量 ACK 到 RabbitMQ
                List<ulong> tagsToAck;
                lock (_lock)
                {
                    tagsToAck = new List<ulong>(_pendingDeliveryTags);
                    _pendingDeliveryTags.Clear();
                }

                if (tagsToAck.Count > 0)
                {
                    var maxTag = tagsToAck.Max();
                    _channel.BasicAck(maxTag, multiple: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Factory:{FactoryId}] Database write failed, NACK", _factoryId);

                // 6. 写入失败 → NACK，消息重新入队
                _channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
            }
        };

        _channel.BasicConsume(_queueName, autoAck: false, consumer);
        await Task.Delay(Timeout.Infinite, ct);
    }
}
```

> **原则**：如果 `TimescaleDbWriter` 需要内存缓冲以提高吞吐，则 ACK 必须延迟到 `FlushAsync()` 成功之后。否则应禁用内存缓冲，改为同步单条写入。

---

## 2. 无限重试风暴：缺少死信队列（DLQ）

### 2.1 问题

当前错误处理策略：数据库写入失败 → 不 ACK → 消息重新入队 → 立即重新投递。

如果 TimescaleDB 或 PostgreSQL **长时间不可用**（云数据库维护、网络分区），这条消息会被**无限循环重试**，导致：
- 日志和 CPU 被打爆；
- 该工厂 Consumer 的吞吐降为 0（一直在重试同一条消息）；
- 其他正常消息被阻塞。

### 2.2 建议

RabbitMQ 侧为每个队列配置 **Dead Letter Exchange（DLX）** + **最大重试次数**。

#### 2.2.1 队列声明（带 DLX）

```csharp
public void DeclareQueueWithDLX(string queueName, string dlxExchange, string dlqName)
{
    // 死信交换机
    _channel.ExchangeDeclare(dlxExchange, ExchangeType.Topic, durable: true);

    // 死信队列
    _channel.QueueDeclare(dlqName, durable: true, exclusive: false, autoDelete: false);
    _channel.QueueBind(dlqName, dlxExchange, routingKey: $"dlx.{queueName}");

    // 主队列参数：失败 3 次后进入死信队列
    var arguments = new Dictionary<string, object>
    {
        ["x-dead-letter-exchange"] = dlxExchange,
        ["x-dead-letter-routing-key"] = $"dlx.{queueName}",
        ["x-message-ttl"] = 30000 // 30s 后未消费也进入 DLQ（可选）
    };

    _channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false, arguments);
}
```

#### 2.2.2 Consumer 内重试计数

```csharp
public class FactoryConsumer : BackgroundService
{
    private const int MaxRetryCount = 3;

    private async Task HandleMessageAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var retryCount = 0;
        if (ea.BasicProperties.Headers?.TryGetValue("x-retry-count", out var rc) == true)
        {
            retryCount = Convert.ToInt32(rc);
        }

        try
        {
            await ProcessAsync(ea, ct);
            _channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            retryCount++;
            if (retryCount >= MaxRetryCount)
            {
                _logger.LogError(ex, "[Factory:{FactoryId}] Message exceeded max retries, routing to DLQ", _factoryId);
                _channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false); // 不 requeue，进入 DLX
            }
            else
            {
                // 更新重试计数后重新入队
                var props = _channel.CreateBasicProperties();
                props.Headers = new Dictionary<string, object>(ea.BasicProperties.Headers ?? [])
                {
                    ["x-retry-count"] = retryCount
                };
                _channel.BasicPublish("", ea.RoutingKey, props, ea.Body);
                _channel.BasicAck(ea.DeliveryTag, multiple: false); // 确认原消息
            }
        }
    }
}
```

#### 2.2.3 熔断机制

```csharp
public class FactoryConsumer : BackgroundService
{
    private int _consecutiveDbFailures = 0;
    private const int CircuitBreakerThreshold = 5;
    private bool _circuitOpen = false;

    private async Task<bool> TryProcessAsync(DataBatch batch, CancellationToken ct)
    {
        if (_circuitOpen)
        {
            _logger.LogWarning("[Factory:{FactoryId}] Circuit breaker open, skipping processing", _factoryId);
            return false;
        }

        try
        {
            await _timescaleWriter.WriteBatchAsync(batch, ct);
            await _deviceStore.EnsureDeviceAsync(batch, ct);
            _consecutiveDbFailures = 0;
            return true;
        }
        catch (Exception ex)
        {
            _consecutiveDbFailures++;
            _logger.LogError(ex, "[Factory:{FactoryId}] DB write failed ({Count} consecutive)", _factoryId, _consecutiveDbFailures);

            if (_consecutiveDbFailures >= CircuitBreakerThreshold)
            {
                _circuitOpen = true;
                _logger.LogCritical("[Factory:{FactoryId}] Circuit breaker OPEN", _factoryId);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    _circuitOpen = false;
                    _consecutiveDbFailures = 0;
                    _logger.LogInformation("[Factory:{FactoryId}] Circuit breaker CLOSED", _factoryId);
                });
            }
            return false;
        }
    }
}
```

> **原则**：数据库故障时，不应让 RabbitMQ 无限重投。DLQ + 熔断 + 重试计数是工业级 Consumer 的标配。

---

## 3. 背压（Backpressure）机制缺失

### 3.1 问题

`PrefetchCount=100` 只是 RabbitMQ 层的预取控制。但如果某个工厂突然推送了海量历史回放数据，而 TimescaleDB 写入较慢，`TimescaleDbWriter` 的内存缓冲队列会无限增长，最终可能导致 **OOM**。

### 3.2 建议

`TimescaleDbWriter` 的内存缓冲队列设置**上限**，满了之后阻塞上游 Consumer：

```csharp
public class TimescaleDbWriter : IAsyncDisposable
{
    private readonly Channel<DataPoint> _buffer;
    private readonly int _maxPendingCount;

    public TimescaleDbWriter(TimescaleDbConfig config)
    {
        _maxPendingCount = config.BatchSize * 5; // 缓冲上限 = 5 个批次
        _buffer = Channel.CreateBounded<DataPoint>(
            new BoundedChannelOptions(_maxPendingCount)
            {
                FullMode = WaitMode.Wait // 满了阻塞写入，自然形成背压
            });
    }

    public async Task WriteBatchAsync(DataBatch batch, CancellationToken ct)
    {
        foreach (var point in batch.Points)
        {
            await _buffer.Writer.WriteAsync(point, ct); // 满了自动阻塞
        }
    }
}
```

或者 Consumer 根据 `TimescaleDbWriter.GetPendingCount()` 动态调节 `BasicQos`：

```csharp
public async Task AdjustPrefetchAsync()
{
    var pending = _timescaleWriter.GetPendingCount();
    var newPrefetch = pending > 5000 ? (ushort)10 : (ushort)100;
    _channel.BasicQos(prefetchSize: 0, prefetchCount: newPrefetch, global: false);
}
```

> **原则**：RabbitMQ 磁盘堆积比应用 OOM 安全得多。让背压自然传递到 RabbitMQ 侧。

---

## 4. 双写一致性：可重试错误 vs 不可重试错误

### 4.1 问题

TimescaleDB 和 PostgreSQL 是并行写入、无事务关联的。假设：
- TimescaleDB 写入成功
- PostgreSQL 写入失败（连接超时）
- 消息 NACK 重入队
- 重试时 TimescaleDB 的 `ON CONFLICT DO NOTHING` 能防重，但如果 PostgreSQL 失败的原因是**数据本身有问题**（如 `deviceId` 超长、`tag` 含非法字符），这条消息会**永远重试失败**。

### 4.2 建议

区分**可重试错误**和**不可重试错误**：

```csharp
public enum WriteErrorKind
{
    Transient,   // 可重试：网络超时、连接断开、DB 暂时不可用
    Permanent    // 不可重试：数据格式违规、约束冲突、消息超大
}

public class WriteException : Exception
{
    public WriteErrorKind Kind { get; }
    public WriteException(string message, WriteErrorKind kind) : base(message) => Kind = kind;
}

public class PostgreSqlDeviceStore
{
    public async Task EnsureDeviceAsync(DataBatch batch, CancellationToken ct)
    {
        try
        {
            // ... 写入逻辑
        }
        catch (NpgsqlException ex) when (ex.SqlState == "22001") // value too long
        {
            throw new WriteException($"DeviceId too long: {batch.DeviceId}", WriteErrorKind.Permanent);
        }
        catch (NpgsqlException ex) when (ex.IsTransient)
        {
            throw new WriteException("Transient DB error", WriteErrorKind.Transient);
        }
    }
}

public class FactoryConsumer : BackgroundService
{
    private async Task HandleMessageAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            await ProcessAsync(ea, ct);
            _channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (WriteException ex) when (ex.Kind == WriteErrorKind.Permanent)
        {
            _logger.LogError(ex, "Permanent error, ACKing message to prevent infinite retry");
            _channel.BasicAck(ea.DeliveryTag, multiple: false); // 直接 ACK，丢弃脏数据
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transient error, NACK and requeue");
            _channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
        }
    }
}
```

> **原则**：脏数据不应该被无限重试。ACK + 记录死信日志是更合理的处理方式。

---

## 5. 时间戳与时区：`DateTimeOffset` 与 `DateTime` 混用

### 5.1 问题

```csharp
public DateTimeOffset Timestamp { get; set; }  // DataBatch
public DateTime Timestamp { get; set; }        // DataPoint
```

`DataPoint` 使用 `DateTime`，如果边缘端传来的值是 `DateTimeKind.Unspecified`，写入 TimescaleDB 的 `TIMESTAMPTZ` 时会被 PostgreSQL 按会话时区解释，导致**时间漂移**。

### 5.2 建议

统一使用 `DateTimeOffset`，并在反序列化时强制规范为 UTC：

```csharp
public class DataPoint
{
    public DateTimeOffset Timestamp { get; set; } // 统一为 DateTimeOffset
}

// 反序列化时强制 UTC
public class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dto = reader.GetDateTimeOffset();
        return dto.Offset != TimeSpan.Zero ? dto.ToUniversalTime() : dto;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToUniversalTime());
    }
}
```

> **原则**：时序数据库的每一行时间戳都必须是确定性的 UTC，避免任何时区歧义。

---

## 6. `devices.tags` TEXT[] 的并发更新热点

### 6.1 问题

`PostgreSqlDeviceStore` 每次收到数据都要更新设备的 `tags` 数组。如果一台设备有 50 个测点、每秒上报一次，且并发 Consumer 较多，`tags` 数组的频繁 UPSERT 会成为**行级锁热点**。

### 6.2 建议

降低 `tags` 更新频率，只在发现新 tag 时才写入：

```csharp
public class PostgreSqlDeviceStore
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _knownTagsCache = new();

    public async Task EnsureDeviceAsync(DataBatch batch, CancellationToken ct)
    {
        var key = $"{batch.FactoryId}:{batch.WorkshopId}:{batch.DeviceId}";
        var currentTags = batch.Points.Select(p => p.Tag).ToHashSet();
        var hasNewTag = false;

        var known = _knownTagsCache.GetOrAdd(key, _ => new HashSet<string>());
        lock (known)
        {
            foreach (var tag in currentTags)
            {
                if (known.Add(tag))
                {
                    hasNewTag = true;
                }
            }
        }

        // 只有发现新 tag 时才更新数据库
        if (hasNewTag)
        {
            await UpsertDeviceTagsAsync(batch, currentTags, ct);
        }

        // last_seen_at 可以批量异步更新，降低频率
        await UpdateLastSeenAsync(batch.DeviceId, batch.Timestamp, ct);
    }
}
```

或者把 `tags` 拆成独立表 `device_tags`：

```sql
CREATE TABLE device_tags (
    factory_id TEXT NOT NULL,
    workshop_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    tag TEXT NOT NULL,
    first_seen_at TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (factory_id, workshop_id, device_id, tag)
);
```

> **原则**：`devices` 主表的行级锁应尽量减少。`last_seen_at` 也可以按 30 秒窗口批量更新。

---

## 7. 配置热更新

### 7.1 问题

`Factories` 列表硬编码在 `appsettings.json` 中，新增一个工厂需要**重启 CloudGateway**。

### 7.2 建议

将 `Factories` 的读取抽象为 `IFactoryRegistry` 接口，预留热更新能力：

```csharp
public interface IFactoryRegistry
{
    IReadOnlyList<FactoryConsumerConfig> GetFactories();
    event EventHandler<FactoryListChangedEventArgs>? FactoriesChanged;
}

public class FileFactoryRegistry : IFactoryRegistry
{
    private readonly IOptionsMonitor<CloudGatewayConfig> _configMonitor;

    public FileFactoryRegistry(IOptionsMonitor<CloudGatewayConfig> configMonitor)
    {
        _configMonitor = configMonitor;
        _configMonitor.OnChange(_ => FactoriesChanged?.Invoke(this, new FactoryListChangedEventArgs()));
    }

    public IReadOnlyList<FactoryConsumerConfig> GetFactories() => _configMonitor.CurrentValue.Factories;
    public event EventHandler<FactoryListChangedEventArgs>? FactoriesChanged;
}

// MultiRabbitMqConsumer 监听变更，动态启停 Consumer
public class MultiRabbitMqConsumer : IHostedService
{
    private readonly IFactoryRegistry _registry;

    public Task StartAsync(CancellationToken ct)
    {
        _registry.FactoriesChanged += OnFactoriesChanged;
        // ... 启动现有 Consumer
        return Task.CompletedTask;
    }

    private void OnFactoriesChanged(object? sender, FactoryListChangedEventArgs e)
    {
        var current = _registry.GetFactories();
        // diff 后启动新 Consumer、停止被移除的 Consumer
    }
}
```

> **原则**：阶段 7 引入配置中心时，只需替换 `IFactoryRegistry` 的实现，无需重构 Consumer 逻辑。

---

## 8. 健康监控端点

### 8.1 问题

`HealthMonitorService` 只描述了内部状态聚合，但没有提到**如何暴露**（HTTP `/health`？TCP 端口？）。

### 8.2 建议

集成 `AspNetCore.HealthChecks`，并暴露 Consumer 级健康指标：

```csharp
// Program.cs
builder.Services.AddHealthChecks()
    .AddCheck<RabbitMqHealthCheck>("rabbitmq")
    .AddCheck<TimescaleDbHealthCheck>("timescaledb")
    .AddCheck<PostgreSqlHealthCheck>("postgresql");

builder.Services.AddSingleton<ConsumerHealthTracker>();

var app = builder.Build();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        var tracker = context.RequestServices.GetRequiredService<ConsumerHealthTracker>();
        var consumers = tracker.GetSnapshot();

        var result = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString() }),
            consumers = consumers.Select(c => new
            {
                c.FactoryId,
                c.IsOnline,
                c.Lag?.TotalSeconds,
                c.MessagesPerSecond,
                c.TotalProcessed
            })
        };

        await context.Response.WriteAsJsonAsync(result);
    }
});
```

> **原则**：K8s/Docker Compose 的存活探针（livenessProbe）和就绪探针（readinessProbe）都依赖 `/health`。

---

## 9. TimescaleDB 连续聚合与数据保留

### 9.1 问题

方案提到了 `device_data_hourly` 物化视图，但**没有配置刷新策略**。TimescaleDB 的连续聚合不会自动刷新。

### 9.2 建议

在 `EnsureHypertableAsync` 中一并配置：

```csharp
public async Task EnsureHypertableAsync(CancellationToken ct)
{
    await _connection.ExecuteAsync(@"
        CREATE TABLE IF NOT EXISTS device_data (
            time TIMESTAMPTZ NOT NULL,
            batch_id UUID,
            tenant_id TEXT,
            factory_id TEXT,
            workshop_id TEXT,
            device_id TEXT,
            protocol TEXT,
            tag TEXT,
            quality TEXT,
            group_name TEXT,
            value_int BIGINT,
            value_float DOUBLE PRECISION,
            value_bool BOOLEAN,
            value_string TEXT,
            value_type TEXT,
            PRIMARY KEY (time, batch_id, tag)
        );
    ");

    await _connection.ExecuteAsync(@"
        SELECT create_hypertable('device_data', 'time', if_not_exists => TRUE);
    ");

    // 连续聚合（小时级聚合）
    await _connection.ExecuteAsync(@"
        CREATE MATERIALIZED VIEW IF NOT EXISTS device_data_hourly
        WITH (timescaledb.continuous) AS
        SELECT
            time_bucket('1 hour', time) AS bucket,
            factory_id, device_id, tag,
            AVG(value_float) AS avg_value,
            MAX(value_float) AS max_value,
            MIN(value_float) AS min_value
        FROM device_data
        WHERE value_float IS NOT NULL
        GROUP BY bucket, factory_id, device_id, tag;
    ");

    // 连续聚合刷新策略
    await _connection.ExecuteAsync(@"
        SELECT add_continuous_aggregate_policy('device_data_hourly',
            start_offset => INTERVAL '1 month',
            end_offset => INTERVAL '1 hour',
            schedule_interval => INTERVAL '1 hour',
            if_not_exists => TRUE);
    ");

    // 数据保留策略：原始数据保留 90 天，聚合视图保留 1 年
    await _connection.ExecuteAsync(@"
        SELECT add_retention_policy('device_data', INTERVAL '90 days', if_not_exists => TRUE);
    ");
    await _connection.ExecuteAsync(@"
        SELECT add_retention_policy('device_data_hourly', INTERVAL '1 year', if_not_exists => TRUE);
    ");
}
```

> **原则**：没有 retention policy 的时序表会无限膨胀，最终拖垮查询性能和磁盘空间。

---

## 10. 消息超大与 `ValueType` 不匹配

### 10.1 问题

- 单条消息 >1MB 直接 ACK 是合理的，但缺少**结构化记录**。
- `DataPoint.Value` 是 `JsonElement`，如果边缘端传了未知的 `ValueType`（如 `"int64"` 而不是 `"long"`），写入逻辑缺少 fallback。

### 10.2 建议

#### 10.2.1 超大消息告警记录

```csharp
public class FactoryConsumer : BackgroundService
{
    private const int MaxMessageSize = 1024 * 1024; // 1MB

    private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        if (ea.Body.Length > MaxMessageSize)
        {
            _logger.LogError("[Factory:{FactoryId}] Message too large: {Size} bytes, BatchId unknown. Dropping.",
                _factoryId, ea.Body.Length);

            // 写入死信审计表
            await _auditLog.LogAsync(new DeadLetterAudit
            {
                FactoryId = _factoryId,
                Reason = $"Message too large: {ea.Body.Length} bytes",
                ReceivedAt = DateTimeOffset.UtcNow,
                RawPayloadPreview = Convert.ToBase64String(ea.Body.Slice(0, 200).ToArray())
            });

            _channel.BasicAck(ea.DeliveryTag, multiple: false);
            return;
        }

        // ... 正常处理
    }
}
```

#### 10.2.2 未知 `ValueType` 的 fallback

```csharp
public class DataPointConverter
{
    public static (object? value, string column) ConvertValue(DataPoint point)
    {
        var type = point.ValueType?.ToLowerInvariant();

        return type switch
        {
            "int" or "short" or "long" or "int32" or "int64"
                => (point.Value.GetInt64(), "value_int"),
            "float" or "double" or "single"
                => (point.Value.GetDouble(), "value_float"),
            "bool" or "boolean"
                => (point.Value.GetBoolean(), "value_bool"),
            "string"
                => (point.Value.GetString(), "value_string"),
            _ => UnknownTypeFallback(point)
        };
    }

    private static (object? value, string column) UnknownTypeFallback(DataPoint point)
    {
        // 尝试根据 JsonElement 的实际类型推断
        return point.Value.ValueKind switch
        {
            JsonValueKind.Number when point.Value.TryGetInt64(out var i) => (i, "value_int"),
            JsonValueKind.Number => (point.Value.GetDouble(), "value_float"),
            JsonValueKind.True or JsonValueKind.False => (point.Value.GetBoolean(), "value_bool"),
            JsonValueKind.String => (point.Value.GetString(), "value_string"),
            _ => (point.Value.ToString(), "value_string") // 兜底：序列化为字符串
        };
    }
}
```

> **原则**：边缘端的 "小错误" 不应导致云端崩溃。优雅降级 + 结构化审计日志是更安全的做法。

---

## 11. Roadmap 全链路视角的优化建议

### 11.1 阶段 3 → 阶段 4：AlarmService 消费时序数据的两种模式

阶段 4 的 AlarmService 需要从时序数据做阈值判断。有两种消费模式，建议阶段 3 就预留架构接口：

| 模式 | 原理 | 优点 | 缺点 |
|------|------|------|------|
| **拉模式（推荐初期）** | AlarmService 定时查询 TimescaleDB：`SELECT * FROM device_data WHERE time > now() - interval '1 minute'` | 实现简单，CloudGateway 无耦合 | 延迟高（分钟级） |
| **推模式** | CloudGateway 写入时同时发 Kafka/RabbitMQ 二次广播 | 延迟低（秒级），适合实时报警 | 增加消息中间件复杂度 |

**建议**：阶段 4 初期用拉模式，阶段 6 引入 Kafka 后再考虑推模式。CloudGateway 无需改动，只需在阶段 4 的 AlarmService 中实现轮询。

### 11.2 阶段 4：报警阈值缺少工业常用机制

现场传感器抖动会导致**频繁报警/恢复**（报警风暴）。

**建议**：报警规则预留 **Deadband（死区）** 和 **延时报警**：

```csharp
public class AlarmRule
{
    public Guid Id { get; set; }
    public string Tag { get; set; } = string.Empty;
    public double Threshold { get; set; }
    public string Operator { get; set; } = ">";
    public double? Deadband { get; set; }      // 死区阈值，避免抖动
    public int? DelaySeconds { get; set; }      // 持续超阈 N 秒才触发
}
```

### 11.3 阶段 6：OpenTelemetry 跨消息队列追踪

RabbitMQ 是异步边界，默认 Trace 会断开。

**建议**：在 `DataBatch` 中预留 `TraceParent` 字段，Consumer 发送 ACK 前将当前 `Activity.Id` 注入日志：

```csharp
public class DataBatch
{
    public string? TraceParent { get; set; }
}

// Consumer 处理时
if (!string.IsNullOrEmpty(batch.TraceParent))
{
    var activity = new Activity("CloudGateway.ProcessBatch");
    activity.SetParentId(batch.TraceParent);
    activity.Start();
}
```

### 11.4 阶段 7：多租户数据隔离

当前 `tenant_id` 只是表中的一列，阶段 7 要实现真正的行级隔离。

**建议**：阶段 3 的 SQL 写入全部使用参数化 `@tenant_id`，并在 `CloudGatewayConfig` 中预留 `TenantResolutionMode`：

```csharp
public enum TenantResolutionMode
{
    Static,      // 从配置文件读取
    FromMessage  // 从 DataBatch.TenantId 读取（推荐，支持多租户）
}
```

这样阶段 7 只需改配置，无需改写入逻辑。

---

## 12. 建议补充的边界条件处理表

| 异常场景 | 当前方案 | 建议补充 |
|---------|---------|---------|
| TimescaleDB 写入成功，PostgreSQL 写入失败 | NACK 重入队 | 区分可重试/不可重试错误，脏数据直接 ACK 进审计日志 |
| 数据库长时间不可用（> 5 分钟） | 无限重试 | DLQ + 熔断机制，Consumer 暂停拉取 |
| 单条消息超大（> 1MB） | 直接 ACK | 结构化审计日志，记录 `BatchId` 和 payload 摘要 |
| 消息反序列化失败 | 直接 ACK | 补充审计日志表，记录原始 payload 前 200 字节 |
| 某工厂队列积压 10 万条 | 监控告警 | Consumer 动态降低 PrefetchCount，让 RabbitMQ 自然堆积 |
| CloudGateway 进程崩溃 | 未提及 | 缓冲队列数据丢失，参考第 1 条改为同步 flush 后 ACK |
| 两个 CloudGateway 实例同时消费同一队列 | 未提及 | 同一队列不应被多个实例消费；如需高可用，用 RabbitMQ 的 Consumer Tag 做独占消费 |
| TimescaleDB 磁盘满 | 未提及 | 健康检查失败，触发 readinessProbe 失败，K8s 停止流量 |
| 未知 `ValueType` | 未定义 | fallback 到字符串列，并记录告警日志 |
| 设备 `tags` 数组频繁更新 | 每次上报都写 | 缓存已知 tags，只有新 tag 出现时才更新数据库 |
| 工厂队列新增/删除 | 需重启服务 | 预留 `IFactoryRegistry` 接口，支持配置热更新 |

---

## 13. 优先级建议

| 优先级 | 条目 | 理由 |
|--------|------|------|
| 🔴 P0 | ACK 时机与缓冲 flush 同步 | 进程崩溃丢数据，这是数据可靠性的核心防线 |
| 🔴 P0 | DLQ + 熔断 + 重试计数 | 数据库故障时无限重试会打垮整个 Consumer |
| 🔴 P0 | 可重试 vs 不可重试错误区分 | 脏数据无限循环是生产环境常见故障源 |
| 🟡 P1 | 背压/内存队列上限 | 海量回放数据时 OOM 风险 |
| 🟡 P1 | `DateTimeOffset` 统一 | 时区漂移导致时序查询结果不可信 |
| 🟡 P1 | TimescaleDB retention policy | 无 retention 时序表无限膨胀 |
| 🟡 P1 | `devices.tags` 缓存降低更新频率 | 行级锁热点影响并发写入性能 |
| 🟡 P1 | 健康检查端点 `/health` | K8s/Docker 存活探针必需 |
| 🟢 P2 | 配置热更新（`IFactoryRegistry`） | 阶段 7 避免大规模重构 |
| 🟢 P2 | 超大消息/未知类型审计日志 | 运维排障需要 |
| 🟢 P2 | 连续聚合刷新策略 | 物化视图不刷新就是空表 |
| ⚪ P3 | 报警规则预留 Deadband / DelaySeconds | 阶段 4 前置设计 |
| ⚪ P3 | OpenTelemetry traceparent | 阶段 6 追踪完整链路 |
| ⚪ P3 | AlarmService 拉/推模式架构预留 | 阶段 4 技术选型参考 |

---

## 14. 总结

CloudGateway 是**边缘到云端的数据咽喉**，它的稳定性直接决定了整个 SaaS 平台的数据完整性。当前最优先补强的三件事：

1. **ACK 与 flush 同步**：确保"先写数据库，后 ACK"不是一句空话。内存缓冲不能成为数据丢失的漏洞。
2. **DLQ + 熔断**：工业现场数据库维护、网络闪断是常态，Consumer 必须有自我保护机制，不能无限重试。
3. **可重试 vs 不可重试错误区分**：边缘端的脏数据、格式异常不应拖垮云端。该丢的丢，该记的记。

这三件事做好了，阶段 4 的 AlarmService 和阶段 7 的多租户才能在一个可靠的数据底座上继续演进。
