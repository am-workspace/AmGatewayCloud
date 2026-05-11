using AmGatewayCloud.AlarmDomain.Aggregates.Alarm;
using AmGatewayCloud.AlarmInfrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AmGatewayCloud.AlarmInfrastructure.Repositories;

/// <summary>
/// 报警规则仓储（EF Core 实现）：对 alarm_rules 表的 CRUD 操作
/// </summary>
public class AlarmRuleRepository
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<AlarmRuleRepository> _logger;

    public AlarmRuleRepository(AppDbContext dbContext, ILogger<AlarmRuleRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<List<AlarmRule>> GetEnabledRulesAsync(CancellationToken ct)
    {
        var entities = await _dbContext.AlarmRules
            .Where(r => r.Enabled)
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    public async Task<AlarmRule?> GetByIdAsync(string ruleId, CancellationToken ct)
    {
        var entity = await _dbContext.AlarmRules.FindAsync([ruleId], ct);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<List<AlarmRule>> GetEnabledRulesByTagAsync(string tag, CancellationToken ct)
    {
        var entities = await _dbContext.AlarmRules
            .Where(r => r.Enabled && r.Tag == tag)
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    public async Task<List<AlarmRule>> GetAllRulesAsync(CancellationToken ct)
    {
        var entities = await _dbContext.AlarmRules
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    public async Task<int> InsertIfNotExistsAsync(AlarmRule rule, CancellationToken ct)
    {
        var existing = await _dbContext.AlarmRules.FindAsync([rule.Id], ct);
        if (existing is not null) return 0;

        var entity = ToEntity(rule);
        _dbContext.AlarmRules.Add(entity);
        await _dbContext.SaveChangesAsync(ct);
        return 1;
    }

    /// <summary>
    /// 创建报警规则（含冲突检测）
    /// </summary>
    public async Task<(AlarmRule? Rule, string? Error)> CreateRuleAsync(AlarmRule rule, CancellationToken ct)
    {
        var entity = ToEntity(rule);
        _dbContext.AlarmRules.Add(entity);

        try
        {
            await _dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
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
        var entity = await _dbContext.AlarmRules.FindAsync([ruleId], ct);
        if (entity is null) return null;

        var allowedProperties = new HashSet<string>
        {
            "name", "tenant_id", "factory_id", "device_id", "tag",
            "operator", "threshold", "threshold_string", "clear_threshold", "level",
            "cooldown_minutes", "delay_seconds", "enabled", "description"
        };

        foreach (var (key, value) in updates)
        {
            if (!allowedProperties.Contains(key))
                throw new ArgumentException($"Invalid column name: {key}", nameof(updates));

            switch (key)
            {
                case "name": entity.Name = (string)value!; break;
                case "tenant_id": entity.TenantId = (string)value!; break;
                case "factory_id": entity.FactoryId = (string?)value; break;
                case "device_id": entity.DeviceId = (string?)value; break;
                case "tag": entity.Tag = (string)value!; break;
                case "operator": entity.Operator = (string)value!; break;
                case "threshold": entity.Threshold = Convert.ToDouble(value); break;
                case "threshold_string": entity.ThresholdString = (string?)value; break;
                case "clear_threshold": entity.ClearThreshold = value is null ? null : Convert.ToDouble(value); break;
                case "level": entity.Level = (string)value!; break;
                case "cooldown_minutes": entity.CooldownMinutes = Convert.ToInt32(value); break;
                case "delay_seconds": entity.DelaySeconds = Convert.ToInt32(value); break;
                case "enabled": entity.Enabled = Convert.ToBoolean(value); break;
                case "description": entity.Description = (string?)value; break;
            }
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
        return ToDomain(entity);
    }

    /// <summary>
    /// 删除报警规则（返回是否成功和错误信息）
    /// </summary>
    public async Task<(bool Success, string? Error)> DeleteRuleAsync(string ruleId, CancellationToken ct)
    {
        var eventCount = await _dbContext.AlarmEvents
            .CountAsync(e => e.RuleId == ruleId, ct);

        if (eventCount > 0)
            return (false, $"Cannot delete rule '{ruleId}': has {eventCount} associated alarm events");

        var entity = await _dbContext.AlarmRules.FindAsync([ruleId], ct);
        if (entity is null)
            return (false, $"Rule '{ruleId}' not found");

        _dbContext.AlarmRules.Remove(entity);
        await _dbContext.SaveChangesAsync(ct);
        return (true, null);
    }

    // ── 映射方法 ──

    private static AlarmRule ToDomain(AlarmRuleEntity entity)
    {
        return new AlarmRule(
            entity.Id, entity.Name, entity.TenantId, entity.FactoryId, entity.DeviceId,
            entity.Tag, AlarmRule.ParseOperator(entity.Operator), entity.Threshold,
            entity.ThresholdString, entity.ClearThreshold, AlarmRule.ParseLevel(entity.Level),
            entity.CooldownMinutes, entity.DelaySeconds, entity.Enabled, entity.Description,
            entity.CreatedAt, entity.UpdatedAt);
    }

    private static AlarmRuleEntity ToEntity(AlarmRule rule)
    {
        return new AlarmRuleEntity
        {
            Id = rule.Id,
            Name = rule.Name,
            TenantId = rule.TenantId,
            FactoryId = rule.FactoryId,
            DeviceId = rule.DeviceId,
            Tag = rule.Tag,
            Operator = rule.OperatorString,
            Threshold = rule.Threshold,
            ThresholdString = rule.ThresholdString,
            ClearThreshold = rule.ClearThreshold,
            Level = rule.LevelString,
            CooldownMinutes = rule.CooldownMinutes,
            DelaySeconds = rule.DelaySeconds,
            Enabled = rule.Enabled,
            Description = rule.Description
        };
    }

    private static bool IsUniqueViolation(Exception ex)
    {
        return ex.InnerException is Npgsql.NpgsqlException pgEx && pgEx.SqlState == "23505";
    }
}
