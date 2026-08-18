using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

internal interface IKickTokenProvider
{
    Task<string?> ResolveAsync(
        ChatSettings settings,
        IAppLogger logger,
        CancellationToken cancellationToken = default);
}

/// <summary>Shares Kick token acquisition without allowing one caller to cancel every waiter.</summary>
internal sealed class KickTokenProvider : IKickTokenProvider
{
    private const int MaximumCacheEntries = 128;
    private static readonly TimeSpan SuccessfulCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedCacheLifetime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    private readonly object gate = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string?>> inFlight = new(StringComparer.Ordinal);
    private readonly Func<ChatSettings, IAppLogger, CancellationToken, Task<string?>> resolveAsync;

    internal static KickTokenProvider Shared { get; } = new();

    internal int InFlightCountForTest
    {
        get
        {
            lock (gate)
            {
                return inFlight.Count;
            }
        }
    }

    internal KickTokenProvider()
        : this(static (settings, logger, cancellationToken) =>
            KickAccessTokenResolver.ResolveAsync(settings, logger, cancellationToken))
    {
    }

    internal KickTokenProvider(
        Func<ChatSettings, IAppLogger, CancellationToken, Task<string?>> resolveAsync)
    {
        this.resolveAsync = resolveAsync ?? throw new ArgumentNullException(nameof(resolveAsync));
    }

    public async Task<string?> ResolveAsync(
        ChatSettings settings,
        IAppLogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        var key = CreateCacheKey(settings);
        Task<string?> operation;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
            {
                cache[key] = cached with { LastAccessUtc = now };
                return cached.Token;
            }

            cache.Remove(key);
            if (!inFlight.TryGetValue(key, out operation!))
            {
                operation = ResolveAndCacheAsync(settings, logger, key);
                inFlight[key] = operation;
                RemoveInFlightWhenCompleted(key, operation);
            }
        }

        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveAndCacheAsync(
        ChatSettings settings,
        IAppLogger logger,
        string originalKey)
    {
        string? token;
        try
        {
            token = await resolveAsync(settings, logger, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "KickOAuth", "Kick access-token resolution failed.", ex);
            token = null;
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = GetCacheExpiry(settings, token, now);
        lock (gate)
        {
            var currentKey = CreateCacheKey(settings);
            if (expiresAt > now)
            {
                var entry = new CacheEntry(token, expiresAt, now);
                cache[originalKey] = entry;
                cache[currentKey] = entry;
            }
            else
            {
                cache.Remove(originalKey);
                cache.Remove(currentKey);
            }

            TrimCacheLocked();
        }

        return token;
    }

    private static DateTimeOffset GetCacheExpiry(
        ChatSettings settings,
        string? token,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return now.Add(FailedCacheLifetime);
        }

        var maximum = now.Add(SuccessfulCacheLifetime);
        if (settings.KickTokenExpiresAtUtc is not { } tokenExpiry)
        {
            return maximum;
        }

        DateTimeOffset safeTokenExpiry;
        try
        {
            safeTokenExpiry = tokenExpiry.Subtract(ExpirySkew);
        }
        catch (ArgumentOutOfRangeException)
        {
            return now;
        }

        if (safeTokenExpiry <= now)
        {
            return now;
        }

        return safeTokenExpiry < maximum ? safeTokenExpiry : maximum;
    }

    private void RemoveInFlightWhenCompleted(string key, Task<string?> operation)
    {
        _ = operation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (gate)
                {
                    if (inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, operation))
                    {
                        inFlight.Remove(key);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void TrimCacheLocked()
    {
        if (cache.Count <= MaximumCacheEntries)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var key in cache
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            cache.Remove(key);
        }

        foreach (var key in cache
                     .OrderBy(pair => pair.Value.LastAccessUtc)
                     .Take(Math.Max(0, cache.Count - MaximumCacheEntries))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            cache.Remove(key);
        }
    }

    private static string CreateCacheKey(ChatSettings settings)
    {
        return OAuthTokenHelpers.CreateCredentialFingerprint(
            settings.KickOAuthToken.Trim(),
            settings.KickRefreshToken.Trim(),
            settings.KickClientId.Trim(),
            settings.KickClientSecret.Trim(),
            settings.KickTokenExpiresAtUtc?.UtcTicks.ToString() ?? "");
    }

    private sealed record CacheEntry(
        string? Token,
        DateTimeOffset ExpiresAtUtc,
        DateTimeOffset LastAccessUtc);
}
