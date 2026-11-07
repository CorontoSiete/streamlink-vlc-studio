internal static class DiscoveryLanguageTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("discovery language: both providers filter every page and omit the all-languages parameter", ProviderRequestsAsync),
        ("discovery language: desktop saves each category and restores choices after restarting", DesktopPersistenceAsync),
        ("discovery language: themed dropdown renders language names in its selection and menu", DropdownRenderingAsync)
    ];

    private static Task DropdownRenderingAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var combo = new ComboBox
        {
            Style = (Style)System.Windows.Application.Current.FindResource("StudioComboBoxStyle"),
            ItemsSource = BrowseLanguages.Options,
            DisplayMemberPath = nameof(BrowseLanguageOption.Name),
            SelectedValuePath = nameof(BrowseLanguageOption.Code),
            Width = 184
        };
        void Layout()
        {
            combo.Measure(new Size(184, 40));
            combo.Arrange(new Rect(0, 0, 184, 40));
            combo.UpdateLayout();
        }
        IEnumerable<TextBlock> Text(DependencyObject parent)
        {
            if (parent is TextBlock text) yield return text;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                foreach (var child in Text(VisualTreeHelper.GetChild(parent, index))) yield return child;
        }
        foreach (var code in new[] { "", "en", "pt" })
        {
            combo.SelectedValue = code;
            Layout();
            var name = BrowseLanguages.Options.Single(language => language.Code == code).Name;
            Assert.SequenceEqual(new[] { name }, Text(combo).Select(text => text.Text));
            if (Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(184, 40, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(combo);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"language-dropdown-{(code.Length == 0 ? "all" : code)}.png"));
                encoder.Save(output);
            }
            var item = new ComboBoxItem
            {
                Content = combo.SelectedItem,
                ContentTemplateSelector = combo.ItemTemplateSelector,
                Style = (Style)System.Windows.Application.Current.FindResource("StudioComboBoxItemStyle")
            };
            item.Measure(new Size(184, 40));
            item.Arrange(new Rect(0, 0, 184, 40));
            item.UpdateLayout();
            Assert.SequenceEqual(new[] { name }, Text(item).Select(text => text.Text));
        }
        return Task.CompletedTask;
    });

    private static Task DesktopPersistenceAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", "category-languages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonSettingsService(Path.Combine(directory, "settings.json"));
            var browse = new FakeBrowseService
            {
                CategoryResponder = request => new(BrowseResultStatus.Available,
                    [new(request.Platform, "1", "Just Chatting", "", [], 1), new(request.Platform, "2", "Music", "", [], 1)], "", "")
            };
            MainViewModel Create(AppSettings settings) => TestViewModels.CreateMain(settings, store,
                new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
                new MemoryLogger(), action => action(), browseService: browse);
            await using (var main = Create(new AppSettings()))
            {
                main.ShowBrowseHomePageCommand.Execute(null);
                await main.BrowseCategories[0].SelectCommand.ExecuteAsync();
                main.SelectedBrowseLanguage = "en";
                await main.RefreshBrowseCommand.ExecuteAsync();
                main.ReturnToBrowseCategoriesCommand.Execute(null);
                await main.BrowseCategories[1].SelectCommand.ExecuteAsync();
                Assert.Equal("", main.SelectedBrowseLanguage);
                main.SelectedBrowseLanguage = "fr";
                main.SelectedBrowseLanguage = "";
                main.ReturnToBrowseCategoriesCommand.Execute(null);
                main.SelectedBrowseLanguage = "de";
                await main.BrowseCategories[1].SelectCommand.ExecuteAsync();
                Assert.Equal("", main.SelectedBrowseLanguage);
                main.ReturnToBrowseCategoriesCommand.Execute(null);
                main.SelectKickBrowsePlatformCommand.Execute(null);
                await main.BrowseCategories[0].SelectCommand.ExecuteAsync();
                main.SelectedBrowseLanguage = "es";
                main.ReturnToBrowseCategoriesCommand.Execute(null);
                main.SelectTwitchBrowsePlatformCommand.Execute(null);
                await main.BrowseCategories[0].SelectCommand.ExecuteAsync();
                Assert.Equal("en", main.SelectedBrowseLanguage);
            }
            var restoredSettings = await store.LoadAsync();
            Assert.Equal("en", restoredSettings.BrowseCategoryLanguages["Twitch:1"]);
            Assert.Equal("", restoredSettings.BrowseCategoryLanguages["Twitch:2"]);
            Assert.Equal("es", restoredSettings.BrowseCategoryLanguages["Kick:1"]);
            await using var restored = Create(restoredSettings);
            restored.ShowBrowseHomePageCommand.Execute(null);
            await restored.BrowseCategories[0].SelectCommand.ExecuteAsync();
            Assert.Equal("en", restored.SelectedBrowseLanguage);
            Assert.Equal("en", browse.StreamRequests[^1].Language);
            restored.ReturnToBrowseCategoriesCommand.Execute(null);
            await restored.BrowseCategories[1].SelectCommand.ExecuteAsync();
            Assert.Equal("", restored.SelectedBrowseLanguage);
            restored.ReturnToBrowseCategoriesCommand.Execute(null);
            restored.SelectKickBrowsePlatformCommand.Execute(null);
            await restored.BrowseCategories[0].SelectCommand.ExecuteAsync();
            Assert.Equal("es", restored.SelectedBrowseLanguage);
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private static async Task ProviderRequestsAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            var requests = new List<Uri>();
            using var http = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                if (uri.Host == "id.twitch.tv")
                    return Json("""{"client_id":"language-client","login":"viewer","user_id":"1234","expires_in":3600,"scopes":[]}""");
                requests.Add(uri);
                return Json("""{"data":[],"pagination":{}}""");
            }));
            var settings = new AppSettings();
            settings.Chat.TwitchClientId = "language-client";
            settings.Chat.TwitchOAuthToken = "language-token";
            settings.Chat.KickOAuthToken = "language-token";
            var service = new BrowseService(new MemoryLogger(), http);
            foreach (var (language, cursor) in new[] { (" EN ", ""), ("en", "page-two"), ("fr", ""), ("", "") })
            {
                var result = await service.GetStreamsAsync(new(platform, "101", "Category", cursor, Language: language), settings);
                Assert.Equal(BrowseResultStatus.Available, result.Status);
                var query = requests[^1].Query;
                Assert.Contains(platform == PlatformKind.Twitch ? "game_id=101" : "category_id=101", query);
                if (language.Length == 0) Assert.DoesNotContain("language=", query);
                else Assert.Contains("language=" + language.Trim().ToLowerInvariant(), query);
                if (cursor.Length > 0) Assert.Contains("page-two", query);
                else Assert.DoesNotContain(platform == PlatformKind.Twitch ? "after=" : "cursor=", query);
            }
            Assert.Equal(4, requests.Count);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
