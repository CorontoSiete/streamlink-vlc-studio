
internal static partial class VodDownloadTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> ProfileImageTests =>
    [
        ("VOD download profile images: offline targets retain the broadcaster's saved avatar", OfflineTargetProfileImageAsync),
        ("VOD download profile images: Twitch avatars are cached and restored without internet", () => CachedProfileImageAsync(PlatformKind.Twitch)),
        ("VOD download profile images: Kick avatars are cached and restored without internet", () => CachedProfileImageAsync(PlatformKind.Kick)),
        ("VOD download profile images: existing Twitch downloads retain their avatar without redownloading video", () => LegacyProfileImageAsync(PlatformKind.Twitch)),
        ("VOD download profile images: existing Kick downloads retain their avatar without redownloading video", () => LegacyProfileImageAsync(PlatformKind.Kick)),
        ("VOD download profile images: unavailable avatars do not fail completed video", UnavailableProfileImageAsync),
        ("VOD download profile images: unsafe avatar URLs are rejected before fetching", UnsafeProfileImageAsync),
        ("VOD download profile images: redirects stay on approved public provider endpoints", ProfileImageRedirectsAsync),
        ("VOD download profile images: canceling avatar work cancels the download and retry restores it", CancelProfileImageAsync),
        ("VOD download profile images: missing optional avatar files retain saved metadata and playable video", MissingProfileImageAsync),
        ("VOD download profile images: refreshed Kick metadata supplies the cached avatar", ResolvedProfileImageAsync)
    ];

    private static Task OfflineTargetProfileImageAsync()
    {
        var target = VodDownloadUrlParser.Parse("https://www.twitch.tv/videos/12345") with
        {
            Channel = "streamer",
            DisplayTitle = "Recorded broadcast",
            ProfileImageUrl = ProfileImageUrl(PlatformKind.Twitch)
        };
        var item = new VodDownloadItem(Guid.NewGuid(), target, "best", DateTimeOffset.UtcNow,
            VodDownloadState.Completed, Duration: TimeSpan.FromMinutes(10),
            LocalMediaPath: Path.Combine(Path.GetTempPath(), "offline.m3u8"));
        Assert.True(item.OfflineTarget.IsOfflineVod);
        Assert.Equal(target.ProfileImageUrl, item.OfflineTarget.ProfileImageUrl);
        Assert.Equal(target.Channel, item.OfflineTarget.Channel);
        Assert.Equal(target.DisplayTitle, item.OfflineTarget.DisplayTitle);
        Assert.Equal(item.Duration, item.OfflineTarget.MediaDuration);
        return Task.CompletedTask;
    }

    private static async Task CachedProfileImageAsync(PlatformKind platform)
    {
        await using var fixture = new DownloadFixture(platform);
        var target = fixture.Target with { ProfileImageUrl = ProfileImageUrl(platform) };
        var bytes = CreateProfileImageBytes();
        fixture.Handler.Put(new Uri(target.ProfileImageUrl).AbsolutePath, bytes);
        var downloaded = await fixture.WaitAsync((await fixture.EnqueueAsync(target)).Id, VodDownloadState.Completed);
        Assert.Equal(target.ProfileImageUrl, downloaded.Target.ProfileImageUrl);
        Assert.Equal(20L, downloaded.BytesDownloaded);
        var offline = await fixture.Service.GetOfflineTargetAsync(downloaded.Id);
        var image = new Uri(offline.ProfileImageUrl);
        Assert.True(image.IsFile);
        Assert.True(File.Exists(image.LocalPath));
        var cachedBytes = await File.ReadAllBytesAsync(image.LocalPath);
        Assert.True(bytes.SequenceEqual(cachedBytes));
        var stored = JsonSerializer.Deserialize<VodDownloadItem>(await File.ReadAllTextAsync(fixture.Record(downloaded.Id)))!;
        Assert.Equal(target.ProfileImageUrl, stored.Target.ProfileImageUrl);
        fixture.Handler.NetworkAvailable = false;
        var requests = fixture.Handler.RequestCount;
        await fixture.RestartAsync();
        var restored = await fixture.Service.GetOfflineTargetAsync(downloaded.Id);
        Assert.Equal(offline.ProfileImageUrl, restored.ProfileImageUrl);
        Assert.Equal(requests, fixture.Handler.RequestCount);
        Assert.Equal(1, fixture.Resolver.ResolveStreamUrlCount);
    }

    private static async Task LegacyProfileImageAsync(PlatformKind platform)
    {
        await using var fixture = new DownloadFixture(platform);
        var downloaded = await fixture.DownloadAsync();
        var target = downloaded.Target with { ProfileImageUrl = ProfileImageUrl(platform) };
        await File.WriteAllTextAsync(fixture.Record(downloaded.Id), JsonSerializer.Serialize(downloaded with { Target = target }));
        fixture.Handler.NetworkAvailable = false;
        var requests = fixture.Handler.RequestCount;
        await fixture.RestartAsync();
        var offline = await fixture.Service.GetOfflineTargetAsync(downloaded.Id);
        Assert.Equal(target.ProfileImageUrl, offline.ProfileImageUrl);
        Assert.True(offline.IsOfflineVod);
        Assert.Equal(requests, fixture.Handler.RequestCount);
        Assert.Equal(1, fixture.Resolver.ResolveStreamUrlCount);
    }

    private static async Task UnavailableProfileImageAsync()
    {
        foreach (var failure in new[] { "oversized", "missing", "empty", "html", "malformed" })
        {
            await using var fixture = new DownloadFixture(PlatformKind.Twitch);
            var target = fixture.Target with
            {
                ProfileImageUrl = failure == "malformed" ? "not a URI" : ProfileImageUrl(PlatformKind.Twitch)
            };
            fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.RequestUri!.AbsolutePath.EndsWith("avatar.png", StringComparison.Ordinal)
                    ? new HttpResponseMessage(failure == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                    {
                        RequestMessage = request,
                        Content = failure == "html"
                            ? new StringContent("<html>Unavailable</html>", Encoding.UTF8, "text/html")
                            : new ByteArrayContent([])
                            {
                                Headers = { ContentLength = failure == "oversized" ? 8 * 1024 * 1024 + 1 : 0 }
                            }
                    }
                    : null);
            var downloaded = await fixture.WaitAsync((await fixture.EnqueueAsync(target)).Id, VodDownloadState.Completed);
            var offline = await fixture.Service.GetOfflineTargetAsync(downloaded.Id);
            Assert.Equal(failure == "malformed" ? "" : target.ProfileImageUrl, offline.ProfileImageUrl);
            Assert.True(offline.IsOfflineVod);
            Assert.Equal(failure != "malformed", fixture.Handler.Addresses.Contains(target.ProfileImageUrl));
        }
    }

    private static async Task UnsafeProfileImageAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var target = fixture.Target with { ProfileImageUrl = "https://static-cdn.jtvnw.net.evil.example/avatar.png" };
        var downloaded = await fixture.WaitAsync((await fixture.EnqueueAsync(target)).Id, VodDownloadState.Completed);
        Assert.Equal(false, fixture.Handler.Addresses.Contains(target.ProfileImageUrl));
        var offline = await fixture.Service.GetOfflineTargetAsync(downloaded.Id);
        Assert.True(offline.IsOfflineVod);
        Assert.Equal("", offline.ProfileImageUrl);
    }

    private static async Task ProfileImageRedirectsAsync()
    {
        foreach (var safe in new[] { true, false })
        {
            await using var fixture = new DownloadFixture(PlatformKind.Twitch);
            var target = fixture.Target with { ProfileImageUrl = ProfileImageUrl(PlatformKind.Twitch) };
            var redirected = new Uri(safe ? "https://static-cdn.jtvnw.net/redirected-avatar.png" : "https://evil.example/redirected-avatar.png");
            fixture.Handler.Put(redirected.AbsolutePath, CreateProfileImageBytes());
            fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.RequestUri!.AbsoluteUri == target.ProfileImageUrl
                    ? new HttpResponseMessage(HttpStatusCode.Redirect)
                    {
                        RequestMessage = request,
                        Headers = { Location = redirected }
                    }
                    : null);
            var downloaded = await fixture.WaitAsync((await fixture.EnqueueAsync(target)).Id, VodDownloadState.Completed);
            var offline = await fixture.Service.GetOfflineTargetAsync(downloaded.Id);
            Assert.Equal(safe, new Uri(offline.ProfileImageUrl).IsFile);
            Assert.Equal(safe, fixture.Handler.Addresses.Contains(redirected.AbsoluteUri));
        }
    }

    private static async Task CancelProfileImageAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var target = fixture.Target with { ProfileImageUrl = ProfileImageUrl(PlatformKind.Twitch) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Override = async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri == target.ProfileImageUrl)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return null;
        };
        var queued = await fixture.EnqueueAsync(target);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Service.CancelAsync(queued.Id);
        await fixture.WaitAsync(queued.Id, VodDownloadState.Canceled);
        var directory = Path.GetDirectoryName(fixture.Record(queued.Id))!;
        Assert.Equal(false, Directory.Exists(Path.Combine(directory, "media")));
        Assert.Equal(false, Directory.Exists(Path.Combine(directory, ".partial")));
        fixture.Handler.Override = null;
        fixture.Handler.Put(new Uri(target.ProfileImageUrl).AbsolutePath, CreateProfileImageBytes());
        await fixture.Service.RetryAsync(queued.Id, fixture.Options);
        await fixture.WaitAsync(queued.Id, VodDownloadState.Completed);
        Assert.True(new Uri((await fixture.Service.GetOfflineTargetAsync(queued.Id)).ProfileImageUrl).IsFile);
    }

    private static async Task MissingProfileImageAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var target = fixture.Target with { ProfileImageUrl = ProfileImageUrl(PlatformKind.Twitch) };
        fixture.Handler.Put(new Uri(target.ProfileImageUrl).AbsolutePath, CreateProfileImageBytes());
        var downloaded = await fixture.WaitAsync((await fixture.EnqueueAsync(target)).Id, VodDownloadState.Completed);
        var cached = new Uri((await fixture.Service.GetOfflineTargetAsync(downloaded.Id)).ProfileImageUrl);
        Assert.True(cached.IsFile);
        File.Delete(cached.LocalPath);
        fixture.Handler.NetworkAvailable = false;
        await fixture.RestartAsync();
        var offline = await fixture.Service.GetOfflineTargetAsync(downloaded.Id);
        Assert.Equal(target.ProfileImageUrl, offline.ProfileImageUrl);
        Assert.True(File.Exists(offline.LocalMediaPath));
        Assert.Equal(VodDownloadState.Completed, (await fixture.Service.GetDownloadsAsync()).Single().State);
    }

    private static async Task ResolvedProfileImageAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var url = ProfileImageUrl(PlatformKind.Kick);
        fixture.Handler.Put(new Uri(url).AbsolutePath, CreateProfileImageBytes());
        fixture.KickResolver = (target, _) => Task.FromResult(target with
        {
            Url = fixture.PlaylistUri.AbsoluteUri,
            ProfileImageUrl = url
        });
        await fixture.RestartAsync();
        var downloaded = await fixture.DownloadAsync();
        Assert.Equal(url, downloaded.Target.ProfileImageUrl);
        Assert.True(new Uri((await fixture.Service.GetOfflineTargetAsync(downloaded.Id)).ProfileImageUrl).IsFile);
    }

    internal static string ProfileImageUrl(PlatformKind platform) => platform == PlatformKind.Twitch
        ? "https://static-cdn.jtvnw.net/jtv_user_pictures/avatar.png"
        : "https://files.kick.com/user/avatar.png";

    internal static byte[] CreateProfileImageBytes()
    {
        var pixels = Enumerable.Range(0, 32 * 32).SelectMany(_ => new byte[] { 96, 160, 240, 255 }).ToArray();
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
