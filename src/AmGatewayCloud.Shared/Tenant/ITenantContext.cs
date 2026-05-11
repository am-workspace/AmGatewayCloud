namespace AmGatewayCloud.Shared.Tenant;

/// <summary>
/// 当前请求的租户上下文（Scoped 生命周期）
/// </summary>
public interface ITenantContext
{
    string TenantId { get; }
    bool IsAvailable { get; }
}
