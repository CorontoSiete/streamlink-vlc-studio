using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class KickChatHistoryProvider : IKickChatHistoryProvider, IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly KickChatHistoryBackfillService backfillService;

    public KickChatHistoryProvider(IAppLogger logger, HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
        ownsHttpClient = httpClient is null;
        ConfigureKickHttpHeaders(this.httpClient);
        backfillService = new KickChatHistoryBackfillService(this.httpClient, logger);
    }

    public async Task<ChatHistoryBackfillResult> BackfillRecentChatRangeAsync(
        StreamTarget target,
        ChatSettings settings,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken = default)
    {
        if (target.Platform != PlatformKind.Kick)
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        var channelInfo = await backfillService
            .ResolveChannelInfoAsync(target.Channel, settings, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(channelInfo.ChatroomId))
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        return await backfillService
            .BackfillRecentChatFromStartTimeAsync(
                target.Channel,
                channelInfo.ChannelId,
                channelInfo.ChatroomId,
                fromTimestampUtc,
                throughTimestampUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        backfillService.Dispose();
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private static void ConfigureKickHttpHeaders(HttpClient httpClient)
    {
        if (!httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamlinkVlcStudio/0.1");
        }

        if (!httpClient.DefaultRequestHeaders.Accept.Any())
        {
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        }
    }
}

internal sealed class KickChatHistoryBackfillService : IDisposable
{
    private const int KickRecentChatSeekBackfillLimit = 2_500;
    private static readonly TimeSpan KickRecentChatBackfillTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient httpClient;
    private readonly IAppLogger logger;
    private readonly SemaphoreSlim backfillGate = new(1, 1);
    private bool directBackfillBlocked;

    public KickChatHistoryBackfillService(HttpClient httpClient, IAppLogger logger)
    {
        this.httpClient = httpClient;
        this.logger = logger;
    }

    public async Task<KickChannelInfo> ResolveChannelInfoAsync(
        string channel,
        ChatSettings settings,
        CancellationToken cancellationToken)
    {
        string? channelId = null;
        string? chatroomId = null;
        long? broadcasterUserId = null;

        if (settings.KickChatroomIds.TryGetValue(channel, out var configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            chatroomId = configured.Trim();
        }

        try
        {
            var escapedChannel = Uri.EscapeDataString(channel);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://kick.com/api/v2/channels/{escapedChannel}");
            request.Headers.Referrer = new Uri($"https://kick.com/{escapedChannel}");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var resolved = ReadKickChannelInfo(document.RootElement);
            channelId = resolved.ChannelId;
            chatroomId ??= resolved.ChatroomId;
            broadcasterUserId = resolved.BroadcasterUserId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = string.IsNullOrWhiteSpace(chatroomId)
                ? $"Kick public channel metadata failed for {channel}."
                : $"Kick public channel metadata failed for {channel}; using configured chatroom ID.";
            logger.Write(AppLogLevel.Warning, "KickChat", message, ex);
        }

        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(chatroomId))
        {
            var curlMetadata = await TryResolveChannelInfoWithCurlAsync(channel, cancellationToken).ConfigureAwait(false);
            if (curlMetadata is not null)
            {
                channelId ??= curlMetadata.ChannelId;
                chatroomId ??= curlMetadata.ChatroomId;
                broadcasterUserId ??= curlMetadata.BroadcasterUserId;
                logger.Write(AppLogLevel.Info, "KickChat", $"Resolved Kick chatroom ID for {channel} with curl fallback.");
            }
        }

        return new KickChannelInfo(
            NormalizeNumericId(channelId),
            NormalizeNumericId(chatroomId),
            broadcasterUserId);
    }

    public async Task<ChatHistoryBackfillResult> BackfillRecentChatFromStartTimeAsync(
        string channel,
        string? channelId,
        string chatroomId,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken)
    {
        var candidateIds = BuildKickMessagesChannelIds(channelId, chatroomId).ToArray();
        if (candidateIds.Length == 0)
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        fromTimestampUtc = fromTimestampUtc.ToUniversalTime();
        throughTimestampUtc = throughTimestampUtc.ToUniversalTime();
        if (throughTimestampUtc < fromTimestampUtc)
        {
            throughTimestampUtc = fromTimestampUtc;
        }

        await backfillGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attempted = false;
            var hasFailure = false;
            var verifiedEmptyIds = new HashSet<string>(StringComparer.Ordinal);
            ChatHistoryBackfillResult? emptyResult = null;
            if (!directBackfillBlocked)
            {
                foreach (var messagesChannelId in candidateIds)
                {
                    var page = await TryReadKickRecentMessagesFromStartTimeDirectAsync(
                            channel,
                            messagesChannelId,
                            fromTimestampUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                    attempted = true;
                    if (page is null)
                    {
                        hasFailure = true;
                        if (directBackfillBlocked)
                        {
                            break;
                        }

                        continue;
                    }

                    var result = await BackfillKickStartTimePageRangeAsync(
                            channel,
                            messagesChannelId,
                            page,
                            fromTimestampUtc,
                            throughTimestampUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (result.LoadedMessageCount > 0)
                    {
                        return result;
                    }

                    if (result.CoveredRequestedRange)
                    {
                        verifiedEmptyIds.Add(messagesChannelId);
                        emptyResult ??= result;
                    }
                    else
                    {
                        hasFailure = true;
                    }
                }
            }

            if (verifiedEmptyIds.Count == candidateIds.Length)
            {
                return emptyResult ?? new ChatHistoryBackfillResult(
                    attempted,
                    0,
                    true,
                    fromTimestampUtc,
                    throughTimestampUtc);
            }

            if (directBackfillBlocked)
            {
                foreach (var messagesChannelId in candidateIds)
                {
                    if (verifiedEmptyIds.Contains(messagesChannelId))
                    {
                        continue;
                    }

                    var page = await TryReadKickRecentMessagesFromStartTimeWithCurlAsync(
                            channel,
                            messagesChannelId,
                            fromTimestampUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                    attempted = true;
                    if (page is null)
                    {
                        hasFailure = true;
                        continue;
                    }

                    var result = await BackfillKickStartTimePageRangeAsync(
                            channel,
                            messagesChannelId,
                            page,
                            fromTimestampUtc,
                            throughTimestampUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (result.LoadedMessageCount > 0)
                    {
                        return result;
                    }

                    if (result.CoveredRequestedRange)
                    {
                        verifiedEmptyIds.Add(messagesChannelId);
                        emptyResult ??= result;
                    }
                    else
                    {
                        hasFailure = true;
                    }
                }
            }

            if (verifiedEmptyIds.Count == candidateIds.Length)
            {
                return emptyResult ?? new ChatHistoryBackfillResult(
                    attempted,
                    0,
                    true,
                    fromTimestampUtc,
                    throughTimestampUtc);
            }

            return new ChatHistoryBackfillResult(attempted || hasFailure, 0, false, null, null);
        }
        finally
        {
            backfillGate.Release();
        }
    }

    private async Task<ChatHistoryBackfillResult> BackfillKickStartTimePageRangeAsync(
        string channel,
        string messagesChannelId,
        KickRecentChatPage firstPage,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken)
    {
        var loadedMessages = new List<ChatMessage>();
        var requestedCursors = new HashSet<string>(StringComparer.Ordinal);
        var page = firstPage;
        var pageCount = 0;
        var completionReason = "through";
        while (true)
        {
            pageCount++;
            var loadedThroughBeforePageUtc = GetLatestMessageTimestampUtc(loadedMessages);
            var pageLatestTimestampUtc = GetLatestMessageTimestampUtc(page.Messages);
            if (pageCount > 1 &&
                loadedThroughBeforePageUtc is { } loadedThroughBeforePage &&
                (pageLatestTimestampUtc is null || pageLatestTimestampUtc <= loadedThroughBeforePage))
            {
                completionReason = "non-advancing";
                break;
            }

            AddKickBackfillPageMessages(loadedMessages, page, KickRecentChatSeekBackfillLimit);

            var loadedThroughTimestampUtc = GetLatestMessageTimestampUtc(loadedMessages);
            if (loadedThroughTimestampUtc is { } loadedThrough &&
                loadedThrough >= throughTimestampUtc)
            {
                break;
            }

            if (loadedMessages.Count >= KickRecentChatSeekBackfillLimit)
            {
                completionReason = "cap";
                break;
            }

            var cursor = NormalizeCursor(page.Cursor);
            if (string.IsNullOrWhiteSpace(cursor) ||
                !requestedCursors.Add(cursor))
            {
                completionReason = "exhausted";
                break;
            }

            var nextPage = await TryReadKickRecentMessagesCursorPageAsync(
                    channel,
                    messagesChannelId,
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);
            if (nextPage is null)
            {
                completionReason = "failure";
                break;
            }

            page = nextPage;
        }

        var orderedMessages = OrderKickMessages(loadedMessages).ToArray();
        var requestedRangeMessages = FilterKickBackfillMessagesToRequestedRange(
            orderedMessages,
            fromTimestampUtc,
            throughTimestampUtc);
        ChatHistoryBackfillResult result;
        if (completionReason == "exhausted" && orderedMessages.Length == 0)
        {
            result = new ChatHistoryBackfillResult(
                Attempted: true,
                LoadedMessageCount: requestedRangeMessages.Length,
                CoveredRequestedRange: true,
                CoveredFromTimestampUtc: fromTimestampUtc,
                CoveredThroughTimestampUtc: throughTimestampUtc,
                Messages: requestedRangeMessages);
            LogKickStartTimeBackfillPageRange(channel, messagesChannelId, pageCount, completionReason, result);
            return result;
        }

        var partialThroughTimestampUtc = GetLatestMessageTimestampUtc(orderedMessages);
        if (partialThroughTimestampUtc is null)
        {
            result = new ChatHistoryBackfillResult(true, 0, false, null, null, requestedRangeMessages);
            LogKickStartTimeBackfillPageRange(channel, messagesChannelId, pageCount, completionReason, result);
            return result;
        }

        if (completionReason == "failure")
        {
            result = new ChatHistoryBackfillResult(
                Attempted: true,
                LoadedMessageCount: requestedRangeMessages.Length,
                CoveredRequestedRange: false,
                CoveredFromTimestampUtc: null,
                CoveredThroughTimestampUtc: null,
                Messages: requestedRangeMessages);
            LogKickStartTimeBackfillPageRange(channel, messagesChannelId, pageCount, completionReason, result);
            return result;
        }

        if (partialThroughTimestampUtc.Value < fromTimestampUtc)
        {
            partialThroughTimestampUtc = fromTimestampUtc;
        }

        var coveredRequestedRange = partialThroughTimestampUtc.Value >= throughTimestampUtc;
        result = new ChatHistoryBackfillResult(
            Attempted: true,
            LoadedMessageCount: requestedRangeMessages.Length,
            CoveredRequestedRange: coveredRequestedRange,
            CoveredFromTimestampUtc: fromTimestampUtc,
            CoveredThroughTimestampUtc: coveredRequestedRange ? throughTimestampUtc : partialThroughTimestampUtc,
            Messages: requestedRangeMessages);
        LogKickStartTimeBackfillPageRange(channel, messagesChannelId, pageCount, completionReason, result);
        return result;
    }

    private void LogKickStartTimeBackfillPageRange(
        string channel,
        string messagesChannelId,
        int pageCount,
        string completionReason,
        ChatHistoryBackfillResult result)
    {
        logger.Write(
            AppLogLevel.Debug,
            "KickChat",
            $"Kick seekback start_time page range for {channel} via {messagesChannelId}: " +
            $"pages={pageCount.ToString(CultureInfo.InvariantCulture)}, loaded={result.LoadedMessageCount.ToString(CultureInfo.InvariantCulture)}, " +
            $"reason={completionReason}, covered={result.CoveredRequestedRange}, " +
            $"range={FormatBackfillTimestamp(result.CoveredFromTimestampUtc)} through {FormatBackfillTimestamp(result.CoveredThroughTimestampUtc)}.");
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesCursorPageAsync(
        string channel,
        string messagesChannelId,
        string cursor,
        CancellationToken cancellationToken)
    {
        var page = directBackfillBlocked
            ? null
            : await TryReadKickRecentMessagesDirectAsync(channel, messagesChannelId, cursor, cancellationToken).ConfigureAwait(false);
        return page ?? await TryReadKickRecentMessagesWithCurlAsync(channel, messagesChannelId, cursor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesDirectAsync(
        string channel,
        string messagesChannelId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(KickRecentChatBackfillTimeout);
        try
        {
            var escapedMessagesChannelId = Uri.EscapeDataString(messagesChannelId);
            var escapedChannel = Uri.EscapeDataString(channel);
            var url = BuildKickRecentMessagesUrl(escapedMessagesChannelId, cursor, startTimeUtc: null);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri($"https://kick.com/{escapedChannel}");

            using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    directBackfillBlocked = true;
                }

                logger.Write(
                    AppLogLevel.Info,
                    "KickChat",
                    $"Kick recent chat backfill failed for {channel}: {(int)response.StatusCode} {response.ReasonPhrase}.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            return ReadKickRecentChatPage(document.RootElement, channel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Kick recent chat backfill timed out for {channel}.");
            return null;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Kick recent chat backfill failed for {channel}.", ex);
            return null;
        }
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesFromStartTimeDirectAsync(
        string channel,
        string messagesChannelId,
        DateTimeOffset startTimeUtc,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(KickRecentChatBackfillTimeout);
        try
        {
            var escapedMessagesChannelId = Uri.EscapeDataString(messagesChannelId);
            var escapedChannel = Uri.EscapeDataString(channel);
            var url = BuildKickRecentMessagesUrl(escapedMessagesChannelId, cursor: null, startTimeUtc: startTimeUtc);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri($"https://kick.com/{escapedChannel}");

            using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    directBackfillBlocked = true;
                }

                logger.Write(
                    AppLogLevel.Info,
                    "KickChat",
                    $"Kick timestamp chat backfill failed for {channel}: {(int)response.StatusCode} {response.ReasonPhrase}.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            return ReadKickRecentChatPage(document.RootElement, channel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Kick timestamp chat backfill timed out for {channel}.");
            return null;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"Kick timestamp chat backfill failed for {channel}.", ex);
            return null;
        }
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesWithCurlAsync(
        string channel,
        string messagesChannelId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var curlPath = ResolveCurlPath();
        if (string.IsNullOrWhiteSpace(curlPath))
        {
            logger.Write(AppLogLevel.Warning, "KickChat", "curl.exe was not found; Kick recent chat backfill fallback is unavailable.");
            return null;
        }

        var escapedChannel = Uri.EscapeDataString(channel);
        var escapedMessagesChannelId = Uri.EscapeDataString(messagesChannelId);
        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildKickRecentMessagesCurlArguments(escapedChannel, escapedMessagesChannelId, cursor))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }

                logger.Write(AppLogLevel.Info, "KickChat", $"curl.exe timed out loading recent Kick chat for {channel}.");
                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                logger.Write(AppLogLevel.Info, "KickChat", $"curl.exe failed loading recent Kick chat for {channel}: {stderr.Trim()}");
                return null;
            }

            using var document = JsonDocument.Parse(stdout);
            var page = ReadKickRecentChatPage(document.RootElement, channel);
            logger.Write(AppLogLevel.Info, "KickChat", $"Loaded recent Kick chat for {channel} with curl fallback.");
            return page;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"curl.exe recent chat fallback failed for {channel}.", ex);
            return null;
        }
    }

    private async Task<KickRecentChatPage?> TryReadKickRecentMessagesFromStartTimeWithCurlAsync(
        string channel,
        string messagesChannelId,
        DateTimeOffset startTimeUtc,
        CancellationToken cancellationToken)
    {
        var curlPath = ResolveCurlPath();
        if (string.IsNullOrWhiteSpace(curlPath))
        {
            logger.Write(AppLogLevel.Warning, "KickChat", "curl.exe was not found; Kick timestamp chat backfill fallback is unavailable.");
            return null;
        }

        var escapedChannel = Uri.EscapeDataString(channel);
        var escapedMessagesChannelId = Uri.EscapeDataString(messagesChannelId);
        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildKickStartTimeMessagesCurlArguments(escapedChannel, escapedMessagesChannelId, startTimeUtc))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }

                logger.Write(AppLogLevel.Info, "KickChat", $"curl.exe timed out loading timestamp Kick chat for {channel}.");
                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                logger.Write(AppLogLevel.Info, "KickChat", $"curl.exe failed loading timestamp Kick chat for {channel}: {stderr.Trim()}");
                return null;
            }

            using var document = JsonDocument.Parse(stdout);
            var page = ReadKickRecentChatPage(document.RootElement, channel);
            logger.Write(AppLogLevel.Info, "KickChat", $"Loaded timestamp Kick chat for {channel} with curl fallback.");
            return page;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Info, "KickChat", $"curl.exe timestamp chat fallback failed for {channel}.", ex);
            return null;
        }
    }

    private async Task<KickChannelInfo?> TryResolveChannelInfoWithCurlAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        var curlPath = ResolveCurlPath();
        if (string.IsNullOrWhiteSpace(curlPath))
        {
            logger.Write(AppLogLevel.Warning, "KickChat", "curl.exe was not found; Kick chatroom metadata fallback is unavailable.");
            return null;
        }

        var escapedChannel = Uri.EscapeDataString(channel);
        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildKickMetadataCurlArguments(escapedChannel))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }

                logger.Write(AppLogLevel.Warning, "KickChat", $"curl.exe timed out resolving Kick metadata for {channel}.");
                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                logger.Write(AppLogLevel.Warning, "KickChat", $"curl.exe failed resolving Kick metadata for {channel}: {stderr.Trim()}");
                return null;
            }

            using var document = JsonDocument.Parse(stdout);
            return ReadKickChannelInfo(document.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "KickChat", $"curl.exe metadata fallback failed for {channel}.", ex);
            return null;
        }
    }

    private static void AddKickBackfillPageMessages(
        List<ChatMessage> loadedMessages,
        KickRecentChatPage page,
        int maxMessages)
    {
        foreach (var message in OrderKickMessages(page.Messages))
        {
            if (loadedMessages.Count >= maxMessages)
            {
                break;
            }

            loadedMessages.Add(message);
        }
    }

    private static IEnumerable<ChatMessage> OrderKickMessages(IEnumerable<ChatMessage> messages)
    {
        return messages
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal);
    }

    private static DateTimeOffset? GetLatestMessageTimestampUtc(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return null;
        }

        return messages
            .Max(message => message.Timestamp)
            .ToUniversalTime();
    }

    private static ChatMessage[] FilterKickBackfillMessagesToRequestedRange(
        IReadOnlyList<ChatMessage> messages,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        fromTimestampUtc = fromTimestampUtc.ToUniversalTime();
        throughTimestampUtc = throughTimestampUtc.ToUniversalTime();
        return messages
            .Where(message =>
            {
                var timestampUtc = message.Timestamp.ToUniversalTime();
                return timestampUtc >= fromTimestampUtc && timestampUtc <= throughTimestampUtc;
            })
            .ToArray();
    }

    private static IReadOnlyList<ChatMessage> ReadKickRecentChatMessages(JsonElement root, string channel)
    {
        return ReadKickRecentChatPage(root, channel).Messages;
    }

    private static KickRecentChatPage ReadKickRecentChatPage(JsonElement root, string channel)
    {
        if (!TryGetKickRecentChatMessageArray(root, out var messagesElement))
        {
            return new KickRecentChatPage([], ReadKickRecentChatCursor(root));
        }

        var messages = new List<ChatMessage>();
        foreach (var item in messagesElement.EnumerateArray())
        {
            var message = KickPusherParser.TryParseMessageData(item, channel);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return new KickRecentChatPage(
            OrderKickMessages(messages).ToArray(),
            ReadKickRecentChatCursor(root));
    }

    private static bool TryGetKickRecentChatMessageArray(JsonElement root, out JsonElement messages)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("messages", out messages) &&
                    messages.ValueKind == JsonValueKind.Array)
                {
                    return true;
                }

                if (data.ValueKind == JsonValueKind.Array)
                {
                    messages = data;
                    return true;
                }
            }

            if (root.TryGetProperty("messages", out messages) &&
                messages.ValueKind == JsonValueKind.Array)
            {
                return true;
            }
        }

        messages = default;
        return false;
    }

    private static string? ReadKickRecentChatCursor(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            TryReadNonEmptyString(data, "cursor", out var dataCursor))
        {
            return dataCursor;
        }

        return TryReadNonEmptyString(root, "cursor", out var rootCursor)
            ? rootCursor
            : null;
    }

    private static bool TryReadNonEmptyString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : property.ToString();
        value = value.Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static KickChannelInfo ReadKickChannelInfo(JsonElement root)
    {
        string? channelId = null;
        string? chatroomId = null;
        long? broadcasterUserId = null;

        if (root.TryGetProperty("id", out var rootId))
        {
            channelId = rootId.ToString();
        }

        if (string.IsNullOrWhiteSpace(channelId) &&
            root.TryGetProperty("channel_id", out var channelIdProperty))
        {
            channelId = channelIdProperty.ToString();
        }

        if (string.IsNullOrWhiteSpace(channelId) &&
            root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("id", out var dataId))
        {
            channelId = dataId.ToString();
        }

        if (string.IsNullOrWhiteSpace(channelId) &&
            root.TryGetProperty("data", out data) &&
            data.TryGetProperty("channel_id", out var dataChannelId))
        {
            channelId = dataChannelId.ToString();
        }

        if (root.TryGetProperty("chatroom", out var chatroom) &&
            chatroom.TryGetProperty("id", out var id))
        {
            chatroomId = id.ToString();
        }

        if (string.IsNullOrWhiteSpace(chatroomId) &&
            root.TryGetProperty("data", out data) &&
            data.TryGetProperty("chatroom", out var dataChatroom) &&
            dataChatroom.TryGetProperty("id", out var dataChatroomId))
        {
            chatroomId = dataChatroomId.ToString();
        }

        if (string.IsNullOrWhiteSpace(chatroomId) &&
            root.TryGetProperty("chatroom_id", out var chatroomIdProperty))
        {
            chatroomId = chatroomIdProperty.ToString();
        }

        if (root.TryGetProperty("user", out var user) &&
            user.TryGetProperty("id", out var userId))
        {
            broadcasterUserId = TryGetInt64(userId);
        }

        if (broadcasterUserId is null &&
            root.TryGetProperty("data", out data) &&
            data.TryGetProperty("user", out var dataUser) &&
            dataUser.TryGetProperty("id", out var dataUserId))
        {
            broadcasterUserId = TryGetInt64(dataUserId);
        }

        if (broadcasterUserId is null &&
            root.TryGetProperty("broadcaster_user_id", out var broadcasterUserIdProperty))
        {
            broadcasterUserId = TryGetInt64(broadcasterUserIdProperty);
        }

        return new KickChannelInfo(
            NormalizeNumericId(channelId),
            NormalizeNumericId(chatroomId),
            broadcasterUserId);
    }

    private static long? TryGetInt64(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(element.GetString(), out var value) => value,
            _ => null
        };
    }

    private static IEnumerable<string> BuildKickMessagesChannelIds(string? channelId, string chatroomId)
    {
        var normalizedChannelId = NormalizeNumericId(channelId);
        if (!string.IsNullOrWhiteSpace(normalizedChannelId))
        {
            yield return normalizedChannelId;
        }

        var normalizedChatroomId = NormalizeNumericId(chatroomId);
        if (!string.IsNullOrWhiteSpace(normalizedChatroomId) &&
            !string.Equals(normalizedChatroomId, normalizedChannelId, StringComparison.Ordinal))
        {
            yield return normalizedChatroomId;
        }
    }

    private static string BuildKickRecentMessagesUrl(
        string escapedMessagesChannelId,
        string? cursor,
        DateTimeOffset? startTimeUtc)
    {
        var url = $"https://kick.com/api/v2/channels/{escapedMessagesChannelId}/messages";
        if (startTimeUtc is { } startTime)
        {
            return $"{url}?start_time={Uri.EscapeDataString(FormatKickStartTime(startTime))}";
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            return $"{url}?cursor={Uri.EscapeDataString(cursor)}";
        }

        return url;
    }

    private static string FormatKickStartTime(DateTimeOffset timestampUtc)
    {
        return timestampUtc
            .ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static string FormatBackfillTimestamp(DateTimeOffset? timestampUtc)
    {
        return timestampUtc is { } timestamp
            ? timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "none";
    }

    private static string? NormalizeCursor(string? cursor)
    {
        return string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
    }

    private static string? NormalizeNumericId(string? value)
    {
        var normalized = NormalizeCursor(value);
        return normalized is not null && normalized.All(char.IsDigit)
            ? normalized
            : null;
    }

    private static string? ResolveCurlPath()
    {
        var configured = Environment.GetEnvironmentVariable("STREAMLINK_KICK_CURL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return "curl.exe";
    }

    private static IEnumerable<string> BuildKickMetadataCurlArguments(string escapedChannel)
    {
        yield return "--location";
        yield return "--silent";
        yield return "--show-error";
        yield return "--fail";
        yield return "--compressed";
        yield return "--max-time";
        yield return "15";
        yield return "--user-agent";
        yield return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
        yield return "--header";
        yield return "Accept: application/json,text/plain,*/*";
        yield return "--header";
        yield return "Accept-Language: *";
        yield return "--referer";
        yield return $"https://kick.com/{escapedChannel}";
        yield return $"https://kick.com/api/v2/channels/{escapedChannel}";
    }

    private static IEnumerable<string> BuildKickRecentMessagesCurlArguments(
        string escapedChannel,
        string escapedMessagesChannelId,
        string? cursor)
    {
        yield return "--location";
        yield return "--silent";
        yield return "--show-error";
        yield return "--fail";
        yield return "--compressed";
        yield return "--max-time";
        yield return "15";
        yield return "--user-agent";
        yield return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
        yield return "--header";
        yield return "Accept: application/json,text/plain,*/*";
        yield return "--header";
        yield return "Accept-Language: *";
        yield return "--referer";
        yield return $"https://kick.com/{escapedChannel}";
        yield return BuildKickRecentMessagesUrl(escapedMessagesChannelId, cursor, startTimeUtc: null);
    }

    private static IEnumerable<string> BuildKickStartTimeMessagesCurlArguments(
        string escapedChannel,
        string escapedMessagesChannelId,
        DateTimeOffset startTimeUtc)
    {
        yield return "--location";
        yield return "--silent";
        yield return "--show-error";
        yield return "--fail";
        yield return "--compressed";
        yield return "--max-time";
        yield return "15";
        yield return "--user-agent";
        yield return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
        yield return "--header";
        yield return "Accept: application/json,text/plain,*/*";
        yield return "--header";
        yield return "Accept-Language: *";
        yield return "--referer";
        yield return $"https://kick.com/{escapedChannel}";
        yield return BuildKickRecentMessagesUrl(escapedMessagesChannelId, cursor: null, startTimeUtc: startTimeUtc);
    }

    public void Dispose()
    {
        backfillGate.Dispose();
    }
}

internal sealed record KickChannelInfo(string? ChannelId, string? ChatroomId, long? BroadcasterUserId);

internal sealed record KickRecentChatPage(IReadOnlyList<ChatMessage> Messages, string? Cursor);
