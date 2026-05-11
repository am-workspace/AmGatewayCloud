using Npgsql;
using AmGatewayCloud.WorkOrderService.Configuration;

namespace AmGatewayCloud.WorkOrderService.Infrastructure;

/// <summary>
/// 数据库初始化器：创建 work_orders 表和索引
/// </summary>
public class WorkOrderDbInitializer
{
    private readonly WorkOrderServiceConfig _config;
    private readonly ILogger<WorkOrderDbInitializer> _logger;

    public WorkOrderDbInitializer(WorkOrderServiceConfig config, ILogger<WorkOrderDbInitializer> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_config.PostgreSql.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS work_orders (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    alarm_id        UUID NOT NULL,
    tenant_id       TEXT NOT NULL,
    factory_id      TEXT NOT NULL,
    workshop_id     TEXT,
    device_id       TEXT NOT NULL,
    title           TEXT NOT NULL,
    description     TEXT,
    level           TEXT NOT NULL DEFAULT 'Warning',
    status          TEXT NOT NULL DEFAULT 'Pending',
    assignee        TEXT,
    assigned_at     TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,
    completed_by    TEXT,
    completion_note TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_work_orders_lookup
    ON work_orders (tenant_id, factory_id, status, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_work_orders_alarm
    ON work_orders (alarm_id);
";
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Database schema verified (work_orders table + indexes)");
    }
}
