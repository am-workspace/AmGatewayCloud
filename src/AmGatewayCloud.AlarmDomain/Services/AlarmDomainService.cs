namespace AmGatewayCloud.AlarmDomain.Services;

using AmGatewayCloud.AlarmDomain.Aggregates.Alarm;

/// <summary>
/// 报警领域服务：封装跨聚合或不属于单一聚合根的领域逻辑
/// </summary>
public class AlarmDomainService
{
    /// <summary>
    /// 校验 ClearThreshold 与 Threshold 的合法性
    /// <para>
    /// 大于类运算符：恢复阈值必须小于触发阈值
    /// 小于类运算符：恢复阈值必须大于触发阈值
    /// </para>
    /// </summary>
    public static bool ValidateClearThreshold(OperatorType op, double threshold, double? clearThreshold)
    {
        if (clearThreshold is null) return true;
        return op switch
        {
            OperatorType.GreaterThan or OperatorType.GreaterThanOrEqual => clearThreshold.Value < threshold,
            OperatorType.LessThan or OperatorType.LessThanOrEqual => clearThreshold.Value > threshold,
            _ => true
        };
    }

    /// <summary>
    /// 校验 ClearThreshold 合法性，返回错误信息
    /// </summary>
    public static (bool Valid, string? Error) ValidateClearThresholdWithError(OperatorType op, double threshold, double? clearThreshold)
    {
        if (clearThreshold is null) return (true, null);

        var valid = ValidateClearThreshold(op, threshold, clearThreshold);
        if (valid) return (true, null);

        var opStr = op switch
        {
            OperatorType.GreaterThan => ">",
            OperatorType.GreaterThanOrEqual => ">=",
            OperatorType.LessThan => "<",
            OperatorType.LessThanOrEqual => "<=",
            _ => op.ToString()
        };

        var error = op is OperatorType.GreaterThan or OperatorType.GreaterThanOrEqual
            ? $"ClearThreshold ({clearThreshold.Value}) must be less than Threshold ({threshold}) for '{opStr}' operator"
            : $"ClearThreshold ({clearThreshold.Value}) must be greater than Threshold ({threshold}) for '{opStr}' operator";

        return (false, error);
    }

    /// <summary>
    /// 校验报警状态流转是否合法
    /// </summary>
    public static bool IsValidTransition(AlarmStatus from, AlarmStatus to)
    {
        return (from, to) switch
        {
            (AlarmStatus.Active, AlarmStatus.Acked) => true,
            (AlarmStatus.Active, AlarmStatus.Suppressed) => true,
            (AlarmStatus.Active, AlarmStatus.Cleared) => true,
            (AlarmStatus.Acked, AlarmStatus.Suppressed) => true,
            (AlarmStatus.Acked, AlarmStatus.Cleared) => true,
            (AlarmStatus.Suppressed, AlarmStatus.Cleared) => true,
            _ => false
        };
    }

    /// <summary>
    /// 判断报警是否处于"开放"状态（可被自动恢复或 Stale 检查）
    /// </summary>
    public static bool IsOpenStatus(AlarmStatus status)
    {
        return status is AlarmStatus.Active or AlarmStatus.Acked or AlarmStatus.Suppressed;
    }
}
