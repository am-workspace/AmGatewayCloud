using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace AmGatewayCloud.Shared.Tenant;

/// <summary>
/// 租户服务注册扩展方法
/// </summary>
public static class TenantServiceExtensions
{
    /// <summary>
    /// 注册 ITenantContext 为 Scoped 服务（从 HttpContext.Items 中获取由 TenantMiddleware 注入的实例）
    /// </summary>
    public static IServiceCollection AddTenantContext(this IServiceCollection services)
    {
        services.AddScoped<ITenantContext>(sp =>
        {
            var httpContextAccessor = sp.GetService<IHttpContextAccessor>();
            var httpContext = httpContextAccessor?.HttpContext;

            if (httpContext?.Items["TenantContext"] is ITenantContext existing)
                return existing;

            // 非 HTTP 上下文（如 BackgroundService）回退到配置默认值
            var config = sp.GetService<IConfiguration>();
            var defaultTenantId = config?["DefaultTenantId"] ?? "default";
            return new TenantContext(defaultTenantId);
        });

        services.AddHttpContextAccessor();
        return services;
    }

    /// <summary>
    /// 启用租户识别中间件
    /// </summary>
    public static IApplicationBuilder UseTenantMiddleware(this IApplicationBuilder app)
    {
        app.UseMiddleware<TenantMiddleware>();
        return app;
    }
}
