using System.Text.Json;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

/// <summary>Strict browser input readers shared by workspace commands and account preferences.</summary>
internal static class LocalWebRequestValues
{
    internal static Guid Id(JsonElement input, string name = "id") => Guid.TryParse(RequiredText(input, name, 40), out var id)
        ? id : throw new ArgumentException("Invalid download ID.");

    internal static double Seconds(JsonElement input, string name) =>
        TryGetProperty(input, name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) && number is >= 0 and <= 31_536_000
            ? number : throw new ArgumentException("Invalid playback time.");

    internal static bool Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new ArgumentException("Invalid field.")
    };

    internal static bool Boolean(JsonElement input, string name, bool fallback) =>
        TryGetProperty(input, name, out var value) ? Boolean(value) : fallback;

    internal static int Integer(JsonElement value, int min, int max) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max
            ? number : throw new ArgumentException("Invalid field.");

    internal static long Integer(JsonElement input, string name, long fallback) =>
        !TryGetProperty(input, name, out var value) ? fallback :
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : throw new ArgumentException("Invalid field.");

    internal static string RequiredText(JsonElement input, string name, int max)
    {
        var value = Optional(input, name, max);
        return value.Length > 0 ? value : throw new ArgumentException("A required field is empty.");
    }

    internal static string Optional(JsonElement input, string name, int max)
    {
        if (!TryGetProperty(input, name, out var property) || property.ValueKind == JsonValueKind.Null) return "";
        if (property.ValueKind != JsonValueKind.String) throw new ArgumentException("Invalid field.");
        var value = property.GetString()?.Trim() ?? "";
        return value.Length <= max && !value.Any(char.IsControl) ? value : throw new ArgumentException("Invalid field.");
    }

    private static bool TryGetProperty(JsonElement input, string name, out JsonElement value)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a JSON object.");
        return input.TryGetProperty(name, out value);
    }
}
