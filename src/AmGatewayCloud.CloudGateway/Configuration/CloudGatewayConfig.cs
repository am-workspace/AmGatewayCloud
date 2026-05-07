namespace AmGatewayCloud.CloudGateway.Configuration;

public class CloudGatewayConfig
{
    public string TenantId { get; set; } = "default";
    public TenantResolutionMode TenantResolutionMode { get; set; } = TenantResolutionMode.Static;
    public TimescaleDbConfig TimescaleDb { get; set; } = new();
    public PostgreSqlConfig PostgreSql { get; set; } = new();
    public RabbitMqConfig RabbitMq { get; set; } = new();
    public List<FactoryConsumerConfig> Factories { get; set; } = [];
}

public enum TenantResolutionMode
{
    Static,
    FromMessage
}

public class TimescaleDbConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SslMode { get; set; } = "Require";
    public int BatchSize { get; set; } = 1000;
    public int FlushIntervalMs { get; set; } = 5000;
}

public class PostgreSqlConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SslMode { get; set; } = "Require";
}

public class RabbitMqConfig
{
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; } = 5671;
    public bool UseSsl { get; set; } = true;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public ushort PrefetchCount { get; set; } = 100;
    public int ReconnectDelayMs { get; set; } = 5000;
    public int MaxReconnectDelayMs { get; set; } = 60000;
}

public class FactoryConsumerConfig
{
    public string FactoryId { get; set; } = string.Empty;
    public string QueueName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
