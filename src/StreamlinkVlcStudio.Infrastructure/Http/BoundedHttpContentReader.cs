using System.Text;
using StreamlinkVlcStudio.Infrastructure.Limits;

namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>Reads an HTTP body completely within an explicit byte limit.</summary>
internal static class BoundedHttpContentReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static Task<string> ReadJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken = default) =>
        ReadStringAsync(content, PayloadLimits.HttpJsonBytes, cancellationToken);

    internal static Task<string> ReadPlaylistAsync(
        HttpContent content,
        CancellationToken cancellationToken = default) =>
        ReadStringAsync(content, PayloadLimits.PlaylistBytes, cancellationToken);

    internal static Task<string> ReadRangeProbeAsync(
        HttpContent content,
        CancellationToken cancellationToken = default) =>
        ReadStringAsync(content, PayloadLimits.RangeProbeBytes, cancellationToken);

    internal static async Task<string> ReadStringAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = await BoundedByteReader
            .ReadOrThrowAsync(content, maxBytes, cancellationToken)
            .ConfigureAwait(false);
        var encoding = ResolveEncoding(content);
        var preamble = encoding.GetPreamble();
        var offset = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble)
            ? preamble.Length
            : 0;
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    private static Encoding ResolveEncoding(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(charset))
        {
            return StrictUtf8;
        }

        try
        {
            var encoding = (Encoding)Encoding.GetEncoding(charset).Clone();
            encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            return encoding;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"Unsupported response charset '{charset}'.", ex);
        }
    }
}
