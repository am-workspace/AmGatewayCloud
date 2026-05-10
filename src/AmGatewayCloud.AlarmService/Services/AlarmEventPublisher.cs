using System.Collections.Concurrent;
using System.Text.Json;
using AmGatewayCloud.Shared.Messages;
using AmGatewayCloud.AlarmService.Infrastructure;
using AmGatewayCloud.AlarmService.Models;

namespace AmGatewayCloud.AlarmService.Services;

/// <summary>
/// 报警事件发布器：将报警事件发布到 RabbitMQ
/// </summary>
public class AlarmEventPublisher
{
    private readonly RabbitMqConnectionManager _connectionManager;
    private readonly ILogger<AlarmEventPublisher> _logger;

    private readonly ConcurrentDictionary<string, AlarmRule> _ruleCache = new();

    public AlarmEventPublisher(
        RabbitMqConnectionManager connectionManager,
        ILogger<AlarmEventPublisher> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public void UpdateRuleNameCache(IEnumerable<AlarmRule> rules)
    {
        _ruleCache.Clear();
        foreach (var rule in rules)
            _ruleCache[rule.Id] = rule;
    }

    public string? GetRuleName(string ruleId)
        => _ruleCache.GetValueOrDefault(ruleId)?.Name;

    public async Task PublishAsync(AlarmEvent alarmEvent, string exchange, CancellationToken ct)
    {
        try
        {
            var channel = await _connectionManager.GetChannelAsync(ct);

            var rule = _ruleCache.GetValueOrDefault(alarmEvent.RuleId);
            var message = new AlarmEventMessage
            {
                Id = alarmEvent.Id,
                RuleId = alarmEvent.RuleId,
                RuleName = rule?.Name ?? alarmEvent.RuleId,
                TenantId = alarmEvent.TenantId,
                FactoryId = alarmEvent.FactoryId,
                WorkshopId = alarmEvent.WorkshopId,
                DeviceId = alarmEvent.DeviceId,
                Tag = alarmEvent.Tag,
                Operator = rule?.Operator ?? ">",
                Threshold = rule?.Threshold ?? 0,
                ThresholdString = rule?.ThresholdString,
                TriggerValue = alarmEvent.TriggerValue,
                Level = alarmEvent.Level,
                Status = alarmEvent.Status.ToString(),
                IsStale = alarmEvent.IsStale,
                Message = alarmEvent.Message,
                TriggeredAt = alarmEvent.TriggeredAt,
                SuppressedAt = alarmEvent.SuppressedAt,
                SuppressedBy = alarmEvent.SuppressedBy,
                SuppressedReason = alarmEvent.SuppressedReason,
                ClearedAt = alarmEvent.ClearedAt,
                ClearValue = alarmEvent.ClearValue
            };

            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            var routingKey = $"alarm.{alarmEvent.TenantId}.{alarmEvent.FactoryId}.{alarmEvent.Level.ToLowerInvariant()}";

            channel.BasicPublish(
                exchange: exchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: null,
                body: body);

            _logger.LogInformation(
                "Alarm event published: {RoutingKey}, alarmId={AlarmId}, status={Status}",
                routingKey, alarmEvent.Id, alarmEvent.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish alarm event {AlarmId} to RabbitMQ. Alarm data is persisted in PostgreSQL.",
                alarmEvent.Id);
        }
    }
}
