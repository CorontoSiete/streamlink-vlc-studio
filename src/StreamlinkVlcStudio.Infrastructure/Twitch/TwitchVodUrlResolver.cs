using System.Globalization;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Twitch;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Replay;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>
/// Resolves ordinary Twitch archives without starting Python or parsing every quality's
/// media playlist. Uses Twitch's playback authorization and validates the selected playlist.
/// Unsupported inputs and provider failures are handled by the caller's Streamlink fallback.
/// </summary>
internal sealed class TwitchVodUrlResolver
{
    private static readonly HttpClient SharedClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(8), includeUserAgent: true, allowAutoRedirect: false);
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator validator;
    private readonly TwitchVodPlaylistHandoff playlistHandoff;

    internal TwitchVodUrlResolver() : this(SharedClient, ReplayUrlSecurityValidator.Shared, TwitchVodPlaylistHandoff.Shared) { }

    internal TwitchVodUrlResolver(HttpClient httpClient, ReplayUrlSecurityValidator validator,
        TwitchVodPlaylistHandoff? playlistHandoff = null)
    {
        this.httpClient = httpClient;
        this.validator = validator;
        this.playlistHandoff = playlistHandoff ?? new TwitchVodPlaylistHandoff();
    }

    internal async Task<StreamlinkResolvedUrl> ResolveAsync(StreamTransportRequest request, CancellationToken cancellationToken)
    {
        var id = request.Target.MediaId;
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit))
            throw new InvalidDataException("A numeric Twitch video ID is required.");

        // Same playback-token operation, player type, codecs and Usher parameters as
        // Streamlink's Twitch plugin. Tokens and signed master URLs never enter logs.
        var payload = JsonSerializer.Serialize(new
        {
            operationName = "PlaybackAccessToken",
            variables = new { isLive = false, login = "", isVod = true, vodID = id, playerType = "embed", platform = "site" },
            extensions = new { persistedQuery = new { version = 1, sha256Hash = "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9" } }
        });
        using var document = await new TwitchGraphQlTransport(httpClient).SendAsync(payload,
            TwitchGraphQlTransport.PublicClientId, TwitchGraphQlTransport.CreateDeviceId(), cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("videoPlaybackAccessToken", out var token) || token.ValueKind != JsonValueKind.Object ||
            !token.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.String ||
            !token.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(signature.GetString()) || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("Twitch did not return a video playback token.");

        var masterUri = new Uri($"https://usher.ttvnw.net/vod/v2/{id}.m3u8?platform=web" +
            $"&p={Random.Shared.Next(999999).ToString(CultureInfo.InvariantCulture)}&allow_source=true&allow_audio_only=true" +
            "&playlist_include_framerate=true&supported_codecs=h264" +
            $"&nauthsig={Uri.EscapeDataString(signature.GetString()!)}&nauth={Uri.EscapeDataString(value.GetString()!)}");
        var master = await ValidatedReplayHttpClient.ReadPlaylistAsync(httpClient, validator, masterUri,
            PlatformKind.Twitch, cancellationToken).ConfigureAwait(false);
        var selected = TwitchVodVariantPlaylist.Select(master.Content, master.Uri, request.Quality);
        var media = await ValidatedReplayHttpClient.ReadPlaylistAsync(httpClient, validator, selected,
            PlatformKind.Twitch, cancellationToken).ConfigureAwait(false);
        var validated = ValidateMediaPlaylist(media.Content, media.Uri);
        cancellationToken.ThrowIfCancellationRequested();
        playlistHandoff.Offer(media.Uri, validated);
        return new StreamlinkResolvedUrl(media.Uri, "Resolved selected Twitch VOD quality directly.");
    }

    private static string ValidateMediaPlaylist(string content, Uri uri)
    {
        var lines = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "#EXTM3U" || !TwitchMutedVodPlaylist.Inspect(content).IsMediaPlaylist)
            throw new InvalidDataException("Twitch did not return a playable media playlist.");
        var pendingSegment = false;
        var segments = 0;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                if (pendingSegment || !decimal.TryParse(line[8..].Split(',')[0], NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var duration) || duration <= 0 || duration > 3600)
                    throw new InvalidDataException("The selected VOD playlist has an invalid segment duration.");
                pendingSegment = true;
            }
            else if (!line.StartsWith('#'))
            {
                if (!pendingSegment) throw new InvalidDataException("The selected VOD playlist has an unpaired segment.");
                pendingSegment = false;
                segments++;
            }
        }
        if (pendingSegment || segments == 0) throw new InvalidDataException("The selected VOD playlist has missing segments.");
        // Reuse the existing provider policy for segment, key and initialization URLs.
        // Playback still uses the remote URL, preserving growing-playlist refreshes.
        return TwitchMutedVodPlaylist.RewriteForRepair(content, uri, static segment => segment.AbsoluteUri);
    }
}
