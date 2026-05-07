using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmGatewayCloud.CloudGateway.Models;

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
    /// 阶段6 OpenTelemetry 预留：边缘端注入的 TraceParent
    /// </summary>
    [JsonPropertyName("traceParent")]
    public string? TraceParent { get; set; }
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
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("groupName")]
    public string? GroupName { get; set; }
}
