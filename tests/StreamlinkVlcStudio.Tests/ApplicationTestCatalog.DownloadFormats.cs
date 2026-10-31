using System.Windows.Automation;

internal static partial class ApplicationTestCatalog
{
    private static Task DownloadFormatSelectorAsync(AppTheme theme) => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new CardDownloadTestService();
        await using var model = CreateDownloadCardsMain(service, [], action => action());
        model.ShowDownloadsSettingsCommand.Execute(null);
        StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
        var window = new MainWindow { DataContext = model };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, model);
        try
        {
            LayoutStudioPolishWindow(window, new Size(1320, 820));
            var selector = (ComboBox)window.FindName("VodDownloadFormatComboBox");
            Assert.Equal("VOD download format", AutomationProperties.GetName(selector));
            Assert.Equal(2, selector.Items.Count);
            Assert.Equal(VodDownloadFormat.Hls, selector.SelectedValue);
            selector.SetCurrentValue(ComboBox.SelectedValueProperty, VodDownloadFormat.Mp4);
            Assert.Equal(VodDownloadFormat.Mp4, model.Settings.Downloads.Format);
            Assert.Equal(VodDownloadFormat.Mp4, service.DownloadFormat);
            foreach (var size in new[] { new Size(1320, 820), new Size(440, 820) })
            {
                LayoutStudioPolishWindow(window, size);
                Assert.Equal(Visibility.Visible, selector.Visibility);
                Assert.True(selector.ActualWidth > 100 && selector.ActualHeight > 20);
                Assert.Equal(VodDownloadFormat.Mp4, selector.SelectedValue);
                SaveResponsiveWindowImage(window, $"vod-format-settings-{theme}-{size.Width}");
            }
            model.Settings.Downloads = new DownloadSettings { Format = VodDownloadFormat.Hls };
            await TestWait.UntilAsync(() => Equals(VodDownloadFormat.Hls, selector.SelectedValue), TimeSpan.FromSeconds(2));
            Assert.Equal(VodDownloadFormat.Hls, service.DownloadFormat);
        }
        finally
        {
            window.Close();
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    });
}
