namespace AmGatewayCloud.Shared.DTOs;

/// <summary>
/// 创建报警规则请求
/// </summary>
public class CreateAlarmRuleRequest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TenantId { get; set; } = "default";
    public string? FactoryId { get; set; }
    public string? DeviceId { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string Operator { get; set; } = ">";
    public double Threshold { get; set; }
    public string? ThresholdString { get; set; }
    public double? ClearThreshold { get; set; }
    public string Level { get; set; } = "Warning";
    public int CooldownMinutes { get; set; } = 5;
    public int DelaySeconds { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }
}

/// <summary>
/// 更新报警规则请求
/// </summary>
public class UpdateAlarmRuleRequest
{
    public string? Name { get; set; }
    public string? FactoryId { get; set; }
    public string? DeviceId { get; set; }
    public string? Tag { get; set; }
    public string? Operator { get; set; }
    public double? Threshold { get; set; }
    public string? ThresholdString { get; set; }
    public double? ClearThreshold { get; set; }
    public string? Level { get; set; }
    public int? CooldownMinutes { get; set; }
    public int? DelaySeconds { get; set; }
    public bool? Enabled { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// 确认报警请求
/// </summary>
public class AckRequest
{
    public string AcknowledgedBy { get; set; } = string.Empty;
}

/// <summary>
/// 手动抑制报警请求
/// </summary>
public class SuppressRequest
{
    public string SuppressedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
}
