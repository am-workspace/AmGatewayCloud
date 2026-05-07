using AmGatewayCloud.Collector.Modbus.Models;

namespace AmGatewayCloud.Collector.Modbus.Output;

/// <summary>
/// 数据输出接口：定义单条和批量数据输出的抽象。
/// </summary>
public interface IDataOutput
{
    /// <summary>输出单条数据点</summary>
    Task WriteAsync(DataPoint point, CancellationToken ct);
    /// <summary>批量输出数据点</summary>
    Task WriteBatchAsync(IEnumerable<DataPoint> points, CancellationToken ct);
}
