using System.Net.Sockets;
using AmGatewayCloud.Collector.Modbus.Configuration;
using Microsoft.Extensions.Options;
using NModbus;

namespace AmGatewayCloud.Collector.Modbus;

public class ModbusConnection : IDisposable
{
    private readonly ModbusConfig _config;
    private readonly ILogger<ModbusConnection> _logger;

    private TcpClient? _tcpClient;
    private IModbusMaster? _master;
    private volatile bool _isConnected;
    private readonly object _lock = new();
    private int _reconnectAttempt;

    public bool IsConnected => _isConnected;

    public ModbusConnection(IOptions<CollectorConfig> config, ILogger<ModbusConnection> logger)
    {
        _config = config.Value.Modbus;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await ConnectInternalAsync(ct);
    }

    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_isConnected) return;
        await ReconnectAsync(ct);
    }

    public async Task ReconnectAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectInternalAsync(ct);
                return; // success
            }
            catch (Exception ex)
            {
                var delay = CalculateReconnectDelay(_reconnectAttempt);
                _logger.LogWarning(ex, "Connection failed (attempt {Attempt}), retrying in {Delay}ms",
                    _reconnectAttempt + 1, delay);
                _reconnectAttempt++;
                await Task.Delay(delay, ct);
            }
        }
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort start, ushort count, CancellationToken ct)
    {
        return await ReadWithRetryAsync(
            () => _master!.ReadHoldingRegistersAsync(_config.SlaveId, start, count),
            start, count, ct);
    }

    public async Task<ushort[]> ReadInputRegistersAsync(ushort start, ushort count, CancellationToken ct)
    {
        return await ReadWithRetryAsync(
            () => _master!.ReadInputRegistersAsync(_config.SlaveId, start, count),
            start, count, ct);
    }

    public async Task<bool[]> ReadCoilsAsync(ushort start, ushort count, CancellationToken ct)
    {
        return await ReadWithRetryAsync(
            () => _master!.ReadCoilsAsync(_config.SlaveId, start, count),
            start, count, ct);
    }

    public async Task<bool[]> ReadDiscreteInputsAsync(ushort start, ushort count, CancellationToken ct)
    {
        return await ReadWithRetryAsync(
            () => _master!.ReadInputsAsync(_config.SlaveId, start, count),
            start, count, ct);
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _master?.Dispose();
            _master = null;

            if (_tcpClient?.Connected == true)
            {
                try { _tcpClient.Close(); } catch { /* ignore */ }
            }
            _tcpClient?.Dispose();
            _tcpClient = null;

            _isConnected = false;
        }
        _logger.LogInformation("Disconnected from Modbus slave {Host}:{Port}", _config.Host, _config.Port);
    }

    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(_config.ConnectTimeoutMs);

        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(_config.Host, _config.Port, connectCts.Token);

            var factory = new ModbusFactory();
            var master = factory.CreateMaster(tcpClient);
            master.Transport.ReadTimeout = _config.ReadTimeoutMs;
            master.Transport.WriteTimeout = _config.ReadTimeoutMs;

            lock (_lock)
            {
                _master?.Dispose();
                _tcpClient?.Dispose();
                _master = master;
                _tcpClient = tcpClient;
                _isConnected = true;
                _reconnectAttempt = 0;
            }

            _logger.LogInformation("Connected to Modbus slave {Host}:{Port}", _config.Host, _config.Port);
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    private async Task<T> ReadWithRetryAsync<T>(Func<Task<T>> readFunc, ushort start, ushort count, CancellationToken ct)
    {
        try
        {
            lock (_lock)
            {
                if (!_isConnected || _master == null)
                    throw new InvalidOperationException("Not connected to Modbus slave");
            }

            return await readFunc();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogWarning(ex, "Read failed for start={Start} count={Count}, marking disconnected", start, count);
            _isConnected = false;
            throw;
        }
    }

    private int CalculateReconnectDelay(int attempt)
    {
        var baseInterval = _config.ReconnectIntervalMs;
        var maxInterval = 60_000;
        var delay = Math.Min(baseInterval * (1 << Math.Min(attempt, 5)), maxInterval);
        return delay;
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
