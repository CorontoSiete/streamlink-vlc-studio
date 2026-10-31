using System.Net.Http.Json;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task VerifyCategoryViewersAsync(LocalWebDependencies dependencies)
    {
        var browse = new FakeBrowseService
        {
            CategoryResponder = _ => new(BrowseResultStatus.Available,
                [new(PlatformKind.Twitch, "1", "Category", "", [])], "next", ""),
            CategoryViewerCountResponder = request => Task.FromResult(new BrowseResult<BrowseCategoryViewerCount>(
                BrowseResultStatus.Available, request.CategoryIds.Select(id => new BrowseCategoryViewerCount(id, id == "1" ? 329518 : 0)).ToArray(), "", ""))
        };
        var services = dependencies with { Browse = browse };
        await using var workspace = new LocalWebWorkspace(services);
        var categories = await Call(workspace, "categories", new { platform = "twitch", query = "Category", cursor = "previous" });
        Assert.Equal(JsonValueKind.Null, categories.GetProperty("items")[0].GetProperty("viewerCount").ValueKind);
        Assert.Equal("next", categories.GetProperty("nextCursor").GetString());
        Assert.Equal(0, browse.CategoryViewerCountRequests.Count);
        var counts = await Call(workspace, "categories/viewers", new { platform = "twitch", categoryIds = new[] { " 1 ", "2", "1" } });
        Assert.True(counts.GetProperty("available").GetBoolean());
        Assert.Equal(329518, counts.GetProperty("items")[0].GetProperty("viewerCount").GetInt32());
        Assert.Equal(0, counts.GetProperty("items")[1].GetProperty("viewerCount").GetInt32());
        Assert.SequenceEqual(new[] { "1", "2" }, browse.CategoryViewerCountRequests.Single().CategoryIds);
        Assert.DoesNotContain("secret-", counts.ToString());
        browse.CategoryViewerCountResponder = _ => Task.FromResult(new BrowseResult<BrowseCategoryViewerCount>(
            BrowseResultStatus.Unavailable, [new("1", 10)], "", "Provider failed"));
        var failed = await Call(workspace, "categories/viewers", new { categoryIds = new[] { "1" } });
        Assert.Equal(false, failed.GetProperty("available").GetBoolean());
        Assert.Equal(0, failed.GetProperty("items").GetArrayLength());

        foreach (var invalid in new object[]
        {
            new { }, new { platform = "kick", categoryIds = new[] { "1" } }, new { categoryIds = "1" },
            new { categoryIds = Array.Empty<string>() }, new { categoryIds = Enumerable.Repeat("1", 21).ToArray() },
            new { categoryIds = new object?[] { null } }, new { categoryIds = new object[] { 1 } },
            new { categoryIds = new[] { " " } }, new { categoryIds = new[] { "1\n2" } }, new { categoryIds = new[] { new string('1', 129) } }
        }) await Assert.ThrowsAsync<ArgumentException>(() => Call(workspace, "categories/viewers", invalid));
        Assert.Equal(2, browse.CategoryViewerCountRequests.Count);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var cancelled = 0;
        browse.CategoryViewerCountResponderWithCancellation = async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            if (current == 4) started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Decrement(ref active); Interlocked.Increment(ref cancelled); }
            throw new InvalidOperationException("A cancelled lookup cannot return a total.");
        };
        using var cancellation = new CancellationTokenSource();
        var lookups = Enumerable.Range(1, 6).Select(id => workspace.InvokeAsync("categories/viewers",
            JsonSerializer.SerializeToElement(new { categoryIds = new[] { id.ToString(CultureInfo.InvariantCulture) } }), cancellation.Token)).ToArray();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(4, active);
            await Call(workspace, "settings", new { }).WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            foreach (var lookup in lookups) await Assert.ThrowsAsync<OperationCanceledException>(async () => await lookup);
            Assert.Equal(4, maximum);
            Assert.Equal(4, cancelled);
            Assert.Equal(0, active);
        }
        finally { cancellation.Cancel(); }

        // Exercise the real HTTPS command gate and security boundary as well as the workspace.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var networkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browse.CategoryViewerCountResponderWithCancellation = async (_, token) =>
        {
            networkStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(BrowseResultStatus.Available, [new("1", 329518)], "", "");
        };
        using var certificate = Certificate();
        await using var server = await Server(certificate, () => new LocalWebWorkspace(services));
        using var http = Client(certificate, server.Address);
        using (var denied = await Post(http, "categories/viewers", new { categoryIds = new[] { "1" } }))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var auth = await Login(http);
        using (var denied = await Post(http, "categories/viewers", new { categoryIds = new[] { "1" } }, auth.Cookie))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await Post(http, "categories/viewers", new { categoryIds = new[] { "1" } }, auth.Cookie, auth.Csrf, "https://attacker.example"))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var networkLookup = Post(http, "categories/viewers", new { categoryIds = new[] { "1" } }, auth.Cookie, auth.Csrf);
        try
        {
            await networkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var settings = await Post(http, "settings", new { }, auth.Cookie, auth.Csrf).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        }
        finally { release.TrySetResult(); }
        using var completed = await networkLookup;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(329518, (await completed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("viewerCount").GetInt32());
    }
}
