using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

/// <summary>
/// Coordinates Twitch's process-wide browse rate-limit window and bounded retries.
/// </summary>
internal sealed class TwitchRateLimitCoordinator
{
    private static readonly object Gate = new();
    private static readonly TimeSpan RetryFallback = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(1);
    private static DateTimeOffset pauseUntilUtc = DateTimeOffset.MinValue;

    internal async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        string url,
        string token,
        string clientId,
        IAppLogger logger,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await WaitForPauseAsync(cancellationToken).ConfigureAwait(false);

            using var request = CreateRequest(url, token, clientId);
            var response = await BoundedHttpResponseSender
                .SendAsync(httpClient, request, cancellationToken)
                .ConfigureAwait(false);
            ObserveHeaders(response);

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 2)
            {
                return response;
            }

            var retryDelay = ClampDelay(GetRetryDelay(response) ?? RetryFallback);
            SetPause(SaturatingAdd(DateTimeOffset.UtcNow, retryDelay));
            logger.Write(
                AppLogLevel.Warning,
                "Browse",
                $"Twitch browse request was rate limited; retrying in {retryDelay.TotalSeconds:0.#}s.");
            response.Dispose();
            if (retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static TimeSpan ClampDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > MaximumDelay ? MaximumDelay : delay;
    }

    internal static DateTimeOffset SaturatingAdd(DateTimeOffset value, TimeSpan delta)
    {
        try
        {
            return value.Add(delta);
        }
        catch (ArgumentOutOfRangeException)
        {
            return delta < TimeSpan.Zero ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue;
        }
    }

    private static HttpRequestMessage CreateRequest(string url, string token, string clientId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);
        return request;
    }

    private static async Task WaitForPauseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (Gate)
            {
                delay = pauseUntilUtc - DateTimeOffset.UtcNow;
            }

            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ObserveHeaders(HttpResponseMessage response)
    {
        if (!TryGetHeaderInt32(response, "Ratelimit-Remaining", out var remaining) ||
            remaining > 1 ||
            !TryGetResetUtc(response, out var resetUtc))
        {
            return;
        }

        SetPause(SaturatingAdd(resetUtc, TimeSpan.FromMilliseconds(500)));
    }

    private static TimeSpan? GetRetryDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return ClampDelay(delta);
        }

        if (response.Headers.RetryAfter?.Date is { } retryAt)
        {
            return ClampDelay(retryAt.ToUniversalTime() - DateTimeOffset.UtcNow);
        }

        return TryGetResetUtc(response, out var resetUtc)
            ? ClampDelay(SaturatingAdd(resetUtc, TimeSpan.FromMilliseconds(500)) - DateTimeOffset.UtcNow)
            : null;
    }

    private static bool TryGetResetUtc(HttpResponseMessage response, out DateTimeOffset resetUtc)
    {
        resetUtc = default;
        if (!TryGetHeaderInt64(response, "Ratelimit-Reset", out var resetUnixSeconds))
        {
            return false;
        }

        try
        {
            resetUtc = DateTimeOffset.FromUnixTimeSeconds(resetUnixSeconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryGetHeaderInt32(HttpResponseMessage response, string name, out int value)
    {
        value = 0;
        if (!TryGetHeaderInt64(response, name, out var longValue) ||
            longValue < int.MinValue ||
            longValue > int.MaxValue)
        {
            return false;
        }

        value = (int)longValue;
        return true;
    }

    private static bool TryGetHeaderInt64(HttpResponseMessage response, string name, out long value)
    {
        value = 0;
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return false;
        }

        return long.TryParse(
            values.FirstOrDefault(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static void SetPause(DateTimeOffset candidate)
    {
        var now = DateTimeOffset.UtcNow;
        if (candidate <= now)
        {
            return;
        }

        var maximum = SaturatingAdd(now, MaximumDelay);
        if (candidate > maximum)
        {
            candidate = maximum;
        }

        lock (Gate)
        {
            if (candidate > pauseUntilUtc)
            {
                pauseUntilUtc = candidate;
            }
        }
    }
}
