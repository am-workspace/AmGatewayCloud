using System.Collections.Concurrent;
using AmGatewayCloud.CloudGateway.Configuration;
using AmGatewayCloud.CloudGateway.Infrastructure;
using AmGatewayCloud.CloudGateway.Models;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AmGatewayCloud.CloudGateway.Services;

public class PostgreSqlDeviceStore
{
    private readonly NpgsqlConnectionFactory _connectionFactory;
    private readonly ILogger<PostgreSqlDeviceStore> _logger;
    private readonly ConcurrentDictionary<string, HashSet<string>> _knownTagsCache = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeenCache = new();
    private readonly TimeSpan _lastSeenThrottle = TimeSpan.FromSeconds(30);

    public PostgreSqlDeviceStore(
        IOptions<CloudGatewayConfig> options,
        ILogger<PostgreSqlDeviceStore> logger)
    {
        _connectionFactory = new NpgsqlConnectionFactory(options, options.Value.PostgreSql.Database);
        _logger = logger;
    }

    public async Task EnsureDeviceAsync(DataBatch batch, CancellationToken ct = default)
    {
        var deviceKey = $"{batch.FactoryId}:{batch.WorkshopId}:{batch.DeviceId}";
        var currentTags = batch.Points.Select(p => p.Tag).ToHashSet();

        await EnsureFactoryAsync(batch.FactoryId, batch.TenantId ?? "default", ct);
        await EnsureWorkshopAsync(batch.WorkshopId, batch.FactoryId, ct);

        var hasNewTag = false;
        var known = _knownTagsCache.GetOrAdd(deviceKey, _ => new HashSet<string>());
        lock (known)
        {
            foreach (var tag in currentTags)
            {
                if (known.Add(tag))
                {
                    hasNewTag = true;
                }
            }
        }

        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        // 设备存在性检查 + 首次注册
        const string upsertDeviceSql = @"
            INSERT INTO devices (id, name, factory_id, workshop_id, protocol, tenant_id, first_seen_at, last_seen_at, tags, created_at, updated_at)
            VALUES (@id, @name, @factory_id, @workshop_id, @protocol, @tenant_id, NOW(), NOW(), @tags, NOW(), NOW())
            ON CONFLICT (id) DO UPDATE SET
                last_seen_at = EXCLUDED.last_seen_at,
                tags = CASE WHEN @update_tags THEN EXCLUDED.tags ELSE devices.tags END,
                updated_at = NOW();
        ";

        await conn.ExecuteAsync(upsertDeviceSql, new
        {
            id = batch.DeviceId,
            name = batch.DeviceId,
            factory_id = batch.FactoryId,
            workshop_id = batch.WorkshopId,
            protocol = batch.Protocol,
            tenant_id = batch.TenantId ?? "default",
            tags = hasNewTag ? currentTags.ToArray() : null,
            update_tags = hasNewTag
        });

        if (hasNewTag)
        {
            _logger.LogDebug("[Device:{DeviceId}] New tags detected, updated database", batch.DeviceId);
        }
    }

    public async Task UpdateLastSeenAsync(string deviceId, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        // 30秒窗口降频
        if (_lastSeenCache.TryGetValue(deviceId, out var lastUpdate))
        {
            if (timestamp - lastUpdate < _lastSeenThrottle)
            {
                return;
            }
        }

        _lastSeenCache[deviceId] = timestamp;

        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync(@"
            UPDATE devices SET last_seen_at = @timestamp, updated_at = NOW()
            WHERE id = @deviceId;
        ", new { deviceId, timestamp });
    }

    public async Task<List<Device>> GetDevicesByFactoryAsync(string factoryId, CancellationToken ct = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        var devices = await conn.QueryAsync<Device>(@"
            SELECT id, name, factory_id, workshop_id, protocol, tenant_id, first_seen_at, last_seen_at, tags, created_at, updated_at
            FROM devices
            WHERE factory_id = @factoryId;
        ", new { factoryId });

        return devices.ToList();
    }

    private async Task EnsureFactoryAsync(string factoryId, string tenantId, CancellationToken ct)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync(@"
            INSERT INTO factories (id, name, tenant_id, created_at)
            VALUES (@id, @name, @tenant_id, NOW())
            ON CONFLICT (id) DO NOTHING;
        ", new { id = factoryId, name = factoryId, tenant_id = tenantId });
    }

    private async Task EnsureWorkshopAsync(string workshopId, string factoryId, CancellationToken ct)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync(@"
            INSERT INTO workshops (id, name, factory_id, created_at)
            VALUES (@id, @name, @factory_id, NOW())
            ON CONFLICT (id) DO NOTHING;
        ", new { id = workshopId, name = workshopId, factory_id = factoryId });
    }
}

public class Device
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string FactoryId { get; set; } = string.Empty;
    public string WorkshopId { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public DateTimeOffset? FirstSeenAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public string[]? Tags { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
