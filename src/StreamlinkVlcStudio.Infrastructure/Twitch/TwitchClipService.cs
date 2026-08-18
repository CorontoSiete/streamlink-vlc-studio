using System.Net.Http.Headers;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

public sealed class TwitchClipService : ITwitchClipService
{
    private const string HelixBaseUrl = "https://api.twitch.tv/helix";
    private const int DefaultClipDurationSeconds = 30;
    private const int DefaultReadinessPollAttempts = 12;
    private static readonly TimeSpan DefaultReadinessPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumReadinessWindow = TimeSpan.FromSeconds(60);
    private static readonly HttpClient SharedHttpClient = HttpClientFactory.Create(TimeSpan.FromSeconds(20));

    private readonly HttpClient httpClient;
    private readonly TimeSpan readinessPollInterval;
    private readonly int readinessPollAttempts;

    public TwitchClipService()
        : this(SharedHttpClient)
    {
    }

    public TwitchClipService(
        HttpClient httpClient,
        TimeSpan? readinessPollInterval = null,
        int readinessPollAttempts = DefaultReadinessPollAttempts)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (readinessPollAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readinessPollAttempts));
        }

        var normalizedPollInterval = readinessPollInterval ?? DefaultReadinessPollInterval;
        if (normalizedPollInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readinessPollInterval));
        }

        this.httpClient = httpClient;
        this.readinessPollInterval = normalizedPollInterval;
        this.readinessPollAttempts = readinessPollAttempts;
    }

    public async Task<TwitchClipResult> CreateLiveClipAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);

        if (target.Platform != PlatformKind.Twitch || target.Kind != StreamTargetKind.Live)
        {
            throw new InvalidOperationException("Twitch clips can only be created from a live Twitch tab.");
        }

        var accessToken = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "Twitch authorization is required to create clips. Re-authorize Twitch in Settings > Accounts.");
        }

        var tokenInfo = await TwitchOAuthService.ValidateTokenAsync(
            httpClient,
            accessToken,
            cancellationToken).ConfigureAwait(false);
        if (!tokenInfo.CanCreateClips)
        {
            throw new InvalidOperationException(
                "Twitch clip creation requires the clips:edit scope. Re-authorize Twitch in Settings > Accounts.");
        }

        var clientId = tokenInfo.ClientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Twitch token validation did not return a Client ID.");
        }

        var broadcasterId = await ResolveBroadcasterIdAsync(
            target,
            accessToken,
            clientId,
            cancellationToken).ConfigureAwait(false);
        var clipId = await StartClipAsync(
            broadcasterId,
            accessToken,
            clientId,
            cancellationToken).ConfigureAwait(false);
        var clipUri = await WaitForPublishedClipAsync(
            clipId,
            accessToken,
            clientId,
            cancellationToken).ConfigureAwait(false);

        return new TwitchClipResult(clipId, clipUri);
    }

    private async Task<string> ResolveBroadcasterIdAsync(
        StreamTarget target,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken)
    {
        var configuredBroadcasterId = target.BroadcasterId.Trim();
        if (!string.IsNullOrWhiteSpace(configuredBroadcasterId))
        {
            return configuredBroadcasterId;
        }

        var channel = target.Channel.Trim();
        if (string.IsNullOrWhiteSpace(channel))
        {
            throw new InvalidOperationException("The selected Twitch tab does not have a channel name.");
        }

        using var request = CreateRequest(
            HttpMethod.Get,
            $"{HelixBaseUrl}/users?login={Uri.EscapeDataString(channel)}",
            accessToken,
            clientId);
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        EnsureSuccess("Resolve Twitch broadcaster", response, responseBody);

        using var document = ParseJson("Resolve Twitch broadcaster", responseBody);
        if (!TryGetFirstDataItem(document.RootElement, out var broadcaster) ||
            !TryGetString(broadcaster, "id", out var broadcasterId) ||
            string.IsNullOrWhiteSpace(broadcasterId))
        {
            throw new InvalidOperationException($"Twitch channel '{channel}' could not be resolved.");
        }

        return broadcasterId.Trim();
    }

    private async Task<string> StartClipAsync(
        string broadcasterId,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken)
    {
        var url = $"{HelixBaseUrl}/clips?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&duration={DefaultClipDurationSeconds}";
        using var request = CreateRequest(HttpMethod.Post, url, accessToken, clientId);
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        EnsureSuccess("Create Twitch clip", response, responseBody);

        using var document = ParseJson("Create Twitch clip", responseBody);
        if (!TryGetFirstDataItem(document.RootElement, out var clip) ||
            !TryGetString(clip, "id", out var clipId) ||
            string.IsNullOrWhiteSpace(clipId))
        {
            throw new InvalidOperationException("Create Twitch clip response did not include a clip ID.");
        }

        return clipId.Trim();
    }

    private async Task<Uri> WaitForPublishedClipAsync(
        string clipId,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken)
    {
        using var readinessTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readinessTimeout.CancelAfter(MaximumReadinessWindow);
        try
        {
            for (var attempt = 0; attempt < readinessPollAttempts; attempt++)
            {
                var clipUri = await TryGetPublishedClipUriAsync(
                    clipId,
                    accessToken,
                    clientId,
                    readinessTimeout.Token).ConfigureAwait(false);
                if (clipUri is not null)
                {
                    return clipUri;
                }

                if (attempt + 1 < readinessPollAttempts)
                {
                    await Task.Delay(readinessPollInterval, readinessTimeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateReadinessTimeoutException();
        }

        throw CreateReadinessTimeoutException();
    }

    private TimeoutException CreateReadinessTimeoutException() => new(
        $"Twitch created the clip, but it was not ready to open after {DescribeReadinessPollingWindow()} (maximum {MaximumReadinessWindow.TotalSeconds:N0} seconds).");

    private string DescribeReadinessPollingWindow()
    {
        var intervalDescription = readinessPollInterval == TimeSpan.Zero
            ? "without a delay"
            : $"with {readinessPollInterval:g} between checks";
        return $"{readinessPollAttempts} readiness checks ({intervalDescription})";
    }

    private async Task<Uri?> TryGetPublishedClipUriAsync(
        string clipId,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"{HelixBaseUrl}/clips?id={Uri.EscapeDataString(clipId)}",
            accessToken,
            clientId);
        using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken).ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
        EnsureSuccess("Check Twitch clip readiness", response, responseBody);

        using var document = ParseJson("Check Twitch clip readiness", responseBody);
        if (!TryGetFirstDataItem(document.RootElement, out var clip))
        {
            return null;
        }

        if (!TryGetString(clip, "url", out var clipUrl) || string.IsNullOrWhiteSpace(clipUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(clipUrl.Trim(), UriKind.Absolute, out var clipUri) ||
            clipUri.Scheme != Uri.UriSchemeHttps ||
            !clipUri.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Twitch clip response contained an invalid public clip URL.");
        }

        return clipUri;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        string accessToken,
        string clientId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);
        return request;
    }

    private static JsonDocument ParseJson(string operation, string responseBody)
    {
        try
        {
            return JsonDocument.Parse(responseBody);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{operation} returned invalid JSON.", ex);
        }
    }

    private static void EnsureSuccess(
        string operation,
        HttpResponseMessage response,
        string responseBody)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = ApiErrorMessage.Extract(responseBody).Trim();
        var detail = string.IsNullOrWhiteSpace(message)
            ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
            : $"{(int)response.StatusCode} {response.ReasonPhrase}. {message}";
        throw new InvalidOperationException($"{operation} failed: {detail}");
    }

    private static bool TryGetFirstDataItem(JsonElement root, out JsonElement item)
    {
        item = default;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Twitch response did not include a data array.");
        }

        foreach (var candidate in data.EnumerateArray())
        {
            if (candidate.ValueKind == JsonValueKind.Object)
            {
                item = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return true;
    }

}
