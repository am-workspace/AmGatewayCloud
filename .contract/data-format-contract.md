# AmGatewayCloud — 边缘数据存储与传输格式契约

> 描述 EdgeGateway 写入 **InfluxDB（本地时序库）** 和 **RabbitMQ（云端转发）** 的数据格式。

---

## 版本

- **当前版本**: `v1.0`
- **生效日期**: 2026-05-07
- **变更原则**: 只增字段，不删字段；新增字段提供默认值

---

## 概述

EdgeGateway 收到采集器数据后，**同步**写入本地 InfluxDB（数据不丢的底线），**异步**转发到 RabbitMQ（云端聚合）。

两种管道的数据格式不同：

| 管道 | 格式 | 目的 |
|------|------|------|
| **InfluxDB** | Line Protocol（时序专用） | 本地持久化、断网缓存、Grafana 查询、回放源 |
| **RabbitMQ** | JSON（跨服务通用） | 云端消费、业务分析、报警触发 |

---

## 一、InfluxDB 存储格式

### 1.1 Measurement

```
device_data
```

### 1.2 Tags（索引维度）

| Tag Key | 来源 | 示例 |
|---------|------|------|
| `factory_id` | `DataBatch.FactoryId` | `factory-001` |
| `workshop_id` | `DataBatch.WorkshopId` | `workshop-001` |
| `device_id` | `DataBatch.DeviceId` | `simulator-001` |
| `protocol` | `DataBatch.Protocol` | `modbus` / `opcua` |
| `tag` | `DataPoint.Tag` | `temperature` |
| `quality` | `DataPoint.Quality` | `Good` / `Uncertain` / `Bad` |
| `group_name` | `DataPoint.GroupName` | `sensors`（可选，为空时不写） |

> **Tag 值转义规则**：空格 → `\ `，逗号 → `\,`，等号 → `\=`

### 1.3 Fields（数据值）

按 `DataPoint.ValueType` 分字段存储：

| ValueType | InfluxDB Field | 数据类型 | Line Protocol 示例 |
|-----------|---------------|---------|-------------------|
| `int` / `short` / `long` / `int32` / `int64` | `value_int` | integer (i) | `value_int=23i` |
| `float` / `double` / `single` | `value_float` | float | `value_float=23.5` |
| `bool` / `boolean` | `value_bool` | bool | `value_bool=true` |
| `string` / `datetime` / `byte` / 其他 | `value_string` | string | `value_string="running"` |
| — | `value_type` | string | `value_type="double"` |

> **保留字段**：`value_type` 记录原始类型，方便消费端识别和 Grafana 展示。

### 1.4 Timestamp

使用 `DataBatch.Timestamp` 的 Unix 毫秒时间戳：

```
device_data,factory_id=factory-001,workshop_id=workshop-001,... value_float=23.5,value_type="double" 1746612660000
```

### 1.5 完整 Line Protocol 示例

**Modbus 温度数据：**
```
device_data,factory_id=factory-001,workshop_id=workshop-001,device_id=simulator-001,protocol=modbus,tag=temperature,quality=Good value_float=23.5,value_type="double" 1746612660000
device_data,factory_id=factory-001,workshop_id=workshop-001,device_id=simulator-001,protocol=modbus,tag=voltage,quality=Good value_float=380.5,value_type="double" 1746612660000
device_data,factory_id=factory-001,workshop_id=workshop-001,device_id=simulator-001,protocol=modbus,tag=alarmMask,quality=Good value_int=0i,value_type="int" 1746612660000
```

**OPC UA 报警数据：**
```
device_data,factory_id=factory-001,workshop_id=workshop-001,device_id=simulator-002,protocol=opcua,tag=diAlarm,quality=Good value_bool=true,value_type="bool" 1746612661000
device_data,factory_id=factory-001,workshop_id=workshop-001,device_id=simulator-002,protocol=opcua,tag=alarmStatusMask,quality=Good value_string="4",value_type="string" 1746612661000
```

### 1.6 Flux 查询示例

```flux
// 查询最近 5 分钟某设备的温度
from(bucket: "edge-data")
  |> range(start: -5m)
  |> filter(fn: (r) => r._measurement == "device_data")
  |> filter(fn: (r) => r.device_id == "simulator-001")
  |> filter(fn: (r) => r.tag == "temperature")
  |> filter(fn: (r) => r._field == "value_float")

// 回放查询（用于断网恢复）
from(bucket: "edge-data")
  |> range(start: 2026-05-07T10:00:00Z, stop: 2026-05-07T11:00:00Z)
  |> filter(fn: (r) => r._measurement == "device_data")
  |> filter(fn: (r) => r.batch_id != "550e8400-e29b-41d4-a716-446655440000")
  |> group(columns: ["batch_id"])
  |> sort(columns: ["_time"])
```

---

## 二、RabbitMQ 传输格式

### 2.1 AMQP 消息属性

| 属性 | 值 | 说明 |
|------|-----|------|
| **Exchange** | `amgateway.topic` | Topic 类型 Exchange |
| **Routing Key** | `amgateway.{factoryId}.{workshopId}.{deviceId}.{protocol}` | 层级路由 |
| **ContentType** | `application/json` | JSON 序列化 |
| **DeliveryMode** | `2` | 持久化消息 |
| **Queue** | `amgateway.{factoryId}` | 消费者绑定队列 |

### 2.2 Routing Key 格式

```
amgateway.{factoryId}.{workshopId}.{deviceId}.{protocol}
```

**示例：**
- `amgateway.factory-001.workshop-001.simulator-001.modbus`
- `amgateway.factory-001.workshop-001.simulator-002.opcua`

**保留字符转义：**
- `.` → `_`
- `*` → `_`
- `#` → `_`

### 2.3 Payload 结构（JSON）

与 MQTT 契约完全一致，见 `mqtt-contract.md`。

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
      "value": 23.5,
      "valueType": "double",
      "quality": "Good",
      "timestamp": "2026-05-07T15:31:00Z",
      "groupName": "sensors"
    },
    {
      "tag": "voltage",
      "value": 380.5,
      "valueType": "double",
      "quality": "Good",
      "timestamp": "2026-05-07T15:31:00Z",
      "groupName": "sensors"
    },
    {
      "tag": "alarmMask",
      "value": 0,
      "valueType": "int",
      "quality": "Good",
      "timestamp": "2026-05-07T15:31:00Z",
      "groupName": null
    }
  ]
}
```

### 2.4 消费端反序列化（C#）

```csharp
public class DataBatch
{
    public Guid BatchId { get; set; }
    public string? TenantId { get; set; }
    public string FactoryId { get; set; } = string.Empty;
    public string WorkshopId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public List<DataPoint> Points { get; set; } = [];
}

public class DataPoint
{
    public string Tag { get; set; } = string.Empty;
    public JsonElement Value { get; set; }
    public string ValueType { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? GroupName { get; set; }
}
```

### 2.5 云端 Consumer 绑定示例

```csharp
// 消费工厂 A 的所有数据
channel.QueueBind("amgateway.factory-001", "amgateway.topic", "amgateway.factory-001.#");

// 消费工厂 A 车间 1 的所有数据
channel.QueueBind("amgateway.factory-001", "amgateway.topic", "amgateway.factory-001.workshop-001.#");

// 消费所有 Modbus 协议数据
channel.QueueBind("amgateway.modbus-consumer", "amgateway.topic", "amgateway.*.*.*.modbus");
```

---

## 三、两种格式对照

| 维度 | InfluxDB | RabbitMQ |
|------|----------|----------|
| **格式** | Line Protocol | JSON |
| **值存储** | 按类型分字段（`value_int`/`value_float`/`value_bool`/`value_string`） | 原始 JSON 值（`JsonElement`） |
| **类型标识** | `value_type` tag | `DataPoint.ValueType` 字段 |
| **时间戳** | Unix 毫秒（`_time`） | ISO 8601 字符串 |
| **分组信息** | `group_name` tag | `DataPoint.GroupName` |
| **质量标识** | `quality` tag | `DataPoint.Quality` |
| **批次关联** | `batch_id` tag（可选） | `DataBatch.BatchId` |
| **消费方式** | Flux 查询 | AMQP 消费 |
| **用途** | 本地持久化、回放、Grafana | 云端业务处理、报警、分析 |

---

## 四、向后兼容承诺

1. **v1.x 版本**：只增加可选字段，不删除或修改现有字段
2. InfluxDB 新增 tag/field 时，旧查询不受影响（忽略不识别的字段）
3. RabbitMQ JSON 新增字段时，消费端应忽略不识别的字段
4. 新增字段必须提供合理的默认值

---

## 五、相关文件

- `mqtt-contract.md` — MQTT 边缘采集器 → EdgeGateway 数据契约
- `rabbitmq-contract.md` — RabbitMQ 拓扑与路由契约
- `edge-gateway-status.md` — EdgeGateway 实现状态
