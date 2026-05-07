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

    public static DataPoint Good(string deviceId, string tag, object? value, DateTime timestamp, string? tenantId = null, string? groupName = null)
    {
        return new DataPoint
        {
            DeviceId = deviceId,
            Tag = tag,
            Value = value,
            ValueType = value?.GetType().Name.ToLowerInvariant(),
            Timestamp = timestamp,
            Quality = DataQuality.Good,
            TenantId = tenantId,
            Properties = groupName is not null ? new Dictionary<string, object> { ["GroupName"] = groupName } : null
        };
    }

    /// <summary>
    /// 创建质量为 Bad 的数据点（值为 null，表示读取失败）。
    /// </summary>
    /// <param name="deviceId">设备标识</param>
    /// <param name="tag">数据标签名</param>
    /// <param name="timestamp">采集时间（UTC）</param>
    /// <param name="tenantId">租户标识（可选）</param>
    /// <param name="groupName">寄存器组名称（写入 Properties.GroupName，可选）</param>
    /// <returns>Bad 质量的 DataPoint</returns>
    public static DataPoint Bad(string deviceId, string tag, DateTime timestamp, string? tenantId = null, string? groupName = null)
    {
        return new DataPoint
        {
            DeviceId = deviceId,
            Tag = tag,
            Value = null,
            ValueType = null,
            Timestamp = timestamp,
            Quality = DataQuality.Bad,
            TenantId = tenantId,
            Properties = groupName is not null ? new Dictionary<string, object> { ["GroupName"] = groupName } : null
        };
    }
}
