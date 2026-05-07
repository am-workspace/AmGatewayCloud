using AmGatewayCloud.EdgeGateway.Configuration;
using AmGatewayCloud.EdgeGateway.Services;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting AmGatewayCloud.EdgeGateway");

    var builder = Host.CreateApplicationBuilder(args);

    // Configuration
    builder.Services.Configure<EdgeGatewayConfig>(
        builder.Configuration.GetSection("EdgeGateway"));
    builder.Services.AddSingleton<IValidateOptions<EdgeGatewayConfig>, EdgeGatewayConfigValidator>();

    // Serilog
    builder.Services.AddSerilog(static configure =>
    {
        configure.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
    });

    // Step 6: Replay + Watermark + RabbitMQ + InfluxDB + MQTT
    builder.Services.AddSingleton<ReplayService>();
    builder.Services.AddSingleton<WatermarkTracker>();
    builder.Services.AddSingleton<RabbitMqForwarder>();
    builder.Services.AddSingleton<InfluxDbWriter>();
    builder.Services.AddHostedService<MqttConsumerService>();

    var host = builder.Build();

    // 启动所有服务
    var influxWriter = host.Services.GetRequiredService<InfluxDbWriter>();
    var rabbitForwarder = host.Services.GetRequiredService<RabbitMqForwarder>();
    var watermarkTracker = host.Services.GetRequiredService<WatermarkTracker>();
    var replayService = host.Services.GetRequiredService<ReplayService>();

    await watermarkTracker.LoadAsync();
    await influxWriter.StartAsync();
    await rabbitForwarder.StartAsync();

    // 注册 RabbitMQ 恢复回调：自动触发回放
    rabbitForwarder.OnOnline(async () =>
    {
        var from = watermarkTracker.LastForwardedAt;
        var to = DateTimeOffset.UtcNow;
        if (from < to)
        {
            await replayService.ReplayAsync(from, to, watermarkTracker, rabbitForwarder);
        }
    });

    try
    {
        await host.RunAsync();
    }
    finally
    {
        await rabbitForwarder.StopAsync();
        await influxWriter.StopAsync();
        await watermarkTracker.SaveAsync();
    }
}
catch (OptionsValidationException ex)
{
    Log.Fatal("Configuration validation failed:\n{Errors}", string.Join("\n", ex.Failures));
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
