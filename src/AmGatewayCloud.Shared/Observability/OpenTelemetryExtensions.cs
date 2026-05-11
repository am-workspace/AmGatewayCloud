using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AmGatewayCloud.Shared.Observability;

public static class OpenTelemetryExtensions
{
    /// <summary>
    /// 注册 OpenTelemetry Tracing + Metrics，输出到 OTLP（Jaeger）。
    /// 配置节：OTEL:ServiceName, OTEL:OtlpEndpoint
    /// </summary>
    public static IServiceCollection AddAmGatewayOpenTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var serviceName = configuration["OTEL:ServiceName"] ?? "unknown";
        var otlpEndpoint = configuration["OTEL:OtlpEndpoint"] ?? "http://localhost:4317";

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = ctx =>
                    {
                        // 不追踪健康检查和 Swagger 请求
                        var path = ctx.Request.Path.Value;
                        return path != null
                               && !path.StartsWith("/health")
                               && !path.StartsWith("/swagger");
                    };
                    options.EnrichWithHttpRequest = (activity, request) =>
                    {
                        // 从请求头提取 TenantId 注入 Span
                        var tenantId = request.Headers["X-Tenant-Id"].FirstOrDefault();
                        if (!string.IsNullOrEmpty(tenantId))
                            activity.SetTag("tenant.id", tenantId);
                    };
                })
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)));

        return services;
    }
}
