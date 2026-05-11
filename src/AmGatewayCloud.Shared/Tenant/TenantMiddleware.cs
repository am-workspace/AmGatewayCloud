using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Context;

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

        // 注入到 HttpContext.Items，供 DI 容器使用
        var tenantContext = new TenantContext(tenantId);
        context.Items["TenantContext"] = tenantContext;

        // 注入 Serilog LogContext，所有日志自动携带 TenantId
        using (LogContext.PushProperty("TenantId", tenantId))
        {
            await _next(context);
        }
    }
}
