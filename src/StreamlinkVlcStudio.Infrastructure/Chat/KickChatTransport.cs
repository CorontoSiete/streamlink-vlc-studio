using System.Globalization;
using System.Net;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>
/// Shared Kick metadata and recent-chat transport. Cursor ownership, duplicate
/// suppression, and coverage decisions remain in the client/history state
/// machines; this type owns only HTTP/curl framing, bounded timeouts, and JSON
/// normalization.
/// </summary>
internal sealed class KickChatTransport
{
    private static readonly TimeSpan DirectRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CurlRequestTimeout = TimeSpan.FromSeconds(18);
    private readonly IAppLogger logger;
    private readonly KickWebsiteJsonReader websiteReader;

    public KickChatTransport(HttpClient httpClient, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        websiteReader = new KickWebsiteJsonReader(
            httpClient,
            logger,
            "KickChat",
            CurlRequestTimeout);
    }

    /// <summary>
    /// Resolves Kick channel metadata with the direct request first and curl fallback second.
    /// Values from configured settings and either response are merged so one partial response
    /// cannot discard an ID supplied by another source.
    /// </summary>
    public async Task<KickChannelInfo> ResolveChannelInfoAsync(
        string channel,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        string? configuredChatroomId = null;
        if (settings.TryGetKickChatroomId(channel, out var configuredChatroom) &&
            !string.IsNullOrWhiteSpace(configuredChatroom))
        {
            configuredChatroomId = KickChannelInfoJson.NormalizeNumericId(configuredChatroom);
        }

        long? configuredBroadcasterUserId = null;
        if (settings.TryGetKickBroadcasterUserId(channel, out var configuredBroadcaster) &&
            long.TryParse(
                configuredBroadcaster,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedBroadcaster) &&
            parsedBroadcaster > 0)
        {
            configuredBroadcasterUserId = parsedBroadcaster;
        }

        return await ResolveChannelInfoAsync(
            channel,
            configuredChatroomId,
            configuredBroadcasterUserId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<KickChannelInfo> ResolveChannelInfoAsync(
        string channel,
        string? configuredChatroomId,
        long? configuredBroadcasterUserId,
        CancellationToken cancellationToken)
    {
        string? channelId = null;
        var chatroomId = configuredChatroomId;
        var broadcasterUserId = configuredBroadcasterUserId;

        var directMetadata = await ReadChannelInfoDirectAsync(channel, cancellationToken).ConfigureAwait(false);
        if (directMetadata is not null)
        {
            channelId ??= directMetadata.ChannelId;
            chatroomId ??= directMetadata.ChatroomId;
            broadcasterUserId ??= directMetadata.BroadcasterUserId;
        }

        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(chatroomId))
        {
            var curlMetadata = await ReadChannelInfoWithCurlAsync(channel, cancellationToken).ConfigureAwait(false);
            if (curlMetadata is not null)
            {
                channelId ??= curlMetadata.ChannelId;
                chatroomId ??= curlMetadata.ChatroomId;
                broadcasterUserId ??= curlMetadata.BroadcasterUserId;
                logger.Write(AppLogLevel.Info, "KickChat", $"Resolved Kick chatroom ID for {channel} with curl fallback.");
            }
        }

        return new KickChannelInfo(
            KickChannelInfoJson.NormalizeNumericId(channelId),
            KickChannelInfoJson.NormalizeNumericId(chatroomId),
            broadcasterUserId);
    }

    public async Task<KickChannelInfo?> ReadChannelInfoDirectAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DirectRequestTimeout);
        try
        {
            var escapedChannel = Uri.EscapeDataString(channel);
            var result = await websiteReader.ReadDirectAsync(
                    $"https://kick.com/api/v2/channels/{escapedChannel}",
                    $"https://kick.com/{escapedChannel}",
                    timeout.Token)
                .ConfigureAwait(false);
            if (result.Body is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(result.Body);
            return KickChannelInfoJson.Read(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Kick channel metadata timed out for {channel}.");
            return null;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Kick channel metadata failed for {channel}.", ex);
            return null;
        }
    }

    public async Task<KickChannelInfo?> ReadChannelInfoWithCurlAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        var escapedChannel = Uri.EscapeDataString(channel);
        try
        {
            var body = await websiteReader.ReadFallbackAsync(
                    $"https://kick.com/api/v2/channels/{escapedChannel}",
                    $"https://kick.com/{escapedChannel}",
                    cancellationToken)
                .ConfigureAwait(false);
            if (body is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            return KickChannelInfoJson.Read(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "KickChat", $"curl.exe metadata fallback failed for {channel}.", ex);
            return null;
        }
    }

    public async Task<KickTransportPageResult> ReadRecentMessagesDirectAsync(
        string channel,
        string messagesChannelId,
        string? cursor,
        DateTimeOffset? startTimeUtc,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DirectRequestTimeout);
        try
        {
            var escapedChannel = Uri.EscapeDataString(channel);
            var url = KickChatApi.BuildRecentMessagesUrl(
                Uri.EscapeDataString(messagesChannelId),
                cursor,
                startTimeUtc);
            var result = await websiteReader.ReadDirectAsync(
                    url,
                    $"https://kick.com/{escapedChannel}",
                    timeout.Token)
                .ConfigureAwait(false);
            if (result.Body is null)
            {
                return new KickTransportPageResult(
                    null,
                    result.StatusCode == HttpStatusCode.Forbidden);
            }

            using var document = JsonDocument.Parse(result.Body);
            return new KickTransportPageResult(ReadPage(document.RootElement, channel), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Kick recent chat request timed out for {channel}.");
            return new KickTransportPageResult(null, false);
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Kick recent chat request failed for {channel}.", ex);
            return new KickTransportPageResult(null, false);
        }
    }

    public async Task<KickRecentChatPage?> ReadRecentMessagesWithCurlAsync(
        string channel,
        string messagesChannelId,
        string? cursor,
        DateTimeOffset? startTimeUtc,
        CancellationToken cancellationToken)
    {
        var escapedChannel = Uri.EscapeDataString(channel);
        try
        {
            var url = KickChatApi.BuildRecentMessagesUrl(
                Uri.EscapeDataString(messagesChannelId),
                cursor,
                startTimeUtc);
            var body = await websiteReader.ReadFallbackAsync(
                    url,
                    $"https://kick.com/{escapedChannel}",
                    cancellationToken)
                .ConfigureAwait(false);
            if (body is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            return ReadPage(document.RootElement, channel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"curl.exe recent chat fallback failed for {channel}.", ex);
            return null;
        }
    }

    public static IEnumerable<string> BuildMessagesChannelIds(string? channelId, string chatroomId)
    {
        var normalizedChannelId = KickChannelInfoJson.NormalizeNumericId(channelId);
        if (!string.IsNullOrWhiteSpace(normalizedChannelId))
        {
            yield return normalizedChannelId;
        }

        var normalizedChatroomId = KickChannelInfoJson.NormalizeNumericId(chatroomId);
        if (!string.IsNullOrWhiteSpace(normalizedChatroomId) &&
            !string.Equals(normalizedChatroomId, normalizedChannelId, StringComparison.Ordinal))
        {
            yield return normalizedChatroomId;
        }
    }

    public static string? NormalizeCursor(string? cursor) =>
        string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();

    internal static KickRecentChatPage ReadPage(JsonElement root, string channel) =>
        new(
            KickRecentChatJson.ReadMessages(root, channel),
            KickRecentChatJson.ReadCursor(root));
}

internal sealed record KickTransportPageResult(KickRecentChatPage? Page, bool DirectForbidden);
