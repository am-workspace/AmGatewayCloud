namespace AmGatewayCloud.Shared.Tenant;

public class TenantContext : ITenantContext
{
    public string TenantId { get; }
    public bool IsAvailable => !string.IsNullOrEmpty(TenantId);

    public TenantContext(string tenantId)
    {
        TenantId = tenantId ?? string.Empty;
    }
}
