namespace AmGatewayCloud.Shared.DTOs;

/// <summary>
/// 分配工单请求
/// </summary>
public class AssignWorkOrderRequest
{
    public string Assignee { get; set; } = string.Empty;
}

/// <summary>
/// 完成工单请求
/// </summary>
public class CompleteWorkOrderRequest
{
    public string CompletedBy { get; set; } = string.Empty;
    public string? CompletionNote { get; set; }
}
