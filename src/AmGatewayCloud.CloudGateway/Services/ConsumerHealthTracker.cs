using System.Collections.Concurrent;

namespace AmGatewayCloud.CloudGateway.Services;

public class ConsumerHealthTracker
{
    private readonly ConcurrentDictionary<string, ConsumerHealth> _health = new();

    public void SetOnline(string factoryId)
    {
        var health = _health.GetOrAdd(factoryId, _ => new ConsumerHealth { FactoryId = factoryId });
        health.IsOnline = true;
        health.LastUpdated = DateTimeOffset.UtcNow;
    }

    public void SetOffline(string factoryId)
    {
        var health = _health.GetOrAdd(factoryId, _ => new ConsumerHealth { FactoryId = factoryId });
        health.IsOnline = false;
        health.LastUpdated = DateTimeOffset.UtcNow;
    }

    public void RecordSuccess(string factoryId)
    {
        var health = _health.GetOrAdd(factoryId, _ => new ConsumerHealth { FactoryId = factoryId });
        Interlocked.Increment(ref health._totalProcessed);
        health.LastMessageAt = DateTimeOffset.UtcNow;
        health.LastUpdated = DateTimeOffset.UtcNow;
    }

    public void RecordFailure(string factoryId)
    {
        var health = _health.GetOrAdd(factoryId, _ => new ConsumerHealth { FactoryId = factoryId });
        Interlocked.Increment(ref health._totalFailures);
        health.LastUpdated = DateTimeOffset.UtcNow;
    }

    public IReadOnlyDictionary<string, ConsumerHealth> GetSnapshot()
    {
        return _health.ToDictionary(kv => kv.Key, kv =>
        {
            var h = kv.Value;
            return new ConsumerHealth
            {
                FactoryId = h.FactoryId,
                IsOnline = h.IsOnline,
                Lag = h.LastMessageAt.HasValue ? DateTimeOffset.UtcNow - h.LastMessageAt.Value : null,
                TotalProcessed = h.TotalProcessed,
                TotalFailures = h.TotalFailures,
                LastUpdated = h.LastUpdated
            };
        });
    }
}

public class ConsumerHealth
{
    public string FactoryId { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public TimeSpan? Lag { get; set; }
    public long TotalProcessed { get; set; }
    public long TotalFailures { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public DateTimeOffset LastUpdated { get; set; }

    internal long _totalProcessed;
    internal long _totalFailures;
}
