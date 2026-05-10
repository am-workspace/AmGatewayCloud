namespace AmGatewayCloud.WebApi.Hubs;

using Microsoft.AspNetCore.SignalR;

/// <summary>
/// SignalR Hub — 向前端实时推送报警事件
/// </summary>
public class AlarmHub : Hub
{
    private readonly ILogger<AlarmHub> _logger;

    public AlarmHub(ILogger<AlarmHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 加入工厂分组，只接收该工厂的报警
    /// </summary>
    public async Task JoinFactory(string factoryId)
    {
        var groupName = $"factory-{factoryId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogDebug("Connection {ConnId} joined group {Group}", Context.ConnectionId, groupName);
    }

    /// <summary>
    /// 离开工厂分组
    /// </summary>
    public async Task LeaveFactory(string factoryId)
    {
        var groupName = $"factory-{factoryId}";
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
}
