namespace AmGatewayCloud.AlarmDomain.Aggregates.Alarm;

/// <summary>
/// 报警状态值对象
/// </summary>
public enum AlarmStatus
{
    Active,
    Acked,
    Suppressed,
    Cleared
}
