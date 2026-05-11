using AmGatewayCloud.AlarmService.Models;

namespace AmGatewayCloud.AlarmService.Services;

using AlarmRuleDomain = AmGatewayCloud.AlarmDomain.Aggregates.Alarm.AlarmRule;
using OperatorType = AmGatewayCloud.AlarmDomain.Aggregates.Alarm.OperatorType;

/// <summary>
/// 规则评估器：根据报警规则对数据点进行阈值判断
/// </summary>
public class RuleEvaluator
{
    private const double Epsilon = 0.0001;

    /// <summary>
    /// 评估数据点是否触发报警规则（Domain.AlarmRule 版）
    /// </summary>
    public bool Evaluate(DataPointReadModel point, AlarmRuleDomain rule)
    {
        // 字符串比较：优先使用 ThresholdString，其次 Threshold.ToString()（仅支持 == 和 !=）
        if (rule.Operator is OperatorType.Equal or OperatorType.NotEqual
            && point.ValueString is not null)
        {
            var thresholdStr = rule.ThresholdString ?? rule.Threshold.ToString();
            return rule.Operator == OperatorType.Equal
                ? string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase)
                : !string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase);
        }

        var value = ExtractValue(point, rule);
        if (value is null) return false;

        return rule.Operator switch
        {
            OperatorType.GreaterThan => value > rule.Threshold,
            OperatorType.GreaterThanOrEqual => value >= rule.Threshold,
            OperatorType.LessThan => value < rule.Threshold,
            OperatorType.LessThanOrEqual => value <= rule.Threshold,
            OperatorType.Equal => Math.Abs(value.Value - rule.Threshold) < Epsilon,
            OperatorType.NotEqual => Math.Abs(value.Value - rule.Threshold) >= Epsilon,
            _ => false
        };
    }

    /// <summary>
    /// 判断报警是否应该自动恢复（Deadband 机制）（Domain.AlarmRule 版）
    /// </summary>
    public bool ShouldClear(DataPointReadModel point, AlarmRuleDomain rule)
    {
        // 没有设置恢复阈值时，对于字符串比较采用默认恢复逻辑
        if (rule.ClearThreshold is null)
        {
            var thresholdStr = rule.ThresholdString ?? rule.Threshold.ToString();
            if (rule.Operator == OperatorType.Equal && point.ValueString is not null)
                return !string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase);

            if (rule.Operator == OperatorType.NotEqual && point.ValueString is not null)
                return string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase);

            return false;
        }

        // 字符串比较带 ClearThreshold 的情况
        if (rule.Operator is OperatorType.Equal or OperatorType.NotEqual
            && point.ValueString is not null)
        {
            var thresholdStr = rule.ThresholdString ?? rule.Threshold.ToString();
            return rule.Operator == OperatorType.Equal
                ? !string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase)
                : string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase);
        }

        var value = ExtractValue(point, rule);
        if (value is null) return false;

        // 大于类规则：值回落到 ClearThreshold 以下时恢复
        if (rule.Operator is OperatorType.GreaterThan or OperatorType.GreaterThanOrEqual)
            return value < rule.ClearThreshold;

        // 小于类规则：值回升到 ClearThreshold 以上时恢复
        if (rule.Operator is OperatorType.LessThan or OperatorType.LessThanOrEqual)
            return value > rule.ClearThreshold;

        return false;
    }

    /// <summary>
    /// 从数据点中提取数值（Domain.AlarmRule 版）
    /// </summary>
    public double? ExtractValue(DataPointReadModel point, AlarmRuleDomain rule)
    {
        if (point.ValueFloat.HasValue) return point.ValueFloat.Value;
        if (point.ValueInt.HasValue) return (double)point.ValueInt.Value;
        if (point.ValueBool.HasValue) return point.ValueBool.Value ? 1.0 : 0.0;
        return null;
    }
}
