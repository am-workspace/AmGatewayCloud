# AmGatewayCloud - 从单体网关到工业微服务 SaaS

> 基于 AmGateway 的分布式演进路线图

## 项目愿景

以工业物联网为场景，从最小闭环出发，逐步引入分布式/微服务技术，最终构建一个可多租户运营的工业 SaaS 平台。

## 核心原则

- **每一步必须闭环**：可运行、可验证、有产出
- **渐进式复杂度**：每次只引入 1-2 个新技术
- **领域先行**：带着 DDD 思维写代码，后期提炼而非推倒重来
- **多租户伏笔**：从阶段 1 起，关键实体预留 `TenantId`

---

## 阶段 1：模拟车 — 最小闭环

**目标**：写一个 .NET 控制台程序，模拟 PLC 设备（Modbus TCP 从站），定时产生温度、转速数据；网关连接它，将数据打印到控制台。

**新增技术**：无（复用 AmGateway 现有 Modbus 驱动）

**产出**：
- `AmGatewayCloud.Simulator` — Modbus TCP 从站模拟器（温度、转速）
- 网关连接模拟器，控制台实时打印采集数据

**验证标准**：启动模拟器 → 启动网关 → 控制台看到温度/转速数值变化

---

## 阶段 2：时序库 + 可视化

**目标**：网关将数据存入 InfluxDB，Grafana 连接 InfluxDB 画出第一张温度趋势图。

**新增技术**：InfluxDB、Grafana

**产出**：
- 网关 InfluxDB 发布器配置（复用 AmGateway.Publisher.InfluxDB）
- Grafana Dashboard — 温度 & 转速趋势图

**验证标准**：Grafana 上看到实时更新的温度曲线

---

## 阶段 3a：消息队列 — 跨进程管道

**目标**：网关将数据推入 RabbitMQ，独立消费者读取并打印。替换 Channel\<T\> 为跨进程消息管道。

**新增技术**：RabbitMQ

**产出**：
- `AmGatewayCloud.Publisher.RabbitMQ` — 网关端 RabbitMQ 发布器插件
- `AmGatewayCloud.Consumer` — 简单控制台消费者

**验证标准**：启动网关 → RabbitMQ 队列有消息 → 消费者控制台打印数据

---

## 阶段 3b：报警服务 — 业务逻辑的起点

**目标**：消费者升级为报警服务，温度超限时生成报警，存入 PostgreSQL，SignalR 推送到网页。

**新增技术**：PostgreSQL、SignalR、简单前端页面

**产出**：
- `AmGatewayCloud.AlarmService` — 报警微服务
  - 消费 RabbitMQ 数据
  - 阈值判断 → 生成 Alarm 记录 → 写入 PostgreSQL
  - SignalR Hub 推送实时报警
- `AmGatewayCloud.Web` — 简单报警看板页面

**领域模型（初版）**：
- `Equipment` — 设备（Id, Name, TenantId）
- `Alarm` — 报警（Id, EquipmentId, Level, Message, Timestamp, TenantId）

**验证标准**：温度超限 → 网页实时弹出报警通知 → PostgreSQL 中可查报警记录

---

## 阶段 4：DDD 提炼 + 维修工单

**目标**：报警出现后，自动/手动生成维修工单。用 DDD 思想提炼领域模型，EF Core 实现仓储。

**新增技术**：EF Core（如果阶段 3b 用的是原始 SQL，这里升级）

**产出**：
- `AmGatewayCloud.Domain` — 领域层
  - 聚合根：`Equipment`、`Alarm`、`WorkOrder`
  - 值对象：`AlarmLevel`、`WorkOrderStatus`
  - 领域事件：`AlarmTriggeredEvent` → 自动创建工单
- `AmGatewayCloud.Infrastructure` — EF Core 仓储实现
- 工单管理 API + 网页

**领域模型（提炼后）**：
```
Equipment (聚合根)
├── Id, Name, Location, TenantId
└── Alarms (集合)

Alarm (聚合根)
├── Id, EquipmentId, Level, Message, Timestamp, IsAcknowledged, TenantId
└── WorkOrders (集合)

WorkOrder (聚合根)
├── Id, AlarmId, EquipmentId, Status, Assignee, CreatedAt, TenantId
└── Status: Pending → InProgress → Completed
```

**验证标准**：报警触发 → 自动生成工单 → 网页上可查看/处理工单

---

## 阶段 5：容器化 + 可观测性

**目标**：全栈 Docker Compose 编排 + 结构化日志 + 分布式追踪。

**新增技术**：Docker Compose、Serilog + Seq、OpenTelemetry + Jaeger/Zipkin

**产出**：
- `docker-compose.yml` — 编排所有服务
  - AmGatewayCloud.Simulator
  - AmGatewayCloud.Gateway（网关）
  - RabbitMQ
  - InfluxDB + Grafana
  - PostgreSQL
  - AmGatewayCloud.AlarmService
  - AmGatewayCloud.Web
  - Seq（日志）
  - Jaeger（追踪）
- Serilog 结构化日志 → Seq
- OpenTelemetry 追踪：设备报警 → RabbitMQ → 报警服务 → 工单生成 完整链路

**验证标准**：
- `docker compose up` 一键启动全部服务
- Jaeger 中可查 "设备报警→工单生成" 完整调用链
- Seq 中可按结构化字段搜索日志

---

## 阶段 6：多租户

**目标**：平台卖给多个工厂，不同工厂登录只看到自己的设备和报警。

**新增技术**：JWT + 租户识别中间件（阶段 3b 已有 JWT 基础）

**产出**：
- 租户识别中间件：从 JWT 提取 TenantId，注入请求上下文
- 数据库查询自动附加 `WHERE TenantId = @currentTenant`
- EF Core Global Query Filter 实现透明隔离
- 网关层按连接/配置识别租户，数据带 TenantId 标签
- 租户管理 API：创建租户、分配设备

**前置伏笔（从阶段 1 开始）**：
- 所有实体预留 `TenantId` 字段（可先为默认值或 nullable）
- 数据库表包含 `tenant_id` 列
- API 接口预留 `X-Tenant-Id` header 支持

**验证标准**：
- 工厂 A 登录 → 只看到自己的设备和报警
- 工厂 B 登录 → 看到的是另一套数据
- 交叉访问 → 403 Forbidden

---

## 技术栈总览

| 分类 | 技术选型 |
|------|---------|
| 运行时 | .NET 10 |
| 数据采集 | Modbus TCP (复用 AmGateway 驱动) |
| 消息队列 | RabbitMQ |
| 时序数据库 | InfluxDB 2.x |
| 关系数据库 | PostgreSQL |
| ORM | EF Core |
| 实时通信 | SignalR |
| 缓存/共享状态 | Redis（阶段 5+ 引入） |
| 容器化 | Docker + Docker Compose |
| 日志 | Serilog → Seq |
| 追踪 | OpenTelemetry → Jaeger |
| 可视化 | Grafana |
| 认证 | JWT |

## 与 AmGateway 的关系

```
AmGateway (边缘)                    AmGatewayCloud (云端)
┌─────────────────┐                ┌──────────────────────────────┐
│ Modbus/OPC UA   │    MQTT/AMQP   │ RabbitMQ → AlarmService      │
│ Driver Plugin    │──────────────► │              → WorkOrder      │
│ Channel Pipeline │                │ InfluxDB → Grafana           │
│ SQLite Config    │                │ PostgreSQL + EF Core          │
│ MQTT Publisher   │                │ SignalR → Web Dashboard      │
└─────────────────┘                │ Docker Compose 全编排         │
                                   │ 多租户 SaaS                   │
                                   └──────────────────────────────┘
```

AmGateway 负责**边缘采集**，AmGatewayCloud 负责**云端聚合与业务**。两者通过 MQTT/RabbitMQ 解耦。
