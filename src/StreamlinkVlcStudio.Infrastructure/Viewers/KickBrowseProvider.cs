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

internal sealed class KickBrowseProvider : BrowseProviderRequests
{
    internal KickBrowseProvider(IAppLogger logger, HttpClient httpClient, IKickTokenProvider kickTokenProvider) : base(logger, httpClient) { this.kickTokenProvider = kickTokenProvider; }
    private const int KickCategoryDetailConcurrency = 4;
    private const int KickTopLiveStreamDiscoveryLimit = 100;
    private readonly IKickTokenProvider kickTokenProvider;

    internal async Task<BrowseResult<BrowseCategory>> GetKickCategoriesAsync(
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
            BrowsePayloadMapper.ReadCategories(document.RootElement, PlatformKind.Kick).ToArray(),
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

    internal async Task<BrowseResult<BrowseLiveStream>> GetKickStreamsAsync(
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

    private static HttpRequestMessage CreateKickRequest(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            KickOAuthService.NormalizeBearerToken(accessToken));
        return request;
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
