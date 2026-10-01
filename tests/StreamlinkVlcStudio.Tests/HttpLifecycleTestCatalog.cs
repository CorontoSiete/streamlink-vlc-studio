using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Limits;

internal static class HttpLifecycleTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("HTTP lifecycle: Kick HTML reads use the HTML budget and retain JSON bounds", HtmlPayloadBudgetAsync),
        ("HTTP lifecycle: replay validation honors cancellation before and after DNS", ReplayValidationCancellationAsync)
    ];

    private static async Task HtmlPayloadBudgetAsync()
    {
        var body = "<html>" + new string(' ', PayloadLimits.HttpJsonBytes) + "</html>";
        using var client = new HttpClient(new FakeHttpMessageHandler(request => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "text/html")
        }));
        var reader = new KickWebsiteJsonReader(client, new MemoryLogger(), "Test", TimeSpan.FromSeconds(1));
        const string url = "https://kick.com/streamer";
        var result = await reader.ReadDirectAsync(url, url, CancellationToken.None, KickWebsitePayloadKind.Html);
        Assert.Equal(body.Length, result.Body?.Length ?? 0);
        Assert.True(body == result.Body);
        Assert.True((await reader.ReadDirectAsync(url, url, CancellationToken.None)).Body is null);
        body = new string('x', PayloadLimits.ProcessOutputBytes + 1);
        Assert.True((await reader.ReadDirectAsync(url, url, CancellationToken.None, KickWebsitePayloadKind.Html)).Body is null);
    }

    private static async Task ReplayValidationCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var resolutions = 0;
        var validator = new ReplayUrlSecurityValidator((_, _) =>
        {
            resolutions++;
            cancellation.Cancel();
            return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
        });
        var uri = new Uri("https://stream.kick.com/vod/index.m3u8");
        await Assert.ThrowsAsync<OperationCanceledException>(() => validator.ValidateAsync(uri, PlatformKind.Kick, cancellation.Token));
        Assert.Equal(1, resolutions);
        await Assert.ThrowsAsync<OperationCanceledException>(() => validator.ValidateAsync(uri, PlatformKind.Kick, cancellation.Token));
        Assert.Equal(1, resolutions);
    }
}
