using System.Globalization;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed partial class FollowedStreamsService
{
    private static async Task<string> LoadKickOfflineBannerAsync(KickWebsiteJsonReader reader,
        FollowedChannel channel, CancellationToken cancellationToken)
    {
        var body = await reader.ReadAsync(
            $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel.Channel)}",
            channel.Url, cancellationToken).ConfigureAwait(false);
        if (body is null) throw new JsonException("Kick did not return offline channel artwork.");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (GetOptionalString(root, "id").Length == 0 ||
            !string.Equals(GetOptionalString(root, "slug"), channel.Channel, StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("offline_banner_image", out var offline))
            throw new JsonException("Kick did not return artwork for the requested channel.");
        root.TryGetProperty("banner_image", out var profile);
        if (offline.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object) ||
            profile.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            throw new JsonException("Kick returned malformed channel artwork.");

        // Verified website responses expose working WebP variants in srcset even when
        // offline_banner_image.src and the official banner_picture return AccessDenied.
        // Use only advertised URLs, selecting the smallest width >= the card's 640px decode.
        var responsive = GetOptionalString(offline, "srcset").Split(',')
            .Select(entry => entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && parts[1].EndsWith('w'))
            .Select(parts => (Url: NormalizeBannerUrl(parts[0]), Width:
                int.TryParse(parts[1].AsSpan(0, parts[1].Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                    ? width : 0))
            .Where(image => image.Url.Length > 0 && image.Width > 0)
            .OrderBy(image => image.Width >= 640 ? 0 : 1)
            .ThenBy(image => image.Width >= 640 ? image.Width : -image.Width)
            .Select(image => image.Url).FirstOrDefault();
        var banner = FirstNonEmpty(responsive,
            NormalizeBannerUrl(GetOptionalString(offline, "src")),
            NormalizeBannerUrl(GetOptionalString(profile, "url")));
        if (banner.Length == 0 && (offline.ValueKind == JsonValueKind.Object || profile.ValueKind == JsonValueKind.Object))
            throw new JsonException("Kick returned artwork without a usable image URL.");
        return banner;
    }

    private static string NormalizeBannerUrl(string value)
    {
        var normalized = NormalizeImageUrl(value);
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? normalized : "";
    }
}
