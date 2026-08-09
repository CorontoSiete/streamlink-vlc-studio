using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;
using static StreamlinkVlcStudio.Infrastructure.Processes.ProcessExtensions;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class StreamSearchService : IStreamSearchService
{
    private const int MinimumDiscoveryQueryLength = 3;
    private const int StreamProbeConcurrency = 4;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly TimeSpan CurlTimeout = TimeSpan.FromSeconds(12);
    private readonly IAppLogger logger;
    private readonly IStreamlinkService streamlinkService;
    private readonly HttpClient httpClient;
    private readonly Func<string, string, CancellationToken, Task<string?>> kickCurlJsonReader;

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
        this.kickCurlJsonReader = kickCurlJsonReader ?? TryReadKickJsonWithCurlAsync;
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
        var order = 0;

        if (explicitPlatform)
        {
            discoveries.Add(ToExactDiscovery(exactCandidates[0], order++));
        }
        else if (query.Length >= MinimumDiscoveryQueryLength)
        {
            var twitchTask = SearchTwitchChannelsAsync(query, request.PageSize, settings, cancellationToken);
            var kickTask = SearchKickChannelsAsync(query, request.PageSize, cancellationToken);

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
            .ThenBy(discovery => LiveRank(discovery.IsLive))
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
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.Chat.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new TwitchSearchLoad([], ["Twitch channel discovery requires a Twitch OAuth token."]);
        }

        var clientId = await ResolveTwitchClientIdAsync(settings.Chat, token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new TwitchSearchLoad([], ["Twitch channel discovery requires a Twitch Client ID that matches the OAuth token."]);
        }

        try
        {
            var url = BuildTwitchSearchUrl(query, pageSize);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Client-Id", clientId);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "Search",
                    $"Twitch channel search failed for {query}: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(body)}");
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

    private async Task<KickSearchLoad> SearchKickChannelsAsync(
        string query,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var url = $"https://kick.com/api/search?searched_word={Uri.EscapeDataString(query)}";
        try
        {
            var body = await TryReadKickJsonWithHttpClientAsync(url, referrer: "https://kick.com/", cancellationToken).ConfigureAwait(false) ??
                await kickCurlJsonReader(url, "https://kick.com/", cancellationToken).ConfigureAwait(false);
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

    private async Task<string?> ResolveTwitchClientIdAsync(
        ChatSettings settings,
        string token,
        CancellationToken cancellationToken)
    {
        var configured = settings.TwitchClientId.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return await TwitchClientIdCache.GetOrResolveAsync(
            httpClient,
            token,
            logger,
            "Search",
            "Twitch token validation failed for channel search.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryReadKickJsonWithHttpClientAsync(
        string url,
        string referrer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamlinkVlcStudio/0.1");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        request.Headers.Referrer = new Uri(referrer);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Info,
                "Search",
                $"Kick website search returned {(int)response.StatusCode} {response.ReasonPhrase}; trying curl fallback.");
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryReadKickJsonWithCurlAsync(
        string url,
        string referrer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var curlPath = ResolveCurlPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildKickCurlArguments(url, referrer))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CurlTimeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        string stdout;
        string stderr;

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            await ObserveOutputReadsAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            logger.Write(AppLogLevel.Warning, "Search", "curl.exe timed out during Kick channel search.");
            return null;
        }

        if (process.ExitCode != 0)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"curl.exe failed during Kick channel search: {stderr.Trim()}");
            return null;
        }

        return stdout;
    }

    private static IEnumerable<DiscoveredChannel> ReadTwitchChannels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var order = 0;
        foreach (var item in data.EnumerateArray())
        {
            var login = GetOptionalString(item, "broadcaster_login").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(login) ||
                !TryCreateTarget(PlatformKind.Twitch, login, out var target))
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
                TryGetBoolean(item, "is_live") == true,
                StreamSearchSourceStatus.Available,
                order++);
        }
    }

    private static IEnumerable<DiscoveredChannel> ReadKickSearchChannels(JsonElement root)
    {
        if (!root.TryGetProperty("channels", out var channels) ||
            channels.ValueKind != JsonValueKind.Array)
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
        var slug = GetOptionalString(item, "slug").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(slug) ||
            !TryCreateTarget(PlatformKind.Kick, slug, out var target))
        {
            return false;
        }

        var user = item.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.Object
            ? userElement
            : default;
        var livestream = item.TryGetProperty("livestream", out var livestreamElement) && livestreamElement.ValueKind == JsonValueKind.Object
            ? livestreamElement
            : default;

        var isLive = TryGetBoolean(item, "isLive") ??
            TryGetBoolean(item, "is_live") ??
            (livestream.ValueKind == JsonValueKind.Object ? TryGetBoolean(livestream, "is_live") : null) ??
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
            StreamSearchSourceStatus.Available,
            order,
            viewerCount is { } value ? Math.Max(0, value) : null);
        return true;
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
            StreamSearchSourceStatus.Available,
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

        if (normalizedChannel.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            normalizedDisplay.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (normalizedChannel.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            normalizedDisplay.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
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
        var parts = new List<string>();
        if (live > 0)
        {
            parts.Add(live == 1 ? "1 live" : $"{live} live");
        }

        if (offline > 0)
        {
            parts.Add(offline == 1 ? "1 offline" : $"{offline} offline");
        }

        if (unavailable > 0)
        {
            parts.Add(unavailable == 1 ? "1 unavailable" : $"{unavailable} unavailable");
        }

        var summary = parts.Count == 0
            ? $"No channels found for {query}."
            : $"{string.Join(", ", parts)} channel result{(channels.Count == 1 ? "" : "s")} found for {query}.";
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

    private static string ResolveCurlPath()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemCurl = string.IsNullOrWhiteSpace(systemRoot)
            ? ""
            : Path.Combine(systemRoot, "System32", "curl.exe");
        if (File.Exists(systemCurl))
        {
            return systemCurl;
        }

        return "curl.exe";
    }

    private static IEnumerable<string> BuildKickCurlArguments(string url, string referrer)
    {
        yield return "-s";
        yield return "-L";
        yield return "--max-time";
        yield return ((int)CurlTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        yield return "-A";
        yield return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        yield return "-H";
        yield return "Accept: application/json, text/plain, */*";
        yield return "-H";
        yield return $"Referer: {referrer}";
        yield return url;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when property.TryGetInt32(out var value) => value != 0,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value != 0,
            _ => null
        };
    }

    private static string ExtractApiMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var message = GetOptionalString(document.RootElement, "message");
            return string.IsNullOrWhiteSpace(message) ? "" : message;
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
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
        StreamSearchSourceStatus SourceStatus,
        int Order,
        int? ViewerCount = null);
}
