using AmGatewayCloud.Collector.OpcUa;
using AmGatewayCloud.Collector.OpcUa.Configuration;
using AmGatewayCloud.Collector.OpcUa.Output;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting AmGatewayCloud.Collector.OpcUa");

    var builder = Host.CreateApplicationBuilder(args);

    // Configuration
    builder.Services.Configure<CollectorConfig>(
        builder.Configuration.GetSection("Collector"));
    builder.Services.AddSingleton<IValidateOptions<CollectorConfig>, CollectorConfigValidator>();

    // Core services
    builder.Services.AddSingleton<OpcUaSession>();
    builder.Services.AddSingleton<IDataOutput, ConsoleDataOutput>();

    // Background service
    builder.Services.AddHostedService<OpcUaCollectorService>();

    // Serilog
    builder.Services.AddSerilog(static configure =>
    {
        configure.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
    });

    var host = builder.Build();
    await host.RunAsync();
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
