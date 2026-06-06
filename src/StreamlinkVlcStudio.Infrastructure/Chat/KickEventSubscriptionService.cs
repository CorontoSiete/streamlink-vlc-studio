using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class KickEventSubscriptionService : IKickEventSubscriptionService, IDisposable
{
    private const string SubscriptionsEndpoint = "https://api.kick.com/public/v1/events/subscriptions";
    private const string ChatMessageSentEventName = "chat.message.sent";
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

    public KickEventSubscriptionService(
        IAppLogger logger,
        HttpClient? httpClient = null,
        Func<ChatSettings, IAppLogger?, CancellationToken, Task<string?>>? appAccessTokenProvider = null,
        Func<string, ChatSettings, IAppLogger?, CancellationToken, Task<long?>>? broadcasterUserIdResolver = null,
        Func<ChatSettings, CancellationToken, Task>? settingsPersister = null)
    {
        this.logger = logger;
        this.httpClient = httpClient ?? new HttpClient();
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

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private async Task<ExistingSubscriptionResult> TryGetExistingSubscriptionAsync(
        string appToken,
        long broadcasterUserId,
        CancellationToken cancellationToken)
    {
        var url = $"{SubscriptionsEndpoint}?broadcaster_user_id={broadcasterUserId.ToString(CultureInfo.InvariantCulture)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", KickOAuthService.NormalizeBearerToken(appToken));
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ExistingSubscriptionResult.Unavailable(
                $"Kick event subscription lookup failed ({(int)response.StatusCode} {response.ReasonPhrase}). {ExtractApiMessage(responseBody)}");
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

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new KickEventSubscriptionEnsureResult(
                KickEventSubscriptionEnsureStatus.Unavailable,
                $"Kick event subscription create failed ({(int)response.StatusCode} {response.ReasonPhrase}). {ExtractApiMessage(responseBody)}",
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
            ? parsed
            : null;
    }

    private static long? TryReadConfiguredBroadcasterUserId(ChatSettings settings, string channel)
    {
        return settings.KickBroadcasterUserIds.TryGetValue(channel, out var configured)
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
        var alreadyConfigured = settings.KickBroadcasterUserIds.TryGetValue(channel, out var configured) &&
            string.Equals(configured?.Trim(), normalizedBroadcasterUserId, StringComparison.Ordinal);

        settings.KickBroadcasterUserIds[channel] = normalizedBroadcasterUserId;
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

    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return "";
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? ""
            : property.ToString().Trim();
    }

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static string FirstNonEmpty(params string[] values)
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

    private static string ExtractApiMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var message = GetOptionalString(root, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }

            var error = GetOptionalString(root, "error");
            if (!string.IsNullOrWhiteSpace(error))
            {
                return error;
            }
        }
        catch (JsonException)
        {
        }

        return responseBody.Length <= 240 ? responseBody : responseBody[..240];
    }

    private sealed record ExistingSubscriptionResult(bool IsAvailable, string SubscriptionId, string Message)
    {
        public static ExistingSubscriptionResult Available(string subscriptionId) => new(true, subscriptionId, "");
        public static ExistingSubscriptionResult Unavailable(string message) => new(false, "", message);
    }
}
