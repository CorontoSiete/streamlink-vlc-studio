using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using StreamlinkVlcStudio.App.Wpf;
using StreamlinkVlcStudio.App.Wpf.Chat;
using StreamlinkVlcStudio.App.Wpf.Controls;
using StreamlinkVlcStudio.App.Wpf.Notifications;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Security;
using StreamlinkVlcStudio.Core.Twitch;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Logging;
using StreamlinkVlcStudio.Infrastructure.Processes;
using StreamlinkVlcStudio.Infrastructure.Replay;
using StreamlinkVlcStudio.Infrastructure.Settings;
using StreamlinkVlcStudio.Infrastructure.Viewers;
using StreamlinkVlcStudio.Infrastructure.Vlc;

internal static class TestSubsystemCatalog
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ..CoreQualityTestCatalog.All,
        ..ChatSecurityTestCatalog.All,
        ("AsyncRelayCommand admits only one concurrent execution", AsyncRelayCommandIsSingleFlightAsync),
        ("native overlay capability probing is asynchronous, bounded, and cancellable", NativeOverlayCapabilityProbeAsync),
        ("PiP hit testing rejects occluded windows and accepts owned descendants", PictureInPictureHitTesting),
        ("shared HTTP client creation applies bounded timeout and defaults", SharedHttpClientDefaults),
        ("bounded process runner drains output and returns timeout data", BoundedProcessRunnerTimeoutAsync),
        ("bounded process runner drains and truncates large stdout and stderr", BoundedProcessRunnerLargeOutputAsync),
        ("bounded process output retains deterministic head and tail", BoundedProcessOutputHeadTail),
        ("bounded byte reader enforces HTTP and stream limits", BoundedByteReaderLimitsAsync),
        ("bounded byte reader handles files and cancellation", BoundedByteReaderFilesAndCancellationAsync),
        ("bounded HTTP text rejects oversized and invalid payloads", BoundedHttpTextAsync),
        ("bounded websocket reader validates frames encoding and aggregate size", BoundedWebSocketTextAsync),
        ("Kick websocket handshake requires exact acknowledgements", KickWebSocketHandshakeAsync),
        ("Twitch IRC lines handshakes and outbound messages are bounded", TwitchIrcBoundariesAsync),
        ("image decode admission enforces dimensions pixels frames and memory", ImageDecodeAdmissionBounds),
        ("settings secrets use a versioned DPAPI current-user envelope", ProtectedSettingsRoundTripAsync),
        ("legacy plaintext settings secrets migrate atomically", LegacySettingsSecretMigrationAsync),
        ("corrupt protected settings preserve nonsecrets and require reconnect", CorruptProtectedSettingsAsync),
        ("Kick webhook validates freshness replay and key rotation", KickWebhookAuthenticationAsync),
        ("Kick webhook preserves framing statuses blocks CORS and caps clients", KickWebhookHttpSurfaceAsync),
        ("replay URL security rejects spoofed hosts and nonpublic addresses", ReplayUrlValidationAsync),
        ("replay HTTP redirects remain on validated public provider hosts", ReplayRedirectValidationAsync),
        ("file logger redacts normalizes and rotates bounded files", FileLoggerSanitizationAndRotationAsync),
        ("file logger bounds its queue and shutdown wait", FileLoggerQueueAndShutdownBoundsAsync),
        ("Kick token provider shares acquisition and isolates waiter cancellation", KickTokenProviderSingleFlightAsync),
        ("live channel snapshots share requests for five seconds", LiveChannelSnapshotCachingAsync),
        ("viewer count and metadata reuse one live channel snapshot", LiveChannelSnapshotServiceSharingAsync),
        ("catalog coordinator retries isolates callbacks and remains bounded", CatalogLoadCoordinatorBoundsAsync),
        ("chat emotes remain scoped by platform channel and code", ChatEmoteCatalogScoping),
        ("emoji runs use grapheme clusters and a bounded LRU cache", EmojiGraphemeAndCacheBounds),
        ("toast thumbnail storage prunes entry and byte limits", ToastThumbnailStorageBounds),
        ("provider playlists and storyboards reject URI spoofing", ProviderPlaylistValidation),
        ("Kick event content badges and channel slugs use exact validation", ExactKickInputValidation),
        ("executable PATH parsing rejects relative and malformed entries", ExecutablePathValidation),
        ("VLC plugin cache manifest invalidates every identity input", VlcPluginCacheManifestValidation),
        ..InfrastructureRuntimeTestCatalog.All
    ];

    private static async Task AsyncRelayCommandIsSingleFlightAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var command = new AsyncRelayCommand(async () =>
        {
            Interlocked.Increment(ref executions);
            started.TrySetResult();
            await release.Task;
        });

        var attempts = Enumerable.Range(0, 32)
            .Select(_ => command.ExecuteAsync())
            .ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, executions);
        release.TrySetResult();
        await Task.WhenAll(attempts);
        Assert.True(command.CanExecute(null));
    }

    private static async Task NativeOverlayCapabilityProbeAsync()
    {
        NativeOverlayCapabilityProbe.ClearCache();
        ProcessStartInfo? observedStartInfo = null;
        var probe = new NativeOverlayCapabilityProbe(
            runProcessAsync: (startInfo, timeout, cancellationToken) =>
            {
                observedStartInfo = startInfo;
                Assert.Equal(TimeSpan.FromMilliseconds(25), timeout);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new ProcessExecutionResult(
                    0,
                    "usage: --font-size",
                    "",
                    TimedOut: false));
            },
            timeout: TimeSpan.FromMilliseconds(25));

        Assert.True(await probe.SupportsFontSizeAsync("controller-under-test.exe"));
        Assert.Equal("--help", observedStartInfo?.ArgumentList.Single());

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new NativeOverlayCapabilityProbe(
                    runProcessAsync: (_, _, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult(new ProcessExecutionResult(0, "", "", false));
                    })
                .SupportsFontSizeAsync("canceled-controller.exe", cancellation.Token));
    }

    private static Task PictureInPictureHitTesting()
    {
        var host = new IntPtr(10);
        var child = new IntPtr(20);
        var popup = new IntPtr(30);
        var other = new IntPtr(40);
        var hitTester = new FakeWindowHitTester
        {
            PointWindow = child,
            ChildWindow = child,
            RootWindow = host
        };

        Assert.True(WindowHitTestPolicy.IsPointInWindow(hitTester, host, 1, 2, includeOwnedPopups: false));
        hitTester.PointWindow = other;
        hitTester.RootWindow = other;
        Assert.Equal(false, WindowHitTestPolicy.IsPointInWindow(hitTester, host, 1, 2, includeOwnedPopups: false));

        hitTester.PointWindow = popup;
        hitTester.RootWindow = popup;
        hitTester.RootOwnerWindow = host;
        Assert.True(WindowHitTestPolicy.IsPointInWindow(hitTester, host, 1, 2, includeOwnedPopups: true));
        Assert.Equal(false, WindowHitTestPolicy.IsPointInWindow(hitTester, host, 1, 2, includeOwnedPopups: false));
        return Task.CompletedTask;
    }

    private static async Task BoundedProcessRunnerTimeoutAsync()
    {
        var startInfo = BoundedProcessRunner.CreateRedirectedStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/c", "echo stdout & echo stderr 1>&2 & ping -n 5 127.0.0.1 >nul"]);
        var result = await new BoundedProcessRunner().RunAsync(
            startInfo,
            TimeSpan.FromMilliseconds(100));
        Assert.True(result.TimedOut);
        Assert.True(result.StandardOutput.Contains("stdout", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.StandardError.Contains("stderr", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task BoundedProcessRunnerLargeOutputAsync()
    {
        const int outputLength = (4 * 1024 * 1024) + 1024;
        var startInfo = BoundedProcessRunner.CreateRedirectedStartInfo(
            "powershell.exe",
            [
                "-NoProfile",
                "-Command",
                $"$a=[Console]::Out.WriteAsync('o' * {outputLength}); $b=[Console]::Error.WriteAsync('e' * {outputLength}); [Threading.Tasks.Task]::WaitAll(@($a,$b))"
            ]);
        var result = await new BoundedProcessRunner().RunAsync(
            startInfo,
            TimeSpan.FromSeconds(15));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(false, result.TimedOut);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
        Assert.Equal(4 * 1024 * 1024, result.StandardOutput.Length);
        Assert.Equal(4 * 1024 * 1024, result.StandardError.Length);
        Assert.True(result.StandardOutput.All(character => character == 'o'));
        Assert.True(result.StandardError.All(character => character == 'e'));
    }

    private static Task BoundedProcessOutputHeadTail()
    {
        var collector = new BoundedProcessOutputCollector(8);
        collector.Append("0123"u8);
        collector.Append("456789AB"u8);
        var output = collector.ToOutput(System.Text.Encoding.UTF8);
        Assert.True(output.Truncated);
        Assert.Equal("012389AB", output.Text);

        var exact = new BoundedProcessOutputCollector(8);
        exact.Append("01234567"u8);
        var exactOutput = exact.ToOutput(System.Text.Encoding.UTF8);
        Assert.Equal(false, exactOutput.Truncated);
        Assert.Equal("01234567", exactOutput.Text);
        return Task.CompletedTask;
    }

    private static Task SharedHttpClientDefaults()
    {
        using var client = HttpClientFactory.CreateDefault();
        Assert.Equal(TimeSpan.FromSeconds(20), client.Timeout);
        Assert.True(client.DefaultRequestHeaders.UserAgent.Any());
        Assert.True(client.DefaultRequestHeaders.Accept.Any());
        return Task.CompletedTask;
    }

    private static async Task BoundedByteReaderLimitsAsync()
    {
        const int maximum = 4;

        using (var normal = new StreamContent(new MemoryStream([1, 2, 3])))
        {
            normal.Headers.ContentLength = 3;
            var bytes = await BoundedByteReader.ReadAsync(normal, maximum);
            Assert.NotNull(bytes);
            Assert.SequenceEqual<byte>([1, 2, 3], bytes!);
        }

        using (var missingLength = new StreamContent(new NonSeekableByteStream([4, 5, 6])))
        {
            missingLength.Headers.ContentLength = null;
            var bytes = await BoundedByteReader.ReadAsync(missingLength, maximum);
            Assert.NotNull(bytes);
            Assert.SequenceEqual<byte>([4, 5, 6], bytes!);
        }

        using (var underreported = new StreamContent(new NonSeekableByteStream([7, 8, 9, 10, 11])))
        {
            underreported.Headers.ContentLength = 1;
            Assert.Equal<byte[]?>(null, await BoundedByteReader.ReadAsync(underreported, maximum));
        }

        var earlyRejectedStream = new TrackingByteStream([1, 2, 3, 4, 5]);
        using (var declaredOversized = new StreamContent(earlyRejectedStream))
        {
            declaredOversized.Headers.ContentLength = maximum + 1;
            Assert.Equal<byte[]?>(null, await BoundedByteReader.ReadAsync(declaredOversized, maximum));
        }

        Assert.Equal(0, earlyRejectedStream.ReadCount);

        var growingStream = new GrowingByteStream(
            initial: [1, 2],
            afterFirstRead: [3, 4, 5, 6, 7]);
        Assert.Equal<byte[]?>(null, await BoundedByteReader.ReadAsync(growingStream, maximum));
        Assert.True(growingStream.ReadCount >= 2);
    }

    private static async Task BoundedHttpTextAsync()
    {
        using (var oversized = new StreamContent(new NonSeekableByteStream("12345"u8.ToArray())))
        {
            oversized.Headers.ContentLength = null;
            await Assert.ThrowsAsync<PayloadTooLargeException>(
                () => BoundedHttpContentReader.ReadStringAsync(oversized, 4));
        }

        using (var invalidUtf8 = new ByteArrayContent([0xC3, 0x28]))
        {
            await Assert.ThrowsAsync<System.Text.DecoderFallbackException>(
                () => BoundedHttpContentReader.ReadStringAsync(invalidUtf8, 4));
        }

        using var valid = new ByteArrayContent("ok"u8.ToArray());
        Assert.Equal("ok", await BoundedHttpContentReader.ReadStringAsync(valid, 4));
    }

    private static async Task BoundedWebSocketTextAsync()
    {
        using (var fragmented = new ScriptedWebSocket(
                   new WebSocketFrame("hel"u8.ToArray(), WebSocketMessageType.Text, false),
                   new WebSocketFrame("lo"u8.ToArray(), WebSocketMessageType.Text, true)))
        {
            Assert.Equal("hello", await BoundedWebSocketTextReader.ReadAsync(fragmented));
        }

        using (var binary = new ScriptedWebSocket(
                   new WebSocketFrame([1], WebSocketMessageType.Binary, true)))
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => BoundedWebSocketTextReader.ReadAsync(binary));
        }

        using (var invalidUtf8 = new ScriptedWebSocket(
                   new WebSocketFrame([0xC3, 0x28], WebSocketMessageType.Text, true)))
        {
            await Assert.ThrowsAsync<DecoderFallbackException>(
                () => BoundedWebSocketTextReader.ReadAsync(invalidUtf8));
        }

        var oversizedFrames = Enumerable.Range(0, 129)
            .Select(index => new WebSocketFrame(
                new byte[8192],
                WebSocketMessageType.Text,
                index == 128))
            .ToArray();
        using var oversized = new ScriptedWebSocket(oversizedFrames);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => BoundedWebSocketTextReader.ReadAsync(oversized));
    }

    private static async Task KickWebSocketHandshakeAsync()
    {
        const string channel = "chatrooms.42.v2";
        using (var socket = new ScriptedWebSocket(
                   TextFrame("""{"event":"pusher:ping","data":{}}"""),
                   TextFrame("{\"event\":\"pusher_internal:subscription_succeeded\",\"channel\":\"" + channel + "\",\"data\":{}}")))
        {
            await KickChatClient.WaitForPusherAcknowledgementAsync(
                socket,
                "pusher_internal:subscription_succeeded",
                channel,
                CancellationToken.None,
                TimeSpan.FromSeconds(1));
            Assert.True(socket.SentText.Contains("\"pusher:pong\"", StringComparison.Ordinal));
        }

        using (var rejected = new ScriptedWebSocket(
                   TextFrame("""{"event":"pusher:error","data":{"message":"denied"}}""")))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                KickChatClient.WaitForPusherAcknowledgementAsync(
                    rejected,
                    "pusher:connection_established",
                    null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(1)));
        }

        using (var closed = new ScriptedWebSocket(
                   new WebSocketFrame([], WebSocketMessageType.Close, true)))
        {
            await Assert.ThrowsAsync<WebSocketException>(() =>
                KickChatClient.WaitForPusherAcknowledgementAsync(
                    closed,
                    "pusher:connection_established",
                    null,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(1)));
        }

        using var stalled = new ScriptedWebSocket();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            KickChatClient.WaitForPusherAcknowledgementAsync(
                stalled,
                "pusher:connection_established",
                null,
                CancellationToken.None,
                TimeSpan.FromMilliseconds(20)));
    }

    private static async Task TwitchIrcBoundariesAsync()
    {
        await using (var lineStream = new MemoryStream("abc\r\n"u8.ToArray()))
        using (var lineReader = new BoundedUtf8LineReader(lineStream, 5))
        {
            Assert.Equal("abc", await lineReader.ReadLineAsync());
        }

        await using (var oversizedStream = new MemoryStream("abcd\r\n"u8.ToArray()))
        using (var oversizedReader = new BoundedUtf8LineReader(oversizedStream, 5))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => oversizedReader.ReadLineAsync());
        }

        await using (var invalidStream = new MemoryStream([0xC3, 0x28, 0x0A]))
        using (var invalidReader = new BoundedUtf8LineReader(invalidStream, 16))
        {
            await Assert.ThrowsAsync<DecoderFallbackException>(() => invalidReader.ReadLineAsync());
        }

        var handshake = string.Join(
            "\r\n",
            "PING :tmi.twitch.tv",
            ":tmi.twitch.tv 001 test :Welcome",
            ":test!test@test.tmi.twitch.tv JOIN #channel",
            "");
        await using var handshakeInput = new MemoryStream(Encoding.UTF8.GetBytes(handshake));
        using var handshakeReader = new BoundedUtf8LineReader(handshakeInput);
        await using var handshakeOutput = new MemoryStream();
        await using var handshakeWriter = new StreamWriter(handshakeOutput, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };
        await using var client = new TwitchChatClient(new ChatSettings(), new MemoryLogger());
        await client.AwaitIrcHandshakeAsync(
            "channel",
            handshakeReader,
            handshakeWriter,
            CancellationToken.None,
            TimeSpan.FromSeconds(1));
        Assert.Contains("PONG :tmi.twitch.tv", Encoding.UTF8.GetString(handshakeOutput.ToArray()));

        var family = "👨‍👩‍👧‍👦";
        var truncated = TwitchChatClient.TruncateIrcMessage("channel", string.Concat(Enumerable.Repeat(family, 100)));
        Assert.True(Encoding.UTF8.GetByteCount($"PRIVMSG #channel :{truncated}\r\n") <= 512);
        Assert.True(truncated.Length > 0);
        Assert.Equal(0, truncated.Length % family.Length);
    }

    private static WebSocketFrame TextFrame(string text) =>
        new(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true);

    private static Task ImageDecodeAdmissionBounds()
    {
        Assert.True(AnimatedEmoteImage.IsDecodedImageShapeAllowedForTest(4000, 4000, 1));
        Assert.Equal(false, AnimatedEmoteImage.IsDecodedImageShapeAllowedForTest(4097, 1, 1));
        Assert.Equal(false, AnimatedEmoteImage.IsDecodedImageShapeAllowedForTest(4096, 4096, 1));
        Assert.Equal(false, AnimatedEmoteImage.IsDecodedImageShapeAllowedForTest(4000, 4000, 2));
        Assert.True(AnimatedEmoteImage.IsDecodedImageShapeAllowedForTest(1, 1, 300));
        Assert.Equal(false, AnimatedEmoteImage.IsDecodedImageShapeAllowedForTest(1, 1, 301));
        Assert.Equal(false, AnimatedEmoteImage.IsDecodedImageShapeAllowedForTest(0, 1, 1));
        return Task.CompletedTask;
    }

    private static async Task ProtectedSettingsRoundTripAsync()
    {
        var directory = CreateSettingsTestDirectory();
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new AppSettings { DefaultQuality = "720p60" };
            settings.Hotkeys.PreviousTab = "Ctrl+Prior";
            settings.Hotkeys.NextTab = "Ctrl+Next";
            settings.Hotkeys.ToggleReplaySeekBar = "F8";
            settings.Hotkeys.DismissFullscreenOrAutoScroll = "Shift+Escape";
            settings.Chat.TwitchOAuthToken = "twitch-secret-value";
            settings.Chat.KickOAuthToken = "kick-access-secret-value";
            settings.Chat.KickRefreshToken = "kick-refresh-secret-value";
            settings.Chat.KickClientSecret = "kick-client-secret-value";
            var service = new JsonSettingsService(path);

            await service.SaveAsync(settings);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.Contains("\"ProtectedSecrets\"", persisted);
            Assert.Contains("\"Version\": 1", persisted);
            Assert.Contains("\"Protection\": \"DPAPI-CurrentUser\"", persisted);
            Assert.DoesNotContain("TwitchOAuthToken", persisted);
            Assert.DoesNotContain("twitch-secret-value", persisted);
            Assert.DoesNotContain("kick-access-secret-value", persisted);
            Assert.DoesNotContain("kick-refresh-secret-value", persisted);
            Assert.DoesNotContain("kick-client-secret-value", persisted);

            var loaded = await service.LoadAsync();
            Assert.Equal("720p60", loaded.DefaultQuality);
            Assert.Equal("twitch-secret-value", loaded.Chat.TwitchOAuthToken);
            Assert.Equal("kick-access-secret-value", loaded.Chat.KickOAuthToken);
            Assert.Equal("kick-refresh-secret-value", loaded.Chat.KickRefreshToken);
            Assert.Equal("kick-client-secret-value", loaded.Chat.KickClientSecret);
            Assert.Equal("Ctrl+Prior", loaded.Hotkeys.PreviousTab);
            Assert.Equal("Ctrl+Next", loaded.Hotkeys.NextTab);
            Assert.Equal("F8", loaded.Hotkeys.ToggleReplaySeekBar);
            Assert.Equal("Shift+Escape", loaded.Hotkeys.DismissFullscreenOrAutoScroll);
            Assert.Equal<string?>(null, service.LastLoadWarning);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task LegacySettingsSecretMigrationAsync()
    {
        var directory = CreateSettingsTestDirectory();
        var path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "DefaultQuality": "480p",
                  "Chat": {
                    "TwitchClientId": "public-client-id",
                    "TwitchOAuthToken": "legacy-twitch-secret",
                    "KickOAuthToken": "legacy-kick-access",
                    "KickRefreshToken": "legacy-kick-refresh",
                    "KickClientSecret": "legacy-kick-client-secret"
                  }
                }
                """);

            var service = new JsonSettingsService(path);
            var loaded = await service.LoadAsync();
            Assert.Equal("480p", loaded.DefaultQuality);
            Assert.Equal("public-client-id", loaded.Chat.TwitchClientId);
            Assert.Equal("legacy-twitch-secret", loaded.Chat.TwitchOAuthToken);
            Assert.Equal("legacy-kick-access", loaded.Chat.KickOAuthToken);
            Assert.Equal("legacy-kick-refresh", loaded.Chat.KickRefreshToken);
            Assert.Equal("legacy-kick-client-secret", loaded.Chat.KickClientSecret);
            Assert.Equal(HotkeySettings.DefaultPreviousTab, loaded.Hotkeys.PreviousTab);
            Assert.Equal(HotkeySettings.DefaultNextTab, loaded.Hotkeys.NextTab);
            Assert.Equal(HotkeySettings.DefaultToggleReplaySeekBar, loaded.Hotkeys.ToggleReplaySeekBar);
            Assert.Equal(
                HotkeySettings.DefaultDismissFullscreenOrAutoScroll,
                loaded.Hotkeys.DismissFullscreenOrAutoScroll);

            var migrated = await File.ReadAllTextAsync(path);
            Assert.Contains("\"ProtectedSecrets\"", migrated);
            Assert.DoesNotContain("TwitchOAuthToken", migrated);
            Assert.DoesNotContain("legacy-twitch-secret", migrated);
            Assert.DoesNotContain("legacy-kick-access", migrated);
            Assert.DoesNotContain("legacy-kick-refresh", migrated);
            Assert.DoesNotContain("legacy-kick-client-secret", migrated);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CorruptProtectedSettingsAsync()
    {
        var directory = CreateSettingsTestDirectory();
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new AppSettings { DefaultQuality = "audio_only" };
            settings.Chat.TwitchClientId = "public-client-id";
            settings.Chat.TwitchOAuthToken = "secret-that-will-be-corrupted";
            await new JsonSettingsService(path).SaveAsync(settings);

            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root["ProtectedSecrets"]!["Ciphertext"] = "not-base64";
            await File.WriteAllTextAsync(path, root.ToJsonString());

            var service = new JsonSettingsService(path);
            var loaded = await service.LoadAsync();
            Assert.Equal("audio_only", loaded.DefaultQuality);
            Assert.Equal("public-client-id", loaded.Chat.TwitchClientId);
            Assert.Equal("", loaded.Chat.TwitchOAuthToken);
            Assert.NotNull(service.LastLoadWarning);
            Assert.Contains("Reconnect Twitch and Kick", service.LastLoadWarning!);

            var backups = Directory.GetFiles(directory, "settings.json.protected-secrets-corrupt-*");
            Assert.Equal(1, backups.Length);
            Assert.Contains("not-base64", await File.ReadAllTextAsync(backups[0]));
            Assert.DoesNotContain("secret-that-will-be-corrupted", await File.ReadAllTextAsync(path));

            var reloaded = await new JsonSettingsService(path).LoadAsync();
            Assert.Equal("audio_only", reloaded.DefaultQuality);
            Assert.Equal("", reloaded.Chat.TwitchOAuthToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateSettingsTestDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "StreamlinkVlcStudioTests",
            $"protected-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task ReplayUrlValidationAsync()
    {
        Assert.True(ReplayUrlSecurityValidator.TryValidateProviderUri(
            new Uri("https://stream.kick.com/replay/index.m3u8"),
            PlatformKind.Kick));
        Assert.True(ReplayUrlSecurityValidator.TryValidateProviderUri(
            new Uri("https://d2e2de1etea730.cloudfront.net/archive/index.m3u8"),
            PlatformKind.Twitch));

        var rejected = new[]
        {
            "http://stream.kick.com/replay/index.m3u8",
            "https://user@stream.kick.com/replay/index.m3u8",
            "https://stream.kick.com:444/replay/index.m3u8",
            "https://kick.com.evil.example/replay/index.m3u8",
            "https://notkick.com/replay/index.m3u8"
        };
        foreach (var value in rejected)
        {
            Assert.Equal(false, ReplayUrlSecurityValidator.TryValidateProviderUri(
                new Uri(value),
                PlatformKind.Kick));
        }

        var nonpublicAddresses = new[]
        {
            IPAddress.Loopback,
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("100.64.0.1"),
            IPAddress.Parse("169.254.1.1"),
            IPAddress.Parse("172.16.0.1"),
            IPAddress.Parse("192.168.1.1"),
            IPAddress.Parse("::1"),
            IPAddress.Parse("fc00::1"),
            IPAddress.Parse("fe80::1")
        };
        foreach (var address in nonpublicAddresses)
        {
            var validator = new ReplayUrlSecurityValidator(
                (_, _) => Task.FromResult(new[] { address }));
            await Assert.ThrowsAsync<InvalidDataException>(() => validator.ValidateAsync(
                new Uri("https://stream.kick.com/replay/index.m3u8"),
                PlatformKind.Kick));
        }

        var mixedValidator = new ReplayUrlSecurityValidator(
            (_, _) => Task.FromResult(new[]
            {
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("192.168.1.1")
            }));
        await Assert.ThrowsAsync<InvalidDataException>(() => mixedValidator.ValidateAsync(
            new Uri("https://stream.kick.com/replay/index.m3u8"),
            PlatformKind.Kick));

        await TestReplayUrlSecurity.PublicValidator.ValidateAsync(
            new Uri("https://stream.kick.com/replay/index.m3u8"),
            PlatformKind.Kick);
    }

    private static async Task ReplayRedirectValidationAsync()
    {
        var privateRedirectRequests = 0;
        using (var privateRedirectClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref privateRedirectRequests);
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://127.0.0.1/private.m3u8") }
            };
        })))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ValidatedReplayHttpClient.SendGetAsync(
                privateRedirectClient,
                TestReplayUrlSecurity.PublicValidator,
                new Uri("https://stream.kick.com/start.m3u8"),
                PlatformKind.Kick,
                static uri => new HttpRequestMessage(HttpMethod.Get, uri),
                CancellationToken.None));
        }

        Assert.Equal(1, privateRedirectRequests);

        var validRedirectRequests = 0;
        using var validRedirectClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Interlocked.Increment(ref validRedirectRequests);
            if (request.RequestUri!.AbsolutePath == "/start.m3u8")
            {
                return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("/final.m3u8", UriKind.Relative) }
                };
            }

            Assert.Equal("/final.m3u8", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("#EXTM3U")
            };
        }));
        using var response = await ValidatedReplayHttpClient.SendGetAsync(
            validRedirectClient,
            TestReplayUrlSecurity.PublicValidator,
            new Uri("https://stream.kick.com/start.m3u8"),
            PlatformKind.Kick,
            static uri => new HttpRequestMessage(HttpMethod.Get, uri),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, validRedirectRequests);
    }

    private static async Task FileLoggerSanitizationAndRotationAsync()
    {
        const string accessSecret = "access-secret-123";
        const string bearerSecret = "bearer-secret-456";
        const string refreshSecret = "refresh-secret-789";
        var directory = Path.Combine(
            Path.GetTempPath(),
            "StreamlinkVlcStudioTests",
            $"logger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var logger = new FileAppLogger(
                directory,
                capacity: 4096,
                maximumFileBytes: 1024,
                maximumFileCount: 5,
                shutdownTimeout: TimeSpan.FromSeconds(2)))
            {
                for (var index = 0; index < 150; index++)
                {
                    logger.Write(
                        AppLogLevel.Info,
                        "Security\r\nForgedSource",
                        $"line {index}: https://stream.kick.com/x?access_token={accessSecret}&quality=best " +
                        $"Authorization: Bearer {bearerSecret}\r\nforged-line " +
                        $"refresh_token={refreshSecret}");
                }

                await logger.FlushAsync();
            }

            var files = Directory.GetFiles(directory, "studio*.log");
            Assert.True(files.Length > 1);
            Assert.True(files.Length <= 5);
            Assert.True(files.All(path => new FileInfo(path).Length <= 1024));
            var content = string.Join("", files.Select(File.ReadAllText));
            Assert.DoesNotContain(accessSecret, content);
            Assert.DoesNotContain(bearerSecret, content);
            Assert.DoesNotContain(refreshSecret, content);
            Assert.Contains("[REDACTED]", content);
            Assert.Contains("\\n", content);
            Assert.Equal(false, content.Contains("\r\nforged-line", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task FileLoggerQueueAndShutdownBoundsAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "StreamlinkVlcStudioTests",
            $"logger-bounds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new FileAppLogger(
            directory,
            capacity: 4,
            maximumFileBytes: 1024 * 1024,
            maximumFileCount: 2,
            shutdownTimeout: TimeSpan.FromMilliseconds(50),
            beforeWriteAsync: _ => releaseWriter.Task);
        try
        {
            for (var index = 0; index < 100; index++)
            {
                logger.Write(AppLogLevel.Debug, "Bounds", $"queued {index}");
            }

            await TestWait.UntilAsync(() => logger.DroppedEntryCount > 0, TimeSpan.FromSeconds(1));
            Assert.True(logger.PendingEntryCount <= 5);
            Assert.True(logger.DroppedEntryCount >= 95);

            var stopwatch = Stopwatch.StartNew();
            await logger.DisposeAsync();
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseWriter.TrySetResult();
            await TestWait.UntilAsync(() => logger.PendingEntryCount == 0, TimeSpan.FromSeconds(1));
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task KickTokenProviderSingleFlightAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutions = 0;
        var provider = new KickTokenProvider(async (_, _, _) =>
        {
            Interlocked.Increment(ref resolutions);
            started.TrySetResult();
            await release.Task;
            return "shared-kick-token";
        });
        var settings = new ChatSettings
        {
            KickClientId = "single-flight-client",
            KickClientSecret = "single-flight-secret"
        };
        var logger = new MemoryLogger();
        using var canceledWaiter = new CancellationTokenSource();
        var canceled = provider.ResolveAsync(settings, logger, canceledWaiter.Token);
        var waiters = Enumerable.Range(0, 32)
            .Select(_ => provider.ResolveAsync(settings, logger))
            .ToArray();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        canceledWaiter.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => _ = await canceled);
        release.TrySetResult();
        var tokens = await Task.WhenAll(waiters);
        Assert.True(tokens.All(token => token == "shared-kick-token"));
        Assert.Equal(1, Volatile.Read(ref resolutions));

        Assert.Equal("shared-kick-token", await provider.ResolveAsync(settings, logger));
        Assert.Equal(1, Volatile.Read(ref resolutions));
    }

    private static async Task LiveChannelSnapshotCachingAsync()
    {
        var requests = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            };
        }));
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.Zero));
        var provider = new LiveChannelSnapshotProvider(httpClient, clock);

        var concurrent = Enumerable.Range(0, 32)
            .Select(_ => provider.GetTwitchAsync(
                "streamer",
                "snapshot-token",
                "snapshot-client",
                CancellationToken.None))
            .ToArray();
        await Task.WhenAll(concurrent);
        Assert.Equal(1, Volatile.Read(ref requests));

        clock.Advance(TimeSpan.FromSeconds(4));
        await provider.GetTwitchAsync(
            "STREAMER",
            "snapshot-token",
            "snapshot-client",
            CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref requests));

        clock.Advance(TimeSpan.FromSeconds(2));
        await provider.GetTwitchAsync(
            "streamer",
            "snapshot-token",
            "snapshot-client",
            CancellationToken.None);
        Assert.Equal(2, Volatile.Read(ref requests));
    }

    private static async Task LiveChannelSnapshotServiceSharingAsync()
    {
        var channelRequests = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("/public/v1/channels", request.RequestUri!.AbsolutePath);
            Interlocked.Increment(ref channelRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [{
                        "slug": "streamer",
                        "profile_picture": "https://files.kick.com/profile.jpg",
                        "stream_title": "Shared snapshot",
                        "category": { "name": "Just Chatting" },
                        "stream": {
                          "is_live": true,
                          "viewer_count": 42,
                          "thumbnail": "https://files.kick.com/thumbnail.jpg"
                        }
                      }]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var tokenResolutions = 0;
        var tokenProvider = new KickTokenProvider((_, _, _) =>
        {
            Interlocked.Increment(ref tokenResolutions);
            return Task.FromResult<string?>("shared-service-token");
        });
        var snapshotProvider = new LiveChannelSnapshotProvider(httpClient);
        var logger = new MemoryLogger();
        var viewerService = new ViewerCountService(logger, snapshotProvider, tokenProvider);
        var metadataService = new StreamMetadataService(
            logger,
            httpClient,
            snapshotProvider,
            tokenProvider);
        var target = StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch);
        var settings = new AppSettings();
        settings.Chat.KickClientId = "shared-service-client";
        settings.Chat.KickClientSecret = "shared-service-secret";

        var viewerTask = viewerService.GetViewerCountAsync(target, settings);
        var metadataTask = metadataService.GetLiveStreamMetadataAsync(target, settings);
        await Task.WhenAll(viewerTask, metadataTask);

        Assert.Equal(ViewerCountState.Available, viewerTask.Result.State);
        Assert.Equal(42, viewerTask.Result.ViewerCount);
        Assert.Equal(StreamMetadataState.Available, metadataTask.Result.State);
        Assert.Equal("https://files.kick.com/thumbnail.jpg", metadataTask.Result.ThumbnailUrl);
        Assert.Equal(1, Volatile.Read(ref tokenResolutions));
        Assert.Equal(1, Volatile.Read(ref channelRequests));
    }

    private static async Task CatalogLoadCoordinatorBoundsAsync()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 15, 21, 0, 0, TimeSpan.Zero));
        var coordinator = new CatalogLoadCoordinator(
            clock,
            maximumEntries: 2,
            timeToLive: TimeSpan.FromMinutes(1),
            retryDelay: TimeSpan.FromSeconds(5));
        var attempts = 0;
        var changedCallbacks = 0;

        Assert.True(coordinator.Ensure(
            "retry-scope",
            () =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromException<CatalogLoadResult>(new HttpRequestException("expected catalog failure"));
            }));
        await TestWait.UntilAsync(() => coordinator.InFlightCount == 0, TimeSpan.FromSeconds(1));
        Assert.Equal(1, attempts);
        Assert.Equal(false, coordinator.Ensure(
            "retry-scope",
            () => Task.FromResult(CatalogLoadResult.Successful())));

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.Ensure(
            "retry-scope",
            () =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(CatalogLoadResult.Successful(changed: true));
            },
            () =>
            {
                Interlocked.Increment(ref changedCallbacks);
                throw new InvalidOperationException("subscriber failures are isolated");
            }));
        await TestWait.UntilAsync(() => coordinator.InFlightCount == 0, TimeSpan.FromSeconds(1));
        Assert.Equal(2, attempts);
        Assert.Equal(1, changedCallbacks);
        Assert.Equal(false, coordinator.Ensure(
            "retry-scope",
            () => Task.FromResult(CatalogLoadResult.Successful())));

        var firstRelease = new TaskCompletionSource<CatalogLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bounded = new CatalogLoadCoordinator(clock, maximumEntries: 2);
        Assert.True(bounded.Ensure("one", () => firstRelease.Task));
        Assert.True(bounded.Ensure("two", () => firstRelease.Task));
        Assert.Equal(false, bounded.Ensure(
            "three",
            () => Task.FromResult(CatalogLoadResult.Successful())));
        Assert.Equal(2, bounded.EntryCount);
        firstRelease.TrySetResult(CatalogLoadResult.Successful());
        await TestWait.UntilAsync(() => bounded.InFlightCount == 0, TimeSpan.FromSeconds(1));
        Assert.True(bounded.Ensure(
            "three",
            () => Task.FromResult(CatalogLoadResult.Successful())));
        Assert.Equal(2, bounded.EntryCount);
        await TestWait.UntilAsync(() => bounded.InFlightCount == 0, TimeSpan.FromSeconds(1));

        var staleRelease = new TaskCompletionSource<CatalogLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleCallbacks = 0;
        var staleCoordinator = new CatalogLoadCoordinator(
            clock,
            maximumEntries: 1,
            timeToLive: TimeSpan.FromMinutes(1),
            retryDelay: TimeSpan.FromSeconds(5));
        Assert.True(staleCoordinator.Ensure(
            "same",
            () => staleRelease.Task,
            () => Interlocked.Increment(ref staleCallbacks)));
        staleCoordinator.InvalidateScopes("same");
        Assert.True(staleCoordinator.Ensure(
            "same",
            () => Task.FromResult(CatalogLoadResult.Successful(changed: true)),
            () => Interlocked.Increment(ref staleCallbacks)));
        await TestWait.UntilAsync(() => staleCoordinator.InFlightCount == 0, TimeSpan.FromSeconds(1));
        staleRelease.TrySetResult(CatalogLoadResult.Failed(changed: true));
        await Task.Yield();
        Assert.Equal(1, staleCallbacks);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(false, staleCoordinator.Ensure(
            "same",
            () => Task.FromResult(CatalogLoadResult.Successful())));

        var evictedScopes = new List<string>();
        var evictionCoordinator = new CatalogLoadCoordinator(
            clock,
            maximumEntries: 2,
            scopeEvicted: evictedScopes.Add);
        Assert.True(evictionCoordinator.Ensure(
            "global",
            () => Task.FromResult(CatalogLoadResult.Successful()),
            preserveFromEviction: true));
        Assert.True(evictionCoordinator.Ensure(
            "channel-one",
            () => Task.FromResult(CatalogLoadResult.Successful())));
        await TestWait.UntilAsync(() => evictionCoordinator.InFlightCount == 0, TimeSpan.FromSeconds(1));
        Assert.True(evictionCoordinator.Ensure(
            "channel-two",
            () => Task.FromResult(CatalogLoadResult.Successful())));
        Assert.SequenceEqual(new[] { "channel-one" }, evictedScopes);

        var safeNotificationCount = 0;
        EventHandler throwing = (_, _) => throw new InvalidOperationException("expected subscriber failure");
        EventHandler succeeding = (_, _) => Interlocked.Increment(ref safeNotificationCount);
        CatalogLoadCoordinator.RaiseSafely(throwing + succeeding, coordinator);
        Assert.Equal(1, safeNotificationCount);
    }

    private static Task ChatEmoteCatalogScoping()
    {
        const string code = "ScopedEmote";
        var catalog = new DockedChatEmoteCatalog();
        Assert.True(catalog.AddEmote(
            PlatformKind.Kick,
            "alpha",
            code,
            "https://files.kick.com/alpha.png",
            28,
            28));
        Assert.True(catalog.AddEmote(
            PlatformKind.Kick,
            "beta",
            code,
            "https://files.kick.com/beta.png",
            28,
            28));
        Assert.True(catalog.AddEmote(
            PlatformKind.Twitch,
            "alpha",
            code,
            "https://static-cdn.jtvnw.net/alpha.png",
            28,
            28));
        Assert.True(catalog.AddEmote(
            PlatformKind.Twitch,
            "",
            "GlobalOnly",
            "https://static-cdn.jtvnw.net/global.png",
            28,
            28));

        var kickAlpha = CreateCatalogMessage(PlatformKind.Kick, "alpha");
        var kickBeta = CreateCatalogMessage(PlatformKind.Kick, "beta");
        var twitchAlpha = CreateCatalogMessage(PlatformKind.Twitch, "alpha");
        Assert.True(catalog.TryGet(kickAlpha, code, out var kickAlphaEmote));
        Assert.True(catalog.TryGet(kickBeta, code, out var kickBetaEmote));
        Assert.True(catalog.TryGet(twitchAlpha, code, out var twitchAlphaEmote));
        Assert.Equal("https://files.kick.com/alpha.png", kickAlphaEmote.ImageUrl);
        Assert.Equal("https://files.kick.com/beta.png", kickBetaEmote.ImageUrl);
        Assert.Equal("https://static-cdn.jtvnw.net/alpha.png", twitchAlphaEmote.ImageUrl);
        Assert.True(catalog.TryGet(twitchAlpha, "GlobalOnly", out _));
        Assert.Equal(false, catalog.TryGet(kickAlpha, "GlobalOnly", out _));

        var lruMessage = CreateCatalogMessage(PlatformKind.Kick, "lru-channel");
        for (var index = 0; index < 4_096; index++)
        {
            catalog.EnsureForMessage(lruMessage with
            {
                Emotes =
                [
                    new ChatEmote(
                        0,
                        0,
                        $"MessageEmote{index}",
                        $"https://files.kick.com/message-emote-{index}.png")
                ]
            });
        }

        Assert.True(catalog.TryGet(lruMessage, "MessageEmote0", out _));
        catalog.EnsureForMessage(lruMessage with
        {
            Emotes = [new ChatEmote(0, 0, "MessageEmote4096", "https://files.kick.com/message-emote-4096.png")]
        });
        Assert.Equal(4_096, catalog.MessageSuppliedEmoteCountForTest);
        Assert.True(catalog.TryGet(lruMessage, "MessageEmote0", out _));
        Assert.Equal(false, catalog.TryGet(lruMessage, "MessageEmote1", out _));
        Assert.True(catalog.TryGet(lruMessage, "MessageEmote4096", out _));
        return Task.CompletedTask;
    }

    private static ChatMessage CreateCatalogMessage(PlatformKind platform, string channel) =>
        new(platform, channel, "viewer", "message", DateTimeOffset.UtcNow);

    private static Task EmojiGraphemeAndCacheBounds()
    {
        var segments = DockedChatMessageTextBlock.SegmentTextForTest("a\U0001F600\U0001F680b");
        Assert.Equal(4, segments.Count);
        Assert.Equal(("a", false), segments[0]);
        Assert.Equal(("\U0001F600", true), segments[1]);
        Assert.Equal(("\U0001F680", true), segments[2]);
        Assert.Equal(("b", false), segments[3]);

        var family = DockedChatMessageTextBlock.SegmentTextForTest("x\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466y");
        Assert.Equal(3, family.Count);
        Assert.Equal(("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466", true), family[1]);

        DockedChatMessageTextBlock.ClearEmojiImageCacheForTest();
        try
        {
            for (var index = 0; index < 600; index++)
            {
                DockedChatMessageTextBlock.AddEmojiImageCacheEntryForTest($"emoji-{index}", 32);
            }

            Assert.Equal(512, DockedChatMessageTextBlock.EmojiImageCacheCountForTest);
        }
        finally
        {
            DockedChatMessageTextBlock.ClearEmojiImageCacheForTest();
        }

        return Task.CompletedTask;
    }

    private static Task ToastThumbnailStorageBounds()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "StreamlinkVlcStudioTests",
            $"toast-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var origin = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);
            for (var index = 0; index < 140; index++)
            {
                var path = Path.Combine(directory, $"thumbnail-{index:D3}.png");
                using (var stream = File.Create(path))
                {
                    stream.SetLength(600 * 1024);
                }

                File.SetLastWriteTimeUtc(path, origin.AddMinutes(index));
            }

            ToastLiveNotificationService.PruneThumbnailStorageForTest(directory);
            var retained = Directory.GetFiles(directory, "*.png")
                .Select(path => new FileInfo(path))
                .ToArray();
            Assert.True(retained.Length <= 128);
            Assert.True(retained.Sum(file => file.Length) <= 64L * 1024 * 1024);
            Assert.Equal(false, File.Exists(Path.Combine(directory, "thumbnail-000.png")));
            Assert.True(File.Exists(Path.Combine(directory, "thumbnail-139.png")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task ProviderPlaylistValidation()
    {
        Assert.True(ProviderUriPolicy.IsApprovedReplayUri(
            new Uri("https://d2e2de1etea730.cloudfront.net/archive/index.m3u8"),
            PlatformKind.Twitch));
        Assert.Equal(false, ProviderUriPolicy.IsApprovedReplayUri(
            new Uri("https://cloudfront.net.attacker.example/archive/index.m3u8"),
            PlatformKind.Twitch));
        Assert.Equal(false, ProviderUriPolicy.IsApprovedReplayUri(
            new Uri("https://127.0.0.1/archive/index.m3u8"),
            PlatformKind.Twitch));

        Assert.True(TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation(
            "https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg",
            out var host,
            out var specialId));
        Assert.Equal("d2e2de1etea730.cloudfront.net", host);
        Assert.Equal("abc_def", specialId);
        foreach (var spoof in new[]
                 {
                     "http://d2e2de1etea730.cloudfront.net/abc/storyboards/0.jpg",
                     "https://user@d2e2de1etea730.cloudfront.net/abc/storyboards/0.jpg",
                     "https://d2e2de1etea730.cloudfront.net:444/abc/storyboards/0.jpg",
                     "https://cloudfront.net.attacker.example/abc/storyboards/0.jpg",
                     "https://d2e2de1etea730.cloudfront.net/abc/not-storyboards/0.jpg",
                     "https://d2e2de1etea730.cloudfront.net/abc%2Fescape/storyboards/0.jpg"
                 })
        {
            Assert.Equal(false, TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation(spoof, out _, out _));
        }

        var playlistUri = new Uri(
            "https://d2e2de1etea730.cloudfront.net/archive/chunked/index-dvr.m3u8");
        var rewritten = TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(
            "#EXTM3U\n#EXTINF:1,\nsegment.ts\n",
            playlistUri);
        Assert.Contains(
            "https://d2e2de1etea730.cloudfront.net/archive/chunked/segment.ts",
            rewritten);
        Assert.Throws<InvalidDataException>(() => TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(
            "#EXTM3U\n#EXTINF:1,\nfile:///c:/secret.txt\n",
            playlistUri));
        Assert.Throws<InvalidDataException>(() => TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"https://cloudfront.net.attacker.example/key\"\n",
            playlistUri));
        Assert.Throws<InvalidDataException>(() => TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=key.bin\n",
            playlistUri));
        return Task.CompletedTask;
    }

    private static Task ExactKickInputValidation()
    {
        const string exactPayload =
            "{\"event\":\"App\\\\Events\\\\ChatMessageEvent\",\"data\":\"{\\\"content\\\":\\\"hello\\\",\\\"sender\\\":{\\\"username\\\":\\\"viewer\\\"}}\"}";
        Assert.NotNull(KickPusherParser.TryParse(exactPayload, "channel"));
        Assert.Equal<ChatMessage?>(null, KickPusherParser.TryParse(
            exactPayload.Replace("ChatMessageEvent", "NotChatMessageEventSpoof", StringComparison.Ordinal),
            "channel"));
        Assert.Equal<ChatMessage?>(null, KickPusherParser.TryParse(
            "{\"event\":123,\"data\":{\"content\":\"hello\"}}",
            "channel"));

        using (var objectContent = System.Text.Json.JsonDocument.Parse(
                   "{\"content\":{\"text\":\"spoof\"},\"sender\":{\"username\":\"viewer\"}}"))
        {
            Assert.Equal<ChatMessage?>(null, KickPusherParser.TryParseMessageData(
                objectContent.RootElement,
                "channel"));
        }

        var longTitle = new string('T', 300);
        var titlePayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = KickEventNameValidator.LegacyChatMessage,
            data = System.Text.Json.JsonSerializer.Serialize(new
            {
                content = "badge",
                sender = new
                {
                    username = "viewer",
                    badges = new[]
                    {
                        new { type = "vip", title = longTitle + "\r\nignored" }
                    }
                }
            })
        });
        var titled = KickPusherParser.TryParse(titlePayload, "channel");
        Assert.NotNull(titled);
        var normalizedBadgeTitle = titled!.Badges!.Single().Title!;
        Assert.Equal(128, normalizedBadgeTitle.Length);
        Assert.DoesNotContain("\r", normalizedBadgeTitle);
        Assert.DoesNotContain("\n", normalizedBadgeTitle);

        Assert.True(StreamInputParser.TryNormalizeChannelSlug(
            PlatformKind.Kick,
            "@some-channel/",
            out var channel));
        Assert.Equal("some-channel", channel);
        Assert.Equal(false, StreamInputParser.TryNormalizeChannelSlug(
            PlatformKind.Kick,
            "bad slug",
            out _));
        Assert.Equal(false, StreamInputParser.TryNormalizeChannelSlug(
            PlatformKind.Kick,
            "login",
            out _));

        var sourceSlugs = new List<string>
        {
            "xqc",
            "bad slug",
            "https://kick.com/offline",
            "https://www.twitch.tv/summit1g"
        };
        var settings = new FollowedChannelsSettings { KickChannelSlugs = sourceSlugs };
        sourceSlugs[0] = "mutated";
        Assert.SequenceEqual(new[] { "xqc", "offline" }, settings.KickChannelSlugs);
        return Task.CompletedTask;
    }

    private static Task ExecutablePathValidation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "StreamlinkVlcStudioTests",
            $"executable-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "tool.exe");
            File.WriteAllBytes(executable, []);
            var pathValue = string.Join(
                Path.PathSeparator,
                ".",
                "\"unterminated",
                $"\"{directory}\"");
            Assert.Equal(executable, ExecutableResolver.FindOnPath("tool.exe", pathValue));
            Assert.Equal<string?>(null, ExecutableResolver.FindOnPath("tool.exe", "."));
            Assert.Equal<string?>(null, ExecutableResolver.FindOnPath("..\\tool.exe", directory));
            Assert.Equal<string?>(null, ExecutableResolver.FindOnPath("tool.exe", $"\"{directory}\"trailing"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task VlcPluginCacheManifestValidation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "StreamlinkVlcStudioTests",
            $"vlc-cache-manifest-{Guid.NewGuid():N}");
        var vlcDirectory = Path.Combine(root, "vlc");
        var pluginRoot = Path.Combine(root, "plugins");
        var pluginDirectory = Path.Combine(pluginRoot, "spu");
        Directory.CreateDirectory(vlcDirectory);
        Directory.CreateDirectory(pluginDirectory);
        try
        {
            var libVlcPath = Path.Combine(vlcDirectory, "libvlc.dll");
            var generatorPath = Path.Combine(vlcDirectory, "vlc-cache-gen.exe");
            var pluginPath = Path.Combine(pluginDirectory, "overlay.dll");
            File.WriteAllBytes(libVlcPath, [1, 2, 3]);
            File.WriteAllBytes(generatorPath, [4, 5, 6]);
            File.WriteAllBytes(pluginPath, [7, 8, 9]);
            File.WriteAllText(Path.Combine(pluginRoot, "plugins.dat"), "cache");

            VlcOverlayPluginRuntimeFactory.WriteCurrentCacheManifestForTest(
                vlcDirectory,
                pluginRoot);
            Assert.True(VlcOverlayPluginRuntimeFactory.IsCurrentCacheManifestForTest(
                vlcDirectory,
                pluginRoot));
            Assert.True(File.Exists(Path.Combine(
                pluginRoot,
                VlcOverlayPluginRuntimeFactory.CacheManifestFileName)));

            File.WriteAllBytes(pluginPath, [9, 8, 7]);
            Assert.Equal(false, VlcOverlayPluginRuntimeFactory.IsCurrentCacheManifestForTest(
                vlcDirectory,
                pluginRoot));
            VlcOverlayPluginRuntimeFactory.WriteCurrentCacheManifestForTest(vlcDirectory, pluginRoot);

            File.WriteAllBytes(generatorPath, [6, 5, 4]);
            Assert.Equal(false, VlcOverlayPluginRuntimeFactory.IsCurrentCacheManifestForTest(
                vlcDirectory,
                pluginRoot));
            VlcOverlayPluginRuntimeFactory.WriteCurrentCacheManifestForTest(vlcDirectory, pluginRoot);

            File.WriteAllBytes(libVlcPath, [3, 2, 1]);
            Assert.Equal(false, VlcOverlayPluginRuntimeFactory.IsCurrentCacheManifestForTest(
                vlcDirectory,
                pluginRoot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task KickWebhookAuthenticationAsync()
    {
        using var oldKey = RSA.Create(2048);
        using var currentKey = RSA.Create(2048);
        var keyRequestCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            var key = Interlocked.Increment(ref keyRequestCount) == 1 ? oldKey : currentKey;
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                data = new { public_key = key.ExportSubjectPublicKeyInfoPem() }
            });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }));
        var now = new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var directory = CreateSettingsTestDirectory();
        await using var server = new KickWebhookChatServer(
            new KickOfficialChatReplayStore(directory),
            new MemoryLogger(),
            port: 0,
            httpClient: httpClient,
            timeProvider: clock);

        var fresh = CreateSignedKickWebhookRequest(currentKey, "message-1", now, "{}");
        Assert.Equal(
            KickWebhookChatServer.WebhookAuthenticationResult.Valid,
            await server.AuthenticateRequestAsync(fresh, CancellationToken.None));
        Assert.Equal(2, keyRequestCount);
        Assert.Equal(
            KickWebhookChatServer.WebhookAuthenticationResult.Replay,
            await server.AuthenticateRequestAsync(fresh, CancellationToken.None));

        var stale = CreateSignedKickWebhookRequest(currentKey, "stale", now - TimeSpan.FromMinutes(6), "{}");
        Assert.Equal(
            KickWebhookChatServer.WebhookAuthenticationResult.Invalid,
            await server.AuthenticateRequestAsync(stale, CancellationToken.None));
        Assert.Equal(2, keyRequestCount);

        clock.Advance(TimeSpan.FromMinutes(11));
        var replayExpired = CreateSignedKickWebhookRequest(currentKey, "message-1", clock.GetUtcNow(), "{}");
        Assert.Equal(
            KickWebhookChatServer.WebhookAuthenticationResult.Valid,
            await server.AuthenticateRequestAsync(replayExpired, CancellationToken.None));

        clock.Advance(TimeSpan.FromHours(25));
        var keyExpired = CreateSignedKickWebhookRequest(currentKey, "message-2", clock.GetUtcNow(), "{}");
        Assert.Equal(
            KickWebhookChatServer.WebhookAuthenticationResult.Valid,
            await server.AuthenticateRequestAsync(keyExpired, CancellationToken.None));
        Assert.Equal(3, keyRequestCount);
        Directory.Delete(directory, recursive: true);
    }

    private static async Task KickWebhookHttpSurfaceAsync()
    {
        var directory = CreateSettingsTestDirectory();
        try
        {
            await using var server = new KickWebhookChatServer(
                new KickOfficialChatReplayStore(directory),
                new MemoryLogger(),
                port: 0);
            Assert.True(server.Start());

            var options = await BrowserCaptureTestClient.SendRawRequestAsync(
                server.ListenerPort,
                $"OPTIONS {KickWebhookChatServer.WebhookPath} HTTP/1.1\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            Assert.True(options.StartsWith("HTTP/1.1 404 Not Found", StringComparison.Ordinal));
            Assert.DoesNotContain("Access-Control-Allow-Origin", options);

            var unsupported = await BrowserCaptureTestClient.SendRawRequestAsync(
                server.ListenerPort,
                $"POST {KickWebhookChatServer.WebhookPath} HTTP/1.1\r\nTransfer-Encoding: chunked\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            Assert.True(unsupported.StartsWith("HTTP/1.1 501 Not Implemented", StringComparison.Ordinal));

            var oversized = await BrowserCaptureTestClient.SendRawRequestAsync(
                server.ListenerPort,
                $"POST {KickWebhookChatServer.WebhookPath} HTTP/1.1\r\nContent-Length: 300000\r\nConnection: close\r\n\r\n");
            Assert.True(oversized.StartsWith("HTTP/1.1 413 Payload Too Large", StringComparison.Ordinal));

            var stalledClients = new List<System.Net.Sockets.TcpClient>();
            try
            {
                for (var index = 0; index < 32; index++)
                {
                    var client = new System.Net.Sockets.TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, server.ListenerPort);
                    stalledClients.Add(client);
                }

                await TestWait.UntilAsync(
                    () => server.AvailableClientAdmissionsForTest == 0,
                    TimeSpan.FromSeconds(2));
                var overloaded = await BrowserCaptureTestClient.SendRawRequestAsync(
                    server.ListenerPort,
                    $"POST {KickWebhookChatServer.WebhookPath} HTTP/1.1\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                Assert.True(overloaded.StartsWith("HTTP/1.1 503 Service Unavailable", StringComparison.Ordinal));
            }
            finally
            {
                foreach (var client in stalledClients)
                {
                    client.Dispose();
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LocalHttpRequest CreateSignedKickWebhookRequest(
        RSA key,
        string messageId,
        DateTimeOffset timestamp,
        string body)
    {
        var timestampText = timestamp.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var signedBytes = Encoding.UTF8.GetBytes($"{messageId}.{timestampText}.{body}");
        var signature = Convert.ToBase64String(key.SignData(
            signedBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
        return new LocalHttpRequest(
            "POST",
            KickWebhookChatServer.WebhookPath,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Kick-Event-Message-Id"] = messageId,
                ["Kick-Event-Message-Timestamp"] = timestampText,
                ["Kick-Event-Signature"] = signature
            },
            bodyBytes);
    }

    private static async Task BoundedByteReaderFilesAndCancellationAsync()
    {
        const int maximum = 4;
        var path = Path.Combine(Path.GetTempPath(), $"svs-bounded-reader-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(path, [11, 12, 13]);
            var bytes = await BoundedByteReader.ReadFileAsync(path, maximum);
            Assert.NotNull(bytes);
            Assert.SequenceEqual<byte>([11, 12, 13], bytes!);

            await File.WriteAllBytesAsync(path, [11, 12, 13, 14, 15]);
            Assert.Equal<byte[]?>(null, await BoundedByteReader.ReadFileAsync(path, maximum));
        }
        finally
        {
            File.Delete(path);
        }

        using var cancellation = new CancellationTokenSource();
        using var content = new StreamContent(new CancellationByteStream());
        var pending = BoundedByteReader.ReadAsync(content, maximum, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => _ = await pending);
    }
}

internal static class TestReplayUrlSecurity
{
    internal static ReplayUrlSecurityValidator PublicValidator { get; } = new(
        static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
}

internal readonly record struct WebSocketFrame(
    byte[] Bytes,
    WebSocketMessageType MessageType,
    bool EndOfMessage);

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return utcNow;
        }
    }

    internal void Advance(TimeSpan duration)
    {
        lock (gate)
        {
            utcNow += duration;
        }
    }
}

internal sealed class ScriptedWebSocket(params WebSocketFrame[] frames) : WebSocket
{
    private readonly Queue<WebSocketFrame> frames = new(frames);
    private readonly StringBuilder sentText = new();
    private WebSocketCloseStatus? closeStatus;
    private string? closeStatusDescription;
    private WebSocketState state = WebSocketState.Open;

    public string SentText => sentText.ToString();
    public override WebSocketCloseStatus? CloseStatus => closeStatus;
    public override string? CloseStatusDescription => closeStatusDescription;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;

    public override void Abort() => state = WebSocketState.Aborted;

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        this.closeStatus = closeStatus;
        closeStatusDescription = statusDescription;
        state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) =>
        CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose() => state = WebSocketState.Closed;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (frames.Count == 0)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var frame = frames.Dequeue();
        Assert.True(frame.Bytes.Length <= buffer.Count);
        frame.Bytes.CopyTo(buffer.AsSpan());
        return new WebSocketReceiveResult(
            frame.Bytes.Length,
            frame.MessageType,
            frame.EndOfMessage);
    }

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        Assert.Equal(WebSocketMessageType.Text, messageType);
        sentText.Append(Encoding.UTF8.GetString(buffer));
        return Task.CompletedTask;
    }
}

internal sealed class FakeWindowHitTester : IWindowHitTester
{
    public IntPtr PointWindow { get; set; }
    public IntPtr ChildWindow { get; set; }
    public IntPtr RootWindow { get; set; }
    public IntPtr RootOwnerWindow { get; set; }

    public IntPtr WindowFromPoint(int screenX, int screenY) => PointWindow;
    public IntPtr GetRootWindow(IntPtr hwnd) => RootWindow;
    public IntPtr GetRootOwnerWindow(IntPtr hwnd) => RootOwnerWindow;
    public bool IsChild(IntPtr parent, IntPtr child) => child == ChildWindow;
}

internal class NonSeekableByteStream : Stream
{
    private byte[] bytes;
    private int position;

    public NonSeekableByteStream(byte[] bytes)
    {
        this.bytes = bytes;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadCore(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        return ReadCore(buffer);
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadCore(buffer.Span));
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    protected virtual int ReadCore(Span<byte> buffer)
    {
        if (position >= bytes.Length)
        {
            return 0;
        }

        var count = Math.Min(buffer.Length, bytes.Length - position);
        bytes.AsSpan(position, count).CopyTo(buffer);
        position += count;
        return count;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class TrackingByteStream : NonSeekableByteStream
{
    public TrackingByteStream(byte[] bytes)
        : base(bytes)
    {
    }

    public int ReadCount { get; private set; }

    protected override int ReadCore(Span<byte> buffer)
    {
        ReadCount++;
        return base.ReadCore(buffer);
    }
}

internal sealed class GrowingByteStream : NonSeekableByteStream
{
    private readonly byte[] afterFirstRead;
    private bool hasGrown;

    public GrowingByteStream(byte[] initial, byte[] afterFirstRead)
        : base(initial)
    {
        this.afterFirstRead = afterFirstRead;
    }

    public int ReadCount { get; private set; }

    protected override int ReadCore(Span<byte> buffer)
    {
        ReadCount++;
        if (ReadCount == 2)
        {
            hasGrown = true;
        }

        return hasGrown
            ? ReadFrom(afterFirstRead, buffer)
            : base.ReadCore(buffer);
    }

    private int ReadFrom(byte[] source, Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, source.Length);
        source.AsSpan(0, count).CopyTo(buffer);
        return count;
    }
}

internal sealed class CancellationByteStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => 0;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(Span<byte> buffer) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
