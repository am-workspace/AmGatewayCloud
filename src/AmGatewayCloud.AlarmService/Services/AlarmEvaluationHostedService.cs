using AmGatewayCloud.AlarmService.Configuration;
using AmGatewayCloud.AlarmService.Models;
using AmGatewayCloud.AlarmDomain.Aggregates.Alarm;
using AmGatewayCloud.AlarmInfrastructure.Repositories;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace AmGatewayCloud.AlarmService.Services;

using DomainAlarm = AmGatewayCloud.AlarmDomain.Aggregates.Alarm.Alarm;
using DomainAlarmRule = AmGatewayCloud.AlarmDomain.Aggregates.Alarm.AlarmRule;

/// <summary>
/// 报警评估主循环：定时拉取 TimescaleDB 最新数据，评估报警规则，生成/恢复报警事件
/// </summary>
public class AlarmEvaluationHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly AlarmServiceConfig _config;
    private readonly RuleEvaluator _ruleEvaluator;
    private readonly CooldownManager _cooldownManager;
    private readonly DelayTracker _delayTracker;
    private readonly TimescaleDbReader _timescaleReader;
    private readonly AlarmEventPublisher _publisher;
    private readonly ILogger<AlarmEvaluationHostedService> _logger;

    private DateTimeOffset _lastEvalTime;
    private List<DomainAlarmRule> _ruleCache = [];
    private DateTimeOffset _ruleCacheTime = DateTimeOffset.MinValue;
    private int _consecutiveErrors;

    public DateTimeOffset LastEvalTime => _lastEvalTime;
    public int ConsecutiveErrors => _consecutiveErrors;

    public AlarmEvaluationHostedService(
        IServiceProvider serviceProvider,
        AlarmServiceConfig config,
        RuleEvaluator ruleEvaluator,
        CooldownManager cooldownManager,
        DelayTracker delayTracker,
        TimescaleDbReader timescaleReader,
        AlarmEventPublisher publisher,
        ILogger<AlarmEvaluationHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _config = config;
        _ruleEvaluator = ruleEvaluator;
        _cooldownManager = cooldownManager;
        _delayTracker = delayTracker;
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
            _delayTracker.Cleanup();
            await Task.Delay(TimeSpan.FromSeconds(_config.EvaluationIntervalSeconds), ct);
        }
    }

    private async Task<DateTimeOffset> GetLastEvalTimeAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var alarmRepo = scope.ServiceProvider.GetRequiredService<AlarmEventRepository>();
            var lastTrigger = await alarmRepo.GetLastTriggerTimeAsync(ct);
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
                using var scope = _serviceProvider.CreateScope();
                var ruleRepo = scope.ServiceProvider.GetRequiredService<AlarmRuleRepository>();
                _ruleCache = await ruleRepo.GetEnabledRulesAsync(ct);
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

        using (var evalScope = _serviceProvider.CreateScope())
        {
            var alarmRepo = evalScope.ServiceProvider.GetRequiredService<AlarmEventRepository>();
            var mediator = evalScope.ServiceProvider.GetRequiredService<IMediator>();

            foreach (var point in dataPoints)
            {
                await alarmRepo.ClearStaleFlagAsync(point.DeviceId, ct);

                var matchedRules = rules.Where(r => r.Tag == point.Tag && r.TenantId == point.TenantId && IsScopeMatch(r, point));

                foreach (var rule in matchedRules)
                {
                    if (_ruleEvaluator.Evaluate(point, rule))
                    {
                        // 延时检查：条件持续满足 DelaySeconds 后才触发
                        if (!_delayTracker.IsDelayElapsed(rule.Id, point.DeviceId, rule.DelaySeconds))
                            continue;

                        var existing = await alarmRepo.GetActiveAlarmAsync(rule.Id, point.DeviceId, ct);
                        if (existing != null) continue;

                        var suppressed = await alarmRepo.GetSuppressedAlarmAsync(rule.Id, point.DeviceId, ct);
                        if (suppressed != null) continue;

                        if (_cooldownManager.IsInCooldown(rule.Id, point.DeviceId, rule.CooldownMinutes)) continue;

                        var alarm = DomainAlarm.Create(
                            rule.Id, rule.TenantId, point.FactoryId, point.WorkshopId,
                            point.DeviceId, point.Tag,
                            _ruleEvaluator.ExtractValue(point, rule),
                            rule.Level,
                            rule.Description ?? $"{rule.Name}: {rule.OperatorString} {rule.Threshold}",
                            point.Time);

                        try
                        {
                            await alarmRepo.InsertAsync(alarm, ct);
                        }
                        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                        {
                            _logger.LogDebug("Duplicate alarm ignored (rule={RuleId}, device={DeviceId})", rule.Id, point.DeviceId);
                            continue;
                        }

                        _cooldownManager.RecordTrigger(rule.Id, point.DeviceId);
                        _delayTracker.RecordTriggered(rule.Id, point.DeviceId);
                        triggeredCount++;

                        // 发布领域事件（通过 MediatR）
                        foreach (var domainEvent in alarm.DomainEvents)
                            await mediator.Publish(domainEvent, ct);
                        alarm.ClearDomainEvents();

                        // 发布到 RabbitMQ（桥接）
                        await _publisher.PublishAsync(alarm, _config.RabbitMq.Exchange, ct);

                        _logger.LogInformation(
                            "Alarm triggered: rule={RuleId} device={DeviceId} value={Value:F2}",
                            rule.Id, point.DeviceId, _ruleEvaluator.ExtractValue(point, rule));
                    }
                    else
                    {
                        // 条件不再满足，清除延时记录（下次重新计时）
                        _delayTracker.ClearDelay(rule.Id, point.DeviceId);

                        var cleared = await TryAutoClearAsync(alarmRepo, mediator, rule, point, ct);
                        if (cleared) clearedCount++;
                    }
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

            using var scope = _serviceProvider.CreateScope();
            var alarmRepo = scope.ServiceProvider.GetRequiredService<AlarmEventRepository>();
            var activeAlarms = await alarmRepo.GetOpenAlarmsAsync(ct);

            foreach (var alarm in activeAlarms)
            {
                if (!alarm.IsStale)
                {
                    var lastDataTime = await _timescaleReader.GetLastDataTimeAsync(alarm.DeviceId, ct);
                    if (lastDataTime < threshold)
                    {
                        alarm.MarkStale();
                        await alarmRepo.UpdateAsync(alarm, ct);
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

    private async Task<bool> TryAutoClearAsync(AlarmEventRepository alarmRepo, IMediator mediator, DomainAlarmRule rule, DataPointReadModel point, CancellationToken ct)
    {
        if (!_ruleEvaluator.ShouldClear(point, rule)) return false;

        var activeAlarm = await alarmRepo.GetActiveAlarmAsync(rule.Id, point.DeviceId, ct);
        if (activeAlarm != null)
        {
            activeAlarm.AutoClear(_ruleEvaluator.ExtractValue(point, rule));
            await alarmRepo.UpdateAsync(activeAlarm, ct);
            _cooldownManager.ResetCooldown(rule.Id, point.DeviceId);

            foreach (var domainEvent in activeAlarm.DomainEvents)
                await mediator.Publish(domainEvent, ct);
            activeAlarm.ClearDomainEvents();

            await _publisher.PublishAsync(activeAlarm, _config.RabbitMq.Exchange, ct);

            _logger.LogInformation(
                "Alarm auto-cleared: rule={RuleId} device={DeviceId} clearValue={Value:F2}",
                rule.Id, point.DeviceId, activeAlarm.ClearValue);
            return true;
        }

        var suppressedAlarm = await alarmRepo.GetSuppressedAlarmAsync(rule.Id, point.DeviceId, ct);
        if (suppressedAlarm != null)
        {
            suppressedAlarm.AutoClear(_ruleEvaluator.ExtractValue(point, rule));
            await alarmRepo.UpdateAsync(suppressedAlarm, ct);
            _cooldownManager.ResetCooldown(rule.Id, point.DeviceId);

            foreach (var domainEvent in suppressedAlarm.DomainEvents)
                await mediator.Publish(domainEvent, ct);
            suppressedAlarm.ClearDomainEvents();

            await _publisher.PublishAsync(suppressedAlarm, _config.RabbitMq.Exchange, ct);

            _logger.LogInformation(
                "Suppressed alarm auto-cleared: rule={RuleId} device={DeviceId}",
                rule.Id, point.DeviceId);
            return true;
        }

        return false;
    }

    private static bool IsScopeMatch(DomainAlarmRule rule, DataPointReadModel point)
    {
        if (rule.DeviceId is not null)
            return rule.DeviceId == point.DeviceId
                && (rule.FactoryId == null || rule.FactoryId == point.FactoryId);

        if (rule.FactoryId is not null)
            return rule.FactoryId == point.FactoryId;

        return true;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is Npgsql.NpgsqlException pgEx && pgEx.SqlState == "23505";
    }
}
