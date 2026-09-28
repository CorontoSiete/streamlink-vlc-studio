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
    private readonly IAppLogger logger;
    private readonly TwitchBrowseProvider twitch;
    private readonly KickBrowseProvider kick;

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
        twitch = new TwitchBrowseProvider(logger, httpClient);
        kick = new KickBrowseProvider(logger, httpClient, kickTokenProvider);
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
                PlatformKind.Twitch => await twitch.GetTwitchCategoriesAsync(request, settings.Chat, cancellationToken).ConfigureAwait(false),
                PlatformKind.Kick => await kick.GetKickCategoriesAsync(request, settings.Chat, cancellationToken).ConfigureAwait(false),
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
                PlatformKind.Twitch => await twitch.GetTwitchStreamsAsync(request, settings.Chat, cancellationToken).ConfigureAwait(false),
                PlatformKind.Kick => await kick.GetKickStreamsAsync(request, settings.Chat, cancellationToken).ConfigureAwait(false),
                _ => BrowseResult<BrowseLiveStream>.Unavailable($"Browse does not support {request.Platform}.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.Write(AppLogLevel.Warning, "Browse", $"{request.Platform} streams could not be loaded.", ex);
            return BrowseResult<BrowseLiveStream>.Unavailable($"{request.Platform} streams unavailable. {ex.Message}");
        }
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
                PlatformKind.Twitch => await twitch.GetTwitchCategoryViewerCountsAsync(request, settings.Chat, cancellationToken).ConfigureAwait(false),
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

}
