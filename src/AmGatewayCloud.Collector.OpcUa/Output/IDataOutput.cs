using AmGatewayCloud.Collector.OpcUa.Models;

namespace AmGatewayCloud.Collector.OpcUa.Output;

public interface IDataOutput
{
    /// <summary>输出单条数据</summary>
    Task WriteAsync(DataPoint point, CancellationToken ct);

    /// <summary>批量输出（如同一 NodeGroup 的所有数据点）</summary>
    Task WriteBatchAsync(IEnumerable<DataPoint> points, CancellationToken ct);
}
