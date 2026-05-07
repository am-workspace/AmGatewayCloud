using System.Text.Json;
using AmGatewayCloud.CloudGateway.Models;

namespace AmGatewayCloud.CloudGateway.Services;

public static class DataPointConverter
{
    public static (object? value, string column) ConvertValue(DataPoint point)
    {
        var type = point.ValueType?.ToLowerInvariant();

        return type switch
        {
            "int" or "short" or "long" or "int32" or "int64"
                => (TryGetInt64(point.Value), "value_int"),
            "float" or "double" or "single"
                => (TryGetDouble(point.Value), "value_float"),
            "bool" or "boolean"
                => (TryGetBool(point.Value), "value_bool"),
            "string"
                => (point.Value.GetString(), "value_string"),
            _ => UnknownTypeFallback(point)
        };
    }

    private static (object? value, string column) UnknownTypeFallback(DataPoint point)
    {
        return point.Value.ValueKind switch
        {
            JsonValueKind.Number when point.Value.TryGetInt64(out var i) => (i, "value_int"),
            JsonValueKind.Number => (point.Value.GetDouble(), "value_float"),
            JsonValueKind.True or JsonValueKind.False => (point.Value.GetBoolean(), "value_bool"),
            JsonValueKind.String => (point.Value.GetString(), "value_string"),
            _ => (point.Value.ToString(), "value_string")
        };
    }

    private static long? TryGetInt64(JsonElement value)
    {
        if (value.TryGetInt64(out var i)) return i;
        return null;
    }

    private static double? TryGetDouble(JsonElement value)
    {
        if (value.TryGetDouble(out var d)) return d;
        return null;
    }

    private static bool? TryGetBool(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return null;
    }
}
