using AmGatewayCloud.Shared.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace AmGatewayCloud.WebApi.Services;

/// <summary>
/// RabbitMQ 连接健康检查（WebApi 只依赖 MQ，不依赖数据库）
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
