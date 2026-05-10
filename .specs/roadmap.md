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

## 阶段 4：报警服务 — 业务逻辑的起点 ✅

**目标**：定时拉取 TimescaleDB 数据评估报警规则，生成报警事件，通过 RabbitMQ + SignalR 实时推送，提供 HTTP API 查询/管理。

**新增技术**：SignalR、RabbitMQ（/business vhost）、YARP 反向代理、Dapper + Npgsql

**产出**：
- `AmGatewayCloud.AlarmService` — 报警业务微服务（WebApplication）
  - 定时拉取 TimescaleDB 最新数据点（DISTINCT ON 去重）
  - 规则评估引擎：数值/字符串阈值比较 + Deadband 自动恢复 + 冷却防抖
  - 报警生命周期：Active → Acked → Suppressed → Cleared
  - 报警事件持久化到 PostgreSQL（唯一索引防并发重复触发）
  - RabbitMQ /business vhost 发布报警事件
  - HTTP API：报警查询/确认/抑制/关闭、规则 CRUD（含 Operator/Level/ClearThreshold 校验）
  - 设备离线检测（is_stale 标记）
  - 16 条默认规则种子数据
  - 健康检查端点（/health）
- `AmGatewayCloud.WebApi` — 纯 BFF（Backend for Frontend）
  - YARP 反向代理：/api/alarms/*、/api/alarmrules/* → AlarmService
  - SignalR Hub：订阅 RabbitMQ 报警事件，按工厂分组推送前端
  - 零数据库访问、零业务逻辑
- `AmGatewayCloud.Shared` — 共享契约库
  - DTOs、常量（ValidOperators/ValidLevels）、MQ 消息定义、配置模型

**领域模型**：
- `AlarmRule` — 报警规则（Id, Tag, Operator, Threshold, ThresholdString, ClearThreshold, Level, CooldownMinutes, 三级作用域）
- `AlarmEvent` — 报警事件（RuleId, DeviceId, Level, Status, IsStale, TriggerValue, ClearValue）
- `AlarmStatus` — 状态枚举（Active/Acked/Suppressed/Cleared）
- `DataPointReadModel` — 时序数据读取模型
- `AlarmEventMessage` — RabbitMQ 消息契约

**部署**：
- Docker 多阶段构建（AlarmService + WebApi）
- docker-compose 编排（3 新服务 + RabbitMQ /business vhost 初始化）
- 统一 ASPNETCORE_ENVIRONMENT 环境变量
- Serilog 结构化日志

**验证标准**：
- 数据点超限 → 规则评估触发报警 → PostgreSQL 写入 + RabbitMQ 发布
- WebApi 订阅 RabbitMQ → SignalR 按工厂分组推送
- 前端 REST 请求 → WebApi YARP → AlarmService HTTP API → 返回结果
- 设备离线 → 报警标记 is_stale
- 条件恢复 → 报警自动 Cleared

---

## 阶段 5：前端看板 — 报警可视化

**目标**：构建 Vue 3 前端，实时展示报警和设备状态，验证 API + SignalR 链路是否通畅。

**新增技术**：Vue 3 + Vite、Pinia、Ant Design Vue、vue-echarts、@microsoft/signalr

**产出**：
- `AmGatewayCloud.Web` — Vue 3 + Vite 前端项目
  - 报警实时看板：SignalR 订阅 → 新报警弹窗 + 列表实时刷新
  - 报警管理页：分页查询、确认、抑制、关闭操作
  - 规则管理页：规则 CRUD、启停切换
  - 设备状态看板：在线/离线、is_stale 标记、ECharts 设备概览图表
  - 工厂/车间树形导航：SignalR JoinFactory/LeaveFactory 分组
  - Pinia 状态管理 + REST API 对接 WebApi BFF
  - Ant Design Vue 组件库 + vue-echarts 图表
- Docker 多阶段构建（nginx 托管 SPA）
- docker-compose 新增 `web` 服务，CORS 配置更新

**数据流**：
```
Vue (Pinia) ──REST──► WebApi (BFF) ──YARP──► AlarmService
     │
     └──SignalR──► WebApi Hub ──► 按工厂分组推送实时报警
```

**前端技术栈**：
| 分类 | 选型 | 说明 |
|------|------|------|
| 框架 | Vue 3 + Vite | Composition API + `<script setup>` |
| 状态管理 | Pinia | 轻量、TS 友好 |
| UI 组件库 | Ant Design Vue 4.x | 企业级组件，表格/表单/布局完善 |
| 图表 | vue-echarts + echarts | ECharts 的 Vue 封装，声明式 + 自动 resize |
| 实时通信 | @microsoft/signalr | 自动重连 + 工厂分组 |
| HTTP 客户端 | axios | REST API 调用 |
| 路由 | Vue Router 4 | 工厂/车间上下文路由 |

**验证标准**：
- 打开前端 → 实时弹出报警通知
- 报警列表可按工厂/状态/级别过滤
- 确认/抑制/关闭操作实时生效
- 规则创建/编辑/删除即时反映
- 设备状态图表正常渲染、实时更新
- Docker 部署后前端可正常访问后端 API

---

## 阶段 6：DDD 提炼 + 维修工单

**目标**：用 DDD 思想提炼领域模型，从 AlarmService 抽取领域层；引入工单系统，实现报警→工单自动联动。

**新增技术**：EF Core、MediatR（领域事件）、领域事件机制

### 6.1 DDD 提炼

**产出**：
- `AmGatewayCloud.Domain` — 领域层
  - 聚合根：`Equipment`、`Alarm`（从阶段4 AlarmEvent/AlarmRule 提炼）
  - 值对象：`AlarmLevel`、`AlarmStatus`、`OperatorType`
  - 领域事件：`AlarmTriggeredEvent`、`AlarmClearedEvent`
  - 领域服务：规则评估逻辑从 AlarmService 迁入
- `AmGatewayCloud.Infrastructure` — 基础设施层
  - EF Core DbContext + 仓储实现
  - 数据库迁移（从 init-db.sql → EF Core Migration）
  - MediatR 领域事件发布
- AlarmService 重构：引用 Domain + Infrastructure，业务逻辑委托给聚合根

**重构策略**：
```
重构前（阶段4）：
AlarmService → Dapper → PostgreSQL（直接 SQL）

重构后（阶段6）：
AlarmService → Domain（聚合根 + 领域服务）
                  ↓
            Infrastructure（EF Core 仓储）
                  ↓
            PostgreSQL
```

**验证标准**：
- 重构后 AlarmService 所有 API 行为不变
- EF Core Migration 可从空库重建完整 schema
- 领域事件（AlarmTriggered/Cleared）通过 MediatR 正确发布

### 6.2 维修工单系统

**产出**：
- `WorkOrder` 聚合根（Domain 层）
  - Id, AlarmId, EquipmentId, Status, Assignee, CreatedAt, TenantId
  - Status: Pending → InProgress → Completed
- 领域事件联动：`AlarmTriggeredEvent` → 自动创建工单
- AlarmService 新增工单管理 API：创建、查询、分配、完成
- WebApi 新增 YARP 代理路由：`/api/workorders/*`
- 前端工单页：工单列表、详情、分配、完成操作
- AlarmRule 热更新：WebApi 修改规则后通过 RabbitMQ 即时通知 AlarmService

**领域模型（提炼后）**：
```
Equipment (聚合根)
├── Id, Name, FactoryId, WorkshopId, TenantId
└── Alarms (集合)

Alarm (聚合根)
├── Id, EquipmentId, Level, Status, Message, Timestamp, TenantId
└── WorkOrders (集合)

WorkOrder (聚合根)
├── Id, AlarmId, EquipmentId, Status, Assignee, CreatedAt, TenantId
└── Status: Pending → InProgress → Completed
```

**验证标准**：
- 报警触发 → 自动生成工单 → 前端工单页可查看/处理
- 工单状态流转正确（Pending → InProgress → Completed）
- 工单按 TenantId 隔离

---

## 阶段 7：容器化 + 可观测性

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
  - AmGatewayCloud.AlarmService + WebApi + Shared
  - Seq（日志）
  - Jaeger（追踪）
- Serilog 结构化日志 → Seq
- OpenTelemetry 追踪：采集器 → EdgeHub → RabbitMQ → CloudGateway → 报警 → 工单 完整链路

**验证标准**：
- `docker compose up` 一键启动全部服务
- Jaeger 中可查 "采集器→EdgeHub→RabbitMQ→报警→工单" 完整调用链
- Seq 中可按结构化字段搜索日志

---

## 阶段 8：多租户完善

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
| ORM | Dapper（阶段4）、EF Core（阶段6+） |
| 实时通信 | SignalR |
| BFF 网关 | YARP 反向代理 |
| 前端 | Vue 3 + Vite + Pinia（阶段5） |
| 前端 UI | Ant Design Vue 4.x（阶段5） |
| 前端图表 | vue-echarts + echarts（阶段5） |
| 领域事件 | MediatR（阶段6+） |
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
| AmGatewayCloud.AlarmService | 4 | 报警业务微服务（评估引擎 + HTTP API） |
| AmGatewayCloud.WebApi | 4 | BFF（YARP 反向代理 + SignalR 推送） |
| AmGatewayCloud.Shared | 4 | 共享契约库（DTOs + Constants + Messages + Config） |
| AmGatewayCloud.Web | 5 | Vue 3 前端（报警看板 + 规则管理 + 设备状态） |
| AmGatewayCloud.Domain | 6 | 领域层（聚合根 + 领域事件） |
| AmGatewayCloud.Infrastructure | 6 | EF Core 仓储 |

AmGatewayCloud 负责**边缘采集 → 边缘聚合 → 云端聚合 → 业务服务**全链路，通过 MQTT/RabbitMQ 逐级解耦，支持从单车间到多工厂的水平扩展。
