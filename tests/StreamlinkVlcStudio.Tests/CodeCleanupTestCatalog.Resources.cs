using System.Net.Security;
using System.Net.Sockets;

internal static partial class CodeCleanupTestCatalog
{
    private static async Task TwitchDisconnectAfterFlushFailureAsync()
    {
        var logger = new MemoryLogger();
        using var writerStream = new FlushFailureStream();
        using var readerStream = new MemoryStream();
        using var tlsStream = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        using var tcp = new TcpClient();
        var socketHandle = tcp.Client.SafeHandle;
        await using var client = new TwitchChatClient(new ChatSettings(), logger);
        SetPrivateField(client, "writer", new StreamWriter(writerStream));
        SetPrivateField(client, "reader", new BoundedUtf8LineReader(readerStream, leaveOpen: false));
        SetPrivateField(client, "sslStream", new SslStream(tlsStream));
        SetPrivateField(client, "tcpClient", tcp);
        SetPrivateField(client, "readCancellation", cancellation);
        SetPrivateField(client, "connectedChannel", "streamer");
        SetPrivateField(client, "canSendMessages", true);

        await client.DisconnectAsync();

        Assert.True(socketHandle.IsClosed);
        Assert.Equal(false, readerStream.CanRead);
        Assert.Equal(false, tlsStream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
        AssertChatFieldsCleared(client, "writer", "reader", "sslStream", "tcpClient", "readCancellation", "connectedChannel");
        Assert.Equal(false, GetPrivateField(client, "canSendMessages"));
        Assert.True(logger.Entries.Any(entry => entry.Exception is IOException));
        await client.DisconnectAsync();
    }

    private static async Task ChatDisconnectAfterCancellationFailureAsync()
    {
        Func<IChatClient>[] clients =
        [
            () => new TwitchChatClient(new ChatSettings(), new MemoryLogger()),
            () => new KickChatClient(new ChatSettings(), new MemoryLogger())
        ];
        foreach (var createClient in clients)
        {
            using var cancellation = new CancellationTokenSource();
            using var registration = cancellation.Token.Register(() => throw new IOException("Cancellation callback failed."));
            var client = createClient();
            await using var eventSub = client is TwitchChatClient
                ? new TwitchPredictionEventSubClient((TwitchPredictionApiClient)GetPrivateField(client, "predictionApiClient")!,
                    new MemoryLogger(), "token", "client", "channel", _ => { }, _ => { }, _ => Task.CompletedTask)
                : null;
            await using (client)
            {
                if (eventSub is not null) SetPrivateField(client, "predictionEventSubClient", eventSub);
                SetPrivateField(client, "readCancellation", cancellation);
                SetPrivateField(client, "connectedChannel", "streamer");
                SetPrivateField(client, "canSendMessages", true);

                await Assert.ThrowsAsync<AggregateException>(() => client.DisconnectAsync());

                AssertChatFieldsCleared(client, "readCancellation", "connectedChannel");
                Assert.Equal(false, GetPrivateField(client, "canSendMessages"));
                Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
                if (eventSub is not null)
                {
                    AssertChatFieldsCleared(client, "predictionEventSubClient");
                    Assert.Throws<ObjectDisposedException>(eventSub.Start);
                }
                await client.DisconnectAsync();
            }
        }
    }

    private static async Task PredictionEventSubDisposalAfterWorkerFailureAsync()
    {
        using var http = new HttpClient();
        var client = new TwitchPredictionEventSubClient(new TwitchPredictionApiClient(http), new MemoryLogger(),
            "token", "client", "channel", _ => { }, _ => { }, _ => Task.FromException(new IOException("Worker failed.")));
        client.Start();
        using var cancellation = (CancellationTokenSource)GetPrivateField(client, "cancellation")!;

        await Assert.ThrowsAsync<IOException>(() => client.DisposeAsync().AsTask());

        Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
        foreach (var field in new[] { "cancellation", "runTask", "webSocket" })
            Assert.Equal<object?>(null, GetPrivateField(client, field));
        Assert.Throws<ObjectDisposedException>(client.Start);
    }

    private static object? GetPrivateField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    private static void SetPrivateField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static void AssertChatFieldsCleared(IChatClient client, params string[] names)
    {
        foreach (var name in names) Assert.Equal<object?>(null, GetPrivateField(client, name));
    }

    private sealed class FlushFailureStream : MemoryStream
    {
        public override void Flush() => throw new IOException("The disconnected stream cannot flush.");
    }
}
