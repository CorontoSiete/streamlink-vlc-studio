using System.Globalization;
using System.Net;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Replay;
using StreamlinkVlcStudio.Infrastructure.Twitch;

namespace StreamlinkVlcStudio.Infrastructure.Previews;

/// <summary>Authorizes the stream and fetches only the quality the preview will use.</summary>
internal sealed class LivePreviewSourceResolver
{
    private static readonly HttpClient SharedClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(8), includeUserAgent: true, allowAutoRedirect: false);
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator validator;
    private readonly Func<StreamTransportRequest, bool> canResolve;

    internal LivePreviewSourceResolver() : this(SharedClient,
        new ReplayUrlSecurityValidator(static (host, token) => Dns.GetHostAddressesAsync(host, token), LivePreviewPolicy.IsAllowedUri),
        LivePreviewPolicy.CanResolve)
    { }

    internal LivePreviewSourceResolver(HttpClient httpClient, ReplayUrlSecurityValidator validator,
        Func<StreamTransportRequest, bool> canResolve)
    {
        this.httpClient = httpClient;
        this.validator = validator;
        this.canResolve = canResolve;
    }

    internal async Task<LivePreviewPlaylistSession?> OpenAsync(StreamTransportRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!canResolve(request)) return null;
        var platform = request.Target.Platform;
        var masterUri = platform == PlatformKind.Twitch
            ? await ResolveTwitchAsync(request.Target.Channel, token).ConfigureAwait(false)
            : await ResolveKickAsync(request.Target.Channel, token).ConfigureAwait(false);
        var master = await ValidatedReplayHttpClient.ReadPlaylistAsync(httpClient, validator, masterUri, platform, token).ConfigureAwait(false);
        var selected = TwitchVodVariantPlaylist.Select(master.Content, master.Uri, request.Quality.Split(','),
            uri => LivePreviewPolicy.IsAllowedUri(uri, platform));
        var firstPlaylist = true;
        Uri? initializationUri = null;
        var initial = await ReadAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // The first validated playlist is handed to VLC locally, avoiding a duplicate
        // provider request. Every later reload is validated by the same session.
        return new LivePreviewPlaylistSession(initial, ReadAsync, LivePreviewPlaylist.GetPlaybackOptions(initial, request));

        async Task<string> ReadAsync(CancellationToken cancellationToken)
        {
            var media = await ValidatedReplayHttpClient.ReadPlaylistAsync(httpClient, validator, selected, platform,
                cancellationToken).ConfigureAwait(false);
            var rewritten = LivePreviewPlaylist.Rewrite(media.Content, media.Uri, platform, out var map);
            // VLC 3 retains one initialization section for its representation. A format
            // or map change needs a new transport, before that refresh reaches VLC.
            if (!firstPlaylist && map != initializationUri)
                throw new InvalidDataException("The live preview initialization section changed.");
            initializationUri = map;
            firstPlaylist = false;
            return rewritten;
        }
    }

    private async Task<Uri> ResolveTwitchAsync(string channel, CancellationToken token)
    {
        // Same token operation and codec request as Streamlink's Twitch plugin.
        var payload = JsonSerializer.Serialize(new
        {
            operationName = "PlaybackAccessToken",
            variables = new { isLive = true, login = channel, isVod = false, vodID = "", playerType = "embed", platform = "site" },
            extensions = new { persistedQuery = new { version = 1, sha256Hash = "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9" } }
        });
        // Keep Streamlink's anonymous playback identity. A fresh web device changes
        // Twitch's token and can return a preroll that forces an unnecessary restart.
        using var document = await new TwitchGraphQlTransport(httpClient).SendAsync(payload,
            TwitchGraphQlTransport.PublicClientId, deviceId: null, token).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("streamPlaybackAccessToken", out var access) || access.ValueKind != JsonValueKind.Object ||
            !access.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.String ||
            !access.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(signature.GetString()) || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("Twitch did not return a live playback token.");
        return new Uri($"https://usher.ttvnw.net/api/v2/channel/hls/{Uri.EscapeDataString(channel)}.m3u8?platform=web" +
            $"&p={Random.Shared.Next(999999).ToString(CultureInfo.InvariantCulture)}&allow_source=true&allow_audio_only=true" +
            "&playlist_include_framerate=true&supported_codecs=h264&fast_bread=true" +
            $"&sig={Uri.EscapeDataString(signature.GetString()!)}&token={Uri.EscapeDataString(value.GetString()!)}");
    }

    private async Task<Uri> ResolveKickAsync(string channel, CancellationToken token)
    {
        var uri = new Uri($"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel)}/livestream");
        using var response = await ValidatedReplayHttpClient.SendGetAsync(httpClient, validator, uri, PlatformKind.Kick, address =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamStudio/0.1");
            request.Headers.Referrer = new Uri("https://kick.com/");
            return request;
        }, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await BoundedHttpContentReader.ReadJsonAsync(response.Content, token).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("playback_url", out var playback) || playback.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(playback.GetString(), UriKind.Absolute, out var master))
            throw new InvalidDataException("Kick did not return a live playback URL.");
        return master;
    }
}
