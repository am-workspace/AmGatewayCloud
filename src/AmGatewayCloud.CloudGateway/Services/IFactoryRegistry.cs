using AmGatewayCloud.CloudGateway.Configuration;
using Microsoft.Extensions.Options;

namespace AmGatewayCloud.CloudGateway.Services;

public interface IFactoryRegistry
{
    IReadOnlyList<FactoryConsumerConfig> GetFactories();
    event EventHandler<FactoryListChangedEventArgs>? FactoriesChanged;
}

public class FactoryListChangedEventArgs : EventArgs
{
}

public class FileFactoryRegistry : IFactoryRegistry
{
    private readonly IOptionsMonitor<CloudGatewayConfig> _configMonitor;

    public FileFactoryRegistry(IOptionsMonitor<CloudGatewayConfig> configMonitor)
    {
        _configMonitor = configMonitor;
        _configMonitor.OnChange(_ =>
        {
            FactoriesChanged?.Invoke(this, new FactoryListChangedEventArgs());
        });
    }

    public IReadOnlyList<FactoryConsumerConfig> GetFactories()
    {
        return _configMonitor.CurrentValue.Factories
            .Where(f => f.Enabled)
            .ToList();
    }

    public event EventHandler<FactoryListChangedEventArgs>? FactoriesChanged;
}
