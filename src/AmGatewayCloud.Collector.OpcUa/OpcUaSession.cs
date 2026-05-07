using System.Diagnostics;
using AmGatewayCloud.Collector.OpcUa.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace AmGatewayCloud.Collector.OpcUa;

/// <summary>
/// 最小的 ITelemetryContext 实现，OPC UA SDK 要求的依赖
/// </summary>
file sealed class NullTelemetryContext : ITelemetryContext
{
    private static readonly NullLoggerFactory _nullLoggerFactory = new();
    public ILoggerFactory LoggerFactory => _nullLoggerFactory;
    public ActivitySource ActivitySource { get; } = new("AmGatewayCloud.OpcUa");
    public System.Diagnostics.Metrics.Meter CreateMeter() => new("AmGatewayCloud.OpcUa");
}

file sealed class NullLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}

file sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

/// <summary>
/// OPC UA 会话管理：连接、断开、自动重连、命名空间解析。
/// 状态机：Disconnected → Connected → Reconnecting → Connected
/// 重连使用指数退避：5s → 10s → 20s → 40s → 60s（上限）
/// </summary>
public class OpcUaSession : IDisposable
{
    private readonly OpcUaConfig _config;
    private readonly ILogger<OpcUaSession> _logger;
    private readonly object _lock = new();
    private Session? _session;
    private ApplicationConfiguration? _appConfig;
    private ConfiguredEndpoint? _endpoint;
    private volatile bool _isConnected;
    private bool _disposed;
    private int _reconnectAttempt;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectLoop;

    private const int MaxReconnectDelayMs = 60_000;

    public bool IsConnected => _isConnected;

    /// <summary>重连成功后触发，通知上层重建订阅</summary>
    public event EventHandler? SessionRestored;

    public OpcUaSession(
        IOptions<CollectorConfig> config,
        ILogger<OpcUaSession> logger)
    {
        _config = config.Value.OpcUa;
        _logger = logger;
    }

    /// <summary>建立连接并创建 Session</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_isConnected) return;
        }

        _logger.LogInformation("Connecting to OPC UA server at {Endpoint} (SecurityPolicy: {Policy})",
            _config.Endpoint, _config.SecurityPolicy);

        // 1. 构建 ApplicationConfiguration
        _appConfig = new ApplicationConfiguration
        {
            ApplicationName = "AmGatewayCloud.Collector.OpcUa",
            ApplicationUri = $"urn:{Environment.MachineName}:AmGatewayCloud.Collector.OpcUa",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = _config.AutoAcceptUntrustedCertificates,
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AmGatewayCloud", "OPC UA", "Certificates")
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AmGatewayCloud", "OPC UA", "Trusted")
                }
            },
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 15000
            }
        };

        await _appConfig.ValidateAsync(ApplicationType.Client);

        // 2. 端点发现
        var endpointDescription = await DiscoverEndpointAsync(_appConfig, _config.Endpoint, _config.SecurityPolicy, ct);
        var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(_appConfig));

        // 3. 创建 Session
        var identity = BuildIdentity();
        var sessionFactory = new DefaultSessionFactory(new NullTelemetryContext());
        var session = (Session)await sessionFactory.CreateAsync(
            _appConfig,
            endpoint,
            updateBeforeConnect: false,
            sessionName: "AmGatewayCloud.Collector.OpcUa",
            sessionTimeout: (uint)_config.SessionTimeoutMs,
            identity: identity,
            preferredLocales: null,
            ct);

        // 4. 注册事件
        session.KeepAlive += OnKeepAlive;

        // 5. 同步服务器命名空间表
        await session.FetchNamespaceTablesAsync();

        lock (_lock)
        {
            _session = session;
            _endpoint = endpoint;
            _isConnected = true;
            _reconnectAttempt = 0;
        }

        _logger.LogInformation("Connected to OPC UA server {Endpoint} (SecurityPolicy: {Policy})",
            _config.Endpoint, _config.SecurityPolicy);
    }

    /// <summary>获取当前 Session（可能为 null）</summary>
    public Session? GetSession()
    {
        lock (_lock)
        {
            return _session;
        }
    }

    /// <summary>获取命名空间索引（连接后可用）</summary>
    public ushort GetNamespaceIndex(string namespaceUri)
    {
        var session = GetSession()
            ?? throw new InvalidOperationException("Session not available");

        // 从服务器端命名空间表查找
        for (ushort i = 0; i < session.NamespaceUris.Count; i++)
        {
            if (string.Equals(session.NamespaceUris.GetString(i), namespaceUri,
                StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidOperationException(
            $"Namespace URI '{namespaceUri}' not found in server namespace table. " +
            $"Available: [{string.Join(", ", session.NamespaceUris.ToArray())}]");
    }

    /// <summary>主动断开，停止重连循环</summary>
    public async Task DisconnectAsync()
    {
        // 停止重连循环
        _reconnectCts?.Cancel();

        Session? session;
        lock (_lock)
        {
            session = _session;
            _session = null;
            _isConnected = false;
        }

        if (session != null)
        {
            session.KeepAlive -= OnKeepAlive;

            try
            {
                await session.CloseAsync();
                _logger.LogInformation("Disconnected from OPC UA server");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing OPC UA session");
            }
            finally
            {
                session.Dispose();
            }
        }

        // 等待重连循环退出
        if (_reconnectLoop != null)
        {
            try { await _reconnectLoop; } catch { }
            _reconnectLoop = null;
        }
    }

    /// <summary>将 NodeId 字符串解析为带命名空间的 NodeId</summary>
    public static NodeId ParseNodeId(string nodeIdString, ushort namespaceIndex)
    {
        // 如果已包含命名空间前缀（如 "ns=2;s=xxx"），直接解析
        if (nodeIdString.Contains('='))
        {
            return NodeId.Parse(nodeIdString);
        }

        // 短名称，使用组的 NamespaceUri 对应的索引
        return new NodeId(nodeIdString, namespaceIndex);
    }

    // --- 内部方法 ---

    private async Task<EndpointDescription> DiscoverEndpointAsync(
        ApplicationConfiguration config, string endpointUrl, string securityPolicy, CancellationToken ct)
    {
        EndpointDescriptionCollection endpoints;
        try
        {
            using var discoveryClient = await DiscoveryClient.CreateAsync(
                config, new Uri(endpointUrl), DiagnosticsMasks.None, ct);
            endpoints = await discoveryClient.GetEndpointsAsync(null, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to discover endpoints at '{endpointUrl}'", ex);
        }

        if (endpoints == null || endpoints.Count == 0)
            throw new InvalidOperationException(
                $"No endpoints available at '{endpointUrl}'");

        // 按安全策略筛选
        var policyUri = MapSecurityPolicyUri(securityPolicy);
        var matched = endpoints.FirstOrDefault(e => e.SecurityPolicyUri == policyUri);

        if (matched != null)
            return matched;

        // 指定了安全策略但服务器不支持，Warn 后回退
        _logger.LogWarning(
            "No endpoint matching SecurityPolicy '{Policy}' found. " +
            "Available: [{Available}]. Falling back to first endpoint",
            securityPolicy,
            string.Join(", ", endpoints.Select(e => e.SecurityPolicyUri)));

        return endpoints[0];
    }

    private static string MapSecurityPolicyUri(string securityPolicy)
    {
        return securityPolicy.ToLowerInvariant() switch
        {
            "none" => SecurityPolicies.None,
            "basic128rsa15" => SecurityPolicies.Basic128Rsa15,
            "basic256" => SecurityPolicies.Basic256,
            "basic256sha256" => SecurityPolicies.Basic256Sha256,
            _ => throw new InvalidOperationException(
                $"Unknown SecurityPolicy '{securityPolicy}'. " +
                "Supported: None, Basic128Rsa15, Basic256, Basic256Sha256")
        };
    }

    // --- 事件处理 & 重连 ---

    private void OnKeepAlive(Opc.Ua.Client.ISession session, KeepAliveEventArgs e)
    {
        if (ServiceResult.IsBad(e.Status))
        {
            _logger.LogWarning(
                "OPC UA keep alive failed (Status={Status}, State={State}), triggering reconnect",
                e.Status, e.CurrentState);
            TriggerReconnect();
        }
        else if (e.CurrentState != ServerState.Running)
        {
            _logger.LogWarning(
                "OPC UA server state changed to {State}, triggering reconnect",
                e.CurrentState);
            TriggerReconnect();
        }
        else
        {
            // 服务器正常，重置退避计数
            _reconnectAttempt = 0;
        }
    }

    private void TriggerReconnect()
    {
        lock (_lock)
        {
            if (!_isConnected || _reconnectLoop != null) return;

            _isConnected = false;
            _reconnectCts = new CancellationTokenSource();
            _reconnectLoop = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token));
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Reconnect loop started");

        while (!ct.IsCancellationRequested)
        {
            _reconnectAttempt++;
            var delayMs = CalculateReconnectDelay(_reconnectAttempt);

            _logger.LogWarning(
                "Session lost, attempting reconnect in {DelayMs}ms (attempt {Attempt})",
                delayMs, _reconnectAttempt);

            try
            {
                await Task.Delay(delayMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ReconnectAsync(ct);

                // 重连成功
                _reconnectAttempt = 0;
                _logger.LogInformation("Reconnected to OPC UA server");

                lock (_lock)
                {
                    _isConnected = true;
                    _reconnectLoop = null;
                }

                // 通知上层重建订阅
                OnSessionRestored();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reconnect attempt {Attempt} failed, will retry",
                    _reconnectAttempt);
            }
        }

        // 退出循环（被取消）
        lock (_lock)
        {
            _reconnectLoop = null;
        }

        _logger.LogInformation("Reconnect loop stopped");
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        ApplicationConfiguration? appConfig;

        lock (_lock)
        {
            appConfig = _appConfig;
        }

        if (appConfig == null)
            throw new InvalidOperationException("No config available for reconnect");

        // 重新发现端点（服务器可能更换了证书）
        var endpointDescription = await DiscoverEndpointAsync(
            appConfig, _config.Endpoint, _config.SecurityPolicy, ct);
        var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(appConfig));

        var identity = BuildIdentity();
        var sessionFactory = new DefaultSessionFactory(new NullTelemetryContext());
        var session = (Session)await sessionFactory.CreateAsync(
            appConfig,
            endpoint,
            updateBeforeConnect: false,
            sessionName: "AmGatewayCloud.Collector.OpcUa",
            sessionTimeout: (uint)_config.SessionTimeoutMs,
            identity: identity,
            preferredLocales: null,
            ct);

        session.KeepAlive += OnKeepAlive;
        await session.FetchNamespaceTablesAsync();

        Session? oldSession;
        lock (_lock)
        {
            oldSession = _session;
            _session = session;
            _endpoint = endpoint;
        }

        // 清理旧 Session
        if (oldSession != null)
        {
            oldSession.KeepAlive -= OnKeepAlive;
            try { oldSession.CloseAsync().Wait(3000); } catch { }
            oldSession.Dispose();
        }
    }

    private int CalculateReconnectDelay(int attempt)
    {
        // 指数退避：base * 2^(attempt-1)，上限 60s
        var delay = _config.ReconnectIntervalMs * (1 << Math.Min(attempt - 1, 6));
        return Math.Min(delay, MaxReconnectDelayMs);
    }

    // --- 其他内部方法 ---

    /// <summary>
    /// 根据配置构建用户身份：UserName 模式使用用户名密码，默认匿名。
    /// </summary>
    /// <returns>OPC UA UserIdentity 实例</returns>
    private UserIdentity BuildIdentity()
    {
        return _config.AuthMode?.ToLowerInvariant() switch
        {
            "username" when !string.IsNullOrEmpty(_config.UserName) =>
                new UserIdentity(_config.UserName,
                    System.Text.Encoding.UTF8.GetBytes(_config.Password ?? string.Empty)),
            _ => new UserIdentity(new AnonymousIdentityToken())
        };
    }

    /// <summary>
    /// 触发 SessionRestored 事件，通知上层重建订阅。
    /// </summary>
    protected virtual void OnSessionRestored()
    {
        SessionRestored?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 释放资源：停止重连循环、断开 Session、清理托管资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 停止重连循环
        _reconnectCts?.Cancel();

        Session? session;
        lock (_lock)
        {
            session = _session;
            _session = null;
            _isConnected = false;
        }

        if (session != null)
        {
            session.KeepAlive -= OnKeepAlive;
            session.Dispose();
        }
    }
}
