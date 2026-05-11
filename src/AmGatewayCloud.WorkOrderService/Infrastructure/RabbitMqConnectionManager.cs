using AmGatewayCloud.Shared.Configuration;
using RabbitMQ.Client;

namespace AmGatewayCloud.WorkOrderService.Infrastructure;

/// <summary>
/// RabbitMQ 连接管理器：管理到 /business vhost 的连接，支持自动重连
/// </summary>
public class RabbitMqConnectionManager : IAsyncDisposable
{
    private readonly RabbitMqConfig _config;
    private readonly ILogger<RabbitMqConnectionManager> _logger;

    private IConnection? _connection;
    private IModel? _channel;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public RabbitMqConnectionManager(RabbitMqConfig config, ILogger<RabbitMqConnectionManager> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// 获取或创建 RabbitMQ 连接和通道，确保 Exchange + Queue 已声明
    /// </summary>
    public async Task<IModel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is not null && _channel.IsOpen)
            return _channel;

        await _lock.WaitAsync(ct);
        try
        {
            if (_channel is not null && _channel.IsOpen)
                return _channel;

            var factory = new ConnectionFactory
            {
                HostName = _config.HostName,
                Port = _config.Port,
                VirtualHost = _config.VirtualHost,
                UserName = _config.Username,
                Password = _config.Password,
                Ssl = new SslOption { Enabled = _config.UseSsl },
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromMilliseconds(_config.ReconnectDelayMs)
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            // 声明 Topic Exchange（幂等操作）
            _channel.ExchangeDeclare(
                exchange: _config.Exchange,
                type: ExchangeType.Topic,
                durable: true);

            // 声明工单服务专用队列并绑定到 alarm.# routing key
            _channel.QueueDeclare(
                queue: _config.QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false);

            _channel.QueueBind(
                queue: _config.QueueName,
                exchange: _config.Exchange,
                routingKey: "alarm.#");

            _logger.LogInformation(
                "RabbitMQ connected to {Host}:{Port}{VHost}, exchange: {Exchange}, queue: {Queue}",
                _config.HostName, _config.Port, _config.VirtualHost, _config.Exchange, _config.QueueName);

            _connection.ConnectionShutdown += (sender, args) =>
            {
                if (!_disposed)
                    _logger.LogWarning("RabbitMQ connection shutdown: {Reason}", args.ReplyText);
            };

            return _channel;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _channel?.Close();
        _channel?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
        _lock.Dispose();
        await ValueTask.CompletedTask;
    }
}
