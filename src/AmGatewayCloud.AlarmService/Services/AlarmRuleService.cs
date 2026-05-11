using AmGatewayCloud.AlarmDomain.Aggregates.Alarm;
using AmGatewayCloud.AlarmDomain.Services;
using AmGatewayCloud.AlarmInfrastructure.Repositories;
using AmGatewayCloud.Shared.Constants;
using AmGatewayCloud.Shared.DTOs;
using AmGatewayCloud.Shared.Tenant;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// 报警规则管理服务：规则 CRUD + ClearThreshold 校验
/// </summary>
public class AlarmRuleService
{
    private readonly AlarmRuleRepository _ruleRepo;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<AlarmRuleService> _logger;

    public AlarmRuleService(AlarmRuleRepository ruleRepo, ITenantContext tenantContext, ILogger<AlarmRuleService> logger)
    {
        _ruleRepo = ruleRepo;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>
    /// 查询规则列表（支持 factoryId / tag 过滤）
    /// </summary>
    public async Task<List<AlarmRuleDto>> GetRulesAsync(string? factoryId, string? tag, CancellationToken ct)
    {
        var allRules = await _ruleRepo.GetAllRulesAsync(ct);

        var filtered = allRules.AsEnumerable();
        if (!string.IsNullOrEmpty(factoryId))
            filtered = filtered.Where(r => r.FactoryId == factoryId);
        if (!string.IsNullOrEmpty(tag))
            filtered = filtered.Where(r => r.Tag == tag);

        return filtered.Select(MapToDto).ToList();
    }

    /// <summary>
    /// 根据 ID 获取规则
    /// </summary>
    public async Task<AlarmRuleDto?> GetByIdAsync(string ruleId, CancellationToken ct)
    {
        var rule = await _ruleRepo.GetByIdAsync(ruleId, ct);
        return rule is null ? null : MapToDto(rule);
    }

    /// <summary>
    /// 创建报警规则（含合法性校验）
    /// </summary>
    public async Task<(AlarmRuleDto? Rule, string? Error)> CreateRuleAsync(CreateAlarmRuleRequest request, CancellationToken ct)
    {
        if (!AlarmConstants.ValidOperators.Contains(request.Operator))
            return (null, $"Invalid operator '{request.Operator}'. Valid: {string.Join(", ", AlarmConstants.ValidOperators)}");

        if (!AlarmConstants.ValidLevels.Contains(request.Level))
            return (null, $"Invalid level '{request.Level}'. Valid: {string.Join(", ", AlarmConstants.ValidLevels)}");

        var op = AlarmRule.ParseOperator(request.Operator);
        var (valid, validationError) = AlarmDomainService.ValidateClearThresholdWithError(op, request.Threshold, request.ClearThreshold);
        if (!valid)
            return (null, validationError);

        var rule = new AlarmRule(
            request.Id, request.Name, _tenantContext.TenantId, request.FactoryId, request.DeviceId,
            request.Tag, op, request.Threshold, request.ThresholdString,
            request.ClearThreshold, AlarmRule.ParseLevel(request.Level),
            request.CooldownMinutes, request.DelaySeconds,
            request.Enabled, request.Description,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var (createdRule, error) = await _ruleRepo.CreateRuleAsync(rule, ct);
        if (error is not null)
            return (null, error);

        return (createdRule is null ? null : MapToDto(createdRule), null);
    }

    /// <summary>
    /// 更新报警规则（含合法性校验）
    /// </summary>
    public async Task<(AlarmRuleDto? Rule, string? Error)> UpdateRuleAsync(string ruleId, UpdateAlarmRuleRequest request, CancellationToken ct)
    {
        var existing = await _ruleRepo.GetByIdAsync(ruleId, ct);
        if (existing is null)
            return (null, $"Rule '{ruleId}' not found");

        var op = request.Operator is not null ? AlarmRule.ParseOperator(request.Operator) : existing.Operator;
        if (request.Operator is not null && !AlarmConstants.ValidOperators.Contains(request.Operator))
            return (null, $"Invalid operator '{request.Operator}'. Valid: {string.Join(", ", AlarmConstants.ValidOperators)}");

        var levelStr = request.Level ?? existing.LevelString;
        if (request.Level is not null && !AlarmConstants.ValidLevels.Contains(request.Level))
            return (null, $"Invalid level '{request.Level}'. Valid: {string.Join(", ", AlarmConstants.ValidLevels)}");

        var threshold = request.Threshold ?? existing.Threshold;
        var clearThreshold = request.ClearThreshold ?? existing.ClearThreshold;

        var (valid, validationError) = AlarmDomainService.ValidateClearThresholdWithError(op, threshold, clearThreshold);
        if (!valid)
            return (null, validationError);

        var updates = new Dictionary<string, object?>();
        if (request.Name is not null) updates["name"] = request.Name;
        if (request.FactoryId is not null) updates["factory_id"] = request.FactoryId;
        if (request.DeviceId is not null) updates["device_id"] = request.DeviceId;
        if (request.Tag is not null) updates["tag"] = request.Tag;
        if (request.Operator is not null) updates["operator"] = request.Operator;
        if (request.Threshold.HasValue) updates["threshold"] = request.Threshold.Value;
        if (request.ThresholdString is not null) updates["threshold_string"] = request.ThresholdString;
        if (request.ClearThreshold.HasValue) updates["clear_threshold"] = request.ClearThreshold.Value;
        if (request.Level is not null) updates["level"] = request.Level;
        if (request.CooldownMinutes.HasValue) updates["cooldown_minutes"] = request.CooldownMinutes.Value;
        if (request.DelaySeconds.HasValue) updates["delay_seconds"] = request.DelaySeconds.Value;
        if (request.Enabled.HasValue) updates["enabled"] = request.Enabled.Value;
        if (request.Description is not null) updates["description"] = request.Description;

        var updatedRule = await _ruleRepo.UpdateRuleAsync(ruleId, updates, ct);
        return (updatedRule is null ? null : MapToDto(updatedRule), null);
    }

    /// <summary>
    /// 删除报警规则
    /// </summary>
    public async Task<(bool Success, string? Error)> DeleteRuleAsync(string ruleId, CancellationToken ct)
    {
        return await _ruleRepo.DeleteRuleAsync(ruleId, ct);
    }

    private static AlarmRuleDto MapToDto(AlarmRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        TenantId = rule.TenantId,
        FactoryId = rule.FactoryId,
        DeviceId = rule.DeviceId,
        Tag = rule.Tag,
        Operator = rule.OperatorString,
        Threshold = rule.Threshold,
        ThresholdString = rule.ThresholdString,
        ClearThreshold = rule.ClearThreshold,
        Level = rule.LevelString,
        CooldownMinutes = rule.CooldownMinutes,
        DelaySeconds = rule.DelaySeconds,
        Enabled = rule.Enabled,
        Description = rule.Description,
        CreatedAt = rule.CreatedAt,
        UpdatedAt = rule.UpdatedAt
    };
}
