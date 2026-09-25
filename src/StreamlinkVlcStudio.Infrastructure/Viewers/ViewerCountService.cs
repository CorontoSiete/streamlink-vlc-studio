using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class ViewerCountService : IViewerCountService
{
    private readonly IAppLogger logger;
    private readonly LiveChannelSnapshotProvider snapshotProvider;
    private readonly IKickTokenProvider kickTokenProvider;

    public ViewerCountService(IAppLogger logger)
        : this(logger, LiveChannelSnapshotProvider.Shared, KickTokenProvider.Shared)
    {
    }

    public ViewerCountService(IAppLogger logger, HttpClient httpClient)
        : this(logger, new LiveChannelSnapshotProvider(httpClient), KickTokenProvider.Shared)
    {
    }

    internal ViewerCountService(
        IAppLogger logger,
        LiveChannelSnapshotProvider snapshotProvider,
        IKickTokenProvider kickTokenProvider)
    {
        this.logger = logger;
        this.snapshotProvider = snapshotProvider;
        this.kickTokenProvider = kickTokenProvider;
    }

    public Task<ViewerCountResult> GetViewerCountAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        return target.Platform switch
        {
            PlatformKind.Twitch => GetTwitchViewerCountAsync(target, settings.Chat, cancellationToken),
            PlatformKind.Kick => GetKickViewerCountAsync(target, settings.Chat, cancellationToken),
            _ => Task.FromResult(new ViewerCountResult(ViewerCountState.Unavailable, null, $"Viewer counts are not supported for {target.Platform}."))
        };
    }

    private async Task<ViewerCountResult> GetTwitchViewerCountAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new ViewerCountResult(
                ViewerCountState.NotConfigured,
                null,
                "Twitch viewer counts require a Twitch OAuth token.");
        }

        var clientId = await TwitchClientIdResolver.ResolveAsync(
            settings,
            snapshotProvider.HttpClientForCredentialValidation,
            token,
            logger,
            "Viewers",
            "Could not resolve Twitch Client ID from the OAuth token.",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new ViewerCountResult(
                ViewerCountState.NotConfigured,
                null,
                "Twitch viewer counts require a Twitch Client ID that matches the OAuth token.");
        }

        var response = await snapshotProvider
            .GetTwitchAsync(target.Channel, token, clientId, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Viewers",
                $"Twitch viewer count request failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(response.Body)}");
            return new ViewerCountResult(
                ViewerCountState.Unavailable,
                null,
                "Twitch viewer count unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(response.Body);
        return ReadViewerCount(target, document.RootElement);
    }

    private async Task<ViewerCountResult> GetKickViewerCountAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var accessToken = await kickTokenProvider
            .ResolveAsync(settings, logger, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new ViewerCountResult(
                ViewerCountState.NotConfigured,
                null,
                "Kick viewer counts require a Kick user token or Kick Client ID and Client Secret.");
        }

        var response = await snapshotProvider
            .GetKickAsync(target.Channel, accessToken, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Viewers",
                $"Kick viewer count request failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(response.Body)}");
            return new ViewerCountResult(
                ViewerCountState.Unavailable,
                null,
                "Kick viewer count unavailable. Check Kick API credentials.");
        }

        using var document = JsonDocument.Parse(response.Body);
        return ReadViewerCount(target, document.RootElement);
    }

    private static ViewerCountResult ReadViewerCount(StreamTarget target, JsonElement root)
    {
        var payload = LiveChannelPayloadReader.Read(target.Platform, target.Channel, root);
        if (payload.State == LiveChannelState.Offline)
        {
            return new(ViewerCountState.Offline, null, $"{target.Platform} stream is offline.");
        }

        var viewerCount = TryGetInt32(payload.Stream, "viewer_count");
        if (payload.State != LiveChannelState.Available || viewerCount is null or < 0)
        {
            return new(ViewerCountState.Unavailable, null,
                $"{target.Platform} viewer count response did not include valid stream data.");
        }

        var twitch = target.Platform == PlatformKind.Twitch;
        return new ViewerCountResult(
            ViewerCountState.Available,
            viewerCount,
            $"{target.Platform} viewer count updated.",
            twitch ? GetOptionalString(payload.Channel, "game_name")
                : TryReadNestedString(payload.Channel, "category", "name"),
            twitch ? GetOptionalString(payload.Stream, "title")
                : FirstNonEmpty(
                    GetOptionalString(payload.Channel, "stream_title"),
                    GetOptionalString(payload.Stream, "stream_title"),
                    GetOptionalString(payload.Stream, "title")));
    }
}
