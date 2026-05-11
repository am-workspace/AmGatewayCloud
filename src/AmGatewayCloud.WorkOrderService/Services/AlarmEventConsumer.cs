using System.Text.Json;
using AmGatewayCloud.Shared.Messages;
using AmGatewayCloud.WorkOrderService.Configuration;
using AmGatewayCloud.WorkOrderService.Infrastructure;
using AmGatewayCloud.WorkOrderService.Models;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AmGatewayCloud.WorkOrderService.Services;

/// <summary>
/// RabbitMQ 报警事件消费者：收到 Active 报警时自动创建工单
/// </summary>
public class AlarmEventConsumer : BackgroundService
{
    private readonly WorkOrderServiceConfig _config;
    private readonly RabbitMqConnectionManager _connectionManager;
    private readonly ILogger<AlarmEventConsumer> _logger;

    public AlarmEventConsumer(
        WorkOrderServiceConfig config,
        RabbitMqConnectionManager connectionManager,
        ILogger<AlarmEventConsumer> logger)
    {
        _config = config;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AlarmEventConsumer starting, queue: {Queue}", _config.RabbitMq.QueueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var channel = await _connectionManager.GetChannelAsync(stoppingToken);

                // 设置 prefetch count，避免消息堆积
                channel.BasicQos(prefetchSize: 0, prefetchCount: 10, global: false);

                var consumer = new EventingBasicConsumer(channel);
                consumer.Received += async (model, ea) =>
                {
                    try
                    {
                        var message = JsonSerializer.Deserialize<AlarmEventMessage>(ea.Body.Span);
                        if (message is null)
                        {
                            _logger.LogWarning("Failed to deserialize alarm event message");
                            channel.BasicAck(ea.DeliveryTag, multiple: false);
                            return;
                        }

                        await HandleAlarmEventAsync(message, stoppingToken);
                        channel.BasicAck(ea.DeliveryTag, multiple: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing alarm event, deliveryTag={Tag}", ea.DeliveryTag);
                        channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
                    }
                };

                channel.BasicConsume(
                    queue: _config.RabbitMq.QueueName,
                    autoAck: false,
                    consumer: consumer);

                _logger.LogInformation("AlarmEventConsumer consuming from queue: {Queue}", _config.RabbitMq.QueueName);

                // 等待取消信号
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AlarmEventConsumer connection error, retrying in {Delay}ms",
                    _config.RabbitMq.ReconnectDelayMs);
                await Task.Delay(_config.RabbitMq.ReconnectDelayMs, stoppingToken);
            }
        }
    }

    private async Task HandleAlarmEventAsync(AlarmEventMessage message, CancellationToken ct)
    {
        // 仅处理 Active 报警
        if (message.Status != "Active")
        {
            _logger.LogDebug("Ignoring non-Active alarm: {AlarmId}, status={Status}",
                message.Id, message.Status);
            return;
        }

        // 检查是否已有工单（幂等）
        if (await ExistsWorkOrderForAlarmAsync(message.Id, ct))
        {
            _logger.LogDebug("Work order already exists for alarm: {AlarmId}", message.Id);
            return;
        }

        // 生成工单
        var workOrder = new WorkOrder
        {
            AlarmId = message.Id,
            TenantId = message.TenantId,
            FactoryId = message.FactoryId,
            WorkshopId = message.WorkshopId,
            DeviceId = message.DeviceId,
            Title = $"报警工单: {message.RuleName} - {message.DeviceId} ({message.Level})",
            Description = $"报警规则: {message.RuleName}\n" +
                          $"设备: {message.DeviceId}\n" +
                          $"标签: {message.Tag}\n" +
                          $"触发值: {message.TriggerValue}\n" +
                          $"阈值: {message.Threshold}\n" +
                          $"级别: {message.Level}\n" +
                          $"触发时间: {message.TriggeredAt:yyyy-MM-dd HH:mm:ss}",
            Level = message.Level,
            Status = WorkOrderStatus.Pending
        };

        await InsertWorkOrderAsync(workOrder, ct);
        _logger.LogInformation("Work order created: {Id}, alarmId={AlarmId}, title={Title}",
            workOrder.Id, workOrder.AlarmId, workOrder.Title);
    }

    private async Task<bool> ExistsWorkOrderForAlarmAsync(Guid alarmId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_config.PostgreSql.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM work_orders WHERE alarm_id = @alarmId";
        cmd.Parameters.AddWithValue("alarmId", alarmId);
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return count > 0;
    }

    private async Task InsertWorkOrderAsync(WorkOrder workOrder, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_config.PostgreSql.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO work_orders (id, alarm_id, tenant_id, factory_id, workshop_id, device_id,
    title, description, level, status, created_at, updated_at)
VALUES (@id, @alarmId, @tenantId, @factoryId, @workshopId, @deviceId,
    @title, @description, @level, @status, @createdAt, @updatedAt)";
        cmd.Parameters.AddWithValue("id", workOrder.Id);
        cmd.Parameters.AddWithValue("alarmId", workOrder.AlarmId);
        cmd.Parameters.AddWithValue("tenantId", workOrder.TenantId);
        cmd.Parameters.AddWithValue("factoryId", workOrder.FactoryId);
        cmd.Parameters.AddWithValue("workshopId", (object?)workOrder.WorkshopId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("deviceId", workOrder.DeviceId);
        cmd.Parameters.AddWithValue("title", workOrder.Title);
        cmd.Parameters.AddWithValue("description", (object?)workOrder.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("level", workOrder.Level);
        cmd.Parameters.AddWithValue("status", workOrder.Status.ToString());
        cmd.Parameters.AddWithValue("createdAt", workOrder.CreatedAt);
        cmd.Parameters.AddWithValue("updatedAt", workOrder.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
