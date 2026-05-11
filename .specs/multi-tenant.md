# 阶段 7：多租户完善

## 1. 总体目标

平台卖给多个公司（租户），不同公司登录后只看到自己的工厂、设备、报警和工单，数据完全隔离。

**核心问题**：当前代码只在**写入时携带** `TenantId`，**查询时全面缺失**过滤，等于没有多租户隔离。

---

## 2. 架构

```
前端 (Vue 3)
  │
  │ HTTP Header: Authorization: Bearer <JWT>
  │              X-Tenant-Id: tenant-a  (开发/调试用备用)
  │
  ▼
WebApi (BFF)
  │
  │ 1. JWT 验证中间件
  │ 2. TenantResolution 中间件 → 从 JWT 提取 TenantId → 注入 ITenantContext
  │ 3. YARP 透传 X-Tenant-Id Header 到下游微服务
  │ 4. SignalR Hub → 按 TenantId + FactoryId 分组推送
  │
  ├──► AlarmService
  │     │ X-Tenant-Id Header
  │     │
  │     ├── Controller → 从 ITenantContext 获取 TenantId
  │     ├── Service → 查询时附加 WHERE tenant_id = @currentTenant
  │     └── EF Core Global Query Filter → 自动过滤（兜底）
  │
  ├──► WorkOrderService
  │     │ X-Tenant-Id Header
  │     │
  │     ├── Controller → 从 ITenantContext 获取 TenantId
  │     ├── Service → 查询时附加 WHERE tenant_id = @currentTenant
  │     └── 操作时校验：工单属于当前租户？
  │
  └──► CloudGateway (无 HTTP API，不涉及)
        └── 已在写入时携带 TenantId ✅
```

---

## 3. 设计决策

### 3.1 为什么用 JWT 而不是 API Key？

| 维度 | API Key | JWT |
|------|---------|-----|
| 身份信息 | 需额外查询才能获取 TenantId | Token 自带 claims（sub, tenant_id, role） |
| 无状态 | 每次查库验证 | 签名验证，无需查库 |
| 扩展性 | 单一维度 | 多 claims（角色、权限、租户） |
| 适合场景 | 服务间调用 | **用户请求** ✅ |

**结论**：用户请求用 JWT，服务间调用（RabbitMQ）已有 TenantId 在消息体中，不需要 JWT。

### 3.2 为什么用 Global Query Filter 而不是手动 WHERE？

| 维度 | 手动 WHERE | EF Core Global Query Filter |
|------|-----------|---------------------------|
| 遗漏风险 | 高（每个查询都要记得加） | **低（EF Core 自动附加）** |
| 代码量 | 每个查询方法都要写 | 配置一次，全局生效 |
| 可测试性 | 正常 | 需注意禁用 filter 的场景 |
| 适用范围 | Dapper + EF Core 都能 | **仅 EF Core** |

**结论**：
- **EF Core 查询**（AlarmDomain/Infrastructure）→ Global Query Filter
- **Dapper 查询**（WorkOrderService/CloudGateway）→ 手动 WHERE（但封装到基础方法中）
- 两者结合，双重保险

### 3.3 为什么用中间件而不是在每个 Controller 里取？

```
❌ 每个 Controller：
var tenantId = User.FindFirst("tenant_id")?.Value;
if (string.IsNullOrEmpty(tenantId)) return Forbid();

✅ 中间件统一处理：
1. 从 JWT 提取 TenantId
2. 校验非空
3. 注入 ITenantContext（Scoped）
4. 下游代码只管 _tenantContext.TenantId
```

**结论**：租户识别是横切关注点，中间件统一处理，Controller/Service 零耦合。

### 3.4 租户信息传递方式

```
链路                          传递方式
───────────────────────────────────────────
前端 → WebApi                 JWT (Authorization Header)
WebApi → AlarmService         X-Tenant-Id Header (YARP 透传)
WebApi → WorkOrderService     X-Tenant-Id Header (YARP 透传)
AlarmService → RabbitMQ       消息体 TenantId 字段（已有 ✅）
RabbitMQ → WorkOrderService   消息体 TenantId 字段（已有 ✅）
RabbitMQ → WebApi SignalR     消息体 TenantId 字段（已有 ✅）
```

---

## 4. 组件设计

### 4.1 Shared — ITenantContext + TenantContext

```csharp
// AmGatewayCloud.Shared/Tenant/ITenantContext.cs
namespace AmGatewayCloud.Shared.Tenant;

/// <summary>
/// 当前请求的租户上下文（Scoped 生命周期）
/// </summary>
public interface ITenantContext
{
    string TenantId { get; }
    bool IsAvailable { get; }
}

// AmGatewayCloud.Shared/Tenant/TenantContext.cs
namespace AmGatewayCloud.Shared.Tenant;

public class TenantContext : ITenantContext
{
    public string TenantId { get; }
    public bool IsAvailable => !string.IsNullOrEmpty(TenantId);

    public TenantContext(string tenantId)
    {
        TenantId = tenantId ?? string.Empty;
    }
}
```

### 4.2 Shared — TenantMiddleware 扩展方法

```csharp
// AmGatewayCloud.Shared/Tenant/TenantMiddleware.cs
namespace AmGatewayCloud.Shared.Tenant;

/// <summary>
/// 从 JWT 或 X-Tenant-Id Header 提取租户标识，注入 ITenantContext
/// 优先级：JWT claim "tenant_id" > X-Tenant-Id Header > 配置默认值
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? tenantId = null;

        // 1. 从 JWT claims 提取
        var claim = context.User.FindFirst("tenant_id");
        if (claim is not null)
            tenantId = claim.Value;

        // 2. 从 X-Tenant-Id Header 提取（备用/调试）
        if (string.IsNullOrEmpty(tenantId))
            tenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();

        // 3. 兜底：配置默认值（开发环境）
        if (string.IsNullOrEmpty(tenantId))
        {
            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            tenantId = config["DefaultTenantId"] ?? "default";
        }

        // 注入 Scoped 服务
        var tenantContext = new TenantContext(tenantId);
        context.Items["TenantContext"] = tenantContext;
        context.RequestServices.GetRequiredService<ITenantContext>(); // 触发 Scoped 创建

        await _next(context);
    }
}
```

> **注意**：TenantMiddleware 实际通过 DI 注入 TenantContext，需在 Program.cs 中注册为 Scoped。

### 4.3 AlarmInfrastructure — EF Core Global Query Filter

```csharp
// 在 AppDbContext.OnModelCreating 中添加：
modelBuilder.Entity<AlarmEventEntity>().HasQueryFilter(e => e.TenantId == _tenantId);
modelBuilder.Entity<AlarmRuleEntity>().HasQueryFilter(e => e.TenantId == _tenantId);

// AppDbContext 构造函数注入 ITenantContext：
public class AppDbContext : DbContext
{
    private readonly string _tenantId;

    public AppDbContext(DbContextOptions options, ITenantContext tenantContext) : base(options)
    {
        _tenantId = tenantContext.TenantId;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Global Query Filter — 自动按租户过滤
        modelBuilder.Entity<AlarmEventEntity>()
            .HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<AlarmRuleEntity>()
            .HasQueryFilter(e => e.TenantId == _tenantId);

        // ... 现有配置
    }
}
```

### 4.4 AlarmService — Controller/Service 改造

```csharp
// Controller 改造示例
[ApiController]
[Route("api/[controller]")]
public class AlarmsController : ControllerBase
{
    private readonly ITenantContext _tenantContext;
    private readonly AlarmQueryService _queryService;

    public AlarmsController(ITenantContext tenantContext, AlarmQueryService queryService)
    {
        _tenantContext = tenantContext;
        _queryService = queryService;
    }

    // 不再需要手动传 tenantId，EF Core Global Filter 自动处理
    // 但 Service 层仍可访问 _tenantContext.TenantId 用于日志/校验
}
```

### 4.5 WorkOrderService — Dapper 查询改造

```csharp
// WorkOrderQueryService 改造
public class WorkOrderQueryService
{
    private readonly ITenantContext _tenantContext;

    // 所有查询方法附加 WHERE tenant_id = @TenantId
    public async Task<PagedResult<WorkOrderDto>> QueryAsync(WorkOrderFilter filter)
    {
        var tenantId = _tenantContext.TenantId;
        // SELECT ... WHERE tenant_id = @TenantId AND ...
    }

    // 操作方法校验租户归属
    public async Task AssignAsync(Guid id, string assignee)
    {
        var tenantId = _tenantContext.TenantId;
        // UPDATE ... WHERE id = @Id AND tenant_id = @TenantId
        // 影响行数=0 → 404 或 403
    }
}
```

### 4.6 WebApi — JWT 验证 + 租户中间件 + YARP 透传

```csharp
// Program.cs
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "amgateway",
            ValidateAudience = true,
            ValidAudience = "amgateway-api",
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]!))
        };
    });

// 租户中间件
builder.Services.AddScoped<ITenantContext>(sp =>
{
    var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!;
    return httpContext.Items["TenantContext"] as ITenantContext
           ?? new TenantContext("default");
});

app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();

// YARP 透传 X-Tenant-Id
// 在 YARP 配置中添加 Transform：
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(transforms =>
    {
        transforms.AddRequestTransform(async context =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            context.ProxyRequest.Headers.Add("X-Tenant-Id", tenantContext.TenantId);
        });
    });
```

### 4.7 前端改造

```
改动点：
1. 登录页 → 获取 JWT Token
2. axios 拦截器 → 自动附加 Authorization Header
3. stores/auth.ts → 管理登录状态 + Token
4. 现有页面无需改动（后端自动过滤）
5. 侧边栏/路由守卫 → 未登录跳转登录页
```

---

## 5. 实施顺序

| 步骤 | 内容 | 涉及项目 | 依赖 |
|------|------|---------|------|
| **1** | Shared 新增 `Tenant/ITenantContext` + `TenantContext` + `TenantMiddleware` | Shared | 无 |
| **2** | Shared 新增 `Auth/JwtHelper`（Token 生成/验证，开发用） | Shared | 无 |
| **3** | AlarmInfrastructure: AppDbContext 注入 ITenantContext + Global Query Filter | AlarmInfrastructure | 步骤 1 |
| **4** | AlarmService: Program.cs 注册 TenantMiddleware + ITenantContext | AlarmService | 步骤 1 |
| **5** | AlarmService: Controller/Service 移除手动 tenantId 传参（依赖 EF Filter） | AlarmService | 步骤 3 |
| **6** | AlarmService: TimescaleDbReader 查询附加 WHERE tenant_id | AlarmService | 步骤 4 |
| **7** | WorkOrderService: 注册 TenantMiddleware + ITenantContext | WorkOrderService | 步骤 1 |
| **8** | WorkOrderService: WorkOrderQueryService 所有查询/操作附加 tenant_id 过滤 | WorkOrderService | 步骤 7 |
| **9** | CloudGateway: PostgreSqlDeviceStore 查询附加 WHERE tenant_id | CloudGateway | 步骤 1 |
| **10** | WebApi: JWT 验证 + TenantMiddleware + YARP 透传 X-Tenant-Id | WebApi | 步骤 1 |
| **11** | WebApi: SignalR Hub 按租户分组推送 | WebApi | 步骤 10 |
| **12** | 前端: 登录页 + auth store + axios 拦截器 + 路由守卫 | Web | 步骤 10 |
| **13** | Shared: UpdateAlarmRuleRequest 补充 TenantId | Shared | 无 |
| **14** | 端到端验证 | 全部 | 步骤 12 |

---

## 6. 逐步骤详细改动

### 步骤 1：Shared — ITenantContext + TenantContext + TenantMiddleware

**新增文件**：
- `src/AmGatewayCloud.Shared/Tenant/ITenantContext.cs`
- `src/AmGatewayCloud.Shared/Tenant/TenantContext.cs`
- `src/AmGatewayCloud.Shared/Tenant/TenantMiddleware.cs`
- `src/AmGatewayCloud.Shared/Tenant/TenantServiceExtensions.cs`（注册扩展方法）

**改动文件**：
- `AmGatewayCloud.Shared.csproj` — 新增 `Microsoft.AspNetCore.Http.Abstractions` 依赖

### 步骤 2：Shared — JwtHelper

**新增文件**：
- `src/AmGatewayCloud.Shared/Auth/JwtTokenHelper.cs`

**功能**：
- `GenerateToken(tenantId, userId, role, ...)` — 开发/测试用 Token 生成
- Token 包含 claims: `sub`, `tenant_id`, `role`, `name`

### 步骤 3：AlarmInfrastructure — Global Query Filter

**改动文件**：
- `AppDbContext.cs` — 构造函数注入 `ITenantContext`，`OnModelCreating` 添加 HasQueryFilter
- `AlarmEventRepository.cs` — 移除手动 `.Where(tenantId)` 过滤（Filter 自动处理）
- `AlarmRuleRepository.cs` — 同上

### 步骤 4：AlarmService — 注册中间件

**改动文件**：
- `Program.cs` — 添加 `services.AddScoped<ITenantContext>(...)`, `app.UseMiddleware<TenantMiddleware>()`
- `AmGatewayCloud.AlarmService.csproj` — 可能需要新增 `Microsoft.AspNetCore.Authentication.JwtBearer`

### 步骤 5：AlarmService — Controller/Service 清理

**改动文件**：
- `AlarmsController.cs` — 注入 `ITenantContext`，移除从 config 读取 TenantId
- `AlarmRulesController.cs` — 同上
- `AlarmQueryService.cs` — 依赖 EF Global Filter，移除手动过滤；TimescaleDB 查询保持手动
- `AlarmRuleService.cs` — 创建规则时从 `ITenantContext` 取 TenantId

### 步骤 6：AlarmService — TimescaleDbReader

**改动文件**：
- `TimescaleDbReader.cs` — 查询附加 `WHERE tenant_id = @TenantId`

### 步骤 7：WorkOrderService — 注册中间件

**改动文件**：
- `Program.cs` — 添加 TenantMiddleware + ITenantContext 注册
- `AmGatewayCloud.WorkOrderService.csproj` — 可能需要新增 JWT 依赖

### 步骤 8：WorkOrderService — 查询/操作改造

**改动文件**：
- `WorkOrderQueryService.cs` — 注入 `ITenantContext`，所有查询加 `WHERE tenant_id`，操作加租户校验
- `WorkOrdersController.cs` — 注入 `ITenantContext`
- `AlarmEventConsumer.cs` — 不改动（RabbitMQ 消息体自带 TenantId）

### 步骤 9：CloudGateway — 查询改造

**改动文件**：
- `PostgreSqlDeviceStore.cs` — `GetDevicesByFactoryAsync` 附加 `WHERE tenant_id = @TenantId`

> 注意：CloudGateway 是 BackgroundService，无 HTTP 请求上下文，TenantId 来自消息体或配置。

### 步骤 10：WebApi — JWT + 中间件 + YARP 透传

**改动文件**：
- `Program.cs` — 添加 JWT 验证、TenantMiddleware、YARP Transform 透传 X-Tenant-Id
- `appsettings.json` — 添加 `Jwt:Secret` 配置
- `AmGatewayCloud.WebApi.csproj` — 新增 `Microsoft.AspNetCore.Authentication.JwtBearer`

### 步骤 11：WebApi — SignalR 租户分组

**改动文件**：
- `AlarmHub.cs`（或对应 Hub 文件）— JoinGroup 时附加 TenantId，推送时按 `tenantId_factoryId` 分组

### 步骤 12：前端改造

**新增文件**：
- `src/views/LoginView.vue` — 登录页
- `src/stores/auth.ts` — 登录状态管理
- `src/api/auth.ts` — 登录 API

**改动文件**：
- `src/api/client.ts` — axios 拦截器附加 Authorization Header
- `src/router/index.ts` — 路由守卫，未登录跳转
- `src/layouts/AppLayout.vue` — 侧边栏显示当前租户/登出

### 步骤 13：Shared — DTO 补全

**改动文件**：
- `AlarmRuleRequests.cs` — `UpdateAlarmRuleRequest` 添加 TenantId 字段

### 步骤 14：端到端验证

---

## 7. 验证标准

### 7.1 基础验证

- [ ] JWT Token 包含 `tenant_id` claim
- [ ] 请求无 JWT → WebApi 返回 401
- [ ] JWT 中 `tenant_id` 不匹配 → 查询结果为空（非报错）
- [ ] X-Tenant-Id Header 可作为开发备用方式

### 7.2 数据隔离验证

- [ ] 租户 A 的 JWT → 只查到租户 A 的报警、规则、工单
- [ ] 租户 B 的 JWT → 只查到租户 B 的报警、规则、工单
- [ ] 租户 A 无法操作（分配/完成/确认）租户 B 的工单/报警
- [ ] EF Core Global Query Filter 生效：直接查 DbSet 自动过滤
- [ ] Dapper 查询生效：WorkOrderService 查询附加 WHERE tenant_id

### 7.3 跨服务验证

- [ ] WebApi YARP 透传 X-Tenant-Id → AlarmService 正确接收
- [ ] WebApi YARP 透传 X-Tenant-Id → WorkOrderService 正确接收
- [ ] RabbitMQ 消息体 TenantId 不受影响（写入侧已正确 ✅）
- [ ] SignalR 推送按租户分组，租户 A 收不到租户 B 的实时报警

### 7.4 前端验证

- [ ] 未登录访问 → 跳转登录页
- [ ] 登录成功 → JWT 存储，后续请求自动附带
- [ ] 侧边栏显示当前租户名称
- [ ] 登出 → 清除 Token，跳转登录页

### 7.5 回归验证

- [ ] 使用默认租户（"default"）的现有行为不变
- [ ] Docker 部署后单租户模式正常
- [ ] 报警触发 → 工单自动创建链路不受影响

---

## 8. 风险与缓解

| 风险 | 缓解 |
|------|------|
| Global Query Filter 导致开发时忘记禁用 | 使用 `IgnoreQueryFilters()` 时必须注释原因 |
| CloudGateway 无 HTTP 上下文，ITenantContext 不可用 | CloudGateway 从消息体取 TenantId，不依赖 ITenantContext |
| JWT Secret 管理不当 | 开发环境用 appsettings，生产环境用环境变量/Secret Manager |
| 现有单租户数据（tenant_id="default"）兼容 | 默认租户逻辑保持，无 JWT 时 fallback 到 "default" |
| 前端改造影响现有功能 | 登录页独立路由，现有页面逻辑不变 |

---

## 9. 后续演进（阶段 8+）

| 阶段 | 变化 |
|------|------|
| 阶段 8 | 可观测性：日志/追踪中携带 TenantId |
| 未来 | Database-per-tenant：按 TenantId 切换连接字符串 |
| 未来 | 租户管理后台：CRUD 租户 + 分配工厂/设备 |
| 未来 | RBAC：角色权限（Admin/Maintainer/Viewer） |
| 未来 | OIDC 集成：接入企业 IdP（Keycloak/Auth0） |
