using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Diagnostics;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class BrowseService : IBrowseService
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly object TwitchRateLimitGate = new();
    private static readonly TimeSpan TwitchRateLimitRetryFallback = TimeSpan.FromSeconds(5);
    private const int KickCategoryDetailConcurrency = 4;
    private const int KickTopLiveStreamDiscoveryLimit = 100;
    private static DateTimeOffset twitchRateLimitPauseUntilUtc = DateTimeOffset.MinValue;
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;

    public BrowseService(IAppLogger logger)
        : this(logger, SharedHttpClient)
    {
    }

    public BrowseService(IAppLogger logger, HttpClient httpClient)
    {
        this.logger = logger;
        this.httpClient = httpClient;
    }

    public async Task<BrowseResult<BrowseCategory>> GetCategoriesAsync(
        BrowseCategoryRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Platform switch
            {
                PlatformKind.Twitch => await GetTwitchCategoriesAsync(request, settings.Chat, cancellationToken).ConfigureAwait(false),
                PlatformKind.Kick => await GetKickCategoriesAsync(request, settings.Chat, cancellationToken).ConfigureAwait(false),
                _ => BrowseResult<BrowseCategory>.Unavailable($"Browse does not support {request.Platform}.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.Write(AppLogLevel.Warning, "Browse", $"{request.Platform} categories could not be loaded.", ex);
            return BrowseResult<BrowseCategory>.Unavailable($"{request.Platform} categories unavailable. {ex.Message}");
        }
    }

    public async Task<BrowseResult<BrowseLiveStream>> GetStreamsAsync(
        BrowseStreamRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Platform switch
            {
                PlatformKind.Twitch => await GetTwitchStreamsAsync(request, settings.Chat, cancellationToken).ConfigureAwait(false),
                PlatformKind.Kick => await GetKickStreamsAsync(request, settings.Chat, cancellationToken).ConfigureAwait(false),
                _ => BrowseResult<BrowseLiveStream>.Unavailable($"Browse does not support {request.Platform}.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.Write(AppLogLevel.Warning, "Browse", $"{request.Platform} streams could not be loaded.", ex);
            return BrowseResult<BrowseLiveStream>.Unavailable($"{request.Platform} streams unavailable. {ex.Message}");
        }
    }

    private async Task<BrowseResult<BrowseCategory>> GetTwitchCategoriesAsync(
        BrowseCategoryRequest request,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return BrowseResult<BrowseCategory>.NotConfigured(
                "Twitch browse requires a Twitch OAuth token.");
        }

        var clientId = await ResolveTwitchClientIdAsync(settings, token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return BrowseResult<BrowseCategory>.NotConfigured(
                "Twitch browse requires a Twitch Client ID that matches the OAuth token.");
        }

        var query = request.Query.Trim();
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 100);
        var url = string.IsNullOrWhiteSpace(query)
            ? BuildUrl(
                "https://api.twitch.tv/helix/games/top",
                [
                    new("first", pageSize.ToString(CultureInfo.InvariantCulture)),
                    new("after", request.Cursor.Trim())
                ])
            : BuildUrl(
                "https://api.twitch.tv/helix/search/categories",
                [
                    new("query", query),
                    new("first", pageSize.ToString(CultureInfo.InvariantCulture)),
                    new("after", request.Cursor.Trim())
                ]);

        using var response = await SendTwitchRequestAsync(url, token, clientId, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return HandleBrowseHttpFailure<BrowseCategory>(
                response,
                responseBody,
                "Twitch categories unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        var categories = ReadTwitchCategories(document.RootElement).ToArray();
        var nextCursor = ReadPaginationCursor(document.RootElement, "cursor");
        return new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            categories,
            nextCursor,
            FormatCategoryMessage(PlatformKind.Twitch, categories.Length, query));
    }

    public async Task<BrowseResult<BrowseCategoryViewerCount>> GetCategoryViewerCountsAsync(
        BrowseCategoryViewerCountRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Platform switch
            {
                PlatformKind.Twitch => await GetTwitchCategoryViewerCountsAsync(request, settings.Chat, cancellationToken).ConfigureAwait(false),
                PlatformKind.Kick => BrowseResult<BrowseCategoryViewerCount>.Unavailable("Kick category viewer counts are loaded with Kick categories."),
                _ => BrowseResult<BrowseCategoryViewerCount>.Unavailable($"Browse does not support {request.Platform}.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.Write(AppLogLevel.Warning, "Browse", $"{request.Platform} category viewer counts could not be loaded.", ex);
            return BrowseResult<BrowseCategoryViewerCount>.Unavailable($"{request.Platform} category viewer counts unavailable. {ex.Message}");
        }
    }

    private async Task<BrowseResult<BrowseLiveStream>> GetTwitchStreamsAsync(
        BrowseStreamRequest request,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return BrowseResult<BrowseLiveStream>.NotConfigured(
                "Twitch browse requires a Twitch OAuth token.");
        }

        var clientId = await ResolveTwitchClientIdAsync(settings, token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return BrowseResult<BrowseLiveStream>.NotConfigured(
                "Twitch browse requires a Twitch Client ID that matches the OAuth token.");
        }

        var categoryId = request.CategoryId.Trim();
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return BrowseResult<BrowseLiveStream>.Unavailable("Select a Twitch category first.");
        }

        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 100);
        var url = BuildUrl(
            "https://api.twitch.tv/helix/streams",
            [
                new("game_id", categoryId),
                new("first", pageSize.ToString(CultureInfo.InvariantCulture)),
                new("after", request.Cursor.Trim())
            ]);

        using var response = await SendTwitchRequestAsync(url, token, clientId, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return HandleBrowseHttpFailure<BrowseLiveStream>(
                response,
                responseBody,
                "Twitch category streams unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        var streams = ReadTwitchStreams(document.RootElement).ToArray();
        await EnrichTwitchProfileImagesAsync(
            streams,
            token,
            clientId,
            cancellationToken).ConfigureAwait(false);
        var nextCursor = ReadPaginationCursor(document.RootElement, "cursor");
        return new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            streams,
            nextCursor,
            FormatStreamMessage(PlatformKind.Twitch, streams.Length, request.CategoryName));
    }

    private async Task<BrowseResult<BrowseCategory>> GetKickCategoriesAsync(
        BrowseCategoryRequest request,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var accessToken = await ResolveKickAccessTokenAsync(settings, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return BrowseResult<BrowseCategory>.NotConfigured(
                "Kick browse requires Kick Client ID and Client Secret or a Kick user token.");
        }

        var query = request.Query.Trim();
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 1000);
        return await GetKickCategoryPageAsync(
            query,
            pageSize,
            request.Cursor.Trim(),
            accessToken,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<BrowseResult<BrowseCategory>> GetKickCategoryPageAsync(
        string query,
        int pageSize,
        string cursor,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var categoryPageResult = await LoadKickCategoryListPageAsync(
            query,
            pageSize,
            cursor,
            accessToken,
            cancellationToken).ConfigureAwait(false);
        if (categoryPageResult.Failure is { } categoryPageFailure)
        {
            return categoryPageFailure;
        }

        var categoriesToEnrich = categoryPageResult.Categories;
        if (ShouldDiscoverKickTopLiveCategories(query, cursor))
        {
            var topLiveCategories = await LoadKickTopLiveCategoriesAsync(accessToken, cancellationToken).ConfigureAwait(false);
            categoriesToEnrich = MergeKickCategoryCandidates(topLiveCategories, categoryPageResult.Categories);
        }

        var detailResult = await LoadKickCategoryDetailsAsync(categoriesToEnrich, accessToken, cancellationToken).ConfigureAwait(false);
        if (detailResult.Failure is { } detailFailure)
        {
            return detailFailure;
        }

        var categories = SortKickCategories(detailResult.Categories);
        return new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            categories,
            categoryPageResult.NextCursor,
            FormatKickCategoryMessage(categories.Length, query, categories.Count(category => category.ViewerCount is null)));
    }

    private async Task<KickCategoryListPageLoadResult> LoadKickCategoryListPageAsync(
        string query,
        int pageSize,
        string cursor,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var queryParameters = new List<KeyValuePair<string, string>>
        {
            new("limit", pageSize.ToString(CultureInfo.InvariantCulture)),
            new("cursor", cursor)
        };
        if (!string.IsNullOrWhiteSpace(query))
        {
            queryParameters.Add(new("name", query));
        }

        var url = BuildUrl(
            "https://api.kick.com/public/v2/categories",
            queryParameters);

        using var httpRequest = CreateKickRequest(url, accessToken);
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new KickCategoryListPageLoadResult(
                [],
                "",
                HandleBrowseHttpFailure<BrowseCategory>(
                    response,
                    responseBody,
                    "Kick categories unavailable. Check Kick API credentials."));
        }

        using var document = JsonDocument.Parse(responseBody);
        return new KickCategoryListPageLoadResult(
            ReadKickCategories(document.RootElement).ToArray(),
            ReadPaginationCursor(document.RootElement, "next_cursor"),
            null);
    }

    private async Task<IReadOnlyList<BrowseCategory>> LoadKickTopLiveCategoriesAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(
            "https://api.kick.com/public/v1/livestreams",
            [
                new("limit", KickTopLiveStreamDiscoveryLimit.ToString(CultureInfo.InvariantCulture)),
                new("sort", "viewer_count")
            ]);

        using var httpRequest = CreateKickRequest(url, accessToken);
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Browse",
                $"Kick top live category discovery failed: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
            return [];
        }

        using var document = JsonDocument.Parse(responseBody);
        return ReadKickLiveStreamCategories(document.RootElement).ToArray();
    }

    private async Task<BrowseResult<BrowseLiveStream>> GetKickStreamsAsync(
        BrowseStreamRequest request,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var accessToken = await ResolveKickAccessTokenAsync(settings, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return BrowseResult<BrowseLiveStream>.NotConfigured(
                "Kick browse requires Kick Client ID and Client Secret or a Kick user token.");
        }

        var categoryId = request.CategoryId.Trim();
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return BrowseResult<BrowseLiveStream>.Unavailable("Select a Kick category first.");
        }

        var url = BuildUrl(
            "https://api.kick.com/public/v1/livestreams",
            [
                new("category_id", categoryId),
                new("limit", "100"),
                new("sort", "viewer_count")
            ]);

        using var httpRequest = CreateKickRequest(url, accessToken);
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return HandleBrowseHttpFailure<BrowseLiveStream>(
                response,
                responseBody,
                "Kick category streams unavailable. Check Kick API credentials.");
        }

        using var document = JsonDocument.Parse(responseBody);
        var streams = ReadKickStreams(document.RootElement, categoryId, request.CategoryName).ToArray();
        return new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            streams,
            "",
            FormatStreamMessage(PlatformKind.Kick, streams.Length, request.CategoryName));
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
            "Browse",
            "Could not resolve Twitch Client ID from the OAuth token.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnrichTwitchProfileImagesAsync(
        BrowseLiveStream[] streams,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken)
    {
        if (streams.Length == 0)
        {
            return;
        }

        try
        {
            var profileImages = await TwitchProfileImageLookup.GetAsync(
                httpClient,
                accessToken,
                clientId,
                streams.Select(stream => stream.Channel),
                cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < streams.Length; index++)
            {
                if (profileImages.TryGetValue(streams[index].Channel, out var profileImage))
                {
                    streams[index] = streams[index] with
                    {
                        ProfileImageUrl = profileImage
                    };
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Browse", "Twitch profile images could not be loaded.", ex);
        }
    }

    private async Task<string?> ResolveKickAccessTokenAsync(ChatSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.KickClientId) &&
            !string.IsNullOrWhiteSpace(settings.KickClientSecret))
        {
            var appToken = await KickOAuthService.TryGetAppAccessTokenAsync(settings, logger, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(appToken))
            {
                return appToken;
            }
        }

        return await KickOAuthService.GetUsableAccessTokenAsync(settings, logger, cancellationToken).ConfigureAwait(false);
    }

    private async Task<KickCategoryDetailsLoadResult> LoadKickCategoryDetailsAsync(
        IReadOnlyList<BrowseCategory> categories,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (categories.Count == 0)
        {
            return new KickCategoryDetailsLoadResult([], 0, null);
        }

        using var throttle = new SemaphoreSlim(KickCategoryDetailConcurrency);
        var loadTasks = categories
            .Select((category, index) => category.ViewerCount is not null
                ? Task.FromResult(new KickCategoryDetailLoadResult(index, category, null, false))
                : LoadKickCategoryDetailWithThrottleAsync(
                    category,
                    index,
                    accessToken,
                    throttle,
                    cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(loadTasks).ConfigureAwait(false);
        var failure = results
            .Select(result => result.Failure)
            .FirstOrDefault(result => result is not null);
        if (failure is not null)
        {
            return new KickCategoryDetailsLoadResult([], 0, failure);
        }

        var enrichedCategories = results
            .Where(result => result.Category is not null)
            .OrderBy(result => result.Index)
            .Select(result => result.Category!)
            .ToArray();
        return new KickCategoryDetailsLoadResult(
            enrichedCategories,
            results.Count(result => result.ViewerCountUnavailable),
            null);
    }

    private async Task<KickCategoryDetailLoadResult> LoadKickCategoryDetailWithThrottleAsync(
        BrowseCategory category,
        int index,
        string accessToken,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadKickCategoryDetailAsync(category, index, accessToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            throttle.Release();
        }
    }

    private async Task<KickCategoryDetailLoadResult> LoadKickCategoryDetailAsync(
        BrowseCategory category,
        int index,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.kick.com/public/v1/categories/{Uri.EscapeDataString(category.Id)}";
            using var httpRequest = CreateKickRequest(url, accessToken);
            using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return new KickCategoryDetailLoadResult(
                        index,
                        null,
                        HandleBrowseHttpFailure<BrowseCategory>(
                            response,
                            responseBody,
                            "Kick category viewer counts unavailable. Check Kick API credentials."),
                        false);
                }

                logger.Write(
                    AppLogLevel.Warning,
                    "Browse",
                    $"Kick category '{category.Name}' viewer count lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}. {ExtractApiMessage(responseBody)}");
                return new KickCategoryDetailLoadResult(index, category, null, category.ViewerCount is null);
            }

            using var document = JsonDocument.Parse(responseBody);
            if (!TryReadKickCategoryDetail(document.RootElement, category, out var enrichedCategory, out var failureMessage))
            {
                logger.Write(AppLogLevel.Warning, "Browse", failureMessage);
                return new KickCategoryDetailLoadResult(index, category, null, category.ViewerCount is null);
            }

            return new KickCategoryDetailLoadResult(index, enrichedCategory, null, enrichedCategory.ViewerCount is null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Browse",
                $"Kick category '{category.Name}' viewer count lookup failed.",
                ex);
            return new KickCategoryDetailLoadResult(index, category, null, category.ViewerCount is null);
        }
    }

    private async Task<BrowseResult<BrowseCategoryViewerCount>> GetTwitchCategoryViewerCountsAsync(
        BrowseCategoryViewerCountRequest request,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return BrowseResult<BrowseCategoryViewerCount>.NotConfigured(
                "Twitch browse requires a Twitch OAuth token.");
        }

        var clientId = await ResolveTwitchClientIdAsync(settings, token, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return BrowseResult<BrowseCategoryViewerCount>.NotConfigured(
                "Twitch browse requires a Twitch Client ID that matches the OAuth token.");
        }

        var categoryIds = request.CategoryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (categoryIds.Length == 0)
        {
            return new BrowseResult<BrowseCategoryViewerCount>(
                BrowseResultStatus.Available,
                [],
                "",
                "No Twitch categories need viewer counts.");
        }

        var stopwatch = Stopwatch.StartNew();
        var allViewerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalPageCount = 0;
        foreach (var categoryIdBatch in categoryIds.Chunk(100))
        {
            var viewerCountsResult = await LoadTwitchCategoryViewerCountsAsync(
                categoryIdBatch,
                token,
                clientId,
                cancellationToken).ConfigureAwait(false);
            totalPageCount += viewerCountsResult.PageCount;
            if (viewerCountsResult.Failure is { } failure)
            {
                return failure;
            }

            foreach (var pair in viewerCountsResult.ViewerCounts)
            {
                allViewerCounts[pair.Key] = pair.Value;
            }
        }

        var counts = categoryIds
            .Select(id => new BrowseCategoryViewerCount(
                id,
                allViewerCounts.TryGetValue(id, out var count) ? count : 0))
            .ToArray();
        stopwatch.Stop();
        logger.Write(
            AppLogLevel.Info,
            "Browse",
            $"Loaded exact Twitch viewer counts for {counts.Length} {(counts.Length == 1 ? "category" : "categories")} using {totalPageCount} Twitch stream {(totalPageCount == 1 ? "page" : "pages")} in {stopwatch.Elapsed.TotalSeconds:0.0}s.");
        return new BrowseResult<BrowseCategoryViewerCount>(
            BrowseResultStatus.Available,
            counts,
            "",
            $"Loaded exact Twitch viewer counts for {counts.Length} {(counts.Length == 1 ? "category" : "categories")}.");
    }

    private async Task<TwitchCategoryViewerCountsLoadResult> LoadTwitchCategoryViewerCountsAsync(
        IReadOnlyList<string> categoryIds,
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        var requestedCategoryIds = categoryIds.ToHashSet(StringComparer.Ordinal);
        var streamIds = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var viewerCounts = categoryIds.ToDictionary(
            id => id,
            _ => 0L,
            StringComparer.Ordinal);
        var cursor = "";
        var pageCount = 0;

        while (true)
        {
            var query = categoryIds
                .Select(id => new KeyValuePair<string, string>("game_id", id))
                .Concat(
                [
                    new("first", "100"),
                    new("after", cursor)
                ]);
            var url = BuildUrl(
                "https://api.twitch.tv/helix/streams",
                query);

            using var response = await SendTwitchRequestAsync(url, token, clientId, cancellationToken).ConfigureAwait(false);
            pageCount++;
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new TwitchCategoryViewerCountsLoadResult(
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    pageCount,
                    HandleBrowseHttpFailure<BrowseCategoryViewerCount>(
                        response,
                        responseBody,
                        "Twitch category viewer counts unavailable. Check the Twitch Client ID and OAuth token."));
            }

            using var document = JsonDocument.Parse(responseBody);
            var streamsResult = ReadTwitchStreamViewerCounts(document.RootElement);
            if (streamsResult.FailureMessage is { } failureMessage)
            {
                logger.Write(AppLogLevel.Warning, "Browse", failureMessage);
                return new TwitchCategoryViewerCountsLoadResult(
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    pageCount,
                    BrowseResult<BrowseCategoryViewerCount>.Unavailable($"Twitch category viewer counts unavailable. {failureMessage}"));
            }

            foreach (var stream in streamsResult.Streams)
            {
                if (!requestedCategoryIds.Contains(stream.GameId))
                {
                    const string unexpectedGameIdMessage = "Twitch stream count response included an unexpected game_id.";
                    logger.Write(AppLogLevel.Warning, "Browse", unexpectedGameIdMessage);
                    return new TwitchCategoryViewerCountsLoadResult(
                        new Dictionary<string, int>(StringComparer.Ordinal),
                        pageCount,
                        BrowseResult<BrowseCategoryViewerCount>.Unavailable($"Twitch category viewer counts unavailable. {unexpectedGameIdMessage}"));
                }

                if (streamIds.Add(stream.Id))
                {
                    viewerCounts[stream.GameId] += stream.ViewerCount;
                }
            }

            var nextCursor = ReadPaginationCursor(document.RootElement, "cursor");
            if (string.IsNullOrWhiteSpace(nextCursor))
            {
                break;
            }

            if (!seenCursors.Add(nextCursor))
            {
                const string repeatedCursorMessage = "Twitch stream count pagination repeated a cursor.";
                logger.Write(AppLogLevel.Warning, "Browse", repeatedCursorMessage);
                return new TwitchCategoryViewerCountsLoadResult(
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    pageCount,
                    BrowseResult<BrowseCategoryViewerCount>.Unavailable($"Twitch category viewer counts unavailable. {repeatedCursorMessage}"));
            }

            cursor = nextCursor;
        }

        var clampedViewerCounts = viewerCounts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value > int.MaxValue ? int.MaxValue : (int)pair.Value,
            StringComparer.Ordinal);
        return new TwitchCategoryViewerCountsLoadResult(
            clampedViewerCounts,
            pageCount,
            null);
    }

    private static HttpRequestMessage CreateTwitchRequest(string url, string token, string clientId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);
        return request;
    }

    private async Task<HttpResponseMessage> SendTwitchRequestAsync(
        string url,
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await WaitForTwitchRateLimitPauseAsync(cancellationToken).ConfigureAwait(false);

            using var request = CreateTwitchRequest(url, token, clientId);
            var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ObserveTwitchRateLimitHeaders(response);

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 2)
            {
                return response;
            }

            var retryDelay = GetTwitchRateLimitRetryDelay(response) ?? TwitchRateLimitRetryFallback;
            SetTwitchRateLimitPause(DateTimeOffset.UtcNow.Add(retryDelay));
            logger.Write(
                AppLogLevel.Warning,
                "Browse",
                $"Twitch browse request was rate limited; retrying in {retryDelay.TotalSeconds:0.#}s.");
            response.Dispose();
            if (retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task WaitForTwitchRateLimitPauseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (TwitchRateLimitGate)
            {
                delay = twitchRateLimitPauseUntilUtc - DateTimeOffset.UtcNow;
            }

            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ObserveTwitchRateLimitHeaders(HttpResponseMessage response)
    {
        if (!TryGetHeaderInt32(response, "Ratelimit-Remaining", out var remaining) ||
            remaining > 1 ||
            !TryGetTwitchRateLimitResetUtc(response, out var resetUtc))
        {
            return;
        }

        SetTwitchRateLimitPause(resetUtc.AddMilliseconds(500));
    }

    private static TimeSpan? GetTwitchRateLimitRetryDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (response.Headers.RetryAfter?.Date is { } retryAt)
        {
            var delay = retryAt.ToUniversalTime() - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return TryGetTwitchRateLimitResetUtc(response, out var resetUtc)
            ? Max(TimeSpan.Zero, resetUtc.AddMilliseconds(500) - DateTimeOffset.UtcNow)
            : null;
    }

    private static bool TryGetTwitchRateLimitResetUtc(HttpResponseMessage response, out DateTimeOffset resetUtc)
    {
        resetUtc = default;
        if (!TryGetHeaderInt64(response, "Ratelimit-Reset", out var resetUnixSeconds))
        {
            return false;
        }

        try
        {
            resetUtc = DateTimeOffset.FromUnixTimeSeconds(resetUnixSeconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryGetHeaderInt32(HttpResponseMessage response, string name, out int value)
    {
        value = 0;
        if (!TryGetHeaderInt64(response, name, out var longValue) ||
            longValue < int.MinValue ||
            longValue > int.MaxValue)
        {
            return false;
        }

        value = (int)longValue;
        return true;
    }

    private static bool TryGetHeaderInt64(HttpResponseMessage response, string name, out long value)
    {
        value = 0;
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return false;
        }

        var rawValue = values.FirstOrDefault();
        return long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static void SetTwitchRateLimitPause(DateTimeOffset pauseUntilUtc)
    {
        if (pauseUntilUtc <= DateTimeOffset.UtcNow)
        {
            return;
        }

        lock (TwitchRateLimitGate)
        {
            if (pauseUntilUtc > twitchRateLimitPauseUntilUtc)
            {
                twitchRateLimitPauseUntilUtc = pauseUntilUtc;
            }
        }
    }

    private static HttpRequestMessage CreateKickRequest(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            KickOAuthService.NormalizeBearerToken(accessToken));
        return request;
    }

    private BrowseResult<T> HandleBrowseHttpFailure<T>(
        HttpResponseMessage response,
        string responseBody,
        string fallbackMessage)
    {
        var apiMessage = ExtractApiMessage(responseBody);
        logger.Write(
            AppLogLevel.Warning,
            "Browse",
            $"Browse request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {apiMessage}");

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return BrowseResult<T>.Unauthorized(fallbackMessage);
        }

        return BrowseResult<T>.Unavailable(fallbackMessage);
    }

    private static IEnumerable<BrowseCategory> ReadTwitchCategories(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
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
                NormalizeTwitchBoxArtUrl(GetOptionalString(item, "box_art_url")),
                []);
        }
    }

    private static IEnumerable<BrowseLiveStream> ReadTwitchStreams(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
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
                NormalizeTwitchThumbnailUrl(GetOptionalString(item, "thumbnail_url")),
                TryGetDateTimeOffset(item, "started_at"),
                TryGetBool(item, "is_mature"),
                GetOptionalString(item, "language"),
                target.Url);
        }
    }

    private static TwitchStreamViewerCountReadResult ReadTwitchStreamViewerCounts(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
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

    private static IEnumerable<BrowseCategory> ReadKickCategories(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
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

    private static bool TryReadKickCategoryDetail(
        JsonElement root,
        BrowseCategory fallbackCategory,
        out BrowseCategory category,
        out string failureMessage)
    {
        category = fallbackCategory;
        failureMessage = "";
        if (!root.TryGetProperty("data", out var data) ||
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

    private static IEnumerable<BrowseLiveStream> ReadKickStreams(
        JsonElement root,
        string requestedCategoryId,
        string requestedCategoryName)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
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

    private static IEnumerable<BrowseCategory> ReadKickLiveStreamCategories(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("category", out var categoryElement) ||
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

    private static string BuildUrl(string baseUrl, IEnumerable<KeyValuePair<string, string>> query)
    {
        var filtered = query
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value.Trim())}")
            .ToArray();
        return filtered.Length == 0
            ? baseUrl
            : $"{baseUrl}?{string.Join('&', filtered)}";
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

    private static string NormalizeTwitchBoxArtUrl(string url)
    {
        return NormalizeImageUrl(url)
            .Replace("{width}", "285", StringComparison.OrdinalIgnoreCase)
            .Replace("{height}", "380", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTwitchThumbnailUrl(string url)
    {
        return NormalizeImageUrl(url)
            .Replace("{width}", "440", StringComparison.OrdinalIgnoreCase)
            .Replace("{height}", "248", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeImageUrl(string url)
    {
        var trimmed = (url ?? "").Trim();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + trimmed
            : trimmed;
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

    private static string FormatCategoryMessage(PlatformKind platform, int count, string query)
    {
        var platformName = platform.ToString();
        if (count == 0)
        {
            return string.IsNullOrWhiteSpace(query)
                ? $"No {platformName} categories were returned."
                : $"No {platformName} categories matched '{query}'.";
        }

        var countText = count == 1 ? "1 category" : $"{count} categories";
        return string.IsNullOrWhiteSpace(query)
            ? $"Loaded {countText} from {platformName}."
            : $"Loaded {countText} matching '{query}' from {platformName}.";
    }

    private static string FormatKickCategoryMessage(int count, string query, int viewerCountUnavailableCount)
    {
        if (viewerCountUnavailableCount <= 0)
        {
            return FormatCategoryMessage(PlatformKind.Kick, count, query);
        }

        var unavailableText = viewerCountUnavailableCount == 1
            ? "1 category"
            : $"{viewerCountUnavailableCount} categories";
        return $"{FormatCategoryMessage(PlatformKind.Kick, count, query)} Viewer counts unavailable for {unavailableText}.";
    }

    private static BrowseCategory[] SortKickCategories(IEnumerable<BrowseCategory> categories)
    {
        return categories
            .OrderBy(category => category.ViewerCount is null ? 1 : 0)
            .ThenByDescending(category => category.ViewerCount ?? 0)
            .ThenBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ShouldDiscoverKickTopLiveCategories(string query, string cursor)
    {
        return string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(cursor);
    }

    private static BrowseCategory[] MergeKickCategoryCandidates(
        IReadOnlyList<BrowseCategory> priorityCategories,
        IReadOnlyList<BrowseCategory> categoryPageCategories)
    {
        if (priorityCategories.Count == 0)
        {
            return categoryPageCategories.ToArray();
        }

        var categories = new List<BrowseCategory>();
        var categoryIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        AddOrMerge(priorityCategories);
        AddOrMerge(categoryPageCategories);
        return categories.ToArray();

        void AddOrMerge(IReadOnlyList<BrowseCategory> source)
        {
            foreach (var category in source)
            {
                if (!categoryIndexes.TryGetValue(category.Id, out var index))
                {
                    categoryIndexes[category.Id] = categories.Count;
                    categories.Add(category);
                    continue;
                }

                categories[index] = MergeKickCategoryFallback(categories[index], category);
            }
        }
    }

    private static BrowseCategory MergeKickCategoryFallback(BrowseCategory current, BrowseCategory candidate)
    {
        return current with
        {
            Name = FirstNonEmpty(current.Name, candidate.Name),
            ThumbnailUrl = FirstNonEmpty(current.ThumbnailUrl, candidate.ThumbnailUrl),
            Tags = current.Tags.Count >= candidate.Tags.Count ? current.Tags : candidate.Tags,
            ViewerCount = current.ViewerCount ?? candidate.ViewerCount
        };
    }

    private static string FormatStreamMessage(PlatformKind platform, int count, string categoryName)
    {
        var platformName = platform.ToString();
        var categoryText = string.IsNullOrWhiteSpace(categoryName)
            ? "this category"
            : categoryName.Trim();
        return count switch
        {
            0 => $"No live {platformName} streams found in {categoryText}.",
            1 => $"Loaded 1 live {platformName} stream in {categoryText}.",
            _ => $"Loaded {count} live {platformName} streams in {categoryText}."
        };
    }

    private static string ExtractApiMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var message = GetOptionalString(document.RootElement, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }

            var errorDescription = GetOptionalString(document.RootElement, "error_description");
            if (!string.IsNullOrWhiteSpace(errorDescription))
            {
                return errorDescription;
            }

            var error = GetOptionalString(document.RootElement, "error");
            if (!string.IsNullOrWhiteSpace(error))
            {
                return error;
            }
        }
        catch (JsonException)
        {
        }

        return responseBody.Length <= 240 ? responseBody : responseBody[..240];
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamlinkVlcStudio/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
    {
        return left >= right ? left : right;
    }

    private sealed record TwitchCategoryViewerCountsLoadResult(
        IReadOnlyDictionary<string, int> ViewerCounts,
        int PageCount,
        BrowseResult<BrowseCategoryViewerCount>? Failure);

    private sealed record TwitchStreamViewerCountReadResult(
        IReadOnlyList<TwitchStreamViewerCount> Streams,
        string? FailureMessage);

    private sealed record TwitchStreamViewerCount(
        string Id,
        string GameId,
        int ViewerCount);

    private sealed record KickCategoryListPageLoadResult(
        IReadOnlyList<BrowseCategory> Categories,
        string NextCursor,
        BrowseResult<BrowseCategory>? Failure);

    private sealed record KickCategoryDetailsLoadResult(
        IReadOnlyList<BrowseCategory> Categories,
        int ViewerCountUnavailableCount,
        BrowseResult<BrowseCategory>? Failure);

    private sealed record KickCategoryDetailLoadResult(
        int Index,
        BrowseCategory? Category,
        BrowseResult<BrowseCategory>? Failure,
        bool ViewerCountUnavailable);

}
