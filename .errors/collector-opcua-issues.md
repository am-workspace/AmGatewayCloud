# OPC UA Collector — 关键问题记录

> 记录开发过程中遇到的超出预期的 API 不兼容、SDK 文档缺失等问题，供后续参考。

---

## 1. `KeepAliveEventHandler` 委托签名：`ISession` 而非 `Session`

**现象**：编译报错 — `OnKeepAlive(Session, KeepAliveEventArgs)` 无法匹配事件签名。

**根因**：OPC UA .NET Standard SDK 1.5.x 中，`Session.KeepAlive` 事件的委托定义为：

```csharp
public delegate void KeepAliveEventHandler(ISession session, KeepAliveEventArgs e);
```

第一个参数是 **`ISession` 接口**，不是 `Session` 具体类。很多旧教程和示例代码用的是 `Session`，这在 1.5.x 中已不兼容。

**修复**：将方法签名改为 `OnKeepAlive(Opc.Ua.Client.ISession session, KeepAliveEventArgs e)`，使用完整限定名避免与 `Microsoft.AspNetCore.Http.ISession` 冲突。

**来源**：GitHub 源码 `Libraries/Opc.Ua.Client/Session/ISession.cs`

---

## 2. `ConfiguredEndpoint.UpdateFromServer()` 已过时

**现象**：编译警告 — `UpdateFromServer()` 标记为 `[Obsolete]`，提示使用带 `ITelemetryContext` 参数的版本。

**根因**：SDK 1.5.x 中 `UpdateFromServer()` 无参版本已废弃，新的 `UpdateFromServerAsync(ITelemetryContext)` 签名不确定。

**修复**：重连时不再调用 `UpdateFromServer`，改为直接用新发现的端点描述创建新的 `ConfiguredEndpoint`：

```csharp
var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(appConfig));
```

这样更干净，避免用过时 API。

---

## 3. `Subscription.DeleteAsync` 需要 `silent` 参数

**现象**：编译报错 — `DeleteAsync()` 不接受 0 个参数。

**根因**：SDK 1.5.x 的 `Subscription.DeleteAsync` 签名为：

```csharp
Task DeleteAsync(bool silent, CancellationToken ct);
```

第一个参数 `silent` 表示是否静默删除（不通知服务器）。很多示例代码只传 `CancellationToken`。

**修复**：调用时传入 `silent: true`：

```csharp
await subscription.DeleteAsync(true, CancellationToken.None);
```

---

## 4. `MonitoredItemNotificationEventArgs` — `NotificationValue` 是单数

**现象**：编译报错 — `NotificationValues` 不存在。

**根因**：SDK 1.5.x 中事件参数只有一个通知值，属性名为 `NotificationValue`（单数），类型为 `IEncodeable`：

```csharp
public IEncodeable NotificationValue { get; }
```

需要手动转型为 `MonitoredItemNotification` 再提取 `Value`。

**修复**：

```csharp
var notification = e.NotificationValue as MonitoredItemNotification;
if (notification?.Value != null)
{
    var value = notification.Value.Value;
    // ...
}
```

---

## 5. `MonitoredItem.Filter` 直接赋值 `DataChangeFilter`

**现象**：编译报错 — `MonitoringFilter` 不接受 `DataChangeFilter` 作为构造函数参数。

**根因**：`MonitoringFilter` 是抽象编码基类，`DataChangeFilter` 继承自它。SDK 1.5.x 中 `MonitoredItem.Filter` 属性类型为 `MonitoringFilter`，可以直接赋值子类实例：

```csharp
item.Filter = new DataChangeFilter
{
    Trigger = DataChangeTrigger.StatusValue,
    DeadbandType = (uint)DeadbandType.Percent,
    DeadbandValue = node.DeadbandPercent
};
```

旧版 API 中曾有 `DeadbandType` / `DeadbandValue` 作为 `MonitoredItem` 的直接属性，1.5.x 中已移除。

---

## 6. `StatusCodes` 命名空间冲突

**现象**：编译报错 — `StatusCodes` 在 `Opc.Ua` 和 `Microsoft.AspNetCore.Http` 之间存在歧义。

**根因**：项目引用了 `Microsoft.AspNetCore` 相关包（用于 DI 等），其中 `Microsoft.AspNetCore.Http.StatusCodes` 与 `Opc.Ua.StatusCodes` 冲突。

**修复**：使用完整限定名 `Opc.Ua.StatusCodes.Good`。

---

## 7. `MonitoredItem.SamplingInterval` 是 `int` 而非 `double`

**现象**：编译警告或隐式转换。

**根因**：配置模型中 `SamplingIntervalMs` 定义为 `double`，但 `MonitoredItem.SamplingInterval` 属性类型为 `int`（毫秒）。

**修复**：赋值时显式转换：

```csharp
item.SamplingInterval = (int)node.SamplingIntervalMs;
```

---

## 8. `Session.FetchNamespaceTablesAsync()` 无参数版本已过时

**现象**：编译警告 — 建议使用带 `ITelemetryContext` 参数的版本。

**当前处理**：暂保留无参调用，因为功能正常。如果后续升级 SDK 版本需注意此 API 可能被移除。

---

## 9. `SecurityConfiguration` 必须显式指定 `TrustedIssuerCertificates` 和 `RejectedCertificateStore`

**现象**：运行时崩溃 — `TrustedIssuerCertificates StorePath must be specified. (BadConfigurationError)`

**根因**：OPC UA .NET Standard SDK 1.5.x 的 `ApplicationConfiguration.ValidateAsync()` 强制校验 `SecurityConfiguration` 的所有证书存储路径。旧版本只需配 `ApplicationCertificate` 和 `TrustedPeerCertificates`，1.5.x 要求完整配置：

```csharp
SecurityConfiguration = new SecurityConfiguration
{
    ApplicationCertificate = ...,
    TrustedPeerCertificates = ...,
    TrustedIssuerCertificates = new CertificateTrustList  // ← 1.5.x 必须
    {
        StoreType = CertificateStoreType.Directory,
        StorePath = Path.Combine(localAppData, "AmGatewayCloud", "OPC UA", "Issuers")
    },
    RejectedCertificateStore = new CertificateTrustList     // ← 1.5.x 必须
    {
        StoreType = CertificateStoreType.Directory,
        StorePath = Path.Combine(localAppData, "AmGatewayCloud", "OPC UA", "Rejected")
    }
};
```

**修复**：补全 `TrustedIssuerCertificates` 和 `RejectedCertificateStore` 配置项，即使 `SecurityPolicy=None` 也必须指定。

**教训**：SDK 1.5.x 的配置校验比旧版严格很多，不要假设可选字段真的可选。

---

## 10. `ApplicationConfiguration` 必须指定 `ClientConfiguration`

**现象**：运行时崩溃 — `ClientConfiguration must be specified. (BadConfigurationError)`

**根因**：在修复问题 #9 后再次触发。SDK 1.5.x 要求 `ApplicationConfiguration.ClientConfiguration` 不能为 null。旧版本此字段有默认值，1.5.x 取消了默认值。

```csharp
_appConfig = new ApplicationConfiguration
{
    // ...
    ClientConfiguration = new ClientConfiguration   // ← 1.5.x 必须
    {
        DefaultSessionTimeout = _config.SessionTimeoutMs  // 注意：int 类型，不是 uint
    }
};
```

**修复**：显式添加 `ClientConfiguration` 并设置 `DefaultSessionTimeout`。注意该属性类型为 `int`，不能直接传 `(uint)` 强转。

**教训**：SDK 1.5.x 的 `ApplicationConfiguration` 有多个必须字段，建议一次性全部配齐，而非逐个碰壁补全。

---

## 通用经验

| 问题类型 | 发生次数 | 建议 |
|---------|---------|------|
| SDK 1.5.x API 签名与旧教程不匹配 | 6 | 始终通过反射或编译验证 API，不要轻信网络示例 |
| SDK 1.5.x 配置校验变严格（必须字段无默认值） | 2 | `ApplicationConfiguration` 所有必要字段一次性配齐，不要逐个碰壁补全 |
| 命名空间冲突 | 2 | 优先使用完整限定名，避免 `using` 歧义 |
| 过时 API 废弃 | 2 | 关注编译警告，优先使用 Async 版本 |
| GitHub 源码路径变更 | 1 | 仓库结构可能重组，`raw` URL 路径需确认 |

---

*最后更新：2026-05-07*
