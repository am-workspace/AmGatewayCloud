# Modbus Collector — 关键问题记录

> 记录开发过程中遇到的超出预期的 API 不兼容、SDK 文档缺失等问题，供后续参考。

---

## 1. MQTTnet 5.x 所有类型在 `MQTTnet` 根命名空间，无 `MQTTnet.Client` 子命名空间

**现象**：编译报错 — `using MQTTnet.Client` 命名空间不存在。

**根因**：MQTTnet 5.1.0.1559 中，`IMqttClient`、`MqttClientOptions`、`MqttClientOptionsBuilder`、`MqttApplicationMessageBuilder` 等所有客户端类型均位于 `MQTTnet` 根命名空间。旧版本（3.x/4.x）曾将客户端类型放在 `MQTTnet.Client` 子命名空间，5.x 已全部移至根命名空间。

**修复**：移除 `using MQTTnet.Client`，仅保留 `using MQTTnet`。

**教训**：MQTTnet 5.x 是一次较大的 API 重组，不要依赖 3.x/4.x 的 using 路径，始终通过 IDE 智能提示或反射确认实际命名空间。

---

## 2. MQTTnet 5.x 工厂类为 `MqttClientFactory` 而非 `MqttFactory`

**现象**：编译报错 — `MqttFactory` 类型不存在。

**根因**：MQTTnet 5.1.0.1559 中创建客户端的工厂类名为 `MqttClientFactory`，而非文档和示例中常见的 `MqttFactory`。不同版本工厂类名不稳定：

| 版本 | 工厂类名 |
|------|----------|
| 3.x | `MqttFactory` |
| 4.x | `MqttFactory` |
| 5.1.0.1559 | `MqttClientFactory` |

**修复**：

```csharp
var factory = new MqttClientFactory();
var client = factory.CreateMqttClient();
```

**教训**：MQTTnet 版本间 API 变化频繁，工厂类名称这种基础类型都可能变更。安装新版本后先通过反射确认类型是否存在。

---

## 3. MQTTnet 5.x TLS 配置：`WithTlsOptions` 替代 `WithTls`

**现象**：编译报错 — `WithTls()` 方法不存在于 `MqttClientOptionsBuilder`。

**根因**：MQTTnet 5.x 将 TLS 配置从简单布尔切换改为回调式配置。旧版本：

```csharp
// 3.x/4.x
builder.WithTls(true);
```

5.x 改为：

```csharp
// 5.x
builder.WithTlsOptions(tls =>
{
    tls.UseTls();
});
```

**修复**：

```csharp
if (_config.UseTls)
{
    builder.WithTlsOptions(tls =>
    {
        tls.UseTls();
    });
}
```

**教训**：5.x 的 `WithTlsOptions` 设计更灵活（可配置证书验证、SNI 等），但破坏了旧 API 兼容性。

---

## 4. MQTTnet 5.x `WithCleanStart` 替代 `WithCleanSession`

**现象**：编译报错 — `WithCleanSession(bool)` 方法不存在。

**根因**：MQTT 5.0 协议规范中将 `CleanSession` 重命名为 `CleanStart`，MQTTnet 5.x 跟进了这一命名变更。

```csharp
// 3.x/4.x（MQTT 3.1.1 语义）
builder.WithCleanSession(true);

// 5.x（MQTT 5.0 语义）
builder.WithCleanStart(true);
```

**修复**：

```csharp
builder.WithCleanStart(true);
```

**教训**：MQTTnet 5.x 大量 API 命名跟随 MQTT 5.0 规范变更，遇到"方法不存在"时优先检查是否是协议术语更名。

---

## 5. MQTTnet 5.x QoS 枚举迁移至 `MQTTnet.Protocol` 命名空间

**现象**：编译报错 — `MqttQualityOfServiceLevel` 类型找不到。

**根因**：MQTTnet 5.x 将 QoS 相关枚举移至 `MQTTnet.Protocol` 子命名空间：

```csharp
using MQTTnet.Protocol;  // MqttQualityOfServiceLevel 在此
```

旧版本（3.x/4.x）这些枚举在 `MQTTnet` 根命名空间。

**修复**：添加 `using MQTTnet.Protocol`。

**教训**：MQTTnet 5.x 做了命名空间重组，协议相关的类型（QoS、保留消息标志等）被抽离到 `MQTTnet.Protocol`，需额外 using。

---

## 6. DI 注册顺序：`MqttOutput` 必须在 `ConsoleDataOutput` 之后注册

**现象**：运行时只有 Console 输出，MQTT 无数据输出，或两者输出顺序不确定。

**根因**：`Program.cs` 中同时注册了 `ConsoleDataOutput` 和 `MqttOutput` 为 `IDataOutput`：

```csharp
builder.Services.AddSingleton<IDataOutput, ConsoleDataOutput>();

if (mqttConfig.Enabled)
{
    builder.Services.AddSingleton<IDataOutput, MqttOutput>();
}
```

DI 容器会解析所有注册的 `IDataOutput` 实现，但如果消费方只注入单个 `IDataOutput`，则只有最后注册的那个会被使用。正确的做法是注入 `IEnumerable<IDataOutput>` 并遍历所有实现。

**当前处理**：当前 `ModbusCollectorService` 已使用 `IEnumerable<IDataOutput>` 注入，所以两个输出都会被调用。但如果后续有新的输出通道，需注意 DI 注册方式。

**教训**：多输出通道场景下，消费方必须注入 `IEnumerable<IDataOutput>`，而非单个 `IDataOutput`，否则只有最后注册的实现生效。

---

## 7. MQTT ClientId 应包含 DeviceId 避免多实例冲突

**现象**：部署多个 Modbus 采集器实例后，MQTT Broker 报错 — ClientId 重复，旧连接被踢断。

**根因**：`appsettings.json` 中 `ClientId` 配置为固定值 `AmGatewayCloud-Modbus`，当多个实例连接同一 Broker 时，MQTT 协议要求 ClientId 唯一（CleanStart=true 时后连接的会踢掉先连接的）。

**修复**：运行时拼接 DeviceId 作为 ClientId 后缀：

```csharp
.WithClientId($"{_config.ClientId}-{_deviceId}")
```

最终 ClientId 为 `AmGatewayCloud-Modbus-simulator-001`，确保不同设备实例不会冲突。

**教训**：MQTT ClientId 必须全局唯一。边缘场景下 DeviceId 作为后缀是最简单的去重方式，也可以用机器名、IP、GUID 等。

---

## 8. MQTT 断线期间数据静默丢弃策略

**现象**：MQTT Broker 宕机后，采集器主循环卡住或内存持续增长。

**根因**：如果断线时不做处理，采集线程会在 `PublishAsync` 处阻塞等待超时，或数据在内存中无限堆积。

**修复**：采用"静默丢弃"策略——断线期间 `WriteBatchAsync` 直接返回，不阻塞采集主循环：

```csharp
if (!_connected) return; // 连接失败，静默丢弃

// ... publish ...

catch (Exception ex)
{
    // 发布失败，静默丢弃，不阻塞采集
    _connected = false;
}
```

**权衡**：
- **优点**：采集器永远不会因输出通道故障而卡死，保证采集主循环稳定
- **缺点**：断线期间的历史数据丢失，不适合作业数据零丢失场景
- **改进方向**：如需零丢失，可增加内存/磁盘队列缓冲，但会增加复杂度和内存压力

**教训**：边缘采集器的核心原则是"采集优先，输出其次"，不能因为输出通道故障而影响采集主循环。

---

## 9. `Configuration.MqttConfig` using 冗余前缀

**现象**：编译正常但代码冗余 — `Program.cs` 中出现 `Configuration.MqttConfig`，而文件顶部已有 `using AmGatewayCloud.Collector.Modbus.Configuration`。

**根因**：代码编辑过程中误加了命名空间前缀。

**修复**：移除冗余前缀，直接使用 `MqttConfig`：

```csharp
// 修改前
var mqttConfig = new Configuration.MqttConfig();

// 修改后
var mqttConfig = new MqttConfig();
```

**教训**：小问题但值得记录——using 已引入的命名空间不需要再写全限定名，保持代码简洁。

---

## 10. MQTTnet 5.x 反射确认 API 是必要的手段

**现象**：多个 API（工厂类名、TLS 配置、CleanStart）与官方文档/网上的示例代码不一致，编译反复报错。

**根因**：MQTTnet 5.x 是一次较大的 API 重组，文档更新滞后于代码变更。通过 NuGet 安装的 5.1.0.1559 版本与 markdown 文档描述的 API 存在多处不一致。

**解决过程**：最终通过反射逐一确认：

```csharp
// 检查 MqttClientOptionsBuilder 的可用方法
typeof(MqttClientOptionsBuilder).GetMethods()
    .Select(m => m.Name)
    .Where(n => n.StartsWith("With"))
    .Distinct()
    .ToList()
    .ForEach(Console.WriteLine);
```

确认了 `WithTlsOptions`、`WithCleanStart`、`WithClientId` 等方法的真实签名。

**教训**：对于 MQTTnet 这种版本间 API 变化剧烈的库，遇到"找不到类型/方法"时，**反射确认**比查文档更可靠。建议在项目初期先写一个小的 API 探测程序，确认关键类型和方法是否存在。

---

## 通用经验

| 问题类型 | 发生次数 | 建议 |
|---------|---------|------|
| MQTTnet 5.x 命名空间/类型名与旧版本不兼容 | 5 | 始终通过反射或编译验证 API，不要轻信 3.x/4.x 的示例代码 |
| MQTTnet 5.x 跟进 MQTT 5.0 规范术语更名 | 1 | 遇到"方法不存在"优先检查协议术语是否更名 |
| DI 多实现注册与消费方式 | 1 | 多输出通道必须注入 `IEnumerable<IT>`，而非单个 `IT>` |
| 边缘场景 MQTT ClientId 唯一性 | 1 | ClientId 必须包含实例标识（DeviceId/机器名/GUID） |
| 断线数据丢失 vs 采集稳定性权衡 | 1 | 边缘采集器核心原则：采集优先，输出其次 |
| 代码冗余（using 重复前缀） | 1 | Code review 时注意 using 引入后的全限定名冗余 |
| 反射确认 API | 1 | MQTTnet 版本间 API 变化大，反射比文档更可靠 |

---

*最后更新：2026-05-07*
