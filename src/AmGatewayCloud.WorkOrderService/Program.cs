using AmGatewayCloud.WorkOrderService.Configuration;
using AmGatewayCloud.WorkOrderService.Infrastructure;
using AmGatewayCloud.WorkOrderService.Services;
using AmGatewayCloud.Shared.Tenant;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(GetConfiguration(args))
    .CreateLogger();

try
{
    Log.Information("AmGatewayCloud.WorkOrderService starting");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // 配置
    builder.Services.Configure<WorkOrderServiceConfig>(
        builder.Configuration.GetSection("WorkOrderService"));
    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<IConfiguration>().GetSection("WorkOrderService").Get<WorkOrderServiceConfig>()
        ?? new WorkOrderServiceConfig());

    // 消息基础设施
    var woConfig = builder.Configuration.GetSection("WorkOrderService").Get<WorkOrderServiceConfig>() ?? new();
    builder.Services.AddSingleton(woConfig);
    builder.Services.AddSingleton<RabbitMqConnectionManager>();

    // 数据库初始化
    builder.Services.AddSingleton<WorkOrderDbInitializer>();

    // 后台服务：消费报警事件自动创建工单
    builder.Services.AddHostedService<AlarmEventConsumer>();

    // 业务服务
    builder.Services.AddScoped<WorkOrderQueryService>();

    // Controllers
    builder.Services.AddControllers();

    // 租户上下文
    builder.Services.AddTenantContext();

    // Swagger
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // Health Checks
    builder.Services.AddHealthChecks()
        .AddCheck<PostgreSqlHealthCheck>("postgresql", tags: new[] { "db" })
        .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: new[] { "mq" });

    var app = builder.Build();

    // 启动前初始化数据库
    var initializer = app.Services.GetRequiredService<WorkOrderDbInitializer>();
    var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        await initializer.InitializeAsync(ctSource.Token);
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Database initialization failed");
        throw;
    }

    // Middleware
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseTenantMiddleware();

    app.MapControllers();
    app.MapHealthChecks("/health");

    Log.Information("AmGatewayCloud.WorkOrderService listening on {Urls}",
        string.Join(", ", app.Urls.Any() ? app.Urls : ["http://localhost:5002"]));

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
    var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
             ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
             ?? "Production";
    return new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();
}
