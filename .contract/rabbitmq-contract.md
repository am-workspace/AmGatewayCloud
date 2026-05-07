# RabbitMQ 边缘-云端数据契约

> 适用于 AmGatewayCloud EdgeGateway → 云端聚合网关 的消息格式与拓扑约定

---

## 版本

- **当前版本**: `v1.0`
- **生效日期**: 2026-05-07
- **变更原则**: 只增字段，不删字段；新增字段给默认值

---

## 核心拓扑约定

```
┌─────────────────┐        ┌──────────────────────────────┐
│  EdgeGateway    │──AMQP──►│  Exchange: amgateway.topic   │
│  (每个工厂一个)  │        │  Type: topic, durable        │
└─────────────────┘        └──────────────┬───────────────┘
                                          │
                    ┌─────────────────────┼─────────────────────┐
                    │                     │                     │
                    ▼                     ▼                     ▼
            ┌──────────────┐     ┌──────────────┐     ┌──────────────┐
            │ Queue-A      │     │ Queue-B      │     │ Queue-C      │
            │ (factory-a)  │     │ (factory-b)  │     │ (factory-c)  │
            └──────────────┘     └──────────────┘     └──────────────┘
```

**原则**：一厂一个队列，Exchange 共享，队列按 `factoryId` 隔离。

---

## Exchange 定义

| 属性 | 值 | 说明 |
|------|-----|------|
| **Name** | `amgateway.topic` | 全局统一 |
| **Type** | `topic` | 支持通配路由 |
| **Durable** | `true` | 持久化，重启后保留 |
| **Auto Delete** | `false` | 不自动删除 |

---

## Queue 定义

| 属性 | 值 | 说明 |
|------|-----|------|
| **Name** | `amgateway.{factoryId}` | 按工厂隔离，如 `amgateway.factory-001` |
| **Durable** | `true` | 持久化，重启后保留 |
| **Exclusive** | `false` | 允许多消费者 |
| **Auto Delete** | `false` | 不自动删除 |
| **Queue Type** | `classic` | 标准队列 |

### 绑定规则

```
Queue: amgateway.{factoryId}
  ←─bind── Exchange: amgateway.topic
           Routing Key: #    (接收所有路由键)
```

> **说明**：EdgeGateway 侧用 `#` 全量接收当前工厂所有设备数据，云端 Consumer 如需更细粒度过滤，可自建额外绑定。

---

## Routing Key 格式

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

> 因为 RabbitMQ Topic 中 `.` `*` `#` 是保留字符，EdgeGateway 发送前会对 `factoryId`/`workshopId`/`deviceId`/`protocol` 中这些字符进行转义。

---

## Message 属性

| 属性 | 值 | 说明 |
|------|-----|------|
| **ContentType** | `application/json` | 消息体为 JSON |
| **DeliveryMode** | `2` | 持久化消息，重启不丢 |
| **Exchange** | `amgateway.topic` | 固定 |

---

## Payload 结构（JSON）

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

## 云端消费端示例（C#）

```csharp
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

var factory = new ConnectionFactory
{
    HostName = "rabbitmq.cloud.example.com",
    Port = 5672,
    UserName = "consumer",
    Password = "xxx"
};

using var connection = factory.CreateConnection();
using var channel = connection.CreateModel();

var factoryId = "factory-001";
var queueName = $"amgateway.{factoryId}";

// 声明队列（确保存在）
channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false);

// 可选：按协议/车间做额外绑定过滤
// channel.QueueBind(queueName, "amgateway.topic", "amgateway.factory-001.*.*.modbus");

var consumer = new EventingBasicConsumer(channel);
consumer.Received += (model, ea) =>
{
    var body = Encoding.UTF8.GetString(ea.Body.Span);
    var batch = JsonSerializer.Deserialize<DataBatch>(body);
    Console.WriteLine($"[{ea.RoutingKey}] device={batch.DeviceId} points={batch.Points.Count}");
    channel.BasicAck(ea.DeliveryTag, multiple: false);
};

channel.BasicConsume(queueName, autoAck: false, consumer);
```

---

## 多工厂扩展约定

| 工厂 | EdgeGateway Queue 名 | 云端 Consumer |
|------|---------------------|--------------|
| Factory A | `amgateway.factory-a` | Consumer-A |
| Factory B | `amgateway.factory-b` | Consumer-B |
| Factory C | `amgateway.factory-c` | Consumer-C |

- 新增工厂时，只需在 EdgeGateway 配置中修改 `FactoryId`，队列名自动跟随
- 云端部署对应 Consumer 消费该队列即可
- 如需跨工厂聚合，可在云端再建一个 Fanout Exchange 做二次分发

---

## 向后兼容承诺

1. **v1.x 版本**：只增加可选字段，不删除或修改现有字段
2. 消费端应**忽略不识别的字段**
3. 新增字段必须提供合理的默认值

---

## 相关文件

- `mqtt-contract.md` — MQTT 边缘采集器 → EdgeGateway 数据契约
- `roadmap.md` — 项目整体路线图与架构设计
