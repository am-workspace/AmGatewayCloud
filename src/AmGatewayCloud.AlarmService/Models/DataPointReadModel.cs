namespace AmGatewayCloud.AlarmService.Models;

/// <summary>
/// 从 TimescaleDB device_data 表读取的最新数据点
/// </summary>
public class DataPointReadModel
{
    public DateTimeOffset Time { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string FactoryId { get; set; } = string.Empty;
    public string? WorkshopId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Quality { get; set; } = "Good";
    public double? ValueFloat { get; set; }
    public long? ValueInt { get; set; }
    public bool? ValueBool { get; set; }
    public string? ValueString { get; set; }
}
