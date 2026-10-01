using System.Text.Json;
using StreamlinkVlcStudio.Core.Json;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

internal static class ReplayPayloadReader
{
    // Preserve provider text until the field-specific normalization step.
    internal static string ReadReplayString(JsonElement element, string propertyName)
    {
        return JsonElementReader.GetOptionalString(element, propertyName, trimStrings: false);
    }

    internal static string UnescapeJsonUrl(string value) =>
        value.Trim().Trim('"').Replace("\\/", "/", StringComparison.Ordinal);
}
