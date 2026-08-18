using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

internal static class BrowsePayloadMapper
{
    public static IEnumerable<BrowseCategory> ReadTwitchCategories(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = GetOptionalString(item, "id");
            var name = GetOptionalString(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            yield return new BrowseCategory(
                PlatformKind.Twitch,
                id,
                name,
                NormalizeImageUrl(GetOptionalString(item, "box_art_url"), "285", "380"),
                []);
        }
    }

    public static IEnumerable<BrowseLiveStream> ReadTwitchStreams(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            var login = GetOptionalString(item, "user_login").Trim();
            if (string.IsNullOrWhiteSpace(login) ||
                !TryCreateTarget(PlatformKind.Twitch, login, out var target))
            {
                continue;
            }

            var displayName = FirstNonEmpty(GetOptionalString(item, "user_name"), target.Channel);
            yield return new BrowseLiveStream(
                PlatformKind.Twitch,
                target.Channel,
                displayName,
                GetOptionalString(item, "title"),
                GetOptionalString(item, "game_id"),
                GetOptionalString(item, "game_name"),
                TryGetInt32(item, "viewer_count"),
                NormalizeImageUrl(GetOptionalString(item, "thumbnail_url"), "440", "248"),
                TryGetDateTimeOffset(item, "started_at"),
                TryGetBool(item, "is_mature"),
                GetOptionalString(item, "language"),
                target.Url);
        }
    }

    public static TwitchStreamViewerCountReadResult ReadTwitchStreamViewerCounts(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new TwitchStreamViewerCountReadResult(
                [],
                "Twitch stream count response did not include stream data.");
        }

        var streams = new List<TwitchStreamViewerCount>();
        foreach (var item in data.EnumerateArray())
        {
            var id = GetOptionalString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return new TwitchStreamViewerCountReadResult(
                    [],
                    "Twitch stream count response included a stream without an id.");
            }

            var gameId = GetOptionalString(item, "game_id");
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return new TwitchStreamViewerCountReadResult(
                    [],
                    "Twitch stream count response included a stream without game_id.");
            }

            var viewerCount = TryGetInt32(item, "viewer_count");
            if (viewerCount is null)
            {
                return new TwitchStreamViewerCountReadResult(
                    [],
                    "Twitch stream count response included a stream without viewer_count.");
            }

            streams.Add(new TwitchStreamViewerCount(id, gameId, Math.Max(0, viewerCount.Value)));
        }

        return new TwitchStreamViewerCountReadResult(streams, null);
    }

    public static IEnumerable<BrowseCategory> ReadKickCategories(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = GetOptionalString(item, "id");
            var name = GetOptionalString(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            yield return new BrowseCategory(
                PlatformKind.Kick,
                id,
                name,
                NormalizeImageUrl(GetOptionalString(item, "thumbnail")),
                ReadTags(item),
                TryGetInt32(item, "viewer_count") is { } viewerCount
                    ? Math.Max(0, viewerCount)
                    : null);
        }
    }

    public static bool TryReadKickCategoryDetail(
        JsonElement root,
        BrowseCategory fallbackCategory,
        out BrowseCategory category,
        out string failureMessage)
    {
        category = fallbackCategory;
        failureMessage = "";
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            failureMessage = $"Kick category viewer counts unavailable. Category '{fallbackCategory.Name}' did not include detail data.";
            return false;
        }

        var tags = ReadTags(data);
        var viewerCount = TryGetInt32(data, "viewer_count");
        category = fallbackCategory with
        {
            Name = FirstNonEmpty(GetOptionalString(data, "name"), fallbackCategory.Name),
            ThumbnailUrl = NormalizeImageUrl(FirstNonEmpty(GetOptionalString(data, "thumbnail"), fallbackCategory.ThumbnailUrl)),
            Tags = tags.Count > 0 ? tags : fallbackCategory.Tags,
            ViewerCount = viewerCount is { } count
                ? Math.Max(0, count)
                : fallbackCategory.ViewerCount
        };
        return true;
    }

    public static IEnumerable<BrowseLiveStream> ReadKickStreams(
        JsonElement root,
        string requestedCategoryId,
        string requestedCategoryName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var slug = GetOptionalString(item, "slug").Trim();
            if (string.IsNullOrWhiteSpace(slug) ||
                !TryCreateTarget(PlatformKind.Kick, slug, out var target))
            {
                continue;
            }

            var categoryId = requestedCategoryId;
            var categoryName = requestedCategoryName;
            if (item.TryGetProperty("category", out var categoryElement) &&
                categoryElement.ValueKind == JsonValueKind.Object)
            {
                categoryId = FirstNonEmpty(GetOptionalString(categoryElement, "id"), categoryId);
                categoryName = FirstNonEmpty(GetOptionalString(categoryElement, "name"), categoryName);
            }

            yield return new BrowseLiveStream(
                PlatformKind.Kick,
                target.Channel,
                target.Channel,
                GetOptionalString(item, "stream_title"),
                categoryId,
                categoryName,
                TryGetInt32(item, "viewer_count"),
                NormalizeImageUrl(GetOptionalString(item, "thumbnail")),
                TryGetDateTimeOffset(item, "started_at"),
                TryGetBool(item, "has_mature_content"),
                GetOptionalString(item, "language"),
                target.Url,
                NormalizeImageUrl(GetOptionalString(item, "profile_picture")));
        }
    }

    public static IEnumerable<BrowseCategory> ReadKickLiveStreamCategories(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("category", out var categoryElement) ||
                categoryElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetOptionalString(categoryElement, "id");
            var name = GetOptionalString(categoryElement, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            yield return new BrowseCategory(
                PlatformKind.Kick,
                id,
                name,
                NormalizeImageUrl(GetOptionalString(categoryElement, "thumbnail")),
                ReadTags(categoryElement),
                TryGetInt32(categoryElement, "viewer_count") is { } viewerCount
                    ? Math.Max(0, viewerCount)
                    : null);
        }
    }

    private static IReadOnlyList<string> ReadTags(JsonElement element)
    {
        if (!element.TryGetProperty("tags", out var tagsElement) ||
            tagsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tags = new List<string>();
        foreach (var tag in tagsElement.EnumerateArray())
        {
            var value = tag.ValueKind switch
            {
                JsonValueKind.String => tag.GetString() ?? "",
                JsonValueKind.Object => GetOptionalString(tag, "name"),
                _ => ""
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                tags.Add(value.Trim());
            }
        }

        return tags;
    }

    private static bool TryCreateTarget(PlatformKind platform, string channel, out StreamTarget target)
    {
        try
        {
            target = StreamInputParser.FromChannel(platform, channel);
            return true;
        }
        catch (ArgumentException)
        {
            target = null!;
            return false;
        }
    }

    internal sealed record TwitchStreamViewerCountReadResult(
        IReadOnlyList<TwitchStreamViewerCount> Streams,
        string? FailureMessage);

    internal sealed record TwitchStreamViewerCount(string Id, string GameId, int ViewerCount);
}
