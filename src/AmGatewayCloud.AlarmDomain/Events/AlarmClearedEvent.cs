using AmGatewayCloud.AlarmDomain.Common;

namespace AmGatewayCloud.AlarmDomain.Events;

/// <summary>
/// 报警恢复领域事件：报警条件不再满足自动恢复时发布
/// </summary>
public class AlarmClearedEvent : DomainEvent
{
    public Guid AlarmId { get; }
    public string TenantId { get; }
    public string FactoryId { get; }
    public string DeviceId { get; }
    public double? ClearValue { get; }

    public AlarmClearedEvent(Guid alarmId, string tenantId, string factoryId, string deviceId, double? clearValue)
    {
        AlarmId = alarmId;
        TenantId = tenantId;
        FactoryId = factoryId;
        DeviceId = deviceId;
        ClearValue = clearValue;
    }
}
