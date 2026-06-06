using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed class KickVodService : IKickVodService
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly TimeSpan CurlTimeout = TimeSpan.FromSeconds(15);
    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly Func<string, string, CancellationToken, Task<string?>> kickCurlJsonReader;

    public KickVodService(IAppLogger logger)
        : this(logger, SharedHttpClient)
    {
    }

    public KickVodService(IAppLogger logger, HttpClient httpClient)
        : this(logger, httpClient, null)
    {
    }

    public KickVodService(
        IAppLogger logger,
        HttpClient httpClient,
        Func<string, string, CancellationToken, Task<string?>>? kickCurlJsonReader)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.kickCurlJsonReader = kickCurlJsonReader ?? TryReadKickJsonWithCurlAsync;
    }

    public async Task<KickVodSearchResult> SearchAsync(
        KickVodSearchRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var channel = NormalizeKickChannel(request.Channel);
        if (string.IsNullOrWhiteSpace(channel))
        {
            return new KickVodSearchResult(
                KickVodSearchStatus.NotFound,
                [],
                "",
                "Enter a Kick channel slug.");
        }

        var url = $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel)}/videos";
        var referrer = $"https://kick.com/{Uri.EscapeDataString(channel)}";
        try
        {
            var body = await TryReadKickJsonWithHttpClientAsync(url, referrer, cancellationToken).ConfigureAwait(false) ??
                await kickCurlJsonReader(url, referrer, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return new KickVodSearchResult(
                    KickVodSearchStatus.Unavailable,
                    [],
                    "",
                    $"Kick VODs unavailable for {channel}.");
            }

            using var document = JsonDocument.Parse(body);
            var videos = ReadVideos(document.RootElement, channel, request.PageSize).ToArray();
            var message = videos.Length switch
            {
                0 => $"No Kick VODs found for {channel}.",
                1 => $"1 Kick VOD found for {channel}.",
                _ => $"{videos.Length} Kick VODs found for {channel}."
            };
            return new KickVodSearchResult(
                KickVodSearchStatus.Available,
                videos,
                "",
                message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "VODs", $"Kick VOD search failed for {channel}.", ex);
            return new KickVodSearchResult(
                KickVodSearchStatus.Unavailable,
                [],
                "",
                $"Kick VODs unavailable for {channel}.");
        }
    }

    private async Task<string?> TryReadKickJsonWithHttpClientAsync(
        string url,
        string referrer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamlinkVlcStudio/0.1");
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        request.Headers.Referrer = new Uri(referrer);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.Write(
                AppLogLevel.Info,
                "VODs",
                $"Kick VOD HTTP request returned {(int)response.StatusCode} {response.ReasonPhrase}; trying curl fallback.");
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryReadKickJsonWithCurlAsync(
        string url,
        string referrer,
        CancellationToken cancellationToken)
    {
        var curlPath = ResolveCurlPath();
        if (string.IsNullOrWhiteSpace(curlPath))
        {
            logger.Write(AppLogLevel.Warning, "VODs", "curl.exe was not found; Kick VOD fallback is unavailable.");
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildKickCurlArguments(url, referrer))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CurlTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            logger.Write(AppLogLevel.Warning, "VODs", "curl.exe timed out loading Kick VODs.");
            return null;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            logger.Write(AppLogLevel.Warning, "VODs", $"curl.exe failed loading Kick VODs: {stderr.Trim()}");
            return null;
        }

        return stdout;
    }

    private static IEnumerable<KickVodItem> ReadVideos(JsonElement root, string channel, int pageSize)
    {
        var videos = EnumerateVideoElements(root)
            .Select(item => TryReadVideo(item, channel))
            .Where(item => item is not null)
            .Select(item => item!)
            .Take(Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 100));

        foreach (var video in videos)
        {
            yield return video;
        }
    }

    private static IEnumerable<JsonElement> EnumerateVideoElements(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }

            yield break;
        }

        foreach (var propertyName in new[] { "data", "videos", "items" })
        {
            if (!root.TryGetProperty(propertyName, out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }

            yield break;
        }
    }

    private static KickVodItem? TryReadVideo(JsonElement item, string channel)
    {
        var source = GetOptionalString(item, "source");
        if (string.IsNullOrWhiteSpace(source))
        {
            source = TryReadNestedString(item, "video", "source");
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var video = item.TryGetProperty("video", out var videoElement) && videoElement.ValueKind == JsonValueKind.Object
            ? videoElement
            : default;
        var uuid = FirstNonEmpty(
            video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "uuid") : "",
            GetOptionalString(item, "uuid"),
            GetOptionalString(item, "slug"),
            GetOptionalString(item, "id"));
        var id = FirstNonEmpty(GetOptionalString(item, "id"), video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "id") : "", uuid);
        var liveStreamId = FirstNonEmpty(
            GetOptionalString(item, "live_stream_id"),
            video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "live_stream_id") : "",
            id);
        var title = FirstNonEmpty(
            GetOptionalString(item, "session_title"),
            GetOptionalString(item, "title"),
            video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "title") : "",
            "Untitled Kick VOD");
        var thumbnail = FirstNonEmpty(
            TryReadNestedString(item, "thumbnail", "src"),
            GetOptionalString(item, "thumbnail"),
            video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "thumb") : "");
        var category = FirstNonEmpty(
            TryReadFirstArrayObjectString(item, "categories", "name"),
            TryReadNestedString(item, "category", "name"));
        var startedAt = TryGetDateTimeOffset(item, "start_time") ??
            TryGetDateTimeOffset(item, "started_at");
        var createdAt = TryGetDateTimeOffset(item, "created_at") ??
            (video.ValueKind == JsonValueKind.Object ? TryGetDateTimeOffset(video, "created_at") : null);
        var duration = ReadDuration(item, "duration");
        var views = TryGetInt32(item, "views") ??
            TryGetInt32(item, "viewer_count") ??
            (video.ValueKind == JsonValueKind.Object ? TryGetInt32(video, "views") : null);
        var channelId = FirstNonEmpty(
            GetOptionalString(item, "channel_id"),
            TryReadNestedString(item, "channel", "id"),
            video.ValueKind == JsonValueKind.Object ? GetOptionalString(video, "channel_id") : "",
            video.ValueKind == JsonValueKind.Object ? TryReadNestedString(video, "channel", "id") : "");

        return new KickVodItem(
            id,
            liveStreamId,
            uuid,
            channel,
            channel,
            title,
            string.IsNullOrWhiteSpace(uuid)
                ? $"https://kick.com/{channel}/videos/{id}"
                : $"https://kick.com/{channel}/videos/{uuid}",
            source,
            thumbnail,
            category,
            createdAt,
            startedAt,
            duration,
            views,
            channelId);
    }

    private static TimeSpan ReadDuration(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return TimeSpan.Zero;
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out var numeric))
        {
            return numeric > TimeSpan.FromDays(7).TotalSeconds
                ? TimeSpan.FromMilliseconds(numeric)
                : TimeSpan.FromSeconds(numeric);
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString()?.Trim() ?? "";
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericText))
            {
                return numericText > TimeSpan.FromDays(7).TotalSeconds
                    ? TimeSpan.FromMilliseconds(numericText)
                    : TimeSpan.FromSeconds(numericText);
            }

            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return TimeSpan.Zero;
    }

    private static string NormalizeKickChannel(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "";
        }

        if (StreamInputParser.TryParsePlatformUrl(trimmed, out var target) &&
            target?.Platform == PlatformKind.Kick)
        {
            return target.Channel.ToLowerInvariant();
        }

        return trimmed.Trim().TrimStart('@').Trim('/').ToLowerInvariant();
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string TryReadNestedString(JsonElement element, string objectPropertyName, string nestedPropertyName)
    {
        return element.TryGetProperty(objectPropertyName, out var nested) &&
            nested.ValueKind == JsonValueKind.Object
            ? GetOptionalString(nested, nestedPropertyName)
            : "";
    }

    private static string TryReadFirstArrayObjectString(JsonElement element, string arrayPropertyName, string propertyName)
    {
        if (!element.TryGetProperty(arrayPropertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = GetOptionalString(item, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "";
    }

    private static string GetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return "";
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? "",
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static string? ResolveCurlPath()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemCurl = string.IsNullOrWhiteSpace(systemRoot)
            ? ""
            : Path.Combine(systemRoot, "System32", "curl.exe");
        if (File.Exists(systemCurl))
        {
            return systemCurl;
        }

        return "curl.exe";
    }

    private static IEnumerable<string> BuildKickCurlArguments(string url, string referrer)
    {
        yield return "-s";
        yield return "-L";
        yield return "--max-time";
        yield return ((int)CurlTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        yield return "-A";
        yield return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        yield return "-H";
        yield return "Accept: application/json, text/plain, */*";
        yield return "-H";
        yield return $"Referer: {referrer}";
        yield return url;
    }

    private static async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }
}
