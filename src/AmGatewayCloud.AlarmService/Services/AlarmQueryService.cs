using AmGatewayCloud.Shared.DTOs;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// 报警查询服务：查询报警列表、确认、抑制、关闭
/// </summary>
public class AlarmQueryService
{
    private readonly AlarmEventRepository _eventRepo;
    private readonly ILogger<AlarmQueryService> _logger;

    public AlarmQueryService(AlarmEventRepository eventRepo, ILogger<AlarmQueryService> logger)
    {
        _eventRepo = eventRepo;
        _logger = logger;
    }

    /// <summary>
    /// 分页查询报警事件列表
    /// </summary>
    public async Task<PagedResult<AlarmEventDto>> GetAlarmsAsync(
        string? factoryId, string? deviceId, string? status, string? level,
        bool? isStale, int page, int pageSize, CancellationToken ct)
    {
        var (items, totalCount) = await _eventRepo.QueryAlarmsAsync(
            factoryId, deviceId, status, level, isStale, page, pageSize, ct);

        return new PagedResult<AlarmEventDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 根据 ID 获取报警事件
    /// </summary>
    public async Task<AlarmEventDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var item = await _eventRepo.GetByIdWithRuleNameAsync(id, ct);
        return item is null ? null : MapToDto(item);
    }

    /// <summary>
    /// 确认报警（Active → Acked）
    /// </summary>
    public async Task<AlarmEventDto?> AcknowledgeAsync(Guid id, string acknowledgedBy, CancellationToken ct)
    {
        var success = await _eventRepo.AcknowledgeAsync(id, acknowledgedBy, ct);
        if (!success) return null;
        return await GetByIdAsync(id, ct);
    }

    /// <summary>
    /// 手动抑制报警（Active/Acked → Suppressed）
    /// </summary>
    public async Task<AlarmEventDto?> SuppressAsync(Guid id, string suppressedBy, string? reason, CancellationToken ct)
    {
        var success = await _eventRepo.SuppressAsync(id, suppressedBy, reason, ct);
        if (!success) return null;
        return await GetByIdAsync(id, ct);
    }

    /// <summary>
    /// 手动关闭报警（→ Cleared）
    /// </summary>
    public async Task<AlarmEventDto?> ClearAsync(Guid id, CancellationToken ct)
    {
        var success = await _eventRepo.ClearAsync(id, ct);
        if (!success) return null;
        return await GetByIdAsync(id, ct);
    }

    private static AlarmEventDto MapToDto(AlarmEventWithRuleName item) => new()
    {
        Id = item.Id,
        RuleId = item.RuleId,
        RuleName = item.RuleName,
        TenantId = item.TenantId,
        FactoryId = item.FactoryId,
        WorkshopId = item.WorkshopId,
        DeviceId = item.DeviceId,
        Tag = item.Tag,
        TriggerValue = item.TriggerValue,
        Level = item.Level,
        Status = item.Status,
        IsStale = item.IsStale,
        StaleAt = item.StaleAt,
        Message = item.Message,
        TriggeredAt = item.TriggeredAt,
        AcknowledgedAt = item.AcknowledgedAt,
        AcknowledgedBy = item.AcknowledgedBy,
        SuppressedAt = item.SuppressedAt,
        SuppressedBy = item.SuppressedBy,
        SuppressedReason = item.SuppressedReason,
        ClearedAt = item.ClearedAt,
        ClearValue = item.ClearValue,
        CreatedAt = item.CreatedAt
    };
}
