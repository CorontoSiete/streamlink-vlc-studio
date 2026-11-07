using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using StreamlinkVlcStudio.Infrastructure.Hls;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Replay;
using StreamlinkVlcStudio.Infrastructure.Vod;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

/// <summary>Only server-resolved resources receive opaque IDs. There is no URL/path proxy endpoint.</summary>
internal sealed class LocalWebMedia : IDisposable
{
    private static readonly Uri TrustedPlaylistUri = new("https://trusted-playlist.invalid/index.m3u8");
    private readonly ConcurrentDictionary<string, Uri> resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> localFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly HttpClient http;
    private readonly string playbackId;
    private string? trustedPlaylist;
    internal string Root { get; }

    internal LocalWebMedia(string playbackId, Uri root, HttpClient? http = null)
    {
        this.playbackId = playbackId;
        Root = RegisterRemote(root, ".m3u8");
        this.http = http ?? CreateHttpClient();
    }

    internal LocalWebMedia(string playbackId, string root, IEnumerable<string> files)
    {
        this.playbackId = playbackId;
        foreach (var path in files.Append(root)) localFiles.Add(Path.GetFullPath(path));
        Root = RegisterLocal(root);
        http = CreateHttpClient();
    }

    internal static async Task<LocalWebMedia> FromTrustedPlaylistAsync(string playbackId, Uri playlist, CancellationToken token)
    {
        // Only the app's subscriber-only resolver can call this path; normal resolver/client URLs cannot authorize files.
        if (!playlist.IsFile || playlist.IsUnc || !playlist.LocalPath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid trusted playlist.");
        OfflineVodPackage.RequireRegularPath(Path.GetDirectoryName(playlist.LocalPath)!);
        OfflineVodPackage.RequireRegularPath(playlist.LocalPath);
        var bytes = await BoundedByteReader.ReadFileAsync(playlist.LocalPath, 16 * 1024 * 1024, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The trusted playlist is empty or too large.");
        var media = new LocalWebMedia(playbackId, TrustedPlaylistUri);
        try
        {
            media.trustedPlaylist = new UTF8Encoding(false, true).GetString(bytes);
            media.RewritePlaylist(media.trustedPlaylist, TrustedPlaylistUri);
        }
        catch { media.Dispose(); throw; }
        return media;
    }

    internal static bool IsSafeUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        !uri.OriginalString.Any(char.IsControl) &&
        (!IPAddress.TryParse(uri.IdnHost, out var address) || ReplayUrlSecurityValidator.IsPublicAddress(address));

    internal string RegisterRemote(Uri uri) => RegisterRemote(uri, null);

    private string RegisterRemote(Uri uri, string? extension)
    {
        if (!IsSafeUri(uri)) throw new InvalidDataException("Media must use a public HTTPS endpoint.");
        return Register(uri, extension);
    }

    private string RegisterLocal(string path)
    {
        path = Path.GetFullPath(path);
        if (!localFiles.Contains(path)) throw new InvalidDataException("Media is outside the validated offline package.");
        OfflineVodPackage.RequireRegularPath(Path.GetDirectoryName(path)!);
        OfflineVodPackage.RequireRegularPath(path);
        return Register(new Uri(path));
    }

    private string Register(Uri uri, string? extension = null)
    {
        lock (gate)
        {
            if (ids.TryGetValue(uri.AbsoluteUri, out var existing)) return existing;
            if (ids.Count >= 100_000) throw new InvalidDataException("Media resource limit reached. Reopen the stream.");
            // Native HLS clients use the URL extension as well as Content-Type to
            // identify playlists and media. The opaque token still owns the route.
            extension ??= Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (extension is not (".m3u8" or ".ts" or ".mp4" or ".m4s" or ".aac" or ".mp3" or ".vtt")) extension = "";
            var id = LocalWebAuthentication.NewToken() + extension;
            ids.Add(uri.AbsoluteUri, id);
            resources[id] = uri;
            return id;
        }
    }

    internal string PathFor(string id) => $"/media/{playbackId}/{id}";

    internal string RewritePlaylist(string playlist, Uri source)
    {
        if (playlist.Length > 16 * 1024 * 1024 || !playlist.TrimStart('\uFEFF').StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid HLS playlist.");
        var result = new StringBuilder();
        foreach (var raw in playlist.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 16384) throw new InvalidDataException("HLS line exceeds the limit.");
            if (line.StartsWith('#'))
            {
                // Unsupported variable substitution/content steering can introduce unproxied URLs.
                if (line.StartsWith("#EXT-X-DEFINE:", StringComparison.Ordinal) || line.StartsWith("#EXT-X-CONTENT-STEERING:", StringComparison.Ordinal))
                    throw new InvalidDataException("This HLS extension is not supported.");
                if (line.StartsWith("#EXT-X-TWITCH-PREFETCH:", StringComparison.Ordinal))
                    line = "#EXT-X-TWITCH-PREFETCH:" + Map(line[23..]);
                if (line.StartsWith("#EXT-X-PREFETCH:", StringComparison.Ordinal))
                    line = "#EXT-X-PREFETCH:" + Map(line[16..]);
                if (line.StartsWith("#EXT-X-", StringComparison.Ordinal) &&
                    (line.Contains("URI=", StringComparison.Ordinal) || line.StartsWith("#EXT-X-DATERANGE:", StringComparison.Ordinal)))
                {
                    var colon = line.IndexOf(':');
                    if (colon < 0 || !HlsAttributeList.TryParse(line.AsSpan(colon + 1), out var attributes))
                        throw new InvalidDataException("Invalid HLS attributes.");
                    if (attributes.TryGetValue("URI", out var uri))
                    {
                        if (!uri.IsQuoted) throw new InvalidDataException("HLS URI attributes must be quoted.");
                        attributes["URI"] = new(Map(uri.Value), true);
                    }
                    if (line.StartsWith("#EXT-X-DATERANGE:", StringComparison.Ordinal))
                        attributes.Remove("X-TV-TWITCH-TRIGGER-URL");
                    line = line[..(colon + 1)] + string.Join(',', attributes.Select(attribute =>
                        attribute.Key + "=" + (attribute.Value.IsQuoted ? "\"" + attribute.Value.Value + "\"" : attribute.Value.Value)));
                }
                if (line.Contains("://", StringComparison.Ordinal))
                    throw new InvalidDataException("Unsupported absolute URL in HLS metadata.");
            }
            else if (!string.IsNullOrWhiteSpace(line)) line = Map(line.Trim());
            result.Append(line).Append('\n');
        }
        return result.ToString();

        string Map(string value)
        {
            if (value.Length == 0 || value.Contains("{$", StringComparison.Ordinal) || value.Any(char.IsControl))
                throw new InvalidDataException("Invalid media reference.");
            if (source.IsFile)
            {
                // Offline packages are flat and validated by AcquireOfflinePlaybackAsync.
                if (!OfflineHlsAssetName.IsValid(value)) throw new InvalidDataException("Invalid offline media reference.");
                return PathFor(RegisterLocal(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source.LocalPath)!, value)));
            }
            if (source.IdnHost == TrustedPlaylistUri.IdnHost && !Uri.TryCreate(value, UriKind.Absolute, out _))
                throw new InvalidDataException("Trusted playlists must contain absolute remote media references.");
            return PathFor(RegisterRemote(new Uri(source, value)));
        }
    }

    internal async Task ServeAsync(HttpContext context, string resourceId)
    {
        if (!resources.TryGetValue(resourceId, out var uri)) { context.Response.StatusCode = 404; return; }
        var token = context.RequestAborted;
        if (resourceId == Root && trustedPlaylist is not null)
        {
            await WritePlaylistAsync(context, trustedPlaylist, uri).ConfigureAwait(false);
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        if (uri.IsFile)
        {
            if (!localFiles.Contains(Path.GetFullPath(uri.LocalPath))) throw new InvalidDataException();
            OfflineVodPackage.RequireRegularPath(Path.GetDirectoryName(uri.LocalPath)!);
            OfflineVodPackage.RequireRegularPath(uri.LocalPath);
            if (uri.LocalPath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await BoundedByteReader.ReadFileAsync(uri.LocalPath, 16 * 1024 * 1024, timeout.Token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Offline playlist is empty or too large.");
                await WritePlaylistAsync(context, Encoding.UTF8.GetString(bytes), uri).ConfigureAwait(false);
            }
            else
                await Results.File(uri.LocalPath, ContentType(uri.LocalPath), enableRangeProcessing: true).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        if (context.Request.Query.Count > 0)
            uri = new Uri(QueryHelpers.AddQueryString(uri.AbsoluteUri, context.Request.Query.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value.ToString()))));
        var requestedRange = context.Request.Headers.Range.ToString();
        var knownPlaylist = resourceId == Root || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        var response = await FetchAsync(uri, knownPlaylist ? "" : requestedRange, timeout.Token).ConfigureAwait(false);
        try
        {
            var final = response.RequestMessage!.RequestUri!;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            var isPlaylist = knownPlaylist || final.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);
            // A native media stack may probe an extensionless URL with a byte range.
            // A partial playlist cannot be rewritten or played, so fetch it whole.
            if (isPlaylist && response.StatusCode == HttpStatusCode.PartialContent)
            {
                response.Dispose();
                response = await FetchAsync(uri, "", timeout.Token).ConfigureAwait(false);
                final = response.RequestMessage!.RequestUri!;
            }
            if (!response.IsSuccessStatusCode)
            {
                context.Response.StatusCode = (int)response.StatusCode is 404 or 410 or 416 ? (int)response.StatusCode : 502;
                return;
            }
            if (isPlaylist)
            {
                if (response.StatusCode == HttpStatusCode.PartialContent) throw new InvalidDataException("The media playlist is incomplete.");
                var bytes = await BoundedByteReader.ReadOrThrowAsync(response.Content, 16 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
                await WritePlaylistAsync(context, new UTF8Encoding(false, true).GetString(bytes), final).ConfigureAwait(false);
                return;
            }
            const long maxBytes = 128L * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Media segment is too large.");
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = ContentType(final.AbsolutePath);
            context.Response.ContentLength = response.Content.Headers.ContentLength;
            if (response.Content.Headers.ContentRange is { } range) context.Response.Headers.ContentRange = range.ToString();
            context.Response.Headers.AcceptRanges = "bytes";
            if (HttpMethods.IsHead(context.Request.Method)) return;
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += count;
                if (total > maxBytes) { context.Abort(); return; }
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
            }
        }
        finally { response.Dispose(); }
    }

    private async Task WritePlaylistAsync(HttpContext context, string playlist, Uri source)
    {
        var rewritten = RewritePlaylist(playlist, source);
        context.Response.ContentType = "application/vnd.apple.mpegurl";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(rewritten);
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.WriteAsync(rewritten, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> FetchAsync(Uri uri, string range, CancellationToken token)
    {
        if (range.Length != 0 && (range.Length > 80 || !RangeHeaderValue.TryParse(range, out var parsed) ||
            parsed.Unit != "bytes" || parsed.Ranges.Count != 1)) throw new InvalidDataException("Invalid byte range.");
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsSafeUri(uri)) throw new InvalidDataException("Invalid media endpoint.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (range.Length != 0) request.Headers.Range = RangeHeaderValue.Parse(range);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("Missing media redirect location.");
            uri = new Uri(uri, location);
        }
        throw new InvalidDataException("Too many media redirects.");
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, token) =>
        {
            // Validate and connect to the SAME address, closing the DNS-rebinding race.
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(a => !ReplayUrlSecurityValidator.IsPublicAddress(a)))
                throw new InvalidDataException("Media host resolved to a non-public address.");
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("The media host is unavailable.");
        }
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static string ContentType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".m4s" => "video/iso.segment",
        ".aac" => "audio/aac",
        ".mp3" => "audio/mpeg",
        ".vtt" => "text/vtt",
        ".ts" => "video/mp2t",
        _ => "application/octet-stream"
    };

    public void Dispose() => http.Dispose();
}
