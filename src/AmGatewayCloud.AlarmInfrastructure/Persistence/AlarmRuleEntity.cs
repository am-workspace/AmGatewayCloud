using System.ComponentModel.DataAnnotations.Schema;

namespace AmGatewayCloud.AlarmInfrastructure.Persistence;

/// <summary>
/// alarm_rules 表的 EF Core 实体映射（与现有数据库 schema 一一对应）
/// </summary>
[Table("alarm_rules")]
public class AlarmRuleEntity
{
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("tenant_id")]
    public string TenantId { get; set; } = "default";

    [Column("factory_id")]
    public string? FactoryId { get; set; }

    [Column("device_id")]
    public string? DeviceId { get; set; }

    [Column("tag")]
    public string Tag { get; set; } = string.Empty;

    [Column("operator")]
    public string Operator { get; set; } = ">";

    [Column("threshold")]
    public double Threshold { get; set; }

    [Column("threshold_string")]
    public string? ThresholdString { get; set; }

    [Column("clear_threshold")]
    public double? ClearThreshold { get; set; }

    [Column("level")]
    public string Level { get; set; } = "Warning";

    [Column("cooldown_minutes")]
    public int CooldownMinutes { get; set; } = 5;

    [Column("delay_seconds")]
    public int DelaySeconds { get; set; } = 0;

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    [Column("description")]
    public string? Description { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }
}
