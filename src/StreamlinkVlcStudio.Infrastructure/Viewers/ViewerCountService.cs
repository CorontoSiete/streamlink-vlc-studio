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
            GetSnapshotHttpClient(),
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
        return ReadTwitchViewerCount(target, document.RootElement);
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
        return ReadKickViewerCount(target, document.RootElement);
    }

    private static ViewerCountResult ReadTwitchViewerCount(StreamTarget target, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new ViewerCountResult(ViewerCountState.Unavailable, null, "Twitch viewer count response did not include stream data.");
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("user_login", out var login) &&
                login.ValueKind == JsonValueKind.String &&
                !string.Equals(login.GetString(), target.Channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetInt32(item, "viewer_count") is { } viewerCount)
            {
                return new ViewerCountResult(
                    ViewerCountState.Available,
                    viewerCount,
                    "Twitch viewer count updated.",
                    GetOptionalString(item, "game_name"),
                    GetOptionalString(item, "title"));
            }
        }

        return new ViewerCountResult(ViewerCountState.Offline, null, "Twitch stream is offline.");
    }

    private static ViewerCountResult ReadKickViewerCount(StreamTarget target, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new ViewerCountResult(ViewerCountState.Unavailable, null, "Kick viewer count response did not include channel data.");
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("slug", out var slug) &&
                slug.ValueKind == JsonValueKind.String &&
                !string.Equals(slug.GetString(), target.Channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("stream", out var stream) ||
                stream.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return new ViewerCountResult(ViewerCountState.Offline, null, "Kick stream is offline.");
            }

            if (stream.ValueKind != JsonValueKind.Object)
            {
                return new ViewerCountResult(ViewerCountState.Unavailable, null, "Kick stream data had an unexpected shape.");
            }

            if (TryGetBool(stream, "is_live") is false)
            {
                return new ViewerCountResult(ViewerCountState.Offline, null, "Kick stream is offline.");
            }

            if (TryGetInt32(stream, "viewer_count") is { } viewerCount)
            {
                // Kick reports the category on the channel object, next to "stream", not inside it.
                return new ViewerCountResult(
                    ViewerCountState.Available,
                    viewerCount,
                    "Kick viewer count updated.",
                    TryReadNestedString(item, "category", "name"),
                    FirstNonEmpty(
                        GetOptionalString(item, "stream_title"),
                        GetOptionalString(stream, "stream_title"),
                        GetOptionalString(stream, "title")));
            }

            return new ViewerCountResult(ViewerCountState.Unavailable, null, "Kick stream data did not include viewer_count.");
        }

        return new ViewerCountResult(ViewerCountState.Offline, null, "Kick stream is offline.");
    }

    private HttpClient GetSnapshotHttpClient()
    {
        return snapshotProvider.HttpClientForCredentialValidation;
    }

}
