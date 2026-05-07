using System.Text;
using AmGatewayCloud.Collector.OpcUa.Models;

namespace AmGatewayCloud.Collector.OpcUa.Output;

/// <summary>
/// 控制台数据输出：将数据点格式化为可读文本后通过 ILogger 输出。
/// 批量输出时按节点组分组，减少日志行数。
/// </summary>
public class ConsoleDataOutput : IDataOutput
{
    private readonly ILogger<ConsoleDataOutput> _logger;

    /// <summary>
    /// 初始化 ConsoleDataOutput。
    /// </summary>
    /// <param name="logger">日志记录器</param>
    public ConsoleDataOutput(ILogger<ConsoleDataOutput> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 输出单条数据点到控制台。
    /// </summary>
    /// <param name="point">数据点</param>
    /// <param name="ct">取消令牌</param>
    public Task WriteAsync(DataPoint point, CancellationToken ct)
    {
        var text = FormatPoint(point);
        _logger.LogInformation("[{DeviceId}] {Text}", point.DeviceId, text);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 批量输出数据点到控制台，同一组的数据合并在一行显示。
    /// 格式：[DeviceId] GroupName: tag1=value1 tag2=value2 ...
    /// </summary>
    /// <param name="points">数据点集合</param>
    /// <param name="ct">取消令牌</param>
    public Task WriteBatchAsync(IEnumerable<DataPoint> points, CancellationToken ct)
    {
        var pointList = points.ToList();
        if (pointList.Count == 0) return Task.CompletedTask;

        var groupName = pointList[0].Properties?.TryGetValue("GroupName", out var gn) == true
            ? gn.ToString() : null;

        var sb = new StringBuilder();
        sb.Append($"[{pointList[0].DeviceId}] ");
        if (groupName is not null) sb.Append($"{groupName}: ");

        for (int i = 0; i < pointList.Count; i++)
        {
            var p = pointList[i];
            sb.Append(p.Quality == DataQuality.Good
                ? $"{p.Tag}={FormatValue(p.Value)}"
                : $"{p.Tag}=<BAD>");

            if (i < pointList.Count - 1) sb.Append(' ');
        }

        _logger.LogInformation("{Output}", sb.ToString());
        return Task.CompletedTask;
    }

    /// <summary>
    /// 将单个数据点格式化为字符串（tag=value 或 tag=&lt;BAD&gt;）。
    /// </summary>
    /// <param name="point">数据点</param>
    /// <returns>格式化字符串</returns>
    private static string FormatPoint(DataPoint point)
    {
        return point.Quality == DataQuality.Good
            ? $"{point.Tag}={FormatValue(point.Value)}"
            : $"{point.Tag}=<BAD>";
    }

    /// <summary>
    /// 将值对象格式化为可读字符串：double/float 保留2位小数，bool 小写，其他直接 ToString。
    /// </summary>
    /// <param name="value">待格式化的值</param>
    /// <returns>格式化字符串</returns>
    private static string FormatValue(object? value)
    {
        return value switch
        {
            double d => d.ToString("F2"),
            float f => f.ToString("F2"),
            int i => i.ToString(),
            long l => l.ToString(),
            bool b => b.ToString().ToLowerInvariant(),
            _ => value?.ToString() ?? "null"
        };
    }
}
