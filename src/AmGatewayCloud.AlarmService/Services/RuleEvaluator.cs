namespace AmGatewayCloud.AlarmService.Services;

using AmGatewayCloud.AlarmService.Models;

/// <summary>
/// 规则评估器：根据报警规则对数据点进行阈值判断
/// </summary>
public class RuleEvaluator
{
    private const double Epsilon = 0.0001;

    /// <summary>
    /// 评估数据点是否触发报警规则
    /// </summary>
    public bool Evaluate(DataPointReadModel point, AlarmRule rule)
    {
        // 字符串比较：优先使用 ThresholdString，其次 Threshold.ToString()（仅支持 == 和 !=）
        if (rule.Operator is "==" or "!=" && point.ValueString is not null)
        {
            var thresholdStr = rule.ThresholdString ?? rule.Threshold.ToString();
            return rule.Operator == "=="
                ? string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase)
                : !string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase);
        }

        var value = ExtractValue(point, rule);
        if (value is null) return false;

        return rule.Operator switch
        {
            ">"  => value > rule.Threshold,
            ">=" => value >= rule.Threshold,
            "<"  => value < rule.Threshold,
            "<=" => value <= rule.Threshold,
            "==" => Math.Abs(value.Value - rule.Threshold) < Epsilon,
            "!=" => Math.Abs(value.Value - rule.Threshold) >= Epsilon,
            _ => false
        };
    }

    /// <summary>
    /// 判断报警是否应该自动恢复（Deadband 机制）
    /// <para>
    /// 大于类规则：值回落到 ClearThreshold 以下时恢复
    /// 小于类规则：值回升到 ClearThreshold 以上时恢复
    /// 字符串规则：当前值不再满足触发条件时恢复
    /// </para>
    /// </summary>
    public bool ShouldClear(DataPointReadModel point, AlarmRule rule)
    {
        // 没有设置恢复阈值时，对于字符串比较采用默认恢复逻辑
        if (rule.ClearThreshold is null)
        {
            var thresholdStr = rule.ThresholdString ?? rule.Threshold.ToString();
            // 字符串 == 规则：当前值不等于阈值时恢复
            if (rule.Operator == "==" && point.ValueString is not null)
                return !string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase);

            // 字符串 != 规则：当前值等于阈值时恢复（回到了正常状态）
            if (rule.Operator == "!=" && point.ValueString is not null)
                return string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase);

            return false;
        }

        // 字符串比较带 ClearThreshold 的情况（如 quality == "Bad", ClearThreshold 表示恢复参考值）
        if (rule.Operator is "==" or "!=" && point.ValueString is not null)
        {
            var thresholdStr = rule.ThresholdString ?? rule.Threshold.ToString();
            return rule.Operator == "=="
                ? !string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase)
                : string.Equals(point.ValueString, thresholdStr, StringComparison.OrdinalIgnoreCase);
        }

        var value = ExtractValue(point, rule);
        if (value is null) return false;

        // 大于类规则：值回落到 ClearThreshold 以下时恢复
        if (rule.Operator is ">" or ">=")
            return value < rule.ClearThreshold;

        // 小于类规则：值回升到 ClearThreshold 以上时恢复
        if (rule.Operator is "<" or "<=")
            return value > rule.ClearThreshold;

        return false;
    }

    /// <summary>
    /// 校验 ClearThreshold 与 Threshold 的合法性
    /// <para>
    /// 大于类运算符：恢复阈值必须小于触发阈值
    /// 小于类运算符：恢复阈值必须大于触发阈值
    /// </para>
    /// </summary>
    public static bool ValidateClearThreshold(string op, double threshold, double? clearThreshold)
    {
        if (clearThreshold is null) return true;
        return op switch
        {
            ">" or ">=" => clearThreshold.Value < threshold,
            "<" or "<=" => clearThreshold.Value > threshold,
            _ => true
        };
    }

    /// <summary>
    /// 从数据点中提取数值，优先 value_float，其次 value_int 转 double，bool 转 0/1
    /// </summary>
    public double? ExtractValue(DataPointReadModel point, AlarmRule rule)
    {
        if (point.ValueFloat.HasValue) return point.ValueFloat.Value;
        if (point.ValueInt.HasValue) return (double)point.ValueInt.Value;
        if (point.ValueBool.HasValue) return point.ValueBool.Value ? 1.0 : 0.0;
        return null;
    }
}
