internal static class KickRecentChatTestCatalog
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Kick recent history: overlapping pages emit each message once", OverlappingPagesAsync),
        ("Kick recent history: changing cursors cannot loop over duplicate messages", RepeatedMessagesAsync),
        ("Kick recent history: disposal cancels the startup request", DisposePendingRequestAsync),
        ("Kick recent history: a retired request cannot block replacement history", RetiredRequestAsync),
        ("Kick recent history: cancellation stops message delivery", CancelDeliveryAsync)
    ];

    private static async Task OverlappingPagesAsync()
    {
        var calls = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => ++calls switch
        {
            1 => Page(10, 10, "page-2"),
            2 => Page(5, 10, "page-3"),
            _ => Page(0, 10, "")
        }));
        await using var client = CreateClient(httpClient);
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);

        var loaded = await LoadRecentAsync(client);

        Assert.Equal(20, loaded);
        Assert.Equal(3, calls);
        Assert.SequenceEqual(Enumerable.Range(0, 20).Select(index => $"message-{index}"),
            received.Select(message => message.MessageId));
    }

    private static async Task RepeatedMessagesAsync()
    {
        var calls = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => Page(0, 1, $"page-{++calls}")));
        await using var client = CreateClient(httpClient);
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);

        Assert.Equal(1, await LoadRecentAsync(client));
        Assert.Equal(2, calls);
        Assert.Equal(1, received.Count);
    }

    private static async Task DisposePendingRequestAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new AsyncHttpMessageHandler(async (_, token) =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Page(0, 1, "");
        }));
        await using var client = CreateClient(httpClient);
        var cancellation = new CancellationTokenSource(); // Owned by the client after assignment.
        typeof(KickChatClient).GetField("readCancellation", PrivateInstance)!.SetValue(client, cancellation);
        var received = 0;
        client.MessageReceived += (_, _) => received++;
        typeof(KickChatClient).GetMethod("StartRecentChatBackfill", PrivateInstance)!
            .Invoke(client, ["streamer", "668", cancellation.Token]);
        var work = (Task)typeof(KickChatClient).GetField("recentChatBackfillTask", PrivateInstance)!.GetValue(client)!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(canceled.Task.IsCompletedSuccessfully);
        Assert.True(work.IsCompletedSuccessfully);
        Assert.Equal(0, received);
    }

    private static KickChatClient CreateClient(HttpClient httpClient)
    {
        return new KickChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
    }

    private static async Task RetiredRequestAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var httpClient = new HttpClient(new AsyncHttpMessageHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                // Model a response stream that does not cooperate with disconnect cancellation.
                await release.Task;
                return Page(0, 1, "retired-cursor");
            }
            return Page(10, 1, "");
        }));
        await using var client = CreateClient(httpClient);
        var cancellation = new CancellationTokenSource(); // Owned by the client after assignment.
        typeof(KickChatClient).GetField("readCancellation", PrivateInstance)!.SetValue(client, cancellation);
        var received = new List<ChatMessage>();
        client.MessageReceived += (_, message) => received.Add(message);
        typeof(KickChatClient).GetMethod("StartRecentChatBackfill", PrivateInstance)!
            .Invoke(client, ["streamer", "668", cancellation.Token]);
        var retired = (Task)typeof(KickChatClient).GetField("recentChatBackfillTask", PrivateInstance)!.GetValue(client)!;
        Task<int>? replacement = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(6));
            replacement = LoadRecentAsync(client);
            Assert.Equal(1, await replacement.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.SequenceEqual(new[] { "message-10" }, received.Select(message => message.MessageId));
        }
        finally
        {
            release.TrySetResult();
            await retired.WaitAsync(TimeSpan.FromSeconds(2));
            if (replacement is not null) await replacement.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(1, received.Count);
    }

    private static async Task CancelDeliveryAsync()
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => Page(0, 3, "")));
        await using var client = CreateClient(httpClient);
        using var cancellation = new CancellationTokenSource();
        var received = 0;
        client.MessageReceived += (_, _) =>
        {
            received++;
            cancellation.Cancel();
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => LoadRecentAsync(client, cancellation.Token));
        Assert.Equal(1, received);
    }

    private static Task<int> LoadRecentAsync(KickChatClient client, CancellationToken cancellationToken = default) =>
        (Task<int>)typeof(KickChatClient).GetMethod("BackfillRecentChatAsync", PrivateInstance)!
            .Invoke(client, ["streamer", "668", 25, cancellationToken])!;

    private static HttpResponseMessage Page(int start, int count, string cursor) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            data = new
            {
                cursor,
                messages = Enumerable.Range(start, count).Select(index => new
                {
                    id = $"message-{index}",
                    content = "hello",
                    created_at = DateTimeOffset.UnixEpoch.AddSeconds(index).ToString("O", CultureInfo.InvariantCulture),
                    sender = new { username = "viewer" }
                })
            }
        }), Encoding.UTF8, "application/json")
    };
}
