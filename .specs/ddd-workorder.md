# 阶段 6：DDD 提炼 + 维修工单

## 1. 总体目标

1. **6.1 DDD 提炼**：从 AlarmService 中提炼领域层，将 Dapper 手写 SQL 迁移到 EF Core + 仓储模式，引入 MediatR 领域事件
2. **6.2 维修工单**：新增独立的 WorkOrderService，订阅 RabbitMQ 报警事件自动创建工单，提供工单管理 API + 前端页面

---

## 2. 架构

```
AlarmService (重构后)                        WorkOrderService (新增)
├── Domain 层 (新增项目)                      ├── 订阅 RabbitMQ alarm.#
│   ├── Alarm 聚合根                          │   → 收到 Active 报警 → 自动创建工单
│   ├── AlarmRule 值对象/实体                  │
│   ├── 领域事件                              ├── Controllers/
│   │   ├── AlarmTriggeredEvent               │   └── WorkOrdersController
│   │   └── AlarmClearedEvent                 ├── Services/
│   └── 领域服务                              │   ├── WorkOrderQueryService
│       └── 规则评估逻辑                       │   └── AlarmEventConsumer (BackgroundService)
├── Infrastructure 层 (新增项目)              ├── Models/
│   ├── AppDbContext (EF Core)                │   └── WorkOrder
│   ├── 仓储实现                              └── Infrastructure/
│   └── MediatR 事件发布                          ├── RabbitMqConnectionManager
├── 现有代码 (委托给 Domain)                      └── WorkOrderDbInitializer
│   ├── Controllers → 不变                    
│   └── Services → 调用 Domain 聚合根         WebApi (扩展 YARP)
                                              ├── /api/workorders/* → WorkOrderService
                                              └── SignalR 推送工单事件

RabbitMQ /business vhost:
  Exchange: amgateway.alarms
  ├── Queue: amgateway.alarm-notifications     → WebApi (现有)
  ├── Queue: amgateway.workorder.alarm-events  → WorkOrderService (新增)
  └── RoutingKey: alarm.{tenantId}.{factoryId}.{level}
```

---

## 3. 设计决策

### 3.1 为什么工单是独立服务而不是放在 AlarmService 里？

| 维度 | 放入 AlarmService | 独立 WorkOrderService |
|------|------------------|----------------------|
| 职责 | AlarmService 变臃肿（5+模块） | 各服务职责单一 |
| 耦合 | 工单和报警数据访问混在一起 | 独立数据库表、独立部署 |
| 故障链路 | 报警服务挂 = 工单也挂 | 互不影响 |
| 演进 | 工单需求膨胀会拖累报警 | 工单可独立迭代（预防性维护、巡检等） |

**结论**：工单与报警是两个不同的限界上下文，独立服务。通过 RabbitMQ 松耦合。

### 3.2 为什么用新队列而不是 WebApi 转发？

| 方案 | 做法 | 优劣 |
|------|------|------|
| A: WebApi 转发 | RabbitMQ → WebApi → HTTP → WorkOrderService | 简单，但 WebApi 不再是纯 BFF |
| **B: 新队列订阅** | RabbitMQ → WorkOrderService 自己消费 | ✅ WebApi 保持零业务逻辑，故障隔离 |

**结论**：方案 B。WebApi 是纯 BFF，不应该知道"报警要创建工单"这个业务规则。

### 3.3 为什么不新建 vhost？

| 维度 | 同 /business 新队列 | 新 vhost |
|------|-------------------|---------|
| 配置 | 零额外配置 | AlarmService 需双 vhost 或配 Shovel |
| 语义 | 报警和工单都是"业务事件" | 过度隔离 |
| 数据 | 同一 exchange，消费同一份消息 | 需保证消息一致性 |
| 权限 | 同团队，不需要 | 适合不同团队/租户 |

**结论**：同一个 `/business` vhost，新建 `amgateway.workorder.alarm-events` 队列。

### 3.4 DDD 提炼的范围

**只提炼 Alarm 领域，不动评估引擎**。原因：
- 评估引擎是调度逻辑（BackgroundService + 定时拉数据），不是领域核心
- 评估引擎依赖 TimescaleDB 读模型，不适合放入 Domain 层
- 提炼重点：`AlarmEvent` → `Alarm` 聚合根，`AlarmRule` → 领域实体，状态流转 → 领域事件

---

## 4. 6.1 DDD 提炼

### 4.1 新增项目

#### AmGatewayCloud.Domain — 领域层

```
src/AmGatewayCloud.Domain/
├── AmGatewayCloud.Domain.csproj          # net10.0 类库，无外部依赖
├── Aggregates/
│   └── Alarm/
│       ├── Alarm.cs                      # Alarm 聚合根
│       ├── AlarmRule.cs                  # 规则实体（领域模型，非 DB 模型）
│       ├── AlarmLevel.cs                 # 值对象枚举
│       ├── AlarmStatus.cs                # 值对象枚举
│       └── OperatorType.cs               # 值对象枚举
├── Events/
│   ├── AlarmTriggeredEvent.cs           # 报警触发领域事件
│   └── AlarmClearedEvent.cs             # 报警恢复领域事件
├── Services/
│   └── AlarmDomainService.cs            # 领域服务（状态流转规则、校验逻辑）
└── Common/
    └── DomainEvent.cs                    # 领域事件基类
```

#### AmGatewayCloud.Infrastructure — 基础设施层

```
src/AmGatewayCloud.Infrastructure/
├── AmGatewayCloud.Infrastructure.csproj # net10.0 类库 + EF Core + MediatR + Npgsql
├── Persistence/
│   ├── AppDbContext.cs                   # EF Core DbContext
│   └── Configurations/
│       ├── AlarmEventConfiguration.cs   # 实体映射 + 索引
│       └── AlarmRuleConfiguration.cs     # 实体映射 + 索引
├── Repositories/
│   ├── AlarmEventRepository.cs          # 仓储实现
│   └── AlarmRuleRepository.cs           # 仓储实现
└── Events/
    └── MediatRDomainEventPublisher.cs    # MediatR 发布领域事件
```

### 4.2 领域模型

#### Alarm 聚合根

```csharp
namespace AmGatewayCloud.Domain.Aggregates.Alarm;

public class Alarm
{
    public Guid Id { get; private set; }
    public string RuleId { get; private set; }
    public string TenantId { get; private set; }
    public string FactoryId { get; private set; }
    public string? WorkshopId { get; private set; }
    public string DeviceId { get; private set; }
    public string Tag { get; private set; }
    public double? TriggerValue { get; private set; }
    public AlarmLevel Level { get; private set; }
    public AlarmStatus Status { get; private set; }
    public bool IsStale { get; private set; }
    public string? Message { get; private set; }
    public DateTimeOffset TriggeredAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public string? AcknowledgedBy { get; private set; }
    public DateTimeOffset? SuppressedAt { get; private set; }
    public string? SuppressedBy { get; private set; }
    public string? SuppressedReason { get; private set; }
    public DateTimeOffset? ClearedAt { get; private set; }
    public double? ClearValue { get; private set; }

    private readonly List<DomainEvent> _domainEvents = [];
    public IReadOnlyList<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    // 工厂方法：创建新报警
    public static Alarm Create(/* params */)
    {
        var alarm = new Alarm { /* 初始化 */ };
        alarm._domainEvents.Add(new AlarmTriggeredEvent(alarm.Id, alarm.TenantId, alarm.FactoryId, alarm.DeviceId, alarm.Level));
        return alarm;
    }

    // 状态流转（领域逻辑内聚）
    public void Acknowledge(string acknowledgedBy) { /* Active → Acked */ }
    public void Suppress(string suppressedBy, string reason) { /* Active/Acked → Suppressed */ }
    public void Clear(double? clearValue) { /* → Cleared */ }
    public void MarkStale() { IsStale = true; }
    public void ClearStale() { IsStale = false; }
    public void ClearDomainEvents() => _domainEvents.Clear();
}
```

#### 值对象

```csharp
public enum AlarmLevel { Info, Warning, Critical, Fatal }
public enum AlarmStatus { Active, Acked, Suppressed, Cleared }
public enum OperatorType { GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Equal, NotEqual }
```

#### 领域事件

```csharp
public abstract class DomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
}

public class AlarmTriggeredEvent : DomainEvent
{
    public Guid AlarmId { get; }
    public string TenantId { get; }
    public string FactoryId { get; }
    public string DeviceId { get; }
    public AlarmLevel Level { get; }

    public AlarmTriggeredEvent(Guid alarmId, string tenantId, string factoryId, string deviceId, AlarmLevel level)
    {
        AlarmId = alarmId; TenantId = tenantId; FactoryId = factoryId;
        DeviceId = deviceId; Level = level;
    }
}

public class AlarmClearedEvent : DomainEvent
{
    public Guid AlarmId { get; }
    public string TenantId { get; }
    public string FactoryId { get; }
    public string DeviceId { get; }

    public AlarmClearedEvent(Guid alarmId, string tenantId, string factoryId, string deviceId) { ... }
}
```

#### 领域服务

```csharp
public class AlarmDomainService
{
    // 校验状态流转是否合法
    public void ValidateTransition(AlarmStatus from, AlarmStatus to) { ... }
    
    // 校验 ClearThreshold 逻辑（从 AlarmRuleService 迁入）
    public void ValidateClearThreshold(OperatorType op, double threshold, double? clearThreshold) { ... }
}
```

### 4.3 EF Core 数据模型映射

> **数据库 schema 完全不变**，EF Core 映射到现有表结构，只是把 Dapper 手写 SQL 替换为 EF Core 操作。

```csharp
// AppDbContext
public class AppDbContext : DbContext
{
    public DbSet<AlarmEventEntity> AlarmEvents => Set<AlarmEventEntity>();
    public DbSet<AlarmRuleEntity> AlarmRules => Set<AlarmRuleEntity>();

    public AppDbContext(DbContextOptions options) : base(options) { }
}

// AlarmEventEntity — 数据库映射实体（与 Domain.Alarm 分离）
// 字段与现有 alarm_events 表一一对应
// 在 Infrastructure 层做 Entity ↔ Domain 转换
```

### 4.4 AlarmService 重构策略

**原则：API 行为不变，内部实现委托给 Domain**

```
重构前：
  Controller → Service → Dapper Repository → PostgreSQL

重构后：
  Controller → Service → Domain (聚合根) → Infrastructure (EF Core Repository) → PostgreSQL
                                     ↓
                              MediatR 发布领域事件
```

**改造步骤**：

| 步骤 | 改动 | 影响范围 |
|------|------|---------|
| 1 | 新增 Domain + Infrastructure 项目 | 无影响 |
| 2 | AlarmService 引用 Domain + Infrastructure | 无影响 |
| 3 | AlarmEventRepository 从 Dapper → EF Core | 替换实现，接口不变 |
| 4 | AlarmRuleRepository 从 Dapper → EF Core | 替换实现，接口不变 |
| 5 | AlarmQueryService 内部改用 Domain 聚合根 | 业务逻辑不变 |
| 6 | AlarmRuleService 校验逻辑委托给 DomainService | 业务逻辑不变 |
| 7 | MediatR 领域事件发布 + 现有 RabbitMQ 发布桥接 | 发布行为不变 |
| 8 | 移除 Dapper 依赖 | 清理 |

**领域事件 → RabbitMQ 桥接**：

```csharp
// 在 AlarmService 中注册 MediatR 通知处理
public class AlarmTriggeredEventHandler : INotificationHandler<AlarmTriggeredEvent>
{
    // 桥接：领域事件 → 现有 RabbitMQ 发布
    // 保持现有 AlarmEventPublisher 不变
    public async Task Handle(AlarmTriggeredEvent evt, CancellationToken ct) { ... }
}
```

### 4.5 验证标准

- 重构后 AlarmService 所有 API 行为不变（可运行现有前端验证）
- EF Core Migration 可从空库重建完整 schema
- 领域事件（AlarmTriggered/Cleared）通过 MediatR 正确发布
- RabbitMQ 报警消息格式不变，WebApi/前端无需改动

---

## 5. 6.2 维修工单系统

### 5.1 AmGatewayCloud.WorkOrderService — 工单业务服务

```
src/AmGatewayCloud.WorkOrderService/
├── AmGatewayCloud.WorkOrderService.csproj   # Web SDK + Npgsql + Dapper + RabbitMQ
├── Program.cs                               # WebApplication 入口 + DI + 数据库初始化
├── appsettings.json
├── Configuration/
│   └── WorkOrderServiceConfig.cs            # 工单配置 + 数据库/MQ 配置
├── Controllers/
│   └── WorkOrdersController.cs              # 工单 CRUD API
├── Models/
│   └── WorkOrder.cs                         # 工单模型 + WorkOrderStatus 枚举
├── Services/
│   ├── AlarmEventConsumer.cs                # RabbitMQ 消费 → 自动创建工单 (BackgroundService)
│   ├── WorkOrderQueryService.cs             # 工单查询/分配/完成
│   └── HealthChecks.cs                      # PG + RMQ 健康检查
└── Infrastructure/
    ├── RabbitMqConnectionManager.cs         # RabbitMQ 连接管理（复用现有模式）
    └── WorkOrderDbInitializer.cs            # 数据库建表
```

### 5.2 数据模型

#### WorkOrder — 工单

```sql
CREATE TABLE work_orders (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    alarm_id        UUID NOT NULL,                          -- 关联报警事件 ID
    tenant_id       TEXT NOT NULL,
    factory_id      TEXT NOT NULL,
    workshop_id     TEXT,
    device_id       TEXT NOT NULL,
    title           TEXT NOT NULL,                          -- 工单标题（从报警信息生成）
    description     TEXT,                                   -- 工单描述（含报警详情）
    level           TEXT NOT NULL DEFAULT 'Warning',        -- 继承报警级别
    status          TEXT NOT NULL DEFAULT 'Pending',         -- Pending/InProgress/Completed
    assignee        TEXT,                                   -- 分配给谁
    assigned_at     TIMESTAMPTZ,                            -- 分配时间
    completed_at    TIMESTAMPTZ,                            -- 完成时间
    completed_by    TEXT,                                   -- 完成人
    completion_note TEXT,                                   -- 完成备注
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_work_orders_lookup
    ON work_orders (tenant_id, factory_id, status, created_at DESC);
CREATE INDEX idx_work_orders_alarm
    ON work_orders (alarm_id);
```

#### 工单状态流转

```
自动创建               分配                完成
┌──────────┐      ┌──────────┐      ┌──────────┐
│ Pending  │─────►│InProgress│─────►│Completed │
└──────────┘      └──────────┘      └──────────┘
                        │
                        │ 可重新分配
                        └──► 更换 assignee
```

- **Pending → InProgress**：分配维修人员
- **InProgress → Completed**：维修完成，填写完成备注
- **InProgress → InProgress**：重新分配（换人）

#### 工单标题生成规则

```
报警工单: {RuleName} - {DeviceId} ({Level})
例: 报警工单: 高温严重 - device-001 (Critical)
```

### 5.3 RabbitMQ 消费逻辑

#### AlarmEventConsumer

```csharp
public class AlarmEventConsumer : BackgroundService
{
    // 订阅 Queue: amgateway.workorder.alarm-events
    // Binding: alarm.# → amgateway.alarms exchange
    
    // 消费逻辑：
    // 1. 收到 AlarmEventMessage
    // 2. 仅处理 Status == "Active" 的报警（Cleared/Suppressed 不创建工单）
    // 3. 检查同一 alarm_id 是否已有工单（防重复）
    // 4. 生成工单标题、描述
    // 5. 写入 PostgreSQL work_orders 表
}
```

**过滤规则**：
- `Status == "Active"` → 创建工单
- `Status == "Cleared"` / `"Suppressed"` → 忽略（工单已存在的不受影响）
- 同一 `alarm_id` 已有工单 → 跳过（幂等）

### 5.4 API 设计

```
GET    /api/workorders              分页查询工单（支持 factoryId/status/assignee 过滤）
GET    /api/workorders/{id}         查询单个工单
POST   /api/workorders/{id}/assign  分配工单（Pending → InProgress）
POST   /api/workorders/{id}/complete 完成工单（InProgress → Completed）
GET    /api/workorders/summary      工单状态汇总（Pending/InProgress/Completed 计数）
```

#### 请求/响应 DTO

```csharp
// Shared/DTOs/WorkOrderDto.cs
public class WorkOrderDto
{
    public Guid Id { get; set; }
    public Guid AlarmId { get; set; }
    public string TenantId { get; set; }
    public string FactoryId { get; set; }
    public string? WorkshopId { get; set; }
    public string DeviceId { get; set; }
    public string Title { get; set; }
    public string? Description { get; set; }
    public string Level { get; set; }
    public string Status { get; set; }
    public string? Assignee { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public string? CompletionNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// Shared/DTOs/WorkOrderRequests.cs
public class AssignWorkOrderRequest
{
    public string Assignee { get; set; } = string.Empty;
}

public class CompleteWorkOrderRequest
{
    public string CompletedBy { get; set; } = string.Empty;
    public string? CompletionNote { get; set; }
}
```

### 5.5 WebApi 扩展

**YARP 新增路由**：

```json
{
  "workorders-route": {
    "ClusterId": "workorder-service",
    "Match": { "Path": "/api/workorders/{**catch-all}" }
  }
}
```

**新增 Cluster**：

```json
{
  "workorder-service": {
    "Destinations": {
      "default": { "Address": "http://workorder-service:5002" }
    }
  }
}
```

**SignalR 扩展**（可选，后续迭代）：
- 工单创建/状态变更推送
- 复用现有 AlarmHub 或新建 WorkOrderHub

### 5.6 前端新增

#### 工单管理页

```
src/AmGatewayCloud.Web/src/
├── views/
│   └── WorkOrdersView.vue         # 工单列表页
├── api/
│   └── workorders.ts              # 工单 API 客户端
├── stores/
│   └── workorder.ts               # 工单 Pinia store
└── components/
    └── WorkOrderActionModal.vue   # 分配/完成操作弹窗
```

#### 侧边栏菜单扩展

`AppLayout.vue` 新增菜单项：

```
┌──────────────────────┐
│ 📊 报警看板           │  (现有)
│ 🔔 报警管理           │  (现有)
│ 🔧 规则管理           │  (现有)
│ 📋 维修工单    ← 新增  │
│ 💻 设备状态           │  (现有)
└──────────────────────┘
```

#### 工单列表页功能

- 分页列表，支持按工厂/状态/负责人过滤
- 状态标签颜色：Pending(橙) / InProgress(蓝) / Completed(绿)
- 操作按钮：分配(Pending) / 完成(InProgress)
- 点击行展开详情（关联报警信息）

### 5.7 配置

#### WorkOrderService — appsettings.json

```json
{
  "WorkOrderService": {
    "TenantId": "default",
    "AutoCreateOnAlarm": true,
    "PostgreSql": {
      "Host": "localhost",
      "Port": 5432,
      "Database": "amgateway_business",
      "Username": "sa",
      "Password": "sa",
      "SslMode": "Disable"
    },
    "RabbitMq": {
      "HostName": "localhost",
      "Port": 5672,
      "UseSsl": false,
      "VirtualHost": "/business",
      "Username": "guest",
      "Password": "guest",
      "Exchange": "amgateway.alarms",
      "QueueName": "amgateway.workorder.alarm-events",
      "ReconnectDelayMs": 5000,
      "MaxReconnectDelayMs": 60000
    }
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:5002" }
    }
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.AspNetCore": "Warning",
        "RabbitMQ": "Warning"
      }
    },
    "WriteTo": [{ "Name": "Console" }]
  }
}
```

### 5.8 部署

#### Dockerfile — WorkOrderService

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/AmGatewayCloud.Shared/AmGatewayCloud.Shared.csproj ./AmGatewayCloud.Shared/
RUN dotnet restore ./AmGatewayCloud.Shared/AmGatewayCloud.Shared.csproj
COPY src/AmGatewayCloud.WorkOrderService/AmGatewayCloud.WorkOrderService.csproj ./AmGatewayCloud.WorkOrderService/
RUN dotnet restore ./AmGatewayCloud.WorkOrderService/AmGatewayCloud.WorkOrderService.csproj
COPY src/AmGatewayCloud.Shared/ ./AmGatewayCloud.Shared/
COPY src/AmGatewayCloud.WorkOrderService/ ./AmGatewayCloud.WorkOrderService/
RUN dotnet publish ./AmGatewayCloud.WorkOrderService/AmGatewayCloud.WorkOrderService.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5002
ENTRYPOINT ["dotnet", "AmGatewayCloud.WorkOrderService.dll"]
```

#### docker-compose 新增服务

```yaml
  workorder-service:
    build:
      context: .
      dockerfile: docker/workorder-service/Dockerfile
    container_name: amgw-workorder-service
    ports:
      - "5002:5002"
    depends_on:
      rabbitmq-init:
        condition: service_completed_successfully
      timescaledb:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      WorkOrderService__TenantId: default
      WorkOrderService__PostgreSql__Host: timescaledb
      WorkOrderService__RabbitMq__HostName: rabbitmq
    restart: unless-stopped
```

#### WebApi 配置扩展

```yaml
  webapi:
    environment:
      # 新增
      ReverseProxy__Clusters__workorder-service__Destinations__default__Address: "http://workorder-service:5002"
```

---

## 6. RabbitMQ 拓扑（Phase 6 完成后）

```
RabbitMQ 实例
├── vhost: /pipeline                     ← 管道层（Phase 1-3）
│   ├── Exchange: amgateway.topic
│   └── Queue: amgateway.factory-a / factory-b
│
└── vhost: /business                     ← 业务层
    ├── Exchange: amgateway.alarms
    ├── Queue: amgateway.alarm-notifications       → WebApi (现有，binding: alarm.#)
    └── Queue: amgateway.workorder.alarm-events    → WorkOrderService (新增，binding: alarm.#)
```

---

## 7. 项目清单（Phase 6 后）

| 项目 | 阶段 | 职责 |
|------|------|------|
| AmGatewayCloud.Simulator | 1 | Modbus TCP 从站模拟器 |
| AmGatewayCloud.Collector.Modbus | 1 | Modbus 采集器 + MQTT 推送 |
| AmGatewayCloud.Collector.OpcUa | 1 | OPC UA 采集器 + MQTT 推送 |
| AmGatewayCloud.EdgeGateway | 2 | 边缘聚合：MQTT→InfluxDB+RabbitMQ |
| AmGatewayCloud.CloudGateway | 3 | 云端聚合：RabbitMQ→PostgreSQL+时序库 |
| AmGatewayCloud.AlarmService | 4→6 | 报警业务（重构：委托 Domain） |
| AmGatewayCloud.WebApi | 4→6 | BFF（YARP + SignalR，新增工单路由） |
| AmGatewayCloud.Shared | 4→6 | 共享契约库（新增 WorkOrder DTOs） |
| AmGatewayCloud.Web | 5→6 | Vue 3 前端（新增工单页面） |
| **AmGatewayCloud.Domain** | **6** | **领域层（聚合根 + 领域事件）** |
| **AmGatewayCloud.Infrastructure** | **6** | **EF Core 仓储** |
| **AmGatewayCloud.WorkOrderService** | **6** | **维修工单业务服务** |

---

## 8. 实施顺序

| 步骤 | 内容 | 依赖 |
|------|------|------|
| **1** | 新增 `AmGatewayCloud.Domain` 项目 | 无 |
| **2** | 新增 `AmGatewayCloud.Infrastructure` 项目 | 步骤 1 |
| **3** | AlarmService 重构：引用 Domain + Infrastructure | 步骤 1、2 |
| **4** | Dapper → EF Core 逐步替换 | 步骤 3 |
| **5** | MediatR 领域事件发布 + RabbitMQ 桥接 | 步骤 4 |
| **6** | 验证：重构后 API 行为不变 | 步骤 5 |
| **7** | 新增 `AmGatewayCloud.WorkOrderService` 项目 | 步骤 5（消费报警事件） |
| **8** | Shared 新增工单 DTOs | 步骤 7 |
| **9** | WebApi 新增 YARP 路由 + Cluster | 步骤 8 |
| **10** | 前端新增工单页面 | 步骤 9 |
| **11** | Docker 部署 + 端到端验证 | 步骤 10 |

---

## 9. 验证标准

### 6.1 DDD 提炼

- [ ] 重构后 AlarmService 所有 API 行为不变
- [ ] EF Core Migration 可从空库重建完整 schema
- [ ] 领域事件（AlarmTriggered/Cleared）通过 MediatR 正确发布
- [ ] RabbitMQ 报警消息格式不变，WebApi/前端无需改动

### 6.2 维修工单

- [ ] 报警触发 → WorkOrderService 自动创建工单
- [ ] 工单按 TenantId 隔离
- [ ] 同一报警不重复创建工单（幂等）
- [ ] 工单状态流转正确（Pending → InProgress → Completed）
- [ ] 前端工单页可查看/分配/完成工单
- [ ] Docker 部署后全链路正常
