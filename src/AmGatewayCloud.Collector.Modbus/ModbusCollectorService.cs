using AmGatewayCloud.Collector.Modbus.Configuration;
using AmGatewayCloud.Collector.Modbus.Models;
using AmGatewayCloud.Collector.Modbus.Output;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AmGatewayCloud.Collector.Modbus;

public class ModbusCollectorService : BackgroundService
{
    private readonly ModbusConnection _connection;
    private readonly CollectorConfig _config;
    private readonly IEnumerable<IDataOutput> _outputs;
    private readonly ILogger<ModbusCollectorService> _logger;

    public ModbusCollectorService(
        ModbusConnection connection,
        IOptions<CollectorConfig> config,
        IEnumerable<IDataOutput> outputs,
        ILogger<ModbusCollectorService> logger)
    {
        _connection = connection;
        _config = config.Value;
        _outputs = outputs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Collector starting - Device: {DeviceId}, Target: {Host}:{Port}/{SlaveId}",
            _config.DeviceId, _config.Modbus.Host, _config.Modbus.Port, _config.Modbus.SlaveId);
        _logger.LogInformation("Polling {GroupCount} register groups every {Interval}ms",
            _config.RegisterGroups.Count, _config.PollIntervalMs);

        // Initial connection
        await _connection.EnsureConnectedAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var timestamp = DateTime.UtcNow;
            var failedCount = 0;

            foreach (var group in _config.RegisterGroups)
            {
                try
                {
                    var points = await ReadGroupAsync(group, timestamp, ct);
                    await OutputBatchAsync(points, ct);
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _logger.LogWarning(ex, "Failed to read register group '{GroupName}'", group.Name);

                    // Output Bad quality points
                    var badPoints = group.Tags.Select(tag =>
                        DataPoint.Bad(_config.DeviceId, tag, timestamp, _config.TenantId)).ToList();
                    await OutputBatchAsync(badPoints, ct);
                }
            }

            // P0: All groups failed → trigger reconnect
            if (failedCount == _config.RegisterGroups.Count && _config.RegisterGroups.Count > 0)
            {
                _logger.LogWarning("All register groups failed, triggering reconnect");
                try
                {
                    await _connection.ReconnectAsync(ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Reconnect failed");
                }
            }

            try
            {
                await Task.Delay(_config.PollIntervalMs, ct);
            }
            catch (OperationCanceledException) { break; }
        }

        _connection.Disconnect();
        _logger.LogInformation("Collector stopped - Device: {DeviceId}", _config.DeviceId);
    }

    private async Task<List<DataPoint>> ReadGroupAsync(RegisterGroupConfig group, DateTime timestamp, CancellationToken ct)
    {
        var points = new List<DataPoint>(group.Count);

        switch (group.Type)
        {
            case RegisterType.Holding:
                var holding = await _connection.ReadHoldingRegistersAsync(group.Start, (ushort)group.Count, ct);
                for (int i = 0; i < group.Count; i++)
                {
                    var value = ApplyScale(holding[i], group, group.Tags[i]);
                    points.Add(DataPoint.Good(_config.DeviceId, group.Tags[i], value, timestamp, _config.TenantId));
                }
                break;

            case RegisterType.Input:
                var input = await _connection.ReadInputRegistersAsync(group.Start, (ushort)group.Count, ct);
                for (int i = 0; i < group.Count; i++)
                {
                    var value = ApplyScale(input[i], group, group.Tags[i]);
                    points.Add(DataPoint.Good(_config.DeviceId, group.Tags[i], value, timestamp, _config.TenantId));
                }
                break;

            case RegisterType.Discrete:
                var discrete = await _connection.ReadDiscreteInputsAsync(group.Start, (ushort)group.Count, ct);
                for (int i = 0; i < group.Count; i++)
                {
                    points.Add(DataPoint.Good(_config.DeviceId, group.Tags[i], discrete[i], timestamp, _config.TenantId));
                }
                break;

            case RegisterType.Coil:
                var coils = await _connection.ReadCoilsAsync(group.Start, (ushort)group.Count, ct);
                for (int i = 0; i < group.Count; i++)
                {
                    points.Add(DataPoint.Good(_config.DeviceId, group.Tags[i], coils[i], timestamp, _config.TenantId));
                }
                break;
        }

        return points;
    }

    private static double ApplyScale(ushort rawValue, RegisterGroupConfig group, string tag)
    {
        // Per-tag ScaleFactor takes precedence
        double scaleFactor;
        if (group.TagScales.TryGetValue(tag, out var tagScale))
        {
            scaleFactor = tagScale;
        }
        else
        {
            // ScaleFactor compatibility: if ScaleFactor != 1.0 and Scale == 1.0, convert
            scaleFactor = group.Scale;
            if (group.ScaleFactor != 1.0 && group.Scale == 1.0)
            {
                scaleFactor = group.ScaleFactor;
            }
        }

        return rawValue / scaleFactor + group.Offset;
    }

    private async Task OutputBatchAsync(List<DataPoint> points, CancellationToken ct)
    {
        foreach (var output in _outputs)
        {
            try
            {
                await output.WriteBatchAsync(points, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Output {OutputType} failed", output.GetType().Name);
            }
        }
    }
}
