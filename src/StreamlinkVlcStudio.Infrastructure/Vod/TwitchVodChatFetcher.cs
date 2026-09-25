using System.Globalization;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Text;
using StreamlinkVlcStudio.Core.Time;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Twitch;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Vod;

/// <summary>
/// Reads Twitch VOD chat from the public <c>VideoCommentsByOffsetOrCursor</c> persisted query.
/// </summary>
/// <remarks>
/// <para>
/// Paging is done purely with <c>contentOffsetSeconds</c>. Twitch rejects the edge
/// <c>cursor</c> variable with <c>IntegrityCheckFailed</c> unless the caller supplies a
/// Client-Integrity token, which only the real web client can mint; offset requests are still
/// served anonymously. Cursor paging therefore cannot be used here at all.
/// </para>
/// <para>
/// Twitch answers an offset request with the page of comments *around* that offset, so a page can
/// both start slightly before the requested offset and end slightly before it. The frontier is
/// advanced with <see cref="ResolveCoveredThroughOffset"/>, which always moves past the requested
/// offset so repeated polling cannot stall on a page that does not reach forward.
/// </para>
/// </remarks>
internal sealed class TwitchVodChatFetcher
{
    private const string LiveDvrReplayIdPrefix = "live-dvr-";
    private const string OperationName = "VideoCommentsByOffsetOrCursor";
    private const string PersistedQueryHash = "b70a3591ff0f4e0313d126c6a1502d79a1c02baebb288227c582044aa76adf6a";
    private const int MaximumVodIdLength = 32;
    private const int MaximumEmoteCodeLength = 96;
    private const int MaximumEmoteIdLength = 256;

    /// <summary>How far to advance when a page carries no comment that reaches past the request.</summary>
    private static readonly TimeSpan MinimumFrontierStep = TimeSpan.FromSeconds(1);

    /// <summary>How far to advance when Twitch returns an empty page that claims more pages exist.</summary>
    private static readonly TimeSpan EmptyPageFrontierStep = TimeSpan.FromSeconds(30);

    private readonly TwitchGraphQlTransport transport;
    private readonly string deviceId = TwitchGraphQlTransport.CreateDeviceId();

    public TwitchVodChatFetcher(HttpClient httpClient)
    {
        transport = new TwitchGraphQlTransport(httpClient);
    }

    public async Task<VodChatFetchResult> FetchAsync(
        ReplaySessionInfo replay,
        TimeSpan fromOffset,
        CancellationToken cancellationToken)
    {
        if (IsLiveDvrReplay(replay))
        {
            return VodChatFetchResult.Unsupported(
                "Twitch has not published VOD comments for the stream that is still live, " +
                "so only chat captured while watching can be replayed.");
        }

        var vodId = replay.ReplayId.Trim();
        if (!IsVodId(vodId))
        {
            return VodChatFetchResult.Unsupported(
                "Twitch VOD chat needs a numeric video id and this replay does not have one.");
        }

        if (fromOffset < TimeSpan.Zero)
        {
            fromOffset = TimeSpan.Zero;
        }

        if (!DurationValues.TryAdd(fromOffset, EmptyPageFrontierStep, out var emptyPageFrontier))
        {
            return VodChatFetchResult.Unsupported("The Twitch VOD chat time range falls outside the supported duration range.");
        }

        JsonDocument document;
        try
        {
            document = await transport
                .SendAsync(BuildPayload(vodId, fromOffset), TwitchGraphQlTransport.PublicClientId, deviceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TwitchGraphQlHttpException ex)
        {
            return VodChatFetchResult.Failed(
                $"Twitch returned {(int)ex.StatusCode} {ex.ReasonPhrase} for VOD chat. " +
                GraphQlErrorReader.ExtractResponseMessage(ex.ResponseBody));
        }
        catch (TwitchGraphQlRejectedException ex)
        {
            return VodChatFetchResult.Failed($"Twitch rejected the VOD chat request: {ex.GraphQlMessage}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return VodChatFetchResult.Failed($"Twitch VOD chat could not be loaded: {ex.Message}");
        }

        using (document)
        {
            var page = ReadPage(document.RootElement, replay);
            if (!page.IsValid)
            {
                return VodChatFetchResult.Failed("Twitch returned an incomplete VOD chat response.");
            }

            if (page.Messages.Count == 0)
            {
                return page.HasNextPage
                    ? VodChatFetchResult.Loaded([], emptyPageFrontier)
                    : VodChatFetchResult.Completed([], emptyPageFrontier);
            }

            var coveredThrough = ResolveCoveredThroughOffset(fromOffset, page.Messages[^1].Offset);
            return page.HasNextPage
                ? VodChatFetchResult.Loaded(page.Messages, coveredThrough)
                : VodChatFetchResult.Completed(page.Messages, coveredThrough);
        }
    }

    /// <summary>
    /// Picks the next frontier so polling always advances. A Twitch page can end before the offset
    /// it was asked for, so the requested offset plus one second is the floor; when the page does
    /// reach further, its last whole second is used so the boundary second is re-requested once and
    /// no comment sharing that second is skipped.
    /// </summary>
    internal static TimeSpan ResolveCoveredThroughOffset(TimeSpan fromOffset, TimeSpan lastMessageOffset)
    {
        if (!DurationValues.TryAdd(fromOffset, MinimumFrontierStep, out var minimum))
        {
            return TimeSpan.MaxValue;
        }

        var lastWholeSecond = TimeSpan.FromSeconds(Math.Floor(lastMessageOffset.TotalSeconds));
        return lastWholeSecond > minimum ? lastWholeSecond : minimum;
    }

    internal static TwitchVodChatPage ReadPage(JsonElement root, ReplaySessionInfo replay)
    {
        var messages = new List<VodChatMessage>();
        var hasNextPage = false;
        var hasPage = false;
        var isValid = true;
        foreach (var comments in EnumerateComments(root))
        {
            hasPage = true;
            if (!TryGetArray(comments, "edges", out var edges) ||
                !comments.TryGetProperty("pageInfo", out var pageInfo) ||
                TryGetBool(pageInfo, "hasNextPage") is not { } nextPage)
            {
                isValid = false;
                continue;
            }

            hasNextPage |= nextPage;
            foreach (var edge in edges.EnumerateArray())
            {
                if (TryReadMessage(edge, replay, out var message))
                {
                    messages.Add(message);
                }
            }
        }

        messages.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        return new TwitchVodChatPage(messages, hasNextPage, hasPage && isValid);
    }

    private static string BuildPayload(string vodId, TimeSpan fromOffset)
    {
        var payload = new[]
        {
            new
            {
                operationName = OperationName,
                variables = new Dictionary<string, object?>
                {
                    ["videoID"] = vodId,
                    ["contentOffsetSeconds"] = (int)Math.Clamp(
                        Math.Floor(fromOffset.TotalSeconds),
                        0,
                        int.MaxValue)
                },
                extensions = new
                {
                    persistedQuery = new
                    {
                        version = 1,
                        sha256Hash = PersistedQueryHash
                    }
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static IEnumerable<JsonElement> EnumerateComments(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                foreach (var nested in EnumerateComments(item))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("video", out var video) ||
            video.ValueKind != JsonValueKind.Object ||
            !video.TryGetProperty("comments", out var comments) ||
            comments.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        yield return comments;
    }

    private static bool TryReadMessage(
        JsonElement edge,
        ReplaySessionInfo replay,
        out VodChatMessage message)
    {
        message = default!;
        if (edge.ValueKind != JsonValueKind.Object ||
            !edge.TryGetProperty("node", out var node) ||
            node.ValueKind != JsonValueKind.Object ||
            !TryGetFiniteDouble(node, "contentOffsetSeconds", out var offsetSeconds) ||
            !TryCreateOffset(offsetSeconds, out var offset))
        {
            return false;
        }

        var body = "";
        string? color = null;
        IReadOnlyList<ChatBadge> badges = [];
        IReadOnlyList<ChatEmote> emotes = [];
        if (node.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.Object)
        {
            body = ReadBody(messageElement);
            color = ReadUntrimmedString(messageElement, "userColor");
            badges = ReadBadges(messageElement);
            emotes = ReadEmotes(messageElement, body);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (!TryResolveTimestamp(node, replay, offset, out var timestamp))
        {
            return false;
        }

        var username = "viewer";
        if (node.TryGetProperty("commenter", out var commenter) &&
            commenter.ValueKind == JsonValueKind.Object)
        {
            username = FirstNonEmpty(
                ReadUntrimmedString(commenter, "displayName"),
                ReadUntrimmedString(commenter, "login"),
                ReadUntrimmedString(commenter, "name"),
                username);
        }

        message = new VodChatMessage(
            offset,
            new ChatMessage(
                PlatformKind.Twitch,
                replay.Channel,
                username,
                body,
                timestamp,
                string.IsNullOrWhiteSpace(color) ? null : color,
                badges.Count > 0 ? badges : null,
                emotes.Count > 0 ? emotes : null,
                RoomId: replay.ChatRoomId,
                MessageId: FirstNonEmpty(
                    ReadUntrimmedString(node, "id"),
                    ReadUntrimmedString(edge, "cursor"))));
        return true;
    }

    private static bool TryResolveTimestamp(
        JsonElement node,
        ReplaySessionInfo replay,
        TimeSpan offset,
        out DateTimeOffset timestamp)
    {
        if (TryGetDateTimeOffset(node, "createdAt", out timestamp))
        {
            return true;
        }

        if (replay.StreamStartedAtUtc is { } startedAt)
        {
            return DurationValues.TryAdd(startedAt, offset, out timestamp);
        }

        timestamp = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>
    /// Rebuilds the message body. Twitch splits it into fragments, so the pieces are concatenated
    /// verbatim — trimming here would drop the spaces that separate fragments.
    /// </summary>
    private static string ReadBody(JsonElement message)
    {
        var body = ReadUntrimmedString(message, "body");
        if (!string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        if (!message.TryGetProperty("fragments", out var fragments) ||
            fragments.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        return string.Concat(fragments
            .EnumerateArray()
            .Select(fragment => ReadUntrimmedString(fragment, "text")));
    }

    private static IReadOnlyList<ChatBadge> ReadBadges(JsonElement message)
    {
        if (!message.TryGetProperty("userBadges", out var badges) ||
            badges.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ChatBadge>();
        foreach (var badge in badges.EnumerateArray())
        {
            if (badge.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadBadgeSetId(badge);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            result.Add(new ChatBadge(
                id,
                NullIfEmpty(ReadUntrimmedString(badge, "version")),
                ChatTextNormalizer.NormalizeBadgeTitle(
                    FirstNonEmpty(
                        ReadUntrimmedString(badge, "title"),
                        ReadUntrimmedString(badge, "name")),
                    TwitchBadgeValues.ResolveTitle(id)),
                NullIfEmpty(FirstNonEmpty(
                    ReadUntrimmedString(badge, "imageURL"),
                    ReadUntrimmedString(badge, "imageUrl"),
                    ReadUntrimmedString(badge, "image_url_4x"),
                    ReadUntrimmedString(badge, "image_url_2x"),
                    ReadUntrimmedString(badge, "image_url_1x")))));
        }

        return result;
    }

    private static string ReadBadgeSetId(JsonElement badge)
    {
        var setId = FirstNonEmpty(
            ReadUntrimmedString(badge, "setID"),
            ReadUntrimmedString(badge, "setId"),
            ReadUntrimmedString(badge, "set_id"),
            ReadUntrimmedString(badge, "name"),
            ReadUntrimmedString(badge, "type"));
        if (!string.IsNullOrWhiteSpace(setId))
        {
            return setId;
        }

        // Twitch also exposes an opaque base64 node id; it is not a badge set name.
        var id = ReadUntrimmedString(badge, "id");
        return LooksLikeOpaqueGraphQlId(id) ? "" : id;
    }

    /// <summary>
    /// Maps Twitch's message fragments onto emote ranges in the rebuilt body. Fragments are matched
    /// in order so a repeated word resolves to its own occurrence rather than the first one.
    /// </summary>
    private static IReadOnlyList<ChatEmote> ReadEmotes(JsonElement message, string body)
    {
        if (body.Length == 0 ||
            !message.TryGetProperty("fragments", out var fragments) ||
            fragments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var emotes = new List<ChatEmote>();
        var cursor = 0;
        foreach (var fragment in fragments.EnumerateArray())
        {
            if (fragment.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = ReadUntrimmedString(fragment, "text");
            if (text.Length == 0)
            {
                continue;
            }

            var startIndex = FindFragmentStart(body, cursor, text);
            if (startIndex < 0)
            {
                continue;
            }

            cursor = startIndex + text.Length;
            AddEmote(emotes, body, startIndex, cursor, ReadEmoteId(fragment));
        }

        return emotes;
    }

    private static string ReadEmoteId(JsonElement fragment)
    {
        foreach (var propertyName in new[] { "emoticon", "emote" })
        {
            if (!fragment.TryGetProperty(propertyName, out var emote) ||
                emote.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // EmbeddedEmote.id identifies the occurrence (for example "1035696;0;3").
            // Only emoteID identifies its CDN image; another occurrence has a different
            // node id even when it uses the exact same image.
            var id = ReadUsableEmoteId(emote, "emoteID", "emoteId", "emoticon_id", "emoticonId", "id");
            if (id.Length > 0)
            {
                return id;
            }
        }

        return ReadUsableEmoteId(fragment, "emoteID", "emoteId", "emoticon_id", "emoticonId");
    }

    private static string ReadUsableEmoteId(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var id = ReadUntrimmedString(element, propertyName).Trim();
            if (id.Length == 0 || id.Length > MaximumEmoteIdLength)
            {
                continue;
            }

            // Preserve numeric legacy ids and modern emotesv2 ids, but never turn an
            // occurrence id or opaque GraphQL node id into a broken image request.
            if (id.All(char.IsAsciiDigit) ||
                (id.StartsWith("emotesv2_", StringComparison.Ordinal) &&
                 id.Length > "emotesv2_".Length &&
                 id["emotesv2_".Length..].All(character =>
                     char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            {
                return id;
            }
        }

        return "";
    }

    private static void AddEmote(
        List<ChatEmote> emotes,
        string body,
        int startIndex,
        int endIndex,
        string id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            startIndex < 0 ||
            endIndex <= startIndex ||
            endIndex > body.Length)
        {
            return;
        }

        var code = body[startIndex..endIndex];
        if (code.Length == 0 ||
            code.Length > MaximumEmoteCodeLength ||
            code.Any(char.IsWhiteSpace))
        {
            return;
        }

        emotes.Add(new ChatEmote(startIndex, endIndex, code, BuildEmoteImageUrl(id)));
    }

    private static int FindFragmentStart(string body, int searchStart, string fragmentText)
    {
        if (searchStart < 0 || searchStart > body.Length)
        {
            return -1;
        }

        if (searchStart + fragmentText.Length <= body.Length &&
            string.CompareOrdinal(body, searchStart, fragmentText, 0, fragmentText.Length) == 0)
        {
            return searchStart;
        }

        return body.IndexOf(fragmentText, searchStart, StringComparison.Ordinal);
    }

    private static string BuildEmoteImageUrl(string id) =>
        $"https://static-cdn.jtvnw.net/emoticons/v2/{Uri.EscapeDataString(id.Trim())}/static/light/2.0";

    private static bool LooksLikeOpaqueGraphQlId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Length >= 4 &&
            normalized.EndsWith('=') &&
            normalized.All(character =>
                char.IsLetterOrDigit(character) ||
                character is '+' or '/' or '-' or '_' or '=');
    }

    /// <summary>Reads a string without trimming, so fragment spacing survives.</summary>
    private static string ReadUntrimmedString(JsonElement element, string propertyName) =>
        GetOptionalString(element, propertyName, trimStrings: false);

    private static bool TryGetFiniteDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        var parsed = property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                property.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
        return parsed && double.IsFinite(value);
    }

    private static bool TryCreateOffset(double seconds, out TimeSpan offset)
    {
        if (seconds == 0)
        {
            offset = TimeSpan.Zero;
            return true;
        }

        return DurationValues.TryCreatePositive(seconds, TimeSpan.TicksPerSecond, out offset);
    }

    private static bool IsVodId(string value) =>
        value.Length is > 0 and <= MaximumVodIdLength &&
        value.All(character => character is >= '0' and <= '9');

    private static bool IsLiveDvrReplay(ReplaySessionInfo replay) =>
        replay.MediaKind == ReplayMediaKind.CurrentLiveDvr ||
        replay.ReplayId.StartsWith(LiveDvrReplayIdPrefix, StringComparison.Ordinal);
}

internal sealed record TwitchVodChatPage(IReadOnlyList<VodChatMessage> Messages, bool HasNextPage, bool IsValid);
