# AmGatewayCloud.Collector.OpcUa — 完整方案

## 1. 定位

独立微服务，通过 OPC UA 协议从设备采集数据，输出为统一的 `DataPoint` 格式。

与 Modbus 采集器的核心区别：OPC UA 采用**订阅/监控模式**（服务器推送变化），而非轮询模式。OPC UA 返回**工程值**，无需 ScaleFactor 转换。

## 2. 架构

```
AmVirtualSlave (:4841)                    Collector.OpcUa
┌──────────────────────────┐           ┌──────────────────────────────────────┐
│                          │           │                                      │
│  Industrial/             │           │  OpcUaSession                        │
│  ├── Sensors/            │  Subscription  ├── 会话管理 + 自动重连           │
│  │   ├── Temperature    │  ────────►  │   ├── 端点发现 + 安全策略          │
│  │   ├── Pressure       │  MonitoredItem                                  │
│  │   └── ...            │  Notification │   └── MonitoredItem 通知回调      │
│  ├── Parameters/         │           │         │                           │
│  ├── Statistics/         │           │         ▼                           │
│  └── Alarms/            │           │  OpcUaCollectorService               │
│                          │           │    ├── BackgroundService 托管        │
│  StandardServer          │           │    ├── 创建订阅 + 监控节点           │
│  ClearChangeMasks()      │           │    ├── MonitoredItemNotification     │
│  → 自动推送到客户端       │           │    │   → DataPoint 转换              │
│                          │           │    └── 调用 IDataOutput 输出          │
│                          │           │         │                           │
│                          │           │         ▼                           │
│                          │           │  IDataOutput                         │
│                          │           │    ├── ConsoleDataOutput  (阶段1)   │
│                          │           │    └── RabbitMqOutput    (阶段3a)   │
└──────────────────────────┘           └──────────────────────────────────────┘
```

### 与 Modbus 采集器对比

| 方面 | Modbus | OPC UA |
|------|--------|--------|
| 数据获取 | 轮询（客户端主动读） | 订阅（服务器推送变化） |
| 数据转换 | 原始值 ÷ ScaleFactor | 工程值，无需转换 |
| 连接模型 | TCP 连接 | Session + Subscription |
| 断线恢复 | 重连后重新读取 | 重连后重建订阅 |
| 安全 | 无 | 可选加密 + 证书 + 身份验证 |
| 配置复杂度 | 寄存器地址 + 类型 | 节点 ID（字符串标识） |

## 3. 数据模型

### 3.1 DataPoint — 复用统一输出模型

与 Modbus 采集器共用相同的 `DataPoint` 格式，下游服务无需关心数据来源。

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

    /// <summary>采集时间（UTC），优先使用 OPC UA SourceTimestamp</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>多租户伏笔，阶段6启用</summary>
    public string? TenantId { get; set; }

    /// <summary>扩展属性（如节点ID、StatusCode、ServerTimestamp）</summary>
    public Dictionary<string, object>? Properties { get; set; }
}
```

### 3.2 OPC UA 值类型映射

OPC UA 返回的是强类型工程值，无需 ScaleFactor：

| OPC UA DataType | .NET 类型 | DataPoint.Value 类型 |
|-----------------|----------|---------------------|
| Double | `double` | `double` |
| Float | `float` | `float` |
| Int32 | `int` | `int` |
| UInt16 | `ushort` | `ushort` |
| Boolean | `bool` | `bool` |
| String | `string` | `string` |

### 3.3 OPC UA StatusCode → 数据质量

```csharp
public enum DataQuality
{
    Good,       // StatusCode.Good
    Uncertain,  // StatusCode.Uncertain*
    Bad         // StatusCode.Bad*
}
```

映射逻辑参考 AmGateway 的 `MapStatusCode`：

```csharp
private static DataQuality MapStatusCode(StatusCode statusCode)
{
    if (StatusCode.IsGood(statusCode.Code)) return DataQuality.Good;
    if (StatusCode.IsUncertain(statusCode.Code)) return DataQuality.Uncertain;
    return DataQuality.Bad;
}
```

## 4. 配置模型

### 4.1 appsettings.json

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
      "SamplingIntervalMs": 500
    },

    "NodeGroups": [
      {
        "Name": "sensors",
        "NamespaceUri": "http://amvirtualslave.org/Industrial",
        "Nodes": [
          { "NodeId": "Sensors_Temperature", "Tag": "temperature" },
          { "NodeId": "Sensors_Pressure", "Tag": "pressure" },
          { "NodeId": "Sensors_FlowRate", "Tag": "flowRate" },
          { "NodeId": "Sensors_Level", "Tag": "liquidLevel" },
          { "NodeId": "Sensors_Humidity", "Tag": "humidity" },
          { "NodeId": "Sensors_Rpm", "Tag": "rpm" },
          { "NodeId": "Sensors_Voltage", "Tag": "voltage" },
          { "NodeId": "Sensors_Current", "Tag": "current" },
          { "NodeId": "Sensors_Power", "Tag": "power" },
          { "NodeId": "Sensors_Frequency", "Tag": "frequency" }
        ]
      },
      {
        "Name": "parameters",
        "NamespaceUri": "http://amvirtualslave.org/Industrial",
        "Nodes": [
          { "NodeId": "Parameters_Running", "Tag": "running" },
          { "NodeId": "Parameters_Mode", "Tag": "mode" },
          { "NodeId": "Parameters_NoiseMultiplier", "Tag": "noiseMultiplier" },
          { "NodeId": "Parameters_ResponseDelayMs", "Tag": "responseDelayMs" },
          { "NodeId": "Parameters_SamplePeriod", "Tag": "samplePeriod" },
          { "NodeId": "Parameters_AlarmHighLimit", "Tag": "alarmHighLimit" },
          { "NodeId": "Parameters_AlarmLowLimit", "Tag": "alarmLowLimit" },
          { "NodeId": "Parameters_PowerFactor", "Tag": "powerFactor" }
        ]
      },
      {
        "Name": "statistics",
        "NamespaceUri": "http://amvirtualslave.org/Industrial",
        "Nodes": [
          { "NodeId": "Statistics_TempMax", "Tag": "tempMax" },
          { "NodeId": "Statistics_TempMin", "Tag": "tempMin" },
          { "NodeId": "Statistics_TempAvg", "Tag": "tempAvg" },
          { "NodeId": "Statistics_PressMax", "Tag": "pressMax" },
          { "NodeId": "Statistics_PressMin", "Tag": "pressMin" },
          { "NodeId": "Statistics_PressAvg", "Tag": "pressAvg" },
          { "NodeId": "Statistics_RunHours", "Tag": "runHours" },
          { "NodeId": "Statistics_StartCount", "Tag": "startCount" },
          { "NodeId": "Statistics_CommCount", "Tag": "commCount" },
          { "NodeId": "Statistics_ErrorCount", "Tag": "errorCount" }
        ]
      },
      {
        "Name": "alarms",
        "NamespaceUri": "http://amvirtualslave.org/Industrial",
        "Nodes": [
          { "NodeId": "Alarms_AlarmEnableMask", "Tag": "alarmEnableMask" },
          { "NodeId": "Alarms_AlarmStatusMask", "Tag": "alarmStatusMask" },
          { "NodeId": "Alarms_FaultCode1", "Tag": "faultCode1" },
          { "NodeId": "Alarms_FaultCode2", "Tag": "faultCode2" },
          { "NodeId": "Alarms_FaultCode3", "Tag": "faultCode3" },
          { "NodeId": "Alarms_FaultCode4", "Tag": "faultCode4" },
          { "NodeId": "Alarms_DiRunning", "Tag": "diRunning" },
          { "NodeId": "Alarms_DiAlarm", "Tag": "diAlarm" },
          { "NodeId": "Alarms_DiTempHigh", "Tag": "diTempHigh" },
          { "NodeId": "Alarms_DiTempLow", "Tag": "diTempLow" },
          { "NodeId": "Alarms_DiCommOk", "Tag": "diCommOk" },
          { "NodeId": "Alarms_DiReady", "Tag": "diReady" }
        ]
      }
    ]
  }
}
```

### 4.2 配置映射类

```csharp
namespace AmGatewayCloud.Collector.OpcUa.Configuration;

public class CollectorConfig
{
    public string DeviceId { get; set; } = "device-001";
    public string? TenantId { get; set; }
    public OpcUaConfig OpcUa { get; set; } = new();
    public List<NodeGroupConfig> NodeGroups { get; set; } = [];
}

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

    /// <summary>订阅发布间隔（毫秒），服务器向客户端推送变化的最小周期</summary>
    public int PublishingIntervalMs { get; set; } = 1000;

    /// <summary>采样间隔（毫秒），服务器对节点采样的最小周期</summary>
    public int SamplingIntervalMs { get; set; } = 500;
}

public class NodeGroupConfig
{
    /// <summary>节点组名称，用于日志和输出分组</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// OPC UA 命名空间 URI，如 "http://amvirtualslave.org/Industrial"。
    /// 用于将 NodeId 字符串解析为带命名空间的 NodeId。
    /// 如果为空，则 NodeId 中必须包含命名空间索引（如 "ns=2;s=xxx"）。
    /// </summary>
    public string? NamespaceUri { get; set; }

    /// <summary>监控节点列表</summary>
    public List<NodeConfig> Nodes { get; set; } = [];
}

public class NodeConfig
{
    /// <summary>
    /// OPC UA 节点标识。
    /// 如果 NodeGroupConfig.NamespaceUri 已设置，此处填短名称（如 "Sensors_Temperature"）；
    /// 否则需填完整 NodeId 字符串（如 "ns=2;s=Sensors_Temperature"）。
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>数据标签名，用于 DataPoint.Tag</summary>
    public string Tag { get; set; } = string.Empty;
}
```

## 5. 组件设计

### 5.1 OpcUaSession — 会话管理 + 自动重连

**职责**：建立/维护 OPC UA Session，断线自动重连，重连后通知上层重建订阅。

```
状态机：

  Disconnected ──ConnectAsync()──► Connected ──SessionClosed──► Reconnecting
       ▲                            │  │                         │
       │                            │  │                         │
       └────────────────────────────┘  └───── retry ───────────┘
                                  订阅正常，保持 Connected
```

**关键行为**：
- 首次启动连接（端点发现 → 安全协商 → 创建 Session）
- Session 断开后自动进入重连循环（指数退避：5s → 10s → 20s → 40s → 60s 上限）
- **重连成功后触发 `SessionRestored` 事件**，通知 `OpcUaCollectorService` 重建订阅
- 连接/断开/重连均输出结构化日志
- 线程安全：Session 操作加锁，防止并发访问

```csharp
namespace AmGatewayCloud.Collector.OpcUa;

public class OpcUaSession : IDisposable
{
    public bool IsConnected { get; }
    public event EventHandler? SessionRestored;

    /// <summary>建立连接并创建 Session</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>获取当前 Session（可能为 null）</summary>
    Session? GetSession();

    /// <summary>获取命名空间索引（连接后可用）</summary>
    ushort GetNamespaceIndex(string namespaceUri);

    /// <summary>主动断开</summary>
    Task DisconnectAsync();
}
```

**与 AmGateway OpcUaDriver 的关键差异**：

| 方面 | AmGateway OpcUaDriver | Collector.OpcUa OpcUaSession |
|------|----------------------|------------------------------|
| 断线重连 | ❌ 无，Session 断开后永久失效 | ✅ 自动重连 + 事件通知 |
| 线程安全 | ❌ SDK 回调跨线程访问 | ✅ 锁保护 + 事件通知 |
| 安全策略 | ⚠️ 配置了但未使用 | ✅ 实际生效 |
| 超时配置 | 硬编码 60s | ✅ 可配置 |

### 5.2 OpcUaCollectorService — 采集主服务

**职责**：BackgroundService，创建订阅 + 监控项，接收通知回调，转换为 DataPoint 输出。

```
生命周期：

  ┌─────────────────────────────────────────────┐
  │ ExecuteAsync(ct)                             │
  │   1. await session.ConnectAsync(ct)          │
  │   2. await CreateSubscriptionAsync()         │
  │   3. 注册 session.SessionRestored 事件       │
  │   4. await TaskCompletionSource (等待取消)    │
  │                                              │
  │ SessionRestored 事件触发时：                   │
  │   → await CreateSubscriptionAsync()          │
  │   （重建订阅，不需要重建 Session）             │
  └─────────────────────────────────────────────┘
```

**订阅创建流程**：

```
CreateSubscriptionAsync()
  │
  ├── 创建 Subscription
  │     PublishingInterval = config.PublishingIntervalMs
  │
  ├── 遍历 NodeGroups
  │     └── 遍历 Nodes
  │           └── 创建 MonitoredItem
  │                 StartNodeId = 解析 NodeId（含命名空间）
  │                 SamplingInterval = config.SamplingIntervalMs
  │                 QueueSize = 10
  │                 Notification += OnNotification
  │
  ├── session.AddSubscription(subscription)
  ├── await subscription.CreateAsync()
  └── await subscription.ApplyChangesAsync()
```

**通知回调处理**：

```
OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
  │
  ├── 从 Notification 提取值
  │     variantValue = notification.Value?.Value
  │     sourceTimestamp = notification.Value?.SourceTimestamp ?? DateTime.UtcNow
  │     statusCode = notification.Value?.StatusCode ?? StatusCodes.Good
  │
  ├── MapStatusCode(statusCode) → DataQuality
  │
  ├── 查找 Tag（item → NodeConfig 映射）
  │
  ├── 构建 DataPoint
  │     DeviceId, Tag, Value, Timestamp=sourceTimestamp, Quality
  │
  └── output.WriteAsync(dataPoint, ct)
```

**关键行为**：
- 启动时等待 Session 就绪再创建订阅
- Session 断线后，`OpcUaSession` 自动重连
- 重连成功触发 `SessionRestored`，服务重建订阅
- MonitoredItem 通知回调中，用 `Channel<DataPoint>` 或直接同步输出（阶段1同步即可）
- 单个节点通知异常不影响其他节点
- 优雅关机：CancellationToken 触发后删除订阅、断开会话

```csharp
namespace AmGatewayCloud.Collector.OpcUa;

public class OpcUaCollectorService : BackgroundService
{
    public OpcUaCollectorService(
        OpcUaSession session,
        IOptions<CollectorConfig> config,
        IEnumerable<IDataOutput> outputs,
        ILogger<OpcUaCollectorService> logger);

    protected override Task ExecuteAsync(CancellationToken ct);
}
```

### 5.3 IDataOutput — 输出抽象（同 Modbus 采集器）

```csharp
namespace AmGatewayCloud.Collector.OpcUa.Output;

public interface IDataOutput
{
    /// <summary>输出单条数据</summary>
    Task WriteAsync(DataPoint point, CancellationToken ct);

    /// <summary>批量输出</summary>
    Task WriteBatchAsync(IEnumerable<DataPoint> points, CancellationToken ct);
}
```

### 5.4 ConsoleDataOutput — 阶段1实现

```csharp
namespace AmGatewayCloud.Collector.OpcUa.Output;

public class ConsoleDataOutput : IDataOutput
{
    // WriteAsync: 格式化单条输出
    // WriteBatchAsync: 一行汇总输出，如：
    //   [20:35:03 INF] [simulator-001] sensors: temperature=85.3 pressure=2.2 flowRate=120.5 ...
    //
    // 与 Modbus 版的区别：
    // - 值直接显示，无需 ScaleFactor 转换
    // - 布尔型显示 true/false
    // - 数值型保留合理精度（double 1位小数，int 整数，float 2位小数）
}
```

## 6. 依赖注入 (Program.cs)

```csharp
var builder = Host.CreateApplicationBuilder(args);

// 配置
builder.Services.Configure<CollectorConfig>(
    builder.Configuration.GetSection("Collector"));

// 核心服务
builder.Services.AddSingleton<OpcUaSession>();
builder.Services.AddSingleton<IDataOutput, ConsoleDataOutput>();

// BackgroundService
builder.Services.AddHostedService<OpcUaCollectorService>();

// 日志
builder.Services.AddSerilog(static configure =>
{
    configure.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
});

var host = builder.Build();
await host.RunAsync();
```

## 7. 错误处理策略

| 场景 | 处理 |
|------|------|
| 首次连接失败 | 循环重试，指数退避（5s→10s→20s→40s→60s） |
| 运行中 Session 断开 | OpcUaSession 自动重连，触发 SessionRestored 事件 |
| 重连成功 | OpcUaCollectorService 重建 Subscription + MonitoredItems |
| 单个节点通知异常 | catch + Warn 日志，跳过该节点，其他节点不受影响 |
| 所有节点长时间无通知 | 可选：超时检测 + Warn 日志（非阻塞） |
| 端点发现失败 | 重试，日志记录可用端点列表 |
| 证书验证失败 | 开发环境 AutoAccept，生产环境应配置受信证书 |
| CancellationToken 取消 | 删除订阅 → 断开 Session → 退出 |
| NodeId 不存在 | 创建 MonitoredItem 时 BadNodeId，Warn 日志 + 跳过该节点 |
| NodeGroups 为空 | 启动校验，抛异常阻止启动 |

## 8. 项目文件

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="OPCFoundation.NetStandard.Opc.Ua" Version="1.5.378.134" />
    <PackageReference Include="OPCFoundation.NetStandard.Opc.Ua.Client" Version="1.5.378.134" />
    <PackageReference Include="Serilog.AspNetCore" Version="8.*" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.*" />
  </ItemGroup>
</Project>
```

> 依赖版本与 AmGateway.Driver.OpcUa 对齐。

## 9. 项目结构

```
src/AmGatewayCloud.Collector.OpcUa/
├── AmGatewayCloud.Collector.OpcUa.csproj
├── Program.cs                              # 入口 + DI
├── appsettings.json                        # 默认配置
├── OpcUaSession.cs                         # 会话管理 + 自动重连
├── OpcUaCollectorService.cs                # BackgroundService 采集主服务
├── Configuration/
│   ├── CollectorConfig.cs                  # 配置映射类（OpcUaConfig + NodeGroupConfig + NodeConfig）
│   └── DataQuality.cs                      # 数据质量枚举
├── Models/
│   └── DataPoint.cs                        # 统一数据模型
└── Output/
    ├── IDataOutput.cs                      # 输出抽象接口
    └── ConsoleDataOutput.cs                # 阶段1：控制台输出
```

## 10. OPC UA 会话建立详细流程

```csharp
async Task ConnectAsync(CancellationToken ct)
{
    // 1. 构建 ApplicationConfiguration（Client 模式）
    var config = new ApplicationConfiguration
    {
        ApplicationName = "AmGatewayCloud.Collector.OpcUa",
        ApplicationUri = $"urn:{Environment.MachineName}:AmGatewayCloud.Collector.OpcUa",
        ApplicationType = ApplicationType.Client,
        SecurityConfiguration = new SecurityConfiguration
        {
            AutoAcceptUntrustedCertificates = _config.AutoAcceptUntrustedCertificates,
            // ... 证书存储配置
        },
        TransportQuotas = new TransportQuotas { OperationTimeout = 15000 }
    };

    // 2. 加载/验证配置
    await config.Validate(ApplicationType.Client);

    // 3. 端点发现
    var endpointDescription = DiscoverEndpoint(config, _config.Endpoint, _config.SecurityPolicy);

    // 4. 构建 ConfiguredEndpoint
    var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(config));

    // 5. 创建 Session
    _session = await Session.Create(
        config,
        endpoint,
        updateBeforeConnect: false,
        sessionName: "AmGatewayCloud.Collector.OpcUa",
        sessionTimeout: _config.SessionTimeoutMs,
        identity: new UserIdentity(new AnonymousIdentityToken()),
        preferredLocales: null
    );

    // 6. 注册 KeepAlive / SessionClosed 事件
    _session.KeepAlive += OnKeepAlive;
    _session.SessionClosing += OnSessionClosing;
}
```

### 10.1 端点发现

```csharp
EndpointDescription DiscoverEndpoint(ApplicationConfiguration config, string endpointUrl, string securityPolicy)
{
    // 使用 DiscoveryClient 获取服务器可用端点
    var endpoints = DiscoveryClient.GetEndpoints(endpointUrl);

    // 按安全策略筛选
    if (securityPolicy.Equals("None", StringComparison.OrdinalIgnoreCase))
    {
        var noneEndpoint = endpoints.FirstOrDefault(e =>
            e.SecurityPolicyUri == SecurityPolicies.None);
        if (noneEndpoint != null) return noneEndpoint;
    }

    // 回退到第一个可用端点
    return endpoints[0];
}
```

### 10.2 NamespaceUri → NamespaceIndex 解析

```csharp
ushort GetNamespaceIndex(string namespaceUri)
{
    var session = GetSession() ?? throw new InvalidOperationException("Session not available");
    var nsIndex = session.NamespaceUris.GetIndex(namespaceUri);
    // NamespaceUris 索引可能不等于实际的 NamespaceIndex
    // 需要从 session.FetchNamespaceTables() 获取服务器的命名空间表
    return (ushort)nsIndex;
}
```

> **注意**：OPC UA 服务器的 NamespaceIndex 在不同连接中可能变化。正确做法是连接后查询服务器的命名空间表，而非硬编码 `ns=2`。`NamespaceUri` 配置就是为了支持动态解析。

## 11. 运行验证

### 前置条件

- AmVirtualSlave 运行，且 `OpcUa.Enabled = true`，端口 4840

### 启动

```powershell
cd src/AmGatewayCloud.Collector.OpcUa
dotnet run
```

### 预期输出

```
[20:35:01 INF] Collector starting - Device: simulator-001, Endpoint: opc.tcp://localhost:4840
[20:35:01 INF] Discovering endpoints at opc.tcp://localhost:4840
[20:35:01 INF] Connected to OPC UA server opc.tcp://localhost:4840 (SecurityPolicy: None)
[20:35:01 INF] Monitoring 40 nodes in 4 groups (PublishingInterval: 1000ms, SamplingInterval: 500ms)
[20:35:02 INF] [simulator-001] sensors: temperature=85.3 pressure=2.2 flowRate=120.5 liquidLevel=50.1 humidity=62.8 rpm=1485 voltage=380.2 current=5.3 power=2010.0 frequency=50.0
[20:35:02 INF] [simulator-001] parameters: running=true mode=1 noiseMultiplier=0.05 responseDelayMs=0 samplePeriod=2000 alarmHighLimit=95.0 alarmLowLimit=10.0 powerFactor=0.85
[20:35:02 INF] [simulator-001] statistics: tempMax=92.1 tempMin=78.5 tempAvg=85.3 pressMax=3.5 pressMin=1.0 pressAvg=2.2 runHours=1234 startCount=56 commCount=7890 errorCount=12
[20:35:02 INF] [simulator-001] alarms: alarmEnableMask=1 alarmStatusMask=0 faultCode1=0 faultCode2=0 faultCode3=0 faultCode4=0 diRunning=true diAlarm=false diTempHigh=false diTempLow=false diCommOk=true diReady=true
[20:35:03 INF] [simulator-001] sensors: temperature=86.1 pressure=2.1 flowRate=119.8 ...
...
```

### 断线恢复验证

1. 停掉 AmVirtualSlave
2. 预期看到 `[WRN] Session closed, attempting reconnect in 5000ms`
3. 预期看到 `[INF] Reconnecting...`（间隔递增：5s→10s→20s...）
4. 重启 AmVirtualSlave
5. 预期看到 `[INF] Reconnected to OPC UA server`
6. 预期看到 `[INF] Rebuilding subscription with 40 monitored items`
7. 数据恢复输出

## 12. 与 Modbus 采集器的代码复用

当前阶段1两个采集器各自独立，但有明显的共同点：

| 可复用部分 | 当前状态 | 后续计划 |
|-----------|---------|---------|
| `DataPoint` 模型 | 各自独立定义 | 抽取到 `Collector.Abstractions` |
| `IDataOutput` 接口 | 各自独立定义 | 抽取到 `Collector.Abstractions` |
| `ConsoleDataOutput` | 各自独立实现 | 抽取到 `Collector.Abstractions` |
| `DataQuality` 枚举 | 各自独立定义 | 抽取到 `Collector.Abstractions` |
| DI 注册模式 | 相似 | 可用扩展方法封装 |

> **现阶段**：先独立实现，跑通闭环。阶段3（RabbitMQ 输出）之前完成 Abstractions 抽取。

## 13. 后续演进

| 阶段 | 变化 |
|------|------|
| 阶段3a | 新增 `RabbitMqOutput : IDataOutput`，Collector 不改 |
| 通用化 | 抽取 `AmGatewayCloud.Collector.Abstractions`（DataPoint + IDataOutput + DataQuality），两个 Collector 共享 |
| 阶段5 | 容器化，Serilog → Seq，加入 OpenTelemetry |
| 阶段6 | 配置中心下发 TenantId，Collector 动态加载 |
| 安全增强 | 支持证书加密 + 用户名密码身份验证 |
| 写操作 | 支持通过 OPC UA Method / 写入节点控制设备（双向通信） |
