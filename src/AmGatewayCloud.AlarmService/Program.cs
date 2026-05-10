using AmGatewayCloud.AlarmService.Configuration;
using AmGatewayCloud.AlarmService.Infrastructure;
using AmGatewayCloud.AlarmService.Services;
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

    // 核心服务
    builder.Services.AddSingleton<RuleEvaluator>();
    builder.Services.AddSingleton<CooldownManager>();

    // 数据访问
    builder.Services.AddSingleton<TimescaleDbReader>();
    builder.Services.AddSingleton<AlarmEventRepository>();
    builder.Services.AddSingleton<AlarmRuleRepository>();

    // 消息基础设施
    builder.Services.AddSingleton<RabbitMqConnectionManager>();
    builder.Services.AddSingleton<AlarmEventPublisher>();

    // 数据库初始化
    builder.Services.AddSingleton<AlarmDbInitializer>();

    // 评估主循环（后台服务）
    builder.Services.AddHostedService<AlarmEvaluationHostedService>();

    // 业务 API
    builder.Services.AddSingleton<AlarmRuleService>();
    builder.Services.AddSingleton<AlarmQueryService>();

    // Controllers
    builder.Services.AddControllers();

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
