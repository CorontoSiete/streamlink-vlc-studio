internal static class ChatSendLifetimeTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("chat send lifetime: edits survive a pending send", () => PreserveDraftAsync(retry: false, retype: false)),
        ("chat send lifetime: a retyped draft survives a pending send", () => PreserveDraftAsync(retry: false, retype: true)),
        ("chat send lifetime: edits survive a credential retry", () => PreserveDraftAsync(retry: true, retype: false)),
        ("chat send lifetime: a retyped draft survives a credential retry", () => PreserveDraftAsync(retry: true, retype: true)),
        ("chat send lifetime: shutdown cancels sending without a late echo", ShutdownAsync),
        ("chat send lifetime: sending waits for the published client to connect", () => PendingConnectionAsync(seek: false)),
        ("chat send lifetime: pending connection rechecks replay state", () => PendingConnectionAsync(seek: true)),
        ("chat send lifetime: replay transition prevents credential retry", RetryAfterSeekAsync),
        ("chat send lifetime: credential retry rechecks replay after connecting", SeekDuringRetryAsync),
        ("chat send lifetime: late send failures do not reconnect a disposed tab", LateFailureAsync)
    ];

    private static async Task PreserveDraftAsync(bool retry, bool retype)
    {
        var factory = new FakeChatClientFactory();
        await using var tab = CreateTab(factory);
        await tab.RestartChatAsync(CreateSettings());
        var entered = Completion();
        var release = Completion();
        var attempts = 0;
        factory.Client.SendHandler = async (_, _) =>
        {
            if (retry && ++attempts == 1) throw new InvalidOperationException("Expired OAuth token.");
            entered.TrySetResult();
            await release.Task;
        };
        tab.OutgoingChatText = " first message ";
        var sending = tab.SendChatMessageAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            tab.OutgoingChatText = "second message";
            if (retype) tab.OutgoingChatText = " first message ";
            var draft = tab.OutgoingChatText;
            release.TrySetResult();
            await sending;
            Assert.Equal(draft, tab.OutgoingChatText);
            Assert.SequenceEqual(["first message"], factory.Client.SentMessages);
            Assert.Equal(1, tab.ChatMessages.Count(message => message.Message == "first message"));
        }
        finally
        {
            release.TrySetResult();
            await sending;
        }
    }

    private static async Task ShutdownAsync()
    {
        var factory = new FakeChatClientFactory();
        await using var tab = CreateTab(factory);
        await tab.RestartChatAsync(CreateSettings());
        var release = Completion();
        var sendToken = CancellationToken.None;
        factory.Client.SendHandler = (_, token) =>
        {
            sendToken = token;
            return release.Task; // Simulate a provider returning after cancellation.
        };
        tab.OutgoingChatText = "first message";
        var sending = tab.SendChatMessageAsync();
        try
        {
            await tab.DisposeAsync();
            var messageCount = tab.ChatMessages.Count;
            release.TrySetResult();
            await sending;
            Assert.True(sendToken.IsCancellationRequested);
            Assert.Equal("first message", tab.OutgoingChatText);
            Assert.Equal(messageCount, tab.ChatMessages.Count);
        }
        finally
        {
            release.TrySetResult();
            await sending;
        }
    }

    private static async Task PendingConnectionAsync(bool seek)
    {
        var factory = new FakeChatClientFactory();
        await using var tab = CreateTab(factory);
        var entered = Completion();
        var release = Completion();
        factory.Client.ConnectHandler = (_, _, token) =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(token);
        };
        var settings = CreateSettings();
        settings.Chat.TwitchOAuthToken = ""; // A pending anonymous connection needs no credential retry.
        var connecting = tab.RestartChatAsync(settings);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        tab.OutgoingChatText = "first message";
        var sending = tab.SendChatMessageAsync();
        try
        {
            Assert.Equal(false, sending.IsCompleted);
            if (seek) SetBehindLive(tab);
            release.TrySetResult();
            await connecting;
            await sending;
            Assert.Equal(seek ? 0 : 1, factory.Client.SentMessages.Count);
            Assert.Equal(seek ? "first message" : "", tab.OutgoingChatText);
            Assert.Equal(false, tab.ChatMessages.Any(message => message.Message.Contains("Chat send failed")));
        }
        finally
        {
            release.TrySetResult();
            await connecting;
            await sending;
        }
    }

    private static async Task RetryAfterSeekAsync()
    {
        var factory = new FakeChatClientFactory();
        await using var tab = CreateTab(factory);
        await tab.RestartChatAsync(CreateSettings());
        var calls = 0;
        factory.Client.SendHandler = (_, _) =>
        {
            if (++calls == 1)
            {
                SetBehindLive(tab);
                throw new InvalidOperationException("Expired OAuth token.");
            }
            return Task.CompletedTask;
        };
        tab.OutgoingChatText = "first message";
        await tab.SendChatMessageAsync();
        Assert.Equal(1, calls);
        Assert.Equal(1, factory.Client.ConnectCount);
        Assert.Equal("first message", tab.OutgoingChatText);
    }

    private static async Task SeekDuringRetryAsync()
    {
        var factory = new FakeChatClientFactory();
        await using var tab = CreateTab(factory);
        await tab.RestartChatAsync(CreateSettings());
        var entered = Completion();
        var release = Completion();
        factory.Client.ConnectHandler = (_, _, token) =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(token);
        };
        var calls = 0;
        factory.Client.SendHandler = (_, _) => ++calls == 1
            ? Task.FromException(new InvalidOperationException("Expired OAuth token."))
            : Task.CompletedTask;
        tab.OutgoingChatText = "first message";
        var sending = tab.SendChatMessageAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            SetBehindLive(tab);
            release.TrySetResult();
            await sending;
            Assert.Equal(1, calls);
            Assert.Equal("first message", tab.OutgoingChatText);
        }
        finally
        {
            release.TrySetResult();
            await sending;
        }
    }

    private static async Task LateFailureAsync()
    {
        var factory = new FakeChatClientFactory();
        await using var tab = CreateTab(factory);
        await tab.RestartChatAsync(CreateSettings());
        var release = Completion();
        factory.Client.SendHandler = (_, _) => release.Task;
        tab.OutgoingChatText = "first message";
        var sending = tab.SendChatMessageAsync();
        try
        {
            await tab.DisposeAsync();
            var messageCount = tab.ChatMessages.Count;
            release.TrySetException(new InvalidOperationException("Expired OAuth token."));
            await sending;
            Assert.Equal(messageCount, tab.ChatMessages.Count);
            Assert.Equal(1, factory.Client.ConnectCount);
        }
        finally
        {
            release.TrySetCanceled();
            await sending;
        }
    }

    private static StreamTabViewModel CreateTab(FakeChatClientFactory factory) => TestViewModels.CreateTab(
        StreamInputParser.FromChannel(PlatformKind.Twitch, "streamer"), "best",
        new FakeStreamlinkService(), new FakePlaybackEngineFactory(), factory, new MemoryLogger(), action => action());

    private static AppSettings CreateSettings() => new()
    {
        Chat = new ChatSettings { ConnectAutomatically = true, Layout = ChatLayout.Docked, TwitchOAuthToken = "test-token" }
    };

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void SetBehindLive(StreamTabViewModel tab) =>
        typeof(StreamTabViewModel).GetProperty(nameof(StreamTabViewModel.IsBehindLive))!.SetValue(tab, true);
}
