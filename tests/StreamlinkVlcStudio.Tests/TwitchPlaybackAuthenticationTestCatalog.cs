internal static partial class TwitchPlaybackAuthenticationTestCatalog
{
    private const string WebsiteToken = "fixture-website-token";
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Twitch video authentication: audio-only best retries once with website session and keeps quality", WebsiteRecoveryAsync),
        ("Twitch video authentication: ordinary video does not read or send website credentials", OrdinaryVideoAsync),
        ("Twitch video authentication: missing website session fails before VLC receives an audio transport", MissingSessionAsync),
        ("Twitch video authentication: persistent audio-only result terminates both processes without retry loop", StillAudioAsync),
        ("Twitch video authentication: explicit audio-only and non-live-Twitch requests retain their transport", IntentionalAudioAsync),
        ("Twitch video authentication: custom Twitch identity is preserved without automatic account replacement", CustomIdentityAsync),
        ("Twitch video authentication: cancellation during cookie lookup leaves no transport process", CancellationAsync),
        ("Twitch video authentication: rejected and malformed website sessions expose no credentials", RejectedSessionAsync),
        ("Twitch video authentication: authentication never substitutes a different requested quality", RequestedQualityAsync),
        .. (Environment.GetEnvironmentVariable("SVS_TEST_SABLEOC_LIVE") == "true"
            ? new (string, Func<Task>)[] { ("Twitch video authentication: live SableOC renders the authenticated source video and audio", LiveSableOcAsync) }
            : [])
    ];

    private static async Task WebsiteRecoveryAsync()
    {
        foreach (var lowLatency in new[] { true, false })
        {
            using var fixture = new Fixture("video-with-session");
            var reads = 0;
            var service = StreamlinkService.WithTwitchWebsiteSession(fixture.Logger, _ => { reads++; return Task.FromResult<string?>(WebsiteToken); });
            await using (var session = await service.StartExternalHttpAsync(fixture.Request() with { LowLatency = lowLatency, IsMultiStream = true }))
            {
                Assert.Equal("http://127.0.0.1:12345/", session.PlaybackUri.AbsoluteUri);
                Assert.Equal(1, reads);
                var attempts = fixture.Attempts;
                Assert.Equal(2, attempts.Length);
                Assert.Equal(false, attempts[0].HasAuthorization);
                Assert.Equal(true, attempts[1].HasAuthorization);
                Assert.True(attempts.All(attempt => attempt.Quality == "best" && attempt.RingBuffer == "16M"));
                Assert.True(attempts.All(attempt => attempt.LowLatency == lowLatency));
                await AssertExitedAsync(attempts.Take(1));
                Assert.True(fixture.Logger.Entries.All(entry => !entry.Message.Contains(WebsiteToken, StringComparison.Ordinal)));
                Assert.True(((StreamlinkExternalHttpSession)session).RecentLogLines.All(line => !line.Contains(WebsiteToken, StringComparison.Ordinal)));
            }
            await AssertExitedAsync(fixture.Attempts);
        }
    }

    private static async Task OrdinaryVideoAsync()
    {
        using var fixture = new Fixture("normal-video");
        var service = StreamlinkService.WithTwitchWebsiteSession(fixture.Logger, _ => throw new InvalidOperationException("Unexpected website lookup"));
        await using (var session = await service.StartExternalHttpAsync(fixture.Request()))
        {
            Assert.Equal(1, fixture.Attempts.Length);
            Assert.Equal(false, fixture.Attempts.Single().HasAuthorization);
        }
        await AssertExitedAsync(fixture.Attempts);
    }

    private static async Task MissingSessionAsync()
    {
        foreach (var providerPresent in new[] { true, false })
        {
            using var fixture = new Fixture("video-with-session");
            var service = providerPresent
                ? StreamlinkService.WithTwitchWebsiteSession(fixture.Logger, _ => Task.FromResult<string?>(null))
                : new StreamlinkService(fixture.Logger);
            var exception = await FailureAsync(() => service.StartExternalHttpAsync(fixture.Request()));
            Assert.Contains("Twitch returned only audio", exception.Message);
            Assert.Contains("Sign in for bonuses", exception.Message);
            Assert.Equal(1, fixture.Attempts.Length);
            await AssertExitedAsync(fixture.Attempts);
        }
    }

    private static async Task StillAudioAsync()
    {
        using var fixture = new Fixture("always-audio");
        var reads = 0;
        var service = StreamlinkService.WithTwitchWebsiteSession(fixture.Logger, _ => { reads++; return Task.FromResult<string?>(WebsiteToken); });
        var exception = await FailureAsync(() => service.StartExternalHttpAsync(fixture.Request()));
        Assert.Contains("Twitch returned only audio", exception.Message);
        Assert.Equal(1, reads);
        Assert.Equal(2, fixture.Attempts.Length);
        await AssertExitedAsync(fixture.Attempts);
    }

    private static async Task IntentionalAudioAsync()
    {
        foreach (var (target, quality) in new[]
        {
            (StreamInputParser.FromChannel(PlatformKind.Twitch, "fixture"), "audio_only"),
            (StreamInputParser.FromChannel(PlatformKind.Kick, "fixture"), "best"),
            (StreamInputParser.Parse("https://www.twitch.tv/videos/123", PlatformKind.Twitch), "best")
        })
        {
            using var fixture = new Fixture("always-audio");
            var service = StreamlinkService.WithTwitchWebsiteSession(fixture.Logger, _ => throw new InvalidOperationException("Unexpected website lookup"));
            await using (var session = await service.StartExternalHttpAsync(fixture.Request() with { Target = target, Quality = quality }))
                Assert.Equal(1, fixture.Attempts.Length);
            await AssertExitedAsync(fixture.Attempts);
        }
    }

    private static async Task CustomIdentityAsync()
    {
        foreach (var custom in new[]
        {
            new[] { "--twitch-api-header", "Authorization=OAuth caller-token" },
            new[] { "--twitch-api-header=authorization=OAuth caller-token" },
            new[] { "--twitch-api-header", "Client-ID=caller-client" }
        })
        {
            using var fixture = new Fixture("always-audio");
            var service = StreamlinkService.WithTwitchWebsiteSession(fixture.Logger, _ => throw new InvalidOperationException("Unexpected account replacement"));
            var request = fixture.Request();
            await FailureAsync(() => service.StartExternalHttpAsync(request with { CustomArguments = [.. request.CustomArguments, .. custom] }));
            Assert.Equal(1, fixture.Attempts.Length);
            await AssertExitedAsync(fixture.Attempts);
        }
    }

    private static async Task CancellationAsync()
    {
        using var fixture = new Fixture("video-with-session");
        using var cancellation = new CancellationTokenSource();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = StreamlinkService.WithTwitchWebsiteSession(fixture.Logger, async token =>
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return WebsiteToken;
        });
        var opening = service.StartExternalHttpAsync(fixture.Request(), cancellation.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertExitedAsync(fixture.Attempts);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => opening);
        Assert.Equal(1, fixture.Attempts.Length);
        await AssertExitedAsync(fixture.Attempts);
    }

    private static async Task RejectedSessionAsync()
    {
        foreach (var token in new[] { WebsiteToken, "invalid\r\nwebsite-token" })
        {
            using var fixture = new Fixture("reject-session");
            var service = StreamlinkService.WithTwitchWebsiteSession(fixture.Logger, _ => Task.FromResult<string?>(token));
            var exception = await FailureAsync(() => service.StartExternalHttpAsync(fixture.Request()));
            Assert.Contains("Sign in for bonuses", exception.Message);
            Assert.Equal(token == WebsiteToken ? 2 : 1, fixture.Attempts.Length);
            Assert.True(fixture.Logger.Entries.All(entry => !entry.Message.Contains(token, StringComparison.Ordinal)));
            await AssertExitedAsync(fixture.Attempts);
        }
    }

    private static async Task RequestedQualityAsync()
    {
        using var fixture = new Fixture("quality-unavailable");
        var service = StreamlinkService.WithTwitchWebsiteSession(fixture.Logger, _ => Task.FromResult<string?>(WebsiteToken));
        await FailureAsync(() => service.StartExternalHttpAsync(fixture.Request() with { Quality = "1080p60" }));
        Assert.Equal(2, fixture.Attempts.Length);
        Assert.True(fixture.Attempts.All(attempt => attempt.Quality == "1080p60"));
        await AssertExitedAsync(fixture.Attempts);
    }

    private static async Task<InvalidOperationException> FailureAsync(Func<Task<IStreamTransportSession>> open)
    {
        try
        {
            await using var unexpected = await open();
        }
        catch (InvalidOperationException exception) { return exception; }
        throw new InvalidOperationException("An audio-only video request unexpectedly provided a transport.");
    }

    private static async Task AssertExitedAsync(IEnumerable<Attempt> attempts)
    {
        foreach (var attempt in attempts)
        {
            try
            {
                using var process = Process.GetProcessById(attempt.ProcessId);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException) { /* Already reaped. */ }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "twitch-playback-" + Guid.NewGuid().ToString("N"));
        private readonly string mode;
        internal MemoryLogger Logger { get; } = new();
        internal Fixture(string mode) { this.mode = mode; Directory.CreateDirectory(directory); }
        internal Attempt[] Attempts => Directory.GetFiles(directory, "*.json")
            .Select(path => JsonSerializer.Deserialize<Attempt>(File.ReadAllText(path))!).OrderBy(attempt => attempt.StartedAt).ToArray();
        internal StreamTransportRequest Request() => new(
            StreamInputParser.FromChannel(PlatformKind.Twitch, "fixture"), "best", OwnedProcessTestHost.Executable,
            true, ["--twitch-playback-fixture", mode, directory]);
        public void Dispose()
        {
            foreach (var attempt in Attempts)
            {
                try
                {
                    using var process = Process.GetProcessById(attempt.ProcessId);
                    if (!process.HasExited && process.ProcessName == "StreamlinkVlcStudio.Tests")
                        process.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    internal sealed record Attempt(int ProcessId, long StartedAt, bool HasAuthorization, string Quality, string RingBuffer, bool LowLatency);

    internal static async Task<int> RunFixtureAsync(string[] arguments)
    {
        var index = Array.IndexOf(arguments, "--twitch-playback-fixture");
        var mode = arguments[index + 1];
        var directory = arguments[index + 2];
        var identity = arguments.Select((argument, offset) => argument.StartsWith("--twitch-api-header=", StringComparison.OrdinalIgnoreCase)
                ? argument["--twitch-api-header=".Length..]
                : argument == "--twitch-api-header" && offset + 1 < arguments.Length ? arguments[offset + 1] : "")
            .FirstOrDefault(header => header.StartsWith("Authorization=", StringComparison.OrdinalIgnoreCase));
        var authenticated = identity == "Authorization=OAuth " + WebsiteToken;
        var ringIndex = Array.IndexOf(arguments, "--ringbuffer-size");
        var attempt = new Attempt(Environment.ProcessId, Stopwatch.GetTimestamp(), identity is not null, arguments[^1],
            ringIndex >= 0 ? arguments[ringIndex + 1] : "", arguments.Contains("--twitch-low-latency"));
        var record = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(record + ".tmp", JsonSerializer.Serialize(attempt));
        File.Move(record + ".tmp", record);
        if (authenticated) Console.WriteLine("[cli][debug] Twitch API header: " + identity);
        if (mode == "reject-session" && authenticated)
        {
            Console.Error.WriteLine("[plugins.twitch][error] 401 Unauthorized: The Authorization token is invalid.");
            return 1;
        }
        var video = mode == "normal-video" || authenticated && mode != "always-audio";
        Console.WriteLine(video
            ? "[cli][info] Available streams: audio_only, 1440p60 (worst, best)"
            : "[cli][info] Available streams: audio_only (worst, best)");
        if (mode == "quality-unavailable" && authenticated)
        {
            Console.Error.WriteLine("[cli][error] The requested quality " + arguments[^1] + " is unavailable.");
            return 1;
        }
        Console.WriteLine("[cli][info] Starting server, access with: http://127.0.0.1:12345/");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
}
