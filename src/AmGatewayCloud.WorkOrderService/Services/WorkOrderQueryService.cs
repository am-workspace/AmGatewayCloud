using AmGatewayCloud.Shared.DTOs;
using AmGatewayCloud.Shared.Tenant;
using AmGatewayCloud.WorkOrderService.Configuration;
using AmGatewayCloud.WorkOrderService.Models;
using Npgsql;
using NpgsqlTypes;

namespace AmGatewayCloud.WorkOrderService.Services;

/// <summary>
/// 工单查询/分配/完成服务
/// 所有查询/操作自动附加租户过滤
/// </summary>
public class WorkOrderQueryService
{
    private readonly WorkOrderServiceConfig _config;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<WorkOrderQueryService> _logger;

    public WorkOrderQueryService(WorkOrderServiceConfig config, ITenantContext tenantContext, ILogger<WorkOrderQueryService> logger)
    {
        _config = config;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// 分页查询工单
    /// </summary>
    public async Task<PagedResult<WorkOrderDto>> QueryAsync(
        string? factoryId, string? status, string? assignee,
        int page, int pageSize, CancellationToken ct)
    {
        var conditions = new List<string> { "tenant_id = @tenantId" };
        var parameters = new List<NpgsqlParameter> { new("tenantId", _tenantContext.TenantId) };

        if (!string.IsNullOrEmpty(factoryId))
        {
            conditions.Add("factory_id = @factoryId");
            parameters.Add(new NpgsqlParameter("factoryId", factoryId));
        }
        if (!string.IsNullOrEmpty(status))
        {
            conditions.Add("status = @status");
            parameters.Add(new NpgsqlParameter("status", status));
        }
        if (!string.IsNullOrEmpty(assignee))
        {
            conditions.Add("assignee = @assignee");
            parameters.Add(new NpgsqlParameter("assignee", assignee));
        }

        var whereClause = string.Join(" AND ", conditions);

        // Count
        await using var conn = new NpgsqlConnection(_config.PostgreSql.ConnectionString);
        await conn.OpenAsync(ct);

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(1) FROM work_orders WHERE {whereClause}";
        countCmd.Parameters.AddRange(parameters.ToArray());
        var totalCount = (long)(await countCmd.ExecuteScalarAsync(ct))!;

        // Query
        var offset = (page - 1) * pageSize;
        await using var queryCmd = conn.CreateCommand();
        queryCmd.CommandText = $@"
SELECT id, alarm_id, tenant_id, factory_id, workshop_id, device_id,
    title, description, level, status, assignee, assigned_at,
    completed_at, completed_by, completion_note, created_at
FROM work_orders
WHERE {whereClause}
ORDER BY created_at DESC
LIMIT @limit OFFSET @offset";
        queryCmd.Parameters.AddRange(parameters.ToArray());
        queryCmd.Parameters.AddWithValue("limit", pageSize);
        queryCmd.Parameters.AddWithValue("offset", offset);

        var items = new List<WorkOrderDto>();
        await using var reader = await queryCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(MapToDto(reader));
        }

        return new PagedResult<WorkOrderDto>
        {
            Items = items,
            TotalCount = (int)totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 查询单个工单
    /// </summary>
    public async Task<WorkOrderDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_config.PostgreSql.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, alarm_id, tenant_id, factory_id, workshop_id, device_id,
    title, description, level, status, assignee, assigned_at,
    completed_at, completed_by, completion_note, created_at
FROM work_orders
WHERE id = @id AND tenant_id = @tenantId";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tenantId", _tenantContext.TenantId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return MapToDto(reader);

        return null;
    }

    /// <summary>
    /// 分配工单（Pending → InProgress）
    /// </summary>
    public async Task<WorkOrderDto?> AssignAsync(Guid id, string assignee, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_config.PostgreSql.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE work_orders
SET status = 'InProgress', assignee = @assignee, assigned_at = NOW(), updated_at = NOW()
WHERE id = @id AND tenant_id = @tenantId AND status = 'Pending'";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("assignee", assignee);
        cmd.Parameters.AddWithValue("tenantId", _tenantContext.TenantId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
        {
            _logger.LogWarning("Failed to assign work order {Id}: not found or not Pending", id);
            return null;
        }

        return await GetByIdAsync(id, ct);
    }

    /// <summary>
    /// 完成工单（InProgress → Completed）
    /// </summary>
    public async Task<WorkOrderDto?> CompleteAsync(Guid id, string completedBy, string? completionNote, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_config.PostgreSql.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE work_orders
SET status = 'Completed', completed_by = @completedBy, completion_note = @completionNote,
    completed_at = NOW(), updated_at = NOW()
WHERE id = @id AND tenant_id = @tenantId AND status IN ('InProgress', 'Pending')";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("completedBy", completedBy);
        cmd.Parameters.AddWithValue("completionNote", (object?)completionNote ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tenantId", _tenantContext.TenantId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
        {
            _logger.LogWarning("Failed to complete work order {Id}: not found or not InProgress/Pending", id);
            return null;
        }

        return await GetByIdAsync(id, ct);
    }

    /// <summary>
    /// 工单状态汇总
    /// </summary>
    public async Task<WorkOrderSummary> GetSummaryAsync(string? factoryId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_config.PostgreSql.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string> { "tenant_id = @tenantId" };
        var where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";

        if (!string.IsNullOrEmpty(factoryId))
        {
            conditions.Add("factory_id = @factoryId");
            where = " WHERE " + string.Join(" AND ", conditions);
        }

        cmd.CommandText = $@"
SELECT status, COUNT(1) as cnt
FROM work_orders{where}
GROUP BY status";

        cmd.Parameters.AddWithValue("tenantId", _tenantContext.TenantId);

        if (!string.IsNullOrEmpty(factoryId))
            cmd.Parameters.AddWithValue("factoryId", factoryId);

        var summary = new WorkOrderSummary();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var status = reader.GetString(0);
            var count = (long)reader.GetInt64(1);
            switch (status)
            {
                case "Pending": summary.Pending = count; break;
                case "InProgress": summary.InProgress = count; break;
                case "Completed": summary.Completed = count; break;
            }
        }

        return summary;
    }

    private static WorkOrderDto MapToDto(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(reader.GetOrdinal("id")),
        AlarmId = reader.GetGuid(reader.GetOrdinal("alarm_id")),
        TenantId = reader.GetString(reader.GetOrdinal("tenant_id")),
        FactoryId = reader.GetString(reader.GetOrdinal("factory_id")),
        WorkshopId = reader.IsDBNull(reader.GetOrdinal("workshop_id"))
            ? null : reader.GetString(reader.GetOrdinal("workshop_id")),
        DeviceId = reader.GetString(reader.GetOrdinal("device_id")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Description = reader.IsDBNull(reader.GetOrdinal("description"))
            ? null : reader.GetString(reader.GetOrdinal("description")),
        Level = reader.GetString(reader.GetOrdinal("level")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        Assignee = reader.IsDBNull(reader.GetOrdinal("assignee"))
            ? null : reader.GetString(reader.GetOrdinal("assignee")),
        AssignedAt = reader.IsDBNull(reader.GetOrdinal("assigned_at"))
            ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("assigned_at")),
        CompletedAt = reader.IsDBNull(reader.GetOrdinal("completed_at"))
            ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("completed_at")),
        CompletedBy = reader.IsDBNull(reader.GetOrdinal("completed_by"))
            ? null : reader.GetString(reader.GetOrdinal("completed_by")),
        CompletionNote = reader.IsDBNull(reader.GetOrdinal("completion_note"))
            ? null : reader.GetString(reader.GetOrdinal("completion_note")),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at"))
    };
}

/// <summary>
/// 工单状态汇总
/// </summary>
public class WorkOrderSummary
{
    public long Pending { get; set; }
    public long InProgress { get; set; }
    public long Completed { get; set; }
}
