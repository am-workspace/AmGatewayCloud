using System.Text;
using AmGatewayCloud.Shared.Configuration;
using AmGatewayCloud.Shared.Observability;
using AmGatewayCloud.Shared.Tenant;
using AmGatewayCloud.WebApi.Configuration;
using AmGatewayCloud.WebApi.Hubs;
using AmGatewayCloud.WebApi.Infrastructure;
using AmGatewayCloud.WebApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Yarp.ReverseProxy.Transforms;

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(GetConfiguration(args))
    .CreateLogger();

try
{
    Log.Information("AmGatewayCloud.WebApi starting");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // 配置
    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<IConfiguration>().GetSection("WebApi").Get<WebApiConfig>()
        ?? new WebApiConfig());
    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<WebApiConfig>().RabbitMq);

    // RabbitMQ + SignalR 实时推送
    builder.Services.AddSingleton<RabbitMqConnectionManager>();
    builder.Services.AddHostedService<AlarmEventSubscriber>();

    // SignalR
    builder.Services.AddSignalR();

    // JWT 验证
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
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]!))
            };

            // SignalR JWT: 从 access_token 查询参数读取
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    {
                        context.Token = accessToken;
                    }
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization();

    // 租户上下文
    builder.Services.AddTenantContext();

    // OpenTelemetry
    builder.Services.AddAmGatewayOpenTelemetry(builder.Configuration);

    // YARP 反向代理 — 转发到 AlarmService / WorkOrderService + 透传 X-Tenant-Id
    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
        .AddTransforms(transforms =>
        {
            transforms.AddRequestTransform(context =>
            {
                var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
                context.ProxyRequest.Headers.Add("X-Tenant-Id", tenantContext.TenantId);
                return ValueTask.CompletedTask;
            });
        });

    // Controllers（AuthController 等本地 API）
    builder.Services.AddControllers();

    // Health Checks
    builder.Services.AddHealthChecks()
        .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: new[] { "mq" });

    // CORS
    var webApiConfig = builder.Configuration.GetSection("WebApi").Get<WebApiConfig>() ?? new WebApiConfig();
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AlarmApiPolicy", policy =>
        {
            policy.WithOrigins(webApiConfig.CorsOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials(); // SignalR 需要
        });
    });

    var app = builder.Build();

    app.UseCors("AlarmApiPolicy");
    app.UseRouting();

    app.UseAuthentication();
    app.UseTenantMiddleware();
    app.UseAuthorization();

    // YARP 转发 — 所有 /api/* 请求转发到 AlarmService
    app.MapReverseProxy();

    // 本地 Controllers（Auth 等）
    app.MapControllers();

    // SignalR Hub
    app.MapHub<AlarmHub>("/hubs/alarm");

    // Health Check
    app.MapHealthChecks("/health");

    Log.Information("AmGatewayCloud.WebApi listening on {Urls}",
        string.Join(", ", app.Urls.Any() ? app.Urls : ["http://localhost:8080"]));

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

static IConfiguration GetConfiguration(string[] args)
{
    var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
    return new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();
}
