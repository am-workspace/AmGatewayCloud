using AmGatewayCloud.Shared.Configuration;
using AmGatewayCloud.AlarmService.Models;
using Dapper;
using Npgsql;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// 报警规则仓储：对 alarm_rules 表的 CRUD 操作
/// </summary>
public class AlarmRuleRepository
{
    private readonly string _connectionString;
    private readonly ILogger<AlarmRuleRepository> _logger;

    public AlarmRuleRepository(PostgreSqlConfig config, ILogger<AlarmRuleRepository> logger)
    {
        _logger = logger;
        _connectionString = config.ConnectionString;
    }

    public async Task<List<AlarmRule>> GetEnabledRulesAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT id, name, tenant_id, factory_id, device_id, tag,
                   operator, threshold, threshold_string, clear_threshold, level,
                   cooldown_minutes, delay_seconds, enabled, description,
                   created_at, updated_at
            FROM alarm_rules
            WHERE enabled = TRUE";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync(sql);
        return rows.Select(MapAlarmRule).ToList();
    }

    public async Task<AlarmRule?> GetByIdAsync(string ruleId, CancellationToken ct)
    {
        const string sql = @"
            SELECT id, name, tenant_id, factory_id, device_id, tag,
                   operator, threshold, threshold_string, clear_threshold, level,
                   cooldown_minutes, delay_seconds, enabled, description,
                   created_at, updated_at
            FROM alarm_rules
            WHERE id = @ruleId";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync(sql, new { ruleId });
        return row is null ? null : MapAlarmRule(row);
    }

    public async Task<List<AlarmRule>> GetEnabledRulesByTagAsync(string tag, CancellationToken ct)
    {
        const string sql = @"
            SELECT id, name, tenant_id, factory_id, device_id, tag,
                   operator, threshold, threshold_string, clear_threshold, level,
                   cooldown_minutes, delay_seconds, enabled, description,
                   created_at, updated_at
            FROM alarm_rules
            WHERE enabled = TRUE AND tag = @tag";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync(sql, new { tag });
        return rows.Select(MapAlarmRule).ToList();
    }

    public async Task<List<AlarmRule>> GetAllRulesAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT id, name, tenant_id, factory_id, device_id, tag,
                   operator, threshold, threshold_string, clear_threshold, level,
                   cooldown_minutes, delay_seconds, enabled, description,
                   created_at, updated_at
            FROM alarm_rules
            ORDER BY created_at";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync(sql);
        return rows.Select(MapAlarmRule).ToList();
    }

    public async Task<int> InsertIfNotExistsAsync(AlarmRule rule, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO alarm_rules (
                id, name, tenant_id, factory_id, device_id, tag,
                operator, threshold, threshold_string, clear_threshold, level,
                cooldown_minutes, delay_seconds, enabled, description
            ) VALUES (
                @Id, @Name, @TenantId, @FactoryId, @DeviceId, @Tag,
                @Operator, @Threshold, @ThresholdString, @ClearThreshold, @Level,
                @CooldownMinutes, @DelaySeconds, @Enabled, @Description
            )
            ON CONFLICT (id) DO NOTHING";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync(sql, new
        {
            rule.Id, rule.Name, rule.TenantId, rule.FactoryId, rule.DeviceId,
            rule.Tag, rule.Operator, rule.Threshold, rule.ThresholdString,
            rule.ClearThreshold, rule.Level, rule.CooldownMinutes,
            rule.DelaySeconds, rule.Enabled, rule.Description
        });
    }

    /// <summary>
    /// 创建报警规则（含冲突检测）
    /// </summary>
    public async Task<(AlarmRule? Rule, string? Error)> CreateRuleAsync(AlarmRule rule, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO alarm_rules (
                id, name, tenant_id, factory_id, device_id, tag,
                operator, threshold, threshold_string, clear_threshold, level,
                cooldown_minutes, delay_seconds, enabled, description
            ) VALUES (
                @Id, @Name, @TenantId, @FactoryId, @DeviceId, @Tag,
                @Operator, @Threshold, @ThresholdString, @ClearThreshold, @Level,
                @CooldownMinutes, @DelaySeconds, @Enabled, @Description
            )";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        try
        {
            await conn.ExecuteAsync(sql, new
            {
                rule.Id, rule.Name, rule.TenantId, rule.FactoryId, rule.DeviceId,
                rule.Tag, rule.Operator, rule.Threshold, rule.ThresholdString,
                rule.ClearThreshold, rule.Level, rule.CooldownMinutes,
                rule.DelaySeconds, rule.Enabled, rule.Description
            });
        }
        catch (NpgsqlException ex) when (ex.SqlState == "23505")
        {
            return (null, $"Rule with id '{rule.Id}' already exists");
        }

        return (await GetByIdAsync(rule.Id, ct), null);
    }

    /// <summary>
    /// 动态更新报警规则字段
    /// </summary>
    public async Task<AlarmRule?> UpdateRuleAsync(string ruleId, Dictionary<string, object?> updates, CancellationToken ct)
    {
        if (updates.Count == 0) return await GetByIdAsync(ruleId, ct);

        var setClauses = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("ruleId", ruleId);

        var allowedColumns = new HashSet<string>
        {
            "name", "tenant_id", "factory_id", "device_id", "tag",
            "operator", "threshold", "threshold_string", "clear_threshold", "level",
            "cooldown_minutes", "delay_seconds", "enabled", "description"
        };

        foreach (var (key, value) in updates)
        {
            if (!allowedColumns.Contains(key))
                throw new ArgumentException($"Invalid column name: {key}", nameof(updates));
            setClauses.Add($"{key} = @{key}");
            parameters.Add(key, value);
        }
        setClauses.Add("updated_at = NOW()");

        var sql = $"UPDATE alarm_rules SET {string.Join(", ", setClauses)} WHERE id = @ruleId";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(sql, parameters);

        return await GetByIdAsync(ruleId, ct);
    }

    /// <summary>
    /// 删除报警规则（返回是否成功和错误信息）
    /// </summary>
    public async Task<(bool Success, string? Error)> DeleteRuleAsync(string ruleId, CancellationToken ct)
    {
        const string checkSql = @"
            SELECT COUNT(*) FROM alarm_events
            WHERE rule_id = @ruleId AND status IN ('Active', 'Acked')";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var activeCount = await conn.ExecuteScalarAsync<int>(checkSql, new { ruleId });

        if (activeCount > 0)
            return (false, $"Cannot delete rule '{ruleId}': has {activeCount} active/acked alarms");

        const string sql = "DELETE FROM alarm_rules WHERE id = @ruleId";
        var rows = await conn.ExecuteAsync(sql, new { ruleId });
        return (rows > 0, rows == 0 ? $"Rule '{ruleId}' not found" : null);
    }

    private static AlarmRule MapAlarmRule(dynamic row) => new()
    {
        Id = (string)row.id,
        Name = (string)row.name,
        TenantId = (string)row.tenant_id,
        FactoryId = row.factory_id as string,
        DeviceId = row.device_id as string,
        Tag = (string)row.tag,
        Operator = (string)row.@operator,
        Threshold = (double)row.threshold,
        ThresholdString = row.threshold_string as string,
        ClearThreshold = row.clear_threshold as double?,
        Level = (string)row.level,
        CooldownMinutes = (int)row.cooldown_minutes,
        DelaySeconds = (int)row.delay_seconds,
        Enabled = (bool)row.enabled,
        Description = row.description as string,
        CreatedAt = (DateTimeOffset)row.created_at,
        UpdatedAt = (DateTimeOffset)row.updated_at
    };
}
