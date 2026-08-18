namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>
/// Creates the shared infrastructure HTTP clients with consistent timeout and optional headers.
/// </summary>
public static class HttpClientFactory
{
    public const string ApplicationUserAgent = "StreamlinkVlcStudio/0.1";

    public static HttpClient CreateDefault() => Create(
        TimeSpan.FromSeconds(20),
        includeUserAgent: true,
        acceptJson: true);

    public static HttpClient Create(
        TimeSpan timeout,
        bool includeUserAgent = false,
        bool acceptJson = false,
        bool allowAutoRedirect = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            CheckCertificateRevocationList = true
        };
        HttpClient client;
        try
        {
            client = new HttpClient(handler, disposeHandler: true);
        }
        catch
        {
            handler.Dispose();
            throw;
        }

        try
        {
            client.Timeout = timeout;

            if (includeUserAgent)
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ApplicationUserAgent);
            }

            if (acceptJson)
            {
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
