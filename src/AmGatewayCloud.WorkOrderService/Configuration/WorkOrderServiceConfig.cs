using AmGatewayCloud.Shared.Configuration;

namespace AmGatewayCloud.WorkOrderService.Configuration;

/// <summary>
/// 工单服务配置
/// </summary>
public class WorkOrderServiceConfig
{
    public string TenantId { get; set; } = "default";
    public bool AutoCreateOnAlarm { get; set; } = true;
    public PostgreSqlConfig PostgreSql { get; set; } = new();
    public RabbitMqConfig RabbitMq { get; set; } = new();
}
