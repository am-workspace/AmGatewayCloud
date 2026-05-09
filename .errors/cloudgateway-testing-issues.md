# CloudGateway 独立测试排错记录

## 1. rabbitmq:management 镜像无法拉取

**现象**：`docker compose up -d` 报错 `dialing registry-1.docker.io:443: connectex: No connection could be made`

**原因**：Docker Desktop 配置的代理 `http.docker.internal:3128` 不通，无法访问 Docker Hub。

**修复**：本地已有 `rabbitmq:latest`（391MB），将 compose 改用本地镜像，启动时通过 command 启用 management 插件：
```yaml
image: rabbitmq:latest
command: ["sh", "-c", "rabbitmq-plugins enable --offline rabbitmq_management && exec rabbitmq-server"]
```

---

## 2. 测试消息脚本在 Windows 上无法执行

**现象**：`.sh` 文件在 cmd/PowerShell 中不能运行。

**修复**：创建 PowerShell 版本 `scripts/publish-test-message.ps1`，使用 `Invoke-RestMethod` 调用 RabbitMQ Management API。

---

## 3. 队列拓扑缺失

**现象**：CloudGateway 的 `FactoryConsumer` 启动时会声明队列拓扑（DLX、DLQ、绑定），但 Edge Gateway 不在时无人建队列，直接发消息无目标队列。

**修复**：PS 脚本增加 `-SetupOnly` / 默认建拓扑逻辑，通过 Management API 声明：
- DLX 交换机 `dlx.{queueName}` (topic, durable)
- 死信队列 `dlq.{queueName}` (durable)
- 绑定 DLQ → DLX (routingKey = `dlx.{queueName}`)
- 主队列 `{queueName}` (durable, 带 x-dead-letter-exchange 参数)

---

## 4. MultiRabbitMqConsumer 创建消费者后未启动

**现象**：`MultiRabbitMqConsumer.SyncConsumers()` 中 `new FactoryConsumer(...)` 创建实例后没有调用 `StartAsync`。消费者对象存在但从未执行，队列消息堆积，`consumers=0`。

**原因**：`FactoryConsumer` 继承 `BackgroundService`，需要显式调用 `StartAsync` 才会执行 `ExecuteAsync` → `ConnectAndConsumeAsync`。

**修复**：`MultiRabbitMqConsumer.cs` 中存储 `_ct` 并在创建消费者后补上：
```csharp
_ = consumer.StartAsync(_ct);
```

---

## 5. PS 测试脚本 JSON 字段名与模型不匹配

**现象**：消息被消费但数据未写入数据库。反序列化后 DataBatch 所有字段为默认值（Guid.Empty, "", []）。

**原因**：`System.Text.Json` 默认大小写敏感。PS 脚本使用 PascalCase（`BatchId`, `DataPoints`），C# 模型用 `[JsonPropertyName]` 标注 camelCase（`batchId`, `points`）。

**修复**：PS 脚本 payload 全部改为 camelCase，对齐模型定义：
```powershell
$payload = @{
    batchId    = $batchId
    tenantId   = "default"
    factoryId  = $factoryId
    workshopId = "ws-001"
    deviceId   = "dev-001"
    protocol   = "opcua"
    timestamp  = $ts
    points     = @(
        @{ tag = "Temperature"; value = 25.5; valueType = "float"; quality = "Good"; timestamp = $ts; groupName = $null },
        @{ tag = "Pressure";    value = 101.3; valueType = "float"; quality = "Good"; timestamp = $ts; groupName = $null }
    )
}
```

---

## 6. AsyncEventingBasicConsumer 在 .NET 10 下不触发 Received 事件

**现象**：RabbitMQ 消费者已连接（`consumers=1`），消息被递送（`messages_unacknowledged > 0`），但 `HandleMessageAsync` 从未被调用。数据库无任何写入。`Console.WriteLine` 调试输出一条都没有。

**原因**：`AsyncEventingBasicConsumer` (RabbitMQ.Client 6.8.1) 在 .NET 10 运行时下事件分发机制不工作，`Received` 事件不触发。

**修复**：将 `AsyncEventingBasicConsumer` 替换为 `EventingBasicConsumer`（同步事件分发 + 内部 fire-and-forget 异步处理）：
```csharp
// Before - 不工作
var consumer = new AsyncEventingBasicConsumer(_channel);
consumer.Received += async (_, ea) => await HandleMessageAsync(ea, ct);

// After - 正常工作
var consumer = new EventingBasicConsumer(_channel);
consumer.Received += (_, ea) => _ = HandleMessageAsync(ea, ct);
```

---

## 最终可用环境

```bash
# 1. 启动基础设施
cd AmGatewayCloud
docker compose up -d

# 2. 启动 CloudGateway
cd src/AmGatewayCloud.CloudGateway
dotnet run

# 3. 发送测试消息（另一个终端）
.\scripts\publish-test-message.ps1 amgateway.factory-a 5
```
