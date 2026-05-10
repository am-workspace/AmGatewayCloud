using AmGatewayCloud.Shared.Messages;
using AmGatewayCloud.WebApi.Hubs;
using AmGatewayCloud.WebApi.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace AmGatewayCloud.WebApi.Services;

/// <summary>
/// 报警事件订阅器：订阅 RabbitMQ 报警消息，推送到 SignalR Hub
/// </summary>
public class AlarmEventSubscriber : BackgroundService
{
    private readonly RabbitMqConnectionManager _mqManager;
    private readonly IHubContext<AlarmHub> _hubContext;
    private readonly ILogger<AlarmEventSubscriber> _logger;

    public AlarmEventSubscriber(
        RabbitMqConnectionManager mqManager,
        IHubContext<AlarmHub> hubContext,
        ILogger<AlarmEventSubscriber> logger)
    {
        _mqManager = mqManager;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RabbitMQ consumer error, reconnecting in 5s...");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var channel = await _mqManager.GetChannelAsync(ct);
        var consumer = new EventingBasicConsumer(channel);

        consumer.Received += (model, ea) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(ea.Body.Span);
                var message = JsonSerializer.Deserialize<AlarmEventMessage>(json);

                if (message is null)
                {
                    _logger.LogWarning("Failed to deserialize alarm message");
                    channel.BasicAck(ea.DeliveryTag, false);
                    return;
                }

                // 按工厂分组推送到 SignalR
                if (!string.IsNullOrEmpty(message.FactoryId))
                {
                    _hubContext.Clients.Group($"factory-{message.FactoryId}")
                        .SendAsync("AlarmReceived", message, CancellationToken.None);
                }
                else
                {
                    // 无工厂信息时广播给所有客户端
                    _hubContext.Clients.All
                        .SendAsync("AlarmReceived", message, CancellationToken.None);
                }

                _logger.LogDebug("Alarm notification pushed: {RuleId} - {Status} for device {DeviceId}",
                    message.RuleId, message.Status, message.DeviceId);

                channel.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing alarm message");
                channel.BasicNack(ea.DeliveryTag, false, true);
            }
        };

        consumer.Shutdown += (model, args) =>
        {
            if (!ct.IsCancellationRequested)
                _logger.LogWarning("RabbitMQ consumer shutdown: {Reason}", args.ReplyText);
        };

        consumer.Registered += (model, args) =>
        {
            _logger.LogInformation("RabbitMQ consumer registered on queue");
        };

        channel.BasicConsume(
            queue: "amgateway.alarm-notifications",
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation("AlarmEventSubscriber started, consuming from amgateway.alarm-notifications");

        var tcs = new TaskCompletionSource();
        using var registration = ct.Register(() => tcs.TrySetResult());
        await tcs.Task;
    }
}
