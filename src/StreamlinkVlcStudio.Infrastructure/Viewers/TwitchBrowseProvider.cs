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

internal sealed class TwitchBrowseProvider : BrowseProviderRequests
{
    internal TwitchBrowseProvider(IAppLogger logger, HttpClient httpClient) : base(logger, httpClient) { }
    private const int MaxTwitchPages = 100;
    private readonly TwitchRateLimitCoordinator twitchRateLimits = new();

    internal async Task<BrowseResult<BrowseCategory>> GetTwitchCategoriesAsync(
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
        var categories = BrowsePayloadMapper.ReadCategories(document.RootElement, PlatformKind.Twitch).ToArray();
        var nextCursor = ReadPaginationCursor(document.RootElement, "cursor");
        return new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            categories,
            nextCursor,
            FormatCategoryMessage(PlatformKind.Twitch, categories.Length, query));
    }

    internal async Task<BrowseResult<BrowseLiveStream>> GetTwitchStreamsAsync(
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

    private Task EnrichTwitchProfileImagesAsync(
        BrowseLiveStream[] streams,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken)
    {
        return ProfileImageLookup.EnrichTwitchAsync(
            httpClient,
            streams,
            stream => stream.Channel,
            (stream, profileImage) => stream with { ProfileImageUrl = profileImage },
            accessToken,
            clientId,
            logger,
            "Browse",
            cancellationToken);
    }

    internal async Task<BrowseResult<BrowseCategoryViewerCount>> GetTwitchCategoryViewerCountsAsync(
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

    private sealed record TwitchCategoryViewerCountsLoadResult(
        IReadOnlyDictionary<string, int> ViewerCounts,
        int PageCount,
        BrowseResult<BrowseCategoryViewerCount>? Failure);
}
