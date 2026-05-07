namespace AmGatewayCloud.Collector.Modbus.Configuration;

/// <summary>
/// MQTT 输出通道配置：Broker 地址、Topic 前缀、认证、重连参数。
/// </summary>
public class MqttConfig
{
    /// <summary>是否启用 MQTT 输出</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Broker 地址</summary>
    public string Broker { get; set; } = "localhost";

    /// <summary>Broker 端口</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 65535)]
    public int Port { get; set; } = 1883;

    /// <summary>Topic 前缀，最终 Topic 格式：{TopicPrefix}/modbus/{DeviceId}</summary>
    public string TopicPrefix { get; set; } = "amgateway";

    /// <summary>客户端标识</summary>
    public string ClientId { get; set; } = "AmGatewayCloud-Modbus";

    /// <summary>用户名（可选）</summary>
    public string? Username { get; set; }

    /// <summary>密码（可选）</summary>
    public string? Password { get; set; }

    /// <summary>重连间隔（毫秒），默认 5s</summary>
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    public int ReconnectDelayMs { get; set; } = 5000;

    /// <summary>最大重连间隔（毫秒），用于指数退避上限，默认 60s</summary>
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    public int MaxReconnectDelayMs { get; set; } = 60_000;

    /// <summary>是否使用 TLS</summary>
    public bool UseTls { get; set; } = false;
}
