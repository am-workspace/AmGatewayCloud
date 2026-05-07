using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmGatewayCloud.EdgeGateway.Models;

/// <summary>
/// MQTT 跨服务数据契约 — Batch 级模型
/// 对应 .contract/mqtt-contract.md v1.0
/// </summary>
public class DataBatch
{
    [JsonPropertyName("batchId")]
    public Guid BatchId { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("factoryId")]
    public string FactoryId { get; set; } = string.Empty;

    [JsonPropertyName("workshopId")]
    public string WorkshopId { get; set; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("points")]
    public List<DataPoint> Points { get; set; } = [];

    /// <summary>
    /// 边缘网关收到该批次的时间（UTC），用于检测采集器时钟漂移
    /// 非契约字段，由 EdgeGateway 在接收时赋值
    /// </summary>
    public DateTimeOffset ReceivedAt { get; set; }
}

public class DataPoint
{
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }

    [JsonPropertyName("valueType")]
    public string ValueType { get; set; } = string.Empty;

    [JsonPropertyName("quality")]
    public string Quality { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("groupName")]
    public string? GroupName { get; set; }
}
