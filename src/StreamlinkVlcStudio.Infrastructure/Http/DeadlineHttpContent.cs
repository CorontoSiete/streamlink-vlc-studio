using System.Net;

namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>Keeps a streaming response within the timeout used to request its headers.</summary>
internal sealed class DeadlineHttpContent : HttpContent
{
    private readonly HttpContent content;
    private readonly CancellationTokenSource deadline;

    internal DeadlineHttpContent(HttpContent content, CancellationTokenSource deadline)
    {
        this.content = content;
        this.deadline = deadline;
        foreach (var header in content.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = content.Headers.ContentLength ?? 0;
        return content.Headers.ContentLength.HasValue;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        await content.CopyToAsync(stream, context, linked.Token).ConfigureAwait(false);
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        var stream = await content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        return new DeadlineReadStream(stream, deadline.Token);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
            {
                content.Dispose();
            }
        }
        finally
        {
            if (disposing)
            {
                deadline.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class DeadlineReadStream(Stream stream, CancellationToken deadlineToken) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => stream.Position = value; }
        public override void Flush() => stream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            return await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    stream.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
