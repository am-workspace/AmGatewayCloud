using AmGatewayCloud.Shared.Configuration;

namespace AmGatewayCloud.AlarmService.Configuration;

/// <summary>
/// 报警服务配置
/// </summary>
public class AlarmServiceConfig
{
    public string TenantId { get; set; } = "default";
    public int EvaluationIntervalSeconds { get; set; } = 5;
    public int EvaluationLookbackSeconds { get; set; } = 30;
    public int MaxConsecutiveErrors { get; set; } = 10;
    public int RuleCacheRefreshSeconds { get; set; } = 30;
    public int MaxQueryWindowHours { get; set; } = 1;
    public int DeviceOfflineThresholdMinutes { get; set; } = 10;
    public TimescaleDbConfig TimescaleDb { get; set; } = new();
    public PostgreSqlConfig PostgreSql { get; set; } = new();
    public RabbitMqConfig RabbitMq { get; set; } = new();
}
