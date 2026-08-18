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

public sealed class StreamMetadataService : IStreamMetadataService
{
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(12),
        includeUserAgent: true,
        acceptJson: true);
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly LiveChannelSnapshotProvider snapshotProvider;
    private readonly IKickTokenProvider kickTokenProvider;

    public StreamMetadataService(IAppLogger logger)
        : this(
            logger,
            SharedHttpClient,
            LiveChannelSnapshotProvider.Shared,
            KickTokenProvider.Shared)
    {
    }

    public StreamMetadataService(IAppLogger logger, HttpClient httpClient)
        : this(
            logger,
            httpClient,
            new LiveChannelSnapshotProvider(httpClient),
            KickTokenProvider.Shared)
    {
    }

    internal StreamMetadataService(
        IAppLogger logger,
        HttpClient httpClient,
        LiveChannelSnapshotProvider snapshotProvider,
        IKickTokenProvider kickTokenProvider)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.snapshotProvider = snapshotProvider;
        this.kickTokenProvider = kickTokenProvider;
    }

    public Task<StreamMetadataResult> GetLiveStreamMetadataAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        return target.Platform switch
        {
            PlatformKind.Twitch => GetTwitchStreamMetadataAsync(target, settings.Chat, cancellationToken),
            PlatformKind.Kick => GetKickStreamMetadataAsync(target, settings.Chat, cancellationToken),
            _ => Task.FromResult(new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                $"Stream metadata is not supported for {target.Platform}."))
        };
    }

    private async Task<StreamMetadataResult> GetTwitchStreamMetadataAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new StreamMetadataResult(
                StreamMetadataState.NotConfigured,
                "",
                "",
                "Twitch stream thumbnails require a Twitch OAuth token.");
        }

        var clientId = await TwitchClientIdResolver.ResolveAsync(
            settings,
            httpClient,
            token,
            logger,
            "Recent",
            "Could not resolve Twitch Client ID from the OAuth token.",
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new StreamMetadataResult(
                StreamMetadataState.NotConfigured,
                "",
                "",
                "Twitch stream thumbnails require a Twitch Client ID that matches the OAuth token.");
        }

        var response = await snapshotProvider
            .GetTwitchAsync(target.Channel, token, clientId, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Recent",
                $"Twitch stream metadata request failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(response.Body)}");
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "Twitch stream metadata unavailable. Check the Twitch Client ID and OAuth token.");
        }

        using var document = JsonDocument.Parse(response.Body);
        var metadata = ReadTwitchMetadata(target, document.RootElement);
        if (metadata.State != StreamMetadataState.Available ||
            !string.IsNullOrWhiteSpace(metadata.ProfileImageUrl))
        {
            return metadata;
        }

        try
        {
            var profileImages = await TwitchProfileImageLookup.GetAsync(
                httpClient,
                token,
                clientId,
                [target.Channel],
                cancellationToken).ConfigureAwait(false);
            return profileImages.TryGetValue(target.Channel, out var profileImage)
                ? metadata with { ProfileImageUrl = profileImage }
                : metadata;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"Twitch profile image lookup failed for {target.DisplayName}.", ex);
            return metadata;
        }
    }

    private async Task<StreamMetadataResult> GetKickStreamMetadataAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        var accessToken = await kickTokenProvider
            .ResolveAsync(settings, logger, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new StreamMetadataResult(
                StreamMetadataState.NotConfigured,
                "",
                "",
                "Kick stream thumbnails require a Kick user token or Kick Client ID and Client Secret.");
        }

        var response = await snapshotProvider
            .GetKickAsync(target.Channel, accessToken, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Recent",
                $"Kick stream metadata request failed for {target.DisplayName}: {(int)response.StatusCode} {response.ReasonPhrase}. {ApiErrorMessage.Extract(response.Body)}");
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "Kick stream metadata unavailable. Check Kick API credentials.");
        }

        using var document = JsonDocument.Parse(response.Body);
        var metadata = ReadKickMetadata(target, document.RootElement);
        if (metadata.State != StreamMetadataState.Available ||
            !string.IsNullOrWhiteSpace(metadata.ProfileImageUrl))
        {
            return metadata;
        }

        var broadcasterUserId = ReadKickBroadcasterUserId(target, document.RootElement);
        if (string.IsNullOrWhiteSpace(broadcasterUserId))
        {
            return metadata;
        }

        try
        {
            var profileImages = await KickProfileImageLookup.GetAsync(
                httpClient,
                accessToken,
                [broadcasterUserId],
                cancellationToken).ConfigureAwait(false);
            return profileImages.TryGetValue(broadcasterUserId, out var profileImage)
                ? metadata with { ProfileImageUrl = profileImage }
                : metadata;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"Kick profile image lookup failed for {target.DisplayName}.", ex);
            return metadata;
        }
    }

    private static StreamMetadataResult ReadTwitchMetadata(StreamTarget target, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "Twitch stream metadata response did not include stream data.");
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

            return new StreamMetadataResult(
                StreamMetadataState.Available,
                NormalizeImageUrl(GetOptionalString(item, "thumbnail_url"), "440", "248"),
                FirstNonEmpty(GetOptionalString(item, "user_name"), target.Channel),
                "Twitch stream metadata updated.",
                GetOptionalString(item, "game_name"),
                GetOptionalString(item, "profile_image_url"));
        }

        return new StreamMetadataResult(StreamMetadataState.Offline, "", "", "Twitch stream is offline.");
    }

    private static StreamMetadataResult ReadKickMetadata(StreamTarget target, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "Kick stream metadata response did not include channel data.");
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
                return new StreamMetadataResult(StreamMetadataState.Offline, "", "", "Kick stream is offline.");
            }

            if (stream.ValueKind != JsonValueKind.Object)
            {
                return new StreamMetadataResult(
                    StreamMetadataState.Unavailable,
                    "",
                    "",
                    "Kick stream data had an unexpected shape.");
            }

            if (TryGetBool(stream, "is_live") == false)
            {
                return new StreamMetadataResult(StreamMetadataState.Offline, "", "", "Kick stream is offline.");
            }

            var category = "";
            if (item.TryGetProperty("category", out var categoryElement) &&
                categoryElement.ValueKind == JsonValueKind.Object)
            {
                category = GetOptionalString(categoryElement, "name");
            }

            return new StreamMetadataResult(
                StreamMetadataState.Available,
                NormalizeImageUrl(FirstNonEmpty(
                    GetOptionalString(stream, "thumbnail"),
                    GetOptionalString(item, "thumbnail"))),
                FirstNonEmpty(GetOptionalString(item, "slug"), target.Channel),
                "Kick stream metadata updated.",
                category,
                NormalizeImageUrl(FirstNonEmpty(
                    GetOptionalString(item, "profile_picture"),
                    GetOptionalString(item, "profile_pic"))));
        }

        return new StreamMetadataResult(StreamMetadataState.Offline, "", "", "Kick stream is offline.");
    }

    private static string ReadKickBroadcasterUserId(StreamTarget target, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!string.Equals(
                    GetOptionalString(item, "slug"),
                    target.Channel,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return GetOptionalString(item, "broadcaster_user_id");
        }

        return "";
    }

}
