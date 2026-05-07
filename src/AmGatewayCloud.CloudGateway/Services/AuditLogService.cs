using AmGatewayCloud.CloudGateway.Configuration;
using AmGatewayCloud.CloudGateway.Infrastructure;
using AmGatewayCloud.CloudGateway.Models;
using Dapper;
using Microsoft.Extensions.Options;

namespace AmGatewayCloud.CloudGateway.Services;

public class AuditLogService
{
    private readonly NpgsqlConnectionFactory _connectionFactory;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(
        IOptions<CloudGatewayConfig> options,
        ILogger<AuditLogService> logger)
    {
        _connectionFactory = new NpgsqlConnectionFactory(options, options.Value.PostgreSql.Database);
        _logger = logger;
    }

    public async Task LogAsync(AuditLog log, CancellationToken ct = default)
    {
        try
        {
            await using var conn = _connectionFactory.CreateConnection();
            await conn.OpenAsync(ct);

            await conn.ExecuteAsync(@"
                INSERT INTO audit_logs (id, factory_id, batch_id, reason, raw_payload_preview, received_at)
                VALUES (@Id, @FactoryId, @BatchId, @Reason, @RawPayloadPreview, @ReceivedAt);
            ", log);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit log");
        }
    }
}
