using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, double> _lastValues = new();

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
                        DataPoint.Bad(_config.DeviceId, tag, timestamp, _config.TenantId, group.Name)).ToList();
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
                    var tag = group.Tags[i];
                    var value = ApplyScale(holding[i], group, tag);
                    if (PassesDeadband(tag, value, ResolveDeadband(group, tag)))
                        points.Add(DataPoint.Good(_config.DeviceId, tag, value, timestamp, _config.TenantId));
                }
                break;

            case RegisterType.Input:
                var input = await _connection.ReadInputRegistersAsync(group.Start, (ushort)group.Count, ct);
                for (int i = 0; i < group.Count; i++)
                {
                    var tag = group.Tags[i];
                    var value = ApplyScale(input[i], group, tag);
                    if (PassesDeadband(tag, value, ResolveDeadband(group, tag)))
                        points.Add(DataPoint.Good(_config.DeviceId, tag, value, timestamp, _config.TenantId));
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

    /// <summary>
    /// 死区检查：变化量低于 threshold 则跳过上报。
    /// </summary>
    private bool PassesDeadband(string tag, double newValue, double threshold)
    {
        if (threshold <= 0) return true;

        if (_lastValues.TryGetValue(tag, out var lastValue))
        {
            var denominator = Math.Max(Math.Abs(lastValue), 0.001);
            var changePercent = Math.Abs(newValue - lastValue) / denominator * 100;
            if (changePercent < threshold)
                return false;
        }

        _lastValues[tag] = newValue;
        return true;
    }

    /// <summary>解析 per-tag 死区：TagDeadbands > group.DeadbandPercent > global DeadbandPercent</summary>
    private double ResolveDeadband(RegisterGroupConfig group, string tag)
    {
        if (group.TagDeadbands.TryGetValue(tag, out var td) && td > 0) return td;
        if (group.DeadbandPercent > 0) return group.DeadbandPercent;
        return _config.DeadbandPercent;
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

    /// <summary>
    /// 将一批数据点分发到所有已注册的 IDataOutput 通道。
    /// 单个通道写入失败不影响其他通道。
    /// </summary>
    /// <param name="points">待输出数据点列表</param>
    /// <param name="ct">取消令牌</param>
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
