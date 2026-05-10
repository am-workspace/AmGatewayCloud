namespace AmGatewayCloud.AlarmService.Models;

public class AlarmRule
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
    public int DelaySeconds { get; set; } = 0;
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
