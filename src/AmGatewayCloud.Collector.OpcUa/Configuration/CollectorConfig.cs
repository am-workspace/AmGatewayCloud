namespace AmGatewayCloud.Collector.OpcUa.Configuration;

public class CollectorConfig
{
    /// <summary>设备标识，配置中指定</summary>
    public string DeviceId { get; set; } = "device-001";

    /// <summary>多租户伏笔，阶段6启用</summary>
    public string? TenantId { get; set; }

    /// <summary>工厂标识</summary>
    public string FactoryId { get; set; } = "factory-001";

    /// <summary>车间标识</summary>
    public string WorkshopId { get; set; } = "workshop-001";

    /// <summary>OPC UA 连接配置</summary>
    public OpcUaConfig OpcUa { get; set; } = new();

    /// <summary>MQTT 输出通道配置</summary>
    public MqttConfig Mqtt { get; set; } = new();

    /// <summary>监控节点组列表</summary>
    public List<NodeGroupConfig> NodeGroups { get; set; } = [];
}

public class OpcUaConfig
{
    /// <summary>OPC UA 服务器端点，如 "opc.tcp://localhost:4840"</summary>
    public string Endpoint { get; set; } = "opc.tcp://localhost:4840";

    /// <summary>安全策略：None / Basic128Rsa15 / Basic256 / Basic256Sha256</summary>
    public string SecurityPolicy { get; set; } = "None";

    /// <summary>会话超时（毫秒）</summary>
    public int SessionTimeoutMs { get; set; } = 60000;

    /// <summary>重连间隔（毫秒），首次退避基准值</summary>
    public int ReconnectIntervalMs { get; set; } = 5000;

    /// <summary>自动接受不受信任的证书（开发环境用，生产环境应关闭）</summary>
    public bool AutoAcceptUntrustedCertificates { get; set; } = true;

    /// <summary>订阅发布间隔（毫秒），服务器向客户端推送变化的最小周期</summary>
    public int PublishingIntervalMs { get; set; } = 1000;

    /// <summary>采样间隔（毫秒），服务器对节点采样的最小周期</summary>
    public int SamplingIntervalMs { get; set; } = 500;

    /// <summary>身份验证方式：Anonymous / UserName / Certificate</summary>
    public string AuthMode { get; set; } = "Anonymous";

    /// <summary>用户名（AuthMode=UserName 时必填）</summary>
    public string? UserName { get; set; }

    /// <summary>密码（AuthMode=UserName 时必填）</summary>
    public string? Password { get; set; }

    /// <summary>每个 MonitoredItem 的队列大小，默认 10</summary>
    public uint QueueSize { get; set; } = 10;

    /// <summary>通知刷新间隔（毫秒），控制控制台批量输出频率</summary>
    public int FlushIntervalMs { get; set; } = 200;
}

public class NodeGroupConfig
{
    /// <summary>节点组名称，用于日志和输出分组</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// OPC UA 命名空间 URI，如 "http://amvirtualslave.org/Industrial"。
    /// 用于将 NodeId 字符串解析为带命名空间的 NodeId。
    /// 如果为空，则 NodeId 中必须包含命名空间索引（如 "ns=2;s=xxx"）。
    /// </summary>
    public string? NamespaceUri { get; set; }

    /// <summary>监控节点列表</summary>
    public List<NodeConfig> Nodes { get; set; } = [];
}

public class NodeConfig
{
    /// <summary>
    /// OPC UA 节点标识。
    /// 如果 NodeGroupConfig.NamespaceUri 已设置，此处填短名称（如 "Sensors_Temperature"）；
    /// 否则需填完整 NodeId 字符串（如 "ns=2;s=Sensors_Temperature"）。
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>数据标签名，用于 DataPoint.Tag</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>死区过滤阈值（0.0-100.0%），0 = 禁用（默认）。阶段3+启用</summary>
    public double DeadbandPercent { get; set; } = 0.0;

    /// <summary>是否可写（用于双向通信场景，阶段6+启用）</summary>
    public bool Writable { get; set; } = false;
}
