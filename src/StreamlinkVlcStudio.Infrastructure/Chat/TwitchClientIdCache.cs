using System.Collections.Concurrent;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>
/// Bounded, per-token-hash cache with single-flight validation for Twitch credentials.
/// </summary>
internal static class TwitchClientIdCache
{
    private const int MaximumEntries = 128;
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry?>>> InFlight = new(StringComparer.Ordinal);
    private static readonly object evictionGate = new();
    private static readonly TimeSpan FallbackCacheLifetime = TimeSpan.FromHours(6);

    public static async Task<string?> GetOrResolveAsync(
        HttpClient httpClient,
        string token,
        IAppLogger logger,
        string logCategory,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var normalizedToken = token.Trim();
        if (normalizedToken.Length == 0)
        {
            return null;
        }

        var tokenKey = OAuthTokenHelpers.CreateCredentialFingerprint(normalizedToken);
        if (TryGetValid(tokenKey, out var cached))
        {
            return cached.ClientId;
        }

        var lazy = InFlight.GetOrAdd(
            tokenKey,
            _ => new Lazy<Task<CacheEntry?>>(
                () => ResolveCoreAsync(
                    httpClient,
                    normalizedToken,
                    logger,
                    logCategory,
                    failureMessage,
                    CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        var operation = lazy.Value;
        _ = operation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                ((ICollection<KeyValuePair<string, Lazy<Task<CacheEntry?>>>>)InFlight)
                    .Remove(new KeyValuePair<string, Lazy<Task<CacheEntry?>>>(tokenKey, lazy));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var resolved = await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        return resolved.ClientId;
    }

    private static async Task<CacheEntry?> ResolveCoreAsync(
        HttpClient httpClient,
        string token,
        IAppLogger logger,
        string logCategory,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        TwitchTokenInfo tokenInfo;
        try
        {
            tokenInfo = await TwitchOAuthService.ValidateTokenAsync(httpClient, token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, logCategory, failureMessage, ex);
            return null;
        }

        if (string.IsNullOrWhiteSpace(tokenInfo.ClientId))
        {
            return null;
        }

        var fallbackExpiry = DateTimeOffset.UtcNow.Add(FallbackCacheLifetime);
        var expiresAt = tokenInfo.ExpiresAtUtc is { } tokenExpiry && tokenExpiry <= fallbackExpiry
            ? tokenExpiry
            : fallbackExpiry;
        var resolved = new CacheEntry(tokenInfo.ClientId.Trim(), expiresAt, DateTimeOffset.UtcNow);
        Cache[OAuthTokenHelpers.CreateCredentialFingerprint(token)] = resolved;
        TrimCache();
        return resolved;
    }

    private static bool TryGetValid(string tokenKey, out CacheEntry entry)
    {
        if (Cache.TryGetValue(tokenKey, out entry!) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            return true;
        }

        Cache.TryRemove(tokenKey, out _);
        entry = null!;
        return false;
    }

    private static void TrimCache()
    {
        if (Cache.Count <= MaximumEntries)
        {
            return;
        }

        lock (evictionGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in Cache.Where(pair => pair.Value.ExpiresAtUtc <= now))
            {
                Cache.TryRemove(pair.Key, out _);
            }
            foreach (var pair in Cache
                         .OrderBy(pair => pair.Value.CreatedAtUtc)
                         .Take(Math.Max(0, Cache.Count - MaximumEntries)))
            {
                Cache.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record CacheEntry(string ClientId, DateTimeOffset ExpiresAtUtc, DateTimeOffset CreatedAtUtc);
}
