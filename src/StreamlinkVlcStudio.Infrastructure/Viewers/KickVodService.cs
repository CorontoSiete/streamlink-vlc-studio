using System.Globalization;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class KickVodService : IKickVodService
{
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(TimeSpan.FromSeconds(20));
    private static readonly TimeSpan CurlTimeout = TimeSpan.FromSeconds(15);
    private readonly IAppLogger logger;
    private readonly KickWebsiteJsonReader kickWebsiteJsonReader;

    public KickVodService(IAppLogger logger)
        : this(logger, SharedHttpClient)
    {
    }

    public KickVodService(IAppLogger logger, HttpClient httpClient)
        : this(logger, httpClient, null)
    {
    }

    public KickVodService(
        IAppLogger logger,
        HttpClient httpClient,
        Func<string, string, CancellationToken, Task<string?>>? kickCurlJsonReader)
    {
        this.logger = logger;
        kickWebsiteJsonReader = new KickWebsiteJsonReader(
            httpClient,
            logger,
            "VODs",
            CurlTimeout,
            kickCurlJsonReader);
    }

    public async Task<KickVodSearchResult> SearchAsync(
        KickVodSearchRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var channel = NormalizeKickChannel(request.Channel);
        if (string.IsNullOrWhiteSpace(channel))
        {
            return new KickVodSearchResult(
                KickVodSearchStatus.NotFound,
                [],
                "",
                "Enter a Kick channel slug.");
        }

        var pageSize = NormalizePageSize(request.PageSize);
        var url = BuildVideosUrl(channel, request.Cursor, pageSize);
        var referrer = $"https://kick.com/{Uri.EscapeDataString(channel)}";
        try
        {
            var body = await kickWebsiteJsonReader
                .ReadAsync(url, referrer, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return new KickVodSearchResult(
                    KickVodSearchStatus.Unavailable,
                    [],
                    "",
                    $"Kick VODs unavailable for {channel}.");
            }

            using var document = JsonDocument.Parse(body);
            var videos = ReadVideos(document.RootElement, channel, pageSize).ToArray();
            if (videos.Any(video => string.IsNullOrWhiteSpace(video.ProfileImageUrl)))
            {
                var channelProfileImage = await TryReadKickChannelProfileImageAsync(
                    channel,
                    referrer,
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(channelProfileImage))
                {
                    videos = videos
                        .Select(video => string.IsNullOrWhiteSpace(video.ProfileImageUrl)
                            ? video with { ProfileImageUrl = channelProfileImage }
                            : video)
                        .ToArray();
                }
            }

            var message = videos.Length switch
            {
                0 => $"No Kick VODs found for {channel}.",
                1 => $"1 Kick VOD found for {channel}.",
                _ => $"{videos.Length} Kick VODs found for {channel}."
            };
            return new KickVodSearchResult(
                KickVodSearchStatus.Available,
                videos,
                ReadNextCursor(document.RootElement),
                message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "VODs", $"Kick VOD search failed for {channel}.", ex);
            return new KickVodSearchResult(
                KickVodSearchStatus.Unavailable,
                [],
                "",
                $"Kick VODs unavailable for {channel}.");
        }
    }

    private async Task<string> TryReadKickChannelProfileImageAsync(
        string channel,
        string referrer,
        CancellationToken cancellationToken)
    {
        var url = $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel)}";
        try
        {
            var body = await kickWebsiteJsonReader
                .ReadAsync(url, referrer, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return "";
            }

            using var document = JsonDocument.Parse(body);
            return ReadProfileImageUrl(document.RootElement, default);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Info, "VODs", $"Kick channel profile image lookup failed for {channel}: {ex.Message}");
            return "";
        }
    }

    private static IEnumerable<KickVodItem> ReadVideos(JsonElement root, string channel, int pageSize)
    {
        var videos = EnumerateVideoElements(root)
            .Select(item => TryReadVideo(item, channel))
            .Where(item => item is not null)
            .Select(item => item!)
            .Take(pageSize);

        foreach (var video in videos)
        {
            yield return video;
        }
    }

    private static IEnumerable<JsonElement> EnumerateVideoElements(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var propertyName in new[] { "data", "videos", "items" })
        {
            if (!root.TryGetProperty(propertyName, out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }

            yield break;
        }
    }

    private static KickVodItem? TryReadVideo(JsonElement item, string channel)
    {
        var source = GetOptionalString(item, "source");
        if (string.IsNullOrWhiteSpace(source))
        {
            source = TryReadNestedString(item, "video", "source");
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) ||
            (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        source = sourceUri.ToString();

        var video = item.TryGetProperty("video", out var videoElement) && videoElement.ValueKind == JsonValueKind.Object
            ? videoElement
            : default;
        var uuid = FirstNonEmpty(
            video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "uuid") : "",
            GetOptionalString(item, "uuid"),
            GetOptionalString(item, "slug"),
            GetOptionalString(item, "id"));
        var id = FirstNonEmpty(GetOptionalString(item, "id"), video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "id") : "", uuid);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var liveStreamId = FirstNonEmpty(
            GetOptionalString(item, "live_stream_id"),
            video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "live_stream_id") : "",
            id);
        var title = FirstNonEmpty(
            GetOptionalString(item, "session_title"),
            GetOptionalString(item, "title"),
            video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "title") : "",
            "Untitled Kick VOD");
        var thumbnail = FirstNonEmpty(
            TryReadNestedString(item, "thumbnail", "src"),
            GetOptionalString(item, "thumbnail"),
            video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "thumb") : "");
        var category = FirstNonEmpty(
            TryReadFirstArrayObjectString(item, "categories", "name"),
            TryReadNestedString(item, "category", "name"));
        var startedAt = TryGetDateTimeOffset(item, "start_time") ??
            TryGetDateTimeOffset(item, "started_at");
        var createdAt = TryGetDateTimeOffset(item, "created_at") ??
            (video.ValueKind == JsonValueKind.Object ? TryGetDateTimeOffset(video, "created_at") : null);
        var duration = ReadDuration(item);
        if (duration == TimeSpan.Zero && video.ValueKind == JsonValueKind.Object)
        {
            duration = ReadDuration(video);
        }
        var views = TryGetInt32(item, "views") ??
            TryGetInt32(item, "viewer_count") ??
            (video.ValueKind == JsonValueKind.Object ? TryGetInt32(video, "views") : null);
        var channelId = FirstNonEmpty(
            GetOptionalString(item, "channel_id"),
            TryReadNestedString(item, "channel", "id"),
            video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "channel_id") : "",
            video.ValueKind == JsonValueKind.Object ? TryReadNestedString(video, "channel", "id") : "");
        var profileImage = ReadProfileImageUrl(item, video);

        return new KickVodItem(
            id,
            liveStreamId,
            uuid,
            channel,
            channel,
            title,
            string.IsNullOrWhiteSpace(uuid)
                ? $"https://kick.com/{channel}/videos/{id}"
                : $"https://kick.com/{channel}/videos/{uuid}",
            source,
            thumbnail,
            category,
            createdAt,
            startedAt,
            duration,
            views,
            channelId,
            profileImage);
    }

    private static string ReadProfileImageUrl(JsonElement item, JsonElement video)
    {
        var data = GetObjectProperty(item, "data");
        var profileImage = FirstNonEmpty(
            ReadProfileImageFields(item),
            ReadProfileImagePath(item, "user"),
            ReadProfileImagePath(item, "channel"),
            ReadProfileImagePath(item, "channel", "user"),
            ReadProfileImagePath(item, "creator"),
            ReadProfileImageFields(video),
            ReadProfileImagePath(video, "user"),
            ReadProfileImagePath(video, "channel"),
            ReadProfileImagePath(video, "channel", "user"),
            ReadProfileImageFields(data),
            ReadProfileImagePath(data, "user"),
            ReadProfileImagePath(data, "channel"),
            ReadProfileImagePath(data, "channel", "user"));

        return NormalizeImageUrl(profileImage);
    }

    private static string ReadProfileImageFields(JsonElement element)
    {
        return FirstNonEmpty(
            GetOptionalString(element, "profile_picture"),
            GetOptionalString(element, "profile_pic"),
            GetOptionalString(element, "profilePic"),
            GetOptionalString(element, "profile_image_url"));
    }

    private static string ReadProfileImagePath(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var propertyName in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(propertyName, out current) ||
                current.ValueKind != JsonValueKind.Object)
            {
                return "";
            }
        }

        return ReadProfileImageFields(current);
    }

    private static JsonElement GetObjectProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Object
            ? property
            : default;
    }

    private static TimeSpan ReadDuration(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return TimeSpan.Zero;
        }

        if (element.TryGetProperty("duration_seconds", out var seconds) &&
            TryGetPositiveDuration(seconds, TimeSpan.TicksPerSecond, out var duration))
        {
            return duration;
        }

        if (element.TryGetProperty("duration", out var milliseconds) &&
            TryGetPositiveDuration(milliseconds, TimeSpan.TicksPerMillisecond, out duration))
        {
            return duration;
        }

        return TimeSpan.Zero;
    }

    private static string BuildVideosUrl(string channel, string cursor, int pageSize)
    {
        var query = new List<string>
        {
            $"limit={pageSize.ToString(CultureInfo.InvariantCulture)}"
        };
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query.Add($"cursor={Uri.EscapeDataString(cursor.Trim())}");
        }

        return $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel)}/videos?{string.Join('&', query)}";
    }

    private static string ReadNextCursor(JsonElement root)
    {
        var cursor = FirstNonEmpty(
            ReadPaginationCursor(root, "next_cursor"),
            ReadPaginationCursor(root, "cursor"),
            GetOptionalString(root, "next_cursor"),
            GetOptionalString(root, "cursor"));
        if (!string.IsNullOrWhiteSpace(cursor) ||
            root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            return cursor;
        }

        return FirstNonEmpty(
            GetOptionalString(data, "next_cursor"),
            GetOptionalString(data, "cursor"),
            ReadPaginationCursor(data, "next_cursor"),
            ReadPaginationCursor(data, "cursor"));
    }

    private static int NormalizePageSize(int pageSize) => Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 100);

    private static string NormalizeKickChannel(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "";
        }

        if (StreamInputParser.TryParsePlatformUrl(trimmed, out var target))
        {
            return target?.Platform == PlatformKind.Kick
                ? target.Channel.ToLowerInvariant()
                : "";
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return "";
        }

        try
        {
            return StreamInputParser.FromChannel(PlatformKind.Kick, trimmed).Channel.ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

}
