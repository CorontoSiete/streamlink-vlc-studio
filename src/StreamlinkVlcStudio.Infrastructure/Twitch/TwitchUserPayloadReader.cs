using System.Text.Json;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>Selects only an identified Helix user matching the requested channel.</summary>
internal static class TwitchUserPayloadReader
{
    internal static bool TryRead(JsonElement root, string expectedLogin, out JsonElement user)
    {
        user = default;
        if (string.IsNullOrWhiteSpace(expectedLogin) || !TryGetArray(root, "data", out var data))
            return false;

        foreach (var item in data.EnumerateArray())
        {
            if (TryGetNonEmptyString(item, "login", out var login) &&
                string.Equals(login, expectedLogin.Trim(), StringComparison.OrdinalIgnoreCase) &&
                TryGetNonEmptyString(item, "id", out var id) && !string.IsNullOrWhiteSpace(id))
            {
                user = item;
                return true;
            }
        }

        return false;
    }
}
