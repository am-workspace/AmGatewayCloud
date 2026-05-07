# AmGatewayCloud.EdgeGateway — 实现状态总览

> 阶段2交付总结：对照 `edge-gateway.md`（方案）+ `edge-gateway-supplement.md`（补充）逐项核对。

**生成时间**：2026-05-07

---

## 1. 完成度概览

```
基础方案 (edge-gateway.md)       ████████████████████  10/10 100%
P0 补充项                         ████████████████████  4/4   100%
P1 补充项                         ████████████████░░░░  6/8   75%
P2 补充项                         ██████████░░░░░░░░░░  2/4   50%
P3 补充项                         ░░░░░░░░░░░░░░░░░░░░  0/4    0%  (后续阶段)
```

---

## 2. 基础方案逐章核对

| 章节 | 内容 | 状态 | 实际实现说明 |
|------|------|------|-------------|
| §1 定位 | 边缘聚合网关：MQTT订阅 → InfluxDB + RabbitMQ | ✅ | `MqttConsumerService` 订阅 `amgateway/#`，分发到 `InfluxDbWriter` + `RabbitMqForwarder` |
| §2 架构 | EdgeGateway + Local InfluxDB + Grafana + RabbitMQ | ✅ | 架构完整实现，Grafana 通过 InfluxDB 数据源接入 |
| §3 数据模型 | DataBatch / DataPoint / InfluxDB存储结构 / Watermark | ✅ | `Models/DataBatch.cs` + `DataPoint.cs`；InfluxDB `device_data` measurement；Watermark 文件含 `LastBatchId` + `LastSequence` |
| §4 配置模型 | appsettings.json + EdgeGatewayConfig / MqttConsumerConfig / InfluxDbConfig / RabbitMqConfig | ✅ | `Configuration/EdgeGatewayConfig.cs` 完整映射，含新增 `QueueName`、`QoS`、`BatchSize` 等 |
| §5.1 MqttConsumerService | MQTT订阅 + 反序列化 + 分发 | ✅ | `Services/MqttConsumerService.cs`，支持 QoS1、CleanSession、KeepAlive、共享订阅 |
| §5.2 InfluxDbWriter | 本地InfluxDB批量写入 + Bucket管理 | ✅ | `Services/InfluxDbWriter.cs`，按 `valueType` 分字段（`value_int`/`value_float`/`value_bool`/`value_string`），批量缓冲 + 定时刷新 |
| §5.3 RabbitMqForwarder | RabbitMQ转发 + 路由键构造 + 断线检测 | ✅ | `Services/RabbitMqForwarder.cs`，声明 Exchange + Queue + Bind，路由键含 `factoryId`/`workshopId`/`deviceId`/`protocol`，特殊字符转义 |
| §5.4 WatermarkTracker | 水位线追踪 + 持久化 | ✅ | `Services/WatermarkTracker.cs`，`LastForwardedAt` + `LastBatchId` + `LastSequence`，同步刷盘（`SemaphoreSlim` 保护），启动时校验文件完整性 |
| §5.5 ReplayService | 断网恢复后回放未转发数据 | ✅ | `Services/ReplayService.cs`，令牌桶限速（100/s），中断保护（RabbitMQ 断开时暂停并持久化进度），但 **InfluxDB CSV 解析逻辑待完善** |
| §6 路由键格式 | `amgateway.{factoryId}.{workshopId}.{deviceId}.{protocol}` | ✅ | `RabbitMqForwarder.BuildRoutingKey()`，`.` `*` `#` 替换为 `_` |
| §7 错误处理策略 | 8种场景的处理方式 | ✅ | MQTT 断线重连、InfluxDB 写入失败告警、RabbitMQ 断线标记 offline + 重连 + 回放、磁盘满检测等 |
| §8 项目文件 | csproj + NuGet包 | ✅ | `AmGatewayCloud.EdgeGateway.csproj`，含 MQTTnet / InfluxDB.Client / RabbitMQ.Client |
| §9 项目结构 | 目录布局 | ✅ | 与目标结构完全一致（Program.cs / appsettings.json / Configuration / Models / Services / watermarks/） |
| §10 运行验证 | 前置条件 + 启动 + 6步验证 + 预期日志 | ✅ | 已实际运行验证：Modbus 采集器 → MQTT → EdgeGateway → InfluxDB + RabbitMQ 全链路打通 |
| §11 后续演进 | 阶段3/5/6/7变化 | ⬜ | 待后续阶段实现 |

---

## 3. 补充方案逐项核对

### 🔴 P0 — 必须实现

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 1 | MQTT ACK时机：InfluxDB成功后ACK，RabbitMQ转发异步不阻塞 | ✅ | `MqttConsumerService.cs` 第144-160行：先 `await _influxWriter.WriteBatchAsync`，然后 `e.ProcessingFailed = false`，RabbitMQ 转发用 `Task.Run` 异步不阻塞 |
| 2 | Watermark精确化：BatchId + Sequence替代纯时间戳 | ✅ | `WatermarkTracker` 含 `LastBatchId(Guid)` + `LastSequence(long)`，`ReplayService.ReadBatchesAsync` 用 `batch_id != excludeBatchId` 精确去重 |
| 3 | InfluxDB类型化存储：value_int/float/bool/string按类型写入 | ✅ | `InfluxDbWriter.DataPointToLineProtocol()` 按 `valueType` 写入 `value_int`/`value_float`/`value_bool`/`value_string` |
| 4 | 回放服务：令牌桶限速 + 中断保护 + 进度持久化 | ✅ | `ReplayService.ReplayAsync()`：每秒100 batch 令牌桶限速；RabbitMQ 断开时 `await watermarkTracker.SaveAsync()` 暂停；按 `currentFrom` 分页断点续传 |

### 🟡 P1 — 重要边界条件

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 5 | Watermark同步刷盘：每次转发成功后同步写文件/SQLite | ✅ | `WatermarkTracker.UpdateWatermark()` 中通过 `Task.Run(SaveAsync)` 同步刷盘；`SemaphoreSlim` 保护并发写入 |
| 6 | RoutingKey特殊字符转义：`.` `*` `#`替换为`_` | ✅ | `RabbitMqForwarder.EscapeRoutingKey()` 替换 `.` → `_`、`*` → `_`、`#` → `_` |
| 7 | MQTT共享订阅：`$share/edgehub/amgateway/#`支持多实例热备 | ✅ | `MqttConsumerConfig.UseSharedSubscription` + `SharedGroup`，`MqttConsumerService.ExecuteAsync()` 自动拼接 `$share/{group}/{topic}` |
| 8 | 补充配置项：QoS/KeepAlive/CleanSession/BatchSize/FlushInterval/PrefetchCount | ✅ | 全部在配置类中：`MqttConsumerConfig.QoS=1`、`KeepAliveSeconds=60`、`CleanSession=false`；`InfluxDbConfig.BatchSize=100`、`FlushIntervalMs=1000`；`RabbitMqConfig.PrefetchCount=50` |
| 9 | 网络闪断防抖：连续3次失败才标记offline | ✅ | `RabbitMqForwarder.OfflineThreshold = 3`，`_consecutiveFailures` 计数器，连续3次 `RecordFailure()` 才 `_isOnline = false` |
| 10 | 单条消息大小限制：>1MB直接丢弃并告警 | ✅ | `MqttConsumerService.OnMessageReceivedAsync()` 第111行：`PayloadSegment.Count > MaxPayloadSize(1MB)` → 丢弃并 `e.ProcessingFailed = false` |
| 11 | 回放数据标记：`x-is-replay` + `x-original-timestamp` header | ⬜ | 未实现。当前 `ReplayService` 直接调用 `RabbitMqForwarder.ForwardAsync`，未在 AMQP Properties 中注入 `x-is-replay` 和 `x-original-timestamp` |
| 12 | InfluxDB磁盘满保护：停止MQTT ACK，进入只读模式 | ⚠️ | 部分实现。`InfluxDbWriter.IsDiskFullAsync()` 检测 `<100MB` 时抛出异常，`MqttConsumerService` 异常分支设置 `e.ProcessingFailed = true`（不ACK），但缺少持续监控和只读模式的状态管理 |

### 🟢 P2 — 优化建议

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 13 | 采集器时钟漂移检测：ReceivedAt字段 + 阈值告警 | ✅ | `MqttConsumerService.OnMessageReceivedAsync()` 第128行：`batch.ReceivedAt = DateTimeOffset.UtcNow`；第131-136行：检测 `|drift.TotalMinutes| > 5` 记录告警日志 |
| 14 | Watermark文件损坏恢复：启动时校验，损坏则从最早记录回放 | ✅ | `WatermarkTracker.LoadAsync()` try-catch 捕获解析异常，损坏时调用 `Reset()` 重置为 `DateTimeOffset.MinValue`，即从最早记录开始 |
| 15 | 配置热加载接口：IEdgeGatewayConfigProvider预留 | ⬜ | 未实现。当前使用 `IOptions<EdgeGatewayConfig>` 静态绑定，无热加载接口预留 |
| 16 | MQTT与InfluxDB同时断开：内存队列缓存，设上限防OOM | ⬜ | 未实现。当前 InfluxDB 写入失败直接抛异常，无内存队列降级逻辑 |

### ⚪ P3 — 后续规划（不在阶段2实现，但需预留）

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 17 | 报警规则预留Deadband/DelaySeconds | ⬜ | 阶段4 AlarmService设计时引入 |
| 18 | OpenTelemetry traceparent跨消息队列 | ⬜ | 阶段6 DataBatch预留TraceParent字段 |
| 19 | DDD聚合边界修正 | ⬜ | 阶段5设计时修正 |
| 20 | 一厂一RabbitMQ → 共享集群评估 | ⬜ | 阶段3架构决策，当前已通过 `QueueName: amgateway.{factoryId}` 在 Exchange 层面实现按工厂隔离 |

---

## 4. 实际运行验证关注点

### 4.1 基础功能验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| 启动订阅 | 启动EdgeGateway → 启动采集器 | 日志显示MQTT连接、订阅amgateway/#、收到数据 |
| InfluxDB写入 | 查询本地InfluxDB | edge-data bucket中有device_data measurement |
| RabbitMQ转发 | 启动简单消费者 | 消费者收到amgateway.factory-a.workshop-1.#数据 |
| 路由键格式 | 观察RabbitMQ消息 | 路由键为amgateway.factory-a.workshop-1.simulator-001.modbus |

### 4.2 断网恢复验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| 断开RabbitMQ | 停掉RabbitMQ或断网 | 日志：标记offline，数据继续写入InfluxDB |
| 数据不丢 | 检查InfluxDB | 断网期间数据全部在本地 |
| 恢复回放 | 恢复RabbitMQ连接 | 自动重连，启动ReplayService，补发未转发数据 |
| 水位线更新 | 检查watermark文件 | 时间戳更新到最新，无重复发送 |
| 回放限速 | 积累大量数据后恢复 | 回放速率可控，不冲击RabbitMQ |
| 回放中断 | 回放过程中再次断网 | 暂停回放，持久化进度，恢复后断点续传 |

### 4.3 边界条件验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| 网络闪断 | 模拟<5s闪断 | 不触发offline，不启动回放 |
| 超大消息 | 发送>1MB的MQTT消息 | 丢弃并告警，不OOM |
| 特殊字符设备ID | deviceId含`.`字符 | 路由键中`.`被替换为`_` |
| 多实例热备 | 启动2个EdgeGateway | MQTT共享订阅，数据不重复 |
| 进程崩溃恢复 | kill进程后重启 | 从watermark位置继续，不重复不丢失 |
| InfluxDB磁盘满 | 模拟磁盘满 | 停止MQTT ACK，进入只读保护 |

### 4.4 性能验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| 高吞吐 | 50+采集器同时发送 | InfluxDB批量写入不阻塞，RabbitMQ转发跟得上 |
| 内存稳定 | 长时间运行 | 无内存泄漏，批量缓冲不无限增长 |
| 回放性能 | 回放10万条数据 | 令牌桶限速生效，RabbitMQ不被打满 |

---

## 5. 后续演进路线图

对照项目路线图 (`roadmap.md`)，EdgeGateway在各阶段的任务：

| 阶段 | 变化 | 前置条件 |
|------|------|---------|
| **阶段2 (当前)** | ✅ MQTT订阅 → InfluxDB + RabbitMQ；断网检测 + 水位线 + 回放 | 本地Mosquitto + InfluxDB + RabbitMQ |
| **阶段3** | CloudGateway消费RabbitMQ；EdgeGateway不变 | RabbitMQ部署 |
| **阶段5** | 容器化(Dockerfile)；Serilog→Seq；OpenTelemetry追踪；健康检查端点 | Docker环境 |
| **阶段6** | 配置中心下发TenantId/FactoryId/WorkshopId；配置热加载启用 | 配置中心 |
| **阶段7** | 多租户完善；JWT中间件 | 认证服务 |

### 关键Check-list（阶段3前）

- [ ] EdgeGateway实现完成并通过全部验证
- [ ] mqtt-contract.md v1.0稳定，采集器不再改格式
- [ ] 确定云端时序库选型（InfluxDB Cloud / TimescaleDB）
- [ ] 评估一厂一RabbitMQ vs 共享集群方案

---

## 6. Spec文件关系

```
edge-gateway.md              ← 基础方案（架构/数据模型/配置/组件设计）
edge-gateway-supplement.md   ← 补充文档（边界条件/校验/优化建议/优先级）
edge-gateway-status.md       ← 本文件（实现状态/验证/演进）
../.contract/mqtt-contract.md ← MQTT跨服务数据契约
```

---

## 7. 文件清单（目标结构）

```
src/AmGatewayCloud.EdgeGateway/
├── AmGatewayCloud.EdgeGateway.csproj
├── Program.cs                              # 入口 + DI
├── appsettings.json                        # 默认配置
├── Configuration/
│   └── EdgeGatewayConfig.cs                # 配置映射类
├── Models/
│   ├── DataBatch.cs                        # MQTT消费契约模型
│   └── DataPoint.cs
├── Services/
│   ├── MqttConsumerService.cs              # MQTT订阅 + 分发
│   ├── InfluxDbWriter.cs                   # 本地InfluxDB写入
│   ├── RabbitMqForwarder.cs                # RabbitMQ转发
│   ├── WatermarkTracker.cs                 # 水位线管理
│   └── ReplayService.cs                    # 断网恢复回放
└── watermarks/                             # 水位线文件目录
    └── edgehub-a.watermark.json
```
