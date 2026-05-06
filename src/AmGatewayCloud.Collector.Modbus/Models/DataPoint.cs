namespace AmGatewayCloud.Collector.Modbus.Models;

public class DataPoint
{
    /// <summary>设备标识</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>数据标签名，如 "temperature", "pressure"</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>采集值</summary>
    public object? Value { get; set; }

    /// <summary>值类型标识，用于 JSON 序列化（"int", "float", "bool", "string"）</summary>
    public string? ValueType { get; set; }

    /// <summary>采集时间（UTC）</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>数据质量</summary>
    public DataQuality Quality { get; set; } = DataQuality.Good;

    /// <summary>多租户伏笔</summary>
    public string? TenantId { get; set; }

    /// <summary>扩展属性</summary>
    public Dictionary<string, object>? Properties { get; set; }

    public static DataPoint Good(string deviceId, string tag, object? value, DateTime timestamp, string? tenantId = null)
    {
        return new DataPoint
        {
            DeviceId = deviceId,
            Tag = tag,
            Value = value,
            ValueType = value?.GetType().Name.ToLowerInvariant(),
            Timestamp = timestamp,
            Quality = DataQuality.Good,
            TenantId = tenantId
        };
    }

    public static DataPoint Bad(string deviceId, string tag, DateTime timestamp, string? tenantId = null)
    {
        return new DataPoint
        {
            DeviceId = deviceId,
            Tag = tag,
            Value = null,
            ValueType = null,
            Timestamp = timestamp,
            Quality = DataQuality.Bad,
            TenantId = tenantId
        };
    }
}
