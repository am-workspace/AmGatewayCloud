namespace AmGatewayCloud.AlarmService.Services;

using System.Collections.Concurrent;

/// <summary>
/// 延时追踪器：记录规则+设备首次满足报警条件的时间，
/// 仅当条件持续满足超过 DelaySeconds 后才允许触发报警。
/// <para>
/// 内存字典实现，进程重启后状态丢失（可接受，最坏情况只是多等一个延时周期）。
/// </para>
/// </summary>
public class DelayTracker
{
    // key: "{ruleId}:{deviceId}", value: 首次满足条件的时间
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingDelays = new();

    /// <summary>
    /// 检查是否已通过延时窗口：如果规则有延时要求，则条件需持续满足 DelaySeconds 才算通过
    /// </summary>
    /// <param name="ruleId">报警规则ID</param>
    /// <param name="deviceId">设备ID</param>
    /// <param name="delaySeconds">延时秒数</param>
    /// <returns>true 表示已通过延时窗口（或无延时要求），可以触发报警</returns>
    public bool IsDelayElapsed(string ruleId, string deviceId, int delaySeconds)
    {
        if (delaySeconds <= 0) return true;

        var key = $"{ruleId}:{deviceId}";
        if (_pendingDelays.TryGetValue(key, out var firstMetTime))
        {
            return (DateTimeOffset.UtcNow - firstMetTime).TotalSeconds >= delaySeconds;
        }

        // 首次满足条件，记录时间但尚未通过延时
        _pendingDelays[key] = DateTimeOffset.UtcNow;
        return false;
    }

    /// <summary>
    /// 条件不再满足时，清除延时记录（下次重新计时）
    /// </summary>
    public void ClearDelay(string ruleId, string deviceId)
    {
        _pendingDelays.TryRemove($"{ruleId}:{deviceId}", out _);
    }

    /// <summary>
    /// 报警触发成功后，清除延时记录
    /// </summary>
    public void RecordTriggered(string ruleId, string deviceId)
    {
        _pendingDelays.TryRemove($"{ruleId}:{deviceId}", out _);
    }

    /// <summary>
    /// 定期清理过期的延时记录，防止内存泄漏
    /// </summary>
    public void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var kvp in _pendingDelays)
        {
            if (kvp.Value < cutoff)
                _pendingDelays.TryRemove(kvp.Key, out _);
        }
    }
}
