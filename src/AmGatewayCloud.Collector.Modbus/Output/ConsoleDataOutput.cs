using System.Text;
using AmGatewayCloud.Collector.Modbus.Models;

namespace AmGatewayCloud.Collector.Modbus.Output;

public class ConsoleDataOutput : IDataOutput
{
    private readonly ILogger<ConsoleDataOutput> _logger;

    public ConsoleDataOutput(ILogger<ConsoleDataOutput> logger)
    {
        _logger = logger;
    }

    public Task WriteAsync(DataPoint point, CancellationToken ct)
    {
        var text = FormatPoint(point);
        _logger.LogInformation("[{DeviceId}] {Text}", point.DeviceId, text);
        return Task.CompletedTask;
    }

    public Task WriteBatchAsync(IEnumerable<DataPoint> points, CancellationToken ct)
    {
        var pointList = points.ToList();
        if (pointList.Count == 0) return Task.CompletedTask;

        // Group by implied register group (consecutive tags from same device)
        var sb = new StringBuilder();
        sb.Append($"[{pointList[0].DeviceId}] ");

        for (int i = 0; i < pointList.Count; i++)
        {
            var p = pointList[i];
            if (p.Quality == DataQuality.Good)
            {
                sb.Append($"{p.Tag}={FormatValue(p.Value)}");
            }
            else
            {
                sb.Append($"{p.Tag}=<BAD>");
            }

            if (i < pointList.Count - 1) sb.Append(" ");
        }

        _logger.LogInformation("{Output}", sb.ToString());
        return Task.CompletedTask;
    }

    private static string FormatPoint(DataPoint point)
    {
        return point.Quality == DataQuality.Good
            ? $"{point.Tag}={FormatValue(point.Value)}"
            : $"{point.Tag}=<BAD>";
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            double d => d.ToString("F1"),
            float f => f.ToString("F1"),
            int i => i.ToString(),
            bool b => b.ToString().ToLowerInvariant(),
            _ => value?.ToString() ?? "null"
        };
    }
}
