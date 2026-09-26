using System.Globalization;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Security;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

internal static partial class TwitchVodVariantPlaylist
{
    internal static Uri Select(string content, Uri baseUri, string quality)
    {
        var lines = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "#EXTM3U") throw Unsupported();
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Where(line => line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)))
        {
            var attributes = ParseAttributes(line[(line.IndexOf(':') + 1)..]);
            // External audio requires Streamlink's muxing and language selection.
            if (attributes.ContainsKey("URI")) throw Unsupported();
            if (attributes.GetValueOrDefault("TYPE") == "VIDEO" &&
                attributes.TryGetValue("GROUP-ID", out var group) && attributes.TryGetValue("NAME", out var name))
                if (!groups.TryAdd(group, name)) throw Unsupported();
        }

        var variants = new List<(string Name, int Weight, Uri Uri)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            var attributes = ParseAttributes(lines[i][18..]);
            var name = attributes.TryGetValue("VIDEO", out var group) && groups.TryGetValue(group, out var groupName)
                ? groupName : attributes.GetValueOrDefault("IVS-NAME");
            if (name is null) throw Unsupported();
            // Streamlink lowercases names and retains their leading identifier. In the
            // current VOD master, "Audio Only" therefore becomes "audio".
            name = name == "Audio Only" ? "audio" : name.ToLowerInvariant();
            var match = ResolutionName().Match(name);
            var weight = name == "source" ? int.MaxValue : 0;
            if (match.Success)
            {
                if (!int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var height) ||
                    !int.TryParse(match.Groups[2].Success ? match.Groups[2].Value : "0", CultureInfo.InvariantCulture, out var fps) ||
                    height is <= 0 or > 16384 || fps is < 0 or > 1000) throw Unsupported();
                weight = height + fps;
            }
            else if (name is not ("source" or "audio" or "audio_only")) throw Unsupported();

            if (++i >= lines.Length || lines[i].StartsWith('#') || variants.Any(variant => variant.Name == name) ||
                !ProviderUriPolicy.TryResolveReplayUri(lines[i], baseUri, PlatformKind.Twitch, out var uri)) throw Unsupported();
            variants.Add((name, weight, uri));
        }
        if (variants.Count == 0) throw Unsupported();
        if (quality is "best" or "worst")
        {
            // Streamlink excludes unweighted audio from best/worst when video exists.
            var ranked = variants.Where(variant => variant.Weight > 0 || variants.Count == 1)
                .OrderBy(variant => variant.Weight).ToArray();
            if (ranked.Length == 0) throw Unsupported();
            return quality == "best" ? ranked[^1].Uri : ranked[0].Uri;
        }
        return variants.FirstOrDefault(variant => variant.Name == quality).Uri ?? throw Unsupported();
    }

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var offset = 0;
        foreach (Match match in Attribute().Matches(text))
        {
            if (match.Index != offset || !attributes.TryAdd(match.Groups[1].Value,
                    match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value)) throw Unsupported();
            offset += match.Length;
        }
        if (offset != text.Length || attributes.Count == 0 || text.EndsWith(',')) throw Unsupported();
        return attributes;
    }

    private static InvalidDataException Unsupported() => new("The Twitch master playlist or requested quality requires Streamlink resolution.");

    [GeneratedRegex("([A-Z0-9-]+)=(?:\"([^\"]*)\"|([^,\"]+))(?:,|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"^(\d+)p(\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionName();
}
