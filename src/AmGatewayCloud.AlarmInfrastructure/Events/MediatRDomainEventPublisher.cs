using AmGatewayCloud.AlarmDomain.Aggregates.Alarm;
using AmGatewayCloud.AlarmDomain.Common;
using AmGatewayCloud.AlarmDomain.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AmGatewayCloud.AlarmInfrastructure.Events;

/// <summary>
/// MediatR 领域事件发布器：在 DbContext.SaveChangesAsync 后发布领域事件
/// </summary>
public class MediatRDomainEventPublisher
{
    private readonly IMediator _mediator;
    private readonly ILogger<MediatRDomainEventPublisher> _logger;

    public MediatRDomainEventPublisher(IMediator mediator, ILogger<MediatRDomainEventPublisher> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    /// <summary>
    /// 发布聚合根中的领域事件
    /// </summary>
    public async Task PublishDomainEventsAsync(IEnumerable<DomainEvent> domainEvents, CancellationToken ct)
    {
        foreach (var domainEvent in domainEvents)
        {
            _logger.LogDebug("Publishing domain event: {EventType} ({EventId})",
                domainEvent.GetType().Name, domainEvent.EventId);

            await _mediator.Publish(domainEvent, ct);
        }
    }
}

/// <summary>
/// AlarmTriggeredEvent 的 MediatR 通知处理 — 仅做日志，实际 RabbitMQ 发布由 AlarmService 中的桥接处理
/// </summary>
public class AlarmTriggeredEventHandler : INotificationHandler<AlarmTriggeredEvent>
{
    private readonly ILogger<AlarmTriggeredEventHandler> _logger;

    public AlarmTriggeredEventHandler(ILogger<AlarmTriggeredEventHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(AlarmTriggeredEvent notification, CancellationToken ct)
    {
        _logger.LogInformation(
            "Domain Event: AlarmTriggered - AlarmId={AlarmId}, Factory={FactoryId}, Device={DeviceId}, Level={Level}",
            notification.AlarmId, notification.FactoryId, notification.DeviceId, notification.Level);
        return Task.CompletedTask;
    }
}

/// <summary>
/// AlarmClearedEvent 的 MediatR 通知处理 — 仅做日志
/// </summary>
public class AlarmClearedEventHandler : INotificationHandler<AlarmClearedEvent>
{
    private readonly ILogger<AlarmClearedEventHandler> _logger;

    public AlarmClearedEventHandler(ILogger<AlarmClearedEventHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(AlarmClearedEvent notification, CancellationToken ct)
    {
        _logger.LogInformation(
            "Domain Event: AlarmCleared - AlarmId={AlarmId}, Factory={FactoryId}, Device={DeviceId}",
            notification.AlarmId, notification.FactoryId, notification.DeviceId);
        return Task.CompletedTask;
    }
}
