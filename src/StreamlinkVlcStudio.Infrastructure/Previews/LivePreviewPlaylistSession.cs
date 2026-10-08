using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Threading;

namespace StreamlinkVlcStudio.Infrastructure.Previews;

/// <summary>Serves one bounded playlist; media bytes still travel directly from the provider to VLC.</summary>
internal sealed class LivePreviewPlaylistSession : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource fallback = new();
    private readonly Func<CancellationToken, Task<string>> refresh;
    private readonly Task serve;
    private readonly object gate = new();
    private Task? disposal;
    private bool disposed;
    private string? initial;

    internal LivePreviewPlaylistSession(string initial, Func<CancellationToken, Task<string>> refresh,
        LivePreviewPlaybackOptions? playbackOptions = null)
    {
        this.initial = initial;
        this.refresh = refresh;
        PlaybackOptions = playbackOptions;
        listener.Server.ExclusiveAddressUse = true;
        try
        {
            listener.Start(4);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            PlaybackUri = new Uri($"http://127.0.0.1:{port}/{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}/preview.m3u8");
            serve = ServeAsync();
        }
        catch
        {
            listener.Stop();
            lifetime.Dispose();
            fallback.Dispose();
            throw;
        }
    }

    internal Uri PlaybackUri { get; }
    internal LivePreviewPlaybackOptions? PlaybackOptions { get; }
    internal CancellationToken FallbackToken => fallback.Token;

    internal void Stop() => CancellationSourceCleanup.Cancel(lifetime);

    private async Task ServeAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                requestBudget.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    using var stream = client.GetStream();
                    var result = await LocalHttpRequestReader.ReadWithStatusAsync(stream, 8192, requestBudget.Token).ConfigureAwait(false);
                    var request = result.Request;
                    if (request is null || request.Path != PlaybackUri.AbsolutePath ||
                        request.Method is not ("GET" or "HEAD") || request.Body.Length != 0 ||
                        !request.Headers.TryGetValue("Host", out var host) || host != PlaybackUri.Authority)
                    {
                        await WriteAsync(stream, 404, "", false, requestBudget.Token).ConfigureAwait(false);
                        continue;
                    }
                    string playlist;
                    try
                    {
                        playlist = initial ?? await refresh(requestBudget.Token).ConfigureAwait(false);
                        if (request.Method == "GET") initial = null;
                    }
                    catch (Exception ex) when (IsSourceFailure(ex) && !lifetime.IsCancellationRequested)
                    {
                        // The caller stops this player before starting Streamlink. No ad,
                        // malformed playlist or newly unsupported URI reaches native HLS.
                        CancellationSourceCleanup.Cancel(fallback);
                        break;
                    }
                    await WriteAsync(stream, 200, playlist, request.Method == "HEAD", requestBudget.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (!lifetime.IsCancellationRequested) CancellationSourceCleanup.Cancel(fallback);
        }
        finally { listener.Stop(); }
    }

    internal static bool IsSourceFailure(Exception ex) => ex is HttpRequestException or IOException or
        InvalidDataException or InvalidOperationException or OperationCanceledException or TimeoutException or
        System.Text.Json.JsonException or DecoderFallbackException or UriFormatException or SocketException;

    private static async Task WriteAsync(NetworkStream stream, int status, string content, bool head, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
            "Content-Type: application/vnd.apple.mpegurl\r\nCache-Control: no-store\r\nConnection: close\r\n" +
            $"Content-Length: {bytes.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (!head) await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(AsyncDisposal.Begin(gate, ref disposed, ref disposal, DisposeCoreAsync));

    private async Task DisposeCoreAsync()
    {
        try
        {
            Stop();
            await serve.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Dispose();
            fallback.Dispose();
        }
    }
}
