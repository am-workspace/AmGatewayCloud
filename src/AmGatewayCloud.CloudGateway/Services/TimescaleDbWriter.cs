using System.Threading.Channels;
using AmGatewayCloud.CloudGateway.Configuration;
using AmGatewayCloud.CloudGateway.Infrastructure;
using AmGatewayCloud.CloudGateway.Models;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AmGatewayCloud.CloudGateway.Services;

public class TimescaleDbWriter : IAsyncDisposable
{
    private readonly NpgsqlConnectionFactory _connectionFactory;
    private readonly ILogger<TimescaleDbWriter> _logger;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly Channel<DataPointRow> _buffer;
    private readonly List<DataPointRow> _pendingBatch = [];
    private readonly object _batchLock = new();
    private readonly Timer _flushTimer;

    public TimescaleDbWriter(
        IOptions<CloudGatewayConfig> options,
        ILogger<TimescaleDbWriter> logger)
    {
        var config = options.Value.TimescaleDb;
        _connectionFactory = new NpgsqlConnectionFactory(options, config.Database);
        _logger = logger;
        _batchSize = config.BatchSize;
        _flushInterval = TimeSpan.FromMilliseconds(config.FlushIntervalMs);

        var maxPending = config.BatchSize * 5;
        _buffer = Channel.CreateBounded<DataPointRow>(new BoundedChannelOptions(maxPending)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _flushTimer = new Timer(_ => _ = FlushAsync(), null, _flushInterval, _flushInterval);
    }

    public async Task WriteBatchAsync(DataBatch batch, CancellationToken ct = default)
    {
        foreach (var point in batch.Points)
        {
            var row = ConvertToRow(batch, point);
            await _buffer.Writer.WriteAsync(row, ct);
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        List<DataPointRow> batchToFlush;

        lock (_batchLock)
        {
            while (_buffer.Reader.TryRead(out var row))
            {
                _pendingBatch.Add(row);
                if (_pendingBatch.Count >= _batchSize)
                    break;
            }

            if (_pendingBatch.Count == 0)
                return;

            batchToFlush = new List<DataPointRow>(_pendingBatch);
            _pendingBatch.Clear();
        }

        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            const string sql = @"
                INSERT INTO device_data
                (time, batch_id, tenant_id, factory_id, workshop_id, device_id, protocol, tag, quality, group_name, value_int, value_float, value_bool, value_string, value_type)
                VALUES
                (@time, @batch_id, @tenant_id, @factory_id, @workshop_id, @device_id, @protocol, @tag, @quality, @group_name, @value_int, @value_float, @value_bool, @value_string, @value_type)
                ON CONFLICT (time, batch_id, tag) DO NOTHING;
            ";

            await conn.ExecuteAsync(sql, batchToFlush, tx);
            await tx.CommitAsync(ct);

            _logger.LogDebug("Flushed {Count} rows to TimescaleDB", batchToFlush.Count);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public int GetPendingCount() => _buffer.Reader.Count + _pendingBatch.Count;

    public async ValueTask DisposeAsync()
    {
        _flushTimer.Dispose();
        await FlushAsync();
        _buffer.Writer.Complete();
    }

    private static DataPointRow ConvertToRow(DataBatch batch, DataPoint point)
    {
        var (value, column) = DataPointConverter.ConvertValue(point);

        var row = new DataPointRow
        {
            time = point.Timestamp.UtcDateTime,
            batch_id = batch.BatchId,
            tenant_id = batch.TenantId ?? "default",
            factory_id = batch.FactoryId,
            workshop_id = batch.WorkshopId,
            device_id = batch.DeviceId,
            protocol = batch.Protocol,
            tag = point.Tag,
            quality = point.Quality,
            group_name = point.GroupName,
            value_type = point.ValueType
        };

        switch (column)
        {
            case "value_int":
                row.value_int = value as long?;
                break;
            case "value_float":
                row.value_float = value as double?;
                break;
            case "value_bool":
                row.value_bool = value as bool?;
                break;
            case "value_string":
                row.value_string = value as string;
                break;
        }

        return row;
    }
}

public class DataPointRow
{
    public DateTime time { get; set; }
    public Guid batch_id { get; set; }
    public string tenant_id { get; set; } = string.Empty;
    public string factory_id { get; set; } = string.Empty;
    public string workshop_id { get; set; } = string.Empty;
    public string device_id { get; set; } = string.Empty;
    public string protocol { get; set; } = string.Empty;
    public string tag { get; set; } = string.Empty;
    public string quality { get; set; } = string.Empty;
    public string? group_name { get; set; }
    public long? value_int { get; set; }
    public double? value_float { get; set; }
    public bool? value_bool { get; set; }
    public string? value_string { get; set; }
    public string value_type { get; set; } = string.Empty;
}
