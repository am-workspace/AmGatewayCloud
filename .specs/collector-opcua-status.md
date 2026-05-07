# AmGatewayCloud.Collector.OpcUa — 实现状态总览

> 阶段1交付总结：对照 `collector-opcua.md`（方案）+ `collector-opcua-supplement.md`（补充）逐项核对。

**生成时间**：2026-05-07

---

## 1. 完成度概览

```
基础方案 (collector-opcua.md)  ████████████████████ 13/13  100%
P0 补充项                      ████████████████████  5/5   100%
P1 补充项                      ██████████████████▒▒  7/8    88%
P2 补充项                      ████████████████▒▒▒▒  6/8    75%
P3 补充项                      ████████████▒▒▒▒▒▒▒▒  3/5    60%
MQTT 输出通道                   ████████████████████  1/1   100%
```

---

## 2. 基础方案逐章核对

| 章节 | 内容 | 状态 | 实际实现说明 |
|------|------|------|-------------|
| §1 定位 | 独立微服务，订阅模式 | ✅ | 完全对齐 |
| §2 架构 | Session + Subscription + MonitoredItem | ✅ | 架构图实现一致 |
| §3 数据模型 | DataPoint / 值类型映射 / StatusCode→质量 | ✅ | 且补了 Quality + ValueType 字段 |
| §4 配置模型 | appsettings.json + 配置映射类 | ✅ | 且比方案多了 AuthMode/QueueSize/FlushIntervalMs/DeadbandPercent |
| §5.1 OpcUaSession | 会话管理 + 自动重连 | ✅ | 指数退避、KeepAlive、FetchNamespaceTables 全有 |
| §5.2 OpcUaCollectorService | 采集主服务 | ✅ | ConcurrentQueue + Timer 批量刷出 |
| §5.3 IDataOutput | 输出抽象 | ✅ | |
| §5.4 ConsoleDataOutput | 控制台输出 | ✅ | 批量输出带组名前缀 |
| §6 依赖注入 | Program.cs | ✅ | 且有 OptionsValidationException 捕获 |
| §7 错误处理策略 | 各场景处理 | ✅ | |
| §8 项目文件 | csproj | ✅ | Serilog 版本比方案高（9.0.0 vs 8.*） |
| §9 项目结构 | 目录布局 | ✅ | 且多了 CollectorConfigValidator.cs |
| §10 会话建立流程 | ConnectAsync 详细流程 | ✅ | 且有 NullTelemetryContext 适配 SDK 1.5.x |
| §11 运行验证 | 启动/断线验证 | ✅ | 可运行 |
| §12 代码复用 | 与 Modbus 对比 | ✅ | DataPoint.MapValueType 注释了后续抽取到 Abstractions |
| §13 后续演进 | 各阶段规划 | — | 不涉及代码实现 |

---

## 3. 补充方案逐项核对

### 🔴 P0 — 必须完成

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 1.1 | DataPoint 补 Quality + ValueType | ✅ | `Quality` 和 `ValueType` 字段 + `Good()`/`Bad()` 工厂方法 |
| 1.2 | DataQuality 枚举统一 (Good/Uncertain/Bad) | ✅ | 三值枚举，注释说明 Modbus Unknown→Uncertain |
| 1.3 | ValueType 命名映射统一 (MapValueType) | ✅ | double/float/int/bool 等统一映射 |
| 2 | MonitoredItem→Tag 映射 (Handle方案) | ✅ | `item.Handle = (Group: groupName, Node: nodeConfig)` 元组 |
| 3.1 | 重建订阅前清理旧 Subscription | ✅ | `CreateSubscriptionAsync` 开头先 `_subscription?.DeleteAsync()` |
| 3.2 | 重建过程中又断线 try-catch | ✅ | `OnSessionRestored` 事件处理器有 try-catch 兜底 |
| 3.3 | 重连后 FetchNamespaceTables + 重解析 NodeId | ✅ | `ReconnectAsync` 中调 `FetchNamespaceTablesAsync()`，`CreateSubscriptionAsync` 每次都调 `GetNamespaceIndex()` |

### 🟡 P1 — 重要边界条件

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 4.1 | OpcUaConfig 校验 (Endpoint/Policy/时间参数) | ✅ | `CollectorConfigValidator` 全覆盖 |
| 4.2 | NodeGroups 校验 (唯一性/非空/NodeId/Tag) | ✅ | 组名唯一、NodeId唯一、Tag唯一、非空校验全有 |
| 4.3 | 全局 Tag 唯一性校验 (Warn) | ✅ | 跨组 Tag 重复检测，以 `Warning:` 前缀输出 |
| 5.1 | NamespaceUri→NamespaceIndex 正确解析 | ✅ | 遍历 `NamespaceUris` 查找，非 `GetIndex()` |
| 5.2 | NodeId 解析逻辑 (短名 vs 完整字符串) | ✅ | `ParseNodeId()` 含 `=` 直接 Parse，否则构造 NodeId |
| 5.3 | 用户身份验证可配置 | ✅ | AuthMode/UserName/Password + `BuildIdentity()` |
| 5.4 | SecurityPolicy 字符串→URI 映射 | ✅ | `DiscoverEndpointAsync` 中做映射，无匹配时 Warn |
| 5.5 | 端点发现异常处理 | ✅ | 空 endpoints 时抛异常 |
| 5.6 | volatile IsConnected | ✅ | `private volatile bool _isConnected` |
| 5.7 | KeepAlive 检测 (ServerState) | ✅ | `OnKeepAlive` 检查 `ServiceResult.IsBad` 和 `ServerState != Running` |
| 6 | 通知回调线程安全 (ConcurrentQueue) | ✅ | `ConcurrentQueue<DataPoint>` + Timer 刷出 |

### 🟢 P2 — 优化建议

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 7 | 批量输出适配推送模式 | ✅ | `FlushPendingPoints` 按 GroupName 分组 + Tag 排序后批量输出 |
| 8.1 | OpcUaConfig 新增字段 (QueueSize/FlushIntervalMs/AuthMode) | ✅ | 全部有 |
| 8.2 | NodeConfig.Writable 写操作伏笔 | ✅ | `bool Writable = false`，注释标注"阶段6+启用" |
| 8.3 | appsettings.json 补充 | ✅ | AuthMode/QueueSize/FlushIntervalMs 都有 |
| 9.1 | 优雅关机先删订阅再断 Session | ✅ | ExecuteAsync 退出时：停Timer→刷数据→删订阅→断Session |
| 9.2 | OpcUaSession.Dispose 顺序 | ✅ | 取消CTS→关Session→清理事件 |
| 10.1 | 配置校验注入 (IValidateOptions) | ✅ | 用 `IValidateOptions<CollectorConfig>` |
| — | DataQuality 枚举与 Modbus 统一 | ⚠️ | 代码已用 Good/Uncertain/Bad，但 Modbus 端仍是 Good/Bad/Unknown，待 Abstractions 抽取时统一 |

### ⚪ P3 — 后续优化

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 12.1 | 多设备支持 | ❌ | 留 TODO，符合预期（阶段1单设备够用） |
| 12.2 | 健康检查端点 | ❌ | 符合预期（阶段5容器化时加） |
| 12.3 | SDK .NET 10 兼容性 POC | ✅ | 代码可运行，且适配了 SDK 1.5.x（NullTelemetryContext） |
| 12.4 | ServerTimestamp 存入 Properties | ✅ | Good 和 Bad 数据都记录了 ServerTimestamp |
| 12.5 | Deadband 死区配置 | ✅ | NodeConfig.DeadbandPercent + DataChangeFilter |

---

## 4. 超出方案的实现亮点

以下功能在方案/补充文档中未提及或标注为更低优先级，但实际已实现：

| 亮点 | 说明 |
|------|------|
| **NullTelemetryContext** | 适配 OPC UA SDK 1.5.x 的 `ITelemetryContext` 接口，避免运行时 MissingMethodException |
| **DiscardOldest = true** | MonitoredItem 设置了丢弃最旧通知，防止队列溢出时阻塞 |
| **OptionsValidationException 捕获** | Program.cs 中对配置校验失败有专门的异常处理和友好日志 |
| **DeadbandPercent（P3→已实现）** | 补充文档标为 P3，但实际已完整实现 |
| **ServerTimestamp（P3→已实现）** | 补充文档标为 P3，但实际已完整实现 |
| **指数退避重连** | 方案只提了"循环重试"，实际实现了 5s→10s→20s→40s→60s 上限的指数退避 |

---

## 5. 已知遗留项

| 条目 | 优先级 | 影响 | 后续计划 |
|------|--------|------|---------|
| 多设备支持 (List\<OpcUaDeviceConfig\>) | P3 | 阶段1单设备够用 | 阶段5+按需重构 |
| 健康检查端点 (/health/ready) | P3 | 无 K8s 就绪探针 | 阶段5容器化时加 |
| DataQuality 枚举与 Modbus 统一 | P2 | 两端枚举值不一致 | 阶段3前抽取 `Collector.Abstractions` |
| 证书认证 (AuthMode=Certificate) | P3 | BuildIdentity 中 TODO | 阶段6+安全增强 |
| NodeConfig.Writable 写操作 | P2 | 字段已预留，逻辑未实现 | 阶段6+双向通信 |

---

## 6. 实际运行测试关注点

### 6.1 基础功能验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| 启动连接 | 启动 AmVirtualSlave → 启动 Collector | 控制台持续输出 4 组数据 |
| 配置校验 | 改错 Endpoint 格式 / SecurityPolicy | 启动即报错，不运行 |
| 匿名连接 | SecurityPolicy=None | 正常连接 |
| 用户名连接 | AuthMode=UserName + 凭据 | 正常连接（需服务器支持） |

### 6.2 断线恢复验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| 服务器重启 | 停掉 AmVirtualSlave → 等几秒 → 重启 | 日志：Session closed → Reconnecting → Reconnected → 数据恢复 |
| 指数退避 | 观察重连间隔 | 5s → 10s → 20s → 40s → 60s 上限 |
| 重建订阅 | 重连后观察 | 40 个 MonitoredItem 重建，数据恢复输出 |
| 多次断连 | 反复停启服务器 | 每次都能恢复，不崩溃不泄漏 |

### 6.3 数据质量验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| Good 数据 | 正常运行 | `tag=value` 格式 |
| Bad 数据 | 使某节点不可读 | `tag=<BAD>` 标记 |
| ValueType | 观察输出 | double/int/bool/string 等正确标识 |

### 6.4 优雅关机验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| Ctrl+C | 运行中按 Ctrl+C | 日志：Stopping → 刷出剩余数据 → 删订阅 → 断Session → Stopped |
| 服务器端残留 | 关机后检查 OPC UA 服务器 | 无残留 Subscription |

### 6.5 边界条件验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| 错误 NodeId | 配置一个不存在的 NodeId | Warn 日志 + 跳过该节点，其他节点正常 |
| 跨组 Tag 重复 | 两个组用相同 Tag | 启动时 Warning 日志 |
| 高频数据 | 快速变化节点 | QueueSize=10 + DiscardOldest 丢旧保新 |

---

## 7. 后续演进路线图

对照项目路线图 (`roadmap.md`)，OPC UA 采集器在各阶段的任务：

| 阶段 | 变化 | 前置条件 |
|------|------|---------|
| **阶段1 (当前)** | ✅ 控制台输出闭环 | 无 |
| **阶段1+** | ✅ 新增 `MqttOutput : IDataOutput` | 本地 Mosquitto 验证通过 |
| **阶段2** | 新增 `InfluxDbOutput : IDataOutput` | InfluxDB 部署 |
| **阶段3a** | 边缘聚合网关（Edge Hub）消费 MQTT → AMQP；抽取 `Collector.Abstractions`（DataPoint + IDataOutput + DataQuality + MapValueType） | RabbitMQ 部署；**阶段3前必须完成 Abstractions 抽取** |
| **阶段3b** | 无变化（采集器本身不变） | — |
| **阶段4** | 无变化 | — |
| **阶段5** | 容器化 (Dockerfile)；健康检查端点；Serilog→Seq；OpenTelemetry 追踪 | Docker 环境 |
| **阶段6** | 配置中心下发 TenantId；NodeConfig.Writable 启用写操作；证书认证 | 配置中心；业务需求 |

### 关键 Check-list（阶段3 前）

- [ ] 抽取 `AmGatewayCloud.Collector.Abstractions` 项目
  - [ ] `DataPoint` 模型（含 Quality + ValueType + MapValueType）
  - [ ] `DataQuality` 枚举（统一为 Good/Uncertain/Bad）
  - [ ] `IDataOutput` 接口
  - [ ] `ConsoleDataOutput` 实现
- [ ] Modbus 采集器 `DataQuality.Unknown` → `DataQuality.Uncertain` 映射
- [ ] 两个采集器引用 Abstractions，移除各自的 Models/Output 重复定义
- [ ] `RabbitMqOutput : IDataOutput` 实现

---

## 8. 文件清单

```
src/AmGatewayCloud.Collector.OpcUa/
├── AmGatewayCloud.Collector.OpcUa.csproj
├── Program.cs                              # 入口 + DI + 异常处理 + MQTT 条件注册
├── appsettings.json                        # 4 组 40 节点默认配置 + MQTT 配置
├── OpcUaSession.cs                         # 会话管理 + 自动重连 + 命名空间解析
├── OpcUaCollectorService.cs                # BackgroundService 采集主服务
├── Configuration/
│   ├── CollectorConfig.cs                  # 配置映射类（含 MqttConfig + Writable/DeadbandPercent 伏笔）
│   ├── MqttConfig.cs                       # MQTT 输出通道配置（Broker/Topic/认证/重连）
│   └── CollectorConfigValidator.cs         # IValidateOptions 启动校验
├── Models/
│   ├── DataPoint.cs                        # 统一数据模型 + MapValueType
│   └── DataQuality.cs                      # Good/Uncertain/Bad 枚举
└── Output/
    ├── IDataOutput.cs                      # 输出抽象接口
    ├── ConsoleDataOutput.cs                # 阶段1控制台输出
    └── MqttOutput.cs                       # MQTT 输出通道（批量 JSON + 指数退避重连 + 懒连接）
```

---

## 9. MQTT 输出通道（阶段1+ 新增）

> 两个采集器均已实现 MQTT 输出通道，作为 Console 之外的第二输出，为后续边缘聚合网关（Edge Hub）对接做准备。

### 9.1 设计要点

| 特性 | 实现方式 |
|------|---------|
| **懒连接** | 首次 `WriteBatchAsync` 时自动连接，不阻塞启动 |
| **指数退避重连** | 5s → 10s → 20s → 40s → 60s 上限，与 OPC UA 会话重连策略一致 |
| **断线数据静默丢弃** | 不阻塞采集主循环，边缘采集器核心原则："采集优先，输出其次" |
| **批量 JSON 打包** | `WriteBatchAsync` 将整批 DataPoint 合并为一条 JSON 消息 |
| **ClientId 拼接 DeviceId** | `AmGatewayCloud-OpcUa-{DeviceId}`，避免多实例冲突 |
| **Topic 格式** | `{TopicPrefix}/opcua/{DeviceId}`，如 `amgateway/opcua/simulator-001` |
| **条件注册** | `Mqtt.Enabled=true` 时才注册 `MqttOutput` 到 DI |
| **双输出并行** | `IEnumerable<IDataOutput>` 注入，Console + MQTT 同时输出 |

### 9.2 MQTT 配置项

| 配置键 | 默认值 | 说明 |
|--------|--------|------|
| `Mqtt:Enabled` | `false` | 是否启用 MQTT 输出 |
| `Mqtt:Broker` | `localhost` | MQTT Broker 地址 |
| `Mqtt:Port` | `1883` | Broker 端口 |
| `Mqtt:TopicPrefix` | `amgateway` | Topic 前缀 |
| `Mqtt:ClientId` | `AmGatewayCloud-OpcUa` | 客户端标识（运行时拼接 DeviceId） |
| `Mqtt:UseTls` | `false` | 是否启用 TLS |
| `Mqtt:Username` | — | 认证用户名（可选） |
| `Mqtt:Password` | — | 认证密码（可选） |
| `Mqtt:ReconnectDelayMs` | `5000` | 重连基准间隔（毫秒） |
| `Mqtt:MaxReconnectDelayMs` | `60000` | 指数退避上限（毫秒） |

### 9.3 MQTT 输出 JSON 格式

```json
{
  "deviceId": "simulator-001",
  "timestamp": "2026-05-07T12:00:00Z",
  "points": [
    {
      "tag": "temperature",
      "value": 28.84,
      "valueType": "double",
      "quality": "Good",
      "timestamp": "2026-05-07T12:00:00Z",
      "groupName": "sensors"
    }
  ]
}
```

### 9.4 运行验证

- **Broker**: 本地 Mosquitto（`localhost:1883`）
- **订阅命令**: `mosquitto_sub -h localhost -p 1883 -t "amgateway/#" -v`
- **验证结果**: `amgateway/opcua/simulator-001` 主题持续收到 JSON 数据，Console 和 MQTT 双输出并行正常
- **运行日志确认**: `MQTT connected to localhost:1883, Topic: amgateway/opcua/simulator-001`

### 9.5 与 Modbus MQTT 输出对齐

| 方面 | Modbus MqttOutput | OPC UA MqttOutput | 状态 |
|------|-------------------|-------------------|------|
| 代码结构 | 完全一致 | 完全一致 | ✅ 对齐 |
| MqttConfig 类 | 字段相同，ClientId 不同 | 字段相同，ClientId 不同 | ✅ 对齐 |
| JSON 序列化格式 | 相同 MqttBatch + MqttDataPoint | 相同 MqttBatch + MqttDataPoint | ✅ 对齐 |
| Topic 协议名 | modbus | opcua | ✅ 区分 |
| 懒连接 + 退避 | 相同策略 | 相同策略 | ✅ 对齐 |
| 断线静默丢弃 | 相同策略 | 相同策略 | ✅ 对齐 |

---

## 10. Spec 文件关系

```
collector-opcua.md           ← 基础方案（架构/数据模型/配置/组件设计）
collector-opcua-supplement.md ← 补充文档（边界条件/校验/线程安全/优化建议）
collector-opcua-status.md    ← 本文件（实现状态/MQTT输出/遗留项/测试/演进）
../errors/collector-opcua-issues.md ← 踩坑记录（SDK 1.5.x API 不兼容等）
```
