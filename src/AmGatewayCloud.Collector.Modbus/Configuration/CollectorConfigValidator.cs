using Microsoft.Extensions.Options;

namespace AmGatewayCloud.Collector.Modbus.Configuration;

public class CollectorConfigValidator : IValidateOptions<CollectorConfig>
{
    public ValidateOptionsResult Validate(string? name, CollectorConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.DeviceId))
            errors.Add("Collector:DeviceId is required");

        if (config.PollIntervalMs <= 0)
            errors.Add("Collector:PollIntervalMs must be > 0");

        // Modbus config
        if (string.IsNullOrWhiteSpace(config.Modbus.Host))
            errors.Add("Collector:Modbus:Host is required");
        if (config.Modbus.Port <= 0 || config.Modbus.Port > 65535)
            errors.Add("Collector:Modbus:Port must be between 1 and 65535");

        // Register groups
        if (config.RegisterGroups.Count == 0)
            errors.Add("Collector:RegisterGroups must have at least one group");

        foreach (var group in config.RegisterGroups)
        {
            ValidateRegisterGroup(group, errors);
        }

        ValidateNoOverlap(config.RegisterGroups, errors);

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateRegisterGroup(RegisterGroupConfig group, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(group.Name))
            errors.Add($"Register group has empty Name");

        // Modbus protocol limits
        var maxCount = group.Type switch
        {
            RegisterType.Holding or RegisterType.Input => 125,
            RegisterType.Coil or RegisterType.Discrete => 2000,
            _ => 0
        };

        if (group.Count <= 0)
            errors.Add($"Register group '{group.Name}': Count must be > 0");

        if (maxCount > 0 && group.Count > maxCount)
            errors.Add($"Register group '{group.Name}': Count={group.Count} exceeds Modbus limit of {maxCount} for type {group.Type}");

        // Tags validation
        if (group.Tags is null || group.Tags.Count == 0)
            errors.Add($"Register group '{group.Name}': Tags cannot be empty when Count > 0");
        else if (group.Tags.Count != group.Count)
            errors.Add($"Register group '{group.Name}': Tags.Count ({group.Tags.Count}) must equal Count ({group.Count})");
    }

    private static void ValidateNoOverlap(List<RegisterGroupConfig> groups, List<string> errors)
    {
        foreach (var typeGroup in groups.GroupBy(g => g.Type))
        {
            var sorted = typeGroup.OrderBy(g => g.Start).ToList();

            for (int i = 1; i < sorted.Count; i++)
            {
                var prev = sorted[i - 1];
                var curr = sorted[i];

                if (curr.Start < prev.Start + prev.Count)
                    errors.Add($"Register groups '{prev.Name}' [{prev.Start}..{prev.Start + prev.Count - 1}] " +
                               $"and '{curr.Name}' [{curr.Start}..{curr.Start + curr.Count - 1}] overlap");
            }
        }
    }
}
