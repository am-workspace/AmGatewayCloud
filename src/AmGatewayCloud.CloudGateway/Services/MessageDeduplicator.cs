using System.Collections.Concurrent;

namespace AmGatewayCloud.CloudGateway.Services;

public class MessageDeduplicator
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _cache = new();
    private readonly ILogger<MessageDeduplicator> _logger;
    private readonly TimeSpan _ttl = TimeSpan.FromHours(1);
    private readonly int _maxSize = 100_000;

    public MessageDeduplicator(ILogger<MessageDeduplicator> logger)
    {
        _logger = logger;
    }

    public bool IsDuplicate(Guid batchId)
    {
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(batchId, out _))
        {
            _logger.LogDebug("Duplicate BatchId detected: {BatchId}", batchId);
            return true;
        }

        // 尝试添加
        if (_cache.TryAdd(batchId, now))
        {
            // 超过上限时清理最旧的 20%
            if (_cache.Count > _maxSize)
            {
                Cleanup();
            }
            return false;
        }

        // 并发添加冲突，视为重复
        return true;
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - _ttl;
        var toRemove = _cache
            .Where(kv => kv.Value < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _cache.TryRemove(key, out _);
        }

        // 如果仍然超过上限，按时间排序移除最旧的
        if (_cache.Count > _maxSize)
        {
            var oldest = _cache
                .OrderBy(kv => kv.Value)
                .Take(_cache.Count - (int)(_maxSize * 0.8))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in oldest)
            {
                _cache.TryRemove(key, out _);
            }
        }

        _logger.LogInformation("Deduplication cache cleaned. Current size: {Count}", _cache.Count);
    }
}
