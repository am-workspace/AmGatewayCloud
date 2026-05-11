using AmGatewayCloud.AlarmDomain.Aggregates.Alarm;
using AmGatewayCloud.AlarmDomain.Common;
using AmGatewayCloud.AlarmInfrastructure.Repositories;
using AmGatewayCloud.Shared.DTOs;
using MediatR;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// 报警查询服务：查询报警列表、确认、抑制、关闭
/// 所有状态操作通过 Domain 聚合根执行，确保业务规则一致性
/// </summary>
public class AlarmQueryService
{
    private readonly AlarmEventRepository _eventRepo;
    private readonly IMediator _mediator;
    private readonly ILogger<AlarmQueryService> _logger;

    public AlarmQueryService(
        AlarmEventRepository eventRepo,
        IMediator mediator,
        ILogger<AlarmQueryService> logger)
    {
        _eventRepo = eventRepo;
        _mediator = mediator;
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
    /// 确认报警（Active → Acked）：通过 Domain 聚合根执行状态流转
    /// </summary>
    public async Task<AlarmEventDto?> AcknowledgeAsync(Guid id, string acknowledgedBy, CancellationToken ct)
    {
        var alarm = await _eventRepo.GetByIdAsync(id, ct);
        if (alarm is null) return null;

        alarm.Acknowledge(acknowledgedBy);

        await _eventRepo.UpdateAsync(alarm, ct);
        await PublishDomainEventsAsync(alarm, ct);
        return await GetByIdAsync(id, ct);
    }

    /// <summary>
    /// 手动抑制报警（Active/Acked → Suppressed）：通过 Domain 聚合根执行状态流转
    /// </summary>
    public async Task<AlarmEventDto?> SuppressAsync(Guid id, string suppressedBy, string? reason, CancellationToken ct)
    {
        var alarm = await _eventRepo.GetByIdAsync(id, ct);
        if (alarm is null) return null;

        alarm.Suppress(suppressedBy, reason);

        await _eventRepo.UpdateAsync(alarm, ct);
        await PublishDomainEventsAsync(alarm, ct);
        return await GetByIdAsync(id, ct);
    }

    /// <summary>
    /// 手动关闭报警（→ Cleared）：通过 Domain 聚合根执行状态流转
    /// </summary>
    public async Task<AlarmEventDto?> ClearAsync(Guid id, CancellationToken ct)
    {
        var alarm = await _eventRepo.GetByIdAsync(id, ct);
        if (alarm is null) return null;

        alarm.ManualClear();

        await _eventRepo.UpdateAsync(alarm, ct);
        await PublishDomainEventsAsync(alarm, ct);
        return await GetByIdAsync(id, ct);
    }

    private async Task PublishDomainEventsAsync(Alarm alarm, CancellationToken ct)
    {
        foreach (var domainEvent in alarm.DomainEvents)
            await _mediator.Publish(domainEvent, ct);
        alarm.ClearDomainEvents();
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
