using StreamlinkVlcStudio.Core.Json;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Text;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Twitch;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class StreamSearchService : IStreamSearchService
{
    private const int MinimumDiscoveryQueryLength = 3;
    private const int StreamProbeConcurrency = 4;
    private const string TwitchWebsiteSearchQuery = """
        query StreamStudioChannelSearch($query: String!, $first: Int!) {
          searchFor(userQuery: $query, platform: "web", options: { targets: [{ index: CHANNEL, limit: $first }] }) {
            channels {
              edges {
                item {
                  ... on User {
                    login
                    displayName
                    profileImageURL(width: 150)
                    stream { id viewersCount game { name } }
                    broadcastSettings { title }
                  }
                }
              }
            }
          }
        }
        """;
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(TimeSpan.FromSeconds(20));
    private static readonly TimeSpan CurlTimeout = TimeSpan.FromSeconds(12);
    private readonly IAppLogger logger;
    private readonly IStreamlinkService streamlinkService;
    private readonly HttpClient httpClient;
    private readonly KickWebsiteJsonReader kickWebsiteJsonReader;

    public StreamSearchService(IAppLogger logger, IStreamlinkService streamlinkService)
        : this(logger, streamlinkService, SharedHttpClient)
    {
    }

    public StreamSearchService(IAppLogger logger, IStreamlinkService streamlinkService, HttpClient httpClient)
        : this(logger, streamlinkService, httpClient, null)
    {
    }

    public StreamSearchService(
        IAppLogger logger,
        IStreamlinkService streamlinkService,
        HttpClient httpClient,
        Func<string, string, CancellationToken, Task<string?>>? kickCurlJsonReader)
    {
        this.logger = logger;
        this.streamlinkService = streamlinkService;
        this.httpClient = httpClient;
        kickWebsiteJsonReader = new KickWebsiteJsonReader(
            httpClient,
            logger,
            "Search",
            CurlTimeout,
            kickCurlJsonReader);
    }

    public async Task<StreamSearchResult> SearchAsync(
        StreamSearchRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var query = (request.Query ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new StreamSearchResult(StreamSearchResultStatus.NotFound, [], "Enter a Twitch or Kick channel.");
        }

        IReadOnlyList<StreamTarget> exactCandidates;
        try
        {
            exactCandidates = StreamInputParser.ParseCandidates(query);
        }
        catch (ArgumentException ex)
        {
            return new StreamSearchResult(StreamSearchResultStatus.NotFound, [], ex.Message);
        }

        var discoveries = new List<DiscoveredChannel>();
        var messages = new List<string>();
        var explicitPlatform = IsExplicitPlatformSearch(query);
        var discoveryQuery = NormalizeForMatch(query);
        var order = 0;

        if (explicitPlatform)
        {
            discoveries.Add(ToExactDiscovery(exactCandidates[0], order++));
        }
        else if (discoveryQuery.Length >= MinimumDiscoveryQueryLength)
        {
            var twitchTask = SearchTwitchChannelsAsync(discoveryQuery, request.PageSize, settings, cancellationToken);
            var kickTask = SearchKickChannelsAsync(discoveryQuery, request.PageSize, cancellationToken);

            await Task.WhenAll(twitchTask, kickTask).ConfigureAwait(false);
            var twitch = await twitchTask.ConfigureAwait(false);
            var kick = await kickTask.ConfigureAwait(false);
            messages.AddRange(twitch.Messages);
            messages.AddRange(kick.Messages);

            foreach (var channel in twitch.Channels)
            {
                discoveries.Add(channel with { Order = order++ });
            }

            foreach (var channel in kick.Channels)
            {
                discoveries.Add(channel with { Order = order++ });
            }

            AddExactFallbacksForMissingPlatforms(discoveries, exactCandidates, ref order);
        }
        else
        {
            foreach (var target in exactCandidates)
            {
                discoveries.Add(ToExactDiscovery(target, order++));
            }
        }

        var distinct = discoveries
            .GroupBy(discovery => $"{discovery.Platform}:{discovery.Channel}", StringComparer.OrdinalIgnoreCase)
            .Select(group => SelectBestDiscovery(group, query))
            .OrderBy(discovery => MatchRank(discovery, query))
            // Keep provider relevance before applying the limit, including offline partial matches.
            .ThenBy(discovery => discovery.Order)
            .Take(NormalizePageSize(request.PageSize))
            .ToArray();

        if (distinct.Length == 0)
        {
            var message = messages.Count > 0
                ? string.Join(" ", messages)
                : $"No Twitch or Kick channels found for {query}.";
            return new StreamSearchResult(StreamSearchResultStatus.NotFound, [], message);
        }

        var customArguments = CommandLineTokenizer.Tokenize(settings.CustomStreamlinkArguments);
        var channels = await CreateSearchChannelsAsync(
            distinct,
            request.Quality,
            settings,
            customArguments,
            cancellationToken).ConfigureAwait(false);

        var orderedChannels = channels
            .OrderBy(channel => channel.IsLive ? 0 : 1)
            .ThenBy(channel => MatchRank(channel.Channel, channel.DisplayName, query))
            .ThenBy(channel => channel.State == StreamSearchChannelState.Offline ? 0 : 1)
            .ThenBy(channel => channel.Order)
            .ToArray();

        var resultMessage = FormatResultMessage(query, orderedChannels, messages);
        return new StreamSearchResult(StreamSearchResultStatus.Available, orderedChannels, resultMessage);
    }

    private async Task<TwitchSearchLoad> SearchTwitchChannelsAsync(
        string query,
        int pageSize,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        // Helix only matches the beginning of a login. Website search also finds names
        // such as iiTzTimmy for "timmy", and works without a configured OAuth token.
        var websiteSearch = await SearchTwitchWebsiteChannelsAsync(query, pageSize, cancellationToken).ConfigureAwait(false);
        if (websiteSearch is { Channels.Count: > 0 })
        {
            return websiteSearch;
        }

        var token = TwitchOAuthService.NormalizeOAuthToken(settings.Chat.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return websiteSearch ?? new TwitchSearchLoad([], ["Twitch channel search is unavailable."]);
        }

        var clientId = await TwitchClientIdResolver.ResolveAsync(
            settings.Chat,
            httpClient,
            token,
            logger,
            "Search",
            "Twitch token validation failed for channel search.",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new TwitchSearchLoad([], ["Twitch channel discovery requires a Twitch Client ID that matches the OAuth token."]);
        }

        try
        {
            var url = BuildTwitchSearchUrl(query, pageSize);
            using var request = TwitchApiRequest.Create(HttpMethod.Get, url, token, clientId);
            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
            var body = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "Search",
                    $"Twitch channel search failed for {query}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(body, includeBodyFallback: false)}");
                return new TwitchSearchLoad([], ["Twitch channel search is unavailable."]);
            }

            using var document = JsonDocument.Parse(body);
            return new TwitchSearchLoad(ReadTwitchChannels(document.RootElement).ToArray(), []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Twitch channel search failed for {query}.", ex);
            return new TwitchSearchLoad([], ["Twitch channel search is unavailable."]);
        }
    }

    private async Task<TwitchSearchLoad?> SearchTwitchWebsiteChannelsAsync(
        string query,
        int pageSize,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                query = TwitchWebsiteSearchQuery,
                variables = new { query, first = NormalizePageSize(pageSize) }
            });
            using var document = await new TwitchGraphQlTransport(httpClient).SendAsync(
                payload,
                TwitchGraphQlTransport.PublicClientId,
                TwitchGraphQlTransport.CreateDeviceId(),
                cancellationToken).ConfigureAwait(false);
            var channels = document.RootElement
                .GetProperty("data")
                .GetProperty("searchFor")
                .GetProperty("channels");
            if (!JsonElementReader.TryGetArray(channels, "edges", out var edges))
            {
                throw new JsonException("Twitch channel search did not return channel results.");
            }

            return new TwitchSearchLoad(ReadTwitchWebsiteChannels(edges).Take(NormalizePageSize(pageSize)).ToArray(), []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Twitch website channel search failed for {query}; trying Helix.", ex);
            return null;
        }
    }

    private async Task<KickSearchLoad> SearchKickChannelsAsync(
        string query,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var url = $"https://kick.com/api/search?searched_word={Uri.EscapeDataString(query)}";
        try
        {
            var body = await kickWebsiteJsonReader
                .ReadAsync(url, "https://kick.com/", cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return new KickSearchLoad([], ["Kick channel search is unavailable."]);
            }

            using var document = JsonDocument.Parse(body);
            return new KickSearchLoad(ReadKickSearchChannels(document.RootElement).Take(NormalizePageSize(pageSize)).ToArray(), []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Kick channel search failed for {query}.", ex);
            return new KickSearchLoad([], ["Kick channel search is unavailable."]);
        }
    }

    private async Task<StreamSearchChannel> CreateSearchChannelAsync(
        DiscoveredChannel discovery,
        string quality,
        AppSettings settings,
        IReadOnlyList<string> customArguments,
        CancellationToken cancellationToken)
    {
        if (discovery.IsLive == false)
        {
            return ToSearchChannel(
                discovery,
                StreamSearchChannelState.Offline,
                StreamSearchSourceStatus.Available,
                "Offline. Open VODs.",
                canPlay: false);
        }

        if (discovery.IsLive == true)
        {
            return ToSearchChannel(
                discovery,
                StreamSearchChannelState.Live,
                StreamSearchSourceStatus.Available,
                "Live. Playback can be attempted.",
                canPlay: true);
        }

        if (string.IsNullOrWhiteSpace(settings.StreamlinkPath))
        {
            return ToSearchChannel(
                discovery,
                StreamSearchChannelState.Unavailable,
                StreamSearchSourceStatus.NotConfigured,
                "Configure the Streamlink executable path in Settings.",
                canPlay: false);
        }

        try
        {
            var target = new StreamTarget(discovery.Platform, discovery.Channel, discovery.Url);
            var transport = new StreamTransportRequest(
                target,
                string.IsNullOrWhiteSpace(quality) ? settings.DefaultQuality : quality,
                settings.StreamlinkPath,
                settings.LowLatency,
                customArguments);
            var probe = await streamlinkService.ProbeStreamsAsync(transport, cancellationToken).ConfigureAwait(false);
            if (probe.HasPlayableStream)
            {
                return ToSearchChannel(
                    discovery,
                    StreamSearchChannelState.Live,
                    StreamSearchSourceStatus.Available,
                    string.IsNullOrWhiteSpace(probe.Message) ? "Live. Playable stream found." : probe.Message,
                    canPlay: true);
            }

            return ToSearchChannel(
                discovery,
                StreamSearchChannelState.Unavailable,
                StreamSearchSourceStatus.Unavailable,
                string.IsNullOrWhiteSpace(probe.Message) ? "Streamlink did not find a playable stream." : probe.Message,
                canPlay: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Streamlink probe failed for {discovery.Platform}: {discovery.Channel}.", ex);
            return ToSearchChannel(
                discovery,
                StreamSearchChannelState.Unavailable,
                StreamSearchSourceStatus.Unavailable,
                ex.Message,
                canPlay: false);
        }
    }

    private async Task<StreamSearchChannel[]> CreateSearchChannelsAsync(
        IReadOnlyList<DiscoveredChannel> discoveries,
        string quality,
        AppSettings settings,
        IReadOnlyList<string> customArguments,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(StreamProbeConcurrency);
        var tasks = discoveries.Select(async discovery =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await CreateSearchChannelAsync(
                    discovery,
                    quality,
                    settings,
                    customArguments,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static IEnumerable<DiscoveredChannel> ReadTwitchChannels(JsonElement root)
    {
        if (!JsonElementReader.TryGetArray(root, "data", out var data))
        {
            yield break;
        }

        var order = 0;
        foreach (var item in data.EnumerateArray())
        {
            var login = GetOptionalString(item, "broadcaster_login").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(login) ||
                !StreamInputParser.TryFromChannel(PlatformKind.Twitch, login, out var target))
            {
                continue;
            }

            yield return new DiscoveredChannel(
                PlatformKind.Twitch,
                target.Channel,
                FirstNonEmpty(GetOptionalString(item, "display_name"), target.Channel),
                target.Url,
                GetOptionalString(item, "thumbnail_url"),
                GetOptionalString(item, "title"),
                GetOptionalString(item, "game_name"),
                TryGetBool(item, "is_live") == true,
                order++);
        }
    }

    private static IEnumerable<DiscoveredChannel> ReadTwitchWebsiteChannels(JsonElement edges)
    {
        var order = 0;
        foreach (var edge in edges.EnumerateArray())
        {
            if (edge.ValueKind != JsonValueKind.Object ||
                !edge.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object ||
                !StreamInputParser.TryFromChannel(PlatformKind.Twitch, GetOptionalString(item, "login").ToLowerInvariant(), out var target))
            {
                continue;
            }

            var stream = item.TryGetProperty("stream", out var streamElement) ? streamElement : default;
            bool? isLive = stream.ValueKind switch
            {
                JsonValueKind.Object => true,
                JsonValueKind.Null => false,
                _ => null
            };
            var viewers = TryGetInt32(stream, "viewersCount");
            yield return new DiscoveredChannel(
                PlatformKind.Twitch,
                target.Channel,
                FirstNonEmpty(GetOptionalString(item, "displayName"), target.Channel),
                target.Url,
                GetOptionalString(item, "profileImageURL"),
                TryReadNestedString(item, "broadcastSettings", "title"),
                TryReadNestedString(stream, "game", "name"),
                isLive,
                order++,
                isLive == true && viewers is { } count ? Math.Max(0, count) : null);
        }
    }

    private static IEnumerable<DiscoveredChannel> ReadKickSearchChannels(JsonElement root)
    {
        if (!JsonElementReader.TryGetArray(root, "channels", out var channels))
        {
            yield break;
        }

        var order = 0;
        foreach (var item in channels.EnumerateArray())
        {
            if (TryReadKickChannel(item, order++, out var channel))
            {
                yield return channel;
            }
        }
    }

    private static bool TryReadKickChannel(JsonElement item, int order, out DiscoveredChannel channel)
    {
        channel = default!;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var slug = GetOptionalString(item, "slug").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(slug) ||
            !StreamInputParser.TryFromChannel(PlatformKind.Kick, slug, out var target))
        {
            return false;
        }

        var user = item.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.Object
            ? userElement
            : default;
        var livestream = item.TryGetProperty("livestream", out var livestreamElement) && livestreamElement.ValueKind == JsonValueKind.Object
            ? livestreamElement
            : default;

        var isLive = TryGetBool(item, "isLive") ??
            TryGetBool(item, "is_live") ??
            (livestream.ValueKind == JsonValueKind.Object ? TryGetBool(livestream, "is_live") : null) ??
            false;
        var viewerCount = livestream.ValueKind == JsonValueKind.Object
            ? TryGetInt32(livestream, "viewer_count")
            : null;

        channel = new DiscoveredChannel(
            PlatformKind.Kick,
            target.Channel,
            FirstNonEmpty(
                user.ValueKind == JsonValueKind.Object ? GetOptionalString(user, "username") : "",
                GetOptionalString(item, "username"),
                target.Channel),
            target.Url,
            FirstNonEmpty(
                user.ValueKind == JsonValueKind.Object ? GetOptionalString(user, "profilePic") : "",
                user.ValueKind == JsonValueKind.Object ? GetOptionalString(user, "profile_pic") : "",
                GetOptionalString(item, "profile_pic"),
                GetOptionalString(item, "thumbnail")),
            FirstNonEmpty(
                GetOptionalString(item, "stream_title"),
                livestream.ValueKind == JsonValueKind.Object ? GetOptionalString(livestream, "session_title") : ""),
            FirstNonEmpty(
                TryReadFirstArrayObjectString(item, "recentCategories", "name"),
                TryReadFirstArrayObjectString(item, "categories", "name"),
                TryReadNestedString(item, "category", "name"),
                livestream.ValueKind == JsonValueKind.Object ? TryReadNestedString(livestream, "category", "name") : ""),
            isLive,
            order,
            viewerCount is { } value ? Math.Max(0, value) : null);
        return true;
    }

    private static void AddExactFallbacksForMissingPlatforms(
        List<DiscoveredChannel> discoveries,
        IReadOnlyList<StreamTarget> exactCandidates,
        ref int order)
    {
        foreach (var target in exactCandidates)
        {
            if (discoveries.Any(discovery => discovery.Platform == target.Platform))
            {
                continue;
            }

            discoveries.Add(ToExactDiscovery(target, order++));
        }
    }

    private static DiscoveredChannel SelectBestDiscovery(IEnumerable<DiscoveredChannel> group, string query)
    {
        return group
            .OrderBy(discovery => MatchRank(discovery, query))
            .ThenBy(discovery => LiveRank(discovery.IsLive))
            .ThenBy(discovery => discovery.Order)
            .First();
    }

    private static StreamSearchChannel ToSearchChannel(
        DiscoveredChannel discovery,
        StreamSearchChannelState state,
        StreamSearchSourceStatus sourceStatus,
        string statusMessage,
        bool canPlay)
    {
        return new StreamSearchChannel(
            discovery.Platform,
            discovery.Channel,
            discovery.DisplayName,
            discovery.Url,
            discovery.ThumbnailUrl,
            discovery.Title,
            discovery.CategoryName,
            state,
            sourceStatus,
            statusMessage,
            canPlay,
            discovery.Order,
            discovery.IsLive,
            discovery.ViewerCount,
            discovery.ThumbnailUrl);
    }

    private static DiscoveredChannel ToExactDiscovery(StreamTarget target, int order)
    {
        return new DiscoveredChannel(
            target.Platform,
            target.Channel,
            target.Channel,
            target.Url,
            "",
            "",
            "",
            null,
            order);
    }

    private static bool IsExplicitPlatformSearch(string query)
    {
        return StreamInputParser.TryParsePlatformUrl(query, out _);
    }

    private static int MatchRank(DiscoveredChannel channel, string query)
    {
        return MatchRank(channel.Channel, channel.DisplayName, query);
    }

    private static int MatchRank(string channel, string displayName, string query)
    {
        var normalizedQuery = NormalizeForMatch(query);
        var normalizedChannel = NormalizeForMatch(channel);
        var normalizedDisplay = NormalizeForMatch(displayName);
        if (string.Equals(normalizedChannel, normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedDisplay, normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        // Prefixes and substrings share a tier so relevant substring matches are not
        // pushed out of the page by less relevant names that happen to start with the query.
        if (normalizedChannel.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            normalizedDisplay.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static int LiveRank(bool? isLive) => isLive == true ? 0 : isLive == false ? 1 : 2;

    private static string FormatResultMessage(
        string query,
        IReadOnlyList<StreamSearchChannel> channels,
        IReadOnlyList<string> sourceMessages)
    {
        var live = channels.Count(channel => channel.IsLive);
        var offline = channels.Count(channel => !channel.IsLive && channel.State == StreamSearchChannelState.Offline);
        var unavailable = channels.Count - live - offline;
        var summary = StreamSearchSummary.Format(query, live, offline, unavailable);
        return sourceMessages.Count == 0
            ? summary
            : $"{summary} {string.Join(" ", sourceMessages.Distinct(StringComparer.Ordinal))}";
    }

    private static string BuildTwitchSearchUrl(string query, int pageSize)
    {
        var normalizedPageSize = NormalizePageSize(pageSize);
        var builder = new StringBuilder("https://api.twitch.tv/helix/search/channels?");
        builder.Append("query=");
        builder.Append(Uri.EscapeDataString(query));
        builder.Append("&first=");
        builder.Append(normalizedPageSize.ToString(CultureInfo.InvariantCulture));
        builder.Append("&live_only=false");
        return builder.ToString();
    }

    private static int NormalizePageSize(int pageSize) => Math.Clamp(pageSize <= 0 ? 10 : pageSize, 1, 100);

    private static string NormalizeForMatch(string value)
    {
        return (value ?? "").Trim().TrimStart('@').Trim('/').ToLowerInvariant();
    }

    private sealed record TwitchSearchLoad(IReadOnlyList<DiscoveredChannel> Channels, IReadOnlyList<string> Messages);

    private sealed record KickSearchLoad(IReadOnlyList<DiscoveredChannel> Channels, IReadOnlyList<string> Messages);

    private sealed record DiscoveredChannel(
        PlatformKind Platform,
        string Channel,
        string DisplayName,
        string Url,
        string ThumbnailUrl,
        string Title,
        string CategoryName,
        bool? IsLive,
        int Order,
        int? ViewerCount = null);
}
