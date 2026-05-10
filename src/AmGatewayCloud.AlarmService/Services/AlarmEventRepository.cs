using AmGatewayCloud.Shared.Configuration;
using AmGatewayCloud.AlarmService.Models;
using Dapper;
using Npgsql;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// 报警事件仓储：对 alarm_events 表的 CRUD 操作
/// </summary>
public class AlarmEventRepository
{
    private readonly string _connectionString;
    private readonly ILogger<AlarmEventRepository> _logger;

    public AlarmEventRepository(PostgreSqlConfig config, ILogger<AlarmEventRepository> logger)
    {
        _logger = logger;
        _connectionString = config.ConnectionString;
    }

    public async Task InsertAsync(AlarmEvent alarmEvent, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO alarm_events (
                rule_id, tenant_id, factory_id, workshop_id, device_id, tag,
                trigger_value, level, status, is_stale, message,
                triggered_at
            ) VALUES (
                @RuleId, @TenantId, @FactoryId, @WorkshopId, @DeviceId, @Tag,
                @TriggerValue, @Level, @Status, @IsStale, @Message,
                @TriggeredAt
            )";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(sql, new
        {
            alarmEvent.RuleId, alarmEvent.TenantId, alarmEvent.FactoryId,
            alarmEvent.WorkshopId, alarmEvent.DeviceId, alarmEvent.Tag,
            alarmEvent.TriggerValue, alarmEvent.Level,
            Status = alarmEvent.Status.ToString(),
            alarmEvent.IsStale, alarmEvent.Message, alarmEvent.TriggeredAt
        });
    }

    public async Task UpdateAsync(AlarmEvent alarmEvent, CancellationToken ct)
    {
        const string sql = @"
            UPDATE alarm_events SET
                status = @Status, is_stale = @IsStale, stale_at = @StaleAt,
                acknowledged_at = @AcknowledgedAt, acknowledged_by = @AcknowledgedBy,
                suppressed_at = @SuppressedAt, suppressed_by = @SuppressedBy,
                suppressed_reason = @SuppressedReason,
                cleared_at = @ClearedAt, clear_value = @ClearValue
            WHERE id = @Id";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(sql, new
        {
            Id = alarmEvent.Id,
            Status = alarmEvent.Status.ToString(),
            alarmEvent.IsStale, alarmEvent.StaleAt,
            alarmEvent.AcknowledgedAt, alarmEvent.AcknowledgedBy,
            alarmEvent.SuppressedAt, alarmEvent.SuppressedBy, alarmEvent.SuppressedReason,
            alarmEvent.ClearedAt, alarmEvent.ClearValue
        });
    }

    public async Task<AlarmEvent?> GetActiveAlarmAsync(string ruleId, string deviceId, CancellationToken ct)
    {
        const string sql = @"
            SELECT id, rule_id, tenant_id, factory_id, workshop_id, device_id, tag,
                   trigger_value, level, status, is_stale, stale_at, message,
                   triggered_at, acknowledged_at, acknowledged_by,
                   suppressed_at, suppressed_by, suppressed_reason,
                   cleared_at, clear_value, created_at
            FROM alarm_events
            WHERE rule_id = @ruleId AND device_id = @deviceId
              AND status IN ('Active', 'Acked')";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(sql, new { ruleId, deviceId });
        return row is null ? null : MapAlarmEvent(row);
    }

    public async Task<AlarmEvent?> GetSuppressedAlarmAsync(string ruleId, string deviceId, CancellationToken ct)
    {
        const string sql = @"
            SELECT id, rule_id, tenant_id, factory_id, workshop_id, device_id, tag,
                   trigger_value, level, status, is_stale, stale_at, message,
                   triggered_at, acknowledged_at, acknowledged_by,
                   suppressed_at, suppressed_by, suppressed_reason,
                   cleared_at, clear_value, created_at
            FROM alarm_events
            WHERE rule_id = @ruleId AND device_id = @deviceId
              AND status = 'Suppressed'";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(sql, new { ruleId, deviceId });
        return row is null ? null : MapAlarmEvent(row);
    }

    public async Task<List<AlarmEvent>> GetOpenAlarmsAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT id, rule_id, tenant_id, factory_id, workshop_id, device_id, tag,
                   trigger_value, level, status, is_stale, stale_at, message,
                   triggered_at, acknowledged_at, acknowledged_by,
                   suppressed_at, suppressed_by, suppressed_reason,
                   cleared_at, clear_value, created_at
            FROM alarm_events
            WHERE status IN ('Active', 'Acked', 'Suppressed')";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync(sql);
        return rows.Select(MapAlarmEvent).ToList();
    }

    public async Task ClearStaleFlagAsync(string deviceId, CancellationToken ct)
    {
        const string sql = @"
            UPDATE alarm_events SET is_stale = FALSE, stale_at = NULL
            WHERE device_id = @deviceId AND is_stale = TRUE
              AND status IN ('Active', 'Acked', 'Suppressed')";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(sql, new { deviceId });
    }

    public async Task MarkStaleAsync(Guid alarmId, CancellationToken ct)
    {
        const string sql = @"
            UPDATE alarm_events SET is_stale = TRUE, stale_at = NOW()
            WHERE id = @alarmId";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(sql, new { alarmId });
    }

    public async Task<DateTimeOffset?> GetLastTriggerTimeAsync(CancellationToken ct)
    {
        const string sql = "SELECT MAX(triggered_at) FROM alarm_events";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<DateTimeOffset?>(sql);
    }

    /// <summary>
    /// 分页查询报警事件列表
    /// </summary>
    public async Task<(List<AlarmEventWithRuleName> Items, int TotalCount)> QueryAlarmsAsync(
        string? factoryId, string? deviceId, string? status, string? level,
        bool? isStale, int page, int pageSize, CancellationToken ct)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrEmpty(factoryId)) { conditions.Add("a.factory_id = @factoryId"); parameters.Add("factoryId", factoryId); }
        if (!string.IsNullOrEmpty(deviceId)) { conditions.Add("a.device_id = @deviceId"); parameters.Add("deviceId", deviceId); }
        if (!string.IsNullOrEmpty(status)) { conditions.Add("a.status = @status"); parameters.Add("status", status); }
        if (!string.IsNullOrEmpty(level)) { conditions.Add("a.level = @level"); parameters.Add("level", level); }
        if (isStale.HasValue) { conditions.Add("a.is_stale = @isStale"); parameters.Add("isStale", isStale.Value); }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        var countSql = $"SELECT COUNT(*) FROM alarm_events a {whereClause}";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var totalCount = await conn.ExecuteScalarAsync<int>(countSql, parameters);

        var offset = (page - 1) * pageSize;
        var dataSql = $@"
            SELECT a.id, a.rule_id, COALESCE(r.name, a.rule_id) AS rule_name,
                   a.tenant_id, a.factory_id, a.workshop_id, a.device_id, a.tag,
                   a.trigger_value, a.level, a.status, a.is_stale, a.stale_at, a.message,
                   a.triggered_at, a.acknowledged_at, a.acknowledged_by,
                   a.suppressed_at, a.suppressed_by, a.suppressed_reason,
                   a.cleared_at, a.clear_value, a.created_at
            FROM alarm_events a
            LEFT JOIN alarm_rules r ON a.rule_id = r.id
            {whereClause}
            ORDER BY a.triggered_at DESC
            LIMIT @limit OFFSET @offset";

        parameters.Add("limit", pageSize);
        parameters.Add("offset", offset);
        var rows = await conn.QueryAsync(dataSql, parameters);
        var items = rows.Select(MapAlarmEventWithRuleName).ToList();

        return (items, totalCount);
    }

    /// <summary>
    /// 根据 ID 获取报警事件（含规则名称）
    /// </summary>
    public async Task<AlarmEventWithRuleName?> GetByIdWithRuleNameAsync(Guid id, CancellationToken ct)
    {
        const string sql = @"
            SELECT a.id, a.rule_id, COALESCE(r.name, a.rule_id) AS rule_name,
                   a.tenant_id, a.factory_id, a.workshop_id, a.device_id, a.tag,
                   a.trigger_value, a.level, a.status, a.is_stale, a.stale_at, a.message,
                   a.triggered_at, a.acknowledged_at, a.acknowledged_by,
                   a.suppressed_at, a.suppressed_by, a.suppressed_reason,
                   a.cleared_at, a.clear_value, a.created_at
            FROM alarm_events a
            LEFT JOIN alarm_rules r ON a.rule_id = r.id
            WHERE a.id = @id";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(sql, new { id });
        return row is null ? null : MapAlarmEventWithRuleName(row);
    }

    /// <summary>
    /// 确认报警（Active → Acked）
    /// </summary>
    public async Task<bool> AcknowledgeAsync(Guid id, string acknowledgedBy, CancellationToken ct)
    {
        const string sql = @"
            UPDATE alarm_events
            SET status = 'Acked', acknowledged_at = NOW(), acknowledged_by = @acknowledgedBy
            WHERE id = @id AND status = 'Active'";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync(sql, new { id, acknowledgedBy }) > 0;
    }

    /// <summary>
    /// 手动抑制报警（Active/Acked → Suppressed）
    /// </summary>
    public async Task<bool> SuppressAsync(Guid id, string suppressedBy, string? reason, CancellationToken ct)
    {
        const string sql = @"
            UPDATE alarm_events
            SET status = 'Suppressed', suppressed_at = NOW(),
                suppressed_by = @suppressedBy, suppressed_reason = @reason
            WHERE id = @id AND status IN ('Active', 'Acked')";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync(sql, new { id, suppressedBy, reason }) > 0;
    }

    /// <summary>
    /// 手动关闭报警（→ Cleared）
    /// </summary>
    public async Task<bool> ClearAsync(Guid id, CancellationToken ct)
    {
        const string sql = @"
            UPDATE alarm_events
            SET status = 'Cleared', cleared_at = NOW()
            WHERE id = @id AND status IN ('Active', 'Acked', 'Suppressed')";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync(sql, new { id }) > 0;
    }

    private static AlarmEvent MapAlarmEvent(dynamic row) => new()
    {
        Id = (Guid)row.id,
        RuleId = (string)row.rule_id,
        TenantId = (string)row.tenant_id,
        FactoryId = (string)row.factory_id,
        WorkshopId = row.workshop_id as string,
        DeviceId = (string)row.device_id,
        Tag = (string)row.tag,
        TriggerValue = row.trigger_value as double?,
        Level = (string)row.level,
        Status = Enum.Parse<AlarmStatus>((string)row.status),
        IsStale = (bool)row.is_stale,
        StaleAt = row.stale_at as DateTimeOffset?,
        Message = row.message as string,
        TriggeredAt = (DateTimeOffset)row.triggered_at,
        AcknowledgedAt = row.acknowledged_at as DateTimeOffset?,
        AcknowledgedBy = row.acknowledged_by as string,
        SuppressedAt = row.suppressed_at as DateTimeOffset?,
        SuppressedBy = row.suppressed_by as string,
        SuppressedReason = row.suppressed_reason as string,
        ClearedAt = row.cleared_at as DateTimeOffset?,
        ClearValue = row.clear_value as double?,
        CreatedAt = (DateTimeOffset)row.created_at
    };

    private static AlarmEventWithRuleName MapAlarmEventWithRuleName(dynamic row) => new()
    {
        Id = (Guid)row.id,
        RuleId = (string)row.rule_id,
        RuleName = (string)row.rule_name,
        TenantId = (string)row.tenant_id,
        FactoryId = (string)row.factory_id,
        WorkshopId = row.workshop_id as string,
        DeviceId = (string)row.device_id,
        Tag = (string)row.tag,
        TriggerValue = row.trigger_value as double?,
        Level = (string)row.level,
        Status = (string)row.status,
        IsStale = (bool)row.is_stale,
        StaleAt = row.stale_at as DateTimeOffset?,
        Message = row.message as string,
        TriggeredAt = (DateTimeOffset)row.triggered_at,
        AcknowledgedAt = row.acknowledged_at as DateTimeOffset?,
        AcknowledgedBy = row.acknowledged_by as string,
        SuppressedAt = row.suppressed_at as DateTimeOffset?,
        SuppressedBy = row.suppressed_by as string,
        SuppressedReason = row.suppressed_reason as string,
        ClearedAt = row.cleared_at as DateTimeOffset?,
        ClearValue = row.clear_value as double?,
        CreatedAt = (DateTimeOffset)row.created_at
    };
}

/// <summary>
/// 报警事件 + 规则名称（查询返回用）
/// </summary>
public class AlarmEventWithRuleName
{
    public Guid Id { get; set; }
    public string RuleId { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string FactoryId { get; set; } = string.Empty;
    public string? WorkshopId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public double? TriggerValue { get; set; }
    public string Level { get; set; } = "Warning";
    public string Status { get; set; } = "Active";
    public bool IsStale { get; set; }
    public DateTimeOffset? StaleAt { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset TriggeredAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTimeOffset? SuppressedAt { get; set; }
    public string? SuppressedBy { get; set; }
    public string? SuppressedReason { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    public double? ClearValue { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
