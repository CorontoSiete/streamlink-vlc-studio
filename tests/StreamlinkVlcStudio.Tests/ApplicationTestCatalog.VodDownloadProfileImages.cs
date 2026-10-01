using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    private static Task DownloadProfileImageUiAsync(PlatformKind platform) => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new VodDownloadTestCatalog.DownloadFixture(platform);
        var target = fixture.Target with { ProfileImageUrl = VodDownloadTestCatalog.ProfileImageUrl(platform) };
        fixture.Handler.Put(new Uri(target.ProfileImageUrl).AbsolutePath, VodDownloadTestCatalog.CreateProfileImageBytes());
        var dispatcher = Dispatcher.CurrentDispatcher;
        Guid downloadId;
        await using (var original = CreateDownloadCardsMain(fixture.Service, [target], action => dispatcher.BeginInvoke(action)))
        {
            original.Initialize();
            if (platform == PlatformKind.Kick) original.SelectKickVodPlatformCommand.Execute(null);
            await original.SearchTwitchVodsCommand.ExecuteAsync();
            var card = original.TwitchVods.Single();
            Assert.Equal(target.ProfileImageUrl, card.Target.ProfileImageUrl);
            await card.DownloadCommand.ExecuteAsync();
            await TestWait.UntilAsync(() => card.Download?.CanPlay == true, TimeSpan.FromSeconds(3));
            downloadId = card.Download!.Id;
            Assert.Equal(target.ProfileImageUrl, card.Download.Item.Target.ProfileImageUrl);
        }

        fixture.Handler.NetworkAvailable = false;
        var requests = fixture.Handler.RequestCount;
        await fixture.RestartAsync();
        await using var model = CreateDownloadCardsMain(fixture.Service, [], action => dispatcher.BeginInvoke(action));
        model.Initialize();
        await TestWait.UntilAsync(() => model.VodDownloads.Count == 1, TimeSpan.FromSeconds(3));
        var window = new MainWindow { DataContext = model };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, model);
        try
        {
            Assert.Equal(downloadId, model.VodDownloads.Single().Id);
            await model.VodDownloads.Single().PlayCommand.ExecuteAsync();
            var tab = model.SelectedTab!;
            Assert.True(tab.Target.IsOfflineVod);
            Assert.True(tab.HasProfileImage);
            Assert.True(new Uri(tab.ProfileImageUrl).IsFile);
            Assert.Equal(tab.ProfileImageUrl, model.TabStripItems.Single().ProfileImageUrl);
            var strip = (ListBox)window.FindName("TabListBox");
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                var avatar = FindVisualDescendants<AnimatedEmoteImage>(strip).Single(image => image.ImageUrl == tab.ProfileImageUrl);
                await TestWait.UntilAsync(() => avatar.Source is not null, TimeSpan.FromSeconds(3));
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                Assert.Equal(Visibility.Visible, avatar.Visibility);
                Assert.True(avatar.ActualWidth > 0 && avatar.ActualHeight > 0);
                var rendered = WpfVisualTest.Render(avatar);
                var pixel = new byte[4];
                rendered.CopyPixels(new Int32Rect(rendered.PixelWidth / 2, rendered.PixelHeight / 2, 1, 1), pixel, 4, 0);
                Assert.True(pixel.SequenceEqual(new byte[] { 96, 160, 240, 255 }), "The tab must paint the downloaded avatar, not the default profile glyph.");
                SaveDownloadCardImage(strip, $"offline-vod-profile-{platform}-{theme}");
            }
            Assert.Equal(requests, fixture.Handler.RequestCount);
            Assert.Equal(1, fixture.Resolver.ResolveStreamUrlCount);
        }
        finally
        {
            window.Close();
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });
}
