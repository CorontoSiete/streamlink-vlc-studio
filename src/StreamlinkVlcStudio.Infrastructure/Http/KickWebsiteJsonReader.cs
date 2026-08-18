using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Limits;
using StreamlinkVlcStudio.Infrastructure.Processes;

namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>
/// Reads bounded Kick website content and falls back to the same bounded curl invocation
/// when Kick rejects the managed HTTP fingerprint. Direct and fallback phases are also exposed
/// for callers whose state machines need to retain the direct HTTP status.
/// </summary>
internal sealed class KickWebsiteJsonReader
{
    private readonly HttpClient httpClient;
    private readonly IAppLogger logger;
    private readonly string logSource;
    private readonly TimeSpan curlTimeout;
    private readonly Func<string, string, CancellationToken, Task<string?>>? curlOverride;

    internal KickWebsiteJsonReader(
        HttpClient httpClient,
        IAppLogger logger,
        string logSource,
        TimeSpan curlTimeout,
        Func<string, string, CancellationToken, Task<string?>>? curlOverride = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.logSource = string.IsNullOrWhiteSpace(logSource) ? "Kick" : logSource.Trim();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(curlTimeout, TimeSpan.Zero);
        this.curlTimeout = curlTimeout;
        this.curlOverride = curlOverride;
    }

    internal async Task<string?> ReadAsync(
        string url,
        string referrer,
        CancellationToken cancellationToken,
        KickWebsitePayloadKind payloadKind = KickWebsitePayloadKind.Json)
    {
        var direct = await ReadDirectAsync(url, referrer, cancellationToken, payloadKind).ConfigureAwait(false);
        if (direct.Body is not null)
        {
            return direct.Body;
        }

        return await ReadFallbackAsync(url, referrer, cancellationToken, payloadKind).ConfigureAwait(false);
    }

    internal async Task<KickWebsiteDirectReadResult> ReadDirectAsync(
        string url,
        string referrer,
        CancellationToken cancellationToken,
        KickWebsitePayloadKind payloadKind = KickWebsitePayloadKind.Json)
    {
        return await TryReadWithHttpClientAsync(url, referrer, payloadKind, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string?> ReadFallbackAsync(
        string url,
        string referrer,
        CancellationToken cancellationToken,
        KickWebsitePayloadKind payloadKind = KickWebsitePayloadKind.Json)
    {
        var body = curlOverride is not null
            ? await curlOverride(url, referrer, cancellationToken).ConfigureAwait(false)
            : await TryReadWithCurlAsync(url, referrer, payloadKind, cancellationToken).ConfigureAwait(false);
        if (!TryNormalizePayload(body, payloadKind, out var normalizedBody))
        {
            if (body is not null)
            {
                logger.Write(AppLogLevel.Warning, logSource, "curl.exe returned blank, invalid, or oversized Kick website content.");
            }

            return null;
        }

        return normalizedBody;
    }

    private async Task<KickWebsiteDirectReadResult> TryReadWithHttpClientAsync(
        string url,
        string referrer,
        KickWebsitePayloadKind payloadKind,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamlinkVlcStudio/0.1");
            request.Headers.Accept.ParseAdd(payloadKind == KickWebsitePayloadKind.Json
                ? "application/json, text/plain, */*"
                : "text/html, application/xhtml+xml, application/xml;q=0.9, */*;q=0.8");
            request.Headers.Referrer = new Uri(referrer);
            using var response = await BoundedHttpResponseSender
                .SendAsync(httpClient, request, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Write(
                    AppLogLevel.Info,
                    logSource,
                    $"Kick website request returned {(int)response.StatusCode} {response.ReasonPhrase}; trying curl fallback.");
                return new KickWebsiteDirectReadResult(null, response.StatusCode);
            }

            var body = await BoundedHttpContentReader
                .ReadJsonAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            if (TryNormalizePayload(body, payloadKind, out var normalizedBody))
            {
                return new KickWebsiteDirectReadResult(normalizedBody, response.StatusCode);
            }

            logger.Write(
                AppLogLevel.Info,
                logSource,
                "Kick website returned blank, invalid, or oversized JSON; trying curl fallback.");
            return new KickWebsiteDirectReadResult(null, response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Write(AppLogLevel.Info, logSource, "Kick website HTTP request timed out; trying curl fallback.");
            return default;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or DecoderFallbackException or InvalidDataException)
        {
            logger.Write(AppLogLevel.Info, logSource, "Kick website HTTP response could not be read; trying curl fallback.", ex);
            return default;
        }
    }

    private async Task<string?> TryReadWithCurlAsync(
        string url,
        string referrer,
        KickWebsitePayloadKind payloadKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var curlPath = KickCurlArguments.ResolveCurlPath();
        try
        {
            var startInfo = BoundedProcessRunner.CreateRedirectedStartInfo(
                curlPath,
                KickCurlArguments.BuildWebsiteRequest(
                    url,
                    referrer,
                    payloadKind == KickWebsitePayloadKind.Json,
                    (int)Math.Ceiling(curlTimeout.TotalSeconds)));
            var result = await new BoundedProcessRunner()
                .RunAsync(startInfo, curlTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (result.TimedOut)
            {
                logger.Write(AppLogLevel.Warning, logSource, "curl.exe timed out loading Kick website JSON.");
                return null;
            }

            if (result.ExitCode != 0 || result.OutputWasTruncated)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    logSource,
                    $"curl.exe failed loading Kick website JSON: {result.StandardError.Trim()}");
                return null;
            }

            return result.StandardOutput;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            logger.Write(AppLogLevel.Warning, logSource, "curl.exe could not start while loading Kick website JSON.", ex);
            return null;
        }
    }

    private static bool TryNormalizePayload(
        string? body,
        KickWebsitePayloadKind payloadKind,
        out string normalizedBody)
    {
        normalizedBody = (body ?? "").Trim();
        var maximumBytes = payloadKind == KickWebsitePayloadKind.Json
            ? PayloadLimits.HttpJsonBytes
            : PayloadLimits.ProcessOutputBytes;
        if (normalizedBody.Length == 0 ||
            Encoding.UTF8.GetByteCount(normalizedBody) > maximumBytes)
        {
            return false;
        }

        if (payloadKind == KickWebsitePayloadKind.Html)
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(normalizedBody);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal enum KickWebsitePayloadKind
{
    Json,
    Html
}

internal readonly record struct KickWebsiteDirectReadResult(string? Body, HttpStatusCode? StatusCode);
