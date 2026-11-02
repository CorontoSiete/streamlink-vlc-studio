using System.IO;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.App.Wpf.Services;

/// <summary>Owns a browser response through bounded acquisition, reading and late cleanup.</summary>
internal static class WebView2ResponseReader
{
    internal static async Task<byte[]> ReadAsync(
        Task<Stream> content,
        int maximumBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Stream stream;
        try { stream = await content.WaitAsync(deadline.Token); }
        catch
        {
            // WebView2 cannot cancel GetContentAsync. Dispose its eventual stream
            // even when the observer has already timed out or the browser closed.
            _ = DisposeLateContentAsync(content);
            throw;
        }
        using (stream)
        {
            return stream is null ? [] : await BoundedByteReader.ReadOrThrowAsync(stream, maximumBytes, deadline.Token);
        }
    }

    private static async Task DisposeLateContentAsync(Task<Stream> content)
    {
        try { (await content)?.Dispose(); }
        catch (Exception) { /* A failed browser request has no stream to release. */ }
    }
}
