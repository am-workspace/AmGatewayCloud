using System.Collections.Concurrent;
using System.Text.Json;
using AmGatewayCloud.Shared.Messages;
using AmGatewayCloud.AlarmService.Infrastructure;
using AmGatewayCloud.AlarmDomain.Aggregates.Alarm;
using AlarmRuleDomain = AmGatewayCloud.AlarmDomain.Aggregates.Alarm.AlarmRule;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// 报警事件发布器：将报警事件发布到 RabbitMQ
/// 支持 Domain.Alarm 聚合根
/// </summary>
public class AlarmEventPublisher
{
    private readonly RabbitMqConnectionManager _connectionManager;
    private readonly ILogger<AlarmEventPublisher> _logger;

    private readonly ConcurrentDictionary<string, AlarmRuleDomain> _ruleCache = new();

    public AlarmEventPublisher(
        RabbitMqConnectionManager connectionManager,
        ILogger<AlarmEventPublisher> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public void UpdateRuleNameCache(IEnumerable<AlarmRuleDomain> rules)
    {
        _ruleCache.Clear();
        foreach (var rule in rules)
            _ruleCache[rule.Id] = rule;
    }

    public string? GetRuleName(string ruleId)
        => _ruleCache.GetValueOrDefault(ruleId)?.Name;

    /// <summary>
    /// 发布 Domain.Alarm 聚合根到 RabbitMQ
    /// </summary>
    public async Task PublishAsync(Alarm alarm, string exchange, CancellationToken ct)
    {
        try
        {
            var channel = await _connectionManager.GetChannelAsync(ct);

            var rule = _ruleCache.GetValueOrDefault(alarm.RuleId);
            var message = new AlarmEventMessage
            {
                Id = alarm.Id,
                RuleId = alarm.RuleId,
                RuleName = rule?.Name ?? alarm.RuleId,
                TenantId = alarm.TenantId,
                FactoryId = alarm.FactoryId,
                WorkshopId = alarm.WorkshopId,
                DeviceId = alarm.DeviceId,
                Tag = alarm.Tag,
                Operator = rule?.OperatorString ?? ">",
                Threshold = rule?.Threshold ?? 0,
                ThresholdString = rule?.ThresholdString,
                TriggerValue = alarm.TriggerValue,
                Level = alarm.LevelString,
                Status = alarm.StatusString,
                IsStale = alarm.IsStale,
                Message = alarm.Message,
                TriggeredAt = alarm.TriggeredAt,
                SuppressedAt = alarm.SuppressedAt,
                SuppressedBy = alarm.SuppressedBy,
                SuppressedReason = alarm.SuppressedReason,
                ClearedAt = alarm.ClearedAt,
                ClearValue = alarm.ClearValue
            };

            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            var routingKey = $"alarm.{alarm.TenantId}.{alarm.FactoryId}.{alarm.LevelString.ToLowerInvariant()}";

            channel.BasicPublish(
                exchange: exchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: null,
                body: body);

            _logger.LogInformation(
                "Alarm event published: {RoutingKey}, alarmId={AlarmId}, status={Status}",
                routingKey, alarm.Id, alarm.StatusString);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish alarm event {AlarmId} to RabbitMQ. Alarm data is persisted in PostgreSQL.",
                alarm.Id);
        }
    }
}
