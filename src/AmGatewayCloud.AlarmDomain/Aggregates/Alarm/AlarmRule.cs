namespace AmGatewayCloud.AlarmDomain.Aggregates.Alarm;

/// <summary>
/// 报警规则领域实体（聚合内实体，非独立聚合根）
/// </summary>
public class AlarmRule
{
    public string Id { get; private set; }
    public string Name { get; private set; }
    public string TenantId { get; private set; }
    public string? FactoryId { get; private set; }
    public string? DeviceId { get; private set; }
    public string Tag { get; private set; }
    public OperatorType Operator { get; private set; }
    public double Threshold { get; private set; }
    public string? ThresholdString { get; private set; }
    public double? ClearThreshold { get; private set; }
    public AlarmLevel Level { get; private set; }
    public int CooldownMinutes { get; private set; }
    public int DelaySeconds { get; private set; }
    public bool Enabled { get; private set; }
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public AlarmRule(
        string id, string name, string tenantId, string? factoryId, string? deviceId,
        string tag, OperatorType op, double threshold, string? thresholdString,
        double? clearThreshold, AlarmLevel level, int cooldownMinutes, int delaySeconds,
        bool enabled, string? description, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        Id = id;
        Name = name;
        TenantId = tenantId;
        FactoryId = factoryId;
        DeviceId = deviceId;
        Tag = tag;
        Operator = op;
        Threshold = threshold;
        ThresholdString = thresholdString;
        ClearThreshold = clearThreshold;
        Level = level;
        CooldownMinutes = cooldownMinutes;
        DelaySeconds = delaySeconds;
        Enabled = enabled;
        Description = description;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// 获取运算符字符串表示（用于兼容现有字符串存储）
    /// </summary>
    public string OperatorString => Operator switch
    {
        OperatorType.GreaterThan => ">",
        OperatorType.GreaterThanOrEqual => ">=",
        OperatorType.LessThan => "<",
        OperatorType.LessThanOrEqual => "<=",
        OperatorType.Equal => "==",
        OperatorType.NotEqual => "!=",
        _ => ">"
    };

    /// <summary>
    /// 获取级别字符串表示（用于兼容现有字符串存储）
    /// </summary>
    public string LevelString => Level.ToString();

    /// <summary>
    /// 从字符串运算符创建 OperatorType
    /// </summary>
    public static OperatorType ParseOperator(string op) => op switch
    {
        ">" => OperatorType.GreaterThan,
        ">=" => OperatorType.GreaterThanOrEqual,
        "<" => OperatorType.LessThan,
        "<=" => OperatorType.LessThanOrEqual,
        "==" => OperatorType.Equal,
        "!=" => OperatorType.NotEqual,
        _ => throw new ArgumentException($"Invalid operator: {op}")
    };

    /// <summary>
    /// 从字符串级别创建 AlarmLevel
    /// </summary>
    public static AlarmLevel ParseLevel(string level) => Enum.TryParse<AlarmLevel>(level, ignoreCase: true, out var result)
        ? result
        : AlarmLevel.Warning;
}
