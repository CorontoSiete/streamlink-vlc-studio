using System.Net;
using System.Security.Cryptography;
using System.Text;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

/// <summary>Shares the raw live-channel response used by viewer-count and metadata consumers.</summary>
internal sealed class LiveChannelSnapshotProvider
{
    private const int MaximumCacheEntries = 256;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(
        TimeSpan.FromSeconds(12),
        includeUserAgent: true,
        acceptJson: true);

    private readonly object gate = new();
    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<SnapshotKey, CacheEntry> cache = [];

    internal static LiveChannelSnapshotProvider Shared { get; } = new(SharedHttpClient);

    internal LiveChannelSnapshotProvider(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal HttpClient HttpClientForCredentialValidation => httpClient;

    internal Task<LiveChannelSnapshotResponse> GetTwitchAsync(
        string channel,
        string token,
        string clientId,
        CancellationToken cancellationToken)
    {
        return GetAsync(
            new SnapshotKey(
                PlatformKind.Twitch,
                channel.Trim().ToLowerInvariant(),
                Fingerprint($"{token}\0{clientId}")),
            () => LiveChannelRequestFactory.CreateTwitchStreamsRequest(channel, token, clientId),
            cancellationToken);
    }

    internal Task<LiveChannelSnapshotResponse> GetKickAsync(
        string channel,
        string accessToken,
        CancellationToken cancellationToken)
    {
        return GetAsync(
            new SnapshotKey(
                PlatformKind.Kick,
                channel.Trim().ToLowerInvariant(),
                Fingerprint(accessToken)),
            () => LiveChannelRequestFactory.CreateKickChannelsRequest(channel, accessToken),
            cancellationToken);
    }

    private async Task<LiveChannelSnapshotResponse> GetAsync(
        SnapshotKey key,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        Task<LiveChannelSnapshotResponse> operation;
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            if (cache.TryGetValue(key, out var existing) &&
                (!existing.Operation.IsCompleted ||
                    (existing.Operation.IsCompletedSuccessfully && existing.ExpiresAtUtc > now)))
            {
                existing.LastAccessUtc = now;
                operation = existing.Operation;
            }
            else
            {
                operation = LoadAsync(requestFactory);
                var entry = new CacheEntry(operation, DateTimeOffset.MaxValue, now);
                cache[key] = entry;
                _ = operation.ContinueWith(
                    completed => MarkCompleted(key, entry, completed),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                TrimCacheLocked();
            }
        }

        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void MarkCompleted(
        SnapshotKey key,
        CacheEntry entry,
        Task<LiveChannelSnapshotResponse> operation)
    {
        if (operation.IsFaulted)
        {
            // The cache entry can be evicted while its shared request is still running and
            // every waiter can independently cancel. Observe the terminal exception even in
            // that case so it cannot surface later as an unobserved task fault.
            _ = operation.Exception;
        }

        lock (gate)
        {
            if (!cache.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            if (!operation.IsCompletedSuccessfully)
            {
                cache.Remove(key);
                return;
            }

            entry.ExpiresAtUtc = timeProvider.GetUtcNow().Add(CacheLifetime);
        }
    }

    private async Task<LiveChannelSnapshotResponse> LoadAsync(Func<HttpRequestMessage> requestFactory)
    {
        using var request = requestFactory();
        using var response = await BoundedHttpResponseSender
            .SendAsync(httpClient, request, CancellationToken.None)
            .ConfigureAwait(false);
        var body = await BoundedHttpContentReader
            .ReadJsonAsync(response.Content, CancellationToken.None)
            .ConfigureAwait(false);
        return new LiveChannelSnapshotResponse(response.StatusCode, response.ReasonPhrase, body);
    }

    private void TrimCacheLocked()
    {
        if (cache.Count <= MaximumCacheEntries)
        {
            return;
        }

        foreach (var key in cache
                     .OrderBy(pair => pair.Value.LastAccessUtc)
                     .Take(cache.Count - MaximumCacheEntries)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            cache.Remove(key);
        }
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private readonly record struct SnapshotKey(
        PlatformKind Platform,
        string Channel,
        string CredentialFingerprint);

    private sealed class CacheEntry(
        Task<LiveChannelSnapshotResponse> operation,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset lastAccessUtc)
    {
        internal Task<LiveChannelSnapshotResponse> Operation { get; } = operation;
        internal DateTimeOffset ExpiresAtUtc { get; set; } = expiresAtUtc;
        internal DateTimeOffset LastAccessUtc { get; set; } = lastAccessUtc;
    }
}

internal sealed record LiveChannelSnapshotResponse(
    HttpStatusCode StatusCode,
    string? ReasonPhrase,
    string Body)
{
    internal bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}
