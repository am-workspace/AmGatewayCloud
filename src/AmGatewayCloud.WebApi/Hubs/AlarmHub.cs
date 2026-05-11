using AmGatewayCloud.Shared.Tenant;
using Microsoft.AspNetCore.SignalR;

namespace AmGatewayCloud.WebApi.Hubs;

/// <summary>
/// SignalR Hub — 向前端实时推送报警事件
/// 按租户+工厂分组，确保租户隔离
/// </summary>
public class AlarmHub : Hub
{
    private readonly ILogger<AlarmHub> _logger;

    public AlarmHub(ILogger<AlarmHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 加入工厂分组，只接收该租户+工厂的报警
    /// </summary>
    public async Task JoinFactory(string factoryId)
    {
        var tenantId = GetTenantId();
        var groupName = GetGroupName(tenantId, factoryId);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogDebug("Connection {ConnId} joined group {Group}", Context.ConnectionId, groupName);
    }

    /// <summary>
    /// 离开工厂分组
    /// </summary>
    public async Task LeaveFactory(string factoryId)
    {
        var tenantId = GetTenantId();
        var groupName = GetGroupName(tenantId, factoryId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogDebug("Connection {ConnId} left group {Group}", Context.ConnectionId, groupName);
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogDebug("SignalR client connected: {ConnId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("SignalR client disconnected: {ConnId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// 从 JWT 或查询参数获取租户 ID
    /// </summary>
    private string GetTenantId()
    {
        var claim = Context.User?.FindFirst("tenant_id");
        if (claim is not null) return claim.Value;

        // SignalR 连接时可能通过查询参数传递
        var queryTenantId = Context.GetHttpContext()?.Request.Query["tenant_id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(queryTenantId)) return queryTenantId;

        return "default";
    }

    /// <summary>
    /// 生成租户+工厂维度的分组名
    /// </summary>
    public static string GetGroupName(string tenantId, string factoryId) =>
        $"tenant-{tenantId}_factory-{factoryId}";
}
