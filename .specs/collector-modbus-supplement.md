# AmGatewayCloud.Collector.Modbus — 方案补充

> 基于 `collector-modbus.md` 的审查，补充异常/边界条件和优化建议。

## 1. 启动校验增强

### 1.1 Modbus 协议单次读取上限

| 寄存器类型 | 功能码 | 单次最大数量 |
|-----------|-------|-------------|
| Holding Register | FC03 | 125 |
| Input Register | FC04 | 125 |
| Coil | FC01 | 2000 |
| Discrete Input | FC02 | 2000 |

**规则**：启动时校验每个 `RegisterGroupConfig.Count` 不超过对应上限，超限则抛异常阻止启动。

```csharp
// CollectorConfigValidator（建议新增）
private static void ValidateRegisterGroup(RegisterGroupConfig group)
{
    var maxCount = group.Type switch
    {
        RegisterType.Holding or RegisterType.Input => 125,
        RegisterType.Coil or RegisterType.Discrete => 2000,
        _ => throw new ArgumentOutOfRangeException(nameof(group.Type))
    };

    if (group.Count > maxCount)
        throw new InvalidOperationException(
            $"Register group '{group.Name}' Count={group.Count} exceeds " +
            $"Modbus limit of {maxCount} for type {group.Type}");

    if (group.Count <= 0)
        throw new InvalidOperationException(
            $"Register group '{group.Name}' Count must be > 0");
}
```

### 1.2 寄存器组地址区间重叠检测

同类型寄存器组之间，`[Start, Start + Count)` 区间不应重叠。重叠会导致重复读取和 Tag 冲突。

```csharp
private static void ValidateNoOverlap(List<RegisterGroupConfig> groups)
{
    foreach (var typeGroup in groups.GroupBy(g => g.Type))
    {
        var sorted = typeGroup
            .OrderBy(g => g.Start)
            .ToList();

        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = sorted[i - 1];
            var curr = sorted[i];

            if (curr.Start < prev.Start + prev.Count)
                throw new InvalidOperationException(
                    $"Register groups '{prev.Name}' [{prev.Start}..{prev.Start + prev.Count - 1}] " +
                    $"and '{curr.Name}' [{curr.Start}..{curr.Start + curr.Count - 1}] overlap");
        }
    }
}
```

### 1.3 Tags 非空校验

`Tags` 不允许为 null 或空列表（当 Count > 0 时），且 `Tags.Count` 必须等于 `Count`。

---

## 2. 连接管理增强

### 2.1 增加连接超时配置

当前只有 `ReadTimeoutMs`，首次 `ConnectAsync` 可能长时间阻塞。增加 `ConnectTimeoutMs`。

```json
{
  "Modbus": {
    "Host": "localhost",
    "Port": 5020,
    "SlaveId": 1,
    "ReconnectIntervalMs": 5000,
    "ReadTimeoutMs": 3000,
    "ConnectTimeoutMs": 5000
  }
}
```

```csharp
public class ModbusConfig
{
    // ... 原有字段 ...
    public int ConnectTimeoutMs { get; set; } = 5000;
}
```

实现方式：`ConnectAsync` 内部用 `CancellationTokenSource` 组合连接超时和外部取消令牌。

### 2.2 重连指数退避

避免对端频繁重启时重连风暴。连接成功后重置退避计数。

```
重连间隔序列：5s → 10s → 20s → 40s → 60s（上限）
连接成功后重置为 5s
```

```csharp
private int CalculateReconnectDelay(int attempt)
{
    var baseInterval = _config.ReconnectIntervalMs;
    var maxInterval = 60_000; // 60s 上限
    var delay = Math.Min(baseInterval * (1 << Math.Min(attempt, 5)), maxInterval);
    return delay;
}
```

### 2.3 IsConnected 线程安全

`IsConnected` 属性需保证多线程可见性。

```csharp
private volatile bool _isConnected;
public bool IsConnected => _isConnected;
```

状态变更在锁内完成，读取用 `volatile` 保证可见性。

---

## 3. 采集主循环增强

### 3.1 全部寄存器组失败时主动重连

当前逻辑是每组 catch + Warn，但如果所有组都失败，说明连接已断，应主动触发重连而非等下一轮再失败。

```csharp
// 在一轮轮询结束后
if (failedCount == registerGroups.Count)
{
    _logger.LogWarning("All register groups failed, triggering reconnect");
    await _connection.ReconnectAsync(ct);
}
```

### 3.2 严格轮询周期（可选）

当前：实际周期 = 读取耗时 + PollIntervalMs。

如需严格固定周期，改用 `PeriodicTimer`（.NET 6+）：

```csharp
using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.PollIntervalMs));

while (await timer.WaitForNextTickAsync(ct))
{
    foreach (var group in _config.RegisterGroups)
    {
        // ... 读取 + 输出
    }
}
```

> **注意**：如果读取耗时超过 PollIntervalMs，`PeriodicTimer` 会立即触发下一轮，可能造成堆积。需要根据实际场景选择。

### 3.3 优雅关机确认

确保 `StopAsync` 等待 `ExecuteAsync` 退出。BackgroundService 默认行为已包含此逻辑，但要确保 `ExecuteAsync` 中所有 `await` 都响应 `CancellationToken`，特别是 `Task.Delay`。

```csharp
// 正确
await Task.Delay(_config.PollIntervalMs, ct);

// 错误 — 不响应取消，关机时可能卡住
await Task.Delay(_config.PollIntervalMs);
```

---

## 4. 数据模型增强

### 4.1 DataPoint.Value 序列化问题

`object?` 类型用 System.Text.Json 序列化时，值类型（int/float/bool）会丢失类型信息，输出为 `{}` 或需要自定义转换。

**方案A**：增加 `ValueType` 字段（推荐）

```csharp
public class DataPoint
{
    // ... 原有字段 ...

    /// <summary>值类型标识，用于 JSON 序列化</summary>
    public string? ValueType { get; set; }  // "int", "float", "bool", "string"
}
```

**方案B**：自定义 JsonConverter

```csharp
public class DataPointValueConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.TryGetProperty("type", out var typeProp) &&
            root.TryGetProperty("value", out var valueProp))
        {
            return typeProp.GetString() switch
            {
                "int" => valueProp.GetInt32(),
                "float" => valueProp.GetSingle(),
                "bool" => valueProp.GetBoolean(),
                "string" => valueProp.GetString(),
                _ => valueProp.GetRawText()
            };
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value?.GetType().Name.ToLowerInvariant());
        writer.WritePropertyName("value");

        switch (value)
        {
            case int i: writer.WriteNumberValue(i); break;
            case float f: writer.WriteNumberValue(f); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case string s: writer.WriteStringValue(s); break;
            default: writer.WriteNullValue(); break;
        }

        writer.WriteEndObject();
    }
}
```

### 4.2 采集质量标记

工业协议中的"质量戳"概念，让下游可以判断数据是否可信。

```csharp
public enum DataQuality
{
    Good,       // 正常读取
    Bad,        // 读取失败
    Unknown     // 初始/无数据状态
}

public class DataPoint
{
    // ... 原有字段 ...

    /// <summary>数据质量</summary>
    public DataQuality Quality { get; set; } = DataQuality.Good;
}
```

**使用场景**：读取失败时仍输出 DataPoint（Quality=Bad, Value=null），而非直接跳过。下游可根据 Quality 决定是否使用、是否告警。

---

## 5. 寄存器转换增强

### 5.1 Scale + Offset 替代 ScaleFactor

当前仅支持 `raw ÷ ScaleFactor`。工业场景中常需要：
- `raw × 0.001`（ScaleFactor 不方便表达小数缩放）
- `raw - 273.15`（开尔文转摄氏）
- `raw × 0.1 - 40`（带偏移的温度传感器）

**扩展方案**：

```csharp
public class RegisterGroupConfig
{
    // ... 原有字段 ...

    /// <summary>
    /// 缩放系数，转换公式：value = raw × Scale + Offset
    /// 默认 1.0。原 ScaleFactor=10.0 等价于 Scale=0.1
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// 偏移量，转换公式：value = raw × Scale + Offset
    /// 默认 0.0
    /// </summary>
    public double Offset { get; set; } = 0.0;
}
```

**迁移映射**：

| 旧配置 | 新配置 | 等价转换 |
|-------|-------|---------|
| `ScaleFactor: 10.0` | `Scale: 0.1, Offset: 0.0` | `raw × 0.1 = raw / 10` |
| `ScaleFactor: 1.0` | `Scale: 1.0, Offset: 0.0` | 不变 |
| 不支持 | `Scale: 0.001, Offset: 0.0` | `raw × 0.001` |
| 不支持 | `Scale: 1.0, Offset: -273.15` | `raw - 273.15` |

> **兼容性**：可同时保留 `ScaleFactor` 字段，启动时如果 `ScaleFactor != 1.0` 且 `Scale == 1.0`，自动换算 `Scale = 1.0 / ScaleFactor`。

---

## 6. 依赖注入调整

### 6.1 配置校验注入

新增 `CollectorConfigValidator`，在 DI 阶段完成校验，失败快速（Fail Fast）。

```csharp
// Program.cs
builder.Services.AddSingleton<IValidateOptions<CollectorConfig>, CollectorConfigValidator>();

// 或在 PostConfigure 中校验
builder.Services.PostConfigure<CollectorConfig>(config =>
{
    ConfigValidator.Validate(config); // 不合法直接抛异常
});
```

### 6.2 Dispose 顺序保障

确保关机时 `ModbusCollectorService` 先停止轮询，再释放 `ModbusConnection`。

当前注册顺序已正确（Singleton 先注册，HostedService 后注册，DI 按逆序 Dispose），但需确认 `ExecuteAsync` 正确响应 `CancellationToken`。

---

## 7. 错误处理策略补充

| 场景 | 处理 |
|------|------|
| Count 超过 Modbus 单次读取上限 | 启动校验，抛异常阻止启动 |
| 同类型寄存器组地址区间重叠 | 启动校验，抛异常阻止启动 |
| Count ≤ 0 | 启动校验，抛异常阻止启动 |
| Tags 为 null 或空（Count > 0 时） | 启动校验，抛异常阻止启动 |
| ConnectAsync 超时 | 使用 ConnectTimeoutMs 的 CancellationToken，超时后进入重连 |
| 重连风暴 | 指数退避，上限 60s，连接成功后重置 |
| 所有寄存器组失败 | 主动触发重连，而非等下一轮 |
| DataPoint.Value JSON 序列化 | 增加 ValueType 字段或自定义 JsonConverter |
| 读取失败的数据点 | 输出 Quality=Bad 的 DataPoint，而非跳过 |

---

## 8. 配置模型补充汇总

```csharp
public class ModbusConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5020;
    public byte SlaveId { get; set; } = 1;
    public int ReconnectIntervalMs { get; set; } = 5000;
    public int ReadTimeoutMs { get; set; } = 3000;
    public int ConnectTimeoutMs { get; set; } = 5000;       // 新增
}

public class RegisterGroupConfig
{
    public string Name { get; set; } = string.Empty;
    public RegisterType Type { get; set; }
    public ushort Start { get; set; }
    public int Count { get; set; }
    public double ScaleFactor { get; set; } = 1.0;          // 保留兼容
    public double Scale { get; set; } = 1.0;                // 新增
    public double Offset { get; set; } = 0.0;               // 新增
    public List<string> Tags { get; set; } = [];
}
```

---

## 9. 其他建议

### 9.1 单设备 vs 多设备

当前 `CollectorConfig` 只有一个 `Modbus` 连接配置，一个实例只能连一个 Slave。如果后续需要采集多台设备（多 IP / 多 SlaveId），需要重构配置和连接管理。

**方案**：将 `Modbus` 改为 `List<ModbusDeviceConfig>`，每个设备独立配置、独立 `ModbusConnection`。

```json
{
  "Collector": {
    "Devices": [
      {
        "DeviceId": "pump-001",
        "TenantId": "factory-a",
        "PollIntervalMs": 2000,
        "Modbus": { "Host": "192.168.1.10", "Port": 502, "SlaveId": 1 },
        "RegisterGroups": [ ... ]
      },
      {
        "DeviceId": "valve-002",
        "TenantId": "factory-a",
        "PollIntervalMs": 3000,
        "Modbus": { "Host": "192.168.1.11", "Port": 502, "SlaveId": 2 },
        "RegisterGroups": [ ... ]
      }
    ]
  }
}
```

> **现阶段**：单设备足够，无需现在改。可以在代码中留个 TODO，后续有需求时扩展。

### 9.2 NModbus 包版本

`3.0.*` 是 NModbus 的预发布版本号区间。当前 NuGet 上稳定版为 `3.0.81` 左右。

**建议**：
- 如果确定用 NModbus，锁定具体版本如 `3.0.81`，避免 `3.0.*` 拉到破坏性变更
- 如果对新版 API 不确定，也可考虑 `NModbus4`（社区维护的 .NET 现代化分支），但需评估兼容性
- 阶段1先用固定版本跑通，后续再评估是否升级

### 9.3 32 位寄存器（双字）支持

当前方案每个寄存器映射一个 Tag，对应 16 位值。但 Modbus 中 32 位整数（INT32）和浮点数（IEEE 754 FLOAT）占用 **2 个连续寄存器**，64 位占 4 个。

当前 `Count=10, Tags=10` 的设计无法表达"这两个寄存器组成一个 float"。

**方案**：在 `RegisterGroupConfig` 中增加可选的 `RegisterWidth` 字段：

```csharp
public class RegisterGroupConfig
{
    // ... 原有字段 ...

    /// <summary>
    /// 每个 Tag 占用的寄存器数量。
    /// 1 = 16位（默认），2 = 32位（INT32/FLOAT），4 = 64位（INT64/DOUBLE）
    /// </summary>
    public int RegisterWidth { get; set; } = 1;

    /// <summary>
    /// 32位+时的字节序：BigEndian（默认，Modbus 标准）、LittleEndian、WordSwap
    /// </summary>
    public string ByteOrder { get; set; } = "BigEndian";
}
```

> **现阶段**：当前虚拟从站都是 16 位整数，暂不需要。但工业场景很常见，建议作为 P2 备忘。

### 9.4 健康检查端点

作为微服务，应暴露健康检查端点，供编排系统（K8s/Docker Compose）探活。

**方案**：利用 ASP.NET Core 的 `HealthChecks` 中间件（项目模板已用 `Microsoft.NET.Sdk.Web`）：

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<ModbusHealthCheck>("modbus", tags: ["ready"]);

// 映射端点
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false }); // 存活
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }); // 就绪
```

```csharp
public class ModbusHealthCheck(ModbusConnection connection) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(connection.IsConnected
            ? HealthCheckResult.Healthy("Modbus connected")
            : HealthCheckResult.Unhealthy("Modbus disconnected"));
}
```

> **现阶段**：阶段1控制台输出不需要，阶段5容器化时加上。

### 9.5 数据点去重 / 变化检测

当前每轮都输出所有数据点。如果值没变化，下游会收到大量重复数据。

**方案**：可选的 `OnlyOnChange` 模式，缓存上一次值，仅输出变化的数据点。

```csharp
public class CollectorConfig
{
    // ... 原有字段 ...

    /// <summary>仅输出值发生变化的数据点（默认 false，每轮全量输出）</summary>
    public bool OnlyOnChange { get; set; } = false;
}
```

> **现阶段**：阶段1不需要，后续如果数据量大或带宽敏感时启用。

---

## 10. 优先级建议

| 优先级 | 条目 | 理由 |
|-------|------|------|
| 🔴 P0 | Count 上限校验（1.1） | 运行时协议异常，难以排查 |
| 🔴 P0 | DataPoint.Value 序列化（4.1） | 阶段3 RabbitMQ 必需，延后是大坑 |
| 🔴 P0 | 全部失败主动重连（3.1） | 断线后空转，数据丢失 |
| 🟡 P1 | 连接超时配置（2.1） | 首次连接可能卡 20s+ |
| 🟡 P1 | 指数退避（2.2） | 防止重连风暴刷屏日志 |
| 🟡 P1 | 地址区间重叠检测（1.2） | 配置错误时数据混乱 |
| 🟢 P2 | Scale + Offset（5.1） | 功能增强，可后续迭代 |
| 🟢 P2 | 采集质量标记（4.2） | 工业标准，可后续迭代 |
| 🟢 P2 | 严格轮询周期（3.2） | 看场景需求 |
| ⚪ P3 | 多设备支持（9.1） | 阶段1单设备够用，后续按需 |
| ⚪ P3 | NModbus 版本锁定（9.2） | 阶段1跑通后锁定 |
| ⚪ P3 | 32位寄存器支持（9.3） | 当前虚拟从站用16位，工业场景常见 |
| ⚪ P3 | 健康检查端点（9.4） | 阶段5容器化时加 |
| ⚪ P3 | 变化检测去重（9.5） | 数据量大时再考虑 |
