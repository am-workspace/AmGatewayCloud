using AmGatewayCloud.Shared.Configuration;
using AmGatewayCloud.AlarmService.Models;
using Dapper;
using Npgsql;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// 时序数据读取器：从 TimescaleDB device_data 表读取最新数据点，供报警评估使用
/// </summary>
public class TimescaleDbReader
{
    private readonly string _connectionString;
    private readonly ILogger<TimescaleDbReader> _logger;

    public TimescaleDbReader(TimescaleDbConfig config, ILogger<TimescaleDbReader> logger)
    {
        _logger = logger;
        _connectionString = config.ConnectionString;
    }

    public async Task<List<DataPointReadModel>> GetLatestDataAsync(DateTimeOffset since, CancellationToken ct)
    {
        const string sql = @"
            SELECT DISTINCT ON (factory_id, device_id, tag)
                   time, tenant_id, factory_id, workshop_id, device_id,
                   tag, quality,
                   value_float, value_int, value_bool, value_string
            FROM device_data
            WHERE time > @since
            ORDER BY factory_id, device_id, tag, time DESC";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var result = await conn.QueryAsync<DataPointReadModel>(sql, new { since });
        return result.AsList();
    }

    public async Task<DateTimeOffset> GetLastDataTimeAsync(string deviceId, CancellationToken ct)
    {
        const string sql = @"
            SELECT COALESCE(MAX(time), '-infinity'::timestamptz)
            FROM device_data
            WHERE device_id = @deviceId";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<DateTimeOffset>(sql, new { deviceId });
    }
}
