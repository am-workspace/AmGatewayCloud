namespace AmGatewayCloud.EdgeGateway.Configuration;

public class EdgeGatewayConfig
{
    public string HubId { get; set; } = "edgehub-001";
    public string FactoryId { get; set; } = "factory-001";
    public string WorkshopId { get; set; } = "workshop-001";
    public string? TenantId { get; set; }

    public MqttConsumerConfig Mqtt { get; set; } = new();
    public InfluxDbConfig InfluxDb { get; set; } = new();
    public RabbitMqConfig RabbitMq { get; set; } = new();
}

public class MqttConsumerConfig
{
    public string Broker { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string TopicFilter { get; set; } = "amgateway/#";
    public bool UseSharedSubscription { get; set; } = false;
    public string SharedGroup { get; set; } = "edgehub";
    public string ClientId { get; set; } = "AmGatewayCloud-EdgeHub";
    public bool UseTls { get; set; } = false;
    public int QoS { get; set; } = 1;
    public int KeepAliveSeconds { get; set; } = 60;
    public bool CleanSession { get; set; } = false;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class InfluxDbConfig
{
    public string Url { get; set; } = "http://localhost:8086";
    public string Token { get; set; } = string.Empty;
    public string Org { get; set; } = "my-org";
    public string Bucket { get; set; } = "edge-data";
    public int RetentionHours { get; set; } = 168;
    public int BatchSize { get; set; } = 100;
    public int FlushIntervalMs { get; set; } = 1000;
}

public class RabbitMqConfig
{
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; } = 5671;
    public bool UseSsl { get; set; } = true;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Exchange { get; set; } = "amgateway.topic";
    public string QueueName { get; set; } = "amgateway.{factoryId}";
    public string RoutingKeyTemplate { get; set; } = "amgateway.{factoryId}.{workshopId}.{deviceId}.{protocol}";
    public int ReconnectDelayMs { get; set; } = 5000;
    public int MaxReconnectDelayMs { get; set; } = 60000;
    public ushort PrefetchCount { get; set; } = 50;
}
