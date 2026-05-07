using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace AmGatewayCloud.CloudGateway.Configuration;

public class CloudGatewayConfigValidator : IValidateOptions<CloudGatewayConfig>
{
    public ValidateOptionsResult Validate(string? name, CloudGatewayConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.TenantId))
            errors.Add("TenantId is required.");

        // TimescaleDB
        if (string.IsNullOrWhiteSpace(config.TimescaleDb.Host))
            errors.Add("TimescaleDb:Host is required.");
        if (config.TimescaleDb.Port <= 0 || config.TimescaleDb.Port > 65535)
            errors.Add("TimescaleDb:Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(config.TimescaleDb.Database))
            errors.Add("TimescaleDb:Database is required.");
        if (string.IsNullOrWhiteSpace(config.TimescaleDb.Username))
            errors.Add("TimescaleDb:Username is required.");
        if (config.TimescaleDb.BatchSize < 1)
            errors.Add("TimescaleDb:BatchSize must be at least 1.");
        if (config.TimescaleDb.FlushIntervalMs < 100)
            errors.Add("TimescaleDb:FlushIntervalMs must be at least 100.");

        // PostgreSQL
        if (string.IsNullOrWhiteSpace(config.PostgreSql.Host))
            errors.Add("PostgreSql:Host is required.");
        if (config.PostgreSql.Port <= 0 || config.PostgreSql.Port > 65535)
            errors.Add("PostgreSql:Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(config.PostgreSql.Database))
            errors.Add("PostgreSql:Database is required.");
        if (string.IsNullOrWhiteSpace(config.PostgreSql.Username))
            errors.Add("PostgreSql:Username is required.");

        // RabbitMQ
        if (string.IsNullOrWhiteSpace(config.RabbitMq.HostName))
            errors.Add("RabbitMq:HostName is required.");
        if (config.RabbitMq.Port <= 0 || config.RabbitMq.Port > 65535)
            errors.Add("RabbitMq:Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(config.RabbitMq.Username))
            errors.Add("RabbitMq:Username is required.");

        // Factories
        if (config.Factories.Count == 0)
            errors.Add("At least one factory must be configured in Factories.");

        foreach (var factory in config.Factories)
        {
            if (string.IsNullOrWhiteSpace(factory.FactoryId))
                errors.Add("Each factory must have a FactoryId.");
            if (string.IsNullOrWhiteSpace(factory.QueueName))
                errors.Add($"Factory '{factory.FactoryId}' must have a QueueName.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
