# AmGatewayCloud.CloudGateway — 实现状态总览

> 阶段3交付总结：对照 `cloud-gateway.md`（方案）+ `cloud-gateway-supplement.md`（补充）逐项核对。

**生成时间**：2026-05-09

---

## 1. 完成度概览

```
基础方案 (cloud-gateway.md)         ████████████████████  18/19  95%
P0 补充项                            ████████████████████  3/3   100%
P1 补充项                            ████████████████████  5/5   100%
P2 补充项                            ██████████████░░░░░░  2/3   67%
P3 补充项                            ██████░░░░░░░░░░░░░░  1/3   33%  (后续阶段)
```

---

## 2. 基础方案逐章核对

| 章节 | 内容 | 状态 | 实际实现说明 |
|------|------|------|-------------|
| §1 定位 | 消费多工厂RabbitMQ → TimescaleDB + PostgreSQL | ✅ | MultiRabbitMqConsumer + TimescaleDbWriter + PostgreSqlDeviceStore，完整链路已验证 |
| §2 架构 | 多工厂 → RabbitMQ → CloudGateway → 双库 | ✅ | 每个工厂独立FactoryConsumer（独立连接、独立Channel、独立重连） |
| §3 数据流转 | 去重 → 分流 → 时序写入 + 设备注册 → ACK | ✅ | 先写DB后ACK，Flush成功才确认，并行写两库 |
| §4.1 DataBatch | 消费契约模型 + TraceParent预留 | ✅ | `JsonPropertyName` camelCase，`TraceParent` 字段已预留 |
| §4.2 TimescaleDB | device_data hypertable + 索引 + 90天retention | ✅ | hypertable、idx_device_data_lookup、retention policy。连续聚合视图未实现（标记可选） |
| §4.3 PostgreSQL | factories / workshops / devices 表 | ✅ | 三张业务表 + 超规格的 audit_logs 审计表 + idx_devices_lookup 唯一索引 |
| §5.1 配置模型 | CloudGatewayConfig + TenantResolutionMode | ✅ | 完整映射 + 超规格的 CloudGatewayConfigValidator 启动校验 |
| §6.1 MultiRabbitMqConsumer | 多工厂独立Consumer + DLQ + 熔断 + 错误分类 | ✅ | 每工厂独立连接/Channel、DLX绑定、CircuitBreaker(5次→30s)、WriteException分类、MaxRetryCount=3。存在bug已修复：创建Consumer后未调用StartAsync |
| §6.2 MessageDeduplicator | BatchId去重 + 内存缓存 + DB兜底 | ✅ | ConcurrentDictionary，1h TTL，100k上限，超限清理最旧20%。DB侧ON CONFLICT DO NOTHING兜底 |
| §6.3 TimescaleDbWriter | 批量缓冲 + 背压 + 自动建表 + valueType分字段 | ✅ | BoundedChannel(BatchSize*5, Wait)背压，定时5s刷新，DataPointConverter按valueType分列，未知类型fallback字符串 |
| §6.4 PostgreSqlDeviceStore | 自动注册 + UPSERT + tags缓存 + last_seen降频 | ✅ | knownTagsCache仅新tag时写DB，lastSeenCache 30s窗口降频，工厂/车间懒创建 |
| §6.5 HealthMonitorService | /health端点 + Consumer级指标 | ✅ | ASP.NET Core HealthChecks，三检查，ConsumerHealthTracker暴露online/lag/processed/failures |
| §7 错误处理 | 11种场景处理 | ✅ | 超大ACK+审计、反序列化失败ACK+审计、重复ACK、可重试NACK+requeue、不可重试ACK+审计、DLQ兜底、熔断保护、未知ValueType fallback |
| §8 项目文件 | csproj + NuGet包 | ⚠️ | 使用**Dapper**而非方案中的EF Core（有意的架构选择），Npgsql 9.0.3，RabbitMQ.Client 6.8.1，Serilog 9.0.0 |
| §9 项目结构 | 目录布局 | ⚠️ | 核心文件完整，无Migrations/（Dapper无需迁移），Infrastructure/含HealthChecks/子目录 |
| §10 运行验证 | 11步验证 | ⚠️ | 1-5步已验证；6-11步（断线、熔断、DLQ、脏数据、retention）待专项验证 |
| §11 阶段4衔接 | 数据底座就绪 | ✅ | TimescaleDB + PostgreSQL 可被AlarmService直接查询 |
| §12 后续演进 | TraceParent预留 | ✅ | DataBatch.TraceParent 字段已定义 |
| §13 Check-list | 7项完成标准 | ⚠️ | 4项确认完成，3项待验证 |

---

## 3. 补充方案逐项核对

### 🔴 P0 — 必须实现

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 1 | ACK与flush同步 | ✅ | FlushAsync成功后BasicAck，缓冲数据不因进程崩溃丢失 |
| 2 | DLQ + 熔断 + 重试计数 | ✅ | DLX dlx.{queue} + DLQ dlq.{queue} + MaxRetryCount=3 + CircuitBreaker(5次失败→30s打开) |
| 3 | 可重试 vs 不可重试错误区分 | ✅ | WriteException + WriteErrorKind(Transient/Permanent)，永久错误直接ACK + 审计 |

### 🟡 P1 — 重要边界条件

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 4 | 背压/内存队列上限 | ✅ | BoundedChannel(BatchSize*5, Wait)，满时阻塞上游，背压传递到RabbitMQ |
| 5 | DateTimeOffset统一 | ⚠️ | CloudGateway内部已统一。EdgeGateway DataPoint.Timestamp为DateTime，跨服务类型差异（反序列化兼容，暂无实际问题） |
| 6 | TimescaleDB retention policy | ✅ | 原始数据保留90天 |
| 7 | devices.tags缓存降频 | ✅ | knownTagsCache + 仅新tag写DB，lastSeenCache 30s降频 |
| 8 | 健康检查端点 /health | ✅ | 三检查 + consumer级状态(lag/processed/failures) |

### 🟢 P2 — 优化建议

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 9 | 配置热更新 (IFactoryRegistry) | ✅ | FileFactoryRegistry + IOptionsMonitor.OnChange，动态启停Consumer |
| 10 | 超大消息/未知类型审计日志 | ✅ | AuditLogService + DataPointConverter.UnknownTypeFallback |
| 11 | 连续聚合视图 + 刷新策略 | ❌ | 方案标记可选，暂未实现 |

### ⚪ P3 — 后续规划

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 12 | 报警规则预留 Deadband/DelaySeconds | ⬜ | 阶段4 AlarmService设计时引入 |
| 13 | OpenTelemetry traceparent | ✅ | DataBatch.TraceParent字段已预留 |
| 14 | AlarmService 拉/推模式 | ⬜ | 阶段4技术选型，初期推荐拉模式 |

---

## 4. 边界条件处理表

| 异常场景 | 要求 | 状态 | 说明 |
|---------|------|------|------|
| TimescaleDB成功，PostgreSQL失败 | NACK重入队 | ✅ | Task.WhenAll等两库，任一失败→NACK。ON CONFLICT DO NOTHING防重 |
| 数据库长时间不可用(>5分钟) | DLQ+熔断 | ✅ | 5次失败→熔断30s，3次重试→DLQ |
| 单条消息超大(>1MB) | ACK+审计 | ✅ | BasicAck + AuditLogService |
| 消息反序列化失败 | ACK+审计 | ✅ | BasicAck + payload前200字节Base64 |
| 某工厂队列积压 | 监控告警 | ⚠️ | Lag时间已暴露，未实现动态降PrefetchCount |
| CloudGateway进程崩溃 | flush后ACK | ✅ | FlushAsync成功才ACK |
| 两个CloudGateway实例同时消费 | 独占消费 | ❌ | 依赖部署侧保证单实例 |
| TimescaleDB磁盘满 | 健康检查失败 | ❌ | 依赖运维监控 |
| 未知ValueType | fallback字符串 | ✅ | DataPointConverter.UnknownTypeFallback |

---

## 5. 实际运行验证记录

### 5.1 已验证项

| 测试项 | 操作 | 结果 |
|--------|------|------|
| 启动+基础设施连接 | docker compose up + dotnet run | ✅ 三检查全部Healthy |
| 队列拓扑自动创建 | 启动CloudGateway | ✅ DLX+DLQ+主队列自动声明 |
| 消息消费+反序列化 | PS脚本发camelCase JSON | ✅ 字段正确解析 |
| TimescaleDB写入 | 发2测点消息 | ✅ 2行，value_float正确 |
| PostgreSQL设备自动注册 | 同上 | ✅ factories/workshops/devices自动创建 |
| 多工厂同时消费 | 配2工厂 | ✅ factory-a+b均online |
| 健康检查端点 | GET /health | ✅ Healthy + consumer状态 |

### 5.2 待验证项

| 测试项 | 说明 |
|--------|------|
| RabbitMQ断线重连 | 停RabbitMQ → 观察重连+offline |
| 消息去重 | 发相同BatchId两次 |
| 熔断机制 | 连续5次DB不可用→30s暂停 |
| DLQ死信路由 | 重试超3次→消息进DLQ |
| 脏数据容错 | 发超大/格式错误消息 |
| Retention policy | 确认90天数据清理 |
| 多实例独占消费 | 两CloudGateway同时消费同队列 |

---

## 6. 与方案差异说明

### 6.1 有意差异

| 差异 | 方案 | 实际 | 原因 |
|------|------|------|------|
| ORM选型 | EF Core | Dapper 2.1.66 | 时序写入无需变更追踪，Dapper更轻量 |
| 每工厂连接 | 独立连接 | 独立连接 ✅ | 每个FactoryConsumer独立CreateConnection() |
| Serilog版本 | 8.* | 9.0.0 | NuGet最新稳定版 |

### 6.2 已知问题（已修复）

| # | 问题 | 状态 |
|---|------|------|
| 1 | AsyncEventingBasicConsumer在.NET 10下不触发Received事件 | ✅ 改用EventingBasicConsumer |
| 2 | MultiRabbitMqConsumer创建Consumer后未调用StartAsync | ✅ 补上consumer.StartAsync(_ct) |
| 3 | EdgeGateway DataPoint.Timestamp为DateTime，CloudGateway为DateTimeOffset | ⚠️ 反序列化兼容，建议阶段5统一 |

---

## 7. 后续演进路线图

| 阶段 | CloudGateway需要做的 |
|------|---------------------|
| **阶段3 (当前)** | 完成剩余验证项（断线、熔断、DLQ） |
| **阶段4** | 无需改动，AlarmService直接查TimescaleDB（拉模式） |
| **阶段5** | 补充Dockerfile；factories/devices升级为DDD聚合根 |
| **阶段6** | 接入TraceParent，跨消息队列链路打通 |
| **阶段7** | 切换TenantResolutionMode.FromMessage；替换IFactoryRegistry实现 |

---

## 8. Spec文件关系

```
cloud-gateway.md              ← 基础方案（架构/数据模型/配置/组件设计）
cloud-gateway-supplement.md   ← 补充文档（边界条件/校验/优化建议/优先级）
cloud-gateway-status.md       ← 本文件（实现状态/验证/演进）
edge-gateway-status.md        ← EdgeGateway实现状态参考
../.contract/mqtt-contract.md ← MQTT跨服务数据契约
../.errors/cloudgateway-testing-issues.md ← 独立测试排错记录
```

---

## 9. 文件清单

```
src/AmGatewayCloud.CloudGateway/
├── AmGatewayCloud.CloudGateway.csproj       # net10.0, Worker SDK
├── Program.cs                               # 入口 + DI + 数据库初始化
├── appsettings.json                         # 默认配置（2工厂）
├── Configuration/
│   ├── CloudGatewayConfig.cs                # 配置POCO（含TenantResolutionMode）
│   └── CloudGatewayConfigValidator.cs       # 启动时配置校验
├── Models/
│   ├── DataBatch.cs                         # 消费契约模型（含TraceParent预留）
│   └── AuditLog.cs                          # 审计日志实体
├── Services/
│   ├── MultiRabbitMqConsumer.cs             # 多工厂Consumer生命周期管理
│   ├── FactoryConsumer.cs                   # 单工厂消费（DLQ/熔断/重试/ACK）
│   ├── MessageDeduplicator.cs               # BatchId去重（100k/1h TTL）
│   ├── TimescaleDbWriter.cs                 # 时序批量写入（BoundedChannel背压）
│   ├── PostgreSqlDeviceStore.cs             # 设备元数据管理（tags缓存/last_seen降频）
│   ├── AuditLogService.cs                   # 审计日志写入
│   ├── ConsumerHealthTracker.cs             # Consumer健康指标聚合
│   ├── DataPointConverter.cs                # valueType→列名转换+fallback
│   ├── IFactoryRegistry.cs                  # 工厂注册接口+FileFactoryRegistry实现
│   └── WriteException.cs                    # WriteErrorKind(Transient/Permanent)
└── Infrastructure/
    ├── NpgsqlConnectionFactory.cs           # 双库连接工厂（按dbName切换凭据）
    ├── TimescaleDbInitializer.cs            # Hypertable+索引+retention policy
    ├── PostgreSqlInitializer.cs             # 业务表+审计表DDL
    └── HealthChecks/
        ├── RabbitMqHealthCheck.cs           # RabbitMQ连接检查
        ├── TimescaleDbHealthCheck.cs        # TimescaleDB连接检查
        └── PostgreSqlHealthCheck.cs         # PostgreSQL连接检查
```
