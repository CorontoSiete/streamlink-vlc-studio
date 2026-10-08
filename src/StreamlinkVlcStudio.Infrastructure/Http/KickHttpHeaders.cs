namespace StreamlinkVlcStudio.Infrastructure.Http;

internal static class KickHttpHeaders
{
    internal static bool IsKickHost(Uri uri) =>
        string.Equals(uri.Host, "kick.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".kick.com", StringComparison.OrdinalIgnoreCase);

    public static void Configure(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamStudio/0.1");
        }

        if (!httpClient.DefaultRequestHeaders.Accept.Any())
        {
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        }
    }
}
