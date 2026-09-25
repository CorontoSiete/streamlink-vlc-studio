internal static class ReplayChatResilienceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("replay chat resilience: malformed Twitch envelopes remain retryable", MalformedTwitchPagesAsync),
        ("replay chat resilience: valid empty Twitch pages retain completion semantics", EmptyTwitchPagesAsync),
        ("replay chat resilience: malformed Kick envelopes are not empty history", MalformedKickPages),
        ("replay chat resilience: valid empty Kick envelopes remain usable", EmptyKickPages),
        ("replay chat resilience: malformed Kick cursors cannot terminate pagination", MalformedKickCursors),
        ("replay chat resilience: failed later Kick pages retry the original range", FailedKickPageAsync),
        ("replay chat resilience: repeated Kick cursors do not skip missing history", RepeatedKickCursorAsync),
        ("replay chat resilience: Kick page budget does not claim complete coverage", KickPageBudgetAsync),
        ("replay chat resilience: Kick cancellation remains cancellation", CanceledKickPageAsync),
        ("replay chat resilience: Kick cursors preserve microsecond boundaries", KickCursorPrecision)
    ];

    private static readonly DateTimeOffset StartedAt = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static ReplaySessionInfo Replay(PlatformKind platform) => new(
        platform, "streamer", "https://example.invalid/video", "123", StartedAt,
        TimeSpan.FromMinutes(10), true, "", ChatRoomId: "42");

    private static async Task MalformedTwitchPagesAsync()
    {
        foreach (var body in new[]
        {
            "null", "[]", "{}", "{\"data\":null}",
            """{"data":{"video":{"comments":{}}}}""",
            """{"data":{"video":{"comments":{"edges":{},"pageInfo":{"hasNextPage":false}}}}}""",
            """{"data":{"video":{"comments":{"edges":[],"pageInfo":null}}}}""",
            """{"data":{"video":{"comments":{"edges":[],"pageInfo":{"hasNextPage":"broken"}}}}}"""
        })
        {
            using var client = new HttpClient(new FakeHttpMessageHandler(_ => Json(body)));
            var result = await new TwitchVodChatFetcher(client).FetchAsync(
                Replay(PlatformKind.Twitch), TimeSpan.Zero, CancellationToken.None);
            Assert.Equal(VodChatFetchOutcome.Failed, result.Outcome);
            Assert.Equal(TimeSpan.Zero, result.CoveredThroughOffset);
        }
    }

    private static async Task EmptyTwitchPagesAsync()
    {
        foreach (var hasNext in new[] { true, false })
        {
            var body = """[{"data":{"video":{"comments":{"edges":[],"pageInfo":{"hasNextPage":HAS_NEXT}}}}}]"""
                .Replace("HAS_NEXT", hasNext ? "true" : "false", StringComparison.Ordinal);
            using var client = new HttpClient(new FakeHttpMessageHandler(_ => Json(body)));
            var result = await new TwitchVodChatFetcher(client).FetchAsync(
                Replay(PlatformKind.Twitch), TimeSpan.Zero, CancellationToken.None);
            Assert.Equal(hasNext ? VodChatFetchOutcome.Loaded : VodChatFetchOutcome.Completed, result.Outcome);
            Assert.True(result.CoveredThroughOffset > TimeSpan.Zero);
        }
    }

    private static Task MalformedKickPages()
    {
        foreach (var body in new[] { "null", "[]", "{}", "{\"data\":null}", "{\"data\":{\"messages\":{}}}" })
        {
            using var document = JsonDocument.Parse(body);
            Assert.Throws<InvalidDataException>(() => KickChatTransport.ReadPage(document.RootElement, "streamer"));
        }
        return Task.CompletedTask;
    }

    private static Task EmptyKickPages()
    {
        foreach (var body in new[] { "{\"data\":[]}", "{\"messages\":[]}", "{\"data\":{\"messages\":[]}}" })
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal(0, KickChatTransport.ReadPage(document.RootElement, "streamer").Messages.Count);
        }
        return Task.CompletedTask;
    }

    private static Task MalformedKickCursors()
    {
        foreach (var cursor in new[] { "true", "{}", "[]" })
        {
            using var document = JsonDocument.Parse("{\"data\":{\"messages\":[],\"cursor\":" + cursor + "}}");
            Assert.Throws<InvalidDataException>(() => KickChatTransport.ReadPage(document.RootElement, "streamer"));
        }
        using var numeric = JsonDocument.Parse("""{"data":{"messages":[],"cursor":1234567890123456}}""");
        Assert.Equal("1234567890123456", KickChatTransport.ReadPage(numeric.RootElement, "streamer").Cursor);
        return Task.CompletedTask;
    }

    private static async Task FailedKickPageAsync()
    {
        var fail = true;
        var requestedCursors = new List<string>();
        using var client = KickClient(request =>
        {
            requestedCursors.Add(request.RequestUri!.Query);
            return request.RequestUri.Query.Contains("cursor=older", StringComparison.Ordinal)
                ? fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(KickPage(0, null))
                : Json(KickPage(15, "older"));
        });
        var fetcher = new KickVodChatFetcher(client, new MemoryLogger(), (_, _, _) => Task.FromResult<string?>(null));
        var result = await fetcher.FetchAsync(Replay(PlatformKind.Kick), new AppSettings(), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(VodChatFetchOutcome.Failed, result.Outcome);
        Assert.Equal(TimeSpan.Zero, result.CoveredThroughOffset);

        Assert.Equal(TimeSpan.FromSeconds(15), result.Messages.Single().Offset);
        fail = false;
        result = await fetcher.FetchAsync(Replay(PlatformKind.Kick), new AppSettings(), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(VodChatFetchOutcome.Loaded, result.Outcome);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal(KickVodChatFetcher.ChunkSize, result.CoveredThroughOffset);
        Assert.Equal(requestedCursors[0], requestedCursors[2]);
    }

    private static async Task RepeatedKickCursorAsync()
    {
        using var client = KickClient(_ => Json(KickPage(15, "same")));
        var result = await FetchKickAsync(client);
        Assert.Equal(VodChatFetchOutcome.Failed, result.Outcome);
        Assert.Equal(TimeSpan.Zero, result.CoveredThroughOffset);
    }

    private static async Task KickPageBudgetAsync()
    {
        var requests = 0;
        using var client = KickClient(_ => Json(KickPage(15, (++requests).ToString(CultureInfo.InvariantCulture))));
        var result = await FetchKickAsync(client);
        Assert.Equal(VodChatFetchOutcome.Failed, result.Outcome);
        Assert.Equal(TimeSpan.Zero, result.CoveredThroughOffset);
        Assert.Equal(20, requests);
        Assert.Equal(TimeSpan.FromSeconds(15), result.Messages.Single().Offset);
    }

    private static async Task CanceledKickPageAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = KickClient(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var fetcher = new KickVodChatFetcher(client, new MemoryLogger(), (_, _, _) =>
            throw new InvalidOperationException("Cancellation must not start a fallback request."));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fetcher.FetchAsync(
            Replay(PlatformKind.Kick), new AppSettings(), TimeSpan.Zero, cancellation.Token));
    }

    private static Task KickCursorPrecision()
    {
        Assert.Equal("1234567", KickVodChatFetcher.ToCursor(DateTimeOffset.UnixEpoch.AddTicks(12_345_670)));
        Assert.Equal("1234568", KickVodChatFetcher.ToCursor(DateTimeOffset.UnixEpoch.AddTicks(12_345_671)));
        Assert.Equal("-1234567", KickVodChatFetcher.ToCursor(DateTimeOffset.UnixEpoch.AddTicks(-12_345_671)));
        Assert.Equal("0", KickVodChatFetcher.ToCursor(DateTimeOffset.UnixEpoch.AddTicks(-1)));
        return Task.CompletedTask;
    }

    private static Task<VodChatFetchResult> FetchKickAsync(HttpClient client) =>
        new KickVodChatFetcher(client, new MemoryLogger(), (_, _, _) => Task.FromResult<string?>(null))
            .FetchAsync(Replay(PlatformKind.Kick), new AppSettings(), TimeSpan.Zero, CancellationToken.None);

    private static HttpClient KickClient(Func<HttpRequestMessage, HttpResponseMessage> messages) =>
        new(new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
            ? messages(request)
            : Json("""{"id":42,"chatroom":{"id":42},"user_id":7}""")));

    private static string KickPage(int seconds, string? cursor) => JsonSerializer.Serialize(new
    {
        data = new
        {
            messages = new[] { new { id = $"message-{seconds}", content = $"at {seconds}", created_at = StartedAt.AddSeconds(seconds), sender = new { username = "viewer" } } },
            cursor
        }
    });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
