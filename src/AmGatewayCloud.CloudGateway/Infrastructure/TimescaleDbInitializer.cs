using AmGatewayCloud.CloudGateway.Configuration;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AmGatewayCloud.CloudGateway.Infrastructure;

public class TimescaleDbInitializer
{
    private readonly NpgsqlConnectionFactory _connectionFactory;
    private readonly ILogger<TimescaleDbInitializer> _logger;

    public TimescaleDbInitializer(
        IOptions<CloudGatewayConfig> options,
        ILogger<TimescaleDbInitializer> logger)
    {
        _connectionFactory = new NpgsqlConnectionFactory(options, options.Value.TimescaleDb.Database);
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        _logger.LogInformation("Ensuring TimescaleDB hypertable and policies...");

        // 1. 创建时序表
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS device_data (
                time TIMESTAMPTZ NOT NULL,
                batch_id UUID,
                tenant_id TEXT,
                factory_id TEXT,
                workshop_id TEXT,
                device_id TEXT,
                protocol TEXT,
                tag TEXT,
                quality TEXT,
                group_name TEXT,
                value_int BIGINT,
                value_float DOUBLE PRECISION,
                value_bool BOOLEAN,
                value_string TEXT,
                value_type TEXT,
                PRIMARY KEY (time, batch_id, tag)
            );
        ");

        // 2. 转换为 hypertable
        await conn.ExecuteAsync(@"
            SELECT create_hypertable('device_data', 'time', if_not_exists => TRUE);
        ");

        // 3. 创建索引
        await conn.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_device_data_lookup
            ON device_data (factory_id, device_id, tag, time DESC);
        ");

        // 4. 数据保留策略（原始数据 90 天）
        await conn.ExecuteAsync(@"
            SELECT add_retention_policy('device_data', INTERVAL '90 days', if_not_exists => TRUE);
        ");

        _logger.LogInformation("TimescaleDB initialization completed.");
    }
}
