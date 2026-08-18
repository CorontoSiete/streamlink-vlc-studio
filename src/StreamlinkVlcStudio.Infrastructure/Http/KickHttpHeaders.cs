namespace StreamlinkVlcStudio.Infrastructure.Http;

internal static class KickHttpHeaders
{
    public static void Configure(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamlinkVlcStudio/0.1");
        }

        if (!httpClient.DefaultRequestHeaders.Accept.Any())
        {
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        }
    }
}
