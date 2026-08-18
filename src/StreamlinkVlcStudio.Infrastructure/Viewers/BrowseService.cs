using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Diagnostics;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class BrowseService : IBrowseService
{
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(15),
        includeUserAgent: true,
        acceptJson: true);
    private const int KickCategoryDetailConcurrency = 4;
    private const int KickTopLiveStreamDiscoveryLimit = 100;
    private const int MaxTwitchPages = 100;
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly IKickTokenProvider kickTokenProvider;
    private readonly TwitchRateLimitCoordinator twitchRateLimits = new();

    public BrowseService(IAppLogger logger)
        : this(logger, SharedHttpClient, KickTokenProvider.Shared)
    {
    }

    public BrowseService(IAppLogger logger, HttpClient httpClient)
        : this(logger, httpClient, KickTokenProvider.Shared)
    {
    }

    internal BrowseService(
        IAppLogger logger,
        HttpClient httpClient,
        IKickTokenProvider kickTokenProvider)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.kickTokenProvider = kickTokenProvider;
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

        var clientId = await TwitchClientIdResolver.ResolveAsync(
            settings,
            httpClient,
            token,
            logger,
            "Browse",
            "Could not resolve Twitch Client ID from the OAuth token.",
            cancellationToken).ConfigureAwait(false);
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
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return HandleBrowseHttpFailure<BrowseCategory>(
                response,
                responseBody,
                "Twitch categories unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        var categories = BrowsePayloadMapper.ReadTwitchCategories(document.RootElement).ToArray();
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

        var clientId = await TwitchClientIdResolver.ResolveAsync(
            settings,
            httpClient,
            token,
            logger,
            "Browse",
            "Could not resolve Twitch Client ID from the OAuth token.",
            cancellationToken).ConfigureAwait(false);
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
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return HandleBrowseHttpFailure<BrowseLiveStream>(
                response,
                responseBody,
                "Twitch category streams unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(responseBody);
        var streams = BrowsePayloadMapper.ReadTwitchStreams(document.RootElement).ToArray();
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
        var accessToken = await kickTokenProvider
            .ResolveAsync(settings, logger, cancellationToken)
            .ConfigureAwait(false);
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
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
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
            BrowsePayloadMapper.ReadKickCategories(document.RootElement).ToArray(),
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
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Browse",
                    $"Kick top live category discovery failed: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(responseBody)}");
            return [];
        }

        using var document = JsonDocument.Parse(responseBody);
        return BrowsePayloadMapper.ReadKickLiveStreamCategories(document.RootElement).ToArray();
    }

    private async Task<BrowseResult<BrowseLiveStream>> GetKickStreamsAsync(
        BrowseStreamRequest request,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var accessToken = await kickTokenProvider
            .ResolveAsync(settings, logger, cancellationToken)
            .ConfigureAwait(false);
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

        var pageSize = Math.Clamp(request.PageSize <= 0 ? 50 : request.PageSize, 1, 100);
        var url = BuildUrl(
            "https://api.kick.com/public/v1/livestreams",
            [
                new("category_id", categoryId),
                new("limit", pageSize.ToString(CultureInfo.InvariantCulture)),
                new("sort", "viewer_count"),
                new("cursor", request.Cursor.Trim())
            ]);

        using var httpRequest = CreateKickRequest(url, accessToken);
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return HandleBrowseHttpFailure<BrowseLiveStream>(
                response,
                responseBody,
                "Kick category streams unavailable. Check Kick API credentials.");
        }

        using var document = JsonDocument.Parse(responseBody);
        var streams = BrowsePayloadMapper.ReadKickStreams(document.RootElement, categoryId, request.CategoryName).ToArray();
        var nextCursor = ReadPaginationCursor(document.RootElement, "next_cursor");
        return new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            streams,
            nextCursor,
            FormatStreamMessage(PlatformKind.Kick, streams.Length, request.CategoryName));
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
            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, httpRequest, cancellationToken).ConfigureAwait(false);
            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
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
                    $"Kick category '{category.Name}' viewer count lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(responseBody)}");
                return new KickCategoryDetailLoadResult(index, category, null, category.ViewerCount is null);
            }

            using var document = JsonDocument.Parse(responseBody);
            if (!BrowsePayloadMapper.TryReadKickCategoryDetail(
                    document.RootElement,
                    category,
                    out var enrichedCategory,
                    out var failureMessage))
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

        var clientId = await TwitchClientIdResolver.ResolveAsync(
            settings,
            httpClient,
            token,
            logger,
            "Browse",
            "Could not resolve Twitch Client ID from the OAuth token.",
            cancellationToken).ConfigureAwait(false);
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
            if (++pageCount > MaxTwitchPages)
            {
                const string pageLimitMessage = "Twitch stream count pagination exceeded the safety limit.";
                logger.Write(AppLogLevel.Warning, "Browse", pageLimitMessage);
                return new TwitchCategoryViewerCountsLoadResult(
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    pageCount - 1,
                    BrowseResult<BrowseCategoryViewerCount>.Unavailable(
                        $"Twitch category viewer counts unavailable. {pageLimitMessage}"));
            }

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
            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
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
            var streamsResult = BrowsePayloadMapper.ReadTwitchStreamViewerCounts(document.RootElement);
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

    private async Task<HttpResponseMessage> SendTwitchRequestAsync(
        string url,
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        return await twitchRateLimits
            .SendAsync(httpClient, url, token, clientId, logger, cancellationToken)
            .ConfigureAwait(false);
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
        var apiMessage = ApiErrorMessage.Extract(responseBody);
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

    internal static TimeSpan ClampTwitchRateLimitDelay(TimeSpan delay)
        => TwitchRateLimitCoordinator.ClampDelay(delay);

    internal static DateTimeOffset SaturatingAdd(DateTimeOffset value, TimeSpan delta)
        => TwitchRateLimitCoordinator.SaturatingAdd(value, delta);

    private sealed record TwitchCategoryViewerCountsLoadResult(
        IReadOnlyDictionary<string, int> ViewerCounts,
        int PageCount,
        BrowseResult<BrowseCategoryViewerCount>? Failure);

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
