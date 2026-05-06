using AmGatewayCloud.Collector.Modbus.Models;

namespace AmGatewayCloud.Collector.Modbus.Output;

public interface IDataOutput
{
    Task WriteAsync(DataPoint point, CancellationToken ct);
    Task WriteBatchAsync(IEnumerable<DataPoint> points, CancellationToken ct);
}
