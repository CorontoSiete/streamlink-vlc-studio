internal static partial class VodDownloadTestCatalog
{
    private static async Task ErrorDocumentMediaAsync(string mediaType)
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            await using var fixture = new DownloadFixture(platform);
            fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.RequestUri!.AbsolutePath.EndsWith("segment-a.ts", StringComparison.Ordinal)
                    ? DownloadHttpHandler.Reply(request, Encoding.UTF8.GetBytes("temporary server error"), mediaType)
                    : null);

            var queued = await fixture.EnqueueAsync();
            var failed = await fixture.WaitAsync(queued.Id, VodDownloadState.Failed);
            Assert.Equal("", failed.LocalMediaPath);
            Assert.True(!Directory.Exists(Path.Combine(fixture.Library, queued.Id.ToString("N"), ".partial")));
            Assert.True(!Directory.Exists(Path.Combine(fixture.Library, queued.Id.ToString("N"), "media")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GetOfflineTargetAsync(queued.Id));
        }
    }

    private static async Task ErrorDocumentProfileImagesAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            foreach (var mediaType in new[] { "TeXt/HtMl", "Application/JSON", "Application/Problem+JSON", "Application/XHTML+XML" })
            {
                await using var fixture = new DownloadFixture(platform);
                var target = fixture.Target with { ProfileImageUrl = ProfileImageUrl(platform) };
                fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
                    request.RequestUri!.AbsoluteUri == target.ProfileImageUrl
                        ? DownloadHttpHandler.Reply(request, Encoding.UTF8.GetBytes("temporary server error"), mediaType)
                        : null);

                var completed = await fixture.WaitAsync((await fixture.EnqueueAsync(target)).Id, VodDownloadState.Completed);
                var offline = await fixture.Service.GetOfflineTargetAsync(completed.Id);
                Assert.Equal(target.ProfileImageUrl, offline.ProfileImageUrl);
                Assert.True(!File.Exists(Path.Combine(Path.GetDirectoryName(completed.LocalMediaPath)!, OfflineVodProfileImage.FileName)));
                Assert.Equal(20L, completed.BytesDownloaded);
            }
        }
    }
}
