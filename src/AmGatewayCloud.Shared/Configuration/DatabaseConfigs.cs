namespace AmGatewayCloud.Shared.Configuration;

/// <summary>
/// PostgreSQL 连接配置（业务库）
/// </summary>
public class PostgreSqlConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SslMode { get; set; } = "Disable";

    public string ConnectionString =>
        $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password};SSL Mode={SslMode}";
}

/// <summary>
/// TimescaleDB 连接配置（时序库）
/// </summary>
public class TimescaleDbConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string SslMode { get; set; } = "Disable";

    public string ConnectionString =>
        $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password};SSL Mode={SslMode}";
}

/// <summary>
/// RabbitMQ 连接配置
/// </summary>
public class RabbitMqConfig
{
    public string HostName { get; set; } = string.Empty;
    public int Port { get; set; } = 5672;
    public bool UseSsl { get; set; }
    public string VirtualHost { get; set; } = "/business";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Exchange { get; set; } = "amgateway.alarms";
    public string QueueName { get; set; } = "amgateway.alarm-notifications";
    public int ReconnectDelayMs { get; set; } = 5000;
    public int MaxReconnectDelayMs { get; set; } = 60000;
}
