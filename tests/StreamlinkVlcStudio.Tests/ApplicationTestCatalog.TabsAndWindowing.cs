internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> TabsAndWindowing { get; } =
    [
    ("docked chat deduplicates source ids without collapsing repeated text", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var chatFactory = new FakeChatClientFactory();
        var target = StreamInputParser.Parse("albralelie", PlatformKind.Twitch);
        var tab = TestViewModels.CreateTab(
            target,
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);

        chatFactory.Client.Receive(new ChatMessage(
            target.Platform,
            target.Channel,
            "viewer",
            "same text",
            DateTimeOffset.Now,
            "#8AB4F8",
            MessageId: "source-message-1"));
        chatFactory.Client.Receive(new ChatMessage(
            target.Platform,
            target.Channel,
            "viewer",
            "same text",
            DateTimeOffset.Now.AddMilliseconds(1),
            "#8AB4F8",
            MessageId: "source-message-1"));
        chatFactory.Client.Receive(new ChatMessage(
            target.Platform,
            target.Channel,
            "viewer",
            "same text",
            DateTimeOffset.Now.AddMilliseconds(2),
            "#8AB4F8",
            MessageId: "source-message-2"));

        Assert.Equal(2, tab.ChatMessages.Count(message => message.Message == "same text"));
        Assert.Equal(2, tab.DockedChatMessages.Count(message => message.Message == "same text"));
        Assert.SequenceEqual(
            new[] { "source-message-1", "source-message-2" },
            tab.DockedChatMessages.Select(message => message.MessageId));
        await tab.DisposeAsync();
    }),
    ("docked chat renders badges as badge elements instead of labels", async () =>
    {
        await TestSta.RunAsync(() =>
        {
            var twitchBlock = new StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock
            {
                ChatFontSize = 14,
                Message = new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "modprime",
                    "badged message",
                    DateTimeOffset.Now,
                    "#8AB4F8",
                    [
                        new ChatBadge("moderator", "1", "Moderator"),
                        new ChatBadge("premium", "1", "Prime Gaming")
                    ])
            };

            var kickBlock = new StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock
            {
                ChatFontSize = 14,
                Message = new ChatMessage(
                    PlatformKind.Kick,
                    "streamer",
                    "kickmod",
                    "badged message",
                    DateTimeOffset.Now,
                    "#8AB4F8",
                    [
                        new ChatBadge(
                            "level",
                            "10",
                            "Level 10",
                            "https://ext.cdn.kick.com/chat/badges/10_804bf82a-c167-4184-a613-dfeb5f8bd1f0.png")
                    ])
            };

            var twitchBadgeImages = twitchBlock.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<StreamlinkVlcStudio.App.Wpf.Controls.AnimatedEmoteImage>()
                .ToArray();
            var twitchBadgeUrls = twitchBadgeImages
                .Select(image => image.ImageUrl.Replace('\\', '/'))
                .ToArray();
            var kickBadgeImages = kickBlock.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<StreamlinkVlcStudio.App.Wpf.Controls.AnimatedEmoteImage>()
                .ToArray();

            Assert.True(twitchBadgeImages.Length >= 2);
            Assert.True(twitchBadgeUrls.Any(url =>
                url.Contains("/TwitchBadges/", StringComparison.OrdinalIgnoreCase) &&
                url.EndsWith("/global/moderator/1.png", StringComparison.OrdinalIgnoreCase)));
            Assert.True(twitchBadgeUrls.Any(url =>
                url.Contains("/TwitchBadges/", StringComparison.OrdinalIgnoreCase) &&
                url.EndsWith("/global/premium/1.png", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(1, kickBadgeImages.Length);
            Assert.Equal("https://ext.cdn.kick.com/chat/badges/10_804bf82a-c167-4184-a613-dfeb5f8bd1f0.png", kickBadgeImages[0].ImageUrl);

            var renderedText = string.Concat(
                twitchBlock.Inlines.Concat(kickBlock.Inlines).OfType<Run>().Select(run => run.Text));
            Assert.DoesNotContain("[MOD]", renderedText);
            Assert.DoesNotContain("[PRIME]", renderedText);
            Assert.DoesNotContain("[SUB]", renderedText);
            Assert.Contains("modprime", renderedText);
            Assert.Contains("kickmod", renderedText);
        });
    }),
    ("docked chat catalog resolves bundled Kick role badges to bundled image files", async () =>
    {
        var catalogType = typeof(StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock).Assembly.GetType(
            "StreamlinkVlcStudio.App.Wpf.Chat.DockedChatBadgeCatalog");
        Assert.NotNull(catalogType);
        var catalog = Activator.CreateInstance(catalogType!, nonPublic: true);
        Assert.NotNull(catalog);

        var loadBundledKickBadges = catalogType!.GetMethod("LoadBundledKickBadgesAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var tryGet = catalogType.GetMethod("TryGet", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(loadBundledKickBadges);
        Assert.NotNull(tryGet);

        var loadResult = await (Task<CatalogLoadResult>)loadBundledKickBadges!.Invoke(catalog, [])!;
        Assert.True(loadResult.Succeeded);

        var args = new object?[]
        {
            new ChatMessage(PlatformKind.Kick, "any-channel", "viewer", "message", DateTimeOffset.Now),
            new ChatBadge("moderator", "0", "Moderator"),
            null
        };
        Assert.True((bool)tryGet!.Invoke(catalog, args)!);
        Assert.NotNull(args[2]);
        var moderatorImageUrl = (string?)args[2]!.GetType().GetProperty("ImageUrl")!.GetValue(args[2]);
        AssertBundledBadgeImageUrl(moderatorImageUrl, "KickBadges", "global/moderator.png");

        var aliasGroups = new Dictionary<string, string[]>
        {
            ["global/broadcaster.png"] = ["broadcaster", "channel-host", "creator"],
            ["global/subscriber.png"] = ["subscriber", "sub", "subscription", "subscriptions"],
            ["global/sub_gifter.png"] =
            [
                "sub_gifter",
                "gift-sub",
                "gift_subs",
                "gift_subscriber",
                "gift_subscription",
                "gifted_sub",
                "gifted_subs",
                "gifted_subscriber",
                "gifted_subscription",
                "gifter",
                "subgift",
                "subgifter",
                "sub_gift",
                "sub_gifter_badge",
                "sub_gifts",
                "subscriber_gifter",
                "subscription_gift",
                "subscription_gifts"
            ]
        };
        foreach (var (expectedImage, aliases) in aliasGroups)
        {
            foreach (var alias in aliases)
            {
                args[1] = new ChatBadge(alias, "25", alias);
                args[2] = null;
                Assert.True((bool)tryGet.Invoke(catalog, args)!);
                Assert.NotNull(args[2]);
                var imageUrl = (string?)args[2]!.GetType().GetProperty("ImageUrl")!.GetValue(args[2]);
                AssertBundledBadgeImageUrl(imageUrl, "KickBadges", expectedImage);
            }
        }
    }),
    ("Kick subscriber badge catalog uses Kick month thresholds", () =>
    {
        var catalogType = typeof(StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock).Assembly.GetType(
            "StreamlinkVlcStudio.App.Wpf.Chat.DockedChatBadgeCatalog");
        Assert.NotNull(catalogType);
        var catalog = Activator.CreateInstance(catalogType!, nonPublic: true);
        Assert.NotNull(catalog);

        var addKickBadge = catalogType!.GetMethod("AddKickBadge", BindingFlags.Instance | BindingFlags.NonPublic);
        var tryGet = catalogType.GetMethod("TryGet", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(addKickBadge);
        Assert.NotNull(tryGet);

        Assert.True((bool)addKickBadge!.Invoke(catalog, ["xqc", "subscriber", "1", "1-Month Subscriber", "https://files.kick.com/channel_subscriber_badges/97968/original"])!);
        Assert.True((bool)addKickBadge.Invoke(catalog, ["xqc", "subscriber", "9", "9-Month Subscriber", "https://files.kick.com/channel_subscriber_badges/97982/original"])!);

        var args = new object?[]
        {
            new ChatMessage(PlatformKind.Kick, "xqc", "viewer", "message", DateTimeOffset.Now),
            new ChatBadge("subscriber", "10", "Subscriber"),
            null
        };
        Assert.True((bool)tryGet!.Invoke(catalog, args)!);
        Assert.NotNull(args[2]);
        var imageUrl = (string?)args[2]!.GetType().GetProperty("ImageUrl")!.GetValue(args[2]);
        Assert.Equal("https://files.kick.com/channel_subscriber_badges/97982/original", imageUrl);
        return Task.CompletedTask;
    }),
    ("Twitch subscriber badge catalog prefers channel badges over global stars", () =>
    {
        var catalogType = typeof(StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock).Assembly.GetType(
            "StreamlinkVlcStudio.App.Wpf.Chat.DockedChatBadgeCatalog");
        Assert.NotNull(catalogType);
        var catalog = Activator.CreateInstance(catalogType!, nonPublic: true);
        Assert.NotNull(catalog);

        var addTwitchBadge = catalogType!.GetMethod("AddTwitchBadge", BindingFlags.Instance | BindingFlags.NonPublic);
        var tryGet = catalogType.GetMethod("TryGet", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(addTwitchBadge);
        Assert.NotNull(tryGet);

        Assert.True((bool)addTwitchBadge!.Invoke(catalog, [null, "subscriber", "1", "Global Subscriber", "https://static-cdn.jtvnw.net/badges/v1/global-star/3"])!);

        var args = new object?[]
        {
            new ChatMessage(PlatformKind.Twitch, "streamer", "viewer", "message", DateTimeOffset.Now, RoomId: "12345"),
            new ChatBadge("subscriber", "1", "Subscriber"),
            null
        };
        Assert.Equal(false, (bool)tryGet!.Invoke(catalog, args)!);

        Assert.True((bool)addTwitchBadge.Invoke(catalog, ["12345", "subscriber", "0", "Channel Subscriber", "https://static-cdn.jtvnw.net/badges/v1/channel-subscriber/3"])!);
        args[2] = null;
        Assert.True((bool)tryGet.Invoke(catalog, args)!);
        Assert.NotNull(args[2]);
        var imageUrl = (string?)args[2]!.GetType().GetProperty("ImageUrl")!.GetValue(args[2]);
        Assert.Equal("https://static-cdn.jtvnw.net/badges/v1/channel-subscriber/3", imageUrl);
        return Task.CompletedTask;
    }),
    ("Twitch Helix global badge catalog resolves badges newer than the bundle", () =>
    {
        var catalogType = typeof(StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock).Assembly.GetType(
            "StreamlinkVlcStudio.App.Wpf.Chat.DockedChatBadgeCatalog");
        Assert.NotNull(catalogType);
        var catalog = Activator.CreateInstance(catalogType!, nonPublic: true);
        Assert.NotNull(catalog);

        var loadTwitchHelixBadges = catalogType!.GetMethod("LoadTwitchHelixBadges", BindingFlags.Instance | BindingFlags.NonPublic);
        var tryGet = catalogType.GetMethod("TryGet", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(loadTwitchHelixBadges);
        Assert.NotNull(tryGet);

        using var document = JsonDocument.Parse("""
            {
              "data": [
                {
                  "set_id": "future-global-badge",
                  "versions": [
                    {
                      "id": "1",
                      "title": "Future Global Badge",
                      "image_url_1x": "https://static-cdn.jtvnw.net/badges/v1/future/1",
                      "image_url_2x": "https://static-cdn.jtvnw.net/badges/v1/future/2",
                      "image_url_4x": "https://static-cdn.jtvnw.net/badges/v1/future/3"
                    }
                  ]
                }
              ]
            }
            """);
        var data = document.RootElement.GetProperty("data");
        Assert.True((bool)loadTwitchHelixBadges!.Invoke(catalog, [data, null])!);

        var args = new object?[]
        {
            new ChatMessage(PlatformKind.Twitch, "streamer", "viewer", "message", DateTimeOffset.Now),
            new ChatBadge("future-global-badge", "1", "Future Global Badge"),
            null
        };
        Assert.True((bool)tryGet!.Invoke(catalog, args)!);
        Assert.NotNull(args[2]);
        var imageUrl = (string?)args[2]!.GetType().GetProperty("ImageUrl")!.GetValue(args[2]);
        Assert.Equal("https://static-cdn.jtvnw.net/badges/v1/future/2", imageUrl);
        return Task.CompletedTask;
    }),
    ("docked chat renders Kick gift badge as gift icon", async () =>
    {
        await TestSta.RunAsync(() =>
        {
            var block = new StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock
            {
                ChatFontSize = 14,
                Message = new ChatMessage(
                    PlatformKind.Kick,
                    "streamer",
                    "gifter",
                    "gift badge",
                    DateTimeOffset.Now,
                    "#8AB4F8",
                    [
                        new ChatBadge("sub_gifter", "25", "Sub Gifter")
                    ])
            };

            var giftBadges = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<System.Windows.Controls.Border>()
                .ToArray();
            var badgeImages = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<StreamlinkVlcStudio.App.Wpf.Controls.AnimatedEmoteImage>()
                .ToArray();
            var renderedText = string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));

            Assert.Equal(0, giftBadges.Length);
            Assert.Equal(1, badgeImages.Length);
            AssertBundledBadgeImageUrl(badgeImages[0].ImageUrl, "KickBadges", "global/sub_gifter.png");
            Assert.Contains("gifter", renderedText);
            Assert.DoesNotContain("[SUB]", renderedText);
        });
    }),
    ("docked chat renders known Kick badge icons without inventing unknown ones", async () =>
    {
        await TestSta.RunAsync(() =>
        {
            var block = new StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock
            {
                ChatFontSize = 14,
                Message = new ChatMessage(
                    PlatformKind.Kick,
                    "streamer",
                    "kickmod",
                    "badged message",
                    DateTimeOffset.Now,
                    "#8AB4F8",
                    [
                        new ChatBadge("moderator", "0", "Moderator"),
                        new ChatBadge("og", "1", "OG"),
                        new ChatBadge("unknown_custom_badge", "7", "Unknown Custom Badge")
                    ])
            };

            var fallbackBadges = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<System.Windows.Controls.Border>()
                .ToArray();
            var badgeImages = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<StreamlinkVlcStudio.App.Wpf.Controls.AnimatedEmoteImage>()
                .ToArray();
            var renderedText = string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));

            Assert.Equal(0, fallbackBadges.Length);
            Assert.Equal(2, badgeImages.Length);
            Assert.True(badgeImages.Any(image =>
                IsBundledBadgeImageUrl(image.ImageUrl, "KickBadges", "global/moderator.png")));
            Assert.True(badgeImages.Any(image =>
                IsBundledBadgeImageUrl(image.ImageUrl, "KickBadges", "global/og.png")));
            Assert.Contains("kickmod", renderedText);
            Assert.DoesNotContain("[UNKNOWN]", renderedText);
        });
    }),
    ("docked chat renders icon fallbacks for unresolved Twitch badges", async () =>
    {
        await TestSta.RunAsync(() =>
        {
            var block = new StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock
            {
                ChatFontSize = 14,
                Message = new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "viewer",
                    "fallback badges",
                    DateTimeOffset.Now,
                    "#8AB4F8",
                    [
                        new ChatBadge("sidekick", "1", "Sidekick"),
                        new ChatBadge("unknown_custom_badge", "7", "Unknown Custom Badge")
                    ])
            };

            var fallbackBadges = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<System.Windows.Controls.Border>()
                .ToArray();
            var renderedText = string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));

            Assert.Equal(2, fallbackBadges.Length);
            Assert.Contains("viewer", renderedText);
            Assert.DoesNotContain("[SIDEKICK]", renderedText);
            Assert.DoesNotContain("[UNKNOWN]", renderedText);
        });
    }),
    ("docked chat keeps Twitch subscriber badge visible while channel image is unresolved", async () =>
    {
        await TestSta.RunAsync(() =>
        {
            var block = new StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock
            {
                ChatFontSize = 14,
                Message = new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "subscriber",
                    "sub badge",
                    DateTimeOffset.Now,
                    "#8AB4F8",
                    [
                        new ChatBadge("subscriber", "1", "Subscriber")
                    ],
                    RoomId: "unresolved-room")
            };

            var fallbackBadges = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<System.Windows.Controls.Border>()
                .ToArray();
            var badgeImages = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<StreamlinkVlcStudio.App.Wpf.Controls.AnimatedEmoteImage>()
                .ToArray();
            var renderedText = string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));

            Assert.Equal(1, fallbackBadges.Length);
            Assert.Equal("Subscriber (1)", fallbackBadges[0].ToolTip);
            Assert.Equal(0, badgeImages.Length);
            Assert.Contains("subscriber", renderedText);
            Assert.DoesNotContain("[SUB]", renderedText);
        });
    }),
    ("docked chat uses parsed Twitch emote image URLs", async () =>
    {
        await TestSta.RunAsync(() =>
        {
            var imageUrl = "https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_4691b27f1e1742c892ea1d3267dc5ea0/static/light/2.0";
            var block = new StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock
            {
                ChatFontSize = 14,
                Message = new ChatMessage(
                    PlatformKind.Twitch,
                    "playapex",
                    "viewer",
                    "apxlgndsHifriends",
                    DateTimeOffset.Now,
                    "#8AB4F8",
                    Emotes:
                    [
                        new ChatEmote(0, 17, "apxlgndsHifriends", imageUrl)
                    ],
                    RoomId: "412132764")
            };

            var emoteImages = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<StreamlinkVlcStudio.App.Wpf.Controls.AnimatedEmoteImage>()
                .ToArray();
            var renderedText = string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));

            Assert.Equal(1, emoteImages.Length);
            Assert.Equal(imageUrl, emoteImages[0].ImageUrl);
            Assert.Equal("apxlgndsHifriends", emoteImages[0].ToolTip);
            Assert.DoesNotContain("apxlgndsHifriends", renderedText);
        });
    }),
    ("parsed emote catalog notifications are not raised synchronously during render", async () =>
    {
        var catalogType = typeof(StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock).Assembly.GetType(
            "StreamlinkVlcStudio.App.Wpf.Chat.DockedChatEmoteCatalog");
        Assert.NotNull(catalogType);
        var catalog = Activator.CreateInstance(catalogType!, nonPublic: true);
        Assert.NotNull(catalog);

        var ensureForMessage = catalogType!.GetMethod("EnsureForMessage", BindingFlags.Instance | BindingFlags.Public);
        var catalogChanged = catalogType.GetEvent("CatalogChanged", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(ensureForMessage);
        Assert.NotNull(catalogChanged);

        var callerThreadId = Environment.CurrentManagedThreadId;
        var callbackCount = 0;
        var synchronousCallbackCount = 0;
        EventHandler handler = (_, _) =>
        {
            Interlocked.Increment(ref callbackCount);
            if (Environment.CurrentManagedThreadId == callerThreadId)
            {
                Interlocked.Increment(ref synchronousCallbackCount);
            }
        };

        catalogChanged!.AddEventHandler(catalog, handler);
        try
        {
            ensureForMessage!.Invoke(catalog, [
                new ChatMessage(
                    PlatformKind.Kick,
                    "streamer",
                    "viewer",
                    "PartyHat",
                    DateTimeOffset.Now,
                    Emotes:
                    [
                        new ChatEmote(0, 8, "PartyHat", "https://example.com/party-hat.png")
                    ])
            ]);

            Assert.Equal(0, Volatile.Read(ref synchronousCallbackCount));
            await TestWait.UntilAsync(() => Volatile.Read(ref callbackCount) == 1, TimeSpan.FromSeconds(2));
        }
        finally
        {
            catalogChanged.RemoveEventHandler(catalog, handler);
        }
    }),
    ("docked chat renders Unicode emoji as emoji text instead of emote images", async () =>
    {
        await TestSta.RunAsync(() =>
        {
            var face = char.ConvertFromUtf32(0x1F602);
            var heart = "\u2764\uFE0F";
            var thumbsUpMediumSkinTone = char.ConvertFromUtf32(0x1F44D) + char.ConvertFromUtf32(0x1F3FD);
            var foldedHands = char.ConvertFromUtf32(0x1F64F);
            var apple = char.ConvertFromUtf32(0x1F34E);
            var family = char.ConvertFromUtf32(0x1F468) +
                "\u200D" +
                char.ConvertFromUtf32(0x1F469) +
                "\u200D" +
                char.ConvertFromUtf32(0x1F467) +
                "\u200D" +
                char.ConvertFromUtf32(0x1F466);
            var keycapOne = "1\uFE0F\u20E3";
            var messageText = $"hello{face}! {heart} {thumbsUpMediumSkinTone} {foldedHands} {apple} {family} {keycapOne} text";
            var block = new StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock
            {
                ChatFontSize = 14,
                Message = new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "viewer",
                    messageText,
                    DateTimeOffset.Now,
                    "#8AB4F8")
            };

            var emoteImages = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<StreamlinkVlcStudio.App.Wpf.Controls.AnimatedEmoteImage>()
                .ToArray();
            var emojiImages = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<Image>()
                .Where(image => image is not StreamlinkVlcStudio.App.Wpf.Controls.AnimatedEmoteImage)
                .ToArray();
            var renderedText = string.Concat(block.Inlines.Select(inline => inline switch
            {
                Run run => run.Text,
                InlineUIContainer { Child: Image { ToolTip: string emojiText } } => emojiText,
                _ => ""
            }));

            Assert.Equal(0, emoteImages.Length);
            Assert.Equal(7, emojiImages.Length);
            Assert.Contains(messageText, renderedText);
            Assert.True(emojiImages.Any(image => string.Equals(image.ToolTip as string, face, StringComparison.Ordinal)));
            Assert.True(emojiImages.Any(image => string.Equals(image.ToolTip as string, heart, StringComparison.Ordinal)));
            Assert.True(emojiImages.Any(image => string.Equals(image.ToolTip as string, thumbsUpMediumSkinTone, StringComparison.Ordinal)));
            Assert.True(emojiImages.Any(image => string.Equals(image.ToolTip as string, foldedHands, StringComparison.Ordinal)));
            Assert.True(emojiImages.Any(image => string.Equals(image.ToolTip as string, apple, StringComparison.Ordinal)));
            Assert.True(emojiImages.Any(image => string.Equals(image.ToolTip as string, family, StringComparison.Ordinal)));
            Assert.True(emojiImages.Any(image => string.Equals(image.ToolTip as string, keycapOne, StringComparison.Ordinal)));
            var faceImage = emojiImages.Single(image => string.Equals(image.ToolTip as string, face, StringComparison.Ordinal));
            var heartImage = emojiImages.Single(image => string.Equals(image.ToolTip as string, heart, StringComparison.Ordinal));
            var foldedHandsImage = emojiImages.Single(image => string.Equals(image.ToolTip as string, foldedHands, StringComparison.Ordinal));
            var appleImage = emojiImages.Single(image => string.Equals(image.ToolTip as string, apple, StringComparison.Ordinal));
            var coloredPixelCounts = emojiImages
                .Select(image => BitmapAssert.CountColoredPixels(image.Source))
                .ToArray();
            Assert.True(
                coloredPixelCounts.All(count => count > 20),
                $"Expected colored emoji images, got color counts [{string.Join(", ", coloredPixelCounts)}].");
            Assert.True(
                BitmapAssert.CountPixels(faceImage.Source, (r, g, b) => r > 180 && g > 100 && b < 140) > 20,
                "The grinning face must retain Segoe UI Emoji's yellow color palette.");
            Assert.True(
                BitmapAssert.CountPixels(heartImage.Source, (r, g, b) => r > 150 && g < 120 && b < 140) > 20,
                "The heart must retain Segoe UI Emoji's red color palette.");
            Assert.True(
                BitmapAssert.CountPixels(foldedHandsImage.Source, (r, g, b) => r > 160 && g > 80 && b < 100) > 20,
                "Folded hands must retain Segoe UI Emoji's yellow color palette.");
            Assert.True(
                BitmapAssert.CountPixels(appleImage.Source, (r, g, b) => r > 150 && g < 120 && b < 120) > 20,
                "The apple must retain Segoe UI Emoji's red color palette.");
        });
    }),
    ("caps tab chat messages at newest 100", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var chatFactory = new FakeChatClientFactory();
        var target = StreamInputParser.Parse("xqc", PlatformKind.Twitch);
        var tab = TestViewModels.CreateTab(
            target,
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);

        for (var index = 0; index < 105; index++)
        {
            chatFactory.Client.Receive(new ChatMessage(
                target.Platform,
                target.Channel,
                $"viewer{index}",
                $"message {index}",
                DateTimeOffset.Now,
                "#8AB4F8"));
        }

        Assert.Equal(100, tab.ChatMessages.Count);
        Assert.Equal("message 5", tab.ChatMessages.First().Message);
        Assert.Equal("message 104", tab.ChatMessages.Last().Message);
        await tab.DisposeAsync();
    }),
    ("waits for video surface before starting playback", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;

        var startTask = tab.StartAsync(settings);
        await Task.Delay(100);
        tab.SetVideoHandle(new IntPtr(1234));
        await startTask;

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(new IntPtr(1234), playbackFactory.Engine!.VideoHandle);
        Assert.True(streamlink.Started);
        Assert.True(playbackFactory.Engine.Played);
        await tab.DisposeAsync();
    }),
    ("stale video surface unload cannot clear a newer rehosted video handle", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;

        var mainSurfaceHandle = new IntPtr(1234);
        var detachedSurfaceHandle = new IntPtr(5678);
        tab.SetVideoHandle(mainSurfaceHandle);
        await tab.StartAsync(settings);

        tab.SetVideoHandle(detachedSurfaceHandle);
        tab.ClearVideoHandle(mainSurfaceHandle);

        Assert.Equal(detachedSurfaceHandle, playbackFactory.Engine!.VideoHandle);

        tab.ClearVideoHandle(detachedSurfaceHandle);

        var parkedHandle = playbackFactory.Engine.VideoHandle;
        Assert.True(parkedHandle != IntPtr.Zero);
        Assert.True(parkedHandle != detachedSurfaceHandle);

        tab.SetVideoHandle(mainSurfaceHandle);

        Assert.Equal(mainSurfaceHandle, playbackFactory.Engine.VideoHandle);
        await tab.DisposeAsync();
    }),
    ("starts Streamlink resolution before video surface is ready", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var streamlinkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamlinkReady = new TaskCompletionSource<IStreamTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        streamlink.StartExternalHttpOverride = (_, cancellationToken) =>
        {
            streamlinkStarted.TrySetResult();
            return streamlinkReady.Task.WaitAsync(cancellationToken);
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;

        var startTask = tab.StartAsync(settings);
        await streamlinkStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.Equal(PlaybackStatus.Resolving, tab.Status);
        Assert.True(streamlink.Started);
        await TestWait.UntilAsync(() => playbackFactory.Engine is not null, TimeSpan.FromMilliseconds(500));
        Assert.Equal(false, playbackFactory.Engine!.Played);

        tab.SetVideoHandle(new IntPtr(1234));
        streamlinkReady.SetResult(new FakeTransportSession());
        await startTask;

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.True(playbackFactory.Engine.Played);
        await tab.DisposeAsync();
    }),
    ("stream start uses parking video surface after expected surface is removed", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var streamlinkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamlinkReady = new TaskCompletionSource<IStreamTransportSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        streamlink.StartExternalHttpOverride = (_, cancellationToken) =>
        {
            streamlinkStarted.TrySetResult();
            return streamlinkReady.Task.WaitAsync(cancellationToken);
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("linny", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;

        var firstSurfaceHandle = new IntPtr(1234);
        var rehostedSurfaceHandle = new IntPtr(5678);
        tab.SetVideoPlacement(visible: true, row: 0, column: 0, rowSpan: 1, columnSpan: 1);
        tab.SetVideoHandle(firstSurfaceHandle);
        var startTask = tab.StartAsync(settings);
        await streamlinkStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(
            () => playbackFactory.Engine?.VideoHandle == firstSurfaceHandle,
            TimeSpan.FromMilliseconds(500));

        tab.SetVideoPlacement(visible: false, row: 0, column: 0, rowSpan: 1, columnSpan: 1);
        tab.ClearVideoHandle(firstSurfaceHandle);
        streamlinkReady.SetResult(new FakeTransportSession());
        await startTask.WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal("", tab.ErrorMessage);
        Assert.True(playbackFactory.Engine!.Played);
        Assert.True(playbackFactory.Engine.VideoHandle != IntPtr.Zero);
        Assert.True(playbackFactory.Engine.VideoHandle != firstSurfaceHandle);

        tab.SetVideoPlacement(visible: true, row: 0, column: 0, rowSpan: 1, columnSpan: 1);
        tab.SetVideoHandle(rehostedSurfaceHandle);

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(rehostedSurfaceHandle, playbackFactory.Engine.VideoHandle);
        await tab.DisposeAsync();
    }),
    ("enables native overlay engine for Kick playback", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/some-channel", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);

        Assert.Equal(true, playbackFactory.LastEnableNativeOverlay);
        await tab.DisposeAsync();
    }),
    ("uses stable per-stream native overlay position path", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        var firstPath = playbackFactory.LastNativeOverlayPositionStatePath;
        await tab.StartAsync(settings);
        var secondPath = playbackFactory.LastNativeOverlayPositionStatePath;

        Assert.NotNull(firstPath);
        Assert.Equal(firstPath, secondPath);
        Assert.Contains("twitch-albralelie", firstPath!);
        await tab.DisposeAsync();
    }),
    ("VLC plugin overlay starts without diagnostic placeholder", () =>
    {
        var engineType = typeof(StreamlinkVlcStudio.Infrastructure.Vlc.LibVlcPlaybackEngine);
        var subSourceMethod = engineType.GetMethod(
            "BuildOverlaySubSourceOption",
            BindingFlags.Static | BindingFlags.NonPublic);
        var optionsMethod = engineType.GetMethod(
            "BuildLibVlcOptions",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(subSourceMethod);
        Assert.NotNull(optionsMethod);

        var option = (string)subSourceMethod!.Invoke(null, ["svs_test", @"C:\state\overlay.txt"])!;
        var options = (IReadOnlyList<string>)optionsMethod!.Invoke(null, null)!;

        Assert.Contains("show-placeholder=0", option);
        Assert.DoesNotContain("show-placeholder=1", option);
        Assert.Equal(false, options.Any(item => item.StartsWith("--myoverlay-", StringComparison.Ordinal)));
        Assert.Equal(false, options.Any(item => item == "--myoverlay-show-placeholder=1"));
        return Task.CompletedTask;
    }),
    ("overlay mode falls back to docked chat when native overlay is unavailable", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var chatFactory = new FakeChatClientFactory();
        var target = StreamInputParser.Parse("albralelie", PlatformKind.Twitch);
        var tab = TestViewModels.CreateTab(
            target,
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            logger,
            action => action());

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Overlay;

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(() => chatFactory.Client.Connected, TimeSpan.FromMilliseconds(500));

        chatFactory.Client.Receive(new ChatMessage(
            target.Platform,
            target.Channel,
            "viewer",
            "this should land in docked chat",
            DateTimeOffset.Now,
            "#8AB4F8"));

        Assert.Equal(true, playbackFactory.LastEnableNativeOverlay);
        Assert.Equal(false, playbackFactory.Engine!.UsesNativeOverlay);
        Assert.Equal(true, tab.IsDockedChatOverrideActive);
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "this should land in docked chat"));
        await tab.DisposeAsync();
    }),
    ("audible VLC audio convergence keeps retrying after accepted calls", () =>
    {
        var engineType = typeof(StreamlinkVlcStudio.Infrastructure.Vlc.LibVlcPlaybackEngine);
        var method = engineType.GetMethod(
            "ShouldStopAudioStateConvergence",
            BindingFlags.Static | BindingFlags.NonPublic);
        var resultType = engineType.GetNestedType("AudioApplyResult", BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.NotNull(resultType);

        var pending = Enum.Parse(resultType!, "Pending");
        var converged = Enum.Parse(resultType!, "Converged");
        var stale = Enum.Parse(resultType!, "Stale");

        Assert.Equal(false, (bool)method!.Invoke(null, [PlaybackAudioState.Audible, pending])!);
        Assert.Equal(false, (bool)method.Invoke(null, [PlaybackAudioState.Audible, converged])!);
        Assert.Equal(true, (bool)method.Invoke(null, [PlaybackAudioState.Audible, stale])!);
        Assert.Equal(false, (bool)method.Invoke(null, [PlaybackAudioState.Muted, pending])!);
        Assert.Equal(true, (bool)method.Invoke(null, [PlaybackAudioState.Muted, converged])!);
        Assert.Equal(true, (bool)method.Invoke(null, [PlaybackAudioState.HardMuted, converged])!);
        return Task.CompletedTask;
    }),
    ("bundled VLC overlay directory is selected when configured path is missing", () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            var appBase = Path.Combine(root, "app");
            var bundledOverlay = CreateValidOverlayDirectory(Path.Combine(appBase, "vlc-overlay"));
            var missingOverlay = Path.Combine(root, "missing-overlay");

            var resolved = VlcOverlayDirectoryResolver.TryResolve(missingOverlay, appBase);

            Assert.Equal(
                VlcOverlayDirectoryResolver.NormalizeDirectory(bundledOverlay),
                resolved);
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }

        return Task.CompletedTask;
    }),
    ("valid configured VLC overlay directory wins over bundled directory", () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            var appBase = Path.Combine(root, "app");
            CreateValidOverlayDirectory(Path.Combine(appBase, "vlc-overlay"));
            var configuredOverlay = CreateValidOverlayDirectory(Path.Combine(root, "configured-overlay"));

            var resolved = VlcOverlayDirectoryResolver.TryResolve(configuredOverlay, appBase);

            Assert.Equal(
                VlcOverlayDirectoryResolver.NormalizeDirectory(configuredOverlay),
                resolved);
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }

        return Task.CompletedTask;
    }),
    ("embedded VLC overlay bundle extracts to a valid overlay directory", () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            Assert.True(VlcOverlayBundledResourceExtractor.HasBundledOverlayResources());

            var extracted = VlcOverlayBundledResourceExtractor.TryExtract(new MemoryLogger(), root);

            Assert.NotNull(extracted);
            Assert.Equal(
                VlcOverlayDirectoryResolver.NormalizeDirectory(
                    Path.Combine(root, VlcOverlayBundledResourceExtractor.ExtractedOverlayDirectoryName)),
                extracted);
            Assert.True(VlcOverlayDirectoryResolver.IsValidOverlayDirectory(extracted));
            Assert.True(File.Exists(VlcOverlayDirectoryResolver.GetPluginPath(extracted!)));
            Assert.True(File.Exists(VlcOverlayDirectoryResolver.GetControllerPath(extracted!)));
            Assert.True(VlcOverlayBundledResourceExtractor.IsExtractedOverlayCurrent(root));
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }

        return Task.CompletedTask;
    }),
    ("VLC overlay plugin runtime recopies same-length DLL by hash and invalidates stale cache", async () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            var vlcDirectory = Path.Combine(root, "vlc");
            var appDataDirectory = Path.Combine(root, "appdata");
            Directory.CreateDirectory(vlcDirectory);
            Directory.CreateDirectory(appDataDirectory);
            var overlayDirectory = CreateValidOverlayDirectory(Path.Combine(root, "overlay"));
            var sourcePlugin = VlcOverlayDirectoryResolver.GetPluginPath(overlayDirectory);
            File.WriteAllBytes(sourcePlugin, [1, 2, 3]);
            var pluginRoot = Path.Combine(appDataDirectory, "vlc-overlay-plugins");
            var targetPlugin = Path.Combine(pluginRoot, "spu", "libmyoverlay_plugin.dll");
            var cachePath = Path.Combine(pluginRoot, "plugins.dat");
            var logger = new MemoryLogger();

            Task<VlcOverlayPluginRuntime?> PrepareAsync() =>
                VlcOverlayPluginRuntimeFactory.TryPrepareAsync(
                    vlcDirectory,
                    overlayDirectory,
                    logger,
                    appDataDirectory);

            Assert.NotNull(await PrepareAsync());
            Assert.SequenceEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(targetPlugin));

            Directory.CreateDirectory(pluginRoot);
            File.WriteAllText(cachePath, "stale plugin cache");
            File.WriteAllBytes(sourcePlugin, [9, 8, 7]);
            File.SetLastWriteTimeUtc(sourcePlugin, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(targetPlugin, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var runtime = await PrepareAsync();
            Assert.NotNull(runtime);

            Assert.SequenceEqual(new byte[] { 9, 8, 7 }, File.ReadAllBytes(targetPlugin));
            Assert.Equal(false, File.Exists(cachePath));
            var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(targetPlugin)));
            Assert.Equal(expectedHash, runtime!.PluginSha256);
            Assert.True(
                logger.Entries.Any(entry =>
                    entry.Message.Contains("pluginSha256=" + expectedHash, StringComparison.Ordinal) &&
                    entry.Message.Contains(targetPlugin, StringComparison.Ordinal)),
                "Expected plugin runtime preparation to log the active cached plugin hash.");
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }
    }),
    ("playback restart compares resolved VLC overlay directory", async () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            var overlay = CreateValidOverlayDirectory(Path.Combine(root, "vlc-overlay"));
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
            };
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Overlay;
            settings.Chat.VlcOverlayDirectory = Path.Combine(overlay, ".");

            var tab = CreateTestStreamTab();
            tab.SetVideoHandle(new IntPtr(1234));
            await tab.StartAsync(settings);

            settings.Chat.VlcOverlayDirectory = overlay;

            Assert.Equal(false, tab.ShouldRestartPlaybackForChatOverlaySettings(settings));
            await tab.DisposeAsync();
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }
    }),
    ("release package zip includes runtime payload and user documentation", async () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            var repoRoot = FindRepoRoot();
            var scriptPath = Path.Combine(repoRoot, "scripts", "package-release.ps1");
            var overlaySource = CreateValidOverlayDirectory(Path.Combine(root, "overlay-source"));
            var publishDir = Path.Combine(root, "publish");
            Directory.CreateDirectory(publishDir);
            File.WriteAllText(Path.Combine(publishDir, "StreamlinkVlcStudio.App.Wpf.exe"), "fake exe");
            File.WriteAllText(Path.Combine(publishDir, "debug.log"), "do not ship");
            File.WriteAllText(Path.Combine(publishDir, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(publishDir, "Microsoft.IdentityModel.Tokens.dll"), "runtime dependency");
            File.WriteAllText(Path.Combine(publishDir, "oauth-token.json"), "do not ship");
            File.WriteAllText(Path.Combine(publishDir, ".env"), "do not ship");
            File.WriteAllText(Path.Combine(publishDir, "production.env"), "do not ship");
            File.WriteAllText(Path.Combine(publishDir, ".env.local"), "do not ship");
            var outputRoot = Path.Combine(root, "release");

            var result = await RunPowerShellAsync(
                [
                    "-ExecutionPolicy", "Bypass",
                    "-File", scriptPath,
                    "-PublishedAppDirectory", publishDir,
                    "-OverlaySource", overlaySource,
                    "-OutputRoot", outputRoot,
                    "-SkipAuthenticodeWhenUnavailable",
                    "-Quiet"
                ],
                TimeSpan.FromSeconds(30));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Package script failed: {result.Output} {result.Error}".Trim());
            }

            var zipPath = Path.Combine(outputRoot, "StreamlinkVlcStudio-release.zip");
            Assert.True(File.Exists(zipPath), $"Expected package zip at '{zipPath}'.");

            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .ToArray();
            Assert.True(entries.Contains("vlc-overlay/build/libmyoverlay_plugin.dll", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("vlc-overlay/build/vlc_chat_overlay.exe", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("browser-extension/manifest.json", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("browser-extension/background.js", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("browser-extension/content-core.js", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("browser-extension/content.js", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("StreamlinkVlcStudio.exe", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("Uninstall.exe", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("install.ps1", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("README.md", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("install.txt", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("THIRD-PARTY-NOTICES.md", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("Microsoft.IdentityModel.Tokens.dll", StringComparer.OrdinalIgnoreCase));
            Assert.True(entries.Contains("browser-extension/README.md", StringComparer.OrdinalIgnoreCase));
            Assert.Equal(false, entries.Contains("debug.log", StringComparer.OrdinalIgnoreCase));
            Assert.Equal(false, entries.Contains("settings.json", StringComparer.OrdinalIgnoreCase));
            Assert.Equal(false, entries.Contains("oauth-token.json", StringComparer.OrdinalIgnoreCase));
            Assert.Equal(false, entries.Contains(".env", StringComparer.OrdinalIgnoreCase));
            Assert.Equal(false, entries.Contains("production.env", StringComparer.OrdinalIgnoreCase));
            Assert.Equal(false, entries.Contains(".env.local", StringComparer.OrdinalIgnoreCase));
            Assert.Equal(false, Directory.Exists(Path.Combine(outputRoot, "StreamlinkVlcStudio")));
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }
    }),
    ("release package refuses recursive staging inside published files", async () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            var repoRoot = FindRepoRoot();
            var scriptPath = Path.Combine(repoRoot, "scripts", "package-release.ps1");
            var overlaySource = CreateValidOverlayDirectory(Path.Combine(root, "overlay-source"));
            var publishDir = Path.Combine(root, "publish");
            Directory.CreateDirectory(publishDir);
            var publishedExe = Path.Combine(publishDir, "StreamlinkVlcStudio.App.Wpf.exe");
            File.WriteAllText(publishedExe, "keep me");

            var result = await RunPowerShellAsync(
                [
                    "-ExecutionPolicy", "Bypass",
                    "-File", scriptPath,
                    "-PublishedAppDirectory", publishDir,
                    "-OverlaySource", overlaySource,
                    "-OutputRoot", publishDir,
                    "-SkipAuthenticodeWhenUnavailable",
                    "-Quiet"
                ],
                TimeSpan.FromSeconds(15));

            Assert.True(result.ExitCode != 0, "Recursive package staging unexpectedly succeeded.");
            Assert.Contains("must not contain each other", result.Output + result.Error);
            Assert.Equal("keep me", File.ReadAllText(publishedExe));
            Assert.Equal(false, Directory.Exists(Path.Combine(publishDir, "StreamlinkVlcStudio")));
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }
    }),
    ("installer resolves the nested release zip in a GitHub Actions artifact", async () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            var repoRoot = FindRepoRoot();
            var installScriptPath = Path.Combine(repoRoot, "scripts", "install.ps1");
            var commonScriptPath = Path.Combine(repoRoot, "scripts", "lib", "common.ps1");
            var releasePayload = Path.Combine(root, "release-payload");
            Directory.CreateDirectory(releasePayload);
            var releaseContractPath = Path.Combine(repoRoot, "shared", "release-contract.json");
            using (var contract = JsonDocument.Parse(File.ReadAllText(releaseContractPath)))
            {
                foreach (var required in contract.RootElement
                    .GetProperty("payload")
                    .GetProperty("requiredFiles")
                    .EnumerateArray())
                {
                    var path = Path.Combine(
                        releasePayload,
                        required.GetString()!.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "stub");
                }
            }

            var artifactExtract = Path.Combine(root, "artifact-extract");
            var nestedPackageDirectory = Path.Combine(artifactExtract, "package");
            Directory.CreateDirectory(nestedPackageDirectory);
            ZipFile.CreateFromDirectory(
                releasePayload,
                Path.Combine(nestedPackageDirectory, "StreamlinkVlcStudio-release.zip"));
            File.WriteAllText(Path.Combine(artifactExtract, "StreamlinkVlcStudio-Setup.exe"), "outer setup");

            var command = string.Join(
                "; ",
                "$tokens = $null",
                "$errors = $null",
                $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({QuotePowerShellLiteral(installScriptPath)}, [ref]$tokens, [ref]$errors)",
                "$definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-AppPayloadRoot' }, $true)",
                $". {QuotePowerShellLiteral(commonScriptPath)}",
                $". {QuotePowerShellLiteral(Path.Combine(repoRoot, "scripts", "lib", "release-contract.ps1"))}",
                $"$script:ReleaseContract = Read-ReleaseContract {QuotePowerShellLiteral(releaseContractPath)}",
                "Invoke-Expression $definition.Extent.Text",
                $"Resolve-AppPayloadRoot {QuotePowerShellLiteral(artifactExtract)}");
            var result = await RunPowerShellAsync(["-Command", command], TimeSpan.FromSeconds(15));
            var output = result.Output.Trim();

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("", result.Error.Trim());
            Assert.True(File.Exists(Path.Combine(output, "StreamlinkVlcStudio.exe")));
            Assert.True(output.StartsWith(artifactExtract, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }
    }),
    ("installer excludes setup assets and confines temporary download paths", async () =>
    {
        var repoRoot = FindRepoRoot();
        var installScriptPath = Path.Combine(repoRoot, "scripts", "install.ps1");
        var commonScriptPath = Path.Combine(repoRoot, "scripts", "lib", "common.ps1");
        var command = string.Join(
            "; ",
            $". {QuotePowerShellLiteral(commonScriptPath)}",
            "$tokens = $null",
            "$errors = $null",
            $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({QuotePowerShellLiteral(installScriptPath)}, [ref]$tokens, [ref]$errors)",
            "$patternsParameter = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'AppAssetPatterns' } | Select-Object -First 1",
            "$patterns = @(Invoke-Expression $patternsParameter.DefaultValue.Extent.Text)",
            "$selectDefinition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Select-ReleaseAsset' }, $true)",
            "$pathDefinition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-TempDownloadPath' }, $true)",
            "Invoke-Expression $selectDefinition.Extent.Text",
            "Invoke-Expression $pathDefinition.Extent.Text",
            "$release = [pscustomobject]@{ assets = @([pscustomobject]@{ name = 'StreamlinkVlcStudio-Setup.exe' }) }",
            "$setupRejected = $false",
            "try { Select-ReleaseAsset $release $patterns 'app' | Out-Null } catch { $setupRejected = $true }",
            "if (-not $setupRejected) { throw 'Setup bootstrap matched the default app asset patterns.' }",
            "$script:TempRoot = 'C:\\safe-temp'",
            "$traversalRejected = $false",
            "try { Get-TempDownloadPath '..\\escape.exe' | Out-Null } catch { $traversalRejected = $true }",
            "if (-not $traversalRejected) { throw 'Traversing download file name was accepted.' }",
            "Get-TempDownloadPath 'asset.zip'");

        var result = await RunPowerShellAsync(["-Command", command], TimeSpan.FromSeconds(15));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error.Trim());
        Assert.Equal(@"C:\safe-temp\asset.zip", result.Output.Trim());
    }),
    ("MSI installer rejects unsafe output file names", async () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            var repoRoot = FindRepoRoot();
            var scriptPath = Path.Combine(repoRoot, "scripts", "build-installer.ps1");
            var payloadDir = Path.Combine(root, "payload");
            Directory.CreateDirectory(payloadDir);
            File.WriteAllText(Path.Combine(payloadDir, "StreamlinkVlcStudio.exe"), "fake exe");
            var releaseZip = Path.Combine(root, "release.zip");
            ZipFile.CreateFromDirectory(payloadDir, releaseZip);
            var outputRoot = Path.Combine(root, "output");
            var outsidePath = Path.Combine(root, "outside.msi");
            File.WriteAllText(outsidePath, "do not replace");

            foreach (var unsafeFileName in new[]
                     {
                         Path.Combine("..", "outside.msi"),
                         "setup:alternate-stream.msi",
                         "CON.msi"
                     })
            {
                var result = await RunPowerShellAsync(
                    [
                        "-ExecutionPolicy", "Bypass",
                        "-File", scriptPath,
                        "-ReleaseZip", releaseZip,
                        "-OutputRoot", outputRoot,
                        "-SetupFileName", unsafeFileName,
                        "-ProductVersion", "1.0.0",
                        "-Quiet"
                    ],
                    TimeSpan.FromSeconds(15));

                Assert.True(result.ExitCode != 0, $"Unsafe setup file name unexpectedly succeeded: {unsafeFileName}");
                Assert.Contains("must be a leaf .msi file name", result.Output + result.Error);
            }
            Assert.Equal("do not replace", File.ReadAllText(outsidePath));
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }
    }),
    ("uninstaller script creates IExpress uninstall executable", async () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            var repoRoot = FindRepoRoot();
            var scriptPath = Path.Combine(repoRoot, "scripts", "build-uninstaller.ps1");
            var outputPath = Path.Combine(root, "output with spaces", "Uninstall.exe");

            var unsafeResult = await RunPowerShellAsync(
                [
                    "-ExecutionPolicy", "Bypass",
                    "-File", scriptPath,
                    "-OutputPath", Path.Combine(root, "CON.exe"),
                    "-Quiet"
                ],
                TimeSpan.FromSeconds(15));
            Assert.True(unsafeResult.ExitCode != 0, "Reserved-device uninstaller output unexpectedly succeeded.");
            Assert.Contains("safe .exe file name", unsafeResult.Output + unsafeResult.Error);

            var result = await RunPowerShellAsync(
                [
                    "-ExecutionPolicy", "Bypass",
                    "-File", scriptPath,
                    "-OutputPath", outputPath,
                    "-Quiet"
                ],
                TimeSpan.FromSeconds(30));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Uninstaller script failed: {result.Output} {result.Error}".Trim());
            }

            Assert.True(File.Exists(outputPath), $"Expected uninstall executable at '{outputPath}'.");
            Assert.True(new FileInfo(outputPath).Length > 0);
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }
    }),
    ("MSI installer requires a complete release payload", async () =>
    {
        var root = CreateTempTestDirectory();
        try
        {
            var repoRoot = FindRepoRoot();
            var scriptPath = Path.Combine(repoRoot, "scripts", "build-installer.ps1");
            var payloadDir = Path.Combine(root, "payload with spaces");
            Directory.CreateDirectory(payloadDir);
            File.WriteAllText(Path.Combine(payloadDir, "StreamlinkVlcStudio.exe"), "fake exe");
            File.WriteAllText(Path.Combine(payloadDir, "install.ps1"), "param()");
            var releaseZip = Path.Combine(root, "StreamlinkVlcStudio-release.zip");
            ZipFile.CreateFromDirectory(payloadDir, releaseZip);
            var outputRoot = Path.Combine(root, "installer output with spaces");

            var result = await RunPowerShellAsync(
                [
                    "-ExecutionPolicy", "Bypass",
                    "-File", scriptPath,
                    "-ReleaseZip", releaseZip,
                    "-OutputRoot", outputRoot,
                    "-SetupFileName", "TestSetup.msi",
                    "-ProductVersion", "1.0.0",
                    "-Quiet"
                ],
                TimeSpan.FromSeconds(30));

            Assert.True(result.ExitCode != 0, "Incomplete release payload unexpectedly produced an MSI.");
            Assert.Contains("Release payload is missing required file", result.Output + result.Error);
            Assert.Equal(false, File.Exists(Path.Combine(outputRoot, "TestSetup.msi")));
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }
    }),
    ("native overlay updates ignore unstarted overlay process handles", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var engine = new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = "svs_test"
        };
        var settings = new AppSettings();
        settings.Chat.Layout = ChatLayout.Overlay;

        var playbackEngineField = typeof(StreamTabViewModel).GetField(
            "playbackEngine",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var chatSettingsField = typeof(StreamTabViewModel).GetField(
            "chatSettings",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var nativeOverlayProcessField = typeof(StreamTabViewModel).GetField(
            "nativeOverlayProcess",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var nativeOverlayPipeNameField = typeof(StreamTabViewModel).GetField(
            "nativeOverlayPipeName",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var updateNativeChatOverlay = typeof(StreamTabViewModel).GetMethod(
            "UpdateNativeChatOverlay",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var isNativeOverlayChatCurrent = typeof(StreamTabViewModel).GetMethod(
            "IsNativeOverlayChatCurrent",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(playbackEngineField);
        Assert.NotNull(chatSettingsField);
        Assert.NotNull(nativeOverlayProcessField);
        Assert.NotNull(nativeOverlayPipeNameField);
        Assert.NotNull(updateNativeChatOverlay);
        Assert.NotNull(isNativeOverlayChatCurrent);

        playbackEngineField!.SetValue(tab, engine);
        chatSettingsField!.SetValue(tab, settings.Chat);
        nativeOverlayProcessField!.SetValue(tab, new Process());
        nativeOverlayPipeNameField!.SetValue(tab, "svs_test");

        updateNativeChatOverlay!.Invoke(tab, []);
        var isCurrent = (bool)isNativeOverlayChatCurrent!.Invoke(tab, [settings])!;

        Assert.Equal(false, isCurrent);        await tab.DisposeAsync();
    }),
    ("libVLC options use non-DXGI video output with hardware decode", () =>
    {
        var buildOptions = typeof(LibVlcPlaybackEngine).GetMethod(
            "BuildLibVlcOptions",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildOptions);

        var options = (IReadOnlyList<string>)buildOptions!.Invoke(null, null)!;

        Assert.True(options.Any(option => option == "--vout=wingdi"));
        Assert.Equal(false, options.Any(option =>
            option.Contains("direct3d", StringComparison.OrdinalIgnoreCase) ||
            option.Contains("dxgi", StringComparison.OrdinalIgnoreCase)));
        Assert.True(options.Any(option => option == "--avcodec-hw=any"));
        Assert.Equal(false, options.Any(option => option == "--avcodec-hw=none"));
        return Task.CompletedTask;
    }),
    ("reuses cached Kick overlay channel info for launch keys", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/some-channel", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var infoType = typeof(StreamTabViewModel).GetNestedType("KickOverlayChannelInfo", BindingFlags.NonPublic);
        Assert.NotNull(infoType);
        var info = Activator.CreateInstance(
            infoType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["123", 456L],
            culture: null);
        Assert.NotNull(info);

        var cacheMethod = typeof(StreamTabViewModel).GetMethod(
            "CacheResolvedKickOverlayChannelInfo",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(cacheMethod);
        cacheMethod!.Invoke(tab, [info]);

        var settings = new AppSettings();
        settings.StreamVlcOverlayFontSizes[tab.Target.StateKey] = 22;
        var buildKeyMethod = typeof(StreamTabViewModel).GetMethod(
            "BuildNativeOverlayLaunchKey",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(buildKeyMethod);
        var key = (string)buildKeyMethod!.Invoke(tab, [settings, null, null, null, null])!;

        Assert.Contains("|123|456|", key);
        Assert.Equal("22", key.Split('|')[4]);

        Assert.True(settings.Chat.SetKickChatroomId("some-channel", "789"));
        var overrideKey = (string)buildKeyMethod.Invoke(tab, [settings, null, null, null, null])!;

        Assert.Contains("|789|456|", overrideKey);
        await tab.DisposeAsync();
    }),
    ("VLC plugin overlay text size setting does not rebuild playback", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("albralelie", PlatformKind.Twitch));
        viewModel.SelectedTab!.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(
            () => playbackFactory.CreateCount == 1 &&
                playbackFactory.LastEnableNativeOverlay == true &&
                playbackFactory.Engine?.Played == true,
            TimeSpan.FromMilliseconds(500));

        viewModel.SelectedVlcOverlayFontSize = 24;

        Assert.Equal(1, playbackFactory.CreateCount);
        Assert.Equal(1, streamlink.StartCount);
        Assert.Equal(24d, settings.StreamVlcOverlayFontSizes["Twitch:albralelie"]);
        await viewModel.DisposeAsync();
    }),
    ("VLC plugin overlay text size is remembered per stream", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Overlay;
        settings.Chat.VlcOverlayDirectory = @"C:\Tools\vlc-overlay";
        settings.Chat.VlcOverlayFontSize = 15;

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("albralelie", PlatformKind.Twitch));
        var firstTab = viewModel.SelectedTab!;
        viewModel.SelectedVlcOverlayFontSize = 24;

        await viewModel.OpenDetectedStreamAsync(StreamInputParser.Parse("summit1g", PlatformKind.Twitch));
        var secondTab = viewModel.SelectedTab!;
        Assert.Equal(15d, viewModel.SelectedVlcOverlayFontSize);
        viewModel.SelectedVlcOverlayFontSize = 18;

        viewModel.SelectedTab = firstTab;
        Assert.Equal(24d, viewModel.SelectedVlcOverlayFontSize);
        viewModel.SelectedTab = secondTab;
        Assert.Equal(18d, viewModel.SelectedVlcOverlayFontSize);
        Assert.Equal(24d, settings.StreamVlcOverlayFontSizes["Twitch:albralelie"]);
        Assert.Equal(18d, settings.StreamVlcOverlayFontSizes["Twitch:summit1g"]);
        await viewModel.DisposeAsync();
    }),
    ("tab switching mutes inactive streams and leaves selected stream audible", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var engines = new Queue<FakePlaybackEngine>();
        var sharedAudio = new FakeSharedAudioState();
        var firstEngine = new FakePlaybackEngine { SharedAudioState = sharedAudio };
        var secondEngine = new FakePlaybackEngine { SharedAudioState = sharedAudio };
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);

        Assert.Equal(false, firstTab.IsMuted);
        Assert.Equal(false, secondTab.IsMuted);
        Assert.Equal(true, firstTab.IsAutoMuted);
        Assert.Equal(false, secondTab.IsAutoMuted);
        Assert.Equal(true, firstEngine.Muted);
        Assert.Equal(0, firstEngine.Volume);
        Assert.Equal(PlaybackAudioState.Muted, firstEngine.AudioState);
        Assert.Equal(false, firstEngine.AudioTrackEnabled);
        Assert.Equal(false, secondEngine.Muted);
        Assert.Equal(80, secondEngine.Volume);
        Assert.Equal(PlaybackAudioState.Audible, secondEngine.AudioState);
        Assert.Equal(true, secondEngine.AudioTrackEnabled);
        Assert.Equal(false, sharedAudio.Muted);
        Assert.Equal(80, sharedAudio.Volume);

        viewModel.SelectedTab = firstTab;
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Playing && !firstTab.PausedByTabSwitch,
            TimeSpan.FromMilliseconds(500));
        await TestWait.UntilAsync(
            () => !sharedAudio.Muted &&
                sharedAudio.Volume == 80 &&
                sharedAudio.AudioState == PlaybackAudioState.Audible,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(true, firstTab.IsSelected);
        Assert.Equal(false, secondTab.IsSelected);
        Assert.Equal(false, firstTab.IsAutoMuted);
        Assert.Equal(true, secondTab.IsAutoMuted);
        Assert.Equal(false, firstEngine.Muted);
        Assert.Equal(80, firstEngine.Volume);
        Assert.Equal(PlaybackAudioState.Audible, firstEngine.AudioState);
        Assert.Equal(true, firstEngine.AudioTrackEnabled);
        Assert.Equal(true, secondEngine.Muted);
        Assert.Equal(0, secondEngine.Volume);
        Assert.Equal(PlaybackAudioState.Muted, secondEngine.AudioState);
        Assert.Equal(false, secondEngine.AudioTrackEnabled);
        Assert.Equal(false, sharedAudio.Muted);
        Assert.Equal(80, sharedAudio.Volume);
        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
    }),
    ("tab strip selection restores selected tab audio", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var engines = new Queue<FakePlaybackEngine>();
        var firstEngine = new FakePlaybackEngine();
        var secondEngine = new FakePlaybackEngine();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);

        var firstTabStripItem = viewModel.TabStripItems.Single(item => item.Contains(firstTab));
        viewModel.SelectedTabStripItem = firstTabStripItem;

        Assert.Equal(firstTab, viewModel.SelectedTab);
        Assert.Equal(false, firstEngine.Muted);
        Assert.Equal(80, firstEngine.Volume);
        Assert.Equal(PlaybackAudioState.Audible, firstEngine.AudioState);
        Assert.Equal(true, firstEngine.AudioTrackEnabled);
        Assert.Equal(true, secondEngine.Muted);
        Assert.Equal(0, secondEngine.Volume);
        Assert.Equal(PlaybackAudioState.Muted, secondEngine.AudioState);
        Assert.Equal(false, secondEngine.AudioTrackEnabled);
        Assert.Equal(1, new[] { firstEngine, secondEngine }.Count(engine => !engine.Muted && engine.Volume > 0 && engine.AudioTrackEnabled));

        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
    }),
    ("tab switch resume reapplies selected tab audio after playback resumes", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var engines = new Queue<FakePlaybackEngine>();
        var firstEngine = new FakePlaybackEngine { IgnoreAudibleWhilePaused = true };
        var secondEngine = new FakePlaybackEngine();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            KeepInactiveTabsRunning = false
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Paused && firstTab.PausedByTabSwitch,
            TimeSpan.FromMilliseconds(500));

        viewModel.SelectedTab = firstTab;
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Playing && !firstTab.PausedByTabSwitch,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(false, firstTab.IsAutoMuted);
        Assert.Equal(true, secondTab.IsAutoMuted);
        Assert.Equal(false, firstEngine.Muted);
        Assert.Equal(80, firstEngine.Volume);
        Assert.Equal(PlaybackAudioState.Audible, firstEngine.AudioState);
        Assert.Equal(true, firstEngine.AudioTrackEnabled);
        Assert.Equal(true, secondEngine.Muted);
        Assert.Equal(0, secondEngine.Volume);
        Assert.Equal(PlaybackAudioState.Muted, secondEngine.AudioState);
        Assert.Equal(false, secondEngine.AudioTrackEnabled);

        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
    }),
    ("inactive playback policy coalesces rapid requests to latest visibility", async () =>
    {
        var dispatched = new Queue<Action>();
        void QueueDispatch(Action action) => dispatched.Enqueue(action);
        void PumpOneDispatchedAction()
        {
            if (dispatched.Count > 0)
            {
                dispatched.Dequeue()();
            }
        }

        var streamlink = new FakeStreamlinkService();
        var engines = new Queue<FakePlaybackEngine>();
        var firstEngine = new FakePlaybackEngine();
        var secondEngine = new FakePlaybackEngine();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            KeepInactiveTabsRunning = false,
            MultiStreamEnabled = false
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            QueueDispatch);
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);
        viewModel.SelectedTab = firstTab;
        viewModel.SelectedTab = secondTab;

        Assert.Equal(0, viewModel.InactivePlaybackPolicyApplyPassCount);
        Assert.True(dispatched.Count > 0);
        PumpOneDispatchedAction();
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, viewModel.InactivePlaybackPolicyApplyPassCount);
        Assert.Equal(PlaybackStatus.Paused, firstTab.Status);
        Assert.Equal(true, firstTab.PausedByTabSwitch);
        Assert.Equal(PlaybackStatus.Playing, secondTab.Status);
        Assert.Equal(false, secondTab.PausedByTabSwitch);
        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
        await viewModel.DisposeAsync();
    }),
    ("tab switching soft mutes previous selected stream before new selected audio", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var audioCalls = new ConcurrentQueue<FakePlaybackAudioCall>();
        var engines = new Queue<FakePlaybackEngine>();
        var sharedAudio = new FakeSharedAudioState();
        var firstEngine = new FakePlaybackEngine { AudioCallLog = audioCalls, SharedAudioState = sharedAudio };
        var secondEngine = new FakePlaybackEngine { AudioCallLog = audioCalls, SharedAudioState = sharedAudio };
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);

        audioCalls.Clear();
        viewModel.SelectedTab = firstTab;
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
        await TestWait.UntilAsync(
            () => !sharedAudio.Muted &&
                sharedAudio.Volume == 80 &&
                sharedAudio.AudioState == PlaybackAudioState.Audible,
            TimeSpan.FromMilliseconds(500));
        var calls = audioCalls.ToArray();

        Assert.True(calls.Length > 1);
        Assert.Equal(secondEngine, calls[0].Engine);
        Assert.Equal(PlaybackAudioState.Muted, calls[0].AudioState);
        Assert.Equal(firstEngine, calls[1].Engine);
        Assert.Equal(PlaybackAudioState.Audible, calls[1].AudioState);
        Assert.True(calls.Any(call => ReferenceEquals(call.Engine, secondEngine) && call.AudioState == PlaybackAudioState.Muted));
        Assert.Equal(firstEngine, calls[^1].Engine);
        Assert.Equal(PlaybackAudioState.Audible, calls[^1].AudioState);
        Assert.Equal(false, sharedAudio.Muted);
        Assert.Equal(80, sharedAudio.Volume);
        Assert.Equal(PlaybackAudioState.Audible, sharedAudio.AudioState);

        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
    }),
    ("tab switching reapplies soft mute to already inactive tabs", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var audioCalls = new ConcurrentQueue<FakePlaybackAudioCall>();
        var engines = new Queue<FakePlaybackEngine>();
        var sharedAudio = new FakeSharedAudioState();
        var firstEngine = new FakePlaybackEngine { AudioCallLog = audioCalls, SharedAudioState = sharedAudio };
        var secondEngine = new FakePlaybackEngine { AudioCallLog = audioCalls, SharedAudioState = sharedAudio };
        var thirdEngine = new FakePlaybackEngine { AudioCallLog = audioCalls, SharedAudioState = sharedAudio };
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        engines.Enqueue(thirdEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var thirdTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("aceu", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        thirdTab.SetVideoHandle(new IntPtr(9012));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);
        viewModel.Tabs.Add(thirdTab);
        viewModel.VideoTabs.Add(thirdTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);
        viewModel.SelectedTab = thirdTab;
        await thirdTab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => !firstTab.IsBusy && !secondTab.IsBusy && !thirdTab.IsBusy,
            TimeSpan.FromSeconds(1));
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(true, secondTab.IsAutoMuted);
        audioCalls.Clear();
        viewModel.SelectedTab = firstTab;
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
        await TestWait.UntilAsync(
            () => firstTab.Status == PlaybackStatus.Playing &&
                !firstTab.PausedByTabSwitch &&
                !firstTab.IsBusy,
            TimeSpan.FromSeconds(1));
        var calls = audioCalls.ToArray();

        Assert.True(calls.Length > 1);
        Assert.Equal(thirdEngine, calls[0].Engine);
        Assert.Equal(PlaybackAudioState.Muted, calls[0].AudioState);
        Assert.Equal(firstEngine, calls[1].Engine);
        Assert.Equal(PlaybackAudioState.Audible, calls[1].AudioState);
        Assert.True(calls.Any(call => ReferenceEquals(call.Engine, secondEngine) && call.AudioState == PlaybackAudioState.Muted));
        Assert.True(calls.Any(call => ReferenceEquals(call.Engine, thirdEngine) && call.AudioState == PlaybackAudioState.Muted));
        Assert.Equal(firstEngine, calls[^1].Engine);
        Assert.Equal(PlaybackAudioState.Audible, calls[^1].AudioState);
        Assert.Equal(false, sharedAudio.Muted);
        Assert.Equal(80, sharedAudio.Volume);
        Assert.Equal(PlaybackAudioState.Audible, sharedAudio.AudioState);

        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
        await thirdTab.DisposeAsync();
    }),
    ("late inactive tab audio reapply restores selected tab audio", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var audioCalls = new ConcurrentQueue<FakePlaybackAudioCall>();
        var engines = new Queue<FakePlaybackEngine>();
        var sharedAudio = new FakeSharedAudioState();
        var firstEngine = new FakePlaybackEngine { AudioCallLog = audioCalls, SharedAudioState = sharedAudio };
        var secondEngine = new FakePlaybackEngine { AudioCallLog = audioCalls, SharedAudioState = sharedAudio };
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);

        audioCalls.Clear();
        firstTab.ReapplyAudio();
        var calls = audioCalls.ToArray();

        Assert.True(calls.Length >= 2);
        Assert.Equal(firstEngine, calls[0].Engine);
        Assert.Equal(PlaybackAudioState.Muted, calls[0].AudioState);
        Assert.Equal(secondEngine, calls[^1].Engine);
        Assert.Equal(PlaybackAudioState.Audible, calls[^1].AudioState);
        Assert.Equal(true, firstEngine.Muted);
        Assert.Equal(0, firstEngine.Volume);
        Assert.Equal(PlaybackAudioState.Muted, firstEngine.AudioState);
        Assert.Equal(false, firstEngine.AudioTrackEnabled);
        Assert.Equal(false, secondEngine.Muted);
        Assert.Equal(80, secondEngine.Volume);
        Assert.Equal(PlaybackAudioState.Audible, secondEngine.AudioState);
        Assert.Equal(true, secondEngine.AudioTrackEnabled);
        Assert.Equal(false, sharedAudio.Muted);
        Assert.Equal(80, sharedAudio.Volume);
        Assert.Equal(PlaybackAudioState.Audible, sharedAudio.AudioState);

        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
    }),
    ("delayed inactive engine audio convergence restores selected tab audio", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var audioCalls = new ConcurrentQueue<FakePlaybackAudioCall>();
        var engines = new Queue<FakePlaybackEngine>();
        var sharedAudio = new FakeSharedAudioState();
        var firstEngine = new FakePlaybackEngine { AudioCallLog = audioCalls, SharedAudioState = sharedAudio };
        var secondEngine = new FakePlaybackEngine { AudioCallLog = audioCalls, SharedAudioState = sharedAudio };
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(true, firstTab.IsAutoMuted);
        Assert.Equal(false, secondTab.IsAutoMuted);
        Assert.Equal(false, sharedAudio.Muted);
        Assert.Equal(PlaybackAudioState.Audible, sharedAudio.AudioState);

        for (var iteration = 0; iteration < 32; iteration++)
        {
            audioCalls.Clear();
            firstEngine.SimulateAudioStateReapplied();
            await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));
            var calls = audioCalls.ToArray();

            Assert.True(calls.Length >= 2);
            Assert.Equal(firstEngine, calls[0].Engine);
            Assert.Equal(PlaybackAudioState.Muted, calls[0].AudioState);
            Assert.Equal(secondEngine, calls[^1].Engine);
            Assert.Equal(PlaybackAudioState.Audible, calls[^1].AudioState);
        }

        Assert.Equal(true, firstEngine.Muted);
        Assert.Equal(0, firstEngine.Volume);
        Assert.Equal(PlaybackAudioState.Muted, firstEngine.AudioState);
        Assert.Equal(false, firstEngine.AudioTrackEnabled);
        Assert.Equal(false, secondEngine.Muted);
        Assert.Equal(80, secondEngine.Volume);
        Assert.Equal(PlaybackAudioState.Audible, secondEngine.AudioState);
        Assert.Equal(true, secondEngine.AudioTrackEnabled);
        Assert.Equal(false, sharedAudio.Muted);
        Assert.Equal(80, sharedAudio.Volume);
        Assert.Equal(PlaybackAudioState.Audible, sharedAudio.AudioState);

        await viewModel.DisposeAsync();
    }),
    ("rapid tab switching leaves exactly one audible stream", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var engines = new Queue<FakePlaybackEngine>();
        var firstEngine = new FakePlaybackEngine();
        var secondEngine = new FakePlaybackEngine();
        var thirdEngine = new FakePlaybackEngine();
        engines.Enqueue(firstEngine);
        engines.Enqueue(secondEngine);
        engines.Enqueue(thirdEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var firstTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var secondTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var thirdTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("aceu", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        firstTab.SetVideoHandle(new IntPtr(1234));
        secondTab.SetVideoHandle(new IntPtr(5678));
        thirdTab.SetVideoHandle(new IntPtr(9012));
        viewModel.Tabs.Add(firstTab);
        viewModel.VideoTabs.Add(firstTab);
        viewModel.Tabs.Add(secondTab);
        viewModel.VideoTabs.Add(secondTab);
        viewModel.Tabs.Add(thirdTab);
        viewModel.VideoTabs.Add(thirdTab);

        viewModel.SelectedTab = firstTab;
        await firstTab.StartAsync(settings);
        viewModel.SelectedTab = secondTab;
        await secondTab.StartAsync(settings);
        viewModel.SelectedTab = thirdTab;
        await thirdTab.StartAsync(settings);

        viewModel.SelectedTab = firstTab;
        viewModel.SelectedTab = secondTab;
        viewModel.SelectedTab = thirdTab;
        viewModel.SelectedTab = firstTab;
        await viewModel.InactivePlaybackPolicyIdleTask.WaitAsync(TimeSpan.FromSeconds(2));

        var engineStates = new[] { firstEngine, secondEngine, thirdEngine };
        Assert.Equal(1, engineStates.Count(engine => !engine.Muted && engine.Volume > 0));
        Assert.Equal(false, firstEngine.Muted);
        Assert.Equal(80, firstEngine.Volume);
        Assert.Equal(PlaybackAudioState.Audible, firstEngine.AudioState);
        Assert.Equal(true, firstEngine.AudioTrackEnabled);
        Assert.Equal(true, secondEngine.Muted);
        Assert.Equal(0, secondEngine.Volume);
        Assert.Equal(PlaybackAudioState.Muted, secondEngine.AudioState);
        Assert.Equal(false, secondEngine.AudioTrackEnabled);
        Assert.Equal(true, thirdEngine.Muted);
        Assert.Equal(0, thirdEngine.Volume);
        Assert.Equal(PlaybackAudioState.Muted, thirdEngine.AudioState);
        Assert.Equal(false, thirdEngine.AudioTrackEnabled);

        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
        await thirdTab.DisposeAsync();
    }),
    ("selecting detached picture-in-picture tab follows selected-tab audio ownership", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var engines = new Queue<FakePlaybackEngine>();
        var sharedAudio = new FakeSharedAudioState();
        var appEngine = new FakePlaybackEngine { SharedAudioState = sharedAudio };
        var pipEngine = new FakePlaybackEngine { SharedAudioState = sharedAudio };
        engines.Enqueue(appEngine);
        engines.Enqueue(pipEngine);
        var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var appTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var pipTab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        appTab.SetVideoHandle(new IntPtr(1234));
        pipTab.SetVideoHandle(new IntPtr(5678));
        viewModel.Tabs.Add(appTab);
        viewModel.VideoTabs.Add(appTab);
        viewModel.Tabs.Add(pipTab);
        viewModel.VideoTabs.Add(pipTab);

        viewModel.SelectedTab = appTab;
        await appTab.StartAsync(settings);
        viewModel.SelectedTab = pipTab;
        await pipTab.StartAsync(settings);
        Assert.True(viewModel.SetTabsDetached([pipTab], detached: true));

        viewModel.SelectedTab = appTab;
        await TestWait.UntilAsync(
            () => !sharedAudio.Muted &&
                sharedAudio.Volume == 80 &&
                sharedAudio.AudioState == PlaybackAudioState.Audible,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(false, appTab.IsAutoMuted);
        Assert.Equal(true, pipTab.IsAutoMuted);
        Assert.Equal(false, appEngine.Muted);
        Assert.Equal(80, appEngine.Volume);
        Assert.Equal(true, appEngine.AudioTrackEnabled);
        Assert.Equal(true, pipEngine.Muted);
        Assert.Equal(0, pipEngine.Volume);
        Assert.Equal(false, pipEngine.AudioTrackEnabled);

        viewModel.SelectedTab = pipTab;
        await TestWait.UntilAsync(
            () => !sharedAudio.Muted &&
                sharedAudio.Volume == 80 &&
                sharedAudio.AudioState == PlaybackAudioState.Audible,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(true, appTab.IsAutoMuted);
        Assert.Equal(false, pipTab.IsAutoMuted);
        Assert.Equal(true, appEngine.Muted);
        Assert.Equal(0, appEngine.Volume);
        Assert.Equal(false, appEngine.AudioTrackEnabled);
        Assert.Equal(false, pipEngine.Muted);
        Assert.Equal(80, pipEngine.Volume);
        Assert.Equal(true, pipEngine.AudioTrackEnabled);
        Assert.Equal(false, sharedAudio.Muted);
        Assert.Equal(80, sharedAudio.Volume);
        await appTab.DisposeAsync();
        await pipTab.DisposeAsync();
    }),
    ("multi-stream grid shows the selected page of up to 16 video tabs", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var tabs = Enumerable.Range(1, 17)
            .Select(index => TestViewModels.CreateTab(
                StreamInputParser.Parse($"streamer{index}", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()))
            .ToArray();

        foreach (var tab in tabs.Take(6))
        {
            viewModel.Tabs.Add(tab);
        }

        viewModel.SelectedTab = tabs[4];

        Assert.Equal(2, viewModel.VideoGridRows);
        Assert.Equal(3, viewModel.VideoGridColumns);
        foreach (var tab in tabs.Take(6))
        {
            Assert.True(tab.IsVideoVisible);
        }

        Assert.Equal(1, tabs[4].VideoGridRow);
        Assert.Equal(1, tabs[4].VideoGridColumn);
        Assert.Equal(1, tabs[5].VideoGridRow);
        Assert.Equal(2, tabs[5].VideoGridColumn);

        foreach (var tab in tabs.Skip(6).Take(3))
        {
            viewModel.Tabs.Add(tab);
        }

        Assert.Equal(3, viewModel.VideoGridRows);
        Assert.Equal(3, viewModel.VideoGridColumns);
        foreach (var tab in tabs.Take(9))
        {
            Assert.True(tab.IsVideoVisible);
        }

        Assert.Equal(1, tabs[4].VideoGridRow);
        Assert.Equal(1, tabs[4].VideoGridColumn);
        Assert.Equal(2, tabs[8].VideoGridRow);
        Assert.Equal(2, tabs[8].VideoGridColumn);

        foreach (var tab in tabs.Skip(9))
        {
            viewModel.Tabs.Add(tab);
        }

        viewModel.SelectedTab = tabs[10];

        Assert.Equal(4, viewModel.VideoGridRows);
        Assert.Equal(4, viewModel.VideoGridColumns);
        foreach (var tab in tabs.Take(16))
        {
            Assert.True(tab.IsVideoVisible);
        }

        Assert.Equal(false, tabs[16].IsVideoVisible);
        Assert.Equal(0, tabs[0].VideoGridRow);
        Assert.Equal(0, tabs[0].VideoGridColumn);
        Assert.Equal(2, tabs[10].VideoGridRow);
        Assert.Equal(2, tabs[10].VideoGridColumn);
        Assert.Equal(3, tabs[15].VideoGridRow);
        Assert.Equal(3, tabs[15].VideoGridColumn);

        viewModel.IsMultiStreamEnabled = false;

        foreach (var tab in tabs.Where(tab => !ReferenceEquals(tab, tabs[10])))
        {
            Assert.Equal(false, tab.IsVideoVisible);
        }

        Assert.True(tabs[10].IsVideoVisible);
        Assert.Equal(2, viewModel.VideoGridRows);
        Assert.Equal(2, viewModel.VideoGridColumns);
        Assert.Equal(0, tabs[10].VideoGridRow);
        Assert.Equal(0, tabs[10].VideoGridColumn);
        Assert.Equal(2, tabs[10].VideoGridRowSpan);
        Assert.Equal(2, tabs[10].VideoGridColumnSpan);

        viewModel.IsMultiStreamEnabled = true;

        Assert.Equal(4, viewModel.VideoGridRows);
        Assert.Equal(4, viewModel.VideoGridColumns);
        foreach (var tab in tabs.Take(16))
        {
            Assert.True(tab.IsVideoVisible);
        }

        Assert.Equal(false, tabs[16].IsVideoVisible);

        viewModel.SelectedTab = tabs[16];

        foreach (var tab in tabs.Take(16))
        {
            Assert.Equal(false, tab.IsVideoVisible);
        }

        Assert.True(tabs[16].IsVideoVisible);
        Assert.Equal(2, viewModel.VideoGridRows);
        Assert.Equal(2, viewModel.VideoGridColumns);
        Assert.Equal(2, tabs[16].VideoGridRowSpan);
        Assert.Equal(2, tabs[16].VideoGridColumnSpan);

        viewModel.IsMultiStreamEnabled = false;

        Assert.True(tabs[16].IsVideoVisible);
        Assert.Equal(2, tabs[16].VideoGridRowSpan);
        Assert.Equal(2, tabs[16].VideoGridColumnSpan);
        return Task.CompletedTask;
    }),
    ("multi-stream ten-up uses equal-size no-crop tiles", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        var tabs = Enumerable.Range(1, 10)
            .Select(index => TestViewModels.CreateTab(
                StreamInputParser.Parse($"streamer{index}", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()))
            .ToArray();

        foreach (var tab in tabs)
        {
            viewModel.Tabs.Add(tab);
        }

        viewModel.SelectedTab = tabs[0];

        Assert.Equal(2, viewModel.VideoGridRows);
        Assert.Equal(5, viewModel.VideoGridColumns);
        foreach (var tab in tabs)
        {
            Assert.True(tab.IsVideoVisible);
            Assert.Equal(1, tab.VideoGridRowSpan);
            Assert.Equal(1, tab.VideoGridColumnSpan);
        }

        Assert.Equal(0, tabs[0].VideoGridRow);
        Assert.Equal(0, tabs[0].VideoGridColumn);
        Assert.Equal(1, tabs[1].VideoGridColumn);
        Assert.Equal(1, tabs[9].VideoGridRow);
        Assert.Equal(4, tabs[9].VideoGridColumn);
        AssertVideoGridFullyCovered(tabs, viewModel.VideoGridRows, viewModel.VideoGridColumns);
        return Task.CompletedTask;
    }),
    ("multi-stream requested six eight ten and twelve counts use equal-size no-crop grids", () =>
    {
        foreach (var (count, rows, columns) in new[] { (6, 2, 3), (8, 2, 4), (10, 2, 5), (12, 3, 4) })
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings { MultiStreamEnabled = true };
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tabs = Enumerable.Range(1, count)
                .Select(index => TestViewModels.CreateTab(
                    StreamInputParser.Parse($"streamer{index}", PlatformKind.Twitch),
                    "best",
                    streamlink,
                    playbackFactory,
                    new FakeChatClientFactory(),
                    logger,
                    action => action()))
                .ToArray();

            foreach (var tab in tabs)
            {
                viewModel.Tabs.Add(tab);
            }

            viewModel.SelectedTab = tabs[0];

            Assert.Equal(rows, viewModel.VideoGridRows);
            Assert.Equal(columns, viewModel.VideoGridColumns);
            AssertVideoGridFullyCovered(tabs, rows, columns);
            foreach (var tab in tabs)
            {
                Assert.Equal(1, tab.VideoGridRowSpan);
                Assert.Equal(1, tab.VideoGridColumnSpan);
            }
        }

        return Task.CompletedTask;
    }),
    ("main multi-stream native left click selects clicked stream for audio", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings
            {
                MultiStreamEnabled = true
            };
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                SetMainWindowHandle(window);

                var surfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).ToArray();
                var secondSurface = surfaces.First(surface => ReferenceEquals(surface.Tag, second));
                Assert.True(secondSurface.ActualWidth > 0);
                Assert.True(secondSurface.ActualHeight > 0);

                var secondPoint = secondSurface.PointToScreen(new System.Windows.Point(
                    secondSurface.ActualWidth / 2,
                    secondSurface.ActualHeight / 2));

                Assert.True(window.TryActivateVideoTabFromScreenClick(
                    (int)Math.Round(secondPoint.X),
                    (int)Math.Round(secondPoint.Y)));
                Assert.Equal(second, viewModel.SelectedTab);
                Assert.Equal(false, first.IsSelected);
                Assert.Equal(true, second.IsSelected);
                Assert.Equal(true, first.IsAutoMuted);
                Assert.Equal(false, second.IsAutoMuted);
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("fullscreen button targets multi-view when current video view has multiple streams", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings
            {
                MultiStreamEnabled = true
            };
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var window = new MainWindow
            {
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            try
            {
                var getMode = typeof(MainWindow).GetMethod(
                    "GetFullscreenButtonMode",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(getMode);

                Assert.Equal("MultiView", getMode!.Invoke(window, [])?.ToString());

                viewModel.IsMultiStreamEnabled = false;

                Assert.Equal("StreamOnly", getMode.Invoke(window, [])?.ToString());
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("main fullscreen stays below other windows and restores prior topmost state", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            viewModel.Tabs.Add(tab);
            viewModel.SelectedTab = tab;

            var window = new MainWindow
            {
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);
                Assert.True(window.Topmost, "WPF Topmost was not set before entering PiP fullscreen.");
                Assert.True(NativeWindowTest.IsTopmost(handle), "Native topmost style was not set before entering PiP fullscreen.");

                var toggleFullscreen = typeof(MainWindow).GetMethod(
                    "ToggleStreamFullscreenFromVideoDoubleClick",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(toggleFullscreen);
                Assert.Equal(true, toggleFullscreen!.Invoke(window, []));

                Assert.Equal(false, window.Topmost);
                Assert.Equal(false, NativeWindowTest.IsTopmost(handle));

                Assert.Equal(true, toggleFullscreen.Invoke(window, []));

                Assert.True(window.Topmost, "WPF Topmost was not restored after leaving PiP fullscreen.");
                Assert.True(NativeWindowTest.IsTopmost(handle), "Native topmost style was not restored after leaving PiP fullscreen.");
            }
            finally
            {
                window.Close();
            }
        });
    }),
    ("main theatre fullscreen marks shell taskbar fullscreen state until exit", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var previousTaskbarController = MainWindow.TaskbarFullscreenController;
            var taskbarController = new FakeTaskbarFullscreenController();
            MainWindow.TaskbarFullscreenController = taskbarController;
            var window = new MainWindow
            {
                Left = 240,
                Top = 180,
                Width = 900,
                Height = 620
            };
            RemoveMainWindowAutomaticStartup(window);

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);

                ToggleMainWindowFullscreen(window, "Theatre");
                Assert.SequenceEqual(new[] { (handle, true) }, taskbarController.Requests.ToArray());

                ToggleMainWindowFullscreen(window, "Theatre");
                Assert.SequenceEqual(
                    new[] { (handle, true), (handle, false) },
                    taskbarController.Requests.ToArray());
            }
            finally
            {
                ExitMainWindowFullscreenIfActive(window);
                window.Close();
                MainWindow.TaskbarFullscreenController = previousTaskbarController;
            }
        });
    }),
    ("main theatre fullscreen restores shell state after taskbar recreation", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var previousTaskbarController = MainWindow.TaskbarFullscreenController;
            var taskbarController = new FakeTaskbarFullscreenController();
            MainWindow.TaskbarFullscreenController = taskbarController;
            var window = new MainWindow
            {
                Left = 240,
                Top = 180,
                Width = 900,
                Height = 620
            };
            RemoveMainWindowAutomaticStartup(window);

            try
            {
                window.Show();
                window.UpdateLayout();
                AttachMainWindowMessageHook(window);
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);

                ToggleMainWindowFullscreen(window, "Theatre");
                var taskbarCreatedField = typeof(MainWindow).GetField(
                    "WmTaskbarCreated",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(taskbarCreatedField);
                var taskbarCreatedMessage = (int)taskbarCreatedField!.GetValue(null)!;
                Assert.True(taskbarCreatedMessage != 0);

                NativeWindowTest.SendMessage(
                    handle,
                    taskbarCreatedMessage,
                    IntPtr.Zero,
                    IntPtr.Zero);

                Assert.SequenceEqual(
                    new[] { (handle, true), (handle, true) },
                    taskbarController.Requests.ToArray());

                taskbarController.ReturnValue = false;
                NativeWindowTest.SendMessage(
                    handle,
                    taskbarCreatedMessage,
                    IntPtr.Zero,
                    IntPtr.Zero);
                taskbarController.ReturnValue = true;

                var activated = typeof(MainWindow).GetMethod(
                    "MainWindowActivated",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(activated);
                activated!.Invoke(window, [window, EventArgs.Empty]);

                Assert.SequenceEqual(
                    new[] { (handle, true), (handle, true), (handle, true), (handle, true) },
                    taskbarController.Requests.ToArray());

                ToggleMainWindowFullscreen(window, "Theatre");
                Assert.SequenceEqual(
                    new[]
                    {
                        (handle, true),
                        (handle, true),
                        (handle, true),
                        (handle, true),
                        (handle, false)
                    },
                    taskbarController.Requests.ToArray());
            }
            finally
            {
                ExitMainWindowFullscreenIfActive(window);
                window.Close();
                MainWindow.TaskbarFullscreenController = previousTaskbarController;
            }
        });
    }),
    ("main theatre fullscreen retries transient shell cleanup failure", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var previousTaskbarController = MainWindow.TaskbarFullscreenController;
            var taskbarController = new FakeTaskbarFullscreenController();
            MainWindow.TaskbarFullscreenController = taskbarController;
            var window = new MainWindow
            {
                Left = 240,
                Top = 180,
                Width = 900,
                Height = 620
            };
            RemoveMainWindowAutomaticStartup(window);

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);

                ToggleMainWindowFullscreen(window, "Theatre");
                taskbarController.ReturnValues.Enqueue(false);
                taskbarController.ReturnValues.Enqueue(true);
                ToggleMainWindowFullscreen(window, "Theatre");

                Assert.SequenceEqual(
                    new[] { (handle, true), (handle, false), (handle, false) },
                    taskbarController.Requests.ToArray());
            }
            finally
            {
                ExitMainWindowFullscreenIfActive(window);
                window.Close();
                MainWindow.TaskbarFullscreenController = previousTaskbarController;
            }
        });
    }),
    ("closing main theatre fullscreen clears shell taskbar state while its window is valid", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var previousTaskbarController = MainWindow.TaskbarFullscreenController;
            var taskbarController = new FakeTaskbarFullscreenController();
            MainWindow.TaskbarFullscreenController = taskbarController;
            var window = new MainWindow
            {
                Left = 240,
                Top = 180,
                Width = 900,
                Height = 620
            };
            RemoveMainWindowHandler<System.Windows.RoutedEventHandler>(
                window,
                nameof(System.Windows.Window.Loaded),
                "MainWindowLoaded");
            RemoveMainWindowHandler<EventHandler>(
                window,
                nameof(System.Windows.Window.SourceInitialized),
                "MainWindowSourceInitialized");
            var windowClosed = false;

            try
            {
                window.Show();
                window.UpdateLayout();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(handle != IntPtr.Zero);

                ToggleMainWindowFullscreen(window, "Theatre");
                Assert.SequenceEqual(new[] { (handle, true) }, taskbarController.Requests.ToArray());

                var closeConfirmedField = typeof(MainWindow).GetField(
                    "closeConfirmed",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(closeConfirmedField);
                closeConfirmedField!.SetValue(window, true);

                var clearedBeforeHandleDestruction = false;
                window.Closing += (_, _) =>
                {
                    clearedBeforeHandleDestruction =
                        new System.Windows.Interop.WindowInteropHelper(window).Handle == handle &&
                        taskbarController.Requests.SequenceEqual(
                            new[] { (handle, true), (handle, false) });
                };

                window.Close();
                windowClosed = true;

                Assert.True(clearedBeforeHandleDestruction);
                Assert.SequenceEqual(
                    new[] { (handle, true), (handle, false) },
                    taskbarController.Requests.ToArray());
            }
            finally
            {
                if (!windowClosed)
                {
                    ExitMainWindowFullscreenIfActive(window);
                    var closeConfirmedField = typeof(MainWindow).GetField(
                        "closeConfirmed",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    closeConfirmedField?.SetValue(window, true);
                    window.Close();
                }

                MainWindow.TaskbarFullscreenController = previousTaskbarController;
            }
        });
    }),
    ("video fullscreen preserves multi-view layout while hiding chat chrome", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        settings.Chat.Layout = ChatLayout.Docked;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var second = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        viewModel.Tabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.SelectedTab = first;

        Assert.True(viewModel.IsCurrentVideoViewMultiStream());
        Assert.SequenceEqual(new[] { first, second }, viewModel.VideoTabs.ToArray());
        Assert.True(first.IsVideoVisible);
        Assert.True(second.IsVideoVisible);
        Assert.True(viewModel.IsDockedChatVisible);

        viewModel.IsVideoFullscreenActive = true;

        Assert.True(viewModel.IsCurrentVideoViewMultiStream());
        Assert.SequenceEqual(new[] { first, second }, viewModel.VideoTabs.ToArray());
        Assert.True(first.IsVideoVisible);
        Assert.True(second.IsVideoVisible);
        Assert.Equal(false, viewModel.IsDockedChatVisible);

        viewModel.IsVideoFullscreenActive = false;

        Assert.True(viewModel.IsDockedChatVisible);
        return Task.CompletedTask;
    }),
    ("stream-only fullscreen still collapses multi-view to selected stream", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var second = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var third = TestViewModels.CreateTab(
            StreamInputParser.Parse("xqc", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        viewModel.Tabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.Tabs.Add(third);
        viewModel.SelectedTab = second;

        Assert.True(viewModel.IsCurrentVideoViewMultiStream());

        viewModel.IsVideoFullscreenActive = true;
        viewModel.IsStreamOnlyFullscreenActive = true;

        Assert.Equal(false, first.IsVideoVisible);
        Assert.True(second.IsVideoVisible);
        Assert.Equal(false, third.IsVideoVisible);
        Assert.SequenceEqual(new[] { first, second, third }, viewModel.VideoTabs.ToArray());
        Assert.Equal(false, viewModel.IsCurrentVideoViewMultiStream());
        return Task.CompletedTask;
    }),
    ("main multi-stream polling drag reorder advances without native mouse move messages", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            var streamlink = new FakeStreamlinkService();
            var firstEngine = new FakePlaybackEngine();
            var secondEngine = new FakePlaybackEngine();
            var engines = new Queue<FakePlaybackEngine>([firstEngine, secondEngine]);
            var playbackFactory = new FakePlaybackEngineFactory(() => engines.Dequeue());
            var logger = new MemoryLogger();
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
                MultiStreamEnabled = true
            };
            settings.Chat.ConnectAutomatically = false;
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(
                StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var second = TestViewModels.CreateTab(
                StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            Assert.NotNull(nativePointType);
            var beginDrag = typeof(MainWindow).GetMethod(
                "BeginVideoReorderDragCandidate",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var pollDrag = typeof(MainWindow).GetMethod(
                "PollVideoReorderDrag",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [nativePointType!, typeof(bool)],
                null);
            var stopPolling = typeof(MainWindow).GetMethod(
                "StopVideoReorderPolling",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var getDropTarget = typeof(MainWindow).GetMethod(
                "GetVideoReorderDropTarget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var getVideoTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "GetVideoTabAtScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var dragTabField = typeof(MainWindow).GetField(
                "videoReorderDragTab",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(beginDrag);
            Assert.NotNull(pollDrag);
            Assert.NotNull(stopPolling);
            Assert.NotNull(getDropTarget);
            Assert.NotNull(getVideoTabAtScreenPoint);
            Assert.NotNull(dragTabField);
            Assert.NotNull(leftButtonField);
            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                stopPolling!.Invoke(window, []);

                var surfaces = FindVisualDescendants<StreamlinkVlcStudio.App.Wpf.Controls.VideoSurface>(window).ToArray();
                var firstSurface = surfaces.First(surface => ReferenceEquals(surface.Tag, first));
                var secondSurface = surfaces.First(surface => ReferenceEquals(surface.Tag, second));
                var firstSurfaceUnloaded = 0;
                var secondSurfaceUnloaded = 0;
                var videoTabChanges = new List<NotifyCollectionChangedAction>();
                firstSurface.Unloaded += (_, _) => firstSurfaceUnloaded++;
                secondSurface.Unloaded += (_, _) => secondSurfaceUnloaded++;
                viewModel.VideoTabs.CollectionChanged += (_, e) => videoTabChanges.Add(e.Action);
                await first.StartAsync(settings);
                await second.StartAsync(settings);
                Assert.SequenceEqual(new[] { firstSurface.Handle }, firstEngine.VideoHandleHistory);
                Assert.SequenceEqual(new[] { secondSurface.Handle }, secondEngine.VideoHandleHistory);
                var leftButtonPressed = true;
                leftButtonField!.SetValue(window, (Func<bool>)(() => leftButtonPressed));

                var firstPoint = firstSurface.PointToScreen(new System.Windows.Point(
                    firstSurface.ActualWidth / 2,
                    firstSurface.ActualHeight / 2));
                var secondPoint = secondSurface.PointToScreen(new System.Windows.Point(
                    secondSurface.ActualWidth / 2,
                    secondSurface.ActualHeight / 2));
                var startNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(firstPoint.X), (int)Math.Round(firstPoint.Y)]);
                var targetNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(secondPoint.X), (int)Math.Round(secondPoint.Y)]);
                Assert.NotNull(startNativePoint);
                Assert.NotNull(targetNativePoint);
                Assert.True(
                    Math.Abs(secondPoint.X - firstPoint.X) > 50 ||
                    Math.Abs(secondPoint.Y - firstPoint.Y) > 50,
                    $"Expected distinct video surfaces, got first ({firstPoint.X:0.##},{firstPoint.Y:0.##}) second ({secondPoint.X:0.##},{secondPoint.Y:0.##}).");

                NativeWindowTest.SetCursorPosition((int)Math.Round(firstPoint.X), (int)Math.Round(firstPoint.Y));
                Assert.True((bool)beginDrag!.Invoke(window, [startNativePoint])!);
                stopPolling.Invoke(window, []);
                Assert.True(
                    ReferenceEquals(first, dragTabField!.GetValue(window)),
                    $"Expected drag candidate '{first.Title}', got '{(dragTabField.GetValue(window) as StreamTabViewModel)?.Title}'.");
                var hitTab = getVideoTabAtScreenPoint!.Invoke(window, [targetNativePoint]) as StreamTabViewModel;
                var dropTarget = getDropTarget!.Invoke(window, [targetNativePoint, first]) as StreamTabViewModel;
                var firstTopLeft = firstSurface.PointToScreen(new System.Windows.Point(0, 0));
                var firstBottomRight = firstSurface.PointToScreen(new System.Windows.Point(firstSurface.ActualWidth, firstSurface.ActualHeight));
                var secondTopLeft = secondSurface.PointToScreen(new System.Windows.Point(0, 0));
                var secondBottomRight = secondSurface.PointToScreen(new System.Windows.Point(secondSurface.ActualWidth, secondSurface.ActualHeight));
                Assert.True(
                    ReferenceEquals(second, dropTarget),
                    $"Expected the second video surface to be the reorder drop target, hit '{hitTab?.Title ?? "null"}' and got '{dropTarget?.Title ?? "null"}'. First [{firstTopLeft.X:0.##},{firstTopLeft.Y:0.##}]-[{firstBottomRight.X:0.##},{firstBottomRight.Y:0.##}], second [{secondTopLeft.X:0.##},{secondTopLeft.Y:0.##}]-[{secondBottomRight.X:0.##},{secondBottomRight.Y:0.##}], target [{secondPoint.X:0.##},{secondPoint.Y:0.##}].");
                NativeWindowTest.SetCursorPosition((int)Math.Round(secondPoint.X), (int)Math.Round(secondPoint.Y));
                pollDrag!.Invoke(window, [targetNativePoint, true]);
                Assert.True(
                    ReferenceEquals(second, viewModel.Tabs[0]) && ReferenceEquals(first, viewModel.Tabs[1]),
                    $"Expected tab order '{second.Title}, {first.Title}', got '{viewModel.Tabs[0].Title}, {viewModel.Tabs[1].Title}'.");
                Assert.SequenceEqual(new[] { first, second }, viewModel.VideoTabs.ToArray());
                Assert.Equal(0, videoTabChanges.Count);
                Assert.Equal(0, firstSurfaceUnloaded);
                Assert.Equal(0, secondSurfaceUnloaded);
                Assert.Equal(1, first.VideoGridColumn);
                Assert.Equal(0, second.VideoGridColumn);
                Assert.SequenceEqual(new[] { firstSurface.Handle }, firstEngine.VideoHandleHistory);
                Assert.SequenceEqual(new[] { secondSurface.Handle }, secondEngine.VideoHandleHistory);
                Assert.Equal(2, streamlink.StartCount);

                leftButtonPressed = false;
                pollDrag.Invoke(window, [targetNativePoint, false]);
                Assert.True(
                    ReferenceEquals(second, viewModel.Tabs[0]) && ReferenceEquals(first, viewModel.Tabs[1]),
                    $"Expected tab order to stay '{second.Title}, {first.Title}', got '{viewModel.Tabs[0].Title}, {viewModel.Tabs[1].Title}'.");
                Assert.Equal(0, videoTabChanges.Count);
                Assert.SequenceEqual(new[] { firstSurface.Handle }, firstEngine.VideoHandleHistory);
                Assert.SequenceEqual(new[] { secondSurface.Handle }, secondEngine.VideoHandleHistory);
            }
            finally
            {
                if (restoreCursor)
                {
                    NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                }

                await first.DisposeAsync();
                await second.DisposeAsync();
                window.Close();
            }
        });
    }),
    ("multi-stream drag reorder moves the stream to the dropped grid position", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = true
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = Enumerable.Range(1, 5)
            .Select(index => TestViewModels.CreateTab(
                StreamInputParser.Parse($"streamer{index}", PlatformKind.Twitch),
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action()))
            .ToArray();

        foreach (var tab in tabs)
        {
            viewModel.Tabs.Add(tab);
        }

        viewModel.SelectedTab = tabs[1];
        var videoTabChanges = new List<NotifyCollectionChangedAction>();
        viewModel.VideoTabs.CollectionChanged += (_, e) => videoTabChanges.Add(e.Action);

        Assert.True(viewModel.TryReorderVisibleVideoTab(tabs[0], tabs[3]));
        Assert.SequenceEqual(new[] { tabs[1], tabs[2], tabs[3], tabs[0], tabs[4] }, viewModel.Tabs.ToArray());
        Assert.SequenceEqual(tabs, viewModel.VideoTabs.ToArray());
        Assert.Equal(0, videoTabChanges.Count);
        Assert.Equal(tabs[0], viewModel.SelectedTab);
        Assert.Equal(1, tabs[0].VideoGridRow);
        Assert.Equal(0, tabs[0].VideoGridColumn);
        Assert.Equal(0, tabs[1].VideoGridRow);
        Assert.Equal(0, tabs[1].VideoGridColumn);
        Assert.Equal(0, tabs[2].VideoGridRow);
        Assert.Equal(1, tabs[2].VideoGridColumn);
        Assert.Equal(0, tabs[3].VideoGridRow);
        Assert.Equal(2, tabs[3].VideoGridColumn);

        Assert.Equal(false, viewModel.TryReorderVisibleVideoTab(tabs[0], tabs[0]));

        viewModel.IsMultiStreamEnabled = false;

        Assert.Equal(false, viewModel.TryReorderVisibleVideoTab(tabs[0], tabs[4]));
        Assert.SequenceEqual(new[] { tabs[1], tabs[2], tabs[3], tabs[0], tabs[4] }, viewModel.Tabs.ToArray());
        Assert.SequenceEqual(tabs, viewModel.VideoTabs.ToArray());
        return Task.CompletedTask;
    }),
    ("tab strip reorder moves the far-right tab to the far-left position", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = new[]
        {
            TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action()),
            TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action()),
            TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action()),
            TestViewModels.CreateTab(StreamInputParser.Parse("shroud", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action())
        };

        foreach (var tab in tabs)
        {
            viewModel.Tabs.Add(tab);
        }

        viewModel.SelectedTab = tabs[0];

        Assert.True(viewModel.TryReorderTabStripTabs([tabs[3]], tabs[0], insertAfterTarget: false, selectedDraggedTab: tabs[3]));
        Assert.SequenceEqual(new[] { tabs[3], tabs[0], tabs[1], tabs[2] }, viewModel.Tabs.ToArray());
        Assert.SequenceEqual(new[] { "shroud", "albralelie", "summit1g", "xqc" }, viewModel.TabStripItems.Select(item => item.ActiveTab.Target.Channel).ToArray());
        Assert.Equal(tabs[3], viewModel.SelectedTab);
        return Task.CompletedTask;
    }),
    ("tab strip reorder inserts before and after the target item", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tabs = new[]
        {
            TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action()),
            TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action()),
            TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action()),
            TestViewModels.CreateTab(StreamInputParser.Parse("shroud", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action())
        };

        foreach (var tab in tabs)
        {
            viewModel.Tabs.Add(tab);
        }

        Assert.True(viewModel.TryReorderTabStripTabs([tabs[0]], tabs[2], insertAfterTarget: true, selectedDraggedTab: tabs[0]));
        Assert.SequenceEqual(new[] { tabs[1], tabs[2], tabs[0], tabs[3] }, viewModel.Tabs.ToArray());

        Assert.True(viewModel.TryReorderTabStripTabs([tabs[3]], tabs[1], insertAfterTarget: false, selectedDraggedTab: tabs[3]));
        Assert.SequenceEqual(new[] { tabs[3], tabs[1], tabs[2], tabs[0] }, viewModel.Tabs.ToArray());
        Assert.Equal(tabs[3], viewModel.SelectedTab);
        return Task.CompletedTask;
    }),
    ("tab strip reorder moves a grouped tab strip item as a block", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var fourth = TestViewModels.CreateTab(StreamInputParser.Parse("shroud", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

        viewModel.Tabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.Tabs.Add(third);
        viewModel.Tabs.Add(fourth);
        viewModel.SelectedTab = first;

        Assert.True(viewModel.TryMergeTabsIntoMultiView([second], first, second));
        var draggedGroup = viewModel.GetPictureInPictureDragTabs(second).ToArray();
        Assert.SequenceEqual(new[] { first, second }, draggedGroup);

        Assert.True(viewModel.TryReorderTabStripTabs(draggedGroup, fourth, insertAfterTarget: true, selectedDraggedTab: second));

        Assert.SequenceEqual(new[] { third, fourth, first, second }, viewModel.Tabs.ToArray());
        Assert.SequenceEqual(new[] { first, second }, viewModel.GetPictureInPictureDragTabs(second).ToArray());
        Assert.Equal(3, viewModel.TabStripItems.Count);
        Assert.True(viewModel.TabStripItems.Last().IsGroup);
        Assert.True(viewModel.TabStripItems.Last().Contains(first));
        Assert.True(viewModel.TabStripItems.Last().Contains(second));
        Assert.True(first.IsMergedTabGroupMember);
        Assert.True(first.IsFirstMergedTabGroupMember);
        Assert.True(second.IsMergedTabGroupMember);
        Assert.True(second.IsLastMergedTabGroupMember);
        Assert.Equal(second, viewModel.SelectedTab);
        return Task.CompletedTask;
    }),
    ("tab strip reorder rejects invalid and no-op requests", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var external = TestViewModels.CreateTab(StreamInputParser.Parse("shroud", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

        viewModel.Tabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.Tabs.Add(third);
        viewModel.SelectedTab = first;
        Assert.True(viewModel.TryMergeTabsIntoMultiView([second], first, second));

        Assert.Equal(false, viewModel.TryReorderTabStripTabs([second], third, insertAfterTarget: true, selectedDraggedTab: second));
        Assert.Equal(false, viewModel.TryReorderTabStripTabs([third], second, insertAfterTarget: true, selectedDraggedTab: third));
        Assert.Equal(false, viewModel.TryReorderTabStripTabs([external], first, insertAfterTarget: false, selectedDraggedTab: external));
        Assert.Equal(false, viewModel.TryReorderTabStripTabs([third], external, insertAfterTarget: false, selectedDraggedTab: third));
        Assert.Equal(false, viewModel.TryReorderTabStripTabs([third, third], first, insertAfterTarget: false, selectedDraggedTab: third));
        Assert.SequenceEqual(new[] { first, second, third }, viewModel.Tabs.ToArray());
        return Task.CompletedTask;
    }),
    ("dragging an unmerged tab over another tab creates explicit multiview", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = false
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

        viewModel.Tabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.Tabs.Add(third);
        viewModel.SelectedTab = first;

        Assert.True(first.IsVideoVisible);
        Assert.Equal(false, second.IsVideoVisible);
        Assert.Equal(false, third.IsVideoVisible);
        Assert.Equal(false, first.IsMergedTabGroupMember);
        Assert.Equal(false, settings.MultiStreamEnabled);

        Assert.True(viewModel.TryMergeTabsIntoMultiView([third], first, third));

        Assert.Equal(false, settings.MultiStreamEnabled);
        Assert.SequenceEqual(new[] { first, third, second }, viewModel.Tabs.ToArray());
        Assert.SequenceEqual(new[] { first, third }, viewModel.VideoTabs.ToArray());
        Assert.Equal(third, viewModel.SelectedTab);
        Assert.True(first.IsVideoVisible);
        Assert.True(third.IsVideoVisible);
        Assert.Equal(false, second.IsVideoVisible);
        Assert.True(first.IsMergedTabGroupMember);
        Assert.True(first.IsFirstMergedTabGroupMember);
        Assert.Equal(false, first.IsLastMergedTabGroupMember);
        Assert.True(third.IsMergedTabGroupMember);
        Assert.Equal(false, third.IsFirstMergedTabGroupMember);
        Assert.True(third.IsLastMergedTabGroupMember);
        Assert.Equal(2, viewModel.TabStripItems.Count);
        var mergedTabStripItem = viewModel.TabStripItems.Single(item => item.Contains(first) && item.Contains(third));
        Assert.True(mergedTabStripItem.IsGroup);
        Assert.Equal(third, mergedTabStripItem.ActiveTab);
        Assert.SequenceEqual(new[] { first, third }, viewModel.GetPictureInPictureDragTabs(third).ToArray());

        Assert.Equal(false, viewModel.TryMergeTabsIntoMultiView([third], second, third));
        return Task.CompletedTask;
    }),
    ("dragging a merged tab group over a single tab merges the whole group", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = false
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

        viewModel.Tabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.Tabs.Add(third);
        viewModel.SelectedTab = first;

        Assert.True(viewModel.TryMergeTabsIntoMultiView([second], first, second));
        var draggedGroup = viewModel.GetPictureInPictureDragTabs(second).ToArray();
        Assert.SequenceEqual(new[] { first, second }, draggedGroup);

        Assert.True(viewModel.TryMergeTabsIntoMultiView(draggedGroup, third, second));

        Assert.Equal(false, settings.MultiStreamEnabled);
        Assert.SequenceEqual(new[] { third, first, second }, viewModel.Tabs.ToArray());
        Assert.Equal(second, viewModel.SelectedTab);
        Assert.Equal(1, viewModel.TabStripItems.Count);
        var mergedTabStripItem = viewModel.TabStripItems.Single(item =>
            item.Contains(first) &&
            item.Contains(second) &&
            item.Contains(third));
        Assert.True(mergedTabStripItem.IsGroup);
        Assert.Equal(second, mergedTabStripItem.ActiveTab);
        Assert.SequenceEqual(new[] { third, first, second }, viewModel.GetPictureInPictureDragTabs(second).ToArray());
        Assert.True(third.IsMergedTabGroupMember);
        Assert.True(third.IsFirstMergedTabGroupMember);
        Assert.Equal(false, third.IsLastMergedTabGroupMember);
        Assert.True(first.IsMergedTabGroupMember);
        Assert.Equal(false, first.IsFirstMergedTabGroupMember);
        Assert.Equal(false, first.IsLastMergedTabGroupMember);
        Assert.True(second.IsMergedTabGroupMember);
        Assert.Equal(false, second.IsFirstMergedTabGroupMember);
        Assert.True(second.IsLastMergedTabGroupMember);
        Assert.True(third.IsVideoVisible);
        Assert.True(first.IsVideoVisible);
        Assert.True(second.IsVideoVisible);
        return Task.CompletedTask;
    }),
    ("dragging a merged tab group over another merged tab group merges both groups", () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            MultiStreamEnabled = false
        };
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
        var fourth = TestViewModels.CreateTab(StreamInputParser.Parse("shroud", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

        viewModel.Tabs.Add(first);
        viewModel.Tabs.Add(second);
        viewModel.Tabs.Add(third);
        viewModel.Tabs.Add(fourth);
        viewModel.SelectedTab = first;

        Assert.True(viewModel.TryMergeTabsIntoMultiView([second], first, second));
        Assert.True(viewModel.TryMergeTabsIntoMultiView([fourth], third, fourth));
        Assert.SequenceEqual(new[] { "albralelie", "summit1g" }, TabChannels(viewModel.GetPictureInPictureDragTabs(second)));
        Assert.SequenceEqual(new[] { "xqc", "shroud" }, TabChannels(viewModel.GetPictureInPictureDragTabs(third)));

        var draggedGroup = viewModel.GetPictureInPictureDragTabs(second).ToArray();
        Assert.True(viewModel.TryMergeTabsIntoMultiView(draggedGroup, third, second));

        Assert.Equal(false, settings.MultiStreamEnabled);
        Assert.SequenceEqual(new[] { "xqc", "shroud", "albralelie", "summit1g" }, TabChannels(viewModel.Tabs));
        Assert.Equal(second, viewModel.SelectedTab);
        Assert.Equal(1, viewModel.TabStripItems.Count);
        var mergedTabStripItem = viewModel.TabStripItems.Single(item =>
            item.Contains(first) &&
            item.Contains(second) &&
            item.Contains(third) &&
            item.Contains(fourth));
        Assert.True(mergedTabStripItem.IsGroup);
        Assert.Equal(second, mergedTabStripItem.ActiveTab);
        Assert.SequenceEqual(new[] { "xqc", "shroud", "albralelie", "summit1g" }, TabChannels(viewModel.GetPictureInPictureDragTabs(second)));
        Assert.True(third.IsMergedTabGroupMember);
        Assert.True(third.IsFirstMergedTabGroupMember);
        Assert.Equal(false, third.IsLastMergedTabGroupMember);
        Assert.True(fourth.IsMergedTabGroupMember);
        Assert.Equal(false, fourth.IsFirstMergedTabGroupMember);
        Assert.Equal(false, fourth.IsLastMergedTabGroupMember);
        Assert.True(first.IsMergedTabGroupMember);
        Assert.Equal(false, first.IsFirstMergedTabGroupMember);
        Assert.Equal(false, first.IsLastMergedTabGroupMember);
        Assert.True(second.IsMergedTabGroupMember);
        Assert.Equal(false, second.IsFirstMergedTabGroupMember);
        Assert.True(second.IsLastMergedTabGroupMember);
        Assert.True(third.IsVideoVisible);
        Assert.True(fourth.IsVideoVisible);
        Assert.True(first.IsVideoVisible);
        Assert.True(second.IsVideoVisible);
        Assert.Equal(0, third.VideoGridRow);
        Assert.Equal(0, third.VideoGridColumn);
        Assert.Equal(0, fourth.VideoGridRow);
        Assert.Equal(1, fourth.VideoGridColumn);
        Assert.Equal(1, first.VideoGridRow);
        Assert.Equal(0, first.VideoGridColumn);
        Assert.Equal(1, second.VideoGridRow);
        Assert.Equal(1, second.VideoGridColumn);
        return Task.CompletedTask;
    }),
    ("normal tab strip drag reorders over target tab without merging", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifier(window, pressed: false);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var continueDrag = typeof(MainWindow).GetMethod(
                "TryContinueTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [nativePointType!, typeof(bool)],
                null);
            var completeDrag = typeof(MainWindow).GetMethod(
                "TryCompleteTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasExceededDragDistance = typeof(MainWindow).GetMethod(
                "HasExceededTabDetachDragDistance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragTabField = typeof(MainWindow).GetField(
                "tabDetachDragTab",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragSourceField = typeof(MainWindow).GetField(
                "tabDetachDragSource",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartPointField = typeof(MainWindow).GetField(
                "tabDetachDragStartPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartScreenPointField = typeof(MainWindow).GetField(
                "tabDetachDragStartScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativePointType);
            Assert.NotNull(continueDrag);
            Assert.NotNull(completeDrag);
            Assert.NotNull(hasExceededDragDistance);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(leftButtonField);
            Assert.NotNull(tabDetachDragTabField);
            Assert.NotNull(tabDetachDragSourceField);
            Assert.NotNull(tabDetachDragStartPointField);
            Assert.NotNull(tabDetachDragStartScreenPointField);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                leftButtonField!.SetValue(window, (Func<bool>)(() => true));

                var firstItem = FindTabStripListBoxItem(window, first);
                var secondItem = FindTabStripListBoxItem(window, second);
                var startPoint = firstItem.TransformToAncestor(window).Transform(new System.Windows.Point(
                    firstItem.ActualWidth / 2,
                    firstItem.ActualHeight / 2));
                var startScreenPoint = firstItem.PointToScreen(new System.Windows.Point(
                    firstItem.ActualWidth / 2,
                    firstItem.ActualHeight / 2));
                var nativeStartPoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(startScreenPoint.X), (int)Math.Round(startScreenPoint.Y)]);
                Assert.NotNull(nativeStartPoint);
                tabDetachDragSourceField!.SetValue(window, firstItem);
                tabDetachDragTabField!.SetValue(window, first);
                tabDetachDragStartPointField!.SetValue(window, startPoint);
                tabDetachDragStartScreenPointField!.SetValue(window, nativeStartPoint);

                var secondPoint = secondItem.PointToScreen(new System.Windows.Point(
                    secondItem.ActualWidth * 0.75,
                    secondItem.ActualHeight / 2));
                var targetNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(secondPoint.X), (int)Math.Round(secondPoint.Y)]);
                Assert.NotNull(targetNativePoint);

                Assert.True(
                    (bool)hasExceededDragDistance!.Invoke(window, [targetNativePoint])!,
                    "Expected tab-to-tab drag to exceed the system drag threshold.");
                var targetArgs = new object?[] { targetNativePoint, null };
                Assert.True(
                    (bool)tryGetTabAtScreenPoint!.Invoke(window, targetArgs)!,
                    "Expected the second tab strip item under the simulated drag point.");
                Assert.Equal(second, targetArgs[1]);

                Assert.True(
                    (bool)continueDrag!.Invoke(window, [targetNativePoint, true])!,
                    "Expected normal tab strip drag to reorder live over the target tab.");
                Assert.SequenceEqual(new[] { second, first }, viewModel.Tabs.ToArray());
                Assert.Equal(false, first.IsMergedTabGroupMember);
                Assert.Equal(false, second.IsMergedTabGroupMember);
                Assert.Equal(2, viewModel.TabStripItems.Count);

                Assert.True(
                    (bool)completeDrag!.Invoke(window, [targetNativePoint])!,
                    "Expected mouse-up after normal tab strip reorder to stay handled.");
                Assert.SequenceEqual(new[] { second, first }, viewModel.Tabs.ToArray());
                Assert.Equal(false, first.IsMergedTabGroupMember);
                Assert.Equal(false, second.IsMergedTabGroupMember);
                Assert.Equal(first, viewModel.SelectedTab);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ("dragging a top tab outside the tab strip detaches to picture-in-picture", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifier(window, pressed: false);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var continueDrag = typeof(MainWindow).GetMethod(
                "TryContinueTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [nativePointType!, typeof(bool)],
                null);
            var hasExceededDragDistance = typeof(MainWindow).GetMethod(
                "HasExceededTabDetachDragDistance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragTabField = typeof(MainWindow).GetField(
                "tabDetachDragTab",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragSourceField = typeof(MainWindow).GetField(
                "tabDetachDragSource",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartPointField = typeof(MainWindow).GetField(
                "tabDetachDragStartPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartScreenPointField = typeof(MainWindow).GetField(
                "tabDetachDragStartScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var detachedWindowsField = typeof(MainWindow).GetField(
                "detachedWindows",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativePointType);
            Assert.NotNull(continueDrag);
            Assert.NotNull(hasExceededDragDistance);
            Assert.NotNull(leftButtonField);
            Assert.NotNull(tabDetachDragTabField);
            Assert.NotNull(tabDetachDragSourceField);
            Assert.NotNull(tabDetachDragStartPointField);
            Assert.NotNull(tabDetachDragStartScreenPointField);
            Assert.NotNull(detachedWindowsField);

            DetachedVideoWindow? detachedWindow = null;
            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                leftButtonField!.SetValue(window, (Func<bool>)(() => true));

                var firstItem = FindTabStripListBoxItem(window, first);
                var startPoint = firstItem.TransformToAncestor(window).Transform(new System.Windows.Point(
                    firstItem.ActualWidth / 2,
                    firstItem.ActualHeight / 2));
                var startScreenPoint = firstItem.PointToScreen(new System.Windows.Point(
                    firstItem.ActualWidth / 2,
                    firstItem.ActualHeight / 2));
                var nativeStartPoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(startScreenPoint.X), (int)Math.Round(startScreenPoint.Y)]);
                Assert.NotNull(nativeStartPoint);
                tabDetachDragSourceField!.SetValue(window, firstItem);
                tabDetachDragTabField!.SetValue(window, first);
                tabDetachDragStartPointField!.SetValue(window, startPoint);
                tabDetachDragStartScreenPointField!.SetValue(window, nativeStartPoint);

                var outsidePoint = window.PointToScreen(new System.Windows.Point(
                    window.ActualWidth + 160,
                    window.ActualHeight + 160));
                var outsideNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(outsidePoint.X), (int)Math.Round(outsidePoint.Y)]);
                Assert.NotNull(outsideNativePoint);
                Assert.True(
                    (bool)hasExceededDragDistance!.Invoke(window, [outsideNativePoint])!,
                    "Expected drag outside the tab strip to exceed the system drag threshold.");

                Assert.True(
                    (bool)continueDrag!.Invoke(window, [outsideNativePoint, false])!,
                    "Expected dragging outside the tab strip to detach the tab.");

                var detachedWindows = (IDictionary<StreamTabViewModel, DetachedVideoWindow>)detachedWindowsField!.GetValue(window)!;
                Assert.True(detachedWindows.TryGetValue(first, out detachedWindow));
                Assert.True(first.IsDetached);
                Assert.Equal(false, second.IsDetached);
                Assert.Equal(first, detachedWindow!.ActiveTab);
                Assert.SequenceEqual(new[] { first }, detachedWindow.Tabs);
            }
            finally
            {
                detachedWindow?.CloseForTabDisposal();
                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ("ctrl tab strip drop over another tab creates explicit multiview", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifier(window, pressed: true);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var continueDrag = typeof(MainWindow).GetMethod(
                "TryContinueTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [nativePointType!, typeof(bool)],
                null);
            var completeDrag = typeof(MainWindow).GetMethod(
                "TryCompleteTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasExceededDragDistance = typeof(MainWindow).GetMethod(
                "HasExceededTabDetachDragDistance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragTabField = typeof(MainWindow).GetField(
                "tabDetachDragTab",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragSourceField = typeof(MainWindow).GetField(
                "tabDetachDragSource",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartPointField = typeof(MainWindow).GetField(
                "tabDetachDragStartPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartScreenPointField = typeof(MainWindow).GetField(
                "tabDetachDragStartScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativePointType);
            Assert.NotNull(continueDrag);
            Assert.NotNull(completeDrag);
            Assert.NotNull(hasExceededDragDistance);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(leftButtonField);
            Assert.NotNull(tabDetachDragTabField);
            Assert.NotNull(tabDetachDragSourceField);
            Assert.NotNull(tabDetachDragStartPointField);
            Assert.NotNull(tabDetachDragStartScreenPointField);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                leftButtonField!.SetValue(window, (Func<bool>)(() => true));

                var firstItem = FindTabStripListBoxItem(window, first);
                var secondItem = FindTabStripListBoxItem(window, second);
                var startPoint = firstItem.TransformToAncestor(window).Transform(new System.Windows.Point(
                    firstItem.ActualWidth / 2,
                    firstItem.ActualHeight / 2));
                var startScreenPoint = firstItem.PointToScreen(new System.Windows.Point(
                    firstItem.ActualWidth / 2,
                    firstItem.ActualHeight / 2));
                var nativeStartPoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(startScreenPoint.X), (int)Math.Round(startScreenPoint.Y)]);
                Assert.NotNull(nativeStartPoint);
                tabDetachDragSourceField!.SetValue(window, firstItem);
                tabDetachDragTabField!.SetValue(window, first);
                tabDetachDragStartPointField!.SetValue(window, startPoint);
                tabDetachDragStartScreenPointField!.SetValue(window, nativeStartPoint);

                var secondPoint = secondItem.PointToScreen(new System.Windows.Point(
                    secondItem.ActualWidth / 2,
                    secondItem.ActualHeight / 2));
                var targetNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(secondPoint.X), (int)Math.Round(secondPoint.Y)]);
                Assert.NotNull(targetNativePoint);

                Assert.True(
                    (bool)hasExceededDragDistance!.Invoke(window, [targetNativePoint])!,
                    "Expected tab-to-tab drag to exceed the system drag threshold.");
                var targetArgs = new object?[] { targetNativePoint, null };
                Assert.True(
                    (bool)tryGetTabAtScreenPoint!.Invoke(window, targetArgs)!,
                    "Expected the second tab strip item under the simulated drag point.");
                Assert.Equal(second, targetArgs[1]);

                Assert.True(
                    (bool)continueDrag!.Invoke(window, [targetNativePoint, true])!,
                    "Expected tab drag to stay active over the target tab until mouse-up.");
                Assert.SequenceEqual(new[] { first, second }, viewModel.Tabs.ToArray());
                Assert.Equal(false, first.IsMergedTabGroupMember);
                Assert.Equal(false, second.IsMergedTabGroupMember);

                Assert.True(
                    (bool)completeDrag!.Invoke(window, [targetNativePoint])!,
                    "Expected mouse-up over the second tab to complete the multiview merge.");

                Assert.SequenceEqual(new[] { second, first }, viewModel.Tabs.ToArray());
                Assert.Equal(1, viewModel.TabStripItems.Count(item => item.Contains(first) && item.Contains(second)));
                Assert.SequenceEqual(new[] { first, second }, viewModel.VideoTabs.ToArray());
                Assert.Equal(first, viewModel.SelectedTab);
                Assert.Equal(0, second.VideoGridColumn);
                Assert.Equal(1, first.VideoGridColumn);
                Assert.True(second.IsMergedTabGroupMember);
                Assert.True(second.IsFirstMergedTabGroupMember);
                Assert.True(first.IsMergedTabGroupMember);
                Assert.True(first.IsLastMergedTabGroupMember);
                Assert.Equal(false, settings.MultiStreamEnabled);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ("ctrl tab strip drag still merges when Ctrl is released before mouse-up", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var controlPressed = true;
            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifierProvider(window, () => controlPressed);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var beginDrag = typeof(MainWindow).GetMethod(
                "BeginTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var continueDrag = typeof(MainWindow).GetMethod(
                "TryContinueTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [nativePointType!, typeof(bool)],
                null);
            var completeDrag = typeof(MainWindow).GetMethod(
                "TryCompleteTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasExceededDragDistance = typeof(MainWindow).GetMethod(
                "HasExceededTabDetachDragDistance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativePointType);
            Assert.NotNull(beginDrag);
            Assert.NotNull(continueDrag);
            Assert.NotNull(completeDrag);
            Assert.NotNull(hasExceededDragDistance);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(leftButtonField);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                leftButtonField!.SetValue(window, (Func<bool>)(() => true));

                var firstItem = FindTabStripListBoxItem(window, first);
                var secondItem = FindTabStripListBoxItem(window, second);
                var startPoint = firstItem.TransformToAncestor(window).Transform(new System.Windows.Point(
                    firstItem.ActualWidth / 2,
                    firstItem.ActualHeight / 2));
                var secondPoint = secondItem.PointToScreen(new System.Windows.Point(
                    secondItem.ActualWidth / 2,
                    secondItem.ActualHeight / 2));
                var targetNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(secondPoint.X), (int)Math.Round(secondPoint.Y)]);
                Assert.NotNull(targetNativePoint);

                beginDrag!.Invoke(window, [firstItem, first, new StreamTabViewModel[] { first }, startPoint, true]);
                controlPressed = false;

                Assert.True(
                    (bool)hasExceededDragDistance!.Invoke(window, [targetNativePoint])!,
                    "Expected tab-to-tab drag to exceed the system drag threshold.");
                var targetArgs = new object?[] { targetNativePoint, null };
                Assert.True(
                    (bool)tryGetTabAtScreenPoint!.Invoke(window, targetArgs)!,
                    "Expected the second tab strip item under the simulated drag point.");
                Assert.Equal(second, targetArgs[1]);

                Assert.True(
                    (bool)continueDrag!.Invoke(window, [targetNativePoint, true])!,
                    "Expected Ctrl-started tab drag to stay in merge mode after Ctrl is released.");
                Assert.SequenceEqual(new[] { first, second }, viewModel.Tabs.ToArray());
                Assert.Equal(false, first.IsMergedTabGroupMember);
                Assert.Equal(false, second.IsMergedTabGroupMember);

                Assert.True(
                    (bool)completeDrag!.Invoke(window, [targetNativePoint])!,
                    "Expected mouse-up over the second tab to complete the multiview merge.");

                Assert.SequenceEqual(new[] { second, first }, viewModel.Tabs.ToArray());
                Assert.Equal(1, viewModel.TabStripItems.Count(item => item.Contains(first) && item.Contains(second)));
                Assert.SequenceEqual(new[] { first, second }, viewModel.VideoTabs.ToArray());
                Assert.Equal(first, viewModel.SelectedTab);
                Assert.Equal(0, second.VideoGridColumn);
                Assert.Equal(1, first.VideoGridColumn);
                Assert.True(second.IsMergedTabGroupMember);
                Assert.True(second.IsFirstMergedTabGroupMember);
                Assert.True(first.IsMergedTabGroupMember);
                Assert.True(first.IsLastMergedTabGroupMember);
                Assert.Equal(false, settings.MultiStreamEnabled);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ("ctrl tab strip drag completes merge with last hovered tab when release misses tab item", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifier(window, pressed: true);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var beginDrag = typeof(MainWindow).GetMethod(
                "BeginTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var continueDrag = typeof(MainWindow).GetMethod(
                "TryContinueTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [nativePointType!, typeof(bool)],
                null);
            var completeDrag = typeof(MainWindow).GetMethod(
                "TryCompleteTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasExceededDragDistance = typeof(MainWindow).GetMethod(
                "HasExceededTabDetachDragDistance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var isScreenPointOutsideTabStrip = typeof(MainWindow).GetMethod(
                "IsScreenPointOutsideTabStrip",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragMergeTargetField = typeof(MainWindow).GetField(
                "tabDetachDragMergeTarget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativePointType);
            Assert.NotNull(beginDrag);
            Assert.NotNull(continueDrag);
            Assert.NotNull(completeDrag);
            Assert.NotNull(hasExceededDragDistance);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(isScreenPointOutsideTabStrip);
            Assert.NotNull(leftButtonField);
            Assert.NotNull(tabDetachDragMergeTargetField);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                leftButtonField!.SetValue(window, (Func<bool>)(() => true));

                var firstItem = FindTabStripListBoxItem(window, first);
                var secondItem = FindTabStripListBoxItem(window, second);
                var startPoint = firstItem.TransformToAncestor(window).Transform(new System.Windows.Point(
                    firstItem.ActualWidth / 2,
                    firstItem.ActualHeight / 2));
                var secondPoint = secondItem.PointToScreen(new System.Windows.Point(
                    secondItem.ActualWidth / 2,
                    secondItem.ActualHeight / 2));
                var targetNativePoint = CreateNativeScreenPoint(nativePointType!, secondPoint);
                var blankNativePoint = CreateBlankTabStripNativePoint(window, secondItem, nativePointType!);

                var blankTargetArgs = new object?[] { blankNativePoint, null };
                Assert.Equal(
                    false,
                    (bool)tryGetTabAtScreenPoint!.Invoke(window, blankTargetArgs)!);
                Assert.Equal(
                    false,
                    (bool)isScreenPointOutsideTabStrip!.Invoke(window, [blankNativePoint])!);

                beginDrag!.Invoke(window, [firstItem, first, new StreamTabViewModel[] { first }, startPoint, true]);

                Assert.True(
                    (bool)hasExceededDragDistance!.Invoke(window, [targetNativePoint])!,
                    "Expected tab-to-tab drag to exceed the system drag threshold.");
                Assert.True(
                    (bool)continueDrag!.Invoke(window, [targetNativePoint, true])!,
                    "Expected Ctrl tab drag to remember the hovered merge target.");
                Assert.True(
                    ReferenceEquals(second, tabDetachDragMergeTargetField!.GetValue(window)),
                    "Expected the second tab to be remembered as the current Ctrl merge target.");

                Assert.True(
                    (bool)completeDrag!.Invoke(window, [blankNativePoint])!,
                    "Expected mouse-up over blank tab-strip space to merge with the last hovered tab.");

                Assert.SequenceEqual(new[] { second, first }, viewModel.Tabs.ToArray());
                Assert.Equal(1, viewModel.TabStripItems.Count(item => item.Contains(first) && item.Contains(second)));
                Assert.SequenceEqual(new[] { first, second }, viewModel.VideoTabs.ToArray());
                Assert.Equal(first, viewModel.SelectedTab);
                Assert.True(second.IsMergedTabGroupMember);
                Assert.True(second.IsFirstMergedTabGroupMember);
                Assert.True(first.IsMergedTabGroupMember);
                Assert.True(first.IsLastMergedTabGroupMember);
                Assert.Equal(false, settings.MultiStreamEnabled);
            }
            finally
            {
                Mouse.Capture(null);
                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ("ctrl tab strip drag clears remembered merge target over blank tab strip space", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifier(window, pressed: true);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var beginDrag = typeof(MainWindow).GetMethod(
                "BeginTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var continueDrag = typeof(MainWindow).GetMethod(
                "TryContinueTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [nativePointType!, typeof(bool)],
                null);
            var completeDrag = typeof(MainWindow).GetMethod(
                "TryCompleteTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasExceededDragDistance = typeof(MainWindow).GetMethod(
                "HasExceededTabDetachDragDistance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var isScreenPointOutsideTabStrip = typeof(MainWindow).GetMethod(
                "IsScreenPointOutsideTabStrip",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragMergeTargetField = typeof(MainWindow).GetField(
                "tabDetachDragMergeTarget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativePointType);
            Assert.NotNull(beginDrag);
            Assert.NotNull(continueDrag);
            Assert.NotNull(completeDrag);
            Assert.NotNull(hasExceededDragDistance);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(isScreenPointOutsideTabStrip);
            Assert.NotNull(leftButtonField);
            Assert.NotNull(tabDetachDragMergeTargetField);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                leftButtonField!.SetValue(window, (Func<bool>)(() => true));

                var firstItem = FindTabStripListBoxItem(window, first);
                var secondItem = FindTabStripListBoxItem(window, second);
                var startPoint = firstItem.TransformToAncestor(window).Transform(new System.Windows.Point(
                    firstItem.ActualWidth / 2,
                    firstItem.ActualHeight / 2));
                var secondPoint = secondItem.PointToScreen(new System.Windows.Point(
                    secondItem.ActualWidth / 2,
                    secondItem.ActualHeight / 2));
                var targetNativePoint = CreateNativeScreenPoint(nativePointType!, secondPoint);
                var blankNativePoint = CreateBlankTabStripNativePoint(window, secondItem, nativePointType!);

                var blankTargetArgs = new object?[] { blankNativePoint, null };
                Assert.Equal(
                    false,
                    (bool)tryGetTabAtScreenPoint!.Invoke(window, blankTargetArgs)!);
                Assert.Equal(
                    false,
                    (bool)isScreenPointOutsideTabStrip!.Invoke(window, [blankNativePoint])!);

                beginDrag!.Invoke(window, [firstItem, first, new StreamTabViewModel[] { first }, startPoint, true]);

                Assert.True(
                    (bool)hasExceededDragDistance!.Invoke(window, [targetNativePoint])!,
                    "Expected tab-to-tab drag to exceed the system drag threshold.");
                Assert.True(
                    (bool)continueDrag!.Invoke(window, [targetNativePoint, true])!,
                    "Expected Ctrl tab drag to remember the hovered merge target.");
                Assert.True(
                    ReferenceEquals(second, tabDetachDragMergeTargetField!.GetValue(window)),
                    "Expected the second tab to be remembered as the current Ctrl merge target.");

                Assert.True(
                    (bool)continueDrag.Invoke(window, [blankNativePoint, true])!,
                    "Expected Ctrl tab drag to remain active over blank tab-strip space.");
                Assert.Equal(null, tabDetachDragMergeTargetField.GetValue(window));

                Assert.Equal(
                    false,
                    (bool)completeDrag!.Invoke(window, [blankNativePoint])!);
                Assert.SequenceEqual(new[] { first, second }, viewModel.Tabs.ToArray());
                Assert.Equal(false, first.IsMergedTabGroupMember);
                Assert.Equal(false, second.IsMergedTabGroupMember);
                Assert.Equal(2, viewModel.TabStripItems.Count);
                Assert.Equal(first, viewModel.SelectedTab);
            }
            finally
            {
                Mouse.Capture(null);
                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ("ctrl tab strip routed mouse drag merges when Ctrl is released before mouse-up", () =>
    {
        return TestSta.RunAsync(() =>
        {
            const int wmLeftButtonUp = 0x0202;
            const int wmMouseMove = 0x0200;
            const int mkLeftButton = 0x0001;
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var controlPressed = true;
            var leftButtonPressed = true;
            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifierProvider(window, () => controlPressed);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragTabField = typeof(MainWindow).GetField(
                "tabDetachDragTab",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragSourceField = typeof(MainWindow).GetField(
                "tabDetachDragSource",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragTabsField = typeof(MainWindow).GetField(
                "tabDetachDragTabs",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartedWithControlModifierField = typeof(MainWindow).GetField(
                "tabDetachDragStartedWithControlModifier",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativePointType);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(leftButtonField);
            Assert.NotNull(tabDetachDragTabField);
            Assert.NotNull(tabDetachDragSourceField);
            Assert.NotNull(tabDetachDragTabsField);
            Assert.NotNull(tabDetachDragStartedWithControlModifierField);

            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                leftButtonField!.SetValue(window, (Func<bool>)(() => leftButtonPressed));

                var firstChrome = FindTabStripChrome(window, first);
                var secondChrome = FindTabStripChrome(window, second);
                var firstPoint = firstChrome.PointToScreen(new System.Windows.Point(
                    firstChrome.ActualWidth / 2,
                    firstChrome.ActualHeight / 2));
                var secondPoint = secondChrome.PointToScreen(new System.Windows.Point(
                    secondChrome.ActualWidth / 2,
                    secondChrome.ActualHeight / 2));
                var windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(windowHandle != IntPtr.Zero);
                AttachMainWindowMessageHook(window);
                var secondMouseLParam = NativeWindowTest.MakeMouseLParamFromScreenPoint(windowHandle, secondPoint);
                var secondNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(secondPoint.X), (int)Math.Round(secondPoint.Y)]);
                Assert.NotNull(secondNativePoint);
                var secondTargetArgs = new object?[] { secondNativePoint, null };
                Assert.True(
                    (bool)tryGetTabAtScreenPoint!.Invoke(window, secondTargetArgs)! &&
                    ReferenceEquals(second, secondTargetArgs[1]),
                    $"Expected the second tab strip item under the simulated release point, hit '{(secondTargetArgs[1] as StreamTabViewModel)?.Title ?? "null"}'.");

                NativeWindowTest.SetCursorPosition((int)Math.Round(firstPoint.X), (int)Math.Round(firstPoint.Y));
                Mouse.Synchronize();
                var mouseDownArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = firstChrome
                };
                firstChrome.RaiseEvent(mouseDownArgs);

                Assert.True(
                    mouseDownArgs.Handled,
                    "Expected custom tab drag to handle Ctrl mouse-down before ListBoxItem selection logic can take over.");
                Assert.True(
                    ReferenceEquals(Mouse.Captured, window),
                    $"Expected tab drag to capture the main window, got '{Mouse.Captured?.GetType().Name ?? "null"}'.");
                Assert.True(
                    ReferenceEquals(first, tabDetachDragTabField!.GetValue(window)),
                    "Expected routed mouse-down to initialize drag state for the first tab.");
                Assert.True(
                    ReferenceEquals(firstChrome, tabDetachDragSourceField!.GetValue(window)),
                    "Expected routed mouse-down to keep the tab chrome as the drag source.");
                Assert.SequenceEqual(
                    new[] { first },
                    (StreamTabViewModel[])tabDetachDragTabsField!.GetValue(window)!);
                Assert.Equal(
                    true,
                    (bool)tabDetachDragStartedWithControlModifierField!.GetValue(window)!);

                window.UpdateLayout();
                secondChrome = FindTabStripChrome(window, second);
                secondPoint = secondChrome.PointToScreen(new System.Windows.Point(
                    secondChrome.ActualWidth / 2,
                    secondChrome.ActualHeight / 2));
                secondMouseLParam = NativeWindowTest.MakeMouseLParamFromScreenPoint(windowHandle, secondPoint);
                NativeWindowTest.SetCursorPosition((int)Math.Round(secondPoint.X), (int)Math.Round(secondPoint.Y));
                Mouse.Synchronize();
                controlPressed = false;
                NativeWindowTest.SendMessage(windowHandle, wmMouseMove, new IntPtr(mkLeftButton), secondMouseLParam);
                Assert.True(
                    ReferenceEquals(first, tabDetachDragTabField!.GetValue(window)),
                    "Expected routed tab drag to remain active after moving over the second tab.");

                Assert.SequenceEqual(new[] { first, second }, viewModel.Tabs.ToArray());
                Assert.Equal(false, first.IsMergedTabGroupMember);
                Assert.Equal(false, second.IsMergedTabGroupMember);

                leftButtonPressed = false;
                NativeWindowTest.SendMessage(windowHandle, wmLeftButtonUp, IntPtr.Zero, secondMouseLParam);

                Assert.SequenceEqual(new[] { second, first }, viewModel.Tabs.ToArray());
                Assert.Equal(1, viewModel.TabStripItems.Count(item => item.Contains(first) && item.Contains(second)));
                Assert.SequenceEqual(new[] { first, second }, viewModel.VideoTabs.ToArray());
                Assert.Equal(first, viewModel.SelectedTab);
                Assert.True(second.IsMergedTabGroupMember);
                Assert.True(first.IsMergedTabGroupMember);
                Assert.Equal(false, settings.MultiStreamEnabled);
            }
            finally
            {
                Mouse.Capture(null);
                if (restoreCursor)
                {
                    NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                }

                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ("ctrl tab strip drag ignores low-level main-window hook activity before routed mouse-up", () =>
    {
        return TestSta.RunAsync(() =>
        {
            const int wmLeftButtonUp = 0x0202;
            const int wmMouseMove = 0x0200;
            const int mkLeftButton = 0x0001;
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.SelectedTab = first;

            var controlPressed = true;
            var leftButtonPressed = true;
            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifierProvider(window, () => controlPressed);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragTabField = typeof(MainWindow).GetField(
                "tabDetachDragTab",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativePointType);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(leftButtonField);
            Assert.NotNull(tabDetachDragTabField);

            var restoreCursor = NativeWindowTest.TryGetCursorPosition(out var originalCursor);
            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                SetMainWindowHandle(window);

                leftButtonField!.SetValue(window, (Func<bool>)(() => leftButtonPressed));

                var firstChrome = FindTabStripChrome(window, first);
                var secondChrome = FindTabStripChrome(window, second);
                var firstPoint = firstChrome.PointToScreen(new System.Windows.Point(
                    firstChrome.ActualWidth / 2,
                    firstChrome.ActualHeight / 2));
                var secondPoint = secondChrome.PointToScreen(new System.Windows.Point(
                    secondChrome.ActualWidth / 2,
                    secondChrome.ActualHeight / 2));
                var windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(windowHandle != IntPtr.Zero);
                AttachMainWindowMessageHook(window);
                var secondMouseLParam = NativeWindowTest.MakeMouseLParamFromScreenPoint(windowHandle, secondPoint);
                var secondNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(secondPoint.X), (int)Math.Round(secondPoint.Y)]);
                Assert.NotNull(secondNativePoint);
                var secondTargetArgs = new object?[] { secondNativePoint, null };
                Assert.True(
                    (bool)tryGetTabAtScreenPoint!.Invoke(window, secondTargetArgs)! &&
                    ReferenceEquals(second, secondTargetArgs[1]),
                    $"Expected the second tab strip item under the simulated release point, hit '{(secondTargetArgs[1] as StreamTabViewModel)?.Title ?? "null"}'.");

                NativeWindowTest.SetCursorPosition((int)Math.Round(firstPoint.X), (int)Math.Round(firstPoint.Y));
                Mouse.Synchronize();
                var mouseDownArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = firstChrome
                };
                firstChrome.RaiseEvent(mouseDownArgs);

                Assert.True(
                    mouseDownArgs.Handled,
                    "Expected custom tab drag to handle Ctrl mouse-down.");
                Assert.True(
                    ReferenceEquals(first, tabDetachDragTabField!.GetValue(window)),
                    "Expected routed mouse-down to initialize drag state for the first tab.");

                window.UpdateLayout();
                secondChrome = FindTabStripChrome(window, second);
                secondPoint = secondChrome.PointToScreen(new System.Windows.Point(
                    secondChrome.ActualWidth / 2,
                    secondChrome.ActualHeight / 2));
                secondMouseLParam = NativeWindowTest.MakeMouseLParamFromScreenPoint(windowHandle, secondPoint);
                NativeWindowTest.SetCursorPosition((int)Math.Round(secondPoint.X), (int)Math.Round(secondPoint.Y));
                Mouse.Synchronize();
                controlPressed = false;
                leftButtonPressed = false;
                InvokeMainWindowLowLevelMouseHook(window, wmMouseMove, secondPoint);

                Assert.True(
                    ReferenceEquals(first, tabDetachDragTabField.GetValue(window)),
                    "Expected low-level hook activity inside the main window to leave routed tab drag state intact.");

                leftButtonPressed = true;
                NativeWindowTest.SendMessage(windowHandle, wmMouseMove, new IntPtr(mkLeftButton), secondMouseLParam);
                Assert.True(
                    ReferenceEquals(first, tabDetachDragTabField.GetValue(window)),
                    "Expected routed tab drag to remain active after moving over the second tab.");

                leftButtonPressed = false;
                NativeWindowTest.SendMessage(windowHandle, wmLeftButtonUp, IntPtr.Zero, secondMouseLParam);

                Assert.SequenceEqual(new[] { second, first }, viewModel.Tabs.ToArray());
                Assert.Equal(1, viewModel.TabStripItems.Count(item => item.Contains(first) && item.Contains(second)));
                Assert.SequenceEqual(new[] { first, second }, viewModel.VideoTabs.ToArray());
                Assert.Equal(first, viewModel.SelectedTab);
                Assert.True(second.IsMergedTabGroupMember);
                Assert.True(second.IsFirstMergedTabGroupMember);
                Assert.True(first.IsMergedTabGroupMember);
                Assert.True(first.IsLastMergedTabGroupMember);
                Assert.Equal(false, settings.MultiStreamEnabled);
            }
            finally
            {
                Mouse.Capture(null);
                if (restoreCursor)
                {
                    NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
                }

                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ("ctrl tab strip drop of a merged group over a single tab merges the whole group", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory();
            var logger = new MemoryLogger();
            var settings = new AppSettings();
            var viewModel = TestViewModels.CreateMain(
                settings,
                new FakeSettingsService(settings),
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                logger,
                action => action());
            var first = TestViewModels.CreateTab(StreamInputParser.Parse("albralelie", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var second = TestViewModels.CreateTab(StreamInputParser.Parse("summit1g", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());
            var third = TestViewModels.CreateTab(StreamInputParser.Parse("xqc", PlatformKind.Twitch), "best", streamlink, playbackFactory, new FakeChatClientFactory(), logger, action => action());

            viewModel.Tabs.Add(first);
            viewModel.Tabs.Add(second);
            viewModel.Tabs.Add(third);
            viewModel.SelectedTab = first;
            Assert.True(viewModel.TryMergeTabsIntoMultiView([second], first, second));
            viewModel.SelectedTab = second;

            var window = new MainWindow
            {
                Width = 1400,
                Height = 560,
                Topmost = true,
                DataContext = viewModel
            };
            RemoveMainWindowAutomaticStartup(window);
            SetMainWindowViewModel(window, viewModel);
            SetMainWindowControlModifier(window, pressed: true);

            var nativePointType = typeof(MainWindow).GetNestedType("NativePoint", BindingFlags.NonPublic);
            var continueDrag = typeof(MainWindow).GetMethod(
                "TryContinueTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [nativePointType!, typeof(bool)],
                null);
            var completeDrag = typeof(MainWindow).GetMethod(
                "TryCompleteTabDetachDrag",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasExceededDragDistance = typeof(MainWindow).GetMethod(
                "HasExceededTabDetachDragDistance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tryGetTabAtScreenPoint = typeof(MainWindow).GetMethod(
                "TryGetTabAtTabStripScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var leftButtonField = typeof(MainWindow).GetField(
                "isLeftMouseButtonPressed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragTabField = typeof(MainWindow).GetField(
                "tabDetachDragTab",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragSourceField = typeof(MainWindow).GetField(
                "tabDetachDragSource",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartPointField = typeof(MainWindow).GetField(
                "tabDetachDragStartPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tabDetachDragStartScreenPointField = typeof(MainWindow).GetField(
                "tabDetachDragStartScreenPoint",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nativePointType);
            Assert.NotNull(continueDrag);
            Assert.NotNull(completeDrag);
            Assert.NotNull(hasExceededDragDistance);
            Assert.NotNull(tryGetTabAtScreenPoint);
            Assert.NotNull(leftButtonField);
            Assert.NotNull(tabDetachDragTabField);
            Assert.NotNull(tabDetachDragSourceField);
            Assert.NotNull(tabDetachDragStartPointField);
            Assert.NotNull(tabDetachDragStartScreenPointField);

            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                leftButtonField!.SetValue(window, (Func<bool>)(() => true));

                var groupItem = FindTabStripListBoxItem(window, second);
                var thirdItem = FindTabStripListBoxItem(window, third);
                Assert.True(groupItem.DataContext is TabStripItemViewModel { IsGroup: true } groupTabStripItem &&
                    groupTabStripItem.Contains(first) &&
                    groupTabStripItem.Contains(second));
                Assert.True(thirdItem.DataContext is TabStripItemViewModel { IsGroup: false } singleTabStripItem &&
                    singleTabStripItem.Contains(third));

                var startPoint = groupItem.TransformToAncestor(window).Transform(new System.Windows.Point(
                    groupItem.ActualWidth / 2,
                    groupItem.ActualHeight / 2));
                var startScreenPoint = groupItem.PointToScreen(new System.Windows.Point(
                    groupItem.ActualWidth / 2,
                    groupItem.ActualHeight / 2));
                var nativeStartPoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(startScreenPoint.X), (int)Math.Round(startScreenPoint.Y)]);
                Assert.NotNull(nativeStartPoint);
                tabDetachDragSourceField!.SetValue(window, groupItem);
                tabDetachDragTabField!.SetValue(window, second);
                tabDetachDragStartPointField!.SetValue(window, startPoint);
                tabDetachDragStartScreenPointField!.SetValue(window, nativeStartPoint);

                var thirdPoint = thirdItem.PointToScreen(new System.Windows.Point(
                    thirdItem.ActualWidth / 2,
                    thirdItem.ActualHeight / 2));
                var targetNativePoint = Activator.CreateInstance(
                    nativePointType!,
                    [(int)Math.Round(thirdPoint.X), (int)Math.Round(thirdPoint.Y)]);
                Assert.NotNull(targetNativePoint);

                Assert.True(
                    (bool)hasExceededDragDistance!.Invoke(window, [targetNativePoint])!,
                    "Expected grouped tab-to-tab drag to exceed the system drag threshold.");
                var targetArgs = new object?[] { targetNativePoint, null };
                Assert.True(
                    (bool)tryGetTabAtScreenPoint!.Invoke(window, targetArgs)!,
                    "Expected the single tab strip item under the simulated drag point.");
                Assert.Equal(third, targetArgs[1]);

                Assert.True(
                    (bool)continueDrag!.Invoke(window, [targetNativePoint, true])!,
                    "Expected grouped tab drag to stay active over the target tab until mouse-up.");
                Assert.SequenceEqual(new[] { first, second, third }, viewModel.Tabs.ToArray());
                Assert.Equal(2, viewModel.TabStripItems.Count);

                Assert.True(
                    (bool)completeDrag!.Invoke(window, [targetNativePoint])!,
                    "Expected mouse-up over the single tab to merge the dragged group with it.");

                Assert.SequenceEqual(new[] { third, first, second }, viewModel.Tabs.ToArray());
                Assert.Equal(1, viewModel.TabStripItems.Count(item =>
                    item.Contains(first) &&
                    item.Contains(second) &&
                    item.Contains(third)));
                Assert.Equal(second, viewModel.SelectedTab);
                Assert.Equal(0, third.VideoGridRow);
                Assert.Equal(0, third.VideoGridColumn);
                Assert.Equal(0, first.VideoGridRow);
                Assert.Equal(1, first.VideoGridColumn);
                Assert.Equal(1, second.VideoGridRow);
                Assert.Equal(0, second.VideoGridColumn);
                Assert.True(third.IsMergedTabGroupMember);
                Assert.True(third.IsFirstMergedTabGroupMember);
                Assert.True(first.IsMergedTabGroupMember);
                Assert.Equal(false, first.IsFirstMergedTabGroupMember);
                Assert.Equal(false, first.IsLastMergedTabGroupMember);
                Assert.True(second.IsMergedTabGroupMember);
                Assert.True(second.IsLastMergedTabGroupMember);
                Assert.Equal(false, settings.MultiStreamEnabled);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });
    }),
    ];
}
