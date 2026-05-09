using AmGatewayCloud.CloudGateway.Configuration;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AmGatewayCloud.CloudGateway.Infrastructure;

public class PostgreSqlInitializer
{
    private readonly NpgsqlConnectionFactory _connectionFactory;
    private readonly ILogger<PostgreSqlInitializer> _logger;

    public PostgreSqlInitializer(
        IOptions<CloudGatewayConfig> options,
        ILogger<PostgreSqlInitializer> logger)
    {
        _connectionFactory = new NpgsqlConnectionFactory(options, options.Value.PostgreSql.Database);
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        _logger.LogInformation("Ensuring PostgreSQL business tables...");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS factories (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                tenant_id TEXT NOT NULL,
                rabbitmq_vhost TEXT,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS workshops (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                factory_id TEXT REFERENCES factories(id),
                created_at TIMESTAMPTZ DEFAULT NOW()
            );
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS devices (
                id TEXT PRIMARY KEY,
                name TEXT,
                factory_id TEXT REFERENCES factories(id),
                workshop_id TEXT REFERENCES workshops(id),
                protocol TEXT NOT NULL,
                tenant_id TEXT NOT NULL,
                first_seen_at TIMESTAMPTZ,
                last_seen_at TIMESTAMPTZ,
                tags TEXT[],
                created_at TIMESTAMPTZ DEFAULT NOW(),
                updated_at TIMESTAMPTZ DEFAULT NOW()
            );
        ");

        await conn.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_devices_lookup
            ON devices (factory_id, workshop_id, id);
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS audit_logs (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                factory_id TEXT NOT NULL,
                batch_id TEXT,
                reason TEXT NOT NULL,
                raw_payload_preview TEXT,
                received_at TIMESTAMPTZ DEFAULT NOW()
            );
        ");

        _logger.LogInformation("PostgreSQL initialization completed.");
    }
}
