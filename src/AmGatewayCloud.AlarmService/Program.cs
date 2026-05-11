using AmGatewayCloud.AlarmService.Configuration;
using AmGatewayCloud.AlarmService.Infrastructure;
using AmGatewayCloud.AlarmService.Services;
using AmGatewayCloud.AlarmInfrastructure.Events;
using AmGatewayCloud.AlarmInfrastructure.Persistence;
using AmGatewayCloud.Shared.Tenant;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(GetConfiguration(args))
    .CreateLogger();

try
{
    Log.Information("AmGatewayCloud.AlarmService starting");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // 配置
    builder.Services.Configure<AlarmServiceConfig>(
        builder.Configuration.GetSection("AlarmService"));
    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<IConfiguration>().GetSection("AlarmService").Get<AlarmServiceConfig>()
        ?? new AlarmServiceConfig());

    // EF Core DbContext
    var alarmConfig = builder.Configuration.GetSection("AlarmService").Get<AlarmServiceConfig>() ?? new();
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(alarmConfig.PostgreSql.ConnectionString));
    builder.Services.AddDbContextFactory<AppDbContext>(options =>
        options.UseNpgsql(alarmConfig.PostgreSql.ConnectionString));

    // MediatR（领域事件发布）
    builder.Services.AddMediatR(cfg =>
        cfg.RegisterServicesFromAssembly(typeof(MediatRDomainEventPublisher).Assembly));

    // 核心服务
    builder.Services.AddSingleton<RuleEvaluator>();
    builder.Services.AddSingleton<CooldownManager>();
    builder.Services.AddSingleton<DelayTracker>();

    // 数据访问（EF Core 仓储，Scoped 以匹配 DbContext 生命周期）
    builder.Services.AddScoped<AmGatewayCloud.AlarmInfrastructure.Repositories.AlarmEventRepository>();
    builder.Services.AddScoped<AmGatewayCloud.AlarmInfrastructure.Repositories.AlarmRuleRepository>();
    builder.Services.AddSingleton<TimescaleDbReader>();

    // 消息基础设施
    builder.Services.AddSingleton<RabbitMqConnectionManager>();
    builder.Services.AddSingleton<AlarmEventPublisher>();

    // 数据库初始化
    builder.Services.AddSingleton<AlarmDbInitializer>();

    // 评估主循环（后台服务）
    builder.Services.AddHostedService<AlarmEvaluationHostedService>();

    // 业务 API
    builder.Services.AddScoped<AlarmRuleService>();
    builder.Services.AddScoped<AlarmQueryService>();

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
    var initializer = app.Services.GetRequiredService<AlarmDbInitializer>();
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

    Log.Information("AmGatewayCloud.AlarmService listening on {Urls}",
        string.Join(", ", app.Urls.Any() ? app.Urls : ["http://localhost:5001"]));

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
