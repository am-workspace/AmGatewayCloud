namespace AmGatewayCloud.Collector.OpcUa.Models;

public class DataPoint
{
    /// <summary>设备标识，配置中指定</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>数据标签名，如 "temperature", "pressure"</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>采集值（动态类型：int/float/bool/string 等）</summary>
    public object? Value { get; set; }

    /// <summary>
    /// 值类型标识，用于 JSON 序列化。
    /// 通过 MapValueType 统一命名，与 Modbus 采集器保持一致。
    /// </summary>
    public string? ValueType { get; set; }

    /// <summary>采集时间（UTC），优先使用 OPC UA SourceTimestamp</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>数据质量</summary>
    public DataQuality Quality { get; set; } = DataQuality.Good;

    /// <summary>多租户伏笔，阶段6启用</summary>
    public string? TenantId { get; set; }

    /// <summary>扩展属性（如 GroupName、ServerTimestamp、NodeId 等）</summary>
    public Dictionary<string, object>? Properties { get; set; }

    // --- 工厂方法 ---

    /// <summary>
    /// 创建质量为 Good 的数据点，自动映射 ValueType。
    /// </summary>
    /// <param name="deviceId">设备标识</param>
    /// <param name="tag">数据标签名</param>
    /// <param name="value">采集值（动态类型）</param>
    /// <param name="timestamp">采集时间（UTC），优先使用 OPC UA SourceTimestamp</param>
    /// <param name="tenantId">租户标识（可选）</param>
    /// <param name="groupName">节点组名称（写入 Properties.GroupName，可选）</param>
    /// <returns>Good 质量的 DataPoint</returns>
    public static DataPoint Good(
        string deviceId, string tag, object? value, DateTime timestamp,
        string? tenantId = null, string? groupName = null)
    {
        return new DataPoint
        {
            DeviceId = deviceId,
            Tag = tag,
            Value = value,
            ValueType = MapValueType(value),
            Timestamp = timestamp,
            Quality = DataQuality.Good,
            TenantId = tenantId,
            Properties = groupName is not null
                ? new Dictionary<string, object> { ["GroupName"] = groupName }
                : null
        };
    }

    /// <summary>
    /// 创建质量为 Bad 的数据点（值为 null，表示读取失败）。
    /// </summary>
    /// <param name="deviceId">设备标识</param>
    /// <param name="tag">数据标签名</param>
    /// <param name="timestamp">采集时间（UTC）</param>
    /// <param name="tenantId">租户标识（可选）</param>
    /// <param name="groupName">节点组名称（写入 Properties.GroupName，可选）</param>
    /// <returns>Bad 质量的 DataPoint</returns>
    public static DataPoint Bad(
        string deviceId, string tag, DateTime timestamp,
        string? tenantId = null, string? groupName = null)
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
            Properties = groupName is not null
                ? new Dictionary<string, object> { ["GroupName"] = groupName }
                : null
        };
    }

    /// <summary>
    /// 统一 ValueType 命名，避免 GetType().Name 的不一致
    /// （如 int→Int32, float→Single, bool→Boolean）。
    /// 此映射应与 Modbus 采集器统一，后续抽取到 Abstractions。
    /// </summary>
    private static string? MapValueType(object? value)
    {
        return value switch
        {
            double => "double",
            float => "float",
            int => "int",
            long => "long",
            short => "short",
            ushort => "ushort",
            uint => "uint",
            bool => "bool",
            string => "string",
            DateTime => "datetime",
            byte => "byte",
            null => null,
            _ => value.GetType().Name.ToLowerInvariant()
        };
    }
}
