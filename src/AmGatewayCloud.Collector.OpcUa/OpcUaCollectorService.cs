using System.Collections.Concurrent;
using AmGatewayCloud.Collector.OpcUa.Configuration;
using AmGatewayCloud.Collector.OpcUa.Models;
using AmGatewayCloud.Collector.OpcUa.Output;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;

namespace AmGatewayCloud.Collector.OpcUa;

/// <summary>
/// OPC UA 采集主服务：BackgroundService 托管。
/// 创建订阅 + 监控项，接收通知回调，转换为 DataPoint 批量输出。
/// 断线后 OpcUaSession 自动重连，重连成功触发 SessionRestored 重建订阅。
/// </summary>
public class OpcUaCollectorService : BackgroundService
{
    private readonly OpcUaSession _session;
    private readonly CollectorConfig _config;
    private readonly IEnumerable<IDataOutput> _outputs;
    private readonly ILogger<OpcUaCollectorService> _logger;

    private Subscription? _subscription;
    private readonly ConcurrentQueue<DataPoint> _pendingPoints = new();
    private Timer? _flushTimer;

    /// <summary>
    /// 初始化 OpcUaCollectorService。
    /// </summary>
    /// <param name="session">OPC UA 会话管理实例</param>
    /// <param name="config">采集器配置（绑定 appsettings.json Collector 节）</param>
    /// <param name="outputs">数据输出通道列表</param>
    /// <param name="logger">日志记录器</param>
    public OpcUaCollectorService(
        OpcUaSession session,
        IOptions<CollectorConfig> config,
        IEnumerable<IDataOutput> outputs,
        ILogger<OpcUaCollectorService> logger)
    {
        _session = session;
        _config = config.Value;
        _outputs = outputs;
        _logger = logger;
    }

    /// <summary>
    /// 后台服务入口：连接服务器、创建订阅与监控项、启动定时刷出，
    /// 收到取消信号后优雅关机（刷出剩余数据→删订阅→断开连接）。
    /// </summary>
    /// <param name="ct">宿主取消令牌</param>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "OPC UA Collector starting - Device: {DeviceId}, Endpoint: {Endpoint}",
            _config.DeviceId, _config.OpcUa.Endpoint);
        _logger.LogInformation(
            "Monitoring {GroupCount} node groups (PublishingInterval: {Interval}ms)",
            _config.NodeGroups.Count, _config.OpcUa.PublishingIntervalMs);

        // 1. 连接
        await _session.ConnectAsync(ct);

        // 2. 创建订阅
        await CreateSubscriptionAsync(ct);

        // 3. 注册重连事件
        _session.SessionRestored += OnSessionRestored;

        // 4. 启动定时刷出
        _flushTimer = new Timer(
            FlushPendingPoints, null,
            TimeSpan.FromMilliseconds(_config.OpcUa.FlushIntervalMs),
            TimeSpan.FromMilliseconds(_config.OpcUa.FlushIntervalMs));

        // 5. 等待取消
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }

        // --- 优雅关机 ---

        _flushTimer?.Dispose();
        _flushTimer = null;

        // 刷出剩余数据
        FlushPendingPoints(null);

        // 先删订阅（在 Session 存活时）
        await DeleteSubscriptionAsync();

        // 再断开 Session
        _session.SessionRestored -= OnSessionRestored;
        await _session.DisconnectAsync();

        _logger.LogInformation("OPC UA Collector stopped - Device: {DeviceId}", _config.DeviceId);
    }

    // --- 订阅管理 ---

    /// <summary>
    /// 创建 OPC UA 订阅：遍历所有 NodeGroup，为每个节点创建 MonitoredItem
    /// （含死区过滤），注册通知回调，提交到服务器。
    /// 先删除旧订阅再创建新订阅，确保幂等。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    private async Task CreateSubscriptionAsync(CancellationToken ct = default)
    {
        var session = _session.GetSession()
            ?? throw new InvalidOperationException("Session not available for creating subscription");

        // 清理旧订阅
        await DeleteSubscriptionAsync();

        // 创建 Subscription
        _subscription = new Subscription(session.DefaultSubscription)
        {
            PublishingInterval = _config.OpcUa.PublishingIntervalMs,
            PublishingEnabled = true,
            KeepAliveCount = 10,
            LifetimeCount = 30,
            MaxNotificationsPerPublish = 0
        };

        // 遍历 NodeGroups，创建 MonitoredItem
        foreach (var group in _config.NodeGroups)
        {
            ushort nsIndex = group.NamespaceUri is not null
                ? _session.GetNamespaceIndex(group.NamespaceUri)
                : (ushort)0;

            foreach (var node in group.Nodes)
            {
                var nodeId = OpcUaSession.ParseNodeId(node.NodeId, nsIndex);

                var item = new MonitoredItem(_subscription.DefaultItem)
                {
                    StartNodeId = nodeId,
                    SamplingInterval = _config.OpcUa.SamplingIntervalMs,
                    QueueSize = _config.OpcUa.QueueSize,
                    DiscardOldest = true,
                    Handle = (Group: group.Name, Node: node)
                };

                // 死区过滤
                if (node.DeadbandPercent > 0)
                {
                    item.Filter = new DataChangeFilter
                    {
                        Trigger = DataChangeTrigger.StatusValue,
                        DeadbandType = (uint)DeadbandType.Percent,
                        DeadbandValue = node.DeadbandPercent
                    };
                }

                item.Notification += OnNotification;
                _subscription.AddItem(item);
            }

            _logger.LogInformation(
                "Created MonitoredItems for group '{GroupName}' (ns={NsIndex}, {NodeCount} nodes)",
                group.Name, nsIndex, group.Nodes.Count);
        }

        // 提交订阅
        session.AddSubscription(_subscription);
        await _subscription.CreateAsync(ct);

        _logger.LogInformation(
            "Subscription created: {ItemCount} monitored items, PublishingInterval={Interval}ms",
            _subscription.MonitoredItemCount, _subscription.PublishingInterval);
    }

    /// <summary>
    /// 删除当前订阅（静默处理异常）。
    /// </summary>
    private async Task DeleteSubscriptionAsync()
    {
        if (_subscription == null) return;

        try
        {
            await _subscription.DeleteAsync(silent: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete subscription");
        }

        _subscription = null;
    }

    // --- 通知回调 ---

    /// <summary>
    /// 监控项通知回调：将 OPC UA MonitoredItemNotification 转换为 DataPoint，
    /// 根据 StatusCode 映射数据质量，写入待刷出队列。
    /// </summary>
    /// <param name="item">触发通知的监控项（Handle 含 Group 和 NodeConfig）</param>
    /// <param name="e">通知事件参数</param>
    private void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        try
        {
            // NotificationValue 是 IEncodeable，需要转型
            if (e.NotificationValue is not MonitoredItemNotification notification)
                return;

            var (groupName, nodeConfig) = ((string Group, NodeConfig Node))item.Handle!;

            var statusCode = notification.Value?.StatusCode ?? Opc.Ua.StatusCodes.Good;
            var quality = MapStatusCode(statusCode);
            var sourceTimestamp = notification.Value?.SourceTimestamp ?? DateTime.UtcNow;
            var variantValue = notification.Value?.Value;

            DataPoint dataPoint;

            if (quality == DataQuality.Good)
            {
                dataPoint = DataPoint.Good(
                    _config.DeviceId, nodeConfig.Tag, variantValue,
                    sourceTimestamp, _config.TenantId, groupName);
            }
            else
            {
                dataPoint = DataPoint.Bad(
                    _config.DeviceId, nodeConfig.Tag,
                    sourceTimestamp, _config.TenantId, groupName);
            }

            // 记录 ServerTimestamp 到扩展属性
            if (notification.Value?.ServerTimestamp != null)
            {
                dataPoint.Properties ??= new Dictionary<string, object>();
                dataPoint.Properties["ServerTimestamp"] = notification.Value.ServerTimestamp;
            }

            _pendingPoints.Enqueue(dataPoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing notification for {NodeId}", item.StartNodeId);
        }
    }

    // --- 定时刷出 ---

    /// <summary>
    /// 定时刷出回调：从并发队列中取出所有待发送数据点，
    /// 按 GroupName 分组后批量输出到各 IDataOutput 通道。
    /// </summary>
    /// <param name="state">Timer 状态对象（未使用）</param>
    private void FlushPendingPoints(object? state)
    {
        var points = new List<DataPoint>();
        while (_pendingPoints.TryDequeue(out var point))
            points.Add(point);

        if (points.Count == 0) return;

        // 按 GroupName 分组后批量输出
        var grouped = points
            .GroupBy(p => p.Properties?.TryGetValue("GroupName", out var gn) == true ? gn.ToString() : null);

        foreach (var group in grouped)
        {
            var groupPoints = group.OrderBy(p => p.Tag).ToList();
            OutputBatchAsync(groupPoints, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// 将一批数据点分发到所有已注册的 IDataOutput 通道。
    /// 单个通道写入失败不影响其他通道。
    /// </summary>
    /// <param name="points">待输出数据点列表</param>
    /// <param name="ct">取消令牌</param>
    private async Task OutputBatchAsync(List<DataPoint> points, CancellationToken ct)
    {
        foreach (var output in _outputs)
        {
            try
            {
                await output.WriteBatchAsync(points, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Output {OutputType} failed", output.GetType().Name);
            }
        }
    }

    // --- 重连事件 ---

    /// <summary>
    /// 会话恢复事件处理：重连成功后重建订阅和监控项。
    /// </summary>
    /// <param name="sender">事件源</param>
    /// <param name="e">事件参数</param>
    private void OnSessionRestored(object? sender, EventArgs e)
    {
        _logger.LogInformation("Session restored, rebuilding subscription");
        try
        {
            CreateSubscriptionAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild subscription after session restore, will retry on next restore");
        }
    }

    // --- 辅助方法 ---

    /// <summary>
    /// 将 OPC UA StatusCode 映射为 DataQuality 枚举。
    /// Good → Good, Uncertain → Uncertain, 其余 → Bad。
    /// </summary>
    /// <param name="statusCode">OPC UA 状态码</param>
    /// <returns>数据质量枚举值</returns>
    private static DataQuality MapStatusCode(StatusCode statusCode)
    {
        if (StatusCode.IsGood(statusCode.Code)) return DataQuality.Good;
        if (StatusCode.IsUncertain(statusCode.Code)) return DataQuality.Uncertain;
        return DataQuality.Bad;
    }
}
