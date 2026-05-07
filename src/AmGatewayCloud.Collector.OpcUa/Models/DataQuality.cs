namespace AmGatewayCloud.Collector.OpcUa.Models;

/// <summary>
/// 数据质量枚举，与 OPC UA StatusCode 对齐。
/// 后续抽取 Abstractions 时，Modbus 的 Unknown 映射为 Uncertain。
/// </summary>
public enum DataQuality
{
    /// <summary>正常读取 / StatusCode.Good</summary>
    Good,

    /// <summary>不确定 / StatusCode.Uncertain* / Modbus 的 Unknown</summary>
    Uncertain,

    /// <summary>读取失败 / StatusCode.Bad*</summary>
    Bad
}
