using AmGatewayCloud.Shared.Configuration;

namespace AmGatewayCloud.WebApi.Configuration;

/// <summary>
/// WebApi 配置（BFF 模式：只需 RabbitMQ + CORS，不再需要数据库连接）
/// </summary>
public class WebApiConfig
{
    public string[] CorsOrigins { get; set; } = ["http://localhost:5173"];
    public RabbitMqConfig RabbitMq { get; set; } = new();
}
