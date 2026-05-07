namespace AmGatewayCloud.Collector.Modbus.Configuration;

/// <summary>
/// Modbus TCP 连接配置：主机、端口、从站ID、超时及重连参数。
/// </summary>
public class ModbusConfig
{
    /// <summary>Modbus 从站主机地址</summary>
    public string Host { get; set; } = "localhost";
    /// <summary>Modbus 从站端口</summary>
    public int Port { get; set; } = 5020;
    /// <summary>Modbus 从站ID（单元标识符）</summary>
    public byte SlaveId { get; set; } = 1;
    /// <summary>重连间隔基准值（毫秒），用于指数退避计算</summary>
    public int ReconnectIntervalMs { get; set; } = 5000;
    /// <summary>读取超时（毫秒）</summary>
    public int ReadTimeoutMs { get; set; } = 3000;
    /// <summary>连接超时（毫秒）</summary>
    public int ConnectTimeoutMs { get; set; } = 5000;
}
