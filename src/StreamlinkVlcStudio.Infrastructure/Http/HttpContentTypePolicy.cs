namespace StreamlinkVlcStudio.Infrastructure.Http;

internal static class HttpContentTypePolicy
{
    internal static bool IsErrorDocument(HttpContent content)
    {
        var mediaType = content.Headers.ContentType?.MediaType;
        return string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true;
    }
}
