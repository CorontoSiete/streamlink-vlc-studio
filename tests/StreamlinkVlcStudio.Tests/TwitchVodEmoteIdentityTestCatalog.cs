internal static class TwitchVodEmoteIdentityTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Twitch VOD repeated emotes use canonical image ids instead of occurrence ids", RepeatedEmotesUseCanonicalImageIds),
        ("Twitch VOD emote identities retain legacy and modern CDN id formats", LegacyAndModernEmoteIdsRemainSupported),
        ("Twitch VOD malformed emote identities preserve visible text without broken image URLs", InvalidEmoteIdsRemainText)
    ];

    private static Task RepeatedEmotesUseCanonicalImageIds()
    {
        // The frozen process held these occurrence-specific ids for repeated xqcG.
        // Twitch's current public response supplies emoteID separately from node id.
        var message = ReadMessage("""
        [
          { "text": "xqcG", "emote": { "id": "1035696;0;3", "emoteID": "1035696", "from": 0, "__typename": "EmbeddedEmote" } },
          { "text": " " },
          { "text": "xqcG", "emote": { "id": "1035696;5;8", "emoteID": "1035696", "from": 5, "__typename": "EmbeddedEmote" } }
        ]
        """);

        Assert.Equal("xqcG xqcG", message.Message);
        var emotes = message.Emotes ?? throw new InvalidOperationException("Expected both replay emotes.");
        Assert.Equal(2, emotes.Count);
        Assert.Equal(0, emotes[0].StartIndex);
        Assert.Equal(4, emotes[0].EndIndex);
        Assert.Equal(5, emotes[1].StartIndex);
        Assert.Equal(9, emotes[1].EndIndex);
        foreach (var emote in emotes)
        {
            Assert.Equal("xqcG", emote.Code);
            Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/1035696/static/light/2.0", emote.ImageUrl);
        }

        return Task.CompletedTask;
    }

    private static Task LegacyAndModernEmoteIdsRemainSupported()
    {
        const string modernId = "emotesv2_61905b27c9b649e8af5c92e1a5c3cd64";
        (string Fragment, string ExpectedId)[] cases =
        [
            ("""{ "text": "Emote", "emoticon": { "emoticon_id": "25" } }""", "25"),
            ("""{ "text": "Emote", "emoticon": { "emoticonId": " 25 " } }""", "25"),
            ("""{ "text": "Emote", "emote": { "id": "25" } }""", "25"),
            ("""{ "text": "Emote", "emoticon_id": "25" }""", "25"),
            ("""{ "text": "Emote", "emoticonId": "25" }""", "25"),
            ("""{ "text": "Emote", "emoteID": "25" }""", "25"),
            ("""{ "text": "Emote", "emote": { "emoteID": "25", "emoticon_id": "26", "id": "25;0;4" } }""", "25"),
            ("""{ "text": "Emote", "emote": { "emoteID": "emotesv2_61905b27c9b649e8af5c92e1a5c3cd64", "id": "emotesv2_61905b27c9b649e8af5c92e1a5c3cd64;0;4" } }""", modernId),
            ("""{ "text": "Emote", "emote": { "id": "emotesv2_61905b27c9b649e8af5c92e1a5c3cd64" } }""", modernId)
        ];

        foreach (var (fragment, expectedId) in cases)
        {
            var message = ReadMessage($"[{fragment}]");
            Assert.Equal("Emote", message.Message);
            var emotes = message.Emotes ?? throw new InvalidOperationException($"Expected supported id: {fragment}");
            Assert.Equal(1, emotes.Count);
            Assert.Equal($"https://static-cdn.jtvnw.net/emoticons/v2/{expectedId}/static/light/2.0", emotes[0].ImageUrl);
        }

        return Task.CompletedTask;
    }

    private static Task InvalidEmoteIdsRemainText()
    {
        object?[] invalidIds =
        [
            null,
            "",
            "1035696;0;3",
            "1035696%3B0%3B3",
            "RW1iZWRkZWRFbW90ZToxMDM1Njk2",
            "RW1iZWRkZWRFbW90ZToxMDM1Njk2=",
            "../25",
            "25/other",
            "25?query=1",
            "25 26",
            "emotesv2_",
            "emotesv2_bad/value",
            new string('1', 257),
            false
        ];

        foreach (var invalidId in invalidIds)
        {
            foreach (var property in new[] { "id", "emoteID", "emoticon_id" })
            {
                var fragments = JsonSerializer.Serialize(new[]
                {
                    new { text = "VisibleText", emote = new Dictionary<string, object?> { [property] = invalidId } }
                });
                var message = ReadMessage(fragments);
                Assert.Equal("VisibleText", message.Message);
                Assert.True(message.Emotes is null || message.Emotes.Count == 0,
                    $"Invalid {property} must remain text: {invalidId}");
            }
        }

        return Task.CompletedTask;
    }

    private static ChatMessage ReadMessage(string fragmentsJson)
    {
        using var document = JsonDocument.Parse($$"""
        [{
          "data": { "video": { "comments": {
            "pageInfo": { "hasNextPage": false },
            "edges": [{ "node": {
              "id": "regression-message",
              "contentOffsetSeconds": 60,
              "createdAt": "2026-09-20T12:01:00Z",
              "commenter": { "login": "viewer" },
              "message": { "fragments": {{fragmentsJson}} }
            } }]
          } } }
        }]
        """);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            DateTimeOffset.Parse("2026-09-20T12:00:00Z", CultureInfo.InvariantCulture),
            TimeSpan.FromHours(1),
            true,
            "");
        var page = TwitchVodChatFetcher.ReadPage(document.RootElement, replay);
        Assert.Equal(1, page.Messages.Count);
        return page.Messages[0].Message;
    }
}
