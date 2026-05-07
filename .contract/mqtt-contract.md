# MQTT 跨服务数据契约

> 适用于 AmGatewayCloud 边缘采集器 → EdgeHub 的消息格式

---

## 版本

- **当前版本**: `v1.0`
- **生效日期**: 2026-05-07
- **变更原则**: 只增字段，不删字段；新增字段给默认值

---

## Topic 格式

```
{TopicPrefix}/{protocol}/{deviceId}
```

| 占位符 | 说明 | 示例 |
|--------|------|------|
| `TopicPrefix` | 配置项，默认 `amgateway` | `amgateway` |
| `protocol` | 采集协议标识 | `modbus` / `opcua` |
| `deviceId` | 设备唯一标识 | `simulator-001` |

**示例:**
- Modbus: `amgateway/modbus/simulator-001`
- OPC UA: `amgateway/opcua/simulator-001`

---

## Payload 结构（JSON）

```json
{
  "batchId": "550e8400-e29b-41d4-a716-446655440000",
  "tenantId": "default",
  "factoryId": "factory-001",
  "workshopId": "workshop-001",
  "deviceId": "simulator-001",
  "protocol": "modbus",
  "timestamp": "2026-05-07T15:31:00.000+00:00",
  "points": [
    {
      "tag": "temperature",
      "value": 125.5,
      "valueType": "double",
      "quality": "Good",
      "timestamp": "2026-05-07T15:31:00Z",
      "groupName": "sensors"
    }
  ]
}
```

---

## 字段说明

### Batch 级字段

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `batchId` | `string` (GUID) | 是 | 批次唯一标识，用于去重 |
| `tenantId` | `string` | 否 | 多租户标识，默认 `"default"` |
| `factoryId` | `string` | 是 | 工厂标识，用于 RabbitMQ 路由键构造 |
| `workshopId` | `string` | 是 | 车间标识，用于 RabbitMQ 路由键构造 |
| `deviceId` | `string` | 是 | 设备标识 |
| `protocol` | `string` | 是 | 采集协议：`modbus` / `opcua` / 未来扩展 |
| `timestamp` | `string` (ISO 8601) | 是 | 批次发布时间，`DateTimeOffset` 格式 |

### Point 级字段

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `tag` | `string` | 是 | 测点名称，如 `temperature` |
| `value` | `number` / `boolean` / `string` | 是 | 采集值 |
| `valueType` | `string` | 是 | 值类型，见下方枚举 |
| `quality` | `string` | 是 | 数据质量，见下方枚举 |
| `timestamp` | `string` (ISO 8601) | 是 | 测点采集时间，UTC |
| `groupName` | `string` | 否 | 分组名称（如寄存器组/节点组），无分组时为 `null` |

---

## 枚举值

### valueType

| 值 | 对应 C# 类型 | 说明 |
|----|-------------|------|
| `double` | `double` | 双精度浮点 |
| `float` | `float` | 单精度浮点 |
| `int` | `int` | 32位有符号整数 |
| `long` | `long` | 64位有符号整数 |
| `short` | `short` | 16位有符号整数 |
| `ushort` | `ushort` | 16位无符号整数 |
| `uint` | `uint` | 32位无符号整数 |
| `bool` | `bool` | 布尔值 |
| `string` | `string` | 字符串 |
| `byte` | `byte` | 字节 |
| `datetime` | `DateTime` | 日期时间 |

### quality

| 值 | 说明 |
|----|------|
| `Good` | 正常采集 |
| `Uncertain` | 不确定/可疑 |
| `Bad` | 采集失败 |

> 注：Modbus 采集器的 `Unknown` 已统一映射为 `Uncertain`。

---

## 消费端反序列化示例（C#）

```csharp
public record DataBatch(
    Guid BatchId,
    string? TenantId,
    string FactoryId,
    string WorkshopId,
    string DeviceId,
    string Protocol,
    DateTimeOffset Timestamp,
    List<DataPoint> Points
);

public record DataPoint(
    string Tag,
    JsonElement Value,      // 消费端根据 valueType 转换
    string ValueType,
    string Quality,
    DateTime Timestamp,
    string? GroupName
);

// 使用
var batch = JsonSerializer.Deserialize<DataBatch>(json);
```

---

## 向后兼容承诺

1. **v1.x 版本**：只增加可选字段，不删除或修改现有字段
2. 消费端应**忽略不识别的字段**
3. 新增字段必须提供合理的默认值

---

## 相关文件

- `collector-modbus.md` — Modbus 采集器详细设计
- `collector-opcua.md` — OPC UA 采集器详细设计
- `roadmap.md` — 项目整体路线图

---

## dd

本地influx已启动，2.7.*的版本，C:\Users\amwor\Documents\influxdb2-2.7.12-windows，这是路径，然后你写的时候可以参照C:\Users\amwor\Documents\MyProject\AmGateway\Plugins\AmGateway.Publisher.InfluxDB，
这个是已经跑通的，
