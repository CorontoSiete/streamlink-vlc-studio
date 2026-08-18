using System.Text.Json;
using StreamlinkVlcStudio.Core.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Time;
using StreamlinkVlcStudio.Core.Twitch;

internal static class CoreQualityTestCatalog
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("platform route and Twitch playlist URI policies are canonical", PlatformRoutePolicyRejectsLandingPages),
        ("Windows command line tokenizer handles quote runs and rejects nulls", CommandLineTokenizerHandlesQuoteRuns),
        ("duration conversion rejects the rounded Int64 overflow boundary", DurationConversionRejectsOverflowBoundary),
        ("Core shared values are canonical and immutable", KickBadgeAliasesAreCanonical),
        ("test runner reports tests not started after a noncooperative timeout", TestRunnerReportsNotRunAsync),
        ("test runner rejects overflowing timeout configuration", TestRunnerRejectsOverflowingTimeout)
    ];

    private static Task PlatformRoutePolicyRejectsLandingPages()
    {
        Assert.Throws<ArgumentException>(() =>
            StreamInputParser.FromChannel(PlatformKind.Kick, "browse"));
        Assert.Throws<ArgumentException>(() =>
            StreamInputParser.FromChannel(PlatformKind.Twitch, "creatorcamp"));
        Assert.Equal(false, StreamInputParser.TryParsePlatformUrl("https://kick.com/browse", out _));
        Assert.Equal(false, StreamInputParser.TryParsePlatformUrl("https://twitch.tv/creatorcamp", out _));

        Assert.Equal("browse_live", StreamInputParser.FromChannel(PlatformKind.Kick, "browse_live").Channel);
        Assert.Equal("creatorcampus", StreamInputParser.FromChannel(PlatformKind.Twitch, "creatorcampus").Channel);
        Assert.Equal("xqc", StreamInputParser.FromChannel(PlatformKind.Twitch, " /@xqc/ ").Channel);
        Assert.Equal(
            "xqc",
            StreamInputParser.Parse("//www.twitch.tv/xqc", PlatformKind.Kick).Channel);
        var kickOnly = StreamInputParser.ParseCandidates("creatorcamp");
        Assert.Equal(1, kickOnly.Count);
        Assert.Equal(PlatformKind.Kick, kickOnly[0].Platform);
        var twitchOnly = StreamInputParser.ParseCandidates("browse");
        Assert.Equal(1, twitchOnly.Count);
        Assert.Equal(PlatformKind.Twitch, twitchOnly[0].Platform);
        Assert.Throws<ArgumentException>(() => StreamInputParser.ParseCandidates("videos"));
        var followed = new FollowedChannelsSettings
        {
            KickChannelSlugs = ["kick.com", "creatorcamp", "https://kick.com/kick.com"]
        };
        Assert.SequenceEqual(["creatorcamp", "kick.com"], followed.KickChannelSlugs);
        TwitchPlaylistBuilderEscapesPathComponents();
        return Task.CompletedTask;
    }

    private static Task CommandLineTokenizerHandlesQuoteRuns()
    {
        Assert.SequenceEqual(["a\"b"], CommandLineTokenizer.Tokenize("\"a\"\"b\""));
        Assert.SequenceEqual(["a\"b"], CommandLineTokenizer.Tokenize("\"a\"\"\"b\""));
        Assert.SequenceEqual(
            ["--http-header", "broken"],
            CommandLineTokenizer.Tokenize("--http-header \"broken"));
        CommandLineTokenizerRejectsNullCharacters();
        return Task.CompletedTask;
    }

    private static Task DurationConversionRejectsOverflowBoundary()
    {
        Assert.Equal(false, DurationValues.TryCreatePositive(
            9_223_372_036_854_775_808d,
            1,
            out var rejected));
        Assert.Equal(TimeSpan.Zero, rejected);

        var lastRepresentableDoubleBelowLimit = Math.BitDecrement(9_223_372_036_854_775_808d);
        Assert.True(DurationValues.TryCreatePositive(lastRepresentableDoubleBelowLimit, 1, out var accepted));
        Assert.True(accepted > TimeSpan.Zero);

        Assert.True(DurationValues.TryCreatePositive(1.25, 10, out var rounded));
        Assert.Equal(TimeSpan.FromTicks(13), rounded);
        return Task.CompletedTask;
    }

    private static Task KickBadgeAliasesAreCanonical()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["creator"] = "broadcaster",
            ["channel-host"] = "broadcaster",
            ["gift_sub"] = "sub_gifter",
            ["gifted_subscription"] = "sub_gifter",
            ["subgift"] = "sub_gifter",
            ["subscription-gifts"] = "sub_gifter",
            ["subscriptions"] = "subscriber",
            ["sub"] = "subscriber",
            ["VIP-Badge"] = "vip_badge"
        };

        foreach (var (alias, expected) in aliases)
        {
            var normalized = KickBadgeIdNormalizer.Normalize(alias);
            Assert.Equal(expected, normalized);
            Assert.Equal(normalized, KickBadgeIdNormalizer.Normalize(normalized));
        }

        Assert.Equal("", KickBadgeIdNormalizer.Normalize(null));
        KickPusherScalarMetadataUsesValidFallbacks();
        KickPusherFractionalTimestampsPreserveUnits();
        KickIdentitySettingsDiscardBlankIds();
        SharedOptionCollectionsAreImmutable();
        PictureInPictureSettingsOwnNormalizedState();
        TwitchEmoteOffsetsRequireProtocolDigits();
        ReplayQualityValuesAreTrimmed();
        return Task.CompletedTask;
    }

    private static void ReplayQualityValuesAreTrimmed()
    {
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "channel",
            "https://example.com/replay",
            "replay-id",
            null,
            TimeSpan.FromMinutes(1),
            true,
            "",
            " source ");
        Assert.Equal("source", replay.GetStreamlinkQuality(" best "));
        Assert.Equal(
            "best",
            (replay with { StreamlinkQuality = "" }).GetStreamlinkQuality(" best "));
    }

    private static void TwitchEmoteOffsetsRequireProtocolDigits()
    {
        var message = TwitchIrcParser.TryParsePrivMsg(
            "@emotes=25:+0-4 :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #channel :Kappa",
            "channel");

        Assert.NotNull(message);
        Assert.NotNull(message!.Emotes);
        Assert.Equal(0, message.Emotes!.Count);
    }

    private static void SharedOptionCollectionsAreImmutable()
    {
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AppThemeOption>)AppThemeOption.All)[0] = AppThemeOption.All[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<QualityOption>)QualityOption.Defaults)[0] = QualityOption.Defaults[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VideoRendererModeOption>)VideoRendererModeOption.All)[0] = VideoRendererModeOption.All[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<WindowCloseBehaviorOption>)WindowCloseBehaviorOption.All)[0] = WindowCloseBehaviorOption.All[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)TwitchSubOnlyVodPlaylist.QualityKeys)[0] = TwitchSubOnlyVodPlaylist.QualityKeys[0]);
    }

    private static void PictureInPictureSettingsOwnNormalizedState()
    {
        var fullscreenScreen = new PictureInPictureFullscreenScreen("  DISPLAY1  ", 10, 20, 1920, 1080);
        var source = new PictureInPictureWindowLocation(1, 2, -1, double.NaN)
        {
            IsFullscreen = true,
            FullscreenMode = (PictureInPictureFullscreenMode)999,
            FullscreenScreen = fullscreenScreen
        };
        var settings = new AppSettings { PictureInPictureWindowLocation = source };
        var stored = settings.PictureInPictureWindowLocation;

        Assert.NotNull(stored);
        Assert.Equal(false, ReferenceEquals(source, stored));
        Assert.Equal(0d, stored!.Width);
        Assert.Equal(0d, stored.Height);
        Assert.Equal(PictureInPictureFullscreenMode.StreamOnly, stored.FullscreenMode);
        Assert.NotNull(stored.FullscreenScreen);
        Assert.Equal(false, ReferenceEquals(fullscreenScreen, stored.FullscreenScreen));
        Assert.Equal("DISPLAY1", stored.FullscreenScreen!.DeviceName);

        source.Left = 500;
        fullscreenScreen.Width = 1;
        Assert.Equal(1d, stored.Left);
        Assert.Equal(1920d, stored.FullscreenScreen.Width);
    }

    private static void KickPusherScalarMetadataUsesValidFallbacks()
    {
        using var document = JsonDocument.Parse("""
        {
          "content": "hello",
          "id": { "invalid": true },
          "message_id": "fallback-id",
          "sender": {
            "username": "   ",
            "slug": "fallback-user",
            "identity": {
              "color": " ",
              "username_color": "#12AB34"
            }
          }
        }
        """);

        var message = KickPusherParser.TryParseMessageData(document.RootElement, "channel");

        Assert.NotNull(message);
        Assert.Equal("fallback-user", message!.Username);
        Assert.Equal("#12AB34", message.Color);
        Assert.Equal("fallback-id", message.MessageId);
        Assert.Equal("", JsonElementReader.GetScalarString(document.RootElement));
    }

    private static void KickPusherFractionalTimestampsPreserveUnits()
    {
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1_780_344_600_123);
        foreach (var timestamp in new[]
        {
            "1780344600123000.0",
            "1780344600123000000.0"
        })
        {
            using var document = JsonDocument.Parse($$"""
            {
              "content": "timestamped",
              "created_at": {{timestamp}}
            }
            """);
            var message = KickPusherParser.TryParseMessageData(document.RootElement, "channel");

            Assert.NotNull(message);
            Assert.Equal(expected, message!.Timestamp);
        }
    }

    private static void KickIdentitySettingsDiscardBlankIds()
    {
        var settings = new ChatSettings
        {
            KickChatroomIds = new Dictionary<string, string>
            {
                [" valid "] = " 123 ",
                [" empty "] = "   "
            },
            KickBroadcasterUserIds = new Dictionary<string, string>
            {
                ["ghost"] = ""
            }
        };

        Assert.Equal(1, settings.KickChatroomIds.Count);
        Assert.True(settings.TryGetKickChatroomId("VALID", out var chatroomId));
        Assert.Equal("123", chatroomId);
        Assert.Equal(false, settings.TryGetKickChatroomId("empty", out _));
        Assert.Equal(false, settings.TryGetKickBroadcasterUserId("ghost", out _));
    }

    private static void CommandLineTokenizerRejectsNullCharacters()
    {
        Assert.Throws<ArgumentException>(() =>
            CommandLineTokenizer.Tokenize("--http-header value\0--retry-open 5"));
    }

    private static void TwitchPlaylistBuilderEscapesPathComponents()
    {
        var url = TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl(
            "highlight",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "cdn.example.com",
            "special/..?",
            "owner/name",
            "123#fragment",
            "720p/source");

        Assert.Equal(
            "https://cdn.example.com/special%2F%2E%2E%3F/720p%2Fsource/highlight-123%23fragment.m3u8",
            url);
        Assert.Throws<ArgumentException>(() => TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl(
            "archive",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "cloudfront.net@evil.example",
            "special",
            "owner",
            "123",
            "chunked"));

        var dotSegmentUrl = TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl(
            "archive",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "cdn.example.com",
            "special",
            "owner",
            "123",
            "..");
        Assert.Contains("/%2E%2E/index-dvr.m3u8", dotSegmentUrl);

        var rewritten = TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(
            "\uFEFF#EXTM3U\nsegment.ts",
            new Uri("https://video-edge.example.cloudfront.net/vod/index-dvr.m3u8"));
        Assert.Equal(
            "#EXTM3U\nhttps://video-edge.example.cloudfront.net/vod/segment.ts\n",
            rewritten);
    }

    private static async Task TestRunnerReportsNotRunAsync()
    {
        var oldFilter = Environment.GetEnvironmentVariable("SVS_TEST_FILTER");
        var oldTimeout = Environment.GetEnvironmentVariable("SVS_TEST_TIMEOUT_SECONDS");
        var oldDrain = Environment.GetEnvironmentVariable("SVS_TEST_DRAIN_TIMEOUT_SECONDS");
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterTestRan = false;
        using var output = new StringWriter();

        try
        {
            Environment.SetEnvironmentVariable("SVS_TEST_FILTER", null);
            Environment.SetEnvironmentVariable("SVS_TEST_TIMEOUT_SECONDS", "0.01");
            Environment.SetEnvironmentVariable("SVS_TEST_DRAIN_TIMEOUT_SECONDS", "0.01");
            Console.SetOut(output);
            Console.SetError(output);

            var result = await DependencyFreeTestRunner.RunAsync(
            [
                ("noncooperative", () => release.Task),
                ("must not start", () =>
                {
                    laterTestRan = true;
                    return Task.CompletedTask;
                })
            ]);

            Assert.Equal(1, result);
            Assert.Equal(false, laterTestRan);
            Assert.True(output.ToString().Contains("1 not run", StringComparison.Ordinal));
        }
        finally
        {
            release.TrySetResult();
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            Environment.SetEnvironmentVariable("SVS_TEST_FILTER", oldFilter);
            Environment.SetEnvironmentVariable("SVS_TEST_TIMEOUT_SECONDS", oldTimeout);
            Environment.SetEnvironmentVariable("SVS_TEST_DRAIN_TIMEOUT_SECONDS", oldDrain);
        }
    }

    private static Task TestRunnerRejectsOverflowingTimeout()
    {
        const string variable = "SVS_TEST_TIMEOUT_OVERFLOW_TEST";
        var oldValue = Environment.GetEnvironmentVariable(variable);
        var fallback = TimeSpan.FromSeconds(7);
        try
        {
            Environment.SetEnvironmentVariable(variable, "1e300");
            Assert.Equal(fallback, DependencyFreeTestRunner.ReadPositiveSeconds(variable, fallback));
            Environment.SetEnvironmentVariable(variable, "86400");
            Assert.Equal(TimeSpan.FromDays(1), DependencyFreeTestRunner.ReadPositiveSeconds(variable, fallback));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, oldValue);
        }

        return Task.CompletedTask;
    }
}
