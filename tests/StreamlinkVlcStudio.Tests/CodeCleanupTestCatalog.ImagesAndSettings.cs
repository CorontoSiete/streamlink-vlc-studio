using StreamlinkVlcStudio.Core.Security;

internal static partial class CodeCleanupTestCatalog
{
    private static Task ImageUriLoadingAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        Assert.Equal(false, ImageUriPolicy.IsSupported(null));
        Assert.Equal(false, ImageUriPolicy.IsSupported(new Uri("avatar.png", UriKind.Relative)));

        var directory = Directory.CreateTempSubdirectory("StreamStudioImageTests-");
        var localPath = Path.Combine(directory.FullName, "image with spaces.png");
        var localUrl = new Uri(localPath).AbsoluteUri;
        var remoteUrl = "https://example.invalid/image-" + Guid.NewGuid().ToString("N") + ".png";
        var unsupportedUrls = new[]
        {
            "file://example.invalid/images/avatar.png",
            @"\\example.invalid\images\avatar.png",
            "file:////example.invalid/images/avatar.png",
            "file:///%5C%5Cexample.invalid/images/avatar.png",
            "file:///%2F%2Fexample.invalid/images/avatar.png",
            "file:///%5Cexample.invalid/images/avatar.png",
            "file:///%2Fexample.invalid/images/avatar.png",
            "file://localhost/images/avatar.png",
            "http://example.invalid/avatar.png",
            "ftp://example.invalid/avatar.png"
        };
        var requests = 0;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "profile-images", "nicewigg-transparent-150.png"));
        using var http = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        try
        {
            foreach (var url in unsupportedUrls)
            {
                Assert.Equal(false, ImageUriPolicy.IsSupported(new Uri(url)));
                Assert.Equal(false, await AnimatedEmoteImage.LoadImageForTestAsync(url, http).WaitAsync(TimeSpan.FromSeconds(2)));
            }

            await File.WriteAllBytesAsync(localPath, bytes);
            Assert.True(await AnimatedEmoteImage.LoadImageForTestAsync(localUrl, http));
            Assert.Equal(0, requests);
            Assert.True(await AnimatedEmoteImage.LoadImageForTestAsync(remoteUrl, http));
            Assert.Equal(1, requests);
        }
        finally
        {
            foreach (var url in unsupportedUrls.Append(localUrl).Append(remoteUrl))
                AnimatedEmoteImage.RemoveCachedImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes);
            directory.Delete(recursive: true);
        }
    });

    private static async Task LegacyUnusedSettingsAsync()
    {
        var directory = Directory.CreateTempSubdirectory("StreamStudioLegacySettingsTests-");
        var path = Path.Combine(directory.FullName, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path,
                """{"DefaultPlatform":"obsolete-value","DefaultQuality":"720p","Theme":"Nord"}""");
            var service = new JsonSettingsService(path);
            var settings = await service.LoadAsync();

            Assert.Equal("720p", settings.DefaultQuality);
            Assert.Equal(AppTheme.Nord, settings.Theme);
            Assert.Equal<string?>(null, service.LastLoadWarning);
            Assert.True(File.Exists(path));

            await service.SaveAsync(settings);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(false, saved.RootElement.TryGetProperty("DefaultPlatform", out _));
            Assert.Equal("720p", saved.RootElement.GetProperty("DefaultQuality").GetString());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
