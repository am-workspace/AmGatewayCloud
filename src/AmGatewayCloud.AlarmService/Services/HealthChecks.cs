using AmGatewayCloud.AlarmInfrastructure.Persistence;
using AmGatewayCloud.Shared.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// PostgreSQL 连接健康检查（使用 EF Core，不再依赖 Dapper）
/// </summary>
public class PostgreSqlHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public PostgreSqlHealthCheck(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            var canConnect = await dbContext.Database.CanConnectAsync(ct);
            return canConnect
                ? HealthCheckResult.Healthy("PostgreSQL connection OK")
                : HealthCheckResult.Unhealthy("PostgreSQL connection failed");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"PostgreSQL connection failed: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// RabbitMQ 连接健康检查
/// </summary>
public class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqConfig _config;

    public RabbitMqHealthCheck(RabbitMqConfig config)
    {
        _config = config;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _config.HostName,
                Port = _config.Port,
                UserName = _config.Username,
                Password = _config.Password,
                VirtualHost = _config.VirtualHost,
                AutomaticRecoveryEnabled = true,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(3)
            };

            using var connection = factory.CreateConnection();
            return connection.IsOpen
                ? Task.FromResult(HealthCheckResult.Healthy("RabbitMQ connection OK"))
                : Task.FromResult(HealthCheckResult.Unhealthy("RabbitMQ connection is closed"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"RabbitMQ connection failed: {ex.Message}", ex));
        }
    }
}
