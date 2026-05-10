namespace AmGatewayCloud.AlarmService.Services;

using System.Collections.Concurrent;

/// <summary>
/// 冷却管理器：同一规则+设备在冷却时间内不重复触发报警
/// <para>
/// 报警恢复时重置冷却，允许条件再次满足时立即重新触发。
/// 内存字典实现，进程重启后冷却状态丢失（可接受，最坏情况只是多触发一次报警）。
/// </para>
/// </summary>
public class CooldownManager
{
    // key: "{ruleId}:{deviceId}", value: 最后触发时间
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new();

    /// <summary>
    /// 检查指定规则+设备是否处于冷却期内
    /// </summary>
    /// <param name="ruleId">报警规则ID</param>
    /// <param name="deviceId">设备ID</param>
    /// <param name="cooldownMinutes">冷却时间（分钟）</param>
    /// <returns>true 表示仍在冷却期内，应跳过</returns>
    public bool IsInCooldown(string ruleId, string deviceId, int cooldownMinutes)
    {
        var key = $"{ruleId}:{deviceId}";
        if (_cooldowns.TryGetValue(key, out var lastTrigger))
        {
            return lastTrigger > DateTimeOffset.UtcNow.AddMinutes(-cooldownMinutes);
        }
        return false;
    }

    /// <summary>
    /// 记录一次报警触发，开始新的冷却周期
    /// </summary>
    public void RecordTrigger(string ruleId, string deviceId)
    {
        _cooldowns[$"{ruleId}:{deviceId}"] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 报警恢复时重置冷却，允许条件再次满足时立即重新触发
    /// </summary>
    public void ResetCooldown(string ruleId, string deviceId)
    {
        _cooldowns.TryRemove($"{ruleId}:{deviceId}", out _);
    }

    /// <summary>
    /// 定期清理过期的冷却记录，防止内存泄漏
    /// <para>建议在评估主循环中周期性调用</para>
    /// </summary>
    public void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var kvp in _cooldowns)
        {
            if (kvp.Value < cutoff)
                _cooldowns.TryRemove(kvp.Key, out _);
        }
    }
}
