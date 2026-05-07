using Microsoft.Extensions.Options;

namespace AmGatewayCloud.Collector.OpcUa.Configuration;

/// <summary>
/// OPC UA 采集器配置验证器：校验 DeviceId、OPC UA 连接参数、
/// NodeGroup 唯一性、节点标识/标签完整性及跨组标签重复。
/// </summary>
public class CollectorConfigValidator : IValidateOptions<CollectorConfig>
{
    /// <summary>
    /// 验证 CollectorConfig 配置项的完整性与合法性。
    /// </summary>
    /// <param name="name">选项名称（可为 null）</param>
    /// <param name="config">待验证的配置实例</param>
    /// <returns>验证结果：成功或包含错误列表的失败结果</returns>
    public ValidateOptionsResult Validate(string? name, CollectorConfig config)
    {
        var errors = new List<string>();

        // DeviceId
        if (string.IsNullOrWhiteSpace(config.DeviceId))
            errors.Add("Collector:DeviceId is required");

        // OpcUa config
        ValidateOpcUaConfig(config.OpcUa, errors);

        // NodeGroups
        ValidateNodeGroups(config.NodeGroups, errors);

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// 验证 OPC UA 连接配置：Endpoint 格式、SecurityPolicy 合法值、超时参数范围、身份验证。
    /// </summary>
    /// <param name="config">OPC UA 配置</param>
    /// <param name="errors">错误收集列表</param>
    private static void ValidateOpcUaConfig(OpcUaConfig config, List<string> errors)
    {
        // Endpoint 格式
        if (string.IsNullOrWhiteSpace(config.Endpoint) ||
            !config.Endpoint.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Collector:OpcUa:Endpoint must start with 'opc.tcp://', got '{config.Endpoint}'");
        }

        // SecurityPolicy 合法值
        var validPolicies = new[] { "None", "Basic128Rsa15", "Basic256", "Basic256Sha256" };
        if (!validPolicies.Contains(config.SecurityPolicy, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Collector:OpcUa:SecurityPolicy must be one of [{string.Join("/", validPolicies)}], " +
                       $"got '{config.SecurityPolicy}'");
        }

        // 时间参数范围
        if (config.SessionTimeoutMs <= 0)
            errors.Add("Collector:OpcUa:SessionTimeoutMs must be > 0");
        if (config.ReconnectIntervalMs <= 0)
            errors.Add("Collector:OpcUa:ReconnectIntervalMs must be > 0");
        if (config.PublishingIntervalMs <= 0)
            errors.Add("Collector:OpcUa:PublishingIntervalMs must be > 0");
        if (config.SamplingIntervalMs <= 0)
            errors.Add("Collector:OpcUa:SamplingIntervalMs must be > 0");
        if (config.FlushIntervalMs <= 0)
            errors.Add("Collector:OpcUa:FlushIntervalMs must be > 0");

        // 身份验证
        if (string.Equals(config.AuthMode, "UserName", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(config.UserName))
        {
            errors.Add("Collector:OpcUa:UserName is required when AuthMode is 'UserName'");
        }
    }

    /// <summary>
    /// 验证 NodeGroup 配置：组名唯一性、节点列表非空、NodeId/Tag 唯一且非空、跨组 Tag 重复警告。
    /// </summary>
    /// <param name="groups">节点组列表</param>
    /// <param name="errors">错误收集列表</param>
    private static void ValidateNodeGroups(List<NodeGroupConfig> groups, List<string> errors)
    {
        if (groups.Count == 0)
        {
            errors.Add("Collector:NodeGroups must have at least one group");
            return;
        }

        // NodeGroup.Name 唯一
        var duplicateNames = groups
            .Select(g => g.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .GroupBy(n => n)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateNames.Count > 0)
            errors.Add($"NodeGroup names must be unique, duplicates: [{string.Join(", ", duplicateNames)}]");

        foreach (var group in groups)
        {
            var prefix = $"NodeGroup '{group.Name}'";

            if (string.IsNullOrWhiteSpace(group.Name))
                errors.Add("NodeGroup has empty Name");

            if (group.Nodes.Count == 0)
            {
                errors.Add($"{prefix} must have at least one node");
                continue;
            }

            // 同组内 NodeId 唯一
            var duplicateNodeIds = group.Nodes
                .Select(n => n.NodeId)
                .GroupBy(id => id)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateNodeIds.Count > 0)
                errors.Add($"{prefix} has duplicate NodeIds: [{string.Join(", ", duplicateNodeIds)}]");

            // 同组内 Tag 唯一
            var duplicateTags = group.Nodes
                .Select(n => n.Tag)
                .GroupBy(t => t)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateTags.Count > 0)
                errors.Add($"{prefix} has duplicate Tags: [{string.Join(", ", duplicateTags)}]");

            // 逐节点校验
            foreach (var node in group.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.NodeId))
                    errors.Add($"{prefix} has a node with empty NodeId");
                if (string.IsNullOrWhiteSpace(node.Tag))
                    errors.Add($"{prefix} has a node with empty Tag (NodeId: {node.NodeId})");
            }
        }

        // 跨组 Tag 重复警告（不阻止启动，但下游数据可能覆盖）
        var allTags = groups.SelectMany(g => g.Nodes.Select(n => n.Tag))
            .Where(t => !string.IsNullOrEmpty(t))
            .GroupBy(t => t)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (allTags.Count > 0)
            errors.Add($"Warning: duplicate Tags across groups detected: [{string.Join(", ", allTags)}]. " +
                       "Downstream data may be overwritten");
    }
}
