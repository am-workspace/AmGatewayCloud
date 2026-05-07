# AmGatewayCloud - 从边缘采集到工业微服务 SaaS

> 基于 AmGateway 的分布式演进路线图

## 项目愿景

以工业物联网为场景，构建**多厂多车间**的边缘-云端协同架构，从最小闭环出发，逐步引入分布式/微服务技术，最终形成可多租户运营的工业 SaaS 平台。

## 整体架构

```
公司 (TenantId)
├── 工厂 A (FactoryId) ─── Queue: amgateway.factory-a
│   ├── 车间1 (WorkshopId) → EdgeHub-A → 50+ 采集器
│   ├── 车间2 (WorkshopId) → EdgeHub-B → 50+ 采集器
│   └── 车间3 (WorkshopId) → EdgeHub-C → 50+ 采集器
│
├── 工厂 B (FactoryId) ─── Queue: amgateway.factory-b
│   ├── 车间1 → EdgeHub-D → 50+ 采集器
│   └── 车间2 → EdgeHub-E → 50+ 采集器
│
└── 云端聚合网关
    ├── Consumer-Queue-A → 写库 (factoryId=A)
    ├── Consumer-Queue-B → 写库 (factoryId=B)
    └── 业务服务 (按 TenantId 隔离)
```

```
边缘侧                                    云端
┌──────────────────────────┐        ┌─────────────────────────────────┐
│ 采集器 × N               │        │                                 │
│ (Modbus/OpcUa/未来协议)   │        │  RabbitMQ (一厂一个)             │
│      │ MQTT(局域网)      │        │      │ AMQP                     │
│      ▼                   │        │      ▼                          │
│ 边缘聚合网关 (EdgeHub)   │──WAN──►│  云端聚合网关                    │
│  ├── Local InfluxDB      │        │   ├── PostgreSQL                │
│  ├── 断网时本地暂存       │        │   └── 云端时序库                 │
│  └── 断网恢复后回放       │        │        │                        │
│      │                   │        │        ▼                        │
│      ▼                   │        │   业务服务                      │
│  Grafana(本地看板)        │        │   (报警/工单/看板/分析)          │
└──────────────────────────┘        └─────────────────────────────────┘
```

## 核心原则

- **每一步必须闭环**：可运行、可验证、有产出
- **渐进式复杂度**：每次只引入 1-2 个新技术
- **领域先行**：带着 DDD 思维写代码，后期提炼而非推倒重来
- **多租户伏笔**：从阶段 1 起，关键实体预留 `TenantId`
- **三级隔离**：TenantId（公司）→ FactoryId（工厂）→ WorkshopId（车间）

---

## 阶段 1：模拟车 + 采集器 ✅

**目标**：模拟 PLC 设备，采集器连接并采集数据，推送到 MQTT。

**产出**：
- `AmGatewayCloud.Simulator` — Modbus TCP 从站模拟器
- `AmGatewayCloud.Collector.Modbus` — Modbus 采集器 + MQTT 推送
- `AmGatewayCloud.Collector.OpcUa` — OPC UA 采集器 + MQTT 推送

**验证标准**：启动模拟器/OPC UA 服务器 → 启动采集器 → Mosquitto 订阅收到数据

---

## 阶段 2：边缘聚合网关

**目标**：构建 EdgeHub，订阅局域网 MQTT，本地持久化 + 转发工厂 RabbitMQ + 断网回放。

**新增技术**：InfluxDB 2.x、Grafana、RabbitMQ

**产出**：
- `AmGatewayCloud.EdgeGateway` — 边缘聚合网关
  - MQTT 订阅 `amgateway/#`
  - 命名空间注入（FactoryId / WorkshopId / HubId）
  - 本地 InfluxDB 持久化（批量写入）
  - RabbitMQ 转发（Topic Exchange，路由键含工厂/车间/设备信息）
  - 断网检测 + 水位线追踪（WatermarkTracker）
  - 断网恢复后从 InfluxDB 回放未转发数据
  - 消息去重 ID（`{hubId}-{timestamp}-{sequence}`）
- 本地 Grafana 看板 — 车间级实时数据可视化

**数据流转**：
```
MQTT Subscribe (amgateway/#)
    │
    ▼
反序列化 + 命名空间注入 (FactoryId/WorkshopId/HubId)
    │
    ├──► 写 Local InfluxDB（同步，确保落盘）
    │
    └──► 发 RabbitMQ（异步）
             │
             ├─ 成功 → 更新 watermark
             └─ 失败 → 等网络恢复后从 InfluxDB 回放
```

**路由键格式**：`amgateway.{factoryId}.{workshopId}.{deviceId}.{protocol}`

**领域模型（初版）**：
- `Device` — 设备（Id, Name, FactoryId, WorkshopId, Protocol, TenantId）
- `DataPoint` — 数据点（Tag, Value, ValueType, Quality, Timestamp）
- `DataBatch` — 数据批次（DeviceId, FactoryId, WorkshopId, HubId, Timestamp, Points[]）

**验证标准**：
- 启动采集器 → EdgeHub 订阅到数据 → 本地 InfluxDB 有记录
- EdgeHub 转发到 RabbitMQ → 简单消费者打印数据
- 断开 RabbitMQ → 数据仍写入 InfluxDB → 恢复后自动回放

---

## 阶段 3：云端聚合网关

**目标**：构建云端网关，消费多个工厂的 RabbitMQ，写入 PostgreSQL + 云端时序库，为业务服务打基础。

**新增技术**：PostgreSQL、云端时序库（InfluxDB Cloud / TimescaleDB）

**产出**：
- `AmGatewayCloud.CloudGateway` — 云端聚合网关
  - 多 RabbitMQ 源消费（每个工厂一个独立 Consumer）
  - 消息去重（PostgreSQL UNIQUE 约束或时序库去重）
  - 写入 PostgreSQL（设备注册、数据元信息）
  - 写入云端时序库（时序数据）
  - Consumer 独立重连，互不影响
- 多租户数据隔离基础（TenantId / FactoryId 贯穿）

**领域模型（扩展）**：
- `Factory` — 工厂（Id, Name, TenantId, RabbitMqEndpoint）
- `Workshop` — 车间（Id, Name, FactoryId）
- `Device` — 设备（Id, Name, FactoryId, WorkshopId, Protocol, TenantId）
- `DataBatch` — 数据批次（含 TenantId/FactoryId/WorkshopId/DeviceId）

**验证标准**：
- 两个工厂 EdgeHub 同时转发 → 云端网关消费到全部数据
- PostgreSQL 可按 FactoryId/WorkshopId 查询设备
- 时序库可按设备/时间范围查询历史数据
- 断开工厂 A 的 RabbitMQ → 工厂 B 数据不受影响

---

## 阶段 4：报警服务 — 业务逻辑的起点

**目标**：消费云端时序数据，温度超限时生成报警，存入 PostgreSQL，SignalR 推送到网页。

**新增技术**：SignalR、简单前端页面

**产出**：
- `AmGatewayCloud.AlarmService` — 报警微服务
  - 消费云端数据流（从时序库或 RabbitMQ 二次消费）
  - 阈值判断 → 生成 Alarm 记录 → 写入 PostgreSQL
  - SignalR Hub 推送实时报警
- `AmGatewayCloud.Web` — 简单报警看板页面

**领域模型**：
- `Equipment` — 设备（Id, Name, FactoryId, WorkshopId, TenantId）
- `Alarm` — 报警（Id, EquipmentId, Level, Message, Timestamp, TenantId）

**验证标准**：温度超限 → 网页实时弹出报警通知 → PostgreSQL 中可查报警记录

---

## 阶段 5：DDD 提炼 + 维修工单

**目标**：报警出现后，自动/手动生成维修工单。用 DDD 思想提炼领域模型，EF Core 实现仓储。

**新增技术**：EF Core

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
├── Id, Name, FactoryId, WorkshopId, TenantId
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

## 阶段 6：容器化 + 可观测性

**目标**：全栈 Docker Compose 编排 + 结构化日志 + 分布式追踪。

**新增技术**：Docker Compose、Serilog + Seq、OpenTelemetry + Jaeger/Zipkin

**产出**：
- `docker-compose.yml` — 编排所有服务
  - AmGatewayCloud.Simulator
  - AmGatewayCloud.Collector.Modbus / OpcUa
  - Mosquitto（边缘 MQTT Broker）
  - AmGatewayCloud.EdgeGateway + InfluxDB + Grafana（边缘侧）
  - RabbitMQ（工厂级）
  - AmGatewayCloud.CloudGateway + PostgreSQL + 云时序库（云端）
  - AmGatewayCloud.AlarmService
  - AmGatewayCloud.Web
  - Seq（日志）
  - Jaeger（追踪）
- Serilog 结构化日志 → Seq
- OpenTelemetry 追踪：采集器 → EdgeHub → RabbitMQ → CloudGateway → 报警 → 工单 完整链路

**验证标准**：
- `docker compose up` 一键启动全部服务
- Jaeger 中可查 "采集器→EdgeHub→RabbitMQ→报警→工单" 完整调用链
- Seq 中可按结构化字段搜索日志

---

## 阶段 7：多租户完善

**目标**：平台卖给多个公司，不同公司登录只看到自己的工厂、设备和报警。

**新增技术**：JWT + 租户识别中间件

**产出**：
- 租户识别中间件：从 JWT 提取 TenantId，注入请求上下文
- 数据库查询自动附加 `WHERE TenantId = @currentTenant`
- EF Core Global Query Filter 实现透明隔离
- 租户管理 API：创建租户、分配工厂/设备
- 边缘网关按配置注入 TenantId，数据自带租户标签

**前置伏笔（从阶段 1 开始）**：
- 所有实体预留 `TenantId` 字段（可先为默认值或 nullable）
- 数据库表包含 `tenant_id` 列
- API 接口预留 `X-Tenant-Id` header 支持

**验证标准**：
- 公司 A 登录 → 只看到自己的工厂、设备、报警
- 公司 B 登录 → 看到的是另一套数据
- 交叉访问 → 403 Forbidden

---

## 技术栈总览

| 分类 | 技术选型 |
|------|---------|
| 运行时 | .NET 10 |
| 数据采集 | Modbus TCP / OPC UA 采集器 |
| 边缘消息 | MQTT (Mosquitto) |
| 边缘时序库 | InfluxDB 2.x |
| 工厂消息队列 | RabbitMQ (一厂一个) |
| 云端时序库 | InfluxDB 2.x / TimescaleDB |
| 关系数据库 | PostgreSQL |
| ORM | EF Core |
| 实时通信 | SignalR |
| 缓存/共享状态 | Redis（阶段 6+ 引入） |
| 容器化 | Docker + Docker Compose |
| 日志 | Serilog → Seq |
| 追踪 | OpenTelemetry → Jaeger |
| 可视化 | Grafana |
| 认证 | JWT |

## 隔离层级

| 层级 | 键 | 作用域 | 隔离方式 |
|------|------|--------|---------|
| 公司 | TenantId | 云端全局 | 数据库行级隔离 + JWT |
| 工厂 | FactoryId | RabbitMQ | 云端 Cluster 中独立 Queue（amgateway.{factoryId}） |
| 车间 | WorkshopId | EdgeHub | 一车间一 Hub 实例 |
| 设备 | DeviceId | 采集器 | MQTT Topic 路由 |

## 项目清单

| 项目 | 阶段 | 职责 |
|------|------|------|
| AmGatewayCloud.Simulator | 1 | Modbus TCP 从站模拟器 |
| AmGatewayCloud.Collector.Modbus | 1 | Modbus 采集器 + MQTT 推送 |
| AmGatewayCloud.Collector.OpcUa | 1 | OPC UA 采集器 + MQTT 推送 |
| AmGatewayCloud.EdgeGateway | 2 | 边缘聚合：MQTT订阅 → InfluxDB + RabbitMQ |
| AmGatewayCloud.CloudGateway | 3 | 云端聚合：多RabbitMQ消费 → PostgreSQL + 时序库 |
| AmGatewayCloud.AlarmService | 4 | 报警微服务 |
| AmGatewayCloud.Web | 4 | 报警看板 + 工单管理 |
| AmGatewayCloud.Domain | 5 | 领域层 |
| AmGatewayCloud.Infrastructure | 5 | EF Core 仓储 |

AmGatewayCloud 负责**边缘采集 → 边缘聚合 → 云端聚合 → 业务服务**全链路，通过 MQTT/RabbitMQ 逐级解耦，支持从单车间到多工厂的水平扩展。
