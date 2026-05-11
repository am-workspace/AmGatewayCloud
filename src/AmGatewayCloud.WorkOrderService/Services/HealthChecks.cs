using AmGatewayCloud.WorkOrderService.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using AmGatewayCloud.WorkOrderService.Infrastructure;

namespace AmGatewayCloud.WorkOrderService.Services;

/// <summary>
/// PostgreSQL 健康检查
/// </summary>
public class PostgreSqlHealthCheck : IHealthCheck
{
    private readonly WorkOrderServiceConfig _config;

    public PostgreSqlHealthCheck(WorkOrderServiceConfig config)
    {
        _config = config;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_config.PostgreSql.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy("PostgreSQL connection OK");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connection failed", ex);
        }
    }
}

/// <summary>
/// RabbitMQ 健康检查
/// </summary>
public class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqConnectionManager _connectionManager;

    public RabbitMqHealthCheck(RabbitMqConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var channel = await _connectionManager.GetChannelAsync(ct);
            return channel.IsOpen
                ? HealthCheckResult.Healthy("RabbitMQ connection OK")
                : HealthCheckResult.Unhealthy("RabbitMQ channel closed");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ connection failed", ex);
        }
    }
}
