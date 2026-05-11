namespace AmGatewayCloud.Shared.DTOs;

/// <summary>
/// 维修工单 DTO
/// </summary>
public class WorkOrderDto
{
    public Guid Id { get; set; }
    public Guid AlarmId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string FactoryId { get; set; } = string.Empty;
    public string? WorkshopId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Level { get; set; } = "Warning";
    public string Status { get; set; } = "Pending";
    public string? Assignee { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public string? CompletionNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
