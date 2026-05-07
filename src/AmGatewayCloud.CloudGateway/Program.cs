using AmGatewayCloud.CloudGateway.Configuration;
using AmGatewayCloud.CloudGateway.Infrastructure;
using AmGatewayCloud.CloudGateway.Infrastructure.HealthChecks;
using AmGatewayCloud.CloudGateway.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using System.Text.Json;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting AmGatewayCloud.CloudGateway");

    var builder = WebApplication.CreateBuilder(args);

    // Configuration
    builder.Services.Configure<CloudGatewayConfig>(
        builder.Configuration.GetSection("CloudGateway"));
    builder.Services.AddSingleton<IValidateOptions<CloudGatewayConfig>, CloudGatewayConfigValidator>();

    // Serilog
    builder.Services.AddSerilog(static configure =>
    {
        configure.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
    });

    // Services
    builder.Services.AddSingleton<MessageDeduplicator>();
    builder.Services.AddSingleton<TimescaleDbWriter>();
    builder.Services.AddSingleton<PostgreSqlDeviceStore>();
    builder.Services.AddSingleton<AuditLogService>();
    builder.Services.AddSingleton<ConsumerHealthTracker>();

    // Factory Registry
    builder.Services.AddSingleton<IFactoryRegistry, FileFactoryRegistry>();

    // Hosted Services
    builder.Services.AddHostedService<MultiRabbitMqConsumer>();

    // Database Initializers
    builder.Services.AddSingleton<TimescaleDbInitializer>();
    builder.Services.AddSingleton<PostgreSqlInitializer>();

    // Health Checks
    builder.Services.AddHealthChecks()
        .AddCheck<RabbitMqHealthCheck>("rabbitmq")
        .AddCheck<TimescaleDbHealthCheck>("timescaledb")
        .AddCheck<PostgreSqlHealthCheck>("postgresql");

    // Web / Health endpoint
    builder.WebHost.UseUrls("http://localhost:5000");
    var app = builder.Build();

    // Initialize databases
    using (var scope = app.Services.CreateScope())
    {
        var tsInit = scope.ServiceProvider.GetRequiredService<TimescaleDbInitializer>();
        var pgInit = scope.ServiceProvider.GetRequiredService<PostgreSqlInitializer>();
        await tsInit.InitializeAsync();
        await pgInit.InitializeAsync();
    }

    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            var tracker = context.RequestServices.GetRequiredService<ConsumerHealthTracker>();
            var consumers = tracker.GetSnapshot();

            var result = new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString() }),
                consumers = consumers.Select(c => new
                {
                    c.Value.FactoryId,
                    c.Value.IsOnline,
                    lagSeconds = c.Value.Lag?.TotalSeconds,
                    c.Value.TotalProcessed,
                    c.Value.TotalFailures
                })
            };

            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, result);
        }
    });

    await app.RunAsync();
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
