using AmGatewayCloud.AlarmDomain.Common;
using AmGatewayCloud.AlarmDomain.Events;

namespace AmGatewayCloud.AlarmDomain.Aggregates.Alarm;

/// <summary>
/// Alarm 聚合根：报警事件的领域模型，封装状态流转规则和领域事件发布
/// </summary>
public class Alarm
{
    public Guid Id { get; private set; }
    public string RuleId { get; private set; }
    public string TenantId { get; private set; }
    public string FactoryId { get; private set; }
    public string? WorkshopId { get; private set; }
    public string DeviceId { get; private set; }
    public string Tag { get; private set; }
    public double? TriggerValue { get; private set; }
    public AlarmLevel Level { get; private set; }
    public AlarmStatus Status { get; private set; }
    public bool IsStale { get; private set; }
    public DateTimeOffset? StaleAt { get; private set; }
    public string? Message { get; private set; }
    public DateTimeOffset TriggeredAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public string? AcknowledgedBy { get; private set; }
    public DateTimeOffset? SuppressedAt { get; private set; }
    public string? SuppressedBy { get; private set; }
    public string? SuppressedReason { get; private set; }
    public DateTimeOffset? ClearedAt { get; private set; }
    public double? ClearValue { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<DomainEvent> _domainEvents = [];
    public IReadOnlyList<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    // EF Core 需要无参构造函数
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor.
    private Alarm() { }
#pragma warning restore CS8618

    /// <summary>
    /// 工厂方法：创建新报警（Active 状态），发布 AlarmTriggeredEvent
    /// </summary>
    public static Alarm Create(
        string ruleId, string tenantId, string factoryId, string? workshopId,
        string deviceId, string tag, double? triggerValue, AlarmLevel level,
        string? message, DateTimeOffset triggeredAt)
    {
        var alarm = new Alarm
        {
            Id = Guid.NewGuid(),
            RuleId = ruleId,
            TenantId = tenantId,
            FactoryId = factoryId,
            WorkshopId = workshopId,
            DeviceId = deviceId,
            Tag = tag,
            TriggerValue = triggerValue,
            Level = level,
            Status = AlarmStatus.Active,
            IsStale = false,
            Message = message,
            TriggeredAt = triggeredAt,
            CreatedAt = DateTimeOffset.UtcNow
        };

        alarm._domainEvents.Add(new AlarmTriggeredEvent(
            alarm.Id, alarm.TenantId, alarm.FactoryId, alarm.DeviceId, alarm.Level));

        return alarm;
    }

    /// <summary>
    /// 确认报警：Active → Acked
    /// </summary>
    public void Acknowledge(string acknowledgedBy)
    {
        if (Status != AlarmStatus.Active)
            throw new AlarmStateException(Status.ToString(), "Acknowledge",
                $"Cannot acknowledge alarm in {Status} status. Only Active alarms can be acknowledged.");

        Status = AlarmStatus.Acked;
        AcknowledgedAt = DateTimeOffset.UtcNow;
        AcknowledgedBy = acknowledgedBy;
    }

    /// <summary>
    /// 抑制报警：Active/Acked → Suppressed
    /// </summary>
    public void Suppress(string suppressedBy, string? reason)
    {
        if (Status is not (AlarmStatus.Active or AlarmStatus.Acked))
            throw new AlarmStateException(Status.ToString(), "Suppress",
                $"Cannot suppress alarm in {Status} status. Only Active/Acked alarms can be suppressed.");

        Status = AlarmStatus.Suppressed;
        SuppressedAt = DateTimeOffset.UtcNow;
        SuppressedBy = suppressedBy;
        SuppressedReason = reason;
    }

    /// <summary>
    /// 自动恢复报警：→ Cleared，发布 AlarmClearedEvent
    /// </summary>
    public void AutoClear(double? clearValue)
    {
        if (Status is AlarmStatus.Cleared)
            return; // 已恢复，幂等

        Status = AlarmStatus.Cleared;
        ClearedAt = DateTimeOffset.UtcNow;
        ClearValue = clearValue;

        _domainEvents.Add(new AlarmClearedEvent(
            Id, TenantId, FactoryId, DeviceId, clearValue));
    }

    /// <summary>
    /// 手动关闭报警：→ Cleared
    /// </summary>
    public void ManualClear()
    {
        if (Status is AlarmStatus.Cleared)
            throw new AlarmStateException(Status.ToString(), "ManualClear",
                $"Cannot clear alarm in {Status} status. Alarm is already cleared.");

        Status = AlarmStatus.Cleared;
        ClearedAt = DateTimeOffset.UtcNow;

        _domainEvents.Add(new AlarmClearedEvent(
            Id, TenantId, FactoryId, DeviceId, null));
    }

    /// <summary>
    /// 标记设备离线
    /// </summary>
    public void MarkStale()
    {
        if (!IsStale)
        {
            IsStale = true;
            StaleAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// 清除设备离线标记（设备恢复在线）
    /// </summary>
    public void ClearStale()
    {
        IsStale = false;
        StaleAt = null;
    }

    /// <summary>
    /// 清除领域事件（发布后调用）
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>
    /// 获取级别字符串表示（兼容现有字符串存储）
    /// </summary>
    public string LevelString => Level.ToString();

    /// <summary>
    /// 获取状态字符串表示（兼容现有字符串存储）
    /// </summary>
    public string StatusString => Status.ToString();

    // ── 用于从数据库重建聚合根的静态方法 ──

    /// <summary>
    /// 从持久化数据重建聚合根（不发布领域事件）
    /// </summary>
    public static Alarm Reconstruct(
        Guid id, string ruleId, string tenantId, string factoryId, string? workshopId,
        string deviceId, string tag, double? triggerValue, AlarmLevel level, AlarmStatus status,
        bool isStale, DateTimeOffset? staleAt, string? message, DateTimeOffset triggeredAt,
        DateTimeOffset? acknowledgedAt, string? acknowledgedBy,
        DateTimeOffset? suppressedAt, string? suppressedBy, string? suppressedReason,
        DateTimeOffset? clearedAt, double? clearValue, DateTimeOffset createdAt)
    {
        return new Alarm
        {
            Id = id,
            RuleId = ruleId,
            TenantId = tenantId,
            FactoryId = factoryId,
            WorkshopId = workshopId,
            DeviceId = deviceId,
            Tag = tag,
            TriggerValue = triggerValue,
            Level = level,
            Status = status,
            IsStale = isStale,
            StaleAt = staleAt,
            Message = message,
            TriggeredAt = triggeredAt,
            AcknowledgedAt = acknowledgedAt,
            AcknowledgedBy = acknowledgedBy,
            SuppressedAt = suppressedAt,
            SuppressedBy = suppressedBy,
            SuppressedReason = suppressedReason,
            ClearedAt = clearedAt,
            ClearValue = clearValue,
            CreatedAt = createdAt
        };
    }
}
