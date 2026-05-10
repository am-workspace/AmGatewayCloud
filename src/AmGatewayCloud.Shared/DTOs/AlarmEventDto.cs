namespace AmGatewayCloud.Shared.DTOs;

/// <summary>
/// 报警事件 DTO（API 返回）
/// </summary>
public class AlarmEventDto
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
