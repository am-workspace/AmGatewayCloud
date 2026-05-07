namespace AmGatewayCloud.Collector.Modbus.Configuration;

/// <summary>
/// Modbus 寄存器类型枚举：对应四种标准读取功能码。
/// </summary>
public enum RegisterType
{
    /// <summary>保持寄存器（功能码 0x03，可读写）</summary>
    Holding,
    /// <summary>输入寄存器（功能码 0x04，只读）</summary>
    Input,
    /// <summary>离散输入（功能码 0x02，只读）</summary>
    Discrete,
    /// <summary>线圈（功能码 0x01，可读写）</summary>
    Coil
}
