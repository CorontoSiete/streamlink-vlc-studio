using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

/// <summary>Twitch returns HTTP 200 JPEGs for missing covers. Inspect redirects before serving artwork.</summary>
public sealed class LocalWebCategoryArtwork
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(10) };
    private const int MaximumEntries = 128;
    private const int MaximumImageBytes = 256 * 1024;
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> now;
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim downloads = new(4, 4);
    private readonly object gate = new();

    public LocalWebCategoryArtwork(HttpClient? http = null) : this(http ?? SharedHttp, () => DateTimeOffset.UtcNow) { }

    internal LocalWebCategoryArtwork(HttpClient http, Func<DateTimeOffset> now)
    {
        this.http = http;
        this.now = now;
    }

    internal static bool IsArtworkUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.Host.Equals("static-cdn.jtvnw.net", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/ttv-boxart/", StringComparison.Ordinal) &&
        uri.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingArtwork(Uri uri) => uri.Host.Equals("static-cdn.jtvnw.net", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/ttv-static/404_boxart-", StringComparison.Ordinal);

    internal async Task ServeAsync(HttpContext context, string url)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!IsArtworkUrl(url)) { context.Response.StatusCode = 404; return; }
        Entry entry;
        lock (gate)
        {
            if (!cache.TryGetValue(url, out entry!))
            {
                if (cache.Count >= MaximumEntries) cache.Remove(cache.MinBy(pair => pair.Value.LastUsed).Key);
                cache[url] = entry = new();
            }
            entry.LastUsed = now();
        }

        var token = context.RequestAborted;
        Artwork? artwork;
        await entry.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (entry.Expires <= now())
            {
                await downloads.WaitAsync(token).ConfigureAwait(false);
                try { entry.Artwork = await DownloadAsync(new Uri(url), token).ConfigureAwait(false); }
                catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !token.IsCancellationRequested)
                { entry.Artwork = null; }
                finally { downloads.Release(); }
                entry.Expires = now().Add(entry.Artwork is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));
            }
            artwork = entry.Artwork;
        }
        finally { entry.Gate.Release(); }
        if (artwork is null) { context.Response.StatusCode = 404; return; }
        context.Response.ContentType = artwork.ContentType;
        context.Response.ContentLength = artwork.Bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.Body.WriteAsync(artwork.Bytes, token).ConfigureAwait(false);
    }

    private async Task<Artwork?> DownloadAsync(Uri uri, CancellationToken token)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // Follow only artwork on Twitch's exact CDN; never forward browser cookies or credentials.
            if (!IsArtworkUrl(uri.AbsoluteUri)) return null;
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && IsMissingArtwork(final)) return null;
            if (response.StatusCode is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Redirect or
                System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location is not { } location || !Uri.TryCreate(uri, location, out var next) || IsMissingArtwork(next)) return null;
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await BoundedByteReader.ReadAsync(response.Content, MaximumImageBytes, token).ConfigureAwait(false);
            // Twitch serves some JPEG covers with image/png headers. Validate the payload
            // and serve its actual type, retaining missing-art redirect detection above.
            var contentType = bytes is null ? null : LocalWebImageContent.ContentType(bytes);
            return contentType is null ? null : new Artwork(bytes!, contentType);
        }
        return null;
    }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal DateTimeOffset LastUsed;
        internal DateTimeOffset Expires;
        internal Artwork? Artwork;
    }

    private sealed record Artwork(byte[] Bytes, string ContentType);
}
