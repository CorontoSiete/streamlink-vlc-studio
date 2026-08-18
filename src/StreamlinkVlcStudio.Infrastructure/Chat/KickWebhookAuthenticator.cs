using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>
/// Owns Kick webhook public-key rotation, signature validation, timestamp policy,
/// and bounded replay reservations. The TCP server remains a transport façade.
/// </summary>
internal sealed class KickWebhookAuthenticator
{
    private const int MaximumReplayIds = 10_000;
    private const string PublicKeyEndpoint = "https://api.kick.com/public/v1/public-key";
    private static readonly TimeSpan AllowedTimestampSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReplayIdLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PublicKeyLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan PublicKeyRefreshBackoff = TimeSpan.FromSeconds(30);
    private const string FallbackPublicKey = """
-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAq/+l1WnlRrGSolDMA+A8
6rAhMbQGmQ2SapVcGM3zq8ANXjnhDWocMqfWcTd95btDydITa10kDvHzw9WQOqp2
MZI7ZyrfzJuz5nhTPCiJwTwnEtWft7nV14BYRDHvlfqPUaZ+1KR4OCaO/wWIk/rQ
L/TjY0M70gse8rlBkbo2a8rKhu69RQTRsoaf4DVhDPEeSeI5jVrRDGAMGL3cGuyY
6CLKGdjVEM78g3JfYOvDU/RvfqD7L89TZ3iN94jrmWdGz34JNlEI5hqK8dd7C5EF
BEbZ5jgB8s8ReQV8H+MkuffjdAj3ajDDX3DOJMIut1lBrUVD1AaSrGCKHooWoL2e
twIDAQAB
-----END PUBLIC KEY-----
""";

    private readonly HttpClient httpClient;
    private readonly IAppLogger logger;
    private readonly TimeProvider timeProvider;
    private readonly CancellationToken lifetimeToken;
    private readonly object publicKeyGate = new();
    private readonly object replayGate = new();
    private readonly Dictionary<string, ReplayState> replayIds = new(StringComparer.Ordinal);
    private readonly Queue<ReplayIdEntry> replayIdOrder = new();
    private string? publicKeyPem;
    private DateTimeOffset publicKeyExpiresAtUtc;
    private DateTimeOffset publicKeyRefreshNotBeforeUtc;
    private DateTimeOffset forcedPublicKeyRefreshNotBeforeUtc;
    private Task<PublicKeySnapshot>? publicKeyRefreshTask;
    private long publicKeyVersion;
    private long nextReplayReservationId;

    internal KickWebhookAuthenticator(
        HttpClient httpClient,
        IAppLogger logger,
        TimeProvider timeProvider,
        CancellationToken lifetimeToken)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.lifetimeToken = lifetimeToken;
    }

    internal async Task<KickWebhookAuthenticationAttempt> AuthenticateAndReserveAsync(
        LocalHttpRequest request,
        CancellationToken cancellationToken)
    {
        var messageId = request.GetHeader("Kick-Event-Message-Id");
        var timestamp = request.GetHeader("Kick-Event-Message-Timestamp");
        var signature = request.GetHeader("Kick-Event-Signature");
        if (string.IsNullOrWhiteSpace(messageId) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            string.IsNullOrWhiteSpace(signature) ||
            messageId.Length > 256 ||
            timestamp.Length > 64 ||
            signature.Length > 4096 ||
            !TryParseFreshTimestamp(timestamp, out _))
        {
            return KickWebhookAuthenticationAttempt.Invalid;
        }

        var publicKey = await GetPublicKeyAsync(false, null, cancellationToken).ConfigureAwait(false);
        if (!VerifySignature(request, messageId, timestamp, signature, publicKey.Pem))
        {
            publicKey = await GetPublicKeyAsync(true, publicKey.Version, cancellationToken).ConfigureAwait(false);
            if (!VerifySignature(request, messageId, timestamp, signature, publicKey.Pem))
            {
                return KickWebhookAuthenticationAttempt.Invalid;
            }
        }

        return TryReserveMessageId(messageId, out var reservation)
            ? new KickWebhookAuthenticationAttempt(
                KickWebhookChatServer.WebhookAuthenticationResult.Valid,
                reservation)
            : KickWebhookAuthenticationAttempt.Replay;
    }

    internal void Commit(KickWebhookReplayReservation reservation)
    {
        lock (replayGate)
        {
            if (replayIds.TryGetValue(reservation.MessageId, out var state) &&
                state.ReservationId == reservation.ReservationId)
            {
                replayIds[reservation.MessageId] = state with { Committed = true };
            }
        }
    }

    internal void Release(KickWebhookReplayReservation reservation)
    {
        lock (replayGate)
        {
            if (replayIds.TryGetValue(reservation.MessageId, out var state) &&
                state.ReservationId == reservation.ReservationId &&
                !state.Committed)
            {
                replayIds.Remove(reservation.MessageId);
            }
            CompactReplayIdOrderIfNeeded();
        }
    }

    private static bool VerifySignature(
        LocalHttpRequest request,
        string messageId,
        string timestamp,
        string signature,
        string publicKey)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKey);
            var signedPrefix = Encoding.UTF8.GetBytes($"{messageId}.{timestamp}.");
            var signedBody = new byte[signedPrefix.Length + request.Body.Length];
            Buffer.BlockCopy(signedPrefix, 0, signedBody, 0, signedPrefix.Length);
            Buffer.BlockCopy(request.Body, 0, signedBody, signedPrefix.Length, request.Body.Length);
            var signatureBytes = Convert.FromBase64String(signature);
            try
            {
                return rsa.VerifyData(
                    signedBody,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signatureBytes);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private Task<PublicKeySnapshot> GetPublicKeyAsync(
        bool forceRefresh,
        long? observedVersion,
        CancellationToken cancellationToken)
    {
        Task<PublicKeySnapshot> operation;
        lock (publicKeyGate)
        {
            var now = timeProvider.GetUtcNow();
            if (!string.IsNullOrWhiteSpace(publicKeyPem))
            {
                var snapshot = new PublicKeySnapshot(publicKeyPem, publicKeyVersion);
                if (!forceRefresh && (now < publicKeyExpiresAtUtc || now < publicKeyRefreshNotBeforeUtc))
                {
                    return Task.FromResult(snapshot);
                }
                if (forceRefresh &&
                    ((observedVersion is { } version && version != publicKeyVersion) ||
                     now < forcedPublicKeyRefreshNotBeforeUtc))
                {
                    return Task.FromResult(snapshot);
                }
            }

            if (publicKeyRefreshTask is not null)
            {
                operation = publicKeyRefreshTask;
            }
            else
            {
                operation = RefreshPublicKeyAsync(forceRefresh, lifetimeToken);
                publicKeyRefreshTask = operation;
                ObservePublicKeyRefresh(operation);
            }
        }
        return operation.WaitAsync(cancellationToken);
    }

    private async Task<PublicKeySnapshot> RefreshPublicKeyAsync(
        bool forcedRefresh,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PublicKeyEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await BoundedHttpResponseSender
                .SendAsync(httpClient, request, cancellationToken)
                .ConfigureAwait(false);
            var body = await BoundedHttpContentReader
                .ReadJsonAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode &&
                TryReadPublicKey(body, out var parsedPublicKey) &&
                IsValidPublicKey(parsedPublicKey))
            {
                lock (publicKeyGate)
                {
                    publicKeyPem = parsedPublicKey;
                    publicKeyVersion++;
                    publicKeyExpiresAtUtc = now + PublicKeyLifetime;
                    publicKeyRefreshNotBeforeUtc = DateTimeOffset.MinValue;
                    if (forcedRefresh)
                    {
                        forcedPublicKeyRefreshNotBeforeUtc = now + PublicKeyRefreshBackoff;
                    }
                    return new PublicKeySnapshot(publicKeyPem, publicKeyVersion);
                }
            }
            logger.Write(
                AppLogLevel.Warning,
                "KickWebhook",
                $"Kick public key request failed: {(int)response.StatusCode} {response.ReasonPhrase}; retaining the last-known-good key.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(
                AppLogLevel.Warning,
                "KickWebhook",
                "Kick public key request failed; retaining the last-known-good key.",
                ex);
        }

        lock (publicKeyGate)
        {
            if (string.IsNullOrWhiteSpace(publicKeyPem))
            {
                publicKeyPem = FallbackPublicKey;
                publicKeyVersion++;
            }
            publicKeyRefreshNotBeforeUtc = now + PublicKeyRefreshBackoff;
            forcedPublicKeyRefreshNotBeforeUtc = now + PublicKeyRefreshBackoff;
            return new PublicKeySnapshot(publicKeyPem, publicKeyVersion);
        }
    }

    private void ObservePublicKeyRefresh(Task<PublicKeySnapshot> operation)
    {
        _ = operation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (publicKeyGate)
                {
                    if (ReferenceEquals(publicKeyRefreshTask, operation))
                    {
                        publicKeyRefreshTask = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool IsValidPublicKey(string publicKey)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKey);
            return rsa.KeySize >= 2048;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private bool TryParseFreshTimestamp(string value, out DateTimeOffset timestamp)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];
        if (!DateTimeOffset.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp))
        {
            return false;
        }
        var difference = timeProvider.GetUtcNow() - timestamp;
        return difference >= -AllowedTimestampSkew && difference <= AllowedTimestampSkew;
    }

    private bool TryReserveMessageId(string messageId, out KickWebhookReplayReservation reservation)
    {
        var now = timeProvider.GetUtcNow();
        var expiresAt = now + ReplayIdLifetime;
        lock (replayGate)
        {
            RemoveExpiredReplayIds(now);
            if (replayIds.TryGetValue(messageId, out var existing) && existing.ExpiresAtUtc > now)
            {
                reservation = default;
                return false;
            }
            reservation = new KickWebhookReplayReservation(
                messageId,
                Interlocked.Increment(ref nextReplayReservationId));
            replayIds[messageId] = new ReplayState(reservation.ReservationId, expiresAt, false);
            replayIdOrder.Enqueue(new ReplayIdEntry(messageId, reservation.ReservationId, expiresAt));
            while (replayIds.Count > MaximumReplayIds && replayIdOrder.Count > 0)
            {
                RemoveOldestReplayId();
            }
            return true;
        }
    }

    private void CompactReplayIdOrderIfNeeded()
    {
        if (replayIdOrder.Count <= MaximumReplayIds * 2) { return; }
        var liveEntries = replayIdOrder
            .Where(entry =>
                replayIds.TryGetValue(entry.MessageId, out var state) &&
                state.ReservationId == entry.ReservationId &&
                state.ExpiresAtUtc == entry.ExpiresAtUtc)
            .ToArray();
        replayIdOrder.Clear();
        foreach (var entry in liveEntries) { replayIdOrder.Enqueue(entry); }
    }

    private void RemoveExpiredReplayIds(DateTimeOffset now)
    {
        while (replayIdOrder.TryPeek(out var oldest) && oldest.ExpiresAtUtc <= now)
        {
            RemoveOldestReplayId();
        }
    }

    private void RemoveOldestReplayId()
    {
        var oldest = replayIdOrder.Dequeue();
        if (replayIds.TryGetValue(oldest.MessageId, out var state) &&
            state.ReservationId == oldest.ReservationId &&
            state.ExpiresAtUtc == oldest.ExpiresAtUtc)
        {
            replayIds.Remove(oldest.MessageId);
        }
    }

    private static bool TryReadPublicKey(string body, out string publicKey)
    {
        publicKey = "";
        if (string.IsNullOrWhiteSpace(body)) { return false; }
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            publicKey = FirstNonEmpty(
                GetOptionalString(root, "public_key"),
                TryReadNestedString(root, "data", "public_key"));
            return !string.IsNullOrWhiteSpace(publicKey);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct PublicKeySnapshot(string Pem, long Version);
    private readonly record struct ReplayState(long ReservationId, DateTimeOffset ExpiresAtUtc, bool Committed);
    private readonly record struct ReplayIdEntry(string MessageId, long ReservationId, DateTimeOffset ExpiresAtUtc);
}

internal readonly record struct KickWebhookAuthenticationAttempt(
    KickWebhookChatServer.WebhookAuthenticationResult Result,
    KickWebhookReplayReservation Reservation)
{
    internal static KickWebhookAuthenticationAttempt Invalid { get; } =
        new(KickWebhookChatServer.WebhookAuthenticationResult.Invalid, default);
    internal static KickWebhookAuthenticationAttempt Replay { get; } =
        new(KickWebhookChatServer.WebhookAuthenticationResult.Replay, default);
}

internal readonly record struct KickWebhookReplayReservation(string MessageId, long ReservationId);
