using AmGatewayCloud.Shared.Configuration;
using AmGatewayCloud.WebApi.Configuration;
using AmGatewayCloud.WebApi.Hubs;
using AmGatewayCloud.WebApi.Infrastructure;
using AmGatewayCloud.WebApi.Services;
using Serilog;

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

    // YARP 反向代理 — 转发到 AlarmService
    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

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

    // YARP 转发 — 所有 /api/* 请求转发到 AlarmService
    app.MapReverseProxy();

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
