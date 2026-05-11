using AmGatewayCloud.AlarmDomain.Aggregates.Alarm;
using AmGatewayCloud.AlarmInfrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AmGatewayCloud.AlarmInfrastructure.Repositories;

/// <summary>
/// 报警事件仓储（EF Core 实现）：对 alarm_events 表的 CRUD 操作
/// </summary>
public class AlarmEventRepository
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<AlarmEventRepository> _logger;

    public AlarmEventRepository(AppDbContext dbContext, ILogger<AlarmEventRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task InsertAsync(Alarm alarm, CancellationToken ct)
    {
        var entity = ToEntity(alarm);
        _dbContext.AlarmEvents.Add(entity);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Alarm alarm, CancellationToken ct)
    {
        var entity = await _dbContext.AlarmEvents.FindAsync([alarm.Id], ct);
        if (entity is null) return;

        ApplyChanges(entity, alarm);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<Alarm?> GetActiveAlarmAsync(string ruleId, string deviceId, CancellationToken ct)
    {
        var entity = await _dbContext.AlarmEvents
            .FirstOrDefaultAsync(e => e.RuleId == ruleId && e.DeviceId == deviceId
                && (e.Status == "Active" || e.Status == "Acked"), ct);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<Alarm?> GetSuppressedAlarmAsync(string ruleId, string deviceId, CancellationToken ct)
    {
        var entity = await _dbContext.AlarmEvents
            .FirstOrDefaultAsync(e => e.RuleId == ruleId && e.DeviceId == deviceId
                && e.Status == "Suppressed", ct);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<List<Alarm>> GetOpenAlarmsAsync(CancellationToken ct)
    {
        var entities = await _dbContext.AlarmEvents
            .Where(e => e.Status == "Active" || e.Status == "Acked" || e.Status == "Suppressed")
            .ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    public async Task ClearStaleFlagAsync(string deviceId, CancellationToken ct)
    {
        var staleAlarms = await _dbContext.AlarmEvents
            .Where(e => e.DeviceId == deviceId && e.IsStale
                && (e.Status == "Active" || e.Status == "Acked" || e.Status == "Suppressed"))
            .ToListAsync(ct);

        foreach (var entity in staleAlarms)
        {
            entity.IsStale = false;
            entity.StaleAt = null;
        }

        if (staleAlarms.Count > 0)
            await _dbContext.SaveChangesAsync(ct);
    }

    public async Task MarkStaleAsync(Guid alarmId, CancellationToken ct)
    {
        var entity = await _dbContext.AlarmEvents.FindAsync([alarmId], ct);
        if (entity is null) return;

        entity.IsStale = true;
        entity.StaleAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<DateTimeOffset?> GetLastTriggerTimeAsync(CancellationToken ct)
    {
        return await _dbContext.AlarmEvents.MaxAsync(e => (DateTimeOffset?)e.TriggeredAt, ct);
    }

    /// <summary>
    /// 分页查询报警事件列表（含规则名称）
    /// </summary>
    public async Task<(List<AlarmEventWithRuleName> Items, int TotalCount)> QueryAlarmsAsync(
        string? factoryId, string? deviceId, string? status, string? level,
        bool? isStale, int page, int pageSize, CancellationToken ct)
    {
        var query = _dbContext.AlarmEvents.Include(e => e.Rule).AsQueryable();

        if (!string.IsNullOrEmpty(factoryId))
            query = query.Where(e => e.FactoryId == factoryId);
        if (!string.IsNullOrEmpty(deviceId))
            query = query.Where(e => e.DeviceId == deviceId);
        if (!string.IsNullOrEmpty(status))
            query = query.Where(e => e.Status == status);
        if (!string.IsNullOrEmpty(level))
            query = query.Where(e => e.Level == level);
        if (isStale.HasValue)
            query = query.Where(e => e.IsStale == isStale.Value);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(e => e.TriggeredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new AlarmEventWithRuleName
            {
                Id = e.Id,
                RuleId = e.RuleId,
                RuleName = e.Rule != null ? e.Rule.Name : e.RuleId,
                TenantId = e.TenantId,
                FactoryId = e.FactoryId,
                WorkshopId = e.WorkshopId,
                DeviceId = e.DeviceId,
                Tag = e.Tag,
                TriggerValue = e.TriggerValue,
                Level = e.Level,
                Status = e.Status,
                IsStale = e.IsStale,
                StaleAt = e.StaleAt,
                Message = e.Message,
                TriggeredAt = e.TriggeredAt,
                AcknowledgedAt = e.AcknowledgedAt,
                AcknowledgedBy = e.AcknowledgedBy,
                SuppressedAt = e.SuppressedAt,
                SuppressedBy = e.SuppressedBy,
                SuppressedReason = e.SuppressedReason,
                ClearedAt = e.ClearedAt,
                ClearValue = e.ClearValue,
                CreatedAt = e.CreatedAt
            })
            .ToListAsync(ct);

        return (items, totalCount);
    }

    /// <summary>
    /// 根据 ID 获取报警事件（含规则名称）
    /// </summary>
    public async Task<AlarmEventWithRuleName?> GetByIdWithRuleNameAsync(Guid id, CancellationToken ct)
    {
        return await _dbContext.AlarmEvents
            .Include(e => e.Rule)
            .Where(e => e.Id == id)
            .Select(e => new AlarmEventWithRuleName
            {
                Id = e.Id,
                RuleId = e.RuleId,
                RuleName = e.Rule != null ? e.Rule.Name : e.RuleId,
                TenantId = e.TenantId,
                FactoryId = e.FactoryId,
                WorkshopId = e.WorkshopId,
                DeviceId = e.DeviceId,
                Tag = e.Tag,
                TriggerValue = e.TriggerValue,
                Level = e.Level,
                Status = e.Status,
                IsStale = e.IsStale,
                StaleAt = e.StaleAt,
                Message = e.Message,
                TriggeredAt = e.TriggeredAt,
                AcknowledgedAt = e.AcknowledgedAt,
                AcknowledgedBy = e.AcknowledgedBy,
                SuppressedAt = e.SuppressedAt,
                SuppressedBy = e.SuppressedBy,
                SuppressedReason = e.SuppressedReason,
                ClearedAt = e.ClearedAt,
                ClearValue = e.ClearValue,
                CreatedAt = e.CreatedAt
            })
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 根据 ID 获取报警聚合根（用于领域操作）
    /// </summary>
    public async Task<Alarm?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var entity = await _dbContext.AlarmEvents.FindAsync([id], ct);
        return entity is null ? null : ToDomain(entity);
    }

    // ── 映射方法 ──

    private static AlarmEventEntity ToEntity(Alarm alarm) => new()
    {
        Id = alarm.Id,
        RuleId = alarm.RuleId,
        TenantId = alarm.TenantId,
        FactoryId = alarm.FactoryId,
        WorkshopId = alarm.WorkshopId,
        DeviceId = alarm.DeviceId,
        Tag = alarm.Tag,
        TriggerValue = alarm.TriggerValue,
        Level = alarm.LevelString,
        Status = alarm.StatusString,
        IsStale = alarm.IsStale,
        StaleAt = alarm.StaleAt,
        Message = alarm.Message,
        TriggeredAt = alarm.TriggeredAt,
        AcknowledgedAt = alarm.AcknowledgedAt,
        AcknowledgedBy = alarm.AcknowledgedBy,
        SuppressedAt = alarm.SuppressedAt,
        SuppressedBy = alarm.SuppressedBy,
        SuppressedReason = alarm.SuppressedReason,
        ClearedAt = alarm.ClearedAt,
        ClearValue = alarm.ClearValue,
        CreatedAt = alarm.CreatedAt
    };

    private static void ApplyChanges(AlarmEventEntity entity, Alarm alarm)
    {
        entity.Status = alarm.StatusString;
        entity.IsStale = alarm.IsStale;
        entity.StaleAt = alarm.StaleAt;
        entity.AcknowledgedAt = alarm.AcknowledgedAt;
        entity.AcknowledgedBy = alarm.AcknowledgedBy;
        entity.SuppressedAt = alarm.SuppressedAt;
        entity.SuppressedBy = alarm.SuppressedBy;
        entity.SuppressedReason = alarm.SuppressedReason;
        entity.ClearedAt = alarm.ClearedAt;
        entity.ClearValue = alarm.ClearValue;
    }

    private static Alarm ToDomain(AlarmEventEntity entity)
    {
        return Alarm.Reconstruct(
            entity.Id, entity.RuleId, entity.TenantId, entity.FactoryId, entity.WorkshopId,
            entity.DeviceId, entity.Tag, entity.TriggerValue,
            AlarmRule.ParseLevel(entity.Level),
            Enum.Parse<AlarmStatus>(entity.Status),
            entity.IsStale, entity.StaleAt, entity.Message, entity.TriggeredAt,
            entity.AcknowledgedAt, entity.AcknowledgedBy,
            entity.SuppressedAt, entity.SuppressedBy, entity.SuppressedReason,
            entity.ClearedAt, entity.ClearValue, entity.CreatedAt);
    }
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
