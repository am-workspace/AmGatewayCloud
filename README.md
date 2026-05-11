# AmGatewayCloud

工业物联网边缘-云端协同平台，支持多厂多车间的数据采集、告警监控、工单管理，可多租户运营。

## 架构总览

```
边缘侧                                    云端
┌──────────────────────────┐        ┌─────────────────────────────────┐
│ 采集器 × N               │        │                                 │
│ (Modbus/OpcUa)           │        │  RabbitMQ (一厂一个)             │
│      │ MQTT(局域网)      │        │      │ AMQP                     │
│      ▼                   │        │      ▼                          │
│ 边缘聚合网关 (EdgeHub)   │──WAN──►│  云端聚合网关 (CloudGateway)    │
│  ├── Local InfluxDB      │        │   ├── PostgreSQL                │
│  ├── 断网时本地暂存       │        │   └── 云端时序库                 │
│  └── 断网恢复后回放       │        │        │                        │
│      │                   │        │        ▼                        │
│      ▼                   │        │   业务服务 (WebApi BFF)        │
│  Grafana(本地看板)        │        │   ├── AlarmService             │
└──────────────────────────┘        │   ├── WorkOrderService         │
                                    │   └── Vue 3 前端                │
                                    │                                 │
                                    │   可观测性                      │
                                    │   ├── Seq (日志聚合)            │
                                    │   └── Jaeger (分布式追踪)       │
                                    └─────────────────────────────────┘
```

## 项目结构

```
AmGatewayCloud/
├── src/
│   ├── AmGatewayCloud.Collector.Modbus/     # Modbus TCP 采集器
│   ├── AmGatewayCloud.Collector.OpcUa/      # OPC UA 采集器
│   ├── AmGatewayCloud.Shared/               # 共享库 (DTOs, Tenant, Auth, OTel)
│   ├── AmGatewayCloud.WebApi/               # BFF 网关 (YARP + SignalR + JWT)
│   ├── AmGatewayCloud.AlarmDomain/          # 报警领域层 (聚合根, 领域事件)
│   ├── AmGatewayCloud.AlarmInfrastructure/  # 报警基础设施层 (EF Core 仓储)
│   ├── AmGatewayCloud.AlarmService/         # 报警业务微服务
│   ├── AmGatewayCloud.WorkOrderService/     # 工单微服务
│   └── AmGatewayCloud.Web/                  # Vue 3 前端
├── docker/                                  # Dockerfile 集合
├── docker-compose.yml
└── .specs/                                  # 设计规格文档
```

## 技术栈

| 分类 | 选型 |
|------|------|
| 运行时 | .NET 10 |
| 数据采集 | Modbus TCP / OPC UA |
| 边缘消息 | MQTT (Mosquitto) |
| 边缘时序库 | InfluxDB 2.x |
| 消息队列 | RabbitMQ (一厂一个 vhost) |
| 云端时序库 | TimescaleDB |
| 关系数据库 | PostgreSQL |
| ORM | EF Core (报警域) + Dapper (查询) |
| 实时通信 | SignalR |
| BFF 网关 | YARP 反向代理 |
| 认证 | JWT Bearer |
| 多租户 | TenantMiddleware + EF Global Query Filter |
| 领域事件 | MediatR |
| 前端 | Vue 3 + Vite + Pinia + Ant Design Vue |
| 日志 | Serilog → Seq |
| 追踪 | OpenTelemetry → Jaeger |
| 容器化 | Docker + Docker Compose |

## 核心特性

### 三级隔离

| 层级 | 键 | 作用域 | 隔离方式 |
|------|------|--------|---------|
| 公司 | TenantId | 云端全局 | JWT claim + EF Global Query Filter + Dapper WHERE |
| 工厂 | FactoryId | RabbitMQ | 独立 Queue (`amgateway.{factoryId}`) |
| 车间 | WorkshopId | EdgeHub | 一车间一 Hub 实例 |

### 数据流转

```
采集器 → MQTT(局域网) → EdgeHub → InfluxDB(本地) + RabbitMQ(WAN)
                                              ↓
                                   CloudGateway → PostgreSQL + TimescaleDB
                                              ↓
                                   AlarmService → 评估规则 → 触发报警
                                              ↓                ↓
                                   WorkOrderService    RabbitMQ → SignalR → 前端
                                      (自动创建工单)
```

### 多租户

- **TenantMiddleware**: 从 JWT `tenant_id` claim 或 `X-Tenant-Id` Header 提取租户标识
- **EF Core Global Query Filter**: 报警域自动附加 `WHERE TenantId = @currentTenant`
- **Dapper 显式过滤**: 工单查询附加 `tenant_id` 条件
- **YARP 透传**: BFF 网关自动转发 `X-Tenant-Id` Header 到下游服务
- **SignalR 分组**: 按 `tenant-{id}` 和 `tenant-{id}-factory-{fid}` 分组推送

### 可观测性

- **Seq** (http://localhost:8081): 结构化日志聚合，日志自动携带 TenantId
- **Jaeger** (http://localhost:16686): 分布式追踪，Span 注入 `tenant.id` Tag
- **Serilog Enrich**: `FromLogContext` + `WithMachineName` + `WithThreadId`

## 快速开始

### 前置条件

- .NET 10 SDK
- Docker + Docker Compose
- Node.js 20+ (前端开发)

### Docker Compose 一键启动

```bash
docker compose up -d
```

启动后可访问：

| 服务 | 地址 |
|------|------|
| 前端 | http://localhost |
| WebApi BFF | http://localhost:8080 |
| AlarmService | http://localhost:5001 |
| WorkOrderService | http://localhost:5002 |
| Seq 日志 | http://localhost:8081 |
| Jaeger 追踪 | http://localhost:16686 |
| RabbitMQ 管理 | http://localhost:15672 |
| TimescaleDB | localhost:5432 |

### 本地开发

```bash
# 还原依赖
dotnet restore

# 构建所有项目
dotnet build

# 启动基础设施 (PostgreSQL, RabbitMQ)
docker compose up -d timescaledb rabbitmq rabbitmq-init

# 启动报警服务
dotnet run --project src/AmGatewayCloud.AlarmService

# 启动工单服务
dotnet run --project src/AmGatewayCloud.WorkOrderService

# 启动 BFF 网关
dotnet run --project src/AmGatewayCloud.WebApi

# 启动前端
cd src/AmGatewayCloud.Web
npm install
npm run dev
```

## 项目清单

| 项目 | 职责 |
|------|------|
| Collector.Modbus | Modbus TCP 采集器 + MQTT 推送 |
| Collector.OpcUa | OPC UA 采集器 + MQTT 推送 |
| Shared | 共享契约 (DTOs, Tenant, Auth, OTel, Config) |
| WebApi | BFF 网关 (YARP + SignalR + JWT 认证 + 租户透传) |
| AlarmDomain | 报警领域层 (聚合根, 值对象, 领域事件, 领域服务) |
| AlarmInfrastructure | 报警基础设施层 (EF Core 仓储, MediatR 事件发布) |
| AlarmService | 报警微服务 (规则评估引擎 + HTTP API) |
| WorkOrderService | 工单微服务 (报警联动 + 工单管理) |
| Web | Vue 3 前端 (报警看板, 工单管理, 规则管理) |

## 开发进度

详见 [.specs/roadmap.md](.specs/roadmap.md)

- [x] 阶段 1: 模拟车 + 采集器
- [x] 阶段 4: BFF 基座 + 报警服务
- [x] 阶段 5: 前端看板
- [x] 阶段 6: DDD 提炼 + 维修工单
- [x] 阶段 7: 多租户完善
- [x] 阶段 8: 容器化 + 可观测性
- [ ] 阶段 2: 边缘聚合网关
- [ ] 阶段 3: 云端聚合网关

## License

Private
