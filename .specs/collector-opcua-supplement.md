# AmGatewayCloud.Collector.OpcUa — 方案补充

> 基于 `collector-opcua.md` 的审查，补充异常/边界条件和优化建议。

## 1. 数据模型修正

### 1.1 DataPoint 补充 Quality + ValueType 字段

原方案 `DataPoint` 缺少 `Quality` 和 `ValueType` 两个字段，与 Modbus 实际实现不一致。`MapStatusCode` 的结果无处安放，`object?` 序列化会丢类型信息。

```csharp
namespace AmGatewayCloud.Collector.OpcUa.Models;

public class DataPoint
{
    /// <summary>设备标识，配置中指定</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>数据标签名，如 "Temperature", "Pressure"</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>采集值（动态类型：int/float/bool/string）</summary>
    public object? Value { get; set; }

    /// <summary>值类型标识，用于 JSON 序列化（"int", "float", "bool", "string", "double"）</summary>
    public string? ValueType { get; set; }

    /// <summary>采集时间（UTC），优先使用 OPC UA SourceTimestamp</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>数据质量</summary>
    public DataQuality Quality { get; set; } = DataQuality.Good;

    /// <summary>多租户伏笔，阶段6启用</summary>
    public string? TenantId { get; set; }

    /// <summary>扩展属性（如节点ID、StatusCode、ServerTimestamp）</summary>
    public Dictionary<string, object>? Properties { get; set; }

    // --- 工厂方法 ---

    public static DataPoint Good(string deviceId, string tag, object? value, DateTime timestamp,
        string? tenantId = null, string? groupName = null)
    {
        return new DataPoint
        {
            DeviceId = deviceId,
            Tag = tag,
            Value = value,
            ValueType = value?.GetType().Name.ToLowerInvariant(),
            Timestamp = timestamp,
            Quality = DataQuality.Good,
            TenantId = tenantId,
            Properties = groupName is not null
                ? new Dictionary<string, object> { ["GroupName"] = groupName }
                : null
        };
    }

    public static DataPoint Bad(string deviceId, string tag, DateTime timestamp,
        string? tenantId = null, string? groupName = null)
    {
        return new DataPoint
        {
            DeviceId = deviceId,
            Tag = tag,
            Value = null,
            ValueType = null,
            Timestamp = timestamp,
            Quality = DataQuality.Bad,
            TenantId = tenantId,
            Properties = groupName is not null
                ? new Dictionary<string, object> { ["GroupName"] = groupName }
                : null
        };
    }
}
```

### 1.2 DataQuality 枚举统一

OPC UA 方案定义了 `Good/Uncertain/Bad`，Modbus 补充文档定义了 `Good/Bad/Unknown`。后续抽取 `Abstractions` 时需要统一。

**建议**：现在就统一为 `Good/Uncertain/Bad`（与 OPC UA 标准对齐），Modbus 的 `Unknown` 映射为 `Uncertain`。

```csharp
public enum DataQuality
{
    Good,       // 正常读取 / StatusCode.Good
    Uncertain,  // 不确定 / StatusCode.Uncertain* / Modbus 的 Unknown
    Bad         // 读取失败 / StatusCode.Bad*
}
```

### 1.3 OPC UA 扩展值类型映射

原方案 3.2 节的类型映射表不完整，OPC UA 服务器可能返回更多类型：

| OPC UA DataType | .NET 类型 | DataPoint.ValueType | 说明 |
|-----------------|----------|---------------------|------|
| Double | `double` | "double" | 最常见 |
| Float | `float` | "single" | 注意：`GetType().Name` 是 `Single` |
| Int32 | `int` | "int32" | 注意：`GetType().Name` 是 `Int32` |
| Int16 | `short` | "int16" | |
| UInt16 | `ushort` | "uint16" | |
| UInt32 | `uint` | "uint32" | |
| Int64 | `long` | "int64" | |
| Boolean | `bool` | "boolean" | 注意：`GetType().Name` 是 `Boolean` |
| String | `string` | "string" | |
| Byte | `byte` | "byte" | |
| SByte | `sbyte` | "sbyte" | |
| DateTime | `DateTime` | "datetime" | OPC UA DateTime → .NET DateTime |

> **问题**：`value?.GetType().Name.ToLowerInvariant()` 对 `int` 返回 `"int32"`，对 `float` 返回 `"single"`，对 `bool` 返回 `"boolean"`。如果 Modbus 端的 ValueType 是 `"int"` / `"float"` / `"bool"`，下游需要做映射。

**方案**：在 `DataPoint.Good()` 工厂方法中统一 ValueType 的命名，或在序列化时用自定义映射：

```csharp
private static string? MapValueType(object? value)
{
    return value switch
    {
        double => "double",
        float => "float",
        int => "int",
        long => "long",
        short => "short",
        ushort => "ushort",
        uint => "uint",
        bool => "bool",
        string => "string",
        DateTime => "datetime",
        byte => "byte",
        null => null,
        _ => value.GetType().Name.ToLowerInvariant()
    };
}
```

> **注意**：此映射应与 Modbus 采集器统一，后续抽取到 `Collector.Abstractions` 时只需一份。

---

## 2. MonitoredItem → Tag 映射机制

原方案 `OnNotification` 流程写了 "查找 Tag（item → NodeConfig 映射）"，但未说明如何实现。OPC UA SDK 的通知回调只提供 `MonitoredItem` 对象，必须自行建立映射。

### 2.1 方案A：MonitoredItem.Handle（推荐）

OPC UA SDK 的 `MonitoredItem` 有 `Handle` 属性（`object?` 类型），专门用于关联用户数据：

```csharp
// 创建 MonitoredItem 时
var item = new MonitoredItem(subscription.DefaultItem)
{
    StartNodeId = nodeId,
    SamplingInterval = _config.OpcUa.SamplingIntervalMs,
    QueueSize = 10,
    Handle = nodeConfig  // ← 关联 NodeConfig
};
item.Notification += OnNotification;

// 通知回调中
private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
{
    var nodeConfig = (NodeConfig)item.Handle;  // ← 直接取出
    // ...
}
```

**优点**：零内存开销，无需额外字典，SDK 原生支持。

### 2.2 方案B：Dictionary<MonitoredItem, NodeConfig>

```csharp
private readonly ConcurrentDictionary<MonitoredItem, (NodeGroupConfig Group, NodeConfig Node)> _itemMap = new();

// 创建时
_itemMap[item] = (group, node);

// 回调中
if (_itemMap.TryGetValue(item, out var mapping)) { ... }

// 重建订阅时
_itemMap.Clear();
```

**缺点**：额外内存、清理时需注意。

### 2.3 建议

用 **方案A（Handle）**，简洁且与 SDK 语义对齐。同时在 Handle 中存储 `(NodeGroupConfig Group, NodeConfig Node)` 元组，以便回调时同时获取组名和 Tag。

---

## 3. 订阅重建的边界条件

### 3.1 重建前必须清理旧 Subscription

`SessionRestored` 事件触发后重建订阅，但如果 SDK 自动重连保留了旧 Session，旧 Subscription 可能仍然存在。不清理就创建新的会导致重复通知。

```csharp
private Subscription? _subscription;

private async Task CreateSubscriptionAsync()
{
    // 1. 清理旧订阅
    if (_subscription != null)
    {
        try
        {
            await _subscription.DeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete old subscription during rebuild");
        }
        _subscription = null;
    }

    // 2. 清理映射表（如果用 Dictionary 方案）
    // _itemMap.Clear();

    // 3. 创建新订阅
    _subscription = new Subscription(_session.DefaultSubscription)
    {
        PublishingInterval = _config.OpcUa.PublishingIntervalMs,
        // ...
    };

    // ...
}
```

### 3.2 重建过程中又断线的递归处理

`SessionRestored` → `CreateSubscriptionAsync()` 执行中，Session 又断了怎么办？

如果 `CreateSubscriptionAsync` 抛异常，不能让它 bubble up 导致服务退出，也不能在事件处理器中 await 时阻塞重连逻辑。

```csharp
_session.SessionRestored += async (_, _) =>
{
    try
    {
        _logger.LogInformation("Session restored, rebuilding subscription");
        await CreateSubscriptionAsync();
    }
    catch (Exception ex)
    {
        // 不退出，等下次 SessionRestored 再试
        _logger.LogError(ex, "Failed to rebuild subscription after session restore, will retry on next restore");
    }
};
```

> **关键**：`SessionRestored` 事件处理器中的异常不能影响 `OpcUaSession` 的重连状态机。必须 try-catch 兜底。

### 3.3 重建订阅时 NamespaceIndex 必须重新解析

OPC UA 服务器的 NamespaceIndex 在不同连接中可能变化。重连后必须：

1. 调用 `session.FetchNamespaceTables()` 同步服务器端的命名空间表
2. 重新解析所有 `NamespaceUri → NamespaceIndex`
3. 用新的 NamespaceIndex 重建 MonitoredItem 的 StartNodeId

```csharp
private async Task CreateSubscriptionAsync()
{
    var session = _session.GetSession()
        ?? throw new InvalidOperationException("Session not available");

    // 重连后必须重新获取命名空间表
    await session.FetchNamespaceTables();

    // 用最新的 NamespaceIndex 解析 NodeId
    foreach (var group in _config.NodeGroups)
    {
        ushort nsIndex = group.NamespaceUri is not null
            ? _session.GetNamespaceIndex(group.NamespaceUri)
            : (ushort)0;

        foreach (var node in group.Nodes)
        {
            var nodeId = ParseNodeId(node.NodeId, nsIndex);
            // 创建 MonitoredItem ...
        }
    }
}
```

---

## 4. 启动校验增强

原方案只有两条校验（NodeGroups 为空、NodeId 不存在），与 Modbus 补充文档相比差距较大。

### 4.1 OpcUaConfig 校验

```csharp
private static void ValidateOpcUaConfig(OpcUaConfig config)
{
    // Endpoint 格式
    if (string.IsNullOrWhiteSpace(config.Endpoint) ||
        !config.Endpoint.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"OpcUa.Endpoint must start with 'opc.tcp://', got '{config.Endpoint}'");
    }

    // SecurityPolicy 合法值
    var validPolicies = new[] { "None", "Basic128Rsa15", "Basic256", "Basic256Sha256" };
    if (!validPolicies.Contains(config.SecurityPolicy, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"OpcUa.SecurityPolicy must be one of [{string.Join("/", validPolicies)}], " +
            $"got '{config.SecurityPolicy}'");
    }

    // 时间参数范围
    if (config.SessionTimeoutMs <= 0)
        throw new InvalidOperationException("OpcUa.SessionTimeoutMs must be > 0");

    if (config.ReconnectIntervalMs <= 0)
        throw new InvalidOperationException("OpcUa.ReconnectIntervalMs must be > 0");

    if (config.PublishingIntervalMs <= 0)
        throw new InvalidOperationException("OpcUa.PublishingIntervalMs must be > 0");

    if (config.SamplingIntervalMs <= 0)
        throw new InvalidOperationException("OpcUa.SamplingIntervalMs must be > 0");
}
```

### 4.2 NodeGroups 校验

```csharp
private static void ValidateNodeGroups(List<NodeGroupConfig> groups)
{
    // NodeGroups 不为空
    if (groups.Count == 0)
        throw new InvalidOperationException("NodeGroups must not be empty");

    // NodeGroup.Name 唯一
    var duplicateNames = groups
        .Select(g => g.Name)
        .Where(n => !string.IsNullOrEmpty(n))
        .GroupBy(n => n)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .ToList();

    if (duplicateNames.Count > 0)
        throw new InvalidOperationException(
            $"NodeGroup names must be unique, duplicates: [{string.Join(", ", duplicateNames)}]");

    foreach (var group in groups)
    {
        // Group.Name 非空
        if (string.IsNullOrWhiteSpace(group.Name))
            throw new InvalidOperationException("NodeGroup.Name must not be empty");

        // Group.Nodes 非空
        if (group.Nodes.Count == 0)
            throw new InvalidOperationException(
                $"NodeGroup '{group.Name}' must have at least one node");

        // 同组内 NodeId 唯一
        var duplicateNodeIds = group.Nodes
            .Select(n => n.NodeId)
            .GroupBy(id => id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateNodeIds.Count > 0)
            throw new InvalidOperationException(
                $"NodeGroup '{group.Name}' has duplicate NodeIds: [{string.Join(", ", duplicateNodeIds)}]");

        // 同组内 Tag 唯一
        var duplicateTags = group.Nodes
            .Select(n => n.Tag)
            .GroupBy(t => t)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateTags.Count > 0)
            throw new InvalidOperationException(
                $"NodeGroup '{group.Name}' has duplicate Tags: [{string.Join(", ", duplicateTags)}]");

        foreach (var node in group.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
                throw new InvalidOperationException(
                    $"NodeGroup '{group.Name}' has a node with empty NodeId");

            if (string.IsNullOrWhiteSpace(node.Tag))
                throw new InvalidOperationException(
                    $"NodeGroup '{group.Name}' has a node with empty Tag (NodeId: {node.NodeId})");
        }
    }
}
```

### 4.3 全局 Tag 唯一性校验（可选）

跨 NodeGroup 的 Tag 重复不会导致运行时错误，但下游数据可能覆盖。建议 Warn 但不阻止启动：

```csharp
var allTags = groups.SelectMany(g => g.Nodes).Select(n => n.Tag).ToList();
var globalDuplicateTags = allTags.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key);
if (globalDuplicateTags.Any())
{
    _logger.LogWarning("Duplicate Tags across groups detected: [{Tags}]. " +
        "Downstream data may be overwritten", string.Join(", ", globalDuplicateTags));
}
```

---

## 5. 连接管理增强

### 5.1 NamespaceUri → NamespaceIndex 正确解析

原方案 10.2 节的代码不完整，`session.NamespaceUris.GetIndex()` 返回的是客户端本地索引，可能不等于服务器端的 NamespaceIndex。

**正确做法**：

```csharp
public ushort GetNamespaceIndex(string namespaceUri)
{
    var session = GetSession()
        ?? throw new InvalidOperationException("Session not available");

    // 从服务器端命名空间表查找
    for (ushort i = 0; i < session.NamespaceUris.Count; i++)
    {
        if (string.Equals(session.NamespaceUris.GetString(i), namespaceUri,
            StringComparison.OrdinalIgnoreCase))
        {
            return i;
        }
    }

    throw new InvalidOperationException(
        $"Namespace URI '{namespaceUri}' not found in server namespace table. " +
        $"Available: [{string.Join(", ", session.NamespaceUris.ToArray())}]");
}
```

**必须**在连接和重连后调用 `session.FetchNamespaceTables()` 同步服务器端的命名空间表：

```csharp
// ConnectAsync 中，Session 创建成功后
await session.FetchNamespaceTables();

// SessionRestored 事件处理中，重建订阅前
await session.FetchNamespaceTables();
```

### 5.2 NodeId 解析逻辑

原方案有两种 NodeId 格式（短名称 vs 完整字符串），需要明确的解析规则：

```csharp
private static NodeId ParseNodeId(string nodeIdString, ushort namespaceIndex)
{
    // 如果已经包含命名空间前缀（如 "ns=2;s=xxx"），直接解析
    if (nodeIdString.Contains('='))
    {
        return NodeId.Parse(nodeIdString);
    }

    // 短名称，使用组的 NamespaceUri 对应的索引
    return new NodeId(nodeIdString, namespaceIndex);
}
```

### 5.3 用户身份验证可配置

原方案 `ConnectAsync` 硬编码了 `AnonymousIdentityToken`。生产环境常需要用户名密码或证书认证。

```csharp
public class OpcUaConfig
{
    // ... 原有字段 ...

    /// <summary>身份验证方式：Anonymous / UserName / Certificate</summary>
    public string AuthMode { get; set; } = "Anonymous";

    /// <summary>用户名（AuthMode=UserName 时必填）</summary>
    public string? UserName { get; set; }

    /// <summary>密码（AuthMode=UserName 时必填）</summary>
    public string? Password { get; set; }
}
```

```csharp
private UserIdentity BuildIdentity()
{
    return _config.AuthMode?.ToLowerInvariant() switch
    {
        "username" when !string.IsNullOrEmpty(_config.UserName) =>
            new UserIdentity(_config.UserName, _config.Password ?? string.Empty),
        "certificate" =>
            // TODO: 证书认证，阶段6+实现
            throw new NotImplementedException("Certificate auth not yet implemented"),
        _ => new UserIdentity(new AnonymousIdentityToken())
    };
}
```

校验：

```csharp
if (string.Equals(config.AuthMode, "UserName", StringComparison.OrdinalIgnoreCase)
    && string.IsNullOrEmpty(config.UserName))
{
    throw new InvalidOperationException(
        "OpcUa.UserName is required when AuthMode is 'UserName'");
}
```

### 5.4 SecurityPolicy 字符串 → 运行时常量映射

`SecurityPolicy` 配置是字符串，需要映射到 SDK 的 `SecurityPolicies` 常量。映射失败必须明确报错。

```csharp
private static string MapSecurityPolicyUri(string securityPolicy)
{
    return securityPolicy.ToLowerInvariant() switch
    {
        "none" => SecurityPolicies.None,
        "basic128rsa15" => SecurityPolicies.Basic128Rsa15,
        "basic256" => SecurityPolicies.Basic256,
        "basic256sha256" => SecurityPolicies.Basic256Sha256,
        _ => throw new InvalidOperationException(
            $"Unknown SecurityPolicy '{securityPolicy}'. " +
            $"Supported: None, Basic128Rsa15, Basic256, Basic256Sha256")
    };
}
```

### 5.5 端点发现异常处理

原方案代码存在两个问题：

```csharp
// 问题1：服务器不可达时 GetEndpoints 可能抛异常，未捕获
var endpoints = DiscoveryClient.GetEndpoints(endpointUrl);

// 问题2：endpoints 为空时，endpoints[0] 会 IndexOutOfRange
return endpoints[0];
```

修正：

```csharp
private EndpointDescription DiscoverEndpoint(
    ApplicationConfiguration config, string endpointUrl, string securityPolicy)
{
    // 端点发现可能因网络问题失败
    EndpointDescription[] endpoints;
    try
    {
        endpoints = DiscoveryClient.GetEndpoints(endpointUrl);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Failed to discover endpoints at '{endpointUrl}'", ex);
    }

    if (endpoints == null || endpoints.Length == 0)
        throw new InvalidOperationException(
            $"No endpoints available at '{endpointUrl}'");

    // 按安全策略筛选
    var policyUri = MapSecurityPolicyUri(securityPolicy);
    var matched = endpoints.FirstOrDefault(e => e.SecurityPolicyUri == policyUri);

    if (matched != null)
        return matched;

    // 指定了安全策略但服务器不支持，不应静默回退
    _logger.LogWarning(
        "No endpoint matching SecurityPolicy '{Policy}' found. " +
        "Available: [{Available}]. Falling back to first endpoint",
        securityPolicy,
        string.Join(", ", endpoints.Select(e => e.SecurityPolicyUri)));

    return endpoints[0];
}
```

### 5.6 IsConnected 线程安全

与 Modbus 采集器相同，`IsConnected` 需保证多线程可见性：

```csharp
private volatile bool _isConnected;
public bool IsConnected => _isConnected;
```

状态变更在锁内完成，读取用 `volatile` 保证可见性。

### 5.7 KeepAlive 检测

原方案注册了 `KeepAlive` 事件但未说明检测逻辑。`KeepAlive` 回调应检查 `ServerState`，如果非 `Running` 则视为异常：

```csharp
private void OnKeepAlive(Session session, KeepAliveEventArgs e)
{
    if (e.CurrentState != ServerState.Running)
    {
        _logger.LogWarning(
            "OPC UA server state changed to {State}, triggering reconnect",
            e.CurrentState);

        // 触发重连逻辑（与 SessionClosing 相同的路径）
        TriggerReconnect();
    }
    else
    {
        // 重置退避计数
        _reconnectAttempt = 0;
    }
}
```

> 比"所有节点长时间无通知"更可靠的连接检测方式。KeepAlive 是 OPC UA 协议层面的心跳，比应用层超时更及时。

---

## 6. 通知回调线程安全

### 6.1 问题

OPC UA SDK 的 `MonitoredItem.Notification` 回调**在不同线程上触发**。多个通知并发到达时：

- 日志输出交错
- 如果 `ConsoleDataOutput` 用了非线程安全类型会崩溃
- 如果直接调 `output.WriteAsync()`，多个 DataPoint 并发写入

### 6.2 阶段1方案：ConcurrentQueue 批量收集

阶段1最简单的做法——用 `ConcurrentQueue` 收集通知，定时消费：

```csharp
private readonly ConcurrentQueue<DataPoint> _pendingPoints = new();
private Timer? _flushTimer;

// 通知回调中：只入队，不做任何 I/O
private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
{
    try
    {
        // ... 提取值，构建 DataPoint ...
        _pendingPoints.Enqueue(dataPoint);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Error processing notification for {NodeId}", item.StartNodeId);
    }
}

// 定时消费（如每 200ms 或每 PublishingInterval）
private void FlushPendingPoints(object? state)
{
    var points = new List<DataPoint>();
    while (_pendingPoints.TryDequeue(out var point))
        points.Add(point);

    if (points.Count > 0)
    {
        // 按 GroupName 分组后批量输出
        foreach (var group in points.GroupBy(p => p.Properties?.GetValueOrDefault("GroupName")))
        {
            await OutputBatchAsync(group.ToList(), CancellationToken.None);
        }
    }
}
```

### 6.3 阶段3+方案：Channel\<DataPoint\>

后续切 RabbitMQ 时，用 `Channel<DataPoint>` 做生产者-消费者缓冲：

```csharp
private readonly Channel<DataPoint> _channel = Channel.CreateBounded<DataPoint>(new BoundedChannelOptions(1000)
{
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = true
});

// 通知回调：生产者
_channel.Writer.TryWrite(dataPoint);

// 消费循环
await foreach (var point in _channel.Reader.ReadAllAsync(ct))
{
    await output.WriteAsync(point, ct);
}
```

> **现阶段**：阶段1用 `ConcurrentQueue` + Timer 足够，代码量小。阶段3切 `Channel` + 专用消费循环。

---

## 7. 批量输出适配推送模式

### 7.1 问题

OPC UA 是推送模式，通知逐条到达。但 `ConsoleDataOutput.WriteBatchAsync` 是为批量设计的，且预期输出格式为：

```
[simulator-001] sensors: temperature=85.3 pressure=2.2 ...
```

即**每个 NodeGroup 一行**，而非每个通知一行。

### 7.2 方案：收集 PublishingInterval 窗口内的通知

在 `OpcUaCollectorService` 中，用一个简单的窗口收集机制：

```csharp
// 在 FlushPendingPoints 中按 GroupName 分组输出
var grouped = points
    .GroupBy(p => p.Properties?.TryGetValue("GroupName", out var gn) == true ? gn.ToString() : null);

foreach (var group in grouped)
{
    var groupPoints = group.OrderBy(p => p.Tag).ToList();
    await OutputBatchAsync(groupPoints, CancellationToken.None);
}
```

这样 `ConsoleDataOutput.WriteBatchAsync` 每次收到一个 NodeGroup 的所有数据点，输出格式与 Modbus 一致。

---

## 8. 配置模型补充

### 8.1 OpcUaConfig 新增字段汇总

```csharp
public class OpcUaConfig
{
    /// <summary>OPC UA 服务器端点，如 "opc.tcp://localhost:4840"</summary>
    public string Endpoint { get; set; } = "opc.tcp://localhost:4840";

    /// <summary>安全策略：None / Basic128Rsa15 / Basic256 / Basic256Sha256</summary>
    public string SecurityPolicy { get; set; } = "None";

    /// <summary>会话超时（毫秒）</summary>
    public int SessionTimeoutMs { get; set; } = 60000;

    /// <summary>重连间隔（毫秒）</summary>
    public int ReconnectIntervalMs { get; set; } = 5000;

    /// <summary>自动接受不受信任的证书（开发环境用，生产环境应关闭）</summary>
    public bool AutoAcceptUntrustedCertificates { get; set; } = true;

    /// <summary>订阅发布间隔（毫秒）</summary>
    public int PublishingIntervalMs { get; set; } = 1000;

    /// <summary>采样间隔（毫秒）</summary>
    public int SamplingIntervalMs { get; set; } = 500;

    // --- 新增 ---

    /// <summary>身份验证方式：Anonymous / UserName / Certificate</summary>
    public string AuthMode { get; set; } = "Anonymous";

    /// <summary>用户名（AuthMode=UserName 时必填）</summary>
    public string? UserName { get; set; }

    /// <summary>密码（AuthMode=UserName 时必填）</summary>
    public string? Password { get; set; }

    /// <summary>每个 MonitoredItem 的队列大小，默认 10</summary>
    public uint QueueSize { get; set; } = 10;

    /// <summary>通知刷新间隔（毫秒），控制控制台输出频率</summary>
    public int FlushIntervalMs { get; set; } = 200;
}
```

### 8.2 NodeConfig 新增字段（写操作伏笔）

```csharp
public class NodeConfig
{
    /// <summary>OPC UA 节点标识</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>数据标签名，用于 DataPoint.Tag</summary>
    public string Tag { get; set; } = string.Empty;

    // --- 新增（阶段6+启用） ---

    /// <summary>是否可写（用于双向通信场景）</summary>
    public bool Writable { get; set; } = false;
}
```

### 8.3 appsettings.json 补充

```json
{
  "Collector": {
    "DeviceId": "simulator-001",
    "TenantId": "default",

    "OpcUa": {
      "Endpoint": "opc.tcp://localhost:4840",
      "SecurityPolicy": "None",
      "SessionTimeoutMs": 60000,
      "ReconnectIntervalMs": 5000,
      "AutoAcceptUntrustedCertificates": true,
      "PublishingIntervalMs": 1000,
      "SamplingIntervalMs": 500,
      "AuthMode": "Anonymous",
      "QueueSize": 10,
      "FlushIntervalMs": 200
    },

    "NodeGroups": [ ... ]
  }
}
```

---

## 9. 优雅关机

### 9.1 关机顺序

必须**先删除 Subscription，再断开 Session**。否则 Session 断开后无法发送 DeleteSubscription 请求，服务器端残留订阅会占用资源。

```csharp
protected override async Task ExecuteAsync(CancellationToken ct)
{
    // ... 主逻辑 ...

    // 关机时
    ct.ThrowIfCancellationRequested();

    // 1. 删除订阅（在 Session 存活时）
    if (_subscription != null)
    {
        try { await _subscription.DeleteAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete subscription during shutdown"); }
    }

    // 2. 断开 Session
    await _session.DisconnectAsync();

    _logger.LogInformation("Collector stopped - Device: {DeviceId}", _config.DeviceId);
}
```

### 9.2 OpcUaSession.Dispose 顺序保障

```csharp
public void Dispose()
{
    // 1. 停止重连循环
    _reconnectCts?.Cancel();

    // 2. 删除订阅（如果有残留）
    _subscription?.Delete();

    // 3. 关闭 Session
    _session?.Close();

    // 4. 清理事件
    _session.KeepAlive -= OnKeepAlive;
    _session.SessionClosing -= OnSessionClosing;
}
```

DI 的 Dispose 顺序：`OpcUaCollectorService`（HostedService）先停 → `OpcUaSession`（Singleton）后释放。这是正确的。

---

## 10. 依赖注入调整

### 10.1 配置校验注入

```csharp
// Program.cs
builder.Services.PostConfigure<CollectorConfig>(config =>
{
    ConfigValidator.Validate(config); // 不合法直接抛异常
});
```

```csharp
public static class ConfigValidator
{
    public static void Validate(CollectorConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DeviceId))
            throw new InvalidOperationException("DeviceId must not be empty");

        ValidateOpcUaConfig(config.OpcUa);
        ValidateNodeGroups(config.NodeGroups);
    }

    // ... 各子校验方法见上文 ...
}
```

### 10.2 OpcUaSession 注入方式

`OpcUaSession` 需要配置和日志，通过 DI 注入：

```csharp
builder.Services.AddSingleton<OpcUaSession>();
```

`OpcUaSession` 构造函数：

```csharp
public class OpcUaSession : IDisposable
{
    public OpcUaSession(
        IOptions<CollectorConfig> config,
        ILogger<OpcUaSession> logger)
    {
        _config = config.Value.OpcUa;
        _logger = logger;
    }
}
```

---

## 11. 错误处理策略补充

| 场景 | 处理 |
|------|------|
| DataPoint 缺少 Quality/ValueType | 补充字段，与 Modbus 对齐 |
| MonitoredItem → Tag 映射缺失 | 用 MonitoredItem.Handle 关联 NodeConfig |
| 重建订阅前未清理旧 Subscription | 先 DeleteAsync 旧订阅再创建新订阅 |
| 重建过程中又断线 | try-catch 兜底，等下次 SessionRestored |
| 重连后 NamespaceIndex 变化 | FetchNamespaceTables + 重新解析 NodeId |
| Endpoint 不以 opc.tcp:// 开头 | 启动校验，抛异常 |
| SecurityPolicy 不合法 | 启动校验，抛异常 |
| 时间参数 ≤ 0 | 启动校验，抛异常 |
| NodeGroup.Name 重复 | 启动校验，抛异常 |
| 同组 NodeId 重复 | 启动校验，抛异常 |
| 同组 Tag 重复 | 启动校验，抛异常 |
| NodeId/Tag 为空 | 启动校验，抛异常 |
| 用户身份验证不可配置 | AuthMode/UserName/Password 字段 |
| SecurityPolicy 映射失败 | 抛明确异常，不静默回退 |
| 端点发现返回空列表 | 抛明确异常 |
| 通知回调线程安全 | ConcurrentQueue + Timer 批量输出 |
| 关机顺序 | 先删订阅再断 Session |
| KeepAlive 检测 | ServerState 非 Running 时触发重连 |

---

## 12. 其他建议

### 12.1 单设备 vs 多设备

与 Modbus 相同，当前配置只支持单个 OPC UA 端点。后续多设备时需重构为 `List<OpcUaDeviceConfig>`。

> **现阶段**：单设备足够，代码中留 TODO。

### 12.2 健康检查端点

作为微服务，应暴露健康检查端点：

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<OpcUaHealthCheck>("opcua", tags: ["ready"]);

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
```

```csharp
public class OpcUaHealthCheck(OpcUaSession session) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(session.IsConnected
            ? HealthCheckResult.Healthy("OPC UA session connected")
            : HealthCheckResult.Unhealthy("OPC UA session disconnected"));
}
```

> **现阶段**：阶段1不需要，阶段5容器化时加上。

### 12.3 OPC UA SDK 版本兼容性

方案用 `OPCFoundation.NetStandard.Opc.Ua` 1.5.378.134，这是 .NET Standard 版本。与 .NET 10 的兼容性需要实际验证，尤其是 `ApplicationConfiguration` 的初始化方式在新版 SDK 中可能有变化。

**建议**：实现前先做一个最小 POC（创建 Session → 创建 Subscription → 接收一条通知），验证 SDK 在 .NET 10 下的行为。

### 12.4 OPC UA DateTime 序列化

OPC UA 的 `SourceTimestamp` 和 `ServerTimestamp` 都是 UTC `DateTime`。`DataPoint.Timestamp` 应统一用 UTC，序列化时用 ISO 8601 格式（`yyyy-MM-ddTHH:mm:ss.fffZ`）。

`ServerTimestamp` 存入 `Properties`：

```csharp
if (notification.Value?.ServerTimestamp != null)
{
    dataPoint.Properties ??= new Dictionary<string, object>();
    dataPoint.Properties["ServerTimestamp"] = notification.Value.ServerTimestamp.Value;
}
```

### 12.5 数据点变化检测 / Deadband

OPC UA 订阅天然支持 Deadband 过滤（只有值变化超过阈值才推送）。对于高频变化但微小波动的节点，可在 `NodeConfig` 中配置：

```csharp
public class NodeConfig
{
    // ... 原有字段 ...

    /// <summary>死区过滤阈值（0.0-100.0%），0 = 禁用（默认）</summary>
    public double DeadbandPercent { get; set; } = 0.0;
}
```

创建 MonitoredItem 时：

```csharp
if (node.DeadbandPercent > 0)
{
    item.DeadbandType = (uint)DeadbandType.Percent;
    item.DeadbandValue = node.DeadbandPercent;
}
```

> **现阶段**：不急，后续数据量大时启用。

---

## 13. 优先级建议

| 优先级 | 条目 | 理由 |
|--------|------|------|
| 🔴 P0 | DataPoint 补 Quality + ValueType | 和 Modbus 不一致，阶段3序列化大坑 |
| 🔴 P0 | MonitoredItem.Handle 映射机制 | 不明确则实现必然出错 |
| 🔴 P0 | 重建订阅前清理旧 Subscription | 重连后重复通知 |
| 🔴 P0 | 重建中又断线的 try-catch | 可能导致服务异常退出 |
| 🔴 P0 | 重连后 FetchNamespaceTables + 重解析 NodeId | 命名空间变化后节点监控失败 |
| 🟡 P1 | 配置校验（Endpoint/Policy/时间参数/唯一性/非空） | 错误配置运行时难以排查 |
| 🟡 P1 | 用户身份验证可配置 | 生产环境必需 |
| 🟡 P1 | SecurityPolicy 映射错误处理 | 静默回退不安全 |
| 🟡 P1 | 端点发现空列表/异常处理 | 服务器不可达时崩溃 |
| 🟡 P1 | 通知回调线程安全 | 多通知并发输出交错 |
| 🟡 P1 | volatile IsConnected | 多线程可见性 |
| 🟡 P1 | ValueType 命名映射统一 | 与 Modbus 下游兼容 |
| 🟢 P2 | 批量输出适配推送模式 | 控制台输出格式一致性 |
| 🟢 P2 | QueueSize 可配置 | 不同场景需求不同 |
| 🟢 P2 | KeepAlive 检测 | 比通知超时更可靠的连接检测 |
| 🟢 P2 | 优雅关机先删订阅再断会话 | 服务器端残留资源 |
| 🟢 P2 | DataQuality 枚举与 Modbus 统一 | Abstractions 抽取时需统一 |
| 🟢 P2 | ServerTimestamp 存入 Properties | 调试/审计需要 |
| ⚪ P3 | 多设备支持 | 阶段1单设备够用 |
| ⚪ P3 | 健康检查端点 | 阶段5容器化时加 |
| ⚪ P3 | SDK .NET 10 兼容性 POC | 实现前验证 |
| ⚪ P3 | Deadband 配置 | 数据量大时再考虑 |
| ⚪ P3 | NodeConfig.Writable 写操作伏笔 | 阶段6+启用 |
