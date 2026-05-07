# AmGatewayCloud.Collector.Modbus — 实现状态总览

> 阶段1交付总结：对照 `collector-modbus.md`（方案）+ `collector-modbus-supplement.md`（补充）逐项核对。

**生成时间**：2026-05-07

---

## 1. 完成度概览

```
基础方案 (collector-modbus.md)  ████████████████████ 11/11  100%
P0 补充项                       ████████████████████  4/4   100%
P1 补充项                       ████████████████████  5/5   100%
P2 补充项                       ████████████████▒▒▒▒  2/3    67%
P3 补充项                       ████████▒▒▒▒▒▒▒▒▒▒▒▒  2/6    33%
代码审查遗留                     ████████▒▒▒▒▒▒▒▒▒▒▒▒  1/3    33%
MQTT 输出通道                   ████████████████████  1/1   100%
```

---

## 2. 基础方案逐章核对

| 章节 | 内容 | 状态 | 实际实现说明 |
|------|------|------|-------------|
| §1 定位 | 独立微服务，轮询模式 | ✅ | 完全对齐 |
| §2 架构 | ModbusConnection + ModbusCollectorService + IDataOutput | ✅ | 架构图实现一致 |
| §3 数据模型 | DataPoint / 寄存器值转换 | ✅ | 且补了 Quality + ValueType 字段 |
| §4 配置模型 | appsettings.json + 配置映射类 | ✅ | 且比方案多了 ConnectTimeoutMs / TagScales |
| §5.1 ModbusConnection | 连接管理 + 自动重连 | ✅ | 指数退避 + ConnectTimeoutMs |
| §5.2 ModbusCollectorService | 采集主循环 | ✅ | 全部失败主动重连 |
| §5.3 IDataOutput | 输出抽象 | ✅ | |
| §5.4 ConsoleDataOutput | 控制台输出 | ✅ | 批量输出带组名前缀 |
| §6 依赖注入 | Program.cs | ✅ | 且有 OptionsValidationException 捕获 |
| §7 错误处理策略 | 各场景处理 | ✅ | |
| §8~10 项目文件/结构/验证 | csproj + 目录布局 + 运行验证 | ✅ | 可运行 |
| §11 后续演进 | 各阶段规划 | — | 不涉及代码实现 |

---

## 3. 补充方案逐项核对

### 🔴 P0 — 必须完成

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 1.1 | Count 上限校验（Holding/Input ≤125, Coil/Discrete ≤2000） | ✅ | `CollectorConfigValidator` 完整校验 |
| 4.1 | DataPoint.Value 序列化（ValueType 字段） | ✅ | `ValueType` 字段 + `MapValueType()` 统一映射 |
| 3.1 | 全部寄存器组失败时主动重连 | ✅ | 一轮轮询后检查 `failedCount == total`，触发 `ReconnectAsync` |
| 3.3 | CancellationToken 传入 Task.Delay | ✅ | `await Task.Delay(PollIntervalMs, ct)` |

### 🟡 P1 — 重要边界条件

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 2.1 | 连接超时配置（ConnectTimeoutMs） | ✅ | `ModbusConfig.ConnectTimeoutMs = 5000`，ConnectAsync 中用链接 CTS |
| 2.2 | 重连指数退避 | ✅ | 5s → 10s → 20s → 40s → 60s 上限，连接成功后重置 |
| 1.2 | 地址区间重叠检测 | ✅ | 同类型寄存器组 `[Start, Start+Count)` 区间不重叠校验 |
| 1.3 | Tags 非空校验 + Count 匹配 | ✅ | `Tags.Count == Count`，Tags 不能含空字符串 |
| 2.3 | volatile IsConnected | ✅ | `private volatile bool _isConnected` |

### 🟢 P2 — 优化建议

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 5.1 | Scale + Offset 替代 ScaleFactor | ✅ | 保留 `ScaleFactor` 兼容 + 新增 `TagScales` 按标签覆盖缩放因子 |
| 4.2 | 采集质量标记（DataQuality） | ✅ | `Good / Bad / Unknown` 枚举 + `DataPoint.Good()` / `DataPoint.Bad()` 工厂方法 |
| 3.2 | 严格轮询周期（PeriodicTimer） | ❌ | 当前用 `Task.Delay` 够用，看场景需求 |

### ⚪ P3 — 后续优化

| # | 条目 | 状态 | 实现说明 |
|---|------|------|---------|
| 9.1 | 多设备支持 (List\<ModbusDeviceConfig\>) | ❌ | 阶段1单设备够用 |
| 9.2 | NModbus 版本锁定 | ✅ | 已锁定 `3.0.83`，与 AmGateway 一致 |
| 9.3 | 32位寄存器支持（RegisterWidth / ByteOrder） | ❌ | 当前虚拟从站用16位，工业场景常见 |
| 9.4 | 健康检查端点 | ❌ | 阶段5容器化时加 |
| 9.5 | 数据点去重/变化检测（OnlyOnChange） | ❌ | 数据量大时再考虑 |

---

## 4. 代码审查遗留问题

| # | 条目 | 优先级 | 状态 | 说明 |
|---|------|--------|------|------|
| 11.1 | ReadWithRetryAsync 锁范围竞态 | P3 | ❌ 未修复 | `lock` 检查 `_isConnected` 后释放锁，`readFunc()` 期间 `_master` 可能被重连线程 Dispose。当前单线程轮询不影响，多设备复用时必须解决 |
| 11.2 | DataPoint.Value 序列化需自定义 JsonConverter | P2 | ❌ 未实现 | `ValueType` 解决了"下游知道类型"问题，但 `object?` 用 System.Text.Json 序列化仍需自定义 Converter。阶段3切 RabbitMQ 前必须实现 |
| 11.3 | ConsoleDataOutput.WriteBatchAsync 缺少分组名 | P3 | ✅ 已修复 | 已在 `DataPoint.Properties["GroupName"]` 中存储组名，输出带组名前缀 |

---

## 5. 超出方案的实现亮点

以下功能在方案/补充文档中未提及或标注为更低优先级，但实际已实现：

| 亮点 | 说明 |
|------|------|
| **TagScales 按标签覆盖缩放因子** | 方案只有组级 `ScaleFactor`，实际增加了 `TagScales` 字典，支持同组内不同标签使用不同缩放因子（如 rpm=1.0, current=100.0） |
| **OptionsValidationException 友好输出** | Program.cs 中捕获配置校验异常，逐条输出所有失败项，而非只看到第一个异常 |
| **Bootstrap Logger** | Serilog 引导日志 + 正式日志两阶段模式，确保启动前和启动失败时均有日志输出 |
| **Serilog 9.0.0** | 方案写的是 `8.*`，实际用了 `9.0.0`，功能更强 |
| **DataPoint.Good() / Bad() 工厂方法** | 补充文档只提了加字段，实际实现含工厂方法 + `MapValueType` 统一映射，与 OPC UA 采集器对齐 |

---

## 6. 已知遗留项

| 条目 | 优先级 | 影响 | 后续计划 |
|------|--------|------|---------|
| 严格轮询周期（PeriodicTimer） | P2 | 当前 `Task.Delay` 实际周期 = 读取耗时 + 间隔 | 看场景需求，性能敏感时改 |
| DataPoint.Value JSON 自定义 Converter | P2 | `object?` 序列化会丢值类型信息 | **阶段3切 RabbitMQ 前必须实现** |
| ReadWithRetryAsync 锁范围竞态 | P3 | 当前单线程不影响 | 多设备支持时一并解决 |
| 多设备支持 | P3 | 一个实例只能连一个从站 | 阶段5+按需重构 |
| 32位寄存器支持 | P3 | 无法表达 INT32/FLOAT 等双字类型 | 工业场景常见，按需加 RegisterWidth |
| 健康检查端点 | P3 | 无 K8s 就绪探针 | 阶段5容器化时加 |
| 变化检测去重 | P3 | 每轮全量输出，值不变也输出 | 数据量大或带宽敏感时启用 |

---

## 7. 与 OPC UA 采集器的差异对齐

| 方面 | Modbus | OPC UA | 统一方案 |
|------|--------|--------|---------|
| **DataQuality 枚举** | Good / Bad / Unknown | Good / Uncertain / Bad | 阶段3前抽取 Abstractions，统一为 Good/Uncertain/Bad，Modbus 的 `Unknown` → `Uncertain` |
| **ValueType 映射** | `MapValueType()` | `MapValueType()` | 抽取到 Abstractions，共用一份映射 |
| **DataPoint 模型** | 各自独立定义 | 各自独立定义 | 抽取到 Abstractions |
| **IDataOutput 接口** | 各自独立定义 | 各自独立定义 | 抽取到 Abstractions |
| **ConsoleDataOutput** | 各自独立实现 | 各自独立实现 | 抽取到 Abstractions |
| **数据获取模式** | 轮询（Task.Delay 间隔） | 订阅（服务器推送） | 模式不同，各自保留 |
| **ScaleFactor / TagScales** | 有（寄存器原始值需缩放） | 无（OPC UA 返回工程值） | Modbus 特有，保留 |
| **Writable 伏笔** | ❌ 无 | ✅ NodeConfig.Writable | Modbus 的 Coil 天然可写，后续可加 |
| **配置校验** | IValidateOptions | IValidateOptions | 模式统一 |
| **MQTT 输出** | ✅ MqttOutput | ✅ MqttOutput | 结构一致，Topic 区分：modbus/opcua |
| **MQTT ClientId** | AmGatewayCloud-Modbus-{DeviceId} | AmGatewayCloud-OpcUa-{DeviceId} | 拼接 DeviceId 避免多实例冲突 |
| **MQTT Topic** | amgateway/modbus/{DeviceId} | amgateway/opcua/{DeviceId} | 协议名区分 |

---

## 8. 实际运行测试关注点

### 8.1 基础功能验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| 启动连接 | 启动 AmVirtualSlave → 启动 Collector | 控制台持续输出 6 组数据 |
| 配置校验 | 改错 Host / Count 超限 / Tags 不匹配 | 启动即报错，列出所有失败项 |
| TagScales | 观察 current/frequency 的值 | current=5.3（÷100）, frequency=50.0（÷100） |

### 8.2 断线恢复验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| 服务器重启 | 停掉 AmVirtualSlave → 等几秒 → 重启 | 日志：Read failed → Reconnecting → Reconnected → 数据恢复 |
| 指数退避 | 观察重连间隔 | 5s → 10s → 20s → 40s → 60s 上限 |
| 全部失败触发重连 | 所有组都读取失败 | 主动触发 ReconnectAsync，不等下一轮 |
| 连接超时 | 连接不可达的 IP | 5s 后超时，进入重连循环 |

### 8.3 数据质量验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| Good 数据 | 正常运行 | `tag=value` 格式 |
| Bad 数据 | 读取失败时 | `tag=<BAD>` 标记 |
| ValueType | 观察输出 | float/bool/int 等正确标识 |

### 8.4 优雅关机验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| Ctrl+C | 运行中按 Ctrl+C | 日志：Stopping → Stopped，无卡住 |
| Task.Delay 取消 | 确认 Delay 期间 Ctrl+C | 立即响应，不等 Delay 结束 |

### 8.5 边界条件验证

| 测试项 | 操作 | 预期 |
|--------|------|------|
| 地址重叠 | 配置两个 Holding 组地址重叠 | 启动校验报错 |
| Tags 数量不匹配 | Tags.Count ≠ Count | 启动校验报错 |
| 跨类型地址空间 | Discrete 和 Holding 同地址 | 允许（各自地址空间独立） |

---

## 9. 后续演进路线图

对照项目路线图 (`roadmap.md`)，Modbus 采集器在各阶段的任务：

| 阶段 | 变化 | 前置条件 |
|------|------|---------|
| **阶段1 (当前)** | ✅ 控制台输出闭环 | 无 |
| **阶段1+** | ✅ 新增 `MqttOutput : IDataOutput` | 本地 Mosquitto 验证通过 |
| **阶段2** | 新增 `InfluxDbOutput : IDataOutput` | InfluxDB 部署 |
| **阶段3a** | 边缘聚合网关（Edge Hub）消费 MQTT → AMQP；抽取 `Collector.Abstractions`；实现 `DataPointJsonConverter` | RabbitMQ 部署；**阶段3前必须完成 Abstractions 抽取和 JsonConverter** |
| **阶段3b** | 无变化（采集器本身不变） | — |
| **阶段4** | 无变化 | — |
| **阶段5** | 容器化 (Dockerfile)；健康检查端点；Serilog→Seq；OpenTelemetry 追踪 | Docker 环境 |
| **阶段6** | 配置中心下发 TenantId；Writable 写操作（Coil 天然可写） | 配置中心；业务需求 |

### 关键 Check-list（阶段3 前）

- [ ] 抽取 `AmGatewayCloud.Collector.Abstractions` 项目
  - [ ] `DataPoint` 模型（含 Quality + ValueType + MapValueType）
  - [ ] `DataQuality` 枚举（统一为 Good/Uncertain/Bad）
  - [ ] `IDataOutput` 接口
  - [ ] `ConsoleDataOutput` 实现
- [ ] Modbus `DataQuality.Unknown` → `DataQuality.Uncertain` 映射
- [ ] 实现 `DataPointJsonConverter`（`object?` 序列化/反序列化）
- [ ] 两个采集器引用 Abstractions，移除各自的 Models/Output 重复定义
- [ ] `RabbitMqOutput : IDataOutput` 实现

---

## 10. 文件清单

```
src/AmGatewayCloud.Collector.Modbus/
├── AmGatewayCloud.Collector.Modbus.csproj
├── Program.cs                              # 入口 + DI + 异常处理 + Bootstrap Logger + MQTT 条件注册
├── appsettings.json                        # 6 组 54 标签默认配置 + TagScales + MQTT 配置
├── ModbusConnection.cs                     # 连接管理 + 自动重连 + 指数退避
├── ModbusCollectorService.cs               # BackgroundService 采集主循环
├── Configuration/
│   ├── CollectorConfig.cs                  # 顶层配置（含 MqttConfig）
│   ├── ModbusConfig.cs                     # Modbus 连接参数（含 ConnectTimeoutMs）
│   ├── MqttConfig.cs                       # MQTT 输出通道配置（Broker/Topic/认证/重连）
│   ├── RegisterGroupConfig.cs              # 寄存器组配置（含 TagScales）
│   ├── RegisterType.cs                     # Holding/Input/Discrete/Coil 枚举
│   └── CollectorConfigValidator.cs         # IValidateOptions 启动校验
├── Models/
│   ├── DataPoint.cs                        # 统一数据模型 + MapValueType + Good/Bad 工厂方法
│   └── DataQuality.cs                      # Good/Bad/Unknown 枚举
└── Output/
    ├── IDataOutput.cs                      # 输出抽象接口
    ├── ConsoleDataOutput.cs                # 阶段1控制台输出（带组名前缀）
    └── MqttOutput.cs                       # MQTT 输出通道（批量 JSON + 指数退避重连 + 懒连接）
```

---

## 11. MQTT 输出通道（阶段1+ 新增）

> 两个采集器均已实现 MQTT 输出通道，作为 Console 之外的第二输出，为后续边缘聚合网关（Edge Hub）对接做准备。

### 11.1 设计要点

| 特性 | 实现方式 |
|------|---------|
| **懒连接** | 首次 `WriteBatchAsync` 时自动连接，不阻塞启动 |
| **指数退避重连** | 5s → 10s → 20s → 40s → 60s 上限，与 Modbus 重连策略一致 |
| **断线数据静默丢弃** | 不阻塞采集主循环，边缘采集器核心原则："采集优先，输出其次" |
| **批量 JSON 打包** | `WriteBatchAsync` 将整批 DataPoint 合并为一条 JSON 消息 |
| **ClientId 拼接 DeviceId** | `AmGatewayCloud-Modbus-{DeviceId}`，避免多实例冲突 |
| **Topic 格式** | `{TopicPrefix}/modbus/{DeviceId}`，如 `amgateway/modbus/simulator-001` |
| **条件注册** | `Mqtt.Enabled=true` 时才注册 `MqttOutput` 到 DI |
| **双输出并行** | `IEnumerable<IDataOutput>` 注入，Console + MQTT 同时输出 |

### 11.2 MQTT 配置项

| 配置键 | 默认值 | 说明 |
|--------|--------|------|
| `Mqtt:Enabled` | `false` | 是否启用 MQTT 输出 |
| `Mqtt:Broker` | `localhost` | MQTT Broker 地址 |
| `Mqtt:Port` | `1883` | Broker 端口 |
| `Mqtt:TopicPrefix` | `amgateway` | Topic 前缀 |
| `Mqtt:ClientId` | `AmGatewayCloud-Modbus` | 客户端标识（运行时拼接 DeviceId） |
| `Mqtt:UseTls` | `false` | 是否启用 TLS |
| `Mqtt:Username` | — | 认证用户名（可选） |
| `Mqtt:Password` | — | 认证密码（可选） |
| `Mqtt:ReconnectDelayMs` | `5000` | 重连基准间隔（毫秒） |
| `Mqtt:MaxReconnectDelayMs` | `60000` | 指数退避上限（毫秒） |

### 11.3 MQTT 输出 JSON 格式

```json
{
  "deviceId": "simulator-001",
  "timestamp": "2026-05-07T12:00:00Z",
  "points": [
    {
      "tag": "temperature",
      "value": 28.84,
      "valueType": "float",
      "quality": "Good",
      "timestamp": "2026-05-07T12:00:00Z",
      "groupName": "sensors"
    }
  ]
}
```

### 11.4 运行验证

- **Broker**: 本地 Mosquitto（`localhost:1883`）
- **订阅命令**: `mosquitto_sub -h localhost -p 1883 -t "amgateway/#" -v`
- **验证结果**: `amgateway/modbus/simulator-001` 主题持续收到 JSON 数据，Console 和 MQTT 双输出并行正常

### 11.5 踩坑记录

详见 `.errors/collector-modbus-issues.md`，核心问题：
- MQTTnet 5.x 命名空间重组（所有类型在 `MQTTnet` 根命名空间）
- `MqttFactory` → `MqttClientFactory` 工厂类更名
- `WithTls()` → `WithTlsOptions()` TLS 配置 API 变更
- `WithCleanSession` → `WithCleanStart` MQTT 5.0 术语更名
- QoS 枚举迁移至 `MQTTnet.Protocol` 子命名空间

---

## 12. Spec 文件关系

```
collector-modbus.md           ← 基础方案（架构/数据模型/配置/组件设计）
collector-modbus-supplement.md ← 补充文档（边界条件/校验/增强/优化建议）
collector-modbus-status.md    ← 本文件（实现状态/MQTT输出/遗留项/测试/演进）
../errors/collector-modbus-issues.md ← 踩坑记录（MQTTnet 5.x API 不兼容等）
```
