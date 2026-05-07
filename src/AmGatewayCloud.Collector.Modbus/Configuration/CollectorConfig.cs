namespace AmGatewayCloud.Collector.Modbus.Configuration;

/// <summary>
/// Modbus 采集器顶层配置：设备标识、轮询间隔、Modbus 连接参数、寄存器组列表。
/// </summary>
public class CollectorConfig
{
    /// <summary>设备标识</summary>
    public string DeviceId { get; set; } = "device-001";
    /// <summary>多租户标识（预留）</summary>
    public string? TenantId { get; set; }
    /// <summary>轮询间隔（毫秒）</summary>
    public int PollIntervalMs { get; set; } = 2000;
    /// <summary>Modbus 连接配置</summary>
    public ModbusConfig Modbus { get; set; } = new();
    /// <summary>寄存器组配置列表</summary>
    public List<RegisterGroupConfig> RegisterGroups { get; set; } = [];
}
