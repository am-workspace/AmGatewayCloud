namespace AmGatewayCloud.Collector.Modbus.Configuration;

public class CollectorConfig
{
    public string DeviceId { get; set; } = "device-001";
    public string? TenantId { get; set; }
    public int PollIntervalMs { get; set; } = 2000;
    public ModbusConfig Modbus { get; set; } = new();
    public List<RegisterGroupConfig> RegisterGroups { get; set; } = [];
}
