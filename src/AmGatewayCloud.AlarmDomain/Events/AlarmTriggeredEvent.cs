using AmGatewayCloud.AlarmDomain.Aggregates.Alarm;
using AmGatewayCloud.AlarmDomain.Common;

namespace AmGatewayCloud.AlarmDomain.Events;

/// <summary>
/// 报警触发领域事件：新报警产生时发布
/// </summary>
public class AlarmTriggeredEvent : DomainEvent
{
    public Guid AlarmId { get; }
    public string TenantId { get; }
    public string FactoryId { get; }
    public string DeviceId { get; }
    public AlarmLevel Level { get; }

    public AlarmTriggeredEvent(Guid alarmId, string tenantId, string factoryId, string deviceId, AlarmLevel level)
    {
        AlarmId = alarmId;
        TenantId = tenantId;
        FactoryId = factoryId;
        DeviceId = deviceId;
        Level = level;
    }
}
