namespace AmGatewayCloud.Collector.Modbus.Models;

/// <summary>
/// 数据质量枚举：标识数据点的采集状态。
/// </summary>
public enum DataQuality
{
    /// <summary>正常采集</summary>
    Good,
    /// <summary>采集失败</summary>
    Bad,
    /// <summary>状态未知</summary>
    Unknown
}
