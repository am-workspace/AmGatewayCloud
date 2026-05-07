# AmGatewayCloud.CloudGateway — 阶段3 完整方案

## 1. 定位

独立微服务，部署在云端，负责：
- 消费多个工厂的 RabbitMQ 队列，聚合边缘数据
- 时序数据写入云端 TimescaleDB（PostgreSQL 扩展）
- 设备/工厂元数据写入 PostgreSQL
- 消息去重，Consumer 独立管理，互不影响
- 为阶段4（AlarmService）提供数据基础

---

## 2. 架构

```
工厂A EdgeGateway ──AMQP(SSL)──►
                                 │
工厂B EdgeGateway ──AMQP(SSL)──►├─► 云端 RabbitMQ Cluster
                                 │      amgateway.topic
                                 │      ├─ Queue: amgateway.factory-a
                                 │      └─ Queue: amgateway.factory-b
                                 │
工厂C EdgeGateway ──AMQP(SSL)──►│
                                 │
                                 ▼
                    ┌─────────────────────────┐
                    │   CloudGateway           │
                    │   ├─ MultiRabbitMqConsumer │  ← 多工厂独立 Consumer
                    │   ├─ MessageDeduplicator   │  ← 去重
                    │   ├─ TimescaleDbWriter     │  ← 时序数据写入
                    │   ├─ PostgreSqlDeviceStore │  ← 设备元数据管理
                    │   └─ HealthMonitorService  │  ← 健康监控
                    │                          │
                    │   ┌─────────────────┐    │
                    │   │  TimescaleDB    │    │  ← 云端时序库
                    │   │  (PostgreSQL)   │    │
                    │   └─────────────────┘    │
                    │   ┌─────────────────┐    │
                    │   │  PostgreSQL     │    │  ← 业务数据库
                    │   │  (设备/报警/工单) │   │
                    │   └─────────────────┘    │
                    └─────────────────────────┘
                                 │
                                 ▼
                    ┌─────────────────────────┐
                    │  阶段4: AlarmService     │
                    │  阶段5: 工单/DDD         │
                    └─────────────────────────┘
```

---

## 3. 数据流转

```
RabbitMQ Queue (amgateway.factory-a)
    │
    ▼
MultiRabbitMqConsumer (工厂A独立线程)
    │
    ├──► MessageDeduplicator (BatchId 去重)
    │         │
    │         ├──► 重复 ──► 丢弃
    │         │
    │         └──► 新数据 ──► 分流
    │                           │
    │                           ├──► TimescaleDbWriter
    │                           │         ├── 时序数据: value_int/float/bool/string
    │                           │         └── 批量写入 + 自动建表
    │                           │
    │                           └──► PostgreSqlDeviceStore
    │                                     ├── 设备自动注册 (首次出现)
    │                                     ├── 工厂/车间信息更新
    │                                     └── 最新数据时间戳记录
    │
    └──► 消费确认 (ACK) ──► RabbitMQ
```

**关键原则**：
- **先写数据库，后 ACK RabbitMQ**（数据不丢）
- **ACK 必须等 `TimescaleDbWriter.FlushAsync()` 成功后才能发出**：如果 Writer 使用内存缓冲，Consumer 不能仅调用 `WriteBatchAsync` 就 ACK，否则进程崩溃会导致缓冲区数据丢失
- TimescaleDB + PostgreSQL 写入失败后不 ACK，消息重新入队
- 两个数据库写入可以并行（无事务关联）
- **背压传递**：`TimescaleDbWriter` 内部缓冲队列满时阻塞上游 Consumer，让 RabbitMQ 自然堆积，防止应用 OOM

---

## 4. 数据模型

### 4.1 消费端 DataBatch（与 mqtt-contract.md 一致）

```csharp
namespace AmGatewayCloud.CloudGateway.Models;

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

    // 阶段6 OpenTelemetry 预留：边缘端注入的 TraceParent
    public string? TraceParent { get; set; }
}

public class DataPoint
{
    public string Tag { get; set; } = string.Empty;
    public JsonElement Value { get; set; }
    public string ValueType { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string? GroupName { get; set; }
}
```

### 4.2 TimescaleDB 存储结构

**Hypertable**: `device_data`

| 列名 | 类型 | 说明 |
|------|------|------|
| `time` | `TIMESTAMPTZ` | 时间戳（分区键） |
| `batch_id` | `UUID` | 批次 ID（去重用） |
| `tenant_id` | `TEXT` | 租户标识 |
| `factory_id` | `TEXT` | 工厂标识 |
| `workshop_id` | `TEXT` | 车间标识 |
| `device_id` | `TEXT` | 设备标识 |
| `protocol` | `TEXT` | 协议类型 |
| `tag` | `TEXT` | 测点名称 |
| `quality` | `TEXT` | 数据质量 |
| `group_name` | `TEXT` | 分组名称 |
| `value_int` | `BIGINT` | 整数值 |
| `value_float` | `DOUBLE PRECISION` | 浮点值 |
| `value_bool` | `BOOLEAN` | 布尔值 |
| `value_string` | `TEXT` | 字符串值 |
| `value_type` | `TEXT` | 原始类型标识 |

**索引**：
```sql
CREATE INDEX idx_device_data_lookup 
ON device_data (factory_id, device_id, tag, time DESC);
```

**数据保留策略**（`EnsureHypertableAsync` 中一并配置）：
```sql
-- 原始数据保留 90 天
SELECT add_retention_policy('device_data', INTERVAL '90 days', if_not_exists => TRUE);

-- 连续聚合视图保留 1 年（若启用）
SELECT add_retention_policy('device_data_hourly', INTERVAL '1 year', if_not_exists => TRUE);
```

**连续聚合视图**（可选）：
```sql
CREATE MATERIALIZED VIEW device_data_hourly
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
```

### 4.3 PostgreSQL 业务表

**Factory 表**：
```sql
CREATE TABLE factories (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    rabbitmq_vhost TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);
```

**Workshop 表**：
```sql
CREATE TABLE workshops (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    factory_id TEXT REFERENCES factories(id),
    created_at TIMESTAMPTZ DEFAULT NOW()
);
```

**Device 表**：
```sql
CREATE TABLE devices (
    id TEXT PRIMARY KEY,
    name TEXT,
    factory_id TEXT REFERENCES factories(id),
    workshop_id TEXT REFERENCES workshops(id),
    protocol TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    first_seen_at TIMESTAMPTZ,
    last_seen_at TIMESTAMPTZ,
    tags TEXT[], -- 测点列表
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE UNIQUE INDEX idx_devices_lookup 
ON devices (factory_id, workshop_id, id);
```

---

## 5. 配置模型

### 5.1 appsettings.json

```json
{
  "CloudGateway": {
    "TenantId": "default",
    
    "TimescaleDb": {
      "Host": "timescale.cloud.example.com",
      "Port": 5432,
      "Database": "amgateway_timeseries",
      "Username": "cloudgateway",
      "Password": "${TIMESCALE_PASSWORD}",
      "SslMode": "Require",
      "BatchSize": 1000,
      "FlushIntervalMs": 5000
    },
    
    "PostgreSql": {
      "Host": "postgres.cloud.example.com",
      "Port": 5432,
      "Database": "amgateway_business",
      "Username": "cloudgateway",
      "Password": "${POSTGRES_PASSWORD}",
      "SslMode": "Require"
    },
    
    "RabbitMq": {
      "HostName": "rmq.cloud.example.com",
      "Port": 5671,
      "UseSsl": true,
      "VirtualHost": "/",
      "Username": "cloudgateway",
      "Password": "${RABBITMQ_PASSWORD}",
      "PrefetchCount": 100,
      "ReconnectDelayMs": 5000,
      "MaxReconnectDelayMs": 60000
    },
    
    "Factories": [
      {
        "FactoryId": "factory-a",
        "QueueName": "amgateway.factory-a",
        "Enabled": true
      },
      {
        "FactoryId": "factory-b",
        "QueueName": "amgateway.factory-b",
        "Enabled": true
      }
    ]
  }
}
```

### 5.2 配置映射类

```csharp
namespace AmGatewayCloud.CloudGateway.Configuration;

public class CloudGatewayConfig
{
    public string TenantId { get; set; } = "default";
    public TenantResolutionMode TenantResolutionMode { get; set; } = TenantResolutionMode.Static;
    public TimescaleDbConfig TimescaleDb { get; set; } = new();
    public PostgreSqlConfig PostgreSql { get; set; } = new();
    public RabbitMqConfig RabbitMq { get; set; } = new();
    public List<FactoryConsumerConfig> Factories { get; set; } = [];
}

public enum TenantResolutionMode
{
    Static,      // 从配置文件读取（阶段3默认）
    FromMessage  // 从 DataBatch.TenantId 读取（阶段7多租户推荐）
}

public class TimescaleDbConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SslMode { get; set; } = "Require";
    public int BatchSize { get; set; } = 1000;
    public int FlushIntervalMs { get; set; } = 5000;
}

public class PostgreSqlConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SslMode { get; set; } = "Require";
}

public class RabbitMqConfig
{
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; } = 5671;
    public bool UseSsl { get; set; } = true;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public ushort PrefetchCount { get; set; } = 100;
    public int ReconnectDelayMs { get; set; } = 5000;
    public int MaxReconnectDelayMs { get; set; } = 60000;
}

public class FactoryConsumerConfig
{
    public string FactoryId { get; set; } = string.Empty;
    public string QueueName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
```

---

## 6. 组件设计

### 6.1 MultiRabbitMqConsumer — 多工厂 RabbitMQ 消费

**职责**：管理多个工厂的独立 Consumer，每个工厂一个线程/连接。

```
状态：

  ┌─────────────────┐
  │ Consumer-A      │──► 消费 amgateway.factory-a
  │ (独立连接)       │    断线 → 独立重连
  └─────────────────┘
  ┌─────────────────┐
  │ Consumer-B      │──► 消费 amgateway.factory-b
  │ (独立连接)       │    断线 → 独立重连
  └─────────────────┘
```

**关键行为**：
- 根据 `Factories` 配置启动 N 个独立 Consumer
- 每个 Consumer 独立连接、独立通道、独立重连逻辑
- 工厂 A 断线不影响工厂 B
- 消息反序列化为 `DataBatch`，送入分流管道
- **DLQ（死信队列）保护**：每个工厂队列绑定 DLX，数据库故障时消息经有限重试后进入死信队列，避免无限重试风暴
- **熔断机制**：单个工厂连续数据库写入失败达到阈值后，暂停该工厂消费，指数退避恢复，保护其他工厂和其他系统资源
- **错误分类**：区分可重试错误（网络超时、连接断开）与不可重试错误（数据格式违规、消息超大），脏数据直接 ACK 并记入审计日志，避免死循环
- 数据库写入且 `FlushAsync()` 成功后 ACK，失败则 NACK（消息重新入队）

```csharp
public class MultiRabbitMqConsumer : IHostedService
{
    public Task StartAsync(CancellationToken ct);
    public Task StopAsync(CancellationToken ct);
    public IReadOnlyDictionary<string, bool> GetConsumerStatus(); // 工厂ID -> 是否在线
}
```

### 6.2 MessageDeduplicator — 消息去重

**职责**：基于 `BatchId` 去重，防止边缘端回放导致重复写入。

**实现方式**：
- **内存缓存**：最近 10 万条 BatchId 的 `MemoryCache` / `HashSet<Guid>`（精确无误判，10 万条约占用 1.6 MB）
- **数据库兜底**：TimescaleDB `batch_id` 列加 UNIQUE 约束（`ON CONFLICT DO NOTHING`）
- **TTL**：内存缓存 1 小时过期

```csharp
public class MessageDeduplicator
{
    public bool IsDuplicate(Guid batchId);
}
```

### 6.3 TimescaleDbWriter — 云端时序库写入

**职责**：将 `DataBatch.Points` 批量写入 TimescaleDB hypertable。

**关键行为**：
- 批量缓冲：积攒 `BatchSize` 条或 `FlushIntervalMs` 时间后提交
- **背压控制**：内部使用 `BoundedChannel`，缓冲上限 = `BatchSize * 5`，队列满时阻塞上游写入，防止 OOM
- 自动建表：首次启动时检查 `device_data` hypertable 是否存在，并配置 retention policy
- 按 `valueType` 分字段写入（与边缘端 InfluxDB 字段结构一致），**未知类型自动 fallback 到字符串列**
- 使用 Npgsql 参数化 SQL，防止注入

```csharp
public class TimescaleDbWriter : IAsyncDisposable
{
    public Task EnsureHypertableAsync(CancellationToken ct);
    public Task WriteBatchAsync(DataBatch batch, CancellationToken ct);
    public Task FlushAsync(CancellationToken ct);
}
```

**SQL 写入示例**：
```sql
INSERT INTO device_data 
(time, batch_id, tenant_id, factory_id, workshop_id, device_id, protocol, tag, quality, group_name, value_float, value_type)
VALUES 
('2026-05-07T15:31:00Z', '550e8400-...', 'default', 'factory-a', 'workshop-1', 'simulator-001', 'modbus', 'temperature', 'Good', 'sensors', 23.5, 'double')
ON CONFLICT (time, batch_id, tag) DO NOTHING;
```

### 6.4 PostgreSqlDeviceStore — 设备元数据管理

**职责**：设备自动注册、工厂/车间信息管理、最新数据时间戳更新。

**关键行为**：
- **自动注册**：首次收到某 `deviceId` 的数据时，检查 `devices` 表，不存在则插入
- **UPSERT 更新**：`last_seen_at`、`tags` 列表更新
- **tags 缓存降噪**：内存缓存每台设备已知的 tag 集合，仅当发现新 tag 时才更新数据库 `tags` 字段，降低行级锁热点
- **`last_seen_at` 批量/降频更新**：可按 30 秒窗口批量更新，减少写压力
- **工厂/车间懒创建**：如果 `factories`/`workshops` 表不存在对应记录，自动插入（兜底）

```csharp
public class PostgreSqlDeviceStore
{
    public Task EnsureDeviceAsync(DataBatch batch, CancellationToken ct);
    public Task UpdateLastSeenAsync(string deviceId, DateTimeOffset timestamp, CancellationToken ct);
    public Task<List<Device>> GetDevicesByFactoryAsync(string factoryId, CancellationToken ct);
}
```

### 6.5 HealthMonitorService — 健康监控

**职责**：通过 `AspNetCore.HealthChecks` 暴露 `/health` 端点，供 K8s/Docker 存活探针与就绪探针使用；同时聚合 Consumer 级指标。

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
                lagSeconds = c.Lag?.TotalSeconds,
                c.MessagesPerSecond,
                c.TotalProcessed
            })
        };
        await context.Response.WriteAsJsonAsync(result);
    }
});
```

```csharp
public class ConsumerHealth
{
    public string FactoryId { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public TimeSpan? Lag { get; set; } // 消费延迟
    public long MessagesPerSecond { get; set; }
    public long TotalProcessed { get; set; }
}

---

## 7. 错误处理策略

### 7.1 错误分类

| 分类 | 示例 | 处理 |
|------|------|------|
| **可重试错误**（Transient） | 网络超时、连接断开、DB 暂时不可用 | NACK + requeue，进入 RabbitMQ 重试；配合 DLQ 限制最大重试次数 |
| **不可重试错误**（Permanent） | 数据格式违规、`deviceId` 超长、消息超大、未知 ValueType | 直接 ACK，丢弃脏数据，记入审计日志，避免无限死循环 |

### 7.2 场景处理

| 场景 | 处理 |
|------|------|
| RabbitMQ 首次连接失败 | 指数退避重连，不影响其他工厂 Consumer |
| RabbitMQ 运行中断开 | 标记 offline，独立重连，不 ACK 消息（消息保留在队列） |
| TimescaleDB 写入失败（可重试） | 不 ACK，消息重新入队；连续失败触发熔断，暂停该工厂消费 |
| PostgreSQL 写入失败（可重试） | 不 ACK，消息重新入队；连续失败触发熔断 |
| 消息反序列化失败 | 直接 ACK，记入审计日志（记录 payload 前 200 字节） |
| 重复消息（BatchId 命中） | 直接 ACK（已处理过） |
| 单条消息超大（>1MB） | 直接 ACK，记入审计日志（记录 BatchId 和 payload 摘要） |
| 未知 `ValueType` | fallback 到 `value_string`，记录告警日志，正常 ACK |
| 某个工厂队列堆积 | 监控告警，Consumer 可动态降低 PrefetchCount，不影响其他工厂 |
| 数据库长时间不可用（>5分钟） | DLQ 承接超限重试消息，熔断器暂停消费，保护系统资源 |

---

## 8. 项目文件

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="RabbitMQ.Client" Version="6.*" />
    <PackageReference Include="Npgsql" Version="9.*" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="9.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="9.*" />
    <PackageReference Include="Serilog.AspNetCore" Version="8.*" />
  </ItemGroup>
</Project>
```

---

## 9. 项目结构

```
src/AmGatewayCloud.CloudGateway/
├── AmGatewayCloud.CloudGateway.csproj
├── Program.cs                              # 入口 + DI
├── appsettings.json                        # 默认配置
├── Configuration/
│   └── CloudGatewayConfig.cs               # 配置映射类
├── Models/
│   ├── DataBatch.cs                        # 消费契约模型
│   └── DataPoint.cs
├── Services/
│   ├── MultiRabbitMqConsumer.cs            # 多工厂 RabbitMQ 消费
│   ├── MessageDeduplicator.cs              # 消息去重
│   ├── TimescaleDbWriter.cs                # 时序数据写入
│   ├── PostgreSqlDeviceStore.cs            # 设备元数据管理
│   └── HealthMonitorService.cs             # 健康监控
└── Infrastructure/
    └── Migrations/                         # EF Core 迁移文件
```

---

## 10. 运行验证

### 前置条件

- PostgreSQL 14+ 运行在云端（含 TimescaleDB 扩展）
- RabbitMQ Cluster 运行在云端（`amgateway.topic` Exchange 已创建）
- 至少一个 EdgeGateway 正在向 RabbitMQ 推送数据

### 启动

```powershell
cd src/AmGatewayCloud.CloudGateway
dotnet run
```

### 验证步骤

| 步骤 | 操作 | 预期结果 |
|------|------|---------|
| 1 | 启动 CloudGateway | 日志显示连接 RabbitMQ、PostgreSQL、TimescaleDB 成功；`/health` 返回 healthy |
| 2 | 检查 Consumer 状态 | 各工厂 Consumer 显示 online |
| 3 | 启动 EdgeGateway 推送数据 | CloudGateway 日志显示收到 batch，factory-a 处理中 |
| 4 | 检查 TimescaleDB | `device_data` 表有数据，按 valueType 分字段正确 |
| 5 | 检查 PostgreSQL | `devices` 表自动注册了新设备，`last_seen_at` 已更新 |
| 6 | 断开工厂 A 的 RabbitMQ | Consumer-A 标记 offline，Consumer-B 继续消费 |
| 7 | 恢复工厂 A | Consumer-A 自动重连，继续消费积压消息 |
| 8 | 重复数据测试 | 相同 BatchId 的消息只写入一次，重复的被去重 |
| 9 | 模拟 TimescaleDB 不可用 | Consumer-A 进入熔断状态，消息进入 DLQ，不阻塞 Consumer-B |
| 10 | 发送脏数据（超长 deviceId） | 直接 ACK，审计日志表有记录，不触发无限重试 |
| 11 | 检查 retention policy | `device_data` 自动清理 90 天前数据 |

### 预期日志输出

```
[16:30:01 INF] CloudGateway starting - Tenant: default, Factories: 2
[16:30:01 INF] PostgreSQL connected to postgres.cloud.example.com:5432
[16:30:01 INF] TimescaleDB connected to timescale.cloud.example.com:5432
[16:30:01 INF] RabbitMQ connected to rmq.cloud.example.com:5671
[16:30:01 INF] Consumer-A started: queue=amgateway.factory-a, prefetch=100
[16:30:01 INF] Consumer-B started: queue=amgateway.factory-b, prefetch=100
[16:30:03 INF] [factory-a] device=simulator-001 protocol=modbus points=10 timescale=ok postgres=ok
[16:30:05 INF] [factory-b] device=simulator-002 protocol=opcua points=20 timescale=ok postgres=ok
...
[16:35:10 WRN] Consumer-A offline: factory-a disconnected
[16:35:10 INF] Consumer-A reconnecting in 5000ms...
[16:40:22 INF] Consumer-A reconnected after 312s
```

---

## 11. 与阶段4的衔接

| 阶段4 需求 | CloudGateway 提供的基础 |
|-----------|------------------------|
| 阈值判断 | TimescaleDB 中可按设备/时间范围查询历史数据 |
| 报警生成 | PostgreSQL `devices` 表提供设备元信息，`device_data` 提供实时数据 |
| 工单关联 | PostgreSQL 设备表是 `Equipment` 聚合根的基础 |
| SignalR 推送 | HealthMonitorService 的消费延迟数据可用于实时监控 |

---

## 12. 后续演进

| 阶段 | 变化 |
|------|------|
| 阶段4 | AlarmService 消费 TimescaleDB 数据，生成报警写入 PostgreSQL |
| 阶段5 | DDD 提炼，EF Core 仓储完善，引入领域事件 |
| 阶段6 | OpenTelemetry 跨消息队列追踪，DataBatch 预留 TraceParent |
| 阶段7 | 多租户完善，JWT 中间件，配置中心动态下发工厂列表 |

---

## 13. 关键Check-list（阶段3 完成标准）

- [ ] CloudGateway 实现完成并通过全部验证
- [ ] 支持至少 2 个工厂同时消费
- [ ] 消息去重机制验证通过
- [ ] 设备自动注册验证通过
- [ ] TimescaleDB 连续聚合视图创建（可选）
- [ ] 健康监控端点可用
- [ ] 阶段4 的 AlarmService 可直接查询 TimescaleDB 和 PostgreSQL
