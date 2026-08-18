using System.Globalization;

namespace StreamlinkVlcStudio.Infrastructure.Http;

public static class KickCurlArguments
{
    private const string JsonUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";

    public static string ResolveCurlPath()
    {
        var configured = Environment.GetEnvironmentVariable("STREAMLINK_KICK_CURL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemCurl = string.IsNullOrWhiteSpace(systemRoot)
            ? ""
            : Path.Combine(systemRoot, "System32", "curl.exe");
        return File.Exists(systemCurl) ? systemCurl : "curl.exe";
    }

    public static IEnumerable<string> BuildJsonRequest(
        string url,
        string referrer,
        int maxTimeSeconds = 15)
    {
        return BuildWebsiteRequest(url, referrer, expectsJson: true, maxTimeSeconds: maxTimeSeconds);
    }

    public static IEnumerable<string> BuildWebsiteRequest(
        string url,
        string referrer,
        bool expectsJson,
        int maxTimeSeconds = 15)
    {
        yield return "--location";
        yield return "--silent";
        yield return "--show-error";
        yield return "--fail";
        yield return "--compressed";
        yield return "--max-time";
        yield return maxTimeSeconds.ToString(CultureInfo.InvariantCulture);
        yield return "--user-agent";
        yield return JsonUserAgent;
        yield return "--header";
        yield return expectsJson
            ? "Accept: application/json,text/plain,*/*"
            : "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
        yield return "--header";
        yield return "Accept-Language: *";
        yield return "--referer";
        yield return referrer;
        yield return url;
    }

}
