using AmGatewayCloud.Shared.Configuration;
using AmGatewayCloud.AlarmService.Models;
using AmGatewayCloud.AlarmService.Services;
using Dapper;
using Npgsql;

namespace AmGatewayCloud.AlarmService.Infrastructure;

/// <summary>
/// 数据库初始化器：确保 alarm_rules/alarm_events 表存在，并插入种子规则数据
/// </summary>
public class AlarmDbInitializer
{
    private readonly string _connectionString;
    private readonly AlarmRuleRepository _ruleRepo;
    private readonly ILogger<AlarmDbInitializer> _logger;

    public AlarmDbInitializer(
        PostgreSqlConfig config,
        AlarmRuleRepository ruleRepo,
        ILogger<AlarmDbInitializer> logger)
    {
        _connectionString = config.ConnectionString;
        _ruleRepo = ruleRepo;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await EnsureTablesAsync(ct);
        await SeedRulesAsync(ct);
    }

    private async Task EnsureTablesAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS alarm_rules (
                id              TEXT PRIMARY KEY,
                name            TEXT NOT NULL,
                tenant_id       TEXT NOT NULL DEFAULT 'default',
                factory_id      TEXT,
                device_id       TEXT,
                tag             TEXT NOT NULL,
                operator        TEXT NOT NULL,
                threshold       DOUBLE PRECISION NOT NULL,
                threshold_string TEXT,
                clear_threshold DOUBLE PRECISION,
                level           TEXT NOT NULL DEFAULT 'Warning',
                cooldown_minutes INT NOT NULL DEFAULT 5,
                delay_seconds   INT NOT NULL DEFAULT 0,
                enabled         BOOLEAN NOT NULL DEFAULT TRUE,
                description     TEXT,
                created_at      TIMESTAMPTZ DEFAULT NOW(),
                updated_at      TIMESTAMPTZ DEFAULT NOW()
            )");

        await conn.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_alarm_rules_tag
                ON alarm_rules (tag, enabled) WHERE enabled = TRUE");
        await conn.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_alarm_rules_scope
                ON alarm_rules (tenant_id, factory_id, device_id)");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS alarm_events (
                id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                rule_id         TEXT NOT NULL REFERENCES alarm_rules(id),
                tenant_id       TEXT NOT NULL,
                factory_id      TEXT NOT NULL,
                workshop_id     TEXT,
                device_id       TEXT NOT NULL,
                tag             TEXT NOT NULL,
                trigger_value   DOUBLE PRECISION,
                level           TEXT NOT NULL,
                status          TEXT NOT NULL DEFAULT 'Active',
                is_stale        BOOLEAN NOT NULL DEFAULT FALSE,
                stale_at        TIMESTAMPTZ,
                message         TEXT,
                triggered_at    TIMESTAMPTZ NOT NULL,
                acknowledged_at TIMESTAMPTZ,
                acknowledged_by TEXT,
                suppressed_at   TIMESTAMPTZ,
                suppressed_by   TEXT,
                suppressed_reason TEXT,
                cleared_at      TIMESTAMPTZ,
                clear_value     DOUBLE PRECISION,
                created_at      TIMESTAMPTZ DEFAULT NOW()
            )");

        await conn.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_alarm_events_lookup
                ON alarm_events (tenant_id, factory_id, device_id, triggered_at DESC)");
        await conn.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_alarm_events_status
                ON alarm_events (status, triggered_at DESC) WHERE status IN ('Active', 'Acked', 'Suppressed')");
        await conn.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_alarm_events_rule_device
                ON alarm_events (rule_id, device_id, status) WHERE status IN ('Active', 'Acked')");
        await conn.ExecuteAsync(@"
            CREATE UNIQUE INDEX IF NOT EXISTS idx_alarm_events_active_unique
                ON alarm_events (rule_id, device_id) WHERE status IN ('Active', 'Acked')");

        _logger.LogInformation("Database tables and indexes verified");
    }

    private async Task SeedRulesAsync(CancellationToken ct)
    {
        var seedRules = GetSeedRules();
        int inserted = 0;

        foreach (var rule in seedRules)
        {
            var rows = await _ruleRepo.InsertIfNotExistsAsync(rule, ct);
            inserted += rows;
        }

        _logger.LogInformation("AlarmDbInitializer: {Inserted} seed rules inserted ({Total} already existed)",
            inserted, seedRules.Count - inserted);
    }

    private static List<AlarmRule> GetSeedRules() =>
    [
        new() { Id = "high-temp-warning", Name = "高温警告", Tag = "temperature", Operator = ">", Threshold = 28, ClearThreshold = 26, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "high-temp-critical", Name = "高温严重", Tag = "temperature", Operator = ">", Threshold = 35, ClearThreshold = 30, Level = "Critical", CooldownMinutes = 5 },
        new() { Id = "low-temp-warning", Name = "低温警告", Tag = "temperature", Operator = "<", Threshold = 18, ClearThreshold = 20, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "high-pressure-warning", Name = "高压警告", Tag = "pressure", Operator = ">", Threshold = 115, ClearThreshold = 110, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "low-pressure-warning", Name = "低压警告", Tag = "pressure", Operator = "<", Threshold = 85, ClearThreshold = 90, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "high-level-warning", Name = "液位过高", Tag = "level", Operator = ">", Threshold = 90, ClearThreshold = 85, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "high-level-critical", Name = "液位严重", Tag = "level", Operator = ">", Threshold = 95, ClearThreshold = 90, Level = "Critical", CooldownMinutes = 5 },
        new() { Id = "low-level-warning", Name = "液位过低", Tag = "level", Operator = "<", Threshold = 10, ClearThreshold = 15, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "high-voltage-warning", Name = "电压过高", Tag = "voltage", Operator = ">", Threshold = 395, ClearThreshold = 390, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "low-voltage-warning", Name = "电压过低", Tag = "voltage", Operator = "<", Threshold = 360, ClearThreshold = 365, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "high-current-warning", Name = "电流过大", Tag = "current", Operator = ">", Threshold = 18, ClearThreshold = 15, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "high-rpm-warning", Name = "转速过高", Tag = "rpm", Operator = ">", Threshold = 1650, ClearThreshold = 1600, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "high-humidity-warning", Name = "湿度过高", Tag = "humidity", Operator = ">", Threshold = 85, ClearThreshold = 80, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "freq-abnormal-warning", Name = "频率异常", Tag = "frequency", Operator = ">", Threshold = 50.8, ClearThreshold = 50.5, Level = "Warning", CooldownMinutes = 5 },
        new() { Id = "device-alarm", Name = "设备报警", Tag = "diAlarm", Operator = "==", Threshold = 1, ClearThreshold = 0, Level = "Warning", CooldownMinutes = 2 },
        new() { Id = "quality-bad", Name = "数据质量差", Tag = "quality", Operator = "==", Threshold = 0, ThresholdString = "Bad", Level = "Warning", CooldownMinutes = 10, Description = "quality == Bad" }
    ];
}
