using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class KickEventSubscriptionService : IKickEventSubscriptionService, IAsyncDisposable
{
    private const string SubscriptionsEndpoint = "https://api.kick.com/public/v1/events/subscriptions";
    private const string ChatMessageSentEventName = KickEventNameValidator.ChatMessageSent;
    private const int ChatMessageSentEventVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null
    };

    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly Func<ChatSettings, IAppLogger?, CancellationToken, Task<string?>> appAccessTokenProvider;
    private readonly Func<string, ChatSettings, IAppLogger?, CancellationToken, Task<long?>> broadcasterUserIdResolver;
    private readonly Func<ChatSettings, CancellationToken, Task>? settingsPersister;
    private readonly bool ownsHttpClient;
    private readonly ConcurrentDictionary<string, Lazy<Task<KickEventSubscriptionEnsureResult>>> inFlight =
        new(StringComparer.Ordinal);
    private readonly object lifetimeGate = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private Task? disposalTask;
    private int disposed;

    public KickEventSubscriptionService(
        IAppLogger logger,
        HttpClient? httpClient = null,
        Func<ChatSettings, IAppLogger?, CancellationToken, Task<string?>>? appAccessTokenProvider = null,
        Func<string, ChatSettings, IAppLogger?, CancellationToken, Task<long?>>? broadcasterUserIdResolver = null,
        Func<ChatSettings, CancellationToken, Task>? settingsPersister = null)
    {
        this.logger = logger;
        this.httpClient = httpClient ?? HttpClientFactory.CreateDefault();
        this.appAccessTokenProvider = appAccessTokenProvider ?? KickOAuthService.TryGetAppAccessTokenAsync;
        this.broadcasterUserIdResolver = broadcasterUserIdResolver ?? KickOAuthService.TryResolveBroadcasterUserIdAsync;
        this.settingsPersister = settingsPersister;
        ownsHttpClient = httpClient is null;
    }

    public async Task<KickEventSubscriptionEnsureResult> EnsureChatMessageSentSubscriptionAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string key;
        Lazy<Task<KickEventSubscriptionEnsureResult>> lazy;
        Task<KickEventSubscriptionEnsureResult> operation;
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            key = CreateSingleFlightKey(target, settings);
            lazy = inFlight.GetOrAdd(
                key,
                _ => new Lazy<Task<KickEventSubscriptionEnsureResult>>(
                    () => EnsureChatMessageSentSubscriptionCoreAsync(
                        target,
                        settings,
                        lifetimeCancellation.Token),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            operation = lazy.Value;
        }
        _ = operation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                ((ICollection<KeyValuePair<string, Lazy<Task<KickEventSubscriptionEnsureResult>>>>)inFlight)
                    .Remove(new KeyValuePair<string, Lazy<Task<KickEventSubscriptionEnsureResult>>>(key, lazy));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<KickEventSubscriptionEnsureResult> EnsureChatMessageSentSubscriptionCoreAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        if (target.Platform != PlatformKind.Kick)
        {
            return new KickEventSubscriptionEnsureResult(
                KickEventSubscriptionEnsureStatus.NotNeeded,
                "Kick event subscriptions are only used for Kick channels.");
        }

        var channel = target.Channel.Trim();
        if (string.IsNullOrWhiteSpace(channel))
        {
            return new KickEventSubscriptionEnsureResult(
                KickEventSubscriptionEnsureStatus.MissingBroadcasterUserId,
                "Official Kick chat webhook subscription was not created because the channel is blank.");
        }

        var appToken = await appAccessTokenProvider(settings, logger, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(appToken))
        {
            return new KickEventSubscriptionEnsureResult(
                KickEventSubscriptionEnsureStatus.MissingCredentials,
                "Official Kick chat webhook subscription needs Kick Client ID and Client Secret settings.");
        }

        var broadcasterUserId = TryReadBroadcasterUserId(target.BroadcasterId) ??
            TryReadConfiguredBroadcasterUserId(settings, channel) ??
            await broadcasterUserIdResolver(channel, settings, logger, cancellationToken)
                .ConfigureAwait(false);

        if (broadcasterUserId is null)
        {
            return new KickEventSubscriptionEnsureResult(
                KickEventSubscriptionEnsureStatus.MissingBroadcasterUserId,
                $"Official Kick chat webhook subscription was not created because broadcaster_user_id could not be resolved for {channel}.");
        }

        var existing = await TryGetExistingSubscriptionAsync(appToken, broadcasterUserId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing.SubscriptionId))
        {
            await PersistBroadcasterUserIdAsync(settings, channel, broadcasterUserId.Value, cancellationToken)
                .ConfigureAwait(false);
            return new KickEventSubscriptionEnsureResult(
                KickEventSubscriptionEnsureStatus.AlreadySubscribed,
                $"Official Kick chat webhook subscription already exists for {channel}.",
                existing.SubscriptionId,
                broadcasterUserId);
        }

        if (!existing.IsAvailable)
        {
            return new KickEventSubscriptionEnsureResult(
                KickEventSubscriptionEnsureStatus.Unavailable,
                existing.Message,
                BroadcasterUserId: broadcasterUserId);
        }

        var created = await CreateSubscriptionAsync(appToken, channel, broadcasterUserId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (created.IsSuccess)
        {
            await PersistBroadcasterUserIdAsync(settings, channel, broadcasterUserId.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        return created;
    }

    public ValueTask DisposeAsync()
    {
        lock (lifetimeGate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task[] operations;
        lock (lifetimeGate)
        {
            Interlocked.Exchange(ref disposed, 1);
            lifetimeCancellation.Cancel();
            operations = inFlight.Values
                .Where(lazy => lazy.IsValueCreated)
                .Select(lazy => (Task)lazy.Value)
                .ToArray();
        }

        try
        {
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Every admitted operation has observed the shared shutdown cancellation. Disposal
            // drains those tasks but does not turn their expected cancellation/failure into a
            // second shutdown failure.
        }
        finally
        {
            inFlight.Clear();
            if (ownsHttpClient)
            {
                httpClient.Dispose();
            }

            lifetimeCancellation.Dispose();
        }
    }

    private static string CreateSingleFlightKey(StreamTarget target, ChatSettings settings)
    {
        var channel = target.Channel.Trim().ToLowerInvariant();
        settings.TryGetKickBroadcasterUserId(target.Channel, out var configuredBroadcasterId);
        return OAuthTokenHelpers.CreateCredentialFingerprint(
            ((int)target.Platform).ToString(CultureInfo.InvariantCulture),
            channel,
            target.BroadcasterId.Trim(),
            configuredBroadcasterId?.Trim() ?? "",
            settings.KickClientId.Trim(),
            settings.KickClientSecret.Trim());
    }

    private async Task<ExistingSubscriptionResult> TryGetExistingSubscriptionAsync(
        string appToken,
        long broadcasterUserId,
        CancellationToken cancellationToken)
    {
        var url = $"{SubscriptionsEndpoint}?broadcaster_user_id={broadcasterUserId.ToString(CultureInfo.InvariantCulture)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", KickOAuthService.NormalizeBearerToken(appToken));
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ExistingSubscriptionResult.Unavailable(
                $"Kick event subscription lookup failed ({(int)response.StatusCode} {response.ReasonPhrase}). {ApiErrorMessage.Extract(responseBody)}");
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return ExistingSubscriptionResult.Available("");
            }

            foreach (var item in data.EnumerateArray())
            {
                var eventName = FirstNonEmpty(GetOptionalString(item, "event"), GetOptionalString(item, "name"));
                var version = TryGetInt64(item, "version");
                var method = GetOptionalString(item, "method");
                if (string.Equals(eventName, ChatMessageSentEventName, StringComparison.OrdinalIgnoreCase) &&
                    version == ChatMessageSentEventVersion &&
                    (string.IsNullOrWhiteSpace(method) || string.Equals(method, "webhook", StringComparison.OrdinalIgnoreCase)))
                {
                    return ExistingSubscriptionResult.Available(GetOptionalString(item, "id"));
                }
            }

            return ExistingSubscriptionResult.Available("");
        }
        catch (JsonException)
        {
            return ExistingSubscriptionResult.Unavailable("Kick event subscription lookup returned invalid JSON.");
        }
    }

    private async Task<KickEventSubscriptionEnsureResult> CreateSubscriptionAsync(
        string appToken,
        string channel,
        long broadcasterUserId,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(
            new
            {
                broadcaster_user_id = broadcasterUserId,
                method = "webhook",
                events = new[]
                {
                    new
                    {
                        name = ChatMessageSentEventName,
                        version = ChatMessageSentEventVersion
                    }
                }
            },
            JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, SubscriptionsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", KickOAuthService.NormalizeBearerToken(appToken));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new KickEventSubscriptionEnsureResult(
                KickEventSubscriptionEnsureStatus.Unavailable,
                $"Kick event subscription create failed ({(int)response.StatusCode} {response.ReasonPhrase}). {ApiErrorMessage.Extract(responseBody)}",
                BroadcasterUserId: broadcasterUserId);
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var eventName = FirstNonEmpty(GetOptionalString(item, "name"), GetOptionalString(item, "event"));
                    var version = TryGetInt64(item, "version");
                    if (!string.Equals(eventName, ChatMessageSentEventName, StringComparison.OrdinalIgnoreCase) ||
                        version != ChatMessageSentEventVersion)
                    {
                        continue;
                    }

                    var error = GetOptionalString(item, "error");
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        return new KickEventSubscriptionEnsureResult(
                            KickEventSubscriptionEnsureStatus.Unavailable,
                            $"Kick rejected the chat.message.sent subscription for {channel}: {error}",
                            BroadcasterUserId: broadcasterUserId);
                    }

                    var subscriptionId = GetOptionalString(item, "subscription_id");
                    if (!string.IsNullOrWhiteSpace(subscriptionId))
                    {
                        return new KickEventSubscriptionEnsureResult(
                            KickEventSubscriptionEnsureStatus.Subscribed,
                            $"Official Kick chat webhook subscription created for {channel}.",
                            subscriptionId,
                            broadcasterUserId);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return new KickEventSubscriptionEnsureResult(
                KickEventSubscriptionEnsureStatus.Unavailable,
                "Kick event subscription create returned invalid JSON.",
                BroadcasterUserId: broadcasterUserId);
        }

        return new KickEventSubscriptionEnsureResult(
            KickEventSubscriptionEnsureStatus.Unavailable,
            $"Kick event subscription create did not return a chat.message.sent subscription for {channel}.",
            BroadcasterUserId: broadcasterUserId);
    }

    private static long? TryReadBroadcasterUserId(string value)
    {
        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? parsed
            : null;
    }

    private static long? TryReadConfiguredBroadcasterUserId(ChatSettings settings, string channel)
    {
        return settings.TryGetKickBroadcasterUserId(channel, out var configured)
            ? TryReadBroadcasterUserId(configured)
            : null;
    }

    private async Task PersistBroadcasterUserIdAsync(
        ChatSettings settings,
        string channel,
        long broadcasterUserId,
        CancellationToken cancellationToken)
    {
        var normalizedBroadcasterUserId = broadcasterUserId.ToString(CultureInfo.InvariantCulture);
        var alreadyConfigured = settings.TryGetKickBroadcasterUserId(channel, out var configured) &&
            string.Equals(configured?.Trim(), normalizedBroadcasterUserId, StringComparison.Ordinal);

        settings.SetKickBroadcasterUserId(channel, normalizedBroadcasterUserId);
        if (alreadyConfigured || settingsPersister is null)
        {
            return;
        }

        try
        {
            await settingsPersister(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(
                AppLogLevel.Warning,
                "KickWebhook",
                $"Persisting Kick broadcaster user ID for {channel} failed.",
                ex);
        }
    }

    private sealed record ExistingSubscriptionResult(bool IsAvailable, string SubscriptionId, string Message)
    {
        public static ExistingSubscriptionResult Available(string subscriptionId) => new(true, subscriptionId, "");
        public static ExistingSubscriptionResult Unavailable(string message) => new(false, "", message);
    }
}
