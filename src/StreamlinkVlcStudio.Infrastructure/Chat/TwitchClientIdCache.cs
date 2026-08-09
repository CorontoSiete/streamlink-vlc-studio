using System.Security.Cryptography;
using System.Text;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>
/// Process-wide cache of Twitch Client IDs resolved from OAuth tokens via the validate endpoint.
/// Consolidates the token-fingerprint cache that was previously duplicated across the browse,
/// metadata, viewer-count, and replay services; the search and VOD lookups share it too.
/// </summary>
internal static class TwitchClientIdCache
{
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly TimeSpan FallbackCacheLifetime = TimeSpan.FromHours(6);

    /// <summary>
    /// Returns the Client ID associated with <paramref name="token"/>, resolving it through
    /// <see cref="TwitchOAuthService.ValidateTokenAsync(HttpClient, string, CancellationToken)"/>
    /// and caching it until the token (or a fallback window) expires. Returns null when validation
    /// fails or reports no Client ID; failures are logged under <paramref name="logCategory"/>
    /// with <paramref name="failureMessage"/> and are never cached.
    /// </summary>
    public static async Task<string?> GetOrResolveAsync(
        HttpClient httpClient,
        string token,
        IAppLogger logger,
        string logCategory,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var tokenKey = Fingerprint(token);
        await CacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(tokenKey, out var cached) &&
                cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return cached.ClientId;
            }

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

            var clientId = tokenInfo.ClientId.Trim();
            var fallbackExpiry = DateTimeOffset.UtcNow.Add(FallbackCacheLifetime);
            var expiresAt = tokenInfo.ExpiresAtUtc is { } tokenExpiry && tokenExpiry <= fallbackExpiry
                ? tokenExpiry
                : fallbackExpiry;
            Cache[tokenKey] = new CacheEntry(clientId, expiresAt);
            return clientId;
        }
        finally
        {
            CacheGate.Release();
        }
    }

    private static string Fingerprint(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()))).ToLowerInvariant();
    }

    private sealed record CacheEntry(string ClientId, DateTimeOffset ExpiresAtUtc);
}
