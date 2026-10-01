using System.Net.Sockets;

internal sealed class LocalHlsHttpServer : IDisposable
{
    private readonly IReadOnlyDictionary<string, (byte[] Bytes, string ContentType)> responses;
    private readonly HttpListener listener = new();
    private readonly Task acceptLoop;

    private LocalHlsHttpServer(
        IReadOnlyDictionary<string, (byte[] Bytes, string ContentType)> responses,
        string mediaPath)
    {
        this.responses = responses;
        var port = GetAvailablePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        MediaUri = new Uri(prefix + mediaPath);
        acceptLoop = AcceptRequestsAsync();
    }

    internal Uri MediaUri { get; }
    internal ConcurrentBag<string> RequestedPaths { get; } = [];

    internal static LocalHlsHttpServer StartPlaylist(string fixtureDirectory) => new(
        Directory.EnumerateFiles(fixtureDirectory).ToDictionary(
            path => "/" + Path.GetFileName(path),
            path => (File.ReadAllBytes(path), Path.GetExtension(path) switch
            {
                ".m3u8" => "application/vnd.apple.mpegurl",
                ".mp4" => "video/mp4",
                ".m4s" => "video/iso.segment",
                _ => "video/mp2t"
            }), StringComparer.Ordinal), "index.m3u8");

    internal static LocalHlsHttpServer Start(string fixtureDirectory)
    {
        var playlistPath = Path.Combine(fixtureDirectory, "index.m3u8");
        var segmentPath = Path.Combine(fixtureDirectory, "tone.ts");
        return new LocalHlsHttpServer(
            new Dictionary<string, (byte[] Bytes, string ContentType)>(StringComparer.Ordinal)
            {
                ["/index.m3u8"] = (File.ReadAllBytes(playlistPath), "application/vnd.apple.mpegurl"),
                ["/tone.ts"] = (File.ReadAllBytes(segmentPath), "video/mp2t")
            },
            "index.m3u8");
    }

    private static int GetAvailablePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private async Task AcceptRequestsAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (!listener.IsListening)
            {
                return;
            }
            catch (ObjectDisposedException) when (!listener.IsListening)
            {
                return;
            }

            _ = RespondAsync(context);
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        try
        {
            var response = context.Response;
            RequestedPaths.Add(context.Request.Url?.AbsolutePath ?? "");
            if (!responses.TryGetValue(context.Request.Url?.AbsolutePath ?? "", out var content))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.ContentLength64 = 0;
                return;
            }

            response.ContentType = content.ContentType;
            response.Headers["Accept-Ranges"] = "bytes";
            var start = 0;
            var end = content.Bytes.Length - 1;
            var range = context.Request.Headers["Range"];
            if (range is not null)
            {
                if (!TryParseRange(range, content.Bytes.Length, out start, out end))
                {
                    response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                    response.Headers["Content-Range"] = $"bytes */{content.Bytes.Length}";
                    response.ContentLength64 = 0;
                    return;
                }

                response.StatusCode = (int)HttpStatusCode.PartialContent;
                response.Headers["Content-Range"] = $"bytes {start}-{end}/{content.Bytes.Length}";
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.OK;
            }

            response.ContentLength64 = end - start + 1;
            if (!string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                await response.OutputStream.WriteAsync(content.Bytes.AsMemory(start, end - start + 1)).ConfigureAwait(false);
        }
        catch (HttpListenerException)
        {
            // VLC may close a range request after it has enough input buffered.
        }
        catch (IOException)
        {
            // The client closed the request before the response body completed.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static bool TryParseRange(string value, int length, out int start, out int end)
    {
        start = 0;
        end = length - 1;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(',')) return false;
        var parts = value[6..].Split('-', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out start) || start < 0 || start >= length) return false;
        if (parts[1].Length > 0 && (!int.TryParse(parts[1], out end) || end < start)) return false;
        end = Math.Min(end, length - 1);
        return true;
    }

    public void Dispose()
    {
        listener.Stop();
        listener.Close();
        try { acceptLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
    }
}
