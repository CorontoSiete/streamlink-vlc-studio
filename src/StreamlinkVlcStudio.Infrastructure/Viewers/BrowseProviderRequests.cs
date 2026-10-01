using System.Net;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

internal abstract class BrowseProviderRequests(IAppLogger logger, HttpClient httpClient)
{
    protected readonly IAppLogger logger = logger;
    protected readonly HttpClient httpClient = httpClient;

    protected BrowseResult<T> HandleBrowseHttpFailure<T>(
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

    protected static string BuildUrl(string baseUrl, IEnumerable<KeyValuePair<string, string>> query)
    {
        var filtered = query
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value.Trim())}")
            .ToArray();
        return filtered.Length == 0
            ? baseUrl
            : $"{baseUrl}?{string.Join('&', filtered)}";
    }

    protected static string FormatCategoryMessage(PlatformKind platform, int count, string query)
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

    protected static string FormatStreamMessage(PlatformKind platform, int count, string categoryName)
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
}
