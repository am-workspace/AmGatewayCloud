namespace AmGatewayCloud.Collector.Modbus.Configuration;

public class ModbusConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5020;
    public byte SlaveId { get; set; } = 1;
    public int ReconnectIntervalMs { get; set; } = 5000;
    public int ReadTimeoutMs { get; set; } = 3000;
    public int ConnectTimeoutMs { get; set; } = 5000;
}
