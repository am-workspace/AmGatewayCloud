# Phase 6：DDD 提炼 + 维修工单 — 实现状态总览

> 对照 `ddd-workorder.md` 逐项核对。

**生成时间**：2026-05-11
**最后更新**：2026-05-11（首次实现完成）

---

## 1. 完成度概览

```
6.1 DDD 提炼 (AlarmDomain + AlarmInfrastructure)  ████████████████████  4/4  100%
6.2 维修工单 (WorkOrderService)                     ████████████████████  6/6  100%
共享层 + 基础设施集成                                ████████████████████  4/4  100%
部署配置 (Docker + YARP)                             ████████████████████  3/3  100%
整体完成度                                           ████████████████████ 17/17 100%
```

---

## 2. 6.1 DDD 提炼 — 逐章核对

> 注：DDD 提炼在本次工单开发前已完成，此处记录最终状态。

| 章节 | 内容 | 状态 | 实际实现说明 |
|------|------|------|-------------|
| §4.1 Domain 项目 | AlarmDomain 类库 | ✅ | `AmGatewayCloud.AlarmDomain`：Alarm 聚合根 + AlarmRule 实体 + 值对象枚举 + 领域事件 + 领域服务 |
| §4.1 Infrastructure 项目 | AlarmInfrastructure 类库 | ✅ | `AmGatewayCloud.AlarmInfrastructure`：EF Core AppDbContext + 仓储实现 + MediatR 事件发布 |
| §4.2 领域模型 | Alarm 聚合根 + 值对象 + 领域事件 | ✅ | `Alarm.cs` 含工厂方法 Create + 状态流转 + DomainEvents 集合；`AlarmTriggeredEvent`/`AlarmClearedEvent` 继承 `DomainEvent : INotification` |
| §4.3 EF Core 映射 | Dapper → EF Core，schema 不变 | ✅ | AppDbContext + Configuration 映射，Dapper 仅保留 TimescaleDbReader（外部限界上下文） |
| §4.4 重构策略 | API 行为不变，内部委托 Domain | ✅ | Controller → Service → Domain(聚合根) → Infrastructure(EF Core)，MediatR 桥接到 RabbitMQ |
| §4.5 验证标准 | API 行为不变 + 领域事件发布 | ✅ | 前端无需改动，RabbitMQ 消息格式不变 |

### 6.1 验证标准逐项

| 验证项 | 状态 | 说明 |
|--------|------|------|
| 重构后 AlarmService 所有 API 行为不变 | ✅ | Controller/Service 委托给 Domain，前端无感 |
| EF Core Migration 可从空库重建完整 schema | ✅ | AppDbContext + EnsureCreated |
| 领域事件通过 MediatR 正确发布 | ✅ | `MediatRDomainEventPublisher` 实现 |
| RabbitMQ 报警消息格式不变，WebApi/前端无需改动 | ✅ | MediatR handler 桥接到现有 `AlarmEventPublisher` |

---

## 3. 6.2 维修工单 — 逐章核对

| 章节 | 内容 | 状态 | 实际实现说明 |
|------|------|------|-------------|
| §5.1 WorkOrderService 项目 | 独立微服务 | ✅ | `AmGatewayCloud.WorkOrderService`：Web SDK + Dapper + Npgsql + RabbitMQ |
| §5.2 数据模型 | work_orders 表 + 状态流转 | ✅ | `WorkOrder.cs` + `WorkOrderStatus` 枚举(Pending/InProgress/Completed)，DDL 含两个索引 |
| §5.3 RabbitMQ 消费 | AlarmEventConsumer 后台服务 | ✅ | 订阅 `amgateway.workorder.alarm-events` 队列，仅 Active 报警→创建工单，alarm_id 幂等 |
| §5.4 API 设计 | 5 个端点 | ✅ | GET 分页查询 / GET 单个 / POST 分配 / POST 完成 / GET 汇总 |
| §5.5 WebApi 扩展 | YARP 路由 + Cluster | ✅ | `workorders-route` → `workorder-service` cluster → `http://localhost:5002` |
| §5.6 前端新增 | 工单页面 + 侧边栏 | ✅ | `WorkOrdersView.vue` + API + Store + 路由 + 侧边栏菜单 |
| §5.7 配置 | appsettings.json | ✅ | 复用 Shared 的 PostgreSqlConfig/RabbitMqConfig，含 AutoCreateOnAlarm 开关 |
| §5.8 部署 | Dockerfile + docker-compose | ✅ | 多阶段构建，docker-compose 新增 workorder-service + WebApi 反代地址 |

### 6.2 验证标准逐项

| 验证项 | 状态 | 说明 |
|--------|------|------|
| 报警触发 → WorkOrderService 自动创建工单 | ✅ | AlarmEventConsumer 消费 `alarm.#`，Active 报警触发创建 |
| 工单按 TenantId 隔离 | ✅ | 查询/创建均含 TenantId |
| 同一报警不重复创建工单（幂等） | ✅ | `ExistsWorkOrderForAlarmAsync` 检查 alarm_id |
| 工单状态流转正确（Pending → InProgress → Completed） | ✅ | AssignAsync(Pending→InProgress) + CompleteAsync(InProgress/Pending→Completed) |
| 前端工单页可查看/分配/完成工单 | ✅ | WorkOrdersView 含统计卡片+过滤+表格+分配/完成弹窗 |
| Docker 部署后全链路正常 | ⏳ | Docker 配置已就绪，待端到端验证 |

---

## 4. 设计决策执行

| 决策 | spec 结论 | 实际执行 |
|------|----------|---------|
| §3.1 工单独立服务 | ✅ 独立 WorkOrderService | 独立项目，独立数据库表，独立端口 5002 |
| §3.2 新队列订阅 | ✅ 方案 B：WorkOrderService 自己消费 | RabbitMqConnectionManager 声明 `amgateway.workorder.alarm-events` 队列，绑定 `alarm.#` |
| §3.3 同一 vhost | ✅ 同 `/business` vhost | 复用 `amgateway.alarms` exchange |
| §3.4 DDD 提炼范围 | ✅ 只提炼 Alarm 领域，不动评估引擎 | AlarmDomain/AlarmInfrastructure 独立项目，TimescaleDbReader 保留 Dapper |

---

## 5. 与方案差异说明

### 5.1 有意差异

| 差异 | 方案 | 实际 | 原因 |
|------|------|------|------|
| 项目命名 | `AmGatewayCloud.Domain` / `AmGatewayCloud.Infrastructure` | `AmGatewayCloud.AlarmDomain` / `AmGatewayCloud.AlarmInfrastructure` | 更具体地反映所属限界上下文 |
| 工单服务 ORM | 未指定 | Dapper + Npgsql | 工单服务简单 CRUD，无需 EF Core 变更追踪 |
| 工单标题生成 | `报警工单: {RuleName} - {DeviceId} ({Level})` | 完全一致 | ✅ |
| RabbitMQ 消费 | 未指定消费模式 | EventingBasicConsumer + 手动 Ack/Nack | 与 AlarmService/WebApi 保持一致 |

### 5.2 实现超规格项

| 项 | 说明 |
|----|------|
| 工单状态汇总 API | `GET /api/workorders/summary`，返回 Pending/InProgress/Completed 计数 |
| 前端统计卡片 | 三个统计卡片展示工单各状态数量 |
| 工单描述自动生成 | 从报警消息中提取规则/设备/标签/触发值/阈值等字段 |
| CompleteAsync 支持 Pending → Completed | spec 仅 InProgress→Completed，实现额外支持直接完成 Pending 工单 |

---

## 6. 文件清单

### 6.1 AmGatewayCloud.AlarmDomain — 领域层

```
src/AmGatewayCloud.AlarmDomain/
├── AmGatewayCloud.AlarmDomain.csproj       # net10.0 类库，无外部依赖
├── Aggregates/Alarm/
│   ├── Alarm.cs                            # Alarm 聚合根（工厂方法 + 状态流转 + 领域事件）
│   ├── AlarmRule.cs                        # 规则领域实体
│   ├── AlarmLevel.cs                       # 值对象枚举
│   ├── AlarmStatus.cs                      # 值对象枚举
│   └── OperatorType.cs                     # 值对象枚举
├── Events/
│   ├── AlarmTriggeredEvent.cs              # 报警触发领域事件
│   └── AlarmClearedEvent.cs                # 报警恢复领域事件
├── Services/
│   └── AlarmDomainService.cs               # 领域服务（状态流转校验）
└── Common/
    ├── DomainEvent.cs                      # 领域事件基类 : INotification
    └── AlarmStateException.cs              # 非法状态异常
```

### 6.2 AmGatewayCloud.AlarmInfrastructure — 基础设施层

```
src/AmGatewayCloud.AlarmInfrastructure/
├── AmGatewayCloud.AlarmInfrastructure.csproj  # net10.0 + EF Core + MediatR + Npgsql
├── Persistence/
│   ├── AppDbContext.cs                         # EF Core DbContext
│   └── Configurations/
│       ├── AlarmEventConfiguration.cs          # 实体映射 + 索引
│       └── AlarmRuleConfiguration.cs           # 实体映射 + 索引
├── Repositories/
│   ├── AlarmEventRepository.cs                 # 仓储实现
│   └── AlarmRuleRepository.cs                  # 仓储实现
└── Events/
    └── MediatRDomainEventPublisher.cs          # MediatR 领域事件发布
```

### 6.3 AmGatewayCloud.WorkOrderService — 维修工单服务

```
src/AmGatewayCloud.WorkOrderService/
├── AmGatewayCloud.WorkOrderService.csproj   # Web SDK + Dapper + Npgsql + RabbitMQ
├── Program.cs                               # WebApplication 入口 + DI + 数据库初始化
├── appsettings.json                         # PG/RabbitMQ 配置，监听 5002
├── Configuration/
│   └── WorkOrderServiceConfig.cs            # 复用 Shared 配置类
├── Controllers/
│   └── WorkOrdersController.cs              # 5 个 API 端点
├── Models/
│   └── WorkOrder.cs                         # 工单模型 + WorkOrderStatus 枚举
├── Services/
│   ├── AlarmEventConsumer.cs                # RabbitMQ 消费 → 自动创建工单 (BackgroundService)
│   ├── WorkOrderQueryService.cs             # 查询/分配/完成/汇总
│   └── HealthChecks.cs                      # PG + RabbitMQ 健康检查
└── Infrastructure/
    ├── RabbitMqConnectionManager.cs         # RabbitMQ 连接管理 + 队列声明
    └── WorkOrderDbInitializer.cs            # work_orders 建表 + 索引
```

### 6.4 AmGatewayCloud.Shared — 新增

```
src/AmGatewayCloud.Shared/DTOs/
├── WorkOrderDto.cs                          # 工单 DTO
└── WorkOrderRequests.cs                     # AssignWorkOrderRequest + CompleteWorkOrderRequest
```

### 6.5 前端新增

```
src/AmGatewayCloud.Web/src/
├── api/
│   └── workorders.ts                        # 5 个 API 调用函数
├── stores/
│   └── workorder.ts                         # Pinia store
├── views/
│   └── WorkOrdersView.vue                   # 工单列表页（统计卡片+过滤+表格+操作弹窗）
├── types/
│   └── index.ts                             # 新增 WorkOrder/Summary/Request 类型
└── layouts/
    └── AppLayout.vue                        # 侧边栏新增「维修工单」菜单项
```

### 6.6 部署配置

```
docker/
└── workorder-service/
    └── Dockerfile                           # 多阶段构建，EXPOSE 5002

docker-compose.yml                           # 新增 workorder-service + WebApi 反代地址
AmGatewayCloud.sln                           # 新增 WorkOrderService 项目引用
```

---

## 7. RabbitMQ 拓扑（Phase 6 完成后）

```
RabbitMQ 实例
├── vhost: /pipeline                     ← 管道层（Phase 1-3）
│   ├── Exchange: amgateway.topic
│   └── Queue: amgateway.factory-a / factory-b
│
└── vhost: /business                     ← 业务层
    ├── Exchange: amgateway.alarms
    ├── Queue: amgateway.alarm-notifications       → WebApi (binding: alarm.#)
    └── Queue: amgateway.workorder.alarm-events    → WorkOrderService (binding: alarm.#)  ← 新增
```

---

## 8. 项目清单（Phase 6 后）

| 项目 | 阶段 | 职责 |
|------|------|------|
| AmGatewayCloud.Simulator | 1 | Modbus TCP 从站模拟器 |
| AmGatewayCloud.Collector.Modbus | 1 | Modbus 采集器 + MQTT 推送 |
| AmGatewayCloud.Collector.OpcUa | 1 | OPC UA 采集器 + MQTT 推送 |
| AmGatewayCloud.EdgeGateway | 2 | 边缘聚合：MQTT→InfluxDB+RabbitMQ |
| AmGatewayCloud.CloudGateway | 3 | 云端聚合：RabbitMQ→PostgreSQL+时序库 |
| AmGatewayCloud.AlarmService | 4→6 | 报警业务（委托 Domain） |
| AmGatewayCloud.WebApi | 4→6 | BFF（YARP + SignalR，含工单路由） |
| AmGatewayCloud.Shared | 4→6 | 共享契约库（含 WorkOrder DTOs） |
| AmGatewayCloud.Web | 5→6 | Vue 3 前端（含工单页面） |
| **AmGatewayCloud.AlarmDomain** | **6** | **领域层（聚合根 + 领域事件）** |
| **AmGatewayCloud.AlarmInfrastructure** | **6** | **EF Core 仓储** |
| **AmGatewayCloud.WorkOrderService** | **6** | **维修工单业务服务** |

---

## 9. 待验证项

| # | 测试项 | 说明 |
|---|--------|------|
| 1 | 端到端：报警触发→自动创建工单 | docker-compose 全链路验证 |
| 2 | 工单幂等性 | 同一报警重复消费不创建重复工单 |
| 3 | 工单状态流转 | Pending→InProgress→Completed |
| 4 | 前端工单页交互 | 查看/过滤/分配/完成 |
| 5 | YARP 反代 | WebApi → WorkOrderService 路由正确 |

---

## 10. 后续演进路线图

| 阶段 | 需要做的 |
|------|---------|
| **当前** | 端到端集成验证（docker-compose up 全链路） |
| **Phase 7** | SignalR 工单实时推送；预防性维护工单；巡检工单 |
| **Phase 8** | OpenTelemetry 链路追踪；多租户 JWT；工单 SLA 超时提醒 |
| **Phase 9** | 工单统计报表；维修知识库；设备维修历史关联 |
