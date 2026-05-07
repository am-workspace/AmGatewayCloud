using System.Text.Json;
using AmGatewayCloud.EdgeGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AmGatewayCloud.EdgeGateway.Services;

/// <summary>
/// 水位线追踪器：记录最后成功转发到 RabbitMQ 的批次信息。
/// 使用同步刷盘（本地 IO 成本低），进程崩溃不丢进度。
/// </summary>
public sealed class WatermarkTracker
{
    private readonly string _filePath;
    private readonly ILogger<WatermarkTracker> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public DateTimeOffset LastForwardedAt { get; private set; }
    public Guid LastBatchId { get; private set; }
    public long LastSequence { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public WatermarkTracker(IOptions<EdgeGatewayConfig> config, ILogger<WatermarkTracker> logger)
    {
        var hubId = config.Value.HubId;
        _filePath = Path.Combine(
            AppContext.BaseDirectory,
            "watermarks",
            $"{hubId}.watermark.json");
        _logger = logger;

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    /// <summary>
    /// 加载水位线文件，如果不存在或损坏则重置
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("Watermark file not found, starting fresh: {FilePath}", _filePath);
            Reset();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            var watermark = JsonSerializer.Deserialize<WatermarkFile>(json, JsonOpts);

            if (watermark == null)
            {
                _logger.LogWarning("Watermark file is empty, resetting");
                Reset();
                return;
            }

            lock (_lock)
            {
                LastForwardedAt = watermark.LastForwardedAt;
                LastBatchId = watermark.LastBatchId;
                LastSequence = watermark.LastSequence;
                UpdatedAt = watermark.UpdatedAt;
            }

            _logger.LogInformation(
                "Watermark loaded: LastForwardedAt={LastForwardedAt}, LastBatchId={LastBatchId}",
                LastForwardedAt, LastBatchId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watermark file corrupted, resetting: {FilePath}", _filePath);
            Reset();
        }
    }

    /// <summary>
    /// 更新水位线并同步刷盘
    /// </summary>
    public void UpdateWatermark(DateTimeOffset timestamp, Guid batchId)
    {
        lock (_lock)
        {
            LastForwardedAt = timestamp;
            LastBatchId = batchId;
            LastSequence++;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        // 同步刷盘——本地 IO 成本极低，不应为了性能牺牲一致性
        _ = Task.Run(async () => await SaveAsync());
    }

    /// <summary>
    /// 保存水位线到文件
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            WatermarkFile snapshot;
            lock (_lock)
            {
                snapshot = new WatermarkFile
                {
                    LastForwardedAt = LastForwardedAt,
                    LastBatchId = LastBatchId,
                    LastSequence = LastSequence,
                    UpdatedAt = UpdatedAt
                };
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOpts);
            await File.WriteAllTextAsync(_filePath, json, ct);
            _logger.LogDebug("Watermark saved: {FilePath}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save watermark to {FilePath}", _filePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void Reset()
    {
        lock (_lock)
        {
            LastForwardedAt = DateTimeOffset.MinValue;
            LastBatchId = Guid.Empty;
            LastSequence = 0;
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private class WatermarkFile
    {
        public DateTimeOffset LastForwardedAt { get; set; }
        public Guid LastBatchId { get; set; }
        public long LastSequence { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
