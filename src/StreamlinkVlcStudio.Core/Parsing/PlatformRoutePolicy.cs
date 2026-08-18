using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Parsing;

internal static class PlatformRoutePolicy
{
    private const string ResourceName = "StreamlinkVlcStudio.PlatformRoutes.json";
    private static readonly Dictionary<PlatformKind, HashSet<string>> NonChannelRoutes = Load();

    internal static bool IsNonChannelRoute(PlatformKind platform, string route)
    {
        return NonChannelRoutes.TryGetValue(platform, out var routes) && routes.Contains(route);
    }

    private static Dictionary<PlatformKind, HashSet<string>> Load()
    {
        using var stream = typeof(PlatformRoutePolicy).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded platform route policy is missing: {ResourceName}");
        using var document = JsonDocument.Parse(stream);

        return new Dictionary<PlatformKind, HashSet<string>>
        {
            [PlatformKind.Twitch] = ReadRoutes(document.RootElement, "twitch"),
            [PlatformKind.Kick] = ReadRoutes(document.RootElement, "kick")
        };
    }

    private static HashSet<string> ReadRoutes(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var routesElement) ||
            routesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Platform route policy has no '{propertyName}' array.");
        }

        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in routesElement.EnumerateArray())
        {
            var route = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(route) || !routes.Add(route))
            {
                throw new InvalidOperationException($"Platform route policy contains an invalid or duplicate '{propertyName}' route.");
            }
        }

        return routes;
    }
}
