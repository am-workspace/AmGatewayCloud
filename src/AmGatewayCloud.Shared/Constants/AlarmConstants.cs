namespace AmGatewayCloud.Shared.Constants;

/// <summary>
/// 报警相关常量
/// </summary>
public static class AlarmConstants
{
    public static readonly HashSet<string> ValidOperators = new() { ">", ">=", "<", "<=", "==", "!=" };
    public static readonly HashSet<string> ValidLevels = new() { "Info", "Warning", "Critical", "Fatal" };

    public const string AlarmExchange = "amgateway.alarms";
    public const string AlarmQueue = "amgateway.alarm-notifications";
    public const string AlarmRoutingKeyPattern = "alarm.#";
}
