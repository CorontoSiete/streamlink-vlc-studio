using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

/// <summary>
/// Kick's website sidebar contract, verified against Kick's own JavaScript.
/// See docs/kick-follow-import.md. This is separate from the public OAuth API.
/// </summary>
internal static class KickFollowedChannelsReader
{
    internal const int MaximumPages = 1000;
    internal const int MaximumResponseBytes = 2 * 1024 * 1024;
    internal const int MaximumChannels = 50_000;

    internal static async Task<IReadOnlyList<string>> ReadAllAsync(
        Func<string, CancellationToken, Task<string>> readPage,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var cursor = "";
        for (var pageIndex = 0; pageIndex < MaximumPages; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = await readPage(BuildPageUrl(cursor), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var page = ReadPage(body);
            channels.UnionWith(page.Channels);
            if (channels.Count > MaximumChannels)
                throw new InvalidOperationException("Kick returned too many followed channels. Nothing was imported.");
            progress?.Report(channels.Count);
            if (page.NextCursor.Length == 0)
                return channels.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!cursors.Add(page.NextCursor))
                throw new InvalidOperationException("Kick repeated a follow-list page. Nothing was imported; try again.");
            cursor = page.NextCursor;
        }
        throw new InvalidOperationException("Kick's follow list exceeded the page limit. Nothing was imported.");
    }

    internal static string BuildPageUrl(string cursor) =>
        "https://kick.com/api/v2/channels/followed" +
        (cursor.Length == 0 ? "" : "?cursor=" + Uri.EscapeDataString(cursor));

    internal static (IReadOnlyList<string> Channels, string NextCursor) ReadPage(string body)
    {
        const string error = "Kick returned an unrecognized follow list. Nothing was imported.";
        if (body.Length > MaximumResponseBytes) throw new InvalidOperationException(error);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("channels", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(error);

            var channels = new List<string>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("channel_slug", out var slug) || slug.ValueKind != JsonValueKind.String ||
                    !StreamInputParser.TryFromChannel(PlatformKind.Kick, slug.GetString()!, out var target))
                    throw new InvalidOperationException(error);
                channels.Add(target.Channel);
            }
            // The site's getNextPageParam ends on a missing/null/empty cursor;
            // buildUrl stringifies a present cursor before adding it to the query.
            var cursor = "";
            if (root.TryGetProperty("nextCursor", out var next) && next.ValueKind != JsonValueKind.Null)
            {
                if (next.ValueKind == JsonValueKind.String) cursor = next.GetString()!;
                else if (next.ValueKind == JsonValueKind.Number && next.TryGetInt64(out var number) && number >= 0)
                    cursor = number == 0 ? "" : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                else throw new InvalidOperationException(error);
            }
            if (cursor.Length > 4096 || (channels.Count == 0 && cursor.Length != 0))
                throw new InvalidOperationException(error);
            return (channels, cursor);
        }
        catch (JsonException)
        {
            // Do not include private response bodies in errors or logs.
            throw new InvalidOperationException(error);
        }
    }
}
