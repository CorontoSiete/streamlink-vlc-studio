using System.Globalization;
using System.Text;

namespace StreamlinkVlcStudio.Infrastructure.Http;

public sealed record LocalHttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public string GetHeader(string name) =>
        Headers.TryGetValue(name, out var value) ? value : "";

    public string? GetOptionalHeader(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;
}

public sealed record LocalHttpRequestReadResult(
    LocalHttpRequest? Request,
    int StatusCode,
    string ReasonPhrase,
    string Message)
{
    public bool IsSuccess => Request is not null;

    public static LocalHttpRequestReadResult Success(LocalHttpRequest request) =>
        new(request, 200, "OK", "");

    public static LocalHttpRequestReadResult Failure(
        int statusCode,
        string reasonPhrase,
        string message) =>
        new(null, statusCode, reasonPhrase, message);
}

public static class LocalHttpRequestReader
{
    public static async Task<LocalHttpRequest?> ReadAsync(
        Stream stream,
        int maxRequestBytes,
        CancellationToken cancellationToken)
    {
        var result = await ReadWithStatusAsync(stream, maxRequestBytes, cancellationToken).ConfigureAwait(false);
        return result.Request;
    }

    public static async Task<LocalHttpRequestReadResult> ReadWithStatusAsync(
        Stream stream,
        int maxRequestBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRequestBytes, 4);

        var buffer = new byte[maxRequestBytes];
        var totalRead = 0;
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            if (totalRead == buffer.Length)
            {
                return LocalHttpRequestReadResult.Failure(
                    413,
                    "Payload Too Large",
                    "HTTP headers exceed the request size limit.");
            }

            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return LocalHttpRequestReadResult.Failure(400, "Bad Request", "The request ended before its headers were complete.");
            }

            totalRead += read;
            headerEnd = FindHeaderEnd(buffer.AsSpan(0, totalRead));
        }

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var headerLines = headerText.Split("\r\n", StringSplitOptions.None);
        if (headerLines.Length == 0 || headerLines.Any(static line => ContainsInvalidHeaderCharacters(line)))
        {
            return LocalHttpRequestReadResult.Failure(400, "Bad Request", "The request contains malformed HTTP headers.");
        }

        var requestLine = headerLines[0].Split(' ', StringSplitOptions.None);
        if (requestLine.Length != 3 ||
            !IsToken(requestLine[0]) ||
            !IsOriginFormTarget(requestLine[1]) ||
            !IsHttpVersion(requestLine[2]))
        {
            return LocalHttpRequestReadResult.Failure(400, "Bad Request", "The HTTP request line is malformed.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contentLength = 0;
        foreach (var line in headerLines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                return LocalHttpRequestReadResult.Failure(400, "Bad Request", "The request contains a malformed header.");
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!IsToken(name) || !headers.TryAdd(name, value))
            {
                return LocalHttpRequestReadResult.Failure(400, "Bad Request", "The request contains a duplicate or malformed header.");
            }

            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                return LocalHttpRequestReadResult.Failure(
                    501,
                    "Not Implemented",
                    "Transfer-Encoding is not supported; send a Content-Length header.");
            }

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsDecimalInteger(value) ||
                    !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) ||
                    contentLength < 0)
                {
                    return LocalHttpRequestReadResult.Failure(400, "Bad Request", "Content-Length is invalid.");
                }
            }
        }

        var bodyOffset = headerEnd + 4;
        if (contentLength > buffer.Length - bodyOffset)
        {
            return LocalHttpRequestReadResult.Failure(413, "Payload Too Large", "The request body exceeds the request size limit.");
        }

        var requiredBytes = bodyOffset + contentLength;
        if (totalRead > requiredBytes)
        {
            return LocalHttpRequestReadResult.Failure(400, "Bad Request", "The request contains bytes beyond its declared body.");
        }

        while (totalRead < requiredBytes)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, requiredBytes - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return LocalHttpRequestReadResult.Failure(400, "Bad Request", "The request body is incomplete.");
            }

            totalRead += read;
        }

        var target = requestLine[1];
        var path = target.Split('?', 2)[0];
        return LocalHttpRequestReadResult.Success(new LocalHttpRequest(
            requestLine[0],
            path,
            headers,
            buffer.AsSpan(bodyOffset, contentLength).ToArray()));
    }

    private static bool IsHttpVersion(string value) =>
        value.Equals("HTTP/1.0", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase);

    private static bool IsOriginFormTarget(string value) =>
        value.Length > 0 &&
        value[0] == '/' &&
        !value.Contains(' ') &&
        !value.Contains('\\') &&
        !value.Contains('\r') &&
        !value.Contains('\n');

    private static bool IsToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character <= 32 || character >= 127 ||
                character is '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or
                '\\' or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDecimalInteger(string value)
    {
        return value.Length > 0 && value.All(static character => character is >= '0' and <= '9');
    }

    private static bool ContainsInvalidHeaderCharacters(string value)
    {
        return value.Any(static character => character < 32 && character != '\t');
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index <= bytes.Length - 4; index++)
        {
            if (bytes[index] == '\r' &&
                bytes[index + 1] == '\n' &&
                bytes[index + 2] == '\r' &&
                bytes[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }
}
