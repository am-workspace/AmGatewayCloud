namespace AmGatewayCloud.CloudGateway.Models;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FactoryId { get; set; } = string.Empty;
    public string? BatchId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RawPayloadPreview { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
