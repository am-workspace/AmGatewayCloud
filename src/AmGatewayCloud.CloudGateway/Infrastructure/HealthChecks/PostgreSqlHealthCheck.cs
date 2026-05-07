using AmGatewayCloud.CloudGateway.Configuration;
using AmGatewayCloud.CloudGateway.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AmGatewayCloud.CloudGateway.Infrastructure.HealthChecks;

public class PostgreSqlHealthCheck : IHealthCheck
{
    private readonly NpgsqlConnectionFactory _connectionFactory;

    public PostgreSqlHealthCheck(IOptions<CloudGatewayConfig> options)
    {
        _connectionFactory = new NpgsqlConnectionFactory(options, options.Value.PostgreSql.Database);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = _connectionFactory.CreateConnection();
            await conn.OpenAsync(ct);
            return HealthCheckResult.Healthy("PostgreSQL is reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unreachable", ex);
        }
    }
}
