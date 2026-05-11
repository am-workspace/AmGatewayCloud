using System.ComponentModel.DataAnnotations.Schema;

namespace AmGatewayCloud.AlarmInfrastructure.Persistence;

/// <summary>
/// alarm_events 表的 EF Core 实体映射（与现有数据库 schema 一一对应）
/// </summary>
[Table("alarm_events")]
public class AlarmEventEntity
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("rule_id")]
    public string RuleId { get; set; } = string.Empty;

    [Column("tenant_id")]
    public string TenantId { get; set; } = string.Empty;

    [Column("factory_id")]
    public string FactoryId { get; set; } = string.Empty;

    [Column("workshop_id")]
    public string? WorkshopId { get; set; }

    [Column("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [Column("tag")]
    public string Tag { get; set; } = string.Empty;

    [Column("trigger_value")]
    public double? TriggerValue { get; set; }

    [Column("level")]
    public string Level { get; set; } = "Warning";

    [Column("status")]
    public string Status { get; set; } = "Active";

    [Column("is_stale")]
    public bool IsStale { get; set; }

    [Column("stale_at")]
    public DateTimeOffset? StaleAt { get; set; }

    [Column("message")]
    public string? Message { get; set; }

    [Column("triggered_at")]
    public DateTimeOffset TriggeredAt { get; set; }

    [Column("acknowledged_at")]
    public DateTimeOffset? AcknowledgedAt { get; set; }

    [Column("acknowledged_by")]
    public string? AcknowledgedBy { get; set; }

    [Column("suppressed_at")]
    public DateTimeOffset? SuppressedAt { get; set; }

    [Column("suppressed_by")]
    public string? SuppressedBy { get; set; }

    [Column("suppressed_reason")]
    public string? SuppressedReason { get; set; }

    [Column("cleared_at")]
    public DateTimeOffset? ClearedAt { get; set; }

    [Column("clear_value")]
    public double? ClearValue { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    // 导航属性：关联规则
    public AlarmRuleEntity? Rule { get; set; }
}
