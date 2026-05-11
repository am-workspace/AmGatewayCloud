using MediatR;

namespace AmGatewayCloud.AlarmDomain.Common;

/// <summary>
/// 领域事件基类，实现 MediatR INotification 以支持事件发布
/// </summary>
public abstract class DomainEvent : INotification
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
}
