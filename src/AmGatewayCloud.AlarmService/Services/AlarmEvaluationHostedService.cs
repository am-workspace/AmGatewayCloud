using AmGatewayCloud.AlarmService.Configuration;
using AmGatewayCloud.AlarmService.Models;
using Npgsql;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// 报警评估主循环：定时拉取 TimescaleDB 最新数据，评估报警规则，生成/恢复报警事件
/// </summary>
public class AlarmEvaluationHostedService : BackgroundService
{
    private readonly AlarmServiceConfig _config;
    private readonly RuleEvaluator _ruleEvaluator;
    private readonly CooldownManager _cooldownManager;
    private readonly AlarmEventRepository _alarmRepo;
    private readonly AlarmRuleRepository _ruleRepo;
    private readonly TimescaleDbReader _timescaleReader;
    private readonly AlarmEventPublisher _publisher;
    private readonly ILogger<AlarmEvaluationHostedService> _logger;

    private DateTimeOffset _lastEvalTime;
    private List<AlarmRule> _ruleCache = [];
    private DateTimeOffset _ruleCacheTime = DateTimeOffset.MinValue;
    private int _consecutiveErrors;

    public DateTimeOffset LastEvalTime => _lastEvalTime;
    public int ConsecutiveErrors => _consecutiveErrors;

    public AlarmEvaluationHostedService(
        AlarmServiceConfig config,
        RuleEvaluator ruleEvaluator,
        CooldownManager cooldownManager,
        AlarmEventRepository alarmRepo,
        AlarmRuleRepository ruleRepo,
        TimescaleDbReader timescaleReader,
        AlarmEventPublisher publisher,
        ILogger<AlarmEvaluationHostedService> logger)
    {
        _config = config;
        _ruleEvaluator = ruleEvaluator;
        _cooldownManager = cooldownManager;
        _alarmRepo = alarmRepo;
        _ruleRepo = ruleRepo;
        _timescaleReader = timescaleReader;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "AlarmService starting - Tenant: {Tenant}, EvalInterval: {Interval}s",
            _config.TenantId, _config.EvaluationIntervalSeconds);

        _lastEvalTime = await GetLastEvalTimeAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync(ct);
                await StaleCheckAsync(ct);
                _consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                _consecutiveErrors++;
                if (_consecutiveErrors >= _config.MaxConsecutiveErrors)
                {
                    _logger.LogCritical(ex,
                        "Alarm evaluation has failed {Count} consecutive times (threshold: {Max})",
                        _consecutiveErrors, _config.MaxConsecutiveErrors);
                }
                else
                {
                    _logger.LogError(ex, "Alarm evaluation cycle failed (consecutive: {Count})", _consecutiveErrors);
                }
            }

            _cooldownManager.Cleanup();
            await Task.Delay(TimeSpan.FromSeconds(_config.EvaluationIntervalSeconds), ct);
        }
    }

    private async Task<DateTimeOffset> GetLastEvalTimeAsync(CancellationToken ct)
    {
        try
        {
            var lastTrigger = await _alarmRepo.GetLastTriggerTimeAsync(ct);
            if (lastTrigger.HasValue)
            {
                _logger.LogInformation("Cold start: last alarm trigger at {Time}", lastTrigger.Value);
                return lastTrigger.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get last alarm trigger time, using lookback");
        }

        var lookback = DateTimeOffset.UtcNow.AddSeconds(-_config.EvaluationLookbackSeconds);
        _logger.LogInformation("Cold start: no previous alarms, using lookback {Time}", lookback);
        return lookback;
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        var querySince = _lastEvalTime > DateTimeOffset.UtcNow.AddHours(-_config.MaxQueryWindowHours)
            ? _lastEvalTime
            : DateTimeOffset.UtcNow.AddHours(-_config.MaxQueryWindowHours);
        var dataPoints = await _timescaleReader.GetLatestDataAsync(querySince, ct);

        if (dataPoints.Count == 0) return;

        if ((DateTimeOffset.UtcNow - _ruleCacheTime).TotalSeconds >= _config.RuleCacheRefreshSeconds)
        {
            try
            {
                _ruleCache = await _ruleRepo.GetEnabledRulesAsync(ct);
                _ruleCacheTime = DateTimeOffset.UtcNow;
                _publisher.UpdateRuleNameCache(_ruleCache);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh rule cache, using previous rules");
            }
        }
        var rules = _ruleCache;

        int triggeredCount = 0;
        int clearedCount = 0;

        foreach (var point in dataPoints)
        {
            await _alarmRepo.ClearStaleFlagAsync(point.DeviceId, ct);

            var matchedRules = rules.Where(r => r.Tag == point.Tag && IsScopeMatch(r, point));

            foreach (var rule in matchedRules)
            {
                if (_ruleEvaluator.Evaluate(point, rule))
                {
                    var existing = await _alarmRepo.GetActiveAlarmAsync(rule.Id, point.DeviceId, ct);
                    if (existing != null) continue;

                    var suppressed = await _alarmRepo.GetSuppressedAlarmAsync(rule.Id, point.DeviceId, ct);
                    if (suppressed != null) continue;

                    if (_cooldownManager.IsInCooldown(rule.Id, point.DeviceId, rule.CooldownMinutes)) continue;

                    var alarmEvent = CreateAlarmEvent(rule, point);
                    try
                    {
                        await _alarmRepo.InsertAsync(alarmEvent, ct);
                    }
                    catch (NpgsqlException ex) when (ex.SqlState == "23505")
                    {
                        _logger.LogDebug("Duplicate alarm ignored (rule={RuleId}, device={DeviceId})", rule.Id, point.DeviceId);
                        continue;
                    }

                    _cooldownManager.RecordTrigger(rule.Id, point.DeviceId);
                    triggeredCount++;

                    await _publisher.PublishAsync(alarmEvent, _config.RabbitMq.Exchange, ct);

                    _logger.LogInformation(
                        "Alarm triggered: rule={RuleId} device={DeviceId} value={Value:F2}",
                        rule.Id, point.DeviceId, _ruleEvaluator.ExtractValue(point, rule));
                }
                else
                {
                    var cleared = await TryAutoClearAsync(rule, point, ct);
                    if (cleared) clearedCount++;
                }
            }
        }

        _lastEvalTime = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Evaluation cycle: {DataPoints} data points, {Triggered} triggered, {Cleared} cleared",
            dataPoints.Count, triggeredCount, clearedCount);
    }

    private async Task StaleCheckAsync(CancellationToken ct)
    {
        try
        {
            var threshold = DateTimeOffset.UtcNow.AddMinutes(-_config.DeviceOfflineThresholdMinutes);
            var activeAlarms = await _alarmRepo.GetOpenAlarmsAsync(ct);

            foreach (var alarm in activeAlarms)
            {
                if (!alarm.IsStale)
                {
                    var lastDataTime = await _timescaleReader.GetLastDataTimeAsync(alarm.DeviceId, ct);
                    if (lastDataTime < threshold)
                    {
                        await _alarmRepo.MarkStaleAsync(alarm.Id, ct);
                        _logger.LogWarning(
                            "Device {DeviceId} offline, alarm {AlarmId} marked stale",
                            alarm.DeviceId, alarm.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stale check failed");
        }
    }

    private async Task<bool> TryAutoClearAsync(AlarmRule rule, DataPointReadModel point, CancellationToken ct)
    {
        if (!_ruleEvaluator.ShouldClear(point, rule)) return false;

        var activeAlarm = await _alarmRepo.GetActiveAlarmAsync(rule.Id, point.DeviceId, ct);
        if (activeAlarm != null)
        {
            activeAlarm.Status = AlarmStatus.Cleared;
            activeAlarm.ClearedAt = DateTimeOffset.UtcNow;
            activeAlarm.ClearValue = _ruleEvaluator.ExtractValue(point, rule);
            await _alarmRepo.UpdateAsync(activeAlarm, ct);
            _cooldownManager.ResetCooldown(rule.Id, point.DeviceId);

            await _publisher.PublishAsync(activeAlarm, _config.RabbitMq.Exchange, ct);

            _logger.LogInformation(
                "Alarm auto-cleared: rule={RuleId} device={DeviceId} clearValue={Value:F2}",
                rule.Id, point.DeviceId, activeAlarm.ClearValue);
            return true;
        }

        var suppressedAlarm = await _alarmRepo.GetSuppressedAlarmAsync(rule.Id, point.DeviceId, ct);
        if (suppressedAlarm != null)
        {
            suppressedAlarm.Status = AlarmStatus.Cleared;
            suppressedAlarm.ClearedAt = DateTimeOffset.UtcNow;
            suppressedAlarm.ClearValue = _ruleEvaluator.ExtractValue(point, rule);
            await _alarmRepo.UpdateAsync(suppressedAlarm, ct);
            _cooldownManager.ResetCooldown(rule.Id, point.DeviceId);

            await _publisher.PublishAsync(suppressedAlarm, _config.RabbitMq.Exchange, ct);

            _logger.LogInformation(
                "Suppressed alarm auto-cleared: rule={RuleId} device={DeviceId}",
                rule.Id, point.DeviceId);
            return true;
        }

        return false;
    }

    private static bool IsScopeMatch(AlarmRule rule, DataPointReadModel point)
    {
        if (rule.DeviceId is not null)
            return rule.DeviceId == point.DeviceId
                && (rule.FactoryId == null || rule.FactoryId == point.FactoryId);

        if (rule.FactoryId is not null)
            return rule.FactoryId == point.FactoryId;

        return true;
    }

    private AlarmEvent CreateAlarmEvent(AlarmRule rule, DataPointReadModel point)
    {
        var triggerValue = _ruleEvaluator.ExtractValue(point, rule);
        var message = rule.Description ?? $"{rule.Name}: {point.Tag} {rule.Operator} {rule.Threshold}";

        return new AlarmEvent
        {
            Id = Guid.NewGuid(),
            RuleId = rule.Id,
            TenantId = rule.TenantId,
            FactoryId = point.FactoryId,
            WorkshopId = point.WorkshopId,
            DeviceId = point.DeviceId,
            Tag = point.Tag,
            TriggerValue = triggerValue,
            Level = rule.Level,
            Status = AlarmStatus.Active,
            IsStale = false,
            Message = message,
            TriggeredAt = point.Time
        };
    }
}
