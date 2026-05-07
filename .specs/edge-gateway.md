# AmGatewayCloud.EdgeGateway — 阶段2 完整方案

## 1. 定位

独立微服务，部署在车间边缘节点（工控机/边缘网关），负责：
- 订阅局域网 MQTT，接收所有采集器数据
- 本地 InfluxDB 持久化（断网缓存）
- 转发到工厂级 RabbitMQ（云端聚合）
- 断网检测 + 恢复后回放

---

## 2. 架构

```
边缘侧局域网                                    WAN / 工厂骨干网
┌─────────────────────────────────┐           ┌─────────────────────────┐
│  采集器 × N                     │           │                         │
│  (Modbus/OpcUa/...)             │           │   RabbitMQ (一厂一个)    │
│       │ MQTT                    │           │   amgateway.topic        │
│       ▼                         │           │                         │
│  ┌─────────────────────────┐    │           │   ┌─────────────────┐   │
│  │   EdgeGateway           │    │  AMQP     │   │  CloudGateway   │   │
│  │   ├─ MqttConsumer       │◄───┼──────────►│   │   (阶段3)       │   │
│  │   ├─ InfluxDbWriter     │    │           │   └─────────────────┘   │
│  │   ├─ RabbitMqForwarder  │    │           │                         │
│  │   ├─ WatermarkTracker   │    │           └─────────────────────────┘
│  │   └─ ReplayService      │    │
│  │                         │    │
│  │   ┌─────────────────┐   │    │
│  │   │  Local InfluxDB │   │    │
│  │   │  (7天滚动保留)   │   │    │
│  │   └─────────────────┘   │    │
│  │                         │    │
│  │   ┌─────────────────┐   │    │
│  │   │  Grafana (可选) │   │    │
│  │   │  本地看板        │   │    │
│  │   └─────────────────┘   │    │
│  └─────────────────────────┘    │
└─────────────────────────────────┘
```

---

## 3. 数据流转

```
MQTT Subscribe (amgateway/#)
    │
    ▼
反序列化 → DataBatch (按 mqtt-contract.md)
    │
    ├──► 写 Local InfluxDB（同步，确保落盘）
    │         ├── 成功 → 继续
    │         └── 失败 → 告警（磁盘满/InfluxDB 宕）
    │
    └──► 发 RabbitMQ（异步）
             │
             ├─ 成功 → 更新 watermark
             │
             └─ 失败（网络断开）
                   └── 数据已在 InfluxDB，不丢
                   └── 后台检测网络恢复
                   └── 恢复后从 watermark 之后读取回放
```

---

## 4. 数据模型

### 4.1 消费端 DataBatch（来自 mqtt-contract.md）

```csharp
namespace AmGatewayCloud.EdgeGateway.Models;

public class DataBatch
{
    public Guid BatchId { get; set; }
    public string? TenantId { get; set; }
    public string FactoryId { get; set; } = string.Empty;
    public string WorkshopId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public List<DataPoint> Points { get; set; } = [];
}

public class DataPoint
{
    public string Tag { get; set; } = string.Empty;
    public JsonElement Value { get; set; }
    public string ValueType { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? GroupName { get; set; }
}
```

### 4.2 InfluxDB 存储结构

| InfluxDB 概念 | 映射 |
|--------------|------|
| **Measurement** | `device_data` |
| **Tag** | `factory_id`, `workshop_id`, `device_id`, `protocol`, `tag`, `quality` |
| **Field** | `value` (string 存储，消费端按 valueType 转换), `value_type`, `group_name` |
| **Timestamp** | `batch.timestamp` (DataBatch 发布时间，用于回放排序) |

> 注：InfluxDB 2.x 使用 Line Protocol 写入，value 统一以字符串存储，避免类型冲突。

### 4.3 Watermark 记录

```csharp
public class Watermark
{
    public string HubId { get; set; } = string.Empty;
    public DateTimeOffset LastForwardedAt { get; set; }
    public long LastSequence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

存储方式：本地 SQLite 或 InfluxDB 单独 bucket（简单场景用文件即可）。

---

## 5. 配置模型

### 5.1 appsettings.json

```json
{
  "EdgeGateway": {
    "HubId": "edgehub-a",
    "FactoryId": "factory-a",
    "WorkshopId": "workshop-1",
    "TenantId": "default",

    "Mqtt": {
      "Broker": "localhost",
      "Port": 1883,
      "TopicFilter": "amgateway/#",
      "ClientId": "AmGatewayCloud-EdgeHub",
      "UseTls": false
    },

    "InfluxDb": {
      "Url": "http://localhost:8086",
      "Token": "edge-token",
      "Org": "amgateway",
      "Bucket": "edge-data",
      "RetentionHours": 168
    },

    "RabbitMq": {
      "HostName": "rmq-factory-a.company.com",
      "Port": 5671,
      "UseSsl": true,
      "VirtualHost": "/factory-a",
      "Username": "edgehub-workshop1",
      "Password": "",
      "Exchange": "amgateway.topic",
      "RoutingKeyTemplate": "amgateway.{factoryId}.{workshopId}.{deviceId}.{protocol}",
      "ReconnectDelayMs": 5000,
      "MaxReconnectDelayMs": 60000
    }
  }
}
```

### 5.2 配置映射类

```csharp
namespace AmGatewayCloud.EdgeGateway.Configuration;

public class EdgeGatewayConfig
{
    public string HubId { get; set; } = "edgehub-001";
    public string FactoryId { get; set; } = "factory-001";
    public string WorkshopId { get; set; } = "workshop-001";
    public string? TenantId { get; set; }

    public MqttConsumerConfig Mqtt { get; set; } = new();
    public InfluxDbConfig InfluxDb { get; set; } = new();
    public RabbitMqConfig RabbitMq { get; set; } = new();
}

public class MqttConsumerConfig
{
    public string Broker { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string TopicFilter { get; set; } = "amgateway/#";
    public string ClientId { get; set; } = "AmGatewayCloud-EdgeHub";
    public bool UseTls { get; set; } = false;
}

public class InfluxDbConfig
{
    public string Url { get; set; } = "http://localhost:8086";
    public string Token { get; set; } = string.Empty;
    public string Org { get; set; } = "amgateway";
    public string Bucket { get; set; } = "edge-data";
    public int RetentionHours { get; set; } = 168; // 7天
}

public class RabbitMqConfig
{
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; } = 5671;
    public bool UseSsl { get; set; } = true;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Exchange { get; set; } = "amgateway.topic";
    public string RoutingKeyTemplate { get; set; } = "amgateway.{factoryId}.{workshopId}.{deviceId}.{protocol}";
    public int ReconnectDelayMs { get; set; } = 5000;
    public int MaxReconnectDelayMs { get; set; } = 60000;
}
```

---

## 6. 组件设计

### 6.1 MqttConsumerService — MQTT 订阅与分发

**职责**：BackgroundService，订阅 MQTT，反序列化 DataBatch，分发到 InfluxDB 写入和 RabbitMQ 转发。

```
状态：

  Subscribed ──消息到达──► Processing ──┬──► Write InfluxDB (同步)
      │                                  │
      │                                  └──► Forward RabbitMQ (异步)
      │                                       │
      │                                       ├─ 成功 → Update Watermark
      │                                       └─ 失败 → 标记 offline，触发 Replay
      │
      └── 断线 ──► Reconnecting ──► 恢复 ──► Resubscribed
```

**关键行为**：
- 懒连接：首次启动时连接 MQTT
- 断线自动重连（指数退避）
- 消息到达后并行触发 InfluxDB 写入 + RabbitMQ 转发
- InfluxDB 写入失败 → 记录错误日志，不阻塞 RabbitMQ 转发
- RabbitMQ 转发失败 → 标记 offline，数据已在 InfluxDB

```csharp
namespace AmGatewayCloud.EdgeGateway.Services;

public class MqttConsumerService : BackgroundService
{
    public MqttConsumerService(
        IOptions<EdgeGatewayConfig> config,
        InfluxDbWriter influxWriter,
        RabbitMqForwarder rabbitForwarder,
        WatermarkTracker watermarkTracker,
        ILogger<MqttConsumerService> logger);

    protected override Task ExecuteAsync(CancellationToken ct);
}
```

### 6.2 InfluxDbWriter — 本地时序库写入

**职责**：将 DataBatch 写入本地 InfluxDB，确保数据落盘。

**关键行为**：
- 批量写入：积攒一定数量或一定时间后批量提交
- 使用 InfluxDB Line Protocol
- 写入失败抛异常，由调用方决定是否继续
- 启动时检查/创建 Bucket，设置 retention policy

```csharp
namespace AmGatewayCloud.EdgeGateway.Services;

public class InfluxDbWriter
{
    public Task EnsureBucketAsync(CancellationToken ct);
    public Task WriteBatchAsync(DataBatch batch, CancellationToken ct);
}
```

### 6.3 RabbitMqForwarder — RabbitMQ 转发

**职责**：将 DataBatch 序列化后发送到 RabbitMQ Topic Exchange。

**关键行为**：
- 懒连接：首次转发时连接
- 断线检测：连接断开时标记 `IsOnline = false`
- 指数退避重连
- 路由键按模板构造：`amgateway.{factoryId}.{workshopId}.{deviceId}.{protocol}`
- 转发成功返回 true，失败返回 false（不抛异常，避免阻塞）

```csharp
namespace AmGatewayCloud.EdgeGateway.Services;

public class RabbitMqForwarder : IAsyncDisposable
{
    public bool IsOnline { get; }
    public Task<bool> ForwardAsync(DataBatch batch, CancellationToken ct);
}
```

### 6.4 WatermarkTracker — 水位线追踪

**职责**：记录最后成功转发到 RabbitMQ 的时间点，用于断网恢复后回放定位。

**关键行为**：
- 内存缓存 + 定期持久化到本地文件
- 更新：每次 RabbitMQ 转发成功后更新
- 读取：断网恢复后读取 watermark，作为回放起点
- 格式：JSON 文件 `{hubId}.watermark.json`

```csharp
namespace AmGatewayCloud.EdgeGateway.Services;

public class WatermarkTracker
{
    public DateTimeOffset GetLastForwardedTime();
    public void UpdateWatermark(DateTimeOffset timestamp);
    public Task LoadAsync(CancellationToken ct);
    public Task SaveAsync(CancellationToken ct);
}
```

### 6.5 ReplayService — 断网恢复回放

**职责**：检测到 RabbitMQ 恢复后，从 InfluxDB 读取未转发数据，补发到 RabbitMQ。

**关键行为**：
- 触发条件：RabbitMQ 从 offline → online
- 读取范围：`watermark.LastForwardedAt` 到 `now`
- 按时间顺序分批读取（每次 100 条）
- 每条补发成功后更新 watermark
- 补发期间新到达的数据正常处理（双轨并行）
- 补发完成后输出统计日志

```csharp
namespace AmGatewayCloud.EdgeGateway.Services;

public class ReplayService
{
    public Task ReplayAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
```

---

## 7. 路由键格式

```
amgateway.{factoryId}.{workshopId}.{deviceId}.{protocol}
```

**示例：**
- `amgateway.factory-a.workshop-1.simulator-001.modbus`
- `amgateway.factory-a.workshop-1.simulator-002.opcua`

**RabbitMQ Exchange 类型**：Topic Exchange

**CloudGateway 消费端绑定**：
- `amgateway.factory-a.#` → 消费工厂 A 所有数据
- `amgateway.factory-a.workshop-1.#` → 消费车间 1 数据

---

## 8. 错误处理策略

| 场景 | 处理 |
|------|------|
| MQTT 首次连接失败 | 指数退避重连，不影响程序启动 |
| MQTT 运行中断开 | 标记断开，后台重连，数据不丢（已在 InfluxDB） |
| InfluxDB 写入失败 | 记录错误日志，继续转发 RabbitMQ（优先保证云端） |
| RabbitMQ 首次连接失败 | 指数退避重连，数据写入 InfluxDB |
| RabbitMQ 运行中断开 | 标记 offline，数据写入 InfluxDB，触发 watermark |
| RabbitMQ 恢复 | 启动 ReplayService 回放未转发数据 |
| 断网超过 retention 时间 | 记录告警，部分数据已丢失，无法回放 |
| 磁盘满（InfluxDB 无法写入） | 记录 Fatal 日志，建议运维介入 |

---

## 9. 项目文件

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MQTTnet" Version="4.*" />
    <PackageReference Include="InfluxDB.Client" Version="4.*" />
    <PackageReference Include="RabbitMQ.Client" Version="6.*" />
    <PackageReference Include="Serilog.AspNetCore" Version="8.*" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.*" />
  </ItemGroup>
</Project>
```

---

## 10. 项目结构

```
src/AmGatewayCloud.EdgeGateway/
├── AmGatewayCloud.EdgeGateway.csproj
├── Program.cs                              # 入口 + DI
├── appsettings.json                        # 默认配置
├── Configuration/
│   └── EdgeGatewayConfig.cs                # 配置映射类
├── Models/
│   ├── DataBatch.cs                        # MQTT 消费契约模型
│   └── DataPoint.cs
├── Services/
│   ├── MqttConsumerService.cs              # MQTT 订阅 + 分发
│   ├── InfluxDbWriter.cs                   # 本地 InfluxDB 写入
│   ├── RabbitMqForwarder.cs                # RabbitMQ 转发
│   ├── WatermarkTracker.cs                 # 水位线管理
│   └── ReplayService.cs                    # 断网恢复回放
└── watermarks/                             # 水位线文件目录
    └── edgehub-a.watermark.json
```

---

## 11. 运行验证

### 前置条件

- Mosquitto 运行在 localhost:1883
- InfluxDB 2.x 运行在 localhost:8086（已创建 token）
- RabbitMQ 运行在可访问地址（阶段2可先本地 Docker 启动）

### 启动

```powershell
cd src/AmGatewayCloud.EdgeGateway
dotnet run
```

### 验证步骤

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 启动 Modbus/OpcUa 采集器 | EdgeGateway 日志显示收到数据 |
| 2 | 检查 InfluxDB | `edge-data` bucket 中有 device_data measurement |
| 3 | 检查 RabbitMQ | 消费者能收到 `amgateway.factory-a.workshop-1.#` 数据 |
| 4 | 断开 RabbitMQ | EdgeGateway 标记 offline，数据继续写入 InfluxDB |
| 5 | 恢复 RabbitMQ | EdgeGateway 自动重连，启动回放，补发未转发数据 |
| 6 | 检查 watermark 文件 | 时间戳已更新到最新 |

### 预期日志输出

```
[16:30:01 INF] EdgeGateway starting - Hub: edgehub-a, Factory: factory-a, Workshop: workshop-1
[16:30:01 INF] MQTT connected to localhost:1883, subscribed: amgateway/#
[16:30:01 INF] InfluxDB bucket 'edge-data' ready
[16:30:01 INF] RabbitMQ connected to rmq-factory-a:5671
[16:30:03 INF] [batch] device=simulator-001 protocol=modbus points=10 influx=ok rabbitmq=ok
[16:30:05 INF] [batch] device=simulator-001 protocol=opcua points=20 influx=ok rabbitmq=ok
...
[16:35:10 WRN] RabbitMQ disconnected, marking offline
[16:35:10 INF] Data will be cached to InfluxDB until connection restored
[16:35:15 INF] [batch] device=simulator-001 protocol=modbus points=10 influx=ok rabbitmq=skipped(offline)
...
[16:40:22 INF] RabbitMQ reconnected after 312s
[16:40:22 INF] Starting replay from 2026-05-07T16:35:10Z to 2026-05-07T16:40:22Z
[16:40:23 INF] Replay progress: 150/150 batches, 100%
[16:40:23 INF] Replay completed, watermark updated
```

---

## 12. 后续演进

| 阶段 | 变化 |
|------|------|
| 阶段3 | CloudGateway 消费 RabbitMQ，EdgeGateway 不变 |
| 阶段5 | 容器化，Serilog → Seq，加入 OpenTelemetry 追踪 |
| 阶段6 | 配置中心下发 `TenantId`/`FactoryId`/`WorkshopId`，动态加载 |
| 阶段7 | 多租户完善，JWT 中间件从请求上下文注入 `TenantId` |
