# AmGatewayCloud.EdgeGateway — 方案补充

> 基于 `edge-gateway.md` 与 `roadmap.md` 的审查，补充异常/边界条件和优化建议。

---

## 1. 数据可靠性边界：MQTT ACK 时机未明确

### 1.1 问题

当前流程是"写 InfluxDB（同步）→ 发 RabbitMQ（异步）"，但**没有说明 MQTT 消息何时 ACK**。

- 如果 MQTT QoS = 1，消息到达后立即 ACK，随后 InfluxDB 写入失败，这条数据就彻底丢了。
- 如果等 RabbitMQ 转发成功才 ACK，则 RabbitMQ 断开时 MQTT 消息会持续重发，导致 InfluxDB 重复写入。

### 1.2 建议

MQTT 消费使用 **QoS 1**，采用 **"写 InfluxDB 成功后才 ACK MQTT"** 的策略。

```csharp
// MqttConsumerService 消息处理流程
private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
{
    var batch = Deserialize(e.ApplicationMessage.Payload);

    // 1. 同步写入 InfluxDB（落盘是底线）
    await _influxWriter.WriteBatchAsync(batch, ct);

    // 2. 异步转发 RabbitMQ（不阻塞 ACK）
    _ = Task.Run(async () =>
    {
        var ok = await _rabbitForwarder.ForwardAsync(batch, ct);
        if (ok)
            _watermarkTracker.UpdateWatermark(batch.Timestamp, batch.BatchId);
    });

    // 3. InfluxDB 成功后立即 ACK MQTT
    e.ProcessingFailed = false;
}
```

> **原则**：RabbitMQ 转发失败不应阻塞 MQTT ACK，因为数据已在 InfluxDB 落盘。RabbitMQ 恢复后通过 ReplayService 补发即可。

---

## 2. Watermark 精度不足：仅靠时间戳无法精确定位

### 2.1 问题

```csharp
public DateTimeOffset LastForwardedAt { get; set; }
```

同一毫秒内可能有多条 `DataBatch`，恢复回放时按 `LastForwardedAt` 查询会导致**重复发送**或**漏发**。

### 2.2 建议

Watermark 应记录 **`LastBatchId`（Guid）+ `LastSequence`**，Replay 查询条件改为 `timestamp >= LastForwardedAt AND batchId != LastBatchId`，实现精确断点续传。

```csharp
public class Watermark
{
    public string HubId { get; set; } = string.Empty;

    /// <summary>最后成功转发的时间戳（排序用）</summary>
    public DateTimeOffset LastForwardedAt { get; set; }

    /// <summary>最后成功转发的批次 ID（精确去重用）</summary>
    public Guid LastBatchId { get; set; }

    /// <summary>同一毫秒内的序列号（防并发冲突）</summary>
    public long LastSequence { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
```

Replay 查询逻辑：

```csharp
// 读取 watermark.LastForwardedAt 之后的所有数据
// 但跳过 LastBatchId 本身（因为已经确认成功）
var query = $@"
    from(bucket: ""{_bucket}"")
    |> range(start: {from:O})
    |> filter(fn: (r) => r._measurement == "device_data")
    |> filter(fn: (r) => r.batch_id != "{watermark.LastBatchId}")
    |> sort(columns: ["_time"])
";
```

---

## 3. InfluxDB 存储设计：value 统一存 string 是巨大隐患

### 3.1 问题

> "value 统一以字符串存储，避免类型冲突"

Grafana 和后续分析无法对字符串做 `MEAN()`、`SUM()` 等数值聚合。工业场景里 90% 的点是数值型，这个设计会让阶段 4 的报警阈值判断和阶段 6 的可视化非常痛苦。

### 3.2 建议

InfluxDB Line Protocol 按 `valueType` 写入对应类型字段：

| valueType | InfluxDB Field | Line Protocol 类型 |
|-----------|----------------|-------------------|
| int / short / long | `value_int` | integer (i) |
| float / double | `value_float` | float |
| bool | `value_bool` | bool |
| string / json / 其他 | `value_string` | string |

```csharp
private string BuildLineProtocol(DataBatch batch, DataPoint point)
{
    var tags = $"factory_id={batch.FactoryId},workshop_id={batch.WorkshopId}," +
               $"device_id={batch.DeviceId},protocol={batch.Protocol}," +
               $"tag={EscapeTag(point.Tag)},quality={point.Quality}";

    var fields = point.ValueType?.ToLowerInvariant() switch
    {
        "int" or "short" or "long" or "int32" or "int64"
            => $"value_int={point.Value.GetInt64()}i,value_type=\"{point.ValueType}\"",
        "float" or "double" or "single"
            => $"value_float={point.Value.GetDouble()},value_type=\"{point.ValueType}\"",
        "bool" or "boolean"
            => $"value_bool={point.Value.GetBoolean().ToString().ToLower()},value_type=\"{point.ValueType}\"",
        _
            => $"value_string=\"{EscapeString(point.Value.ToString())}\",value_type=\"{point.ValueType}\""
    };

    return $"device_data,{tags} {fields} {new DateTimeOffset(batch.Timestamp).ToUnixTimeMilliseconds()}";
}
```

> 同时保留 `value_type` 字段用于消费端识别原始类型。

---

## 4. 回放服务的边界条件

### 4.1 问题

当前方案对以下场景没有处理：

1. 断网 3 天积累 50 万条数据，恢复后全速回放可能**打满 RabbitMQ 带宽**，影响该工厂实时数据；
2. 回放过程中网络再次闪断，没有**中断保护**；
3. 新数据与旧数据时间戳交错，导致**云端时序乱序**。

### 4.2 建议

#### 4.2.1 令牌桶限速

```csharp
public class ReplayService
{
    private readonly SemaphoreSlim _rateLimiter = new(100, 100); // 每秒 100 batch

    private async Task<bool> ThrottleAsync(CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(ct);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000, ct);
            _rateLimiter.Release();
        }, ct);
        return true;
    }
}
```

#### 4.2.2 回放中断保护

```csharp
public async Task ReplayAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
{
    var watermark = await _watermarkTracker.LoadAsync(ct);
    var currentFrom = from;

    while (currentFrom < to)
    {
        // 回放过程中 RabbitMQ 再次断开 → 暂停并持久化进度
        if (!_rabbitForwarder.IsOnline)
        {
            _logger.LogWarning("RabbitMQ disconnected during replay, pausing at {Position}", currentFrom);
            await _watermarkTracker.SaveAsync(ct); // 保存当前进度
            return; // 不抛异常，等下次恢复再继续
        }

        var batches = await _influxWriter.ReadRangeAsync(currentFrom, to, pageSize: 100, ct);
        if (batches.Count == 0) break;

        foreach (var batch in batches)
        {
            await ThrottleAsync(ct);
            var ok = await _rabbitForwarder.ForwardAsync(batch, ct);
            if (ok)
            {
                _watermarkTracker.UpdateWatermark(batch.Timestamp, batch.BatchId);
            }
        }

        currentFrom = batches.Last().Timestamp;
    }

    _logger.LogInformation("Replay completed: {From:O} → {To:O}", from, to);
}
```

#### 4.2.3 回放数据标记

```csharp
// 回放时增加 header 标记
var properties = new Dictionary<string, object>
{
    ["x-is-replay"] = true,
    ["x-original-timestamp"] = batch.Timestamp
};
```

让云端 Consumer 可选择性处理乱序数据。

---

## 5. 进程崩溃数据丢失：Watermark 刷盘策略

### 5.1 问题

> "内存缓存 + 定期持久化到本地文件"

进程崩溃时，上次刷盘到崩溃之间的成功转发记录全部丢失，恢复后会**重复发送**。

### 5.2 建议

**每次 RabbitMQ 转发成功后同步写 watermark 文件**。

```csharp
public class WatermarkTracker
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public void UpdateWatermark(DateTimeOffset timestamp, Guid batchId)
    {
        lock (_lock)
        {
            LastForwardedAt = timestamp;
            LastBatchId = batchId;
            LastSequence++;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        // 同步刷盘——本地 IO 成本极低，不应为了性能牺牲一致性
        _ = Task.Run(SaveAsync);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(this, _jsonOptions);
        await File.WriteAllTextAsync(_filePath, json, ct);
    }
}
```

> 如果担心高频写入，可用 **SQLite**（`UPSERT` 单条记录）替代文件，事务保证原子性且性能更好。

---

## 6. 特殊字符未处理：RoutingKey 中的点号冲突

### 6.1 问题

路由键模板：`amgateway.{factoryId}.{workshopId}.{deviceId}.{protocol}`

如果 `deviceId` 是 `"PLC.Line1.Temp"`，路由键变成 `...PLC.Line1.Temp...`，Topic 绑定会解析错误。

### 6.2 建议

生成 RoutingKey 时对变量值中的 `.` 替换为 `_` 或 `_2E`，并规定命名规范。

```csharp
public class RabbitMqForwarder
{
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
        // RabbitMQ Topic 中 . 是层级分隔符，必须转义
        return value.Replace(".", "_").Replace("*", "_").Replace("#", "_");
    }
}
```

---

## 7. 高可用场景：多 EdgeGateway 实例

### 7.1 问题

如果车间部署 2 台 EdgeGateway 做热备，两者订阅相同 MQTT Topic，会导致数据**重复写入 InfluxDB** 和**重复转发 RabbitMQ**。

### 7.2 建议

文档中补充 **MQTT 共享订阅**方案：

```json
{
  "Mqtt": {
    "TopicFilter": "$share/edgehub/amgateway/#",
    "ClientId": "AmGatewayCloud-EdgeHub-{HubId}-{Guid.NewGuid():N}"
  }
}
```

| 方案 | Topic 格式 | 效果 |
|------|-----------|------|
| 无共享订阅 | `amgateway/#` | 所有实例各收一份，数据重复 |
| 共享订阅（推荐） | `$share/edgehub/amgateway/#` | 同一消息只被一个实例消费 |

> 共享订阅需要 MQTT Broker 支持（Mosquitto 2.0+、EMQ X、HiveMQ 均支持）。

---

## 8. 缺少的配置项

| 缺失配置 | 影响 | 建议默认值 |
|---------|------|-----------|
| MQTT `QoS` | 消息可靠性等级 | `1`（至少一次） |
| MQTT `KeepAlive` | 心跳检测间隔 | `60` 秒 |
| MQTT `CleanSession` | 断线后是否清理会话 | `false`（保留订阅） |
| InfluxDB `BatchSize` | 批量写入条数 | `1000` |
| InfluxDB `FlushIntervalMs` | 批量刷新间隔 | `1000` ms |
| RabbitMQ `PrefetchCount` | 消费者预取数 | `50` |
| 密码/Token 加密存储 | 安全合规 | 用 DPAPI 或环境变量 |

```csharp
public class MqttConsumerConfig
{
    // ... 原有字段 ...
    public int QoS { get; set; } = 1;
    public int KeepAliveSeconds { get; set; } = 60;
    public bool CleanSession { get; set; } = false;
}

public class InfluxDbConfig
{
    // ... 原有字段 ...
    public int BatchSize { get; set; } = 1000;
    public int FlushIntervalMs { get; set; } = 1000;
}

public class RabbitMqConfig
{
    // ... 原有字段 ...
    public ushort PrefetchCount { get; set; } = 50;
}
```

---

## 9. Roadmap 全链路视角的优化建议

### 9.1 阶段 2 → 阶段 3：尽早确定云端时序库

路线图中阶段 3 写"InfluxDB Cloud / TimescaleDB 二选一"。

**建议**：阶段 2 结束前就确定。如果选 TimescaleDB（PostgreSQL 扩展），阶段 3 的 CloudGateway 可以直接复用 EF Core + Npgsql；如果选 InfluxDB Cloud，阶段 3 要引入 InfluxDB.Client，且和阶段 2 的 InfluxDB 用法一致。

### 9.2 阶段 3："一厂一 RabbitMQ" 在大规模下的隐患

> "一厂一个 RabbitMQ 实例"

100 个工厂 = 100 个 RabbitMQ 实例/连接，CloudGateway 要维护 100 个 Consumer，内存和运维压力巨大。

**建议**：评估改为 **一个 RabbitMQ Cluster + 每厂一个 VirtualHost** 或 **单实例 + Exchange/Topic 隔离**。阶段 3 的架构图可以预留"共享集群"方案。

### 9.3 阶段 4：报警阈值缺少工业常用机制

> "阈值判断 → 生成 Alarm"

现场传感器抖动会导致**频繁报警/恢复**（报警风暴）。

**建议**：报警规则预留 **Deadband（死区）** 和 **延时报警（如持续超阈 30s 才触发）**。在阶段 4 设计 `AlarmRule` 实体时就要预留字段：

```csharp
public class AlarmRule
{
    public Guid Id { get; set; }
    public string Tag { get; set; } = string.Empty;
    public double Threshold { get; set; }
    public string Operator { get; set; } = ">"; // >, <, >=, <=, ==
    public double? Deadband { get; set; }      // 死区阈值，避免抖动
    public int? DelaySeconds { get; set; }      // 持续超阈 N 秒才触发
}
```

### 9.4 阶段 5：DDD 聚合边界有冲突

```
Equipment (聚合根)
└── Alarms (集合)

Alarm (聚合根)
```

`Alarm` 既被声明为聚合根，又出现在 `Equipment` 的集合中，违反 DDD 聚合根原则。

**建议**：`Equipment` 只保留 `EquipmentId`，`Alarm` 作为独立聚合根，通过 `IAlarmRepository` 按 `EquipmentId` 查询。`WorkOrder` 同理。

### 9.5 阶段 6：OpenTelemetry 跨越消息队列会断链

> "完整链路：采集器 → EdgeHub → RabbitMQ → CloudGateway → 报警 → 工单"

RabbitMQ 是异步边界，默认 Trace 会断开。

**建议**：在 MQTT 消息和 AMQP 消息的 **Header/Properties** 中注入 `traceparent`，确保跨消息队列的链路追踪不断。

```csharp
// DataBatch 预留 traceparent
public class DataBatch
{
    // ... 原有字段 ...
    public string? TraceParent { get; set; }
}

// 发送 AMQP 消息时注入
var properties = channel.CreateBasicProperties();
properties.Headers ??= new Dictionary<string, object>();
properties.Headers["traceparent"] = Activity.Current?.Id;
```

### 9.6 阶段 7：边缘网关的 TenantId 不应硬编码

> "边缘网关按配置注入 TenantId"

阶段 2 的 `appsettings.json` 里 `TenantId` 写死 `"default"`，阶段 7 要改为动态下发时，所有车间网关都要改配置并重启。

**建议**：阶段 2 就设计 **配置中心接口**（哪怕实现是轮询本地文件），`EdgeGatewayConfig` 从 `IOptions` 改为 `IOptionsMonitor` 或自己实现热加载。

```csharp
// Program.cs
builder.Services.AddSingleton<IEdgeGatewayConfigProvider, FileConfigProvider>();
builder.Services.AddSingleton<EdgeGatewayConfig>(sp =>
    sp.GetRequiredService<IEdgeGatewayConfigProvider>().GetConfig());

// 配置中心接口
public interface IEdgeGatewayConfigProvider
{
    EdgeGatewayConfig GetConfig();
    event EventHandler<ConfigChangedEventArgs>? ConfigChanged;
}
```

---

## 10. 建议补充的边界条件处理表

| 异常场景 | 当前方案 | 建议补充 |
|---------|---------|---------|
| MQTT 与 InfluxDB 同时断开 | 未提及 | 进入"只缓存内存队列"模式，设上限防 OOM |
| 网络闪断 (< 5s) | 立即标记 offline 并触发回放 | 增加防抖（如连续 3 次心跳失败才标记 offline） |
| 回放时 RabbitMQ 再次断开 | 未提及 | 暂停回放，持久化进度，恢复后断点续传 |
| InfluxDB 磁盘满 | Fatal 日志 | 除 Fatal 外，停止 MQTT ACK，进入只读保护，防止新数据覆盖旧数据 |
| 采集器时钟漂移 | 未提及 | 边缘网关写入时增加 `received_at` 字段，与 `batch.timestamp` 分离 |
| 单条 MQTT 消息超大（> 1MB） | 未提及 | 设置消息大小上限，超限直接丢弃并告警，防内存爆炸 |
| 两个 EdgeGateway 同时运行 | 未提及 | MQTT 共享订阅（`$share/edgehub/amgateway/#`） |
| Watermark 文件损坏 | 未提及 | 启动时校验 JSON 完整性，损坏则从 InfluxDB 最早记录开始回放 |
| InfluxDB 与 RabbitMQ 均正常但写入/发送超时 | 未提及 | 设置单次操作超时（如 10s），超时视为失败走降级逻辑 |

### 10.1 网络闪断防抖

```csharp
public class RabbitMqForwarder
{
    private int _consecutiveFailures = 0;
    private const int OfflineThreshold = 3;

    public async Task<bool> ForwardAsync(DataBatch batch, CancellationToken ct)
    {
        try
        {
            await PublishAsync(batch, ct);
            _consecutiveFailures = 0;
            if (!IsOnline) IsOnline = true; // 恢复
            return true;
        }
        catch
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= OfflineThreshold)
            {
                IsOnline = false;
                _logger.LogWarning("RabbitMQ marked offline after {Count} consecutive failures", OfflineThreshold);
            }
            return false;
        }
    }
}
```

### 10.2 单条消息大小限制

```csharp
private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
{
    const int MaxPayloadSize = 1024 * 1024; // 1MB

    if (e.ApplicationMessage.Payload.Length > MaxPayloadSize)
    {
        _logger.LogError("MQTT payload too large: {Size} bytes, dropping", e.ApplicationMessage.Payload.Length);
        e.ProcessingFailed = false; // ACK 但不处理
        return;
    }

    // ... 正常处理
}
```

### 10.3 采集器时钟漂移处理

```csharp
public class DataBatch
{
    // ... 原有字段 ...

    /// <summary>边缘网关收到该批次的时间（UTC），用于检测采集器时钟漂移</summary>
    public DateTimeOffset ReceivedAt { get; set; }
}

// MqttConsumerService 中写入时赋值
batch.ReceivedAt = DateTimeOffset.UtcNow;
```

如果 `ReceivedAt - Timestamp > 阈值`（如 5 分钟），记录告警日志。

---

## 11. 优先级建议

| 优先级 | 条目 | 理由 |
|--------|------|------|
| 🔴 P0 | Watermark 精确化（BatchId + Sequence） | 断网恢复后重复/漏发数据 |
| 🔴 P0 | InfluxDB 类型化存储（value_int/float/bool/string） | 否则 Grafana 和报警服务无法做数值聚合 |
| 🔴 P0 | MQTT ACK 时机（InfluxDB 成功后 ACK） | 数据丢失的核心防线 |
| 🔴 P0 | 回放中断保护 + 限速 | 全速回放可能打垮 RabbitMQ |
| 🟡 P1 | Watermark 同步刷盘 | 进程崩溃导致重复发送 |
| 🟡 P1 | RoutingKey 特殊字符转义 | 设备 ID 含点号时 Topic 解析错误 |
| 🟡 P1 | MQTT 共享订阅（多实例高可用） | 热备场景数据重复 |
| 🟡 P1 | 缺少配置项（QoS/KeepAlive/BatchSize/FlushInterval） | 工业现场调优必需 |
| 🟡 P1 | 网络闪断防抖 | 避免频繁标记 offline/online |
| 🟡 P1 | 单条消息大小限制 | 防止超大消息导致 OOM |
| 🟢 P2 | 采集器时钟漂移检测（received_at） | 调试/审计需要 |
| 🟢 P2 | 配置热加载接口 | 阶段 7 避免大规模重构 |
| 🟢 P2 | Watermark 文件损坏恢复 | 启动鲁棒性 |
| 🟢 P2 | 回放数据标记 `x-is-replay` | 云端 Consumer 可选处理乱序 |
| ⚪ P3 | 报警规则预留 Deadband / DelaySeconds | 阶段 4 前置设计 |
| ⚪ P3 | OpenTelemetry traceparent 跨消息队列 | 阶段 6 追踪完整链路 |
| ⚪ P3 | DDD 聚合边界修正 | 阶段 5 避免返工 |
| ⚪ P3 | 一厂一 RabbitMQ → 共享集群评估 | 阶段 3 架构决策 |

---

## 12. 总结

工业网关的核心是 **"极端环境下的确定性行为"**。当前最优先补强的三件事：

1. **Watermark 精确化**：从"时间戳"改为 `BatchId + Sequence`，杜绝重复回放。
2. **InfluxDB 类型化**：数值型数据必须按数值类型存储，否则阶段 4/6 会很痛苦。
3. **配置与安全前置**：MQTT QoS、共享订阅、密码加密、配置热加载，在阶段 2 就打好地基，而不是阶段 7 再重构。
