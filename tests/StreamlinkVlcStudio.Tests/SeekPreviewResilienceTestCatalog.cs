internal static class SeekPreviewResilienceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("seek preview resilience: metadata deadlines use retry backoff", () => DeadlineAsync(metadata: true)),
        ("seek preview resilience: sprite deadlines use retry backoff", () => DeadlineAsync(metadata: false)),
        ("seek preview resilience: metadata cancellation allows immediate retry", () => CancellationAsync(metadata: true)),
        ("seek preview resilience: sprite cancellation allows immediate retry", () => CancellationAsync(metadata: false)),
        ("seek preview resilience: malformed GraphQL metadata avoids sprite requests", MalformedMetadataAsync),
        ("seek preview resilience: underflowing storyboard intervals are rejected", UnderflowingIntervalAsync)
    ];

    private static readonly Uri ManifestUri = new("https://vod-secure.twitch.tv/storyboard.json");
    private const string Metadata = """{"data":{"video":{"lengthSeconds":60,"seekPreviewsURL":"https://vod-secure.twitch.tv/storyboard.json"}}}""";
    private const string Storyboard = """[{"width":192,"height":108,"cols":2,"rows":1,"count":2,"images":["sprite.jpg"]}]""";

    private static async Task DeadlineAsync(bool metadata)
    {
        var attempts = 0;
        long now = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (IsTarget(request, metadata))
            {
                attempts++;
                throw new OperationCanceledException("HTTP deadline");
            }
            return Response(request);
        }));
        var images = new ReplaySeekPreviewImages(Client(http), () => now);
        var source = new ReplaySeekPreviewSource("123", "123", null, PlatformKind.Twitch, null, "");
        Assert.True(await images.GetAsync(source, 0, default) is null);
        Assert.True(await images.GetAsync(source, 0, default) is null);
        Assert.Equal(1, attempts);
        now = metadata ? 60_000 : 30_000;
        Assert.True(await images.GetAsync(source, 0, default) is null);
        Assert.Equal(2, attempts);
    }

    private static async Task CancellationAsync(bool metadata)
    {
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (IsTarget(request, metadata))
            {
                if (++attempts == 1)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                // A missing preview is a normal result, without image decoding or live HTTP.
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return Response(request);
        }));
        var images = new ReplaySeekPreviewImages(Client(http), () => 0);
        await Assert.ThrowsAsync<OperationCanceledException>(() => images.GetAsync("123", 0, cancellation.Token));
        Assert.True(await images.GetAsync("123", 0, default) is null);
        Assert.Equal(2, attempts);
    }

    private static async Task MalformedMetadataAsync()
    {
        foreach (var payload in new[]
        {
            "[]", "null", "42", "{\"data\":null}",
            Metadata.Replace("\"lengthSeconds\":60", "\"lengthSeconds\":null"),
            Metadata.Replace("\"lengthSeconds\":60", "\"lengthSeconds\":\"60\""),
            Metadata.Replace("\"lengthSeconds\":60", "\"lengthSeconds\":-1"),
            Metadata.Replace("\"lengthSeconds\":60", "\"lengthSeconds\":1e999")
        })
        {
            var requests = 0;
            using var http = new HttpClient(new FakeHttpMessageHandler(_ =>
            {
                requests++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
            }));
            Assert.True(await Client(http).GetStoryboardAsync("123", default) is null);
            Assert.Equal(1, requests);
        }
    }

    private static Task UnderflowingIntervalAsync()
    {
        Assert.True(TwitchSeekStoryboard.Parse(Storyboard, ManifestUri, double.Epsilon) is null);
        var valid = TwitchSeekStoryboard.Parse(Storyboard, ManifestUri, 60)!;
        Assert.NotNull(valid.GetFrame(0));
        Assert.Equal(192, valid.GetFrame(30)!.X);
        return Task.CompletedTask;
    }

    private static bool IsTarget(HttpRequestMessage request, bool metadata) => metadata
        ? request.RequestUri!.Host == "gql.twitch.tv"
        : request.RequestUri!.AbsolutePath.EndsWith("sprite.jpg", StringComparison.Ordinal);

    private static HttpResponseMessage Response(HttpRequestMessage request) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = new StringContent(request.RequestUri!.Host == "gql.twitch.tv" ? Metadata : Storyboard)
    };

    private static TwitchSeekPreviewClient Client(HttpClient http) => new(http,
        new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") })));
}
