using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>Coordinates user-token rotation independently of app-token selection.</summary>
internal sealed class KickUserTokenCoordinator(
    Func<ChatSettings, CancellationToken, Task<KickOAuthTokenResult>> refreshAsync)
{
    private const int MaximumCompletedEntries = 128;
    private static readonly TimeSpan RotationRetention = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<string, RefreshEntry> refreshes = new(StringComparer.Ordinal);

    internal static KickUserTokenCoordinator Shared { get; } = new(KickOAuthService.RefreshUserTokenAsync);

    internal async Task<string?> ResolveAsync(
        ChatSettings settings,
        Func<ChatSettings, KickOAuthTokenResult, CancellationToken, Task> applyAsync,
        IAppLogger? logger,
        CancellationToken cancellationToken,
        string? rejectedToken = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credentials = KickCredentialSnapshot.Capture(settings);
        var token = KickOAuthService.NormalizeBearerToken(credentials.AccessToken);
        var forceRefresh = rejectedToken is not null && string.Equals(token,
            KickOAuthService.NormalizeBearerToken(rejectedToken), StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(token) && !forceRefresh && !KickOAuthService.ShouldRefresh(credentials.ToSettings()))
            return token;
        if (string.IsNullOrWhiteSpace(credentials.RefreshToken) || string.IsNullOrWhiteSpace(credentials.ClientId) ||
            string.IsNullOrWhiteSpace(credentials.ClientSecret))
            return rejectedToken is null && token.Length > 0 ? token : null;

        Task<KickOAuthTokenResult?> pending;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in refreshes.Where(pair => pair.Value.Task.IsCompleted &&
                         (!pair.Value.Task.IsCompletedSuccessfully || pair.Value.RetainUntil <= now || pair.Value.Task.Result is null)).Select(pair => pair.Key).ToArray())
                refreshes.Remove(key);
            if (!refreshes.TryGetValue(credentials.CacheKey, out var entry))
            {
                // A waiter's cancellation never cancels rotation for other tabs. The HTTP
                // request has its own deadline; only a private credential snapshot is used.
                pending = RefreshAsync(credentials.ToSettings(), logger);
                refreshes[credentials.CacheKey] = new RefreshEntry(pending, now + RotationRetention);
                foreach (var key in refreshes.Where(pair => pair.Value.Task.IsCompleted)
                             .OrderBy(pair => pair.Value.RetainUntil)
                             .Take(Math.Max(0, refreshes.Count(pair => pair.Value.Task.IsCompleted) - MaximumCompletedEntries))
                             .Select(pair => pair.Key).ToArray())
                    refreshes.Remove(key);
            }
            else pending = entry.Task;
        }

        var refreshed = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (refreshed is null)
            return rejectedToken is null && credentials.Matches(settings) && token.Length > 0 ? token : null;

        // Another surviving waiter may already have published this same rotation.
        var rotated = new KickCredentialSnapshot(refreshed.AccessToken.Trim(),
            string.IsNullOrWhiteSpace(refreshed.RefreshToken) ? credentials.RefreshToken : refreshed.RefreshToken.Trim(),
            credentials.ClientId, credentials.ClientSecret, refreshed.ExpiresAtUtc);
        Task publication;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rotated.Matches(settings)) return KickOAuthService.NormalizeBearerToken(refreshed.AccessToken);
            if (!credentials.Matches(settings)) return null;
            // UI publishers also check the snapshot immediately before their deferred write.
            publication = applyAsync(settings, refreshed, cancellationToken);
        }
        await publication.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return rotated.Matches(settings) ? KickOAuthService.NormalizeBearerToken(refreshed.AccessToken) : null;
    }

    private async Task<KickOAuthTokenResult?> RefreshAsync(ChatSettings snapshot, IAppLogger? logger)
    {
        try
        {
            var result = await refreshAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            logger.WriteSafely(AppLogLevel.Info, "KickOAuth", "Refreshed Kick OAuth token.");
            return result;
        }
        catch (Exception ex)
        {
            logger.WriteSafely(AppLogLevel.Warning, "KickOAuth", "Kick OAuth token refresh failed.", ex);
            return null;
        }
    }

    private sealed record RefreshEntry(Task<KickOAuthTokenResult?> Task, DateTimeOffset RetainUntil);
}
