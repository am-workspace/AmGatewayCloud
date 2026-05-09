using AmGatewayCloud.CloudGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AmGatewayCloud.CloudGateway.Services;

public class MultiRabbitMqConsumer : IHostedService
{
    private readonly IFactoryRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MultiRabbitMqConsumer> _logger;
    private readonly Dictionary<string, FactoryConsumer> _consumers = new();
    private readonly object _lock = new();
    private CancellationToken _ct;

    public MultiRabbitMqConsumer(
        IFactoryRegistry registry,
        IServiceProvider serviceProvider,
        ILogger<MultiRabbitMqConsumer> logger)
    {
        _registry = registry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _ct = ct;
        _registry.FactoriesChanged += OnFactoriesChanged;
        SyncConsumers();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _registry.FactoriesChanged -= OnFactoriesChanged;

        lock (_lock)
        {
            foreach (var consumer in _consumers.Values)
            {
                consumer.Dispose();
            }
            _consumers.Clear();
        }

        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<string, bool> GetConsumerStatus()
    {
        lock (_lock)
        {
            return _consumers.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ExecuteTask?.Status == TaskStatus.Running);
        }
    }

    private void OnFactoriesChanged(object? sender, FactoryListChangedEventArgs e)
    {
        _logger.LogInformation("Factory configuration changed, syncing consumers...");
        SyncConsumers();
    }

    private void SyncConsumers()
    {
        var factories = _registry.GetFactories();
        var currentIds = factories.Select(f => f.FactoryId).ToHashSet();

        lock (_lock)
        {
            // 停止被移除的 Consumer
            var toRemove = _consumers.Keys.Where(id => !currentIds.Contains(id)).ToList();
            foreach (var id in toRemove)
            {
                _logger.LogInformation("Stopping consumer for factory: {FactoryId}", id);
                _consumers[id].Dispose();
                _consumers.Remove(id);
            }

            // 启动新增的 Consumer
            foreach (var factory in factories)
            {
                if (!_consumers.ContainsKey(factory.FactoryId))
                {
                    _logger.LogInformation("Starting consumer for factory: {FactoryId}, queue: {QueueName}",
                        factory.FactoryId, factory.QueueName);

                    var consumer = CreateConsumer(factory);
                    _consumers[factory.FactoryId] = consumer;
                    _ = consumer.StartAsync(_ct);
                }
            }
        }
    }

    private FactoryConsumer CreateConsumer(FactoryConsumerConfig config)
    {
        var scope = _serviceProvider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<CloudGatewayConfig>>();
        var rabbitConfig = options.Value.RabbitMq;

        return new FactoryConsumer(
            config,
            rabbitConfig,
            scope.ServiceProvider.GetRequiredService<MessageDeduplicator>(),
            scope.ServiceProvider.GetRequiredService<TimescaleDbWriter>(),
            scope.ServiceProvider.GetRequiredService<PostgreSqlDeviceStore>(),
            scope.ServiceProvider.GetRequiredService<AuditLogService>(),
            scope.ServiceProvider.GetRequiredService<ConsumerHealthTracker>(),
            scope.ServiceProvider.GetRequiredService<ILogger<FactoryConsumer>>()
        );
    }
}
