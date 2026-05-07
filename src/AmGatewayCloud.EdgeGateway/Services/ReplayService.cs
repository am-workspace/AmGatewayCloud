using System.Text;
using System.Text.Json;
using AmGatewayCloud.EdgeGateway.Configuration;
using AmGatewayCloud.EdgeGateway.Models;
using Microsoft.Extensions.Options;

namespace AmGatewayCloud.EdgeGateway.Services;

/// <summary>
/// 断网恢复回放服务：从 InfluxDB 读取未转发数据，补发到 RabbitMQ。
/// 支持令牌桶限速、中断保护、断点续传。
/// </summary>
public sealed class ReplayService
{
    private readonly InfluxDbConfig _config;
    private readonly ILogger<ReplayService> _logger;
    private readonly HttpClient _httpClient;

    // 令牌桶限速：每秒 100 batch
    private readonly SemaphoreSlim _rateLimiter = new(100, 100);

    public ReplayService(IOptions<EdgeGatewayConfig> config, ILogger<ReplayService> logger)
    {
        _config = config.Value.InfluxDb;
        _logger = logger;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Token {_config.Token}");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// 回放指定时间范围内的未转发数据
    /// </summary>
    public async Task ReplayAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        WatermarkTracker watermarkTracker,
        RabbitMqForwarder rabbitForwarder,
        CancellationToken ct = default)
    {
        if (from >= to)
        {
            _logger.LogInformation("Replay range empty: {From:O} >= {To:O}", from, to);
            return;
        }

        _logger.LogInformation("Starting replay: {From:O} → {To:O}", from, to);

        var totalBatches = 0;
        var successBatches = 0;
        var currentFrom = from;
        const int pageSize = 100;

        try
        {
            while (currentFrom < to)
            {
                // 中断保护：RabbitMQ 再次断开 → 暂停并持久化进度
                if (!rabbitForwarder.IsOnline)
                {
                    _logger.LogWarning("RabbitMQ disconnected during replay, pausing at {Position}", currentFrom);
                    await watermarkTracker.SaveAsync(ct);
                    return;
                }

                var batches = await ReadBatchesAsync(currentFrom, to, pageSize, watermarkTracker.LastBatchId, ct);
                if (batches.Count == 0)
                    break;

                foreach (var batch in batches)
                {
                    await ThrottleAsync(ct);

                    // 再次检查连接状态
                    if (!rabbitForwarder.IsOnline)
                    {
                        _logger.LogWarning("RabbitMQ disconnected during replay, pausing at batch {BatchId}", batch.BatchId);
                        await watermarkTracker.SaveAsync(ct);
                        return;
                    }

                    var ok = await rabbitForwarder.ForwardAsync(batch, watermarkTracker, ct);
                    if (ok)
                    {
                        successBatches++;
                    }
                }

                totalBatches += batches.Count;
                currentFrom = batches.Last().Timestamp;

                _logger.LogInformation("Replay progress: {Success}/{Total} batches, position={Position:O}",
                    successBatches, totalBatches, currentFrom);
            }

            _logger.LogInformation("Replay completed: {From:O} → {To:O}, {Success}/{Total} batches forwarded",
                from, to, successBatches, totalBatches);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Replay cancelled at {Position:O}", currentFrom);
            await watermarkTracker.SaveAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// 从 InfluxDB 读取指定时间范围内的 DataBatch
    /// </summary>
    private async Task<List<DataBatch>> ReadBatchesAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        Guid excludeBatchId,
        CancellationToken ct)
    {
        var queryUrl = $"{_config.Url.TrimEnd('/')}/api/v2/query?org={Uri.EscapeDataString(_config.Org)}";

        // Flux 查询：读取 device_data measurement，按 batch_id 分组聚合
        var fluxQuery = $@"
from(bucket: """"{_config.Bucket}"""")
  |> range(start: {from:O}, stop: {to:O})
  |> filter(fn: (r) => r._measurement == """"device_data"""")
  |> filter(fn: (r) => r.batch_id != """"{excludeBatchId}"""")
  |> group(columns: [""""batch_id""""])
  |> sort(columns: [""""_time""""])
  |> limit(n: {limit})
";

        try
        {
            var content = new StringContent(fluxQuery, Encoding.UTF8, "application/vnd.flux");
            var response = await _httpClient.PostAsync(queryUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("InfluxDB query failed: {StatusCode} - {Error}", (int)response.StatusCode, error);
                return [];
            }

            // TODO: 解析 CSV 响应为 DataBatch 列表
            // InfluxDB 2.x 查询返回 CSV 格式，需要解析
            // 简化实现：返回空列表，后续完善解析逻辑
            _logger.LogWarning("InfluxDB query response parsing not yet implemented");
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InfluxDB query exception");
            return [];
        }
    }

    /// <summary>
    /// 令牌桶限速：控制回放速率
    /// </summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000, ct);
                _rateLimiter.Release();
            }
            catch (OperationCanceledException)
            {
                // 取消时释放令牌，避免死锁
                _rateLimiter.Release();
            }
        }, ct);
    }
}
