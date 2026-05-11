namespace AmGatewayCloud.WorkOrderService.Models;

/// <summary>
/// 工单状态
/// </summary>
public enum WorkOrderStatus
{
    Pending,
    InProgress,
    Completed
}

/// <summary>
/// 维修工单
/// </summary>
public class WorkOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AlarmId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string FactoryId { get; set; } = string.Empty;
    public string? WorkshopId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Level { get; set; } = "Warning";
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Pending;
    public string? Assignee { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public string? CompletionNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
