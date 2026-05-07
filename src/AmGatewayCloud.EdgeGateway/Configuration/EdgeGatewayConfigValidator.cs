using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace AmGatewayCloud.EdgeGateway.Configuration;

public class EdgeGatewayConfigValidator : IValidateOptions<EdgeGatewayConfig>
{
    public ValidateOptionsResult Validate(string? name, EdgeGatewayConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.HubId))
            errors.Add("HubId is required.");

        if (string.IsNullOrWhiteSpace(config.FactoryId))
            errors.Add("FactoryId is required.");

        if (string.IsNullOrWhiteSpace(config.WorkshopId))
            errors.Add("WorkshopId is required.");

        // MQTT
        if (string.IsNullOrWhiteSpace(config.Mqtt.Broker))
            errors.Add("Mqtt:Broker is required.");
        if (config.Mqtt.Port <= 0 || config.Mqtt.Port > 65535)
            errors.Add("Mqtt:Port must be between 1 and 65535.");
        if (config.Mqtt.QoS is < 0 or > 2)
            errors.Add("Mqtt:QoS must be 0, 1, or 2.");
        if (config.Mqtt.KeepAliveSeconds < 5)
            errors.Add("Mqtt:KeepAliveSeconds must be at least 5.");

        // InfluxDB
        if (string.IsNullOrWhiteSpace(config.InfluxDb.Url))
            errors.Add("InfluxDb:Url is required.");
        if (string.IsNullOrWhiteSpace(config.InfluxDb.Token))
            errors.Add("InfluxDb:Token is required.");
        if (string.IsNullOrWhiteSpace(config.InfluxDb.Org))
            errors.Add("InfluxDb:Org is required.");
        if (string.IsNullOrWhiteSpace(config.InfluxDb.Bucket))
            errors.Add("InfluxDb:Bucket is required.");
        if (config.InfluxDb.BatchSize < 1)
            errors.Add("InfluxDb:BatchSize must be at least 1.");
        if (config.InfluxDb.FlushIntervalMs < 100)
            errors.Add("InfluxDb:FlushIntervalMs must be at least 100.");

        // RabbitMQ
        if (string.IsNullOrWhiteSpace(config.RabbitMq.HostName))
            errors.Add("RabbitMq:HostName is required.");
        if (config.RabbitMq.Port <= 0 || config.RabbitMq.Port > 65535)
            errors.Add("RabbitMq:Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(config.RabbitMq.Username))
            errors.Add("RabbitMq:Username is required.");
        if (string.IsNullOrWhiteSpace(config.RabbitMq.Exchange))
            errors.Add("RabbitMq:Exchange is required.");
        if (!config.RabbitMq.RoutingKeyTemplate.Contains("{factoryId}") ||
            !config.RabbitMq.RoutingKeyTemplate.Contains("{workshopId}") ||
            !config.RabbitMq.RoutingKeyTemplate.Contains("{deviceId}") ||
            !config.RabbitMq.RoutingKeyTemplate.Contains("{protocol}"))
            errors.Add("RabbitMq:RoutingKeyTemplate must contain {factoryId}, {workshopId}, {deviceId}, and {protocol}.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
