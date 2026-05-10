using AmGatewayCloud.Shared.Configuration;
using Dapper;
using Npgsql;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// PostgreSQL 连接健康检查
/// </summary>
public class PostgreSqlHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public PostgreSqlHealthCheck(PostgreSqlConfig config)
    {
        _connectionString = config.ConnectionString;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            var result = await conn.ExecuteScalarAsync<int>("SELECT 1", ct);
            return result == 1
                ? HealthCheckResult.Healthy("PostgreSQL connection OK")
                : HealthCheckResult.Unhealthy("PostgreSQL query returned unexpected result");
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
