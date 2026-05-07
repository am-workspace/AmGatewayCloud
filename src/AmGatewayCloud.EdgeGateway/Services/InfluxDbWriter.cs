using System.Net;
using System.Text;
using System.Text.Json;
using AmGatewayCloud.EdgeGateway.Configuration;
using AmGatewayCloud.EdgeGateway.Models;
using Microsoft.Extensions.Options;

namespace AmGatewayCloud.EdgeGateway.Services;

/// <summary>
/// InfluxDB 本地写入器：使用 Line Protocol 通过 HTTP API 写入。
/// 参照 AmGateway.Publisher.InfluxDB 实现，按 valueType 分字段存储。
/// </summary>
public sealed class InfluxDbWriter : IAsyncDisposable
{
    private readonly InfluxDbConfig _config;
    private readonly ILogger<InfluxDbWriter> _logger;
    private HttpClient? _httpClient;
    private readonly string _writeUrl;

    // 批量缓冲
    private readonly List<string> _batchBuffer = [];
    private readonly object _batchLock = new();
    private Timer? _flushTimer;
    private int _batchCount;

    public InfluxDbWriter(IOptions<EdgeGatewayConfig> config, ILogger<InfluxDbWriter> logger)
    {
        _config = config.Value.InfluxDb;
        _logger = logger;

        _writeUrl = $"{_config.Url.TrimEnd('/')}/api/v2/write" +
                    $"?org={Uri.EscapeDataString(_config.Org)}" +
                    $"&bucket={Uri.EscapeDataString(_config.Bucket)}" +
                    $"&precision=ms";
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Token {_config.Token}");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

        // 验证连接
        try
        {
            var healthUrl = $"{_config.Url.TrimEnd('/')}/health";
            var response = await _httpClient.GetAsync(healthUrl, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("InfluxDB connection verified - {Url}", _config.Url);
            }
            else
            {
                _logger.LogWarning("InfluxDB health check returned {StatusCode}: {Reason}",
                    (int)response.StatusCode, response.ReasonPhrase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InfluxDB connection check failed, will retry on write");
        }

        // 启动定时刷新
        _flushTimer = new Timer(_ => FlushBatchAsync().GetAwaiter().GetResult(),
            null, TimeSpan.FromMilliseconds(_config.FlushIntervalMs), TimeSpan.FromMilliseconds(_config.FlushIntervalMs));

        _logger.LogInformation("InfluxDB writer started (BatchSize: {BatchSize}, FlushInterval: {FlushMs}ms)",
            _config.BatchSize, _config.FlushIntervalMs);
    }

    public async Task WriteBatchAsync(DataBatch batch, CancellationToken ct = default)
    {
        // InfluxDB 磁盘满检测：写入前检查磁盘空间
        if (await IsDiskFullAsync())
        {
            _logger.LogError("InfluxDB disk full, stopping MQTT ACK protection");
            throw new InvalidOperationException("InfluxDB disk full");
        }

        var lines = new List<string>(batch.Points.Count);
        foreach (var point in batch.Points)
        {
            lines.Add(DataPointToLineProtocol(batch, point));
        }

        bool shouldFlush;
        lock (_batchLock)
        {
            _batchBuffer.AddRange(lines);
            shouldFlush = _batchBuffer.Count >= _config.BatchSize;
        }

        if (shouldFlush)
        {
            await FlushBatchAsync();
        }
    }

    /// <summary>
    /// 检查 InfluxDB 数据目录磁盘空间（简化实现：检查固定路径）
    /// </summary>
    private async Task<bool> IsDiskFullAsync()
    {
        try
        {
            // 简化：检查 InfluxDB 数据目录所在驱动器
            var drive = new DriveInfo(Path.GetPathRoot(_config.Url) ?? "C");
            if (drive.IsReady && drive.AvailableFreeSpace < 1024L * 1024 * 100) // < 100MB
            {
                return true;
            }
        }
        catch
        {
            // 无法检测时默认继续
        }
        return false;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        await FlushBatchAsync();
        _logger.LogInformation("InfluxDB writer stopped, total batches: {Count}", _batchCount);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _httpClient?.Dispose();
    }

    /// <summary>
    /// 将缓冲区中的 Line Protocol 数据刷写到 InfluxDB
    /// </summary>
    private async Task FlushBatchAsync()
    {
        List<string> toFlush;
        lock (_batchLock)
        {
            if (_batchBuffer.Count == 0) return;
            toFlush = [.. _batchBuffer];
            _batchBuffer.Clear();
        }

        var payload = string.Join("\n", toFlush);

        try
        {
            if (_httpClient == null)
            {
                _logger.LogWarning("HTTP client not initialized, dropping {Count} lines", toFlush.Count);
                return;
            }

            var content = new StringContent(payload, Encoding.UTF8, "text/plain");
            var response = await _httpClient.PostAsync(_writeUrl, content);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent)
            {
                Interlocked.Increment(ref _batchCount);
                _logger.LogDebug("InfluxDB write success: {Count} lines", toFlush.Count);
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("InfluxDB write failed: {StatusCode} - {Error}",
                    (int)response.StatusCode, errorBody);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "InfluxDB write exception (HTTP connection)");
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("InfluxDB write timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InfluxDB write unknown exception");
        }
    }

    /// <summary>
    /// 将 DataBatch + DataPoint 转换为 InfluxDB Line Protocol
    /// 按 valueType 分字段存储：value_int / value_float / value_bool / value_string
    /// </summary>
    private string DataPointToLineProtocol(DataBatch batch, DataPoint point)
    {
        var sb = new StringBuilder(256);

        // Measurement
        sb.Append("device_data");

        // Tags
        sb.Append(",factory_id=").Append(EscapeTagValue(batch.FactoryId));
        sb.Append(",workshop_id=").Append(EscapeTagValue(batch.WorkshopId));
        sb.Append(",device_id=").Append(EscapeTagValue(batch.DeviceId));
        sb.Append(",protocol=").Append(EscapeTagValue(batch.Protocol));
        sb.Append(",tag=").Append(EscapeTagValue(point.Tag));
        sb.Append(",quality=").Append(EscapeTagValue(point.Quality));
        if (!string.IsNullOrEmpty(point.GroupName))
            sb.Append(",group_name=").Append(EscapeTagValue(point.GroupName));

        // Fields（按类型分字段）
        sb.Append(' ');
        AppendValueField(sb, point);
        sb.Append(",value_type=\"").Append(EscapeFieldValue(point.ValueType)).Append('"');

        // Timestamp（使用 batch 发布时间，毫秒精度）
        var unixMs = batch.Timestamp.ToUnixTimeMilliseconds();
        sb.Append(' ').Append(unixMs);

        return sb.ToString();
    }

    private static void AppendValueField(StringBuilder sb, DataPoint point)
    {
        var valueType = point.ValueType?.ToLowerInvariant() ?? "string";

        switch (valueType)
        {
            case "int":
            case "short":
            case "long":
            case "int32":
            case "int64":
                if (point.Value.ValueKind == JsonValueKind.Number && point.Value.TryGetInt64(out var intVal))
                    sb.Append("value_int=").Append(intVal).Append('i');
                else
                    sb.Append("value_int=0i");
                break;

            case "float":
            case "double":
            case "single":
                if (point.Value.ValueKind == JsonValueKind.Number && point.Value.TryGetDouble(out var doubleVal))
                    sb.Append("value_float=").Append(doubleVal.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
                else
                    sb.Append("value_float=0.0");
                break;

            case "bool":
            case "boolean":
                if (point.Value.ValueKind == JsonValueKind.True || point.Value.ValueKind == JsonValueKind.False)
                    sb.Append("value_bool=").Append(point.Value.GetBoolean().ToString().ToLowerInvariant());
                else
                    sb.Append("value_bool=false");
                break;

            default:
                sb.Append("value_string=\"").Append(EscapeFieldValue(point.Value.ToString() ?? "")).Append('"');
                break;
        }
    }

    private static string EscapeTagValue(string value) =>
        value.Replace(" ", "\\ ")
             .Replace(",", "\\,")
             .Replace("=", "\\=");

    private static string EscapeFieldValue(string value) =>
        value.Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("\n", "\\n");
}
