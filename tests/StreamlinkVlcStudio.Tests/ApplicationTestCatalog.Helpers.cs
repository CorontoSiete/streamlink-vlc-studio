internal static partial class ApplicationTestCatalog
{
    static HttpResponseMessage CreateTwitchTokenValidationResponse(string clientId) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"client_id":"{{clientId}}","login":"viewer","user_id":"1234","scopes":[],"expires_in":3600}""",
                Encoding.UTF8,
                "application/json")
        };

    static string QuotePowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    static async Task<(int ExitCode, string Output, string Error)> RunPowerShellAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start Windows PowerShell.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between HasExited and Kill.
            }

            await process.WaitForExitAsync();
            var timedOutOutput = await outputTask;
            var timedOutError = await errorTask;
            throw new TimeoutException(
                $"PowerShell did not exit within {timeout}. Output: {timedOutOutput} Error: {timedOutError}".Trim());
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }

    static void AssertOptionValue(IReadOnlyList<string> arguments, string option, string expectedValue)
    {
        var optionIndex = Array.IndexOf(arguments.ToArray(), option);
        Assert.True(optionIndex >= 0, $"Expected argument '{option}'.");
        Assert.True(optionIndex + 1 < arguments.Count, $"Expected value after '{option}'.");
        Assert.Equal(expectedValue, arguments[optionIndex + 1]);
    }

    static void SetKickClientBackfillState(
        KickChatClient client,
        string channel,
        string? channelId,
        string chatroomId)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(KickChatClient).GetField("connectedChannel", flags)!.SetValue(client, channel);
        typeof(KickChatClient).GetField("currentChannelId", flags)!.SetValue(client, channelId);
        typeof(KickChatClient).GetField("currentChatroomId", flags)!.SetValue(client, chatroomId);
    }

    static void SetTwitchPredictionContext(TwitchChatClient client)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(TwitchChatClient).GetField("predictionBroadcasterId", flags)!.SetValue(client, "broadcaster-id");
        typeof(TwitchChatClient).GetField("predictionAccessToken", flags)!.SetValue(client, "twitch-token");
        typeof(TwitchChatClient).GetField("predictionClientId", flags)!.SetValue(client, "client-id");
        typeof(TwitchChatClient).GetProperty(
            nameof(TwitchChatClient.PredictionAccess),
            flags | BindingFlags.Public)!.SetValue(
                client,
                new TwitchPredictionAccessState(
                    true,
                    true,
                    "Prediction controls are available.",
                    BroadcasterId: "broadcaster-id"));
    }

    static void MarkReplayClockSeekConfirmed(StreamTabViewModel tab, TimeSpan? elapsedSinceAnchor = null)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(StreamTabViewModel).GetField("replayClockAnchorAwaitingSeekConfirmation", flags)!.SetValue(tab, false);
        if (elapsedSinceAnchor is { } elapsed)
        {
            typeof(StreamTabViewModel)
                .GetField("replayClockAnchorObservedAtUtc", flags)!
                .SetValue(tab, DateTimeOffset.UtcNow - elapsed);
        }
    }

    static void InvokeReplayClockUpdate(StreamTabViewModel tab)
    {
        var updateClock = typeof(StreamTabViewModel).GetMethod(
            "UpdateReplayClock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(updateClock);
        updateClock!.Invoke(tab, []);
    }

    static async Task StopReplayClockPollingAsync(StreamTabViewModel tab)
    {
        var stopClock = typeof(StreamTabViewModel).GetMethod(
            "StopReplayClockPollingAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(stopClock);
        await ((Task)stopClock!.Invoke(tab, [])!).WaitAsync(TimeSpan.FromSeconds(1));
    }

    static bool DockedChatMessagesContain(StreamTabViewModel tab, string message)
    {
        return SnapshotDockedChatMessages(tab).Any(candidate => candidate.Message == message);
    }

    static bool DockedChatMessagesContainText(StreamTabViewModel tab, string text)
    {
        return SnapshotDockedChatMessages(tab).Any(candidate => candidate.Message.Contains(text, StringComparison.Ordinal));
    }

    static ChatMessage[] SnapshotDockedChatMessages(StreamTabViewModel tab)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return tab.DockedChatMessages.ToArray();
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(10);
            }
        }

        return tab.DockedChatMessages.ToArray();
    }

    static StreamTabViewModel CreateTestStreamTab() =>
        TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());

    static FollowedLiveStream CreateTestFollowedStream(
        PlatformKind platform,
        string channel,
        string? displayName = null,
        int viewerCount = 100)
    {
        var url = platform == PlatformKind.Twitch
            ? $"https://www.twitch.tv/{channel}"
            : $"https://kick.com/{channel}";
        var thumbnailUrl = platform == PlatformKind.Twitch
            ? $"https://static-cdn.jtvnw.net/previews-ttv/live_user_{channel}.jpg"
            : $"https://files.kick.com/{channel}.jpg";
        return new FollowedLiveStream(
            platform,
            channel,
            displayName ?? channel,
            "live now",
            "Just Chatting",
            viewerCount,
            thumbnailUrl,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            false,
            "en",
            url);
    }

    static string CreateTempTestDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "StreamlinkVlcStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static void DeleteTempTestDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var fullPath = Path.GetFullPath(directory);
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StreamlinkVlcStudioTests"));
        Assert.True(
            fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase),
            $"Refusing to delete unexpected temp directory '{fullPath}'.");
        Directory.Delete(fullPath, recursive: true);
    }

    static string CreateValidOverlayDirectory(string overlayDirectory)
    {
        var buildDirectory = Path.Combine(overlayDirectory, "build");
        Directory.CreateDirectory(buildDirectory);
        var sourceBuildDirectory = Path.Combine(
            FindRepoRoot(),
            "src",
            "StreamlinkVlcStudio.Infrastructure",
            "Vlc",
            "BundledOverlay",
            "build");
        File.Copy(
            Path.Combine(sourceBuildDirectory, "libmyoverlay_plugin.dll"),
            Path.Combine(buildDirectory, "libmyoverlay_plugin.dll"),
            overwrite: true);
        File.Copy(
            Path.Combine(sourceBuildDirectory, "vlc_chat_overlay.exe"),
            Path.Combine(buildDirectory, "vlc_chat_overlay.exe"),
            overwrite: true);
        return overlayDirectory;
    }

    static string FindRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "scripts", "package-release.ps1")) &&
                    File.Exists(Path.Combine(directory.FullName, "StreamlinkVlcStudio.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    static void AssertBundledBadgeImageUrl(string? imageUrl, string badgeDirectory, string relativePath)
    {
        Assert.True(
            IsBundledBadgeImageUrl(imageUrl, badgeDirectory, relativePath),
            $"Expected bundled badge image '{badgeDirectory}/{relativePath}', got '{imageUrl}'.");
    }

    static bool IsBundledBadgeImageUrl(string? imageUrl, string badgeDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) ||
            !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsFile ||
            !File.Exists(uri.LocalPath))
        {
            return false;
        }

        var normalizedUrl = imageUrl.Replace('\\', '/');
        var normalizedRelativePath = relativePath.Replace('\\', '/');
        return normalizedUrl.Contains($"/{badgeDirectory}/", StringComparison.OrdinalIgnoreCase) &&
            normalizedUrl.EndsWith($"/{normalizedRelativePath}", StringComparison.OrdinalIgnoreCase);
    }

    static string ExtractBundledBadgeManifestPath(string badgeDirectory)
    {
        var extractMethod = typeof(BundledBadgeAssets).GetMethod(
            "ExtractBadgeManifestPath",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(extractMethod);

        var manifestPath = (string?)extractMethod!.Invoke(null, [badgeDirectory]);
        Assert.True(
            !string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath),
            $"Expected extracted {badgeDirectory} manifest, got '{manifestPath}'.");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.True(!string.IsNullOrWhiteSpace(localAppData), "Expected a LocalApplicationData path.");
        var expectedRoot = Path.GetFullPath(Path.Combine(
            localAppData,
            "StreamlinkVlcStudio",
            "BundledBadgeAssets",
            badgeDirectory)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var fullManifestPath = Path.GetFullPath(manifestPath!);
        Assert.True(
            fullManifestPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase),
            $"Expected {badgeDirectory} manifest under '{expectedRoot}', got '{fullManifestPath}'.");
        return manifestPath!;
    }

    static void AssertManifestParsesFromUtf8Bytes(string manifestPath)
    {
        var bytes = File.ReadAllBytes(manifestPath);
        Assert.True(
            bytes.Length < 3 ||
            bytes[0] != 0xEF ||
            bytes[1] != 0xBB ||
            bytes[2] != 0xBF,
            $"Expected extracted manifest without UTF-8 BOM: '{manifestPath}'.");

        using var document = JsonDocument.Parse(bytes);
        Assert.True(
            document.RootElement.TryGetProperty("entries", out var entries) &&
            entries.ValueKind == JsonValueKind.Array,
            $"Expected parseable badge entries array in '{manifestPath}'.");
    }

    static void AssertManifestImageExists(string manifestPath, string relativeImagePath)
    {
        var root = Path.GetDirectoryName(manifestPath);
        Assert.True(!string.IsNullOrWhiteSpace(root), $"Expected manifest root for '{manifestPath}'.");
        var imagePath = Path.GetFullPath(Path.Combine(
            root!,
            relativeImagePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(File.Exists(imagePath), $"Expected bundled badge image '{imagePath}'.");
    }

    static void AssertManifestEntryTitle(string manifestPath, string id, string expectedTitle)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var entries = document.RootElement.GetProperty("entries");
        var entry = entries.EnumerateArray().SingleOrDefault(item =>
            item.ValueKind == JsonValueKind.Object &&
            string.Equals(item.GetProperty("id").GetString(), id, StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Object, entry.ValueKind);
        Assert.Equal(expectedTitle, entry.GetProperty("title").GetString());
    }

    static TwitchPrediction CreateTestPrediction(
        string id,
        TwitchPredictionStatus status,
        string title,
        IReadOnlyList<TwitchPredictionOutcome> outcomes,
        string? winningOutcomeId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var locksAtUtc = status is TwitchPredictionStatus.Active or TwitchPredictionStatus.Locked
            ? now.AddMinutes(status == TwitchPredictionStatus.Active ? 2 : 0)
            : (DateTimeOffset?)null;
        var endedAtUtc = status is TwitchPredictionStatus.Resolved or TwitchPredictionStatus.Canceled
            ? now
            : (DateTimeOffset?)null;

        return new TwitchPrediction(
            id,
            "broadcaster-1",
            "streamer",
            "Streamer",
            title,
            winningOutcomeId,
            outcomes,
            120,
            status,
            now,
            locksAtUtc,
            endedAtUtc);
    }

    static TwitchPredictionOutcome[] CreateTestPredictionOutcomes(
        int yesUsers = 1,
        int yesPoints = 100,
        int noUsers = 0,
        int noPoints = 0)
    {
        return
        [
            new TwitchPredictionOutcome("outcome-1", "Yes", "blue", yesUsers, yesPoints, []),
        new TwitchPredictionOutcome("outcome-2", "No", "pink", noUsers, noPoints, [])
        ];
    }

    static NamedPipeServerStream CreateNativeOverlayPipeServer(string pipeName)
    {
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    static async Task<byte[]> ReadNativeOverlayPipeMessageAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync();
        var buffer = new byte[40];
        await server.ReadExactlyAsync(buffer);
        return buffer;
    }

    static async Task<byte[]?> TryReadNativeOverlayPipeFullMessageFromConnectedStreamAsync(Stream stream)
    {
        var header = new byte[36];
        var initialRead = await stream.ReadAsync(header);
        if (initialRead == 0)
        {
            return null;
        }

        if (initialRead < header.Length)
        {
            await stream.ReadExactlyAsync(header.AsMemory(initialRead));
        }

        var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        var message = new byte[header.Length + payloadSize];
        Buffer.BlockCopy(header, 0, message, 0, header.Length);
        if (payloadSize > 0)
        {
            await stream.ReadExactlyAsync(message.AsMemory(header.Length, (int)payloadSize));
        }

        return message;
    }

    static async Task<IReadOnlyList<byte[]>> ReadNativeOverlayPipeConnectionMessagesAsync(
        string pipeName,
        TimeSpan timeout)
    {
        var messages = new List<byte[]>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        await using var server = CreateNativeOverlayPipeServer(pipeName);
        await server.WaitForConnectionAsync().WaitAsync(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            byte[]? message;
            try
            {
                message = await TryReadNativeOverlayPipeFullMessageFromConnectedStreamAsync(server)
                    .WaitAsync(remaining);
            }
            catch (IOException)
            {
                // A superseded overlay frame may cancel its client while a message is
                // still being written. Discard that partial connection and let the
                // caller accept the next complete frame.
                break;
            }

            if (message is null)
            {
                break;
            }

            messages.Add(message);
        }

        return messages;
    }

    static async Task<(byte[] Primary, byte[] Followup)> ReadNativeOverlayPipeMessagePairMatchingAsync(
        string pipeName,
        Func<byte[], bool> primaryPredicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var messages = await ReadNativeOverlayPipeConnectionMessagesAsync(pipeName, remaining);
            if (messages.Count < 2)
            {
                continue;
            }

            var primary = messages[0];
            if (!primaryPredicate(primary))
            {
                continue;
            }

            return (primary, messages[1]);
        }

        throw new TimeoutException("Timed out waiting for matching native overlay pipe messages.");
    }

    static async Task<byte[]> ReadNativeOverlayPipeMatchingMessageAsync(
        string pipeName,
        Func<byte[], bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var messages = await ReadNativeOverlayPipeConnectionMessagesAsync(pipeName, remaining);
            var match = messages.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }
        }

        throw new TimeoutException("Timed out waiting for a matching native overlay pipe message.");
    }

    static async Task<IReadOnlyList<byte[]>> ReadNativeOverlayPipeMessagesUntilAsync(
        string pipeName,
        Func<IReadOnlyList<byte[]>, bool> predicate,
        TimeSpan timeout)
    {
        var messages = new List<byte[]>();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var connectionMessages = await ReadNativeOverlayPipeConnectionMessagesAsync(pipeName, remaining);
            messages.AddRange(connectionMessages);
            if (predicate(messages))
            {
                return messages;
            }
        }

        throw new TimeoutException("Timed out waiting for native overlay pipe messages.");
    }

    static async Task WriteNativeOverlayEventPipeMessageAsync(string pipeName, byte[] message, TimeSpan timeout)
    {
        await WriteNativeOverlayEventPipeMessagesAsync(pipeName, [message], timeout);
    }

    static async Task WriteNativeOverlayEventPipeMessagesAsync(
        string pipeName,
        IReadOnlyList<byte[]> messages,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                var connectTimeout = (int)Math.Clamp(
                    100,
                    1,
                    Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds));
                await pipe.ConnectAsync(connectTimeout);
                foreach (var message in messages)
                {
                    await pipe.WriteAsync(message);
                }

                await pipe.FlushAsync();
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                lastException = ex;
                await Task.Delay(25);
            }
        }

        throw new TimeoutException("Timed out writing a native overlay event pipe message.", lastException);
    }

    static byte[] BuildNativeOverlayEventMessage(uint type, int value)
    {
        var message = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(0, 4), 0x564C4F56u);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8, 4), type);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(12, 4), value);
        return message;
    }

    static int PackNativeOverlaySize(int width, int height)
    {
        return unchecked((int)(((uint)width << 16) | ((uint)height & 0xFFFFu)));
    }

    static bool IsNativeOverlayRenderedChatFrame(byte[] message)
    {
        return IsNativeOverlayFullSizeFrame(message) &&
            message.AsSpan(36).ToArray().Any(value => value != 0);
    }

    static bool IsNativeOverlayScrollbarStateFrame(byte[] message)
    {
        return message.Length == 36 &&
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(0, 4)) == 0x564C4F56u &&
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4, 4)) == 1u &&
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8, 4)) == 0u &&
            message[12] == 4 &&
            message[32] == 255;
    }

    static int ReadNativeOverlayScrollbarMessageOffset(byte[] message)
    {
        Assert.True(IsNativeOverlayScrollbarStateFrame(message));
        return BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(16, 4));
    }

    static int ReadNativeOverlayScrollbarTotalMessageCount(byte[] message)
    {
        Assert.True(IsNativeOverlayScrollbarStateFrame(message));
        return BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(28, 4));
    }

    static void AssertNativeOverlayScrollbarStateFrame(
        byte[] message,
        int expectedMessageOffset,
        int expectedMaximumMessageOffset,
        int expectedVisibleMessageCount,
        int expectedTotalMessageCount)
    {
        Assert.True(IsNativeOverlayScrollbarStateFrame(message));
        Assert.Equal(expectedMessageOffset, BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(16, 4)));
        Assert.Equal(expectedMaximumMessageOffset, BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(20, 4)));
        Assert.Equal(expectedVisibleMessageCount, BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(24, 4)));
        Assert.Equal(expectedTotalMessageCount, BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(28, 4)));
    }

    static NativeOverlayAlphaBounds GetNativeOverlayAlphaBounds(byte[] message)
    {
        Assert.True(IsNativeOverlayFullSizeFrame(message));
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4));
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4));
        var pixels = message.AsSpan(36);
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (pixels[rowOffset + x * 4 + 3] == 0)
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        return new NativeOverlayAlphaBounds(minX, minY, maxX, maxY);
    }

    static bool IsNativeOverlayBlankFrame(byte[] message)
    {
        return message.Length == 40 &&
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(0, 4)) == 0x564C4F56u &&
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4, 4)) == 1u &&
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8, 4)) == 4u &&
            message[12] == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4)) == 1u &&
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4)) == 1u &&
            message[32] == 0 &&
            message.AsSpan(36, 4).ToArray().All(value => value == 0);
    }

    static bool IsNativeOverlayTransparentFrame(byte[] message)
    {
        return IsNativeOverlayFullSizeFrame(message) &&
            message.AsSpan(36).ToArray().All(value => value == 0);
    }

    static bool IsNativeOverlayFullSizeFrame(byte[] message)
    {
        if (message.Length < 36)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(0, 4)) != 0x564C4F56u ||
            BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4, 4)) != 1u ||
            message[12] != 1 ||
            message[32] != 255)
        {
            return false;
        }

        var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8, 4));
        var width = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4));
        if (width < NativeOverlaySizing.MinWidth ||
            height < NativeOverlaySizing.MinHeight ||
            (ulong)payloadSize != (ulong)width * height * 4 ||
            payloadSize > int.MaxValue - 36)
        {
            return false;
        }

        return message.Length == 36 + (int)payloadSize;
    }

    static void AssertNativeOverlayBlankFrame(byte[] message)
    {
        Assert.Equal(0x564C4F56u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(0, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8, 4)));
        Assert.Equal(1, message[12]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4)));
        Assert.Equal(0, message[32]);
        Assert.True(message.AsSpan(36, 4).ToArray().All(value => value == 0));
    }

    static void AssertNativeOverlayTransparentFrame(byte[] message)
    {
        Assert.Equal(0x564C4F56u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(0, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4, 4)));
        var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8, 4));
        Assert.Equal(1, message[12]);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4));
        Assert.True(width >= NativeOverlaySizing.MinWidth);
        Assert.True(height >= NativeOverlaySizing.MinHeight);
        Assert.Equal((ulong)payloadSize, (ulong)width * height * 4);
        Assert.Equal(255, message[32]);
        Assert.Equal(36 + (int)payloadSize, message.Length);
        Assert.True(message.AsSpan(36).ToArray().All(value => value == 0));
    }

    static void AssertNativeOverlayChatFrame(byte[] message)
    {
        Assert.Equal(0x564C4F56u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(0, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4, 4)));
        var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8, 4));
        Assert.Equal(1, message[12]);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4));
        Assert.True(width >= NativeOverlaySizing.MinWidth);
        Assert.True(height >= NativeOverlaySizing.MinHeight);
        Assert.Equal((ulong)payloadSize, (ulong)width * height * 4);
        Assert.Equal(255, message[32]);
        Assert.Equal(36 + (int)payloadSize, message.Length);
        Assert.True(message.AsSpan(36).ToArray().Any(value => value != 0));
    }

    static bool ContainsUnpairedSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(current))
            {
                return true;
            }
        }

        return false;
    }

    static void AssertNear(double expected, double actual, double tolerance = 0.001)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"Expected {expected:0.###}, got {actual:0.###}.");
        }
    }

    static Border[] AddHomeCardPanelChildren(HomeCardWrapPanel panel, int count)
    {
        var cards = new Border[count];
        for (var index = 0; index < cards.Length; index++)
        {
            var card = new Border
            {
                Height = 50
            };
            panel.Children.Add(card);
            cards[index] = card;
        }

        return cards;
    }

    static void AssertVideoGridFullyCovered(IEnumerable<StreamTabViewModel> tabs, int rows, int columns)
    {
        var coverage = new int[rows, columns];

        foreach (var tab in tabs)
        {
            for (var row = tab.VideoGridRow; row < tab.VideoGridRow + tab.VideoGridRowSpan; row++)
            {
                for (var column = tab.VideoGridColumn; column < tab.VideoGridColumn + tab.VideoGridColumnSpan; column++)
                {
                    if (row < 0 || row >= rows || column < 0 || column >= columns)
                    {
                        throw new InvalidOperationException($"Grid placement for {tab.Target.DisplayName} is out of bounds.");
                    }

                    coverage[row, column]++;
                }
            }
        }

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                Assert.Equal(1, coverage[row, column]);
            }
        }
    }

    static void RemoveMainWindowAutomaticStartup(MainWindow window)
    {
        RemoveMainWindowHandler<System.Windows.RoutedEventHandler>(window, nameof(System.Windows.Window.Loaded), "MainWindowLoaded");
        RemoveMainWindowHandler<EventHandler>(window, nameof(System.Windows.Window.SourceInitialized), "MainWindowSourceInitialized");
        RemoveMainWindowHandler<System.ComponentModel.CancelEventHandler>(window, nameof(System.Windows.Window.Closing), "MainWindowClosing");
        RemoveMainWindowHandler<EventHandler>(window, nameof(System.Windows.Window.Closed), "MainWindowClosed");
    }

    static void RemoveMainWindowHandler<TDelegate>(MainWindow window, string eventName, string methodName)
        where TDelegate : Delegate
    {
        var method = typeof(MainWindow).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        var eventInfo = typeof(MainWindow).GetEvent(eventName);
        Assert.NotNull(method);
        Assert.NotNull(eventInfo);

        var handler = Delegate.CreateDelegate(typeof(TDelegate), window, method!);
        eventInfo!.RemoveEventHandler(window, handler);
    }

    static void SetMainWindowHandle(MainWindow window)
    {
        var windowHandleField = typeof(MainWindow).GetField("windowHandle", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(windowHandleField);
        windowHandleField!.SetValue(window, new System.Windows.Interop.WindowInteropHelper(window).Handle);
    }

    static void AttachMainWindowMessageHook(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("WindowMessageHook", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Assert.True(handle != IntPtr.Zero);
        var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
        Assert.NotNull(source);
        var hook = (System.Windows.Interop.HwndSourceHook)Delegate.CreateDelegate(
            typeof(System.Windows.Interop.HwndSourceHook),
            window,
            method!);
        source!.AddHook(hook);
    }

    static void SetMainWindowViewModel(MainWindow window, MainViewModel viewModel)
    {
        var viewModelField = typeof(MainWindow).GetField("viewModel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(viewModelField);
        viewModelField!.SetValue(window, viewModel);
    }

    static void ToggleMainWindowFullscreen(MainWindow window, string modeName)
    {
        var fullscreenModeType = typeof(MainWindow).GetNestedType(
            "FullscreenMode",
            BindingFlags.NonPublic);
        var toggleFullscreen = typeof(MainWindow).GetMethod(
            "ToggleFullscreenMode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(fullscreenModeType);
        Assert.NotNull(toggleFullscreen);

        var mode = Enum.Parse(fullscreenModeType!, modeName);
        toggleFullscreen!.Invoke(window, [mode]);
    }

    static void ExitMainWindowFullscreenIfActive(MainWindow window)
    {
        var fullscreenField = typeof(MainWindow).GetField(
            "fullscreen",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var exitFullscreen = typeof(MainWindow).GetMethod(
            "ExitFullscreenMode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(fullscreenField);
        Assert.NotNull(exitFullscreen);

        if ((bool)fullscreenField!.GetValue(window)!)
        {
            exitFullscreen!.Invoke(window, []);
        }
    }

    static void SetMainWindowControlModifier(MainWindow window, bool pressed)
    {
        var controlModifierField = typeof(MainWindow).GetField(
            "isControlModifierPressed",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(controlModifierField);
        controlModifierField!.SetValue(window, (Func<bool>)(() => pressed));
    }

    static void SetMainWindowControlModifierProvider(MainWindow window, Func<bool> isPressed)
    {
        var controlModifierField = typeof(MainWindow).GetField(
            "isControlModifierPressed",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(controlModifierField);
        controlModifierField!.SetValue(window, isPressed);
    }

    static ListBoxItem FindTabStripListBoxItem(MainWindow window, StreamTabViewModel tab)
    {
        return FindVisualDescendants<ListBoxItem>(window)
            .First(item => item.DataContext is TabStripItemViewModel tabStripItem && tabStripItem.Contains(tab));
    }

    static object CreateNativeScreenPoint(Type nativePointType, System.Windows.Point screenPoint)
    {
        var nativePoint = Activator.CreateInstance(
            nativePointType,
            [(int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)]);
        Assert.NotNull(nativePoint);
        return nativePoint!;
    }

    static object CreateBlankTabStripNativePoint(MainWindow window, ListBoxItem afterItem, Type nativePointType)
    {
        var tabListBox = (ListBox)window.FindName("TabListBox");
        Assert.NotNull(tabListBox);

        var afterItemRight = afterItem.TransformToAncestor(tabListBox).Transform(new System.Windows.Point(
            afterItem.ActualWidth,
            afterItem.ActualHeight / 2));
        var blankX = Math.Min(tabListBox.ActualWidth - 4, afterItemRight.X + 40);
        Assert.True(
            blankX > afterItemRight.X + 4,
            $"Expected blank tab strip space after the generated tab items; tab strip width was {tabListBox.ActualWidth.ToString(CultureInfo.InvariantCulture)} and the item right edge was {afterItemRight.X.ToString(CultureInfo.InvariantCulture)}.");

        return CreateNativeScreenPoint(
            nativePointType,
            tabListBox.PointToScreen(new System.Windows.Point(blankX, afterItemRight.Y)));
    }

    static bool InvokeMainWindowLowLevelMouseHook(MainWindow window, int message, System.Windows.Point screenPoint)
    {
        return window.RouteLowLevelMouseHookEvent(new LowLevelMouseHookEvent(
            message,
            (int)Math.Round(screenPoint.X),
            (int)Math.Round(screenPoint.Y),
            0));
    }

    static Border FindTabStripChrome(MainWindow window, StreamTabViewModel tab)
    {
        return FindVisualDescendants<Border>(window)
            .First(border =>
                border.Name == "TabChrome" &&
                border.DataContext is TabStripItemViewModel tabStripItem &&
                tabStripItem.Contains(tab));
    }

    static string[] TabChannels(IEnumerable<StreamTabViewModel> tabs)
    {
        return tabs.Select(tab => tab.Target.Channel).ToArray();
    }

    static ListBoxItem GetGeneratedTabStripListBoxItem(MainWindow window, MainViewModel viewModel, StreamTabViewModel tab)
    {
        var tabListBox = (ListBox)window.FindName("TabListBox");
        var tabStripItem = viewModel.TabStripItems.First(item => item.Contains(tab));
        var listBoxItem = (ListBoxItem)tabListBox.ItemContainerGenerator.ContainerFromItem(tabStripItem);
        Assert.NotNull(listBoxItem);
        return listBoxItem;
    }

    static IEnumerable<T> FindVisualDescendants<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    static void AssertHomeCardCompactHorizontalGutter(MainWindow window, RoundedClipBorder clipBorder)
    {
        var cardButton = FindVisualAncestor<Button>(clipBorder);
        var homeScrollViewer = window.FindName("HomeContentScrollViewer") as ScrollViewer;
        Assert.NotNull(cardButton);
        Assert.NotNull(homeScrollViewer);

        var leftEdge = cardButton!.TranslatePoint(new Point(0, 0), homeScrollViewer).X;
        var rightEdge = cardButton.TranslatePoint(new Point(cardButton.ActualWidth, 0), homeScrollViewer).X;
        AssertNear(16, leftEdge);
        AssertNear(16, homeScrollViewer!.ActualWidth - rightEdge);
    }

    static void AssertHomeMediaThumbnailClip(RoundedClipBorder clipBorder)
    {
        var cardButton = FindVisualAncestor<Button>(clipBorder);
        Assert.NotNull(cardButton);
        var cardChrome = VisualTreeHelper.GetChild(cardButton!, 0) as Border;
        Assert.NotNull(cardChrome);
        Assert.Equal(12d, cardChrome!.CornerRadius.TopLeft);
        Assert.Equal(1d, cardChrome.BorderThickness.Left);
        Assert.Equal(1d, cardChrome.BorderThickness.Top);
        Assert.Equal<Geometry?>(null, cardChrome.Clip);
        Assert.NotNull(cardChrome.Effect);

        var expectedInnerRadius = cardChrome.CornerRadius.TopLeft - (cardChrome.BorderThickness.Top / 2);
        Assert.Equal(expectedInnerRadius, clipBorder.CornerRadius.TopLeft);
        Assert.Equal(expectedInnerRadius, clipBorder.CornerRadius.TopRight);
        Assert.Equal(0d, clipBorder.CornerRadius.BottomRight);
        Assert.Equal(0d, clipBorder.CornerRadius.BottomLeft);
        Assert.NotNull(clipBorder.Clip);
        Assert.Equal(false, clipBorder.Clip.FillContains(new Point(1, 1)));
        Assert.True(clipBorder.Clip.FillContains(new Point(1, clipBorder.RenderSize.Height - 1)));
        Assert.NotNull(FindVisualDescendants<AnimatedEmoteImage>(clipBorder).Single().Source);
    }

    static T? FindVisualAncestor<T>(DependencyObject child)
        where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T match)
            {
                return match;
            }
        }

        return null;
    }

}
