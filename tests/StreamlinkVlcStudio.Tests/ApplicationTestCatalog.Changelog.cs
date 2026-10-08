using System.Windows.Threading;
using System.Xml.Linq;
using StreamlinkVlcStudio.App.Wpf.Themes;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ChangelogTests { get; } =
    [
        ("changelog: bundled installed and historical notes match the release publication source", BundledChangelogMatchesSourceAsync),
        ("changelog: release builds reject missing empty and whitespace notes and accept authored changes", ChangelogBuildGuardAsync),
        ("changelog: upgrades and skipped versions open once while restarts and downgrades stay quiet", ChangelogVersionLifecycleAsync),
        ("changelog: fresh setup establishes a baseline and existing users see the first changelog", ChangelogFirstRunAsync),
        ("changelog: failed canceled reboot and stale update results select the correct installed version", ChangelogCompletionOutcomesAsync),
        ("changelog: pending repairs survive consumed completion records and clear after success", ChangelogPendingRepairAsync),
        ("changelog: missing current notes never substitute an earlier or future version", ChangelogMissingNotesAsync),
        ("changelog: startup waits for the installation result even with update checks disabled", ChangelogWaitsForCompletionAsync),
        ("changelog: unreadable update results still allow offline version detection", ChangelogCompletionReadFailureAsync),
        ("changelog: closing before presentation keeps the update pending for the next launch", ChangelogUnshownVersionAsync),
        ("changelog: formatted notes preserve headings lists paths code and safe links", ChangelogMarkdownRenderingAsync),
        ("changelog: history controls respect release boundaries and return to the installed version", ChangelogHistoryNavigationAsync),
        ("changelog: update instructions stay separate without losing notes or splitting code samples", ChangelogUpdatingInstructionsAsync),
        ("changelog: page fits wide and compact layouts and browses history in every theme", ChangelogResponsivePageAsync),
        ("changelog: actual page presentation saves the version and suppresses the next startup", ChangelogPresentationPersistsAsync),
        ("changelog: manual access returns to the same studio destination", ChangelogManualNavigationAsync)
    ];

    private static Task BundledChangelogMatchesSourceAsync()
    {
        var catalog = ReleaseNotesCatalog.LoadEmbedded();
        Assert.Equal(ReleaseNotesCatalog.Normalize(typeof(MainWindow).Assembly.GetName().Version!), catalog.InstalledVersion);
        Assert.NotNull(catalog.InstalledRelease);
        var notesDirectory = Path.Combine(FindRepoRoot(), "docs", "releases");
        var sources = Directory.EnumerateFiles(notesDirectory, "v*.md")
            .Select(path => (Path: path, Version: Version.Parse(Path.GetFileNameWithoutExtension(path)[1..])))
            .Where(source => source.Version <= catalog.InstalledVersion)
            .OrderByDescending(source => source.Version).ToArray();
        Assert.SequenceEqual(sources.Select(source => source.Version), catalog.Releases.Select(release => release.Version));
        foreach (var source in sources)
            Assert.Equal(File.ReadAllText(source.Path), catalog.Releases.Single(release => release.Version == source.Version).Markdown);
        return Task.CompletedTask;
    }

    private static Task ChangelogVersionLifecycleAsync()
    {
        var settings = ChangelogSettings("1.7.2");
        var model = new ChangelogViewModel(ChangelogCatalog(), settings);
        Assert.True(model.ShouldOpenAfterStartup(null));
        Assert.Equal("1.7.2", settings.Updates.LastSeenChangelogVersion);
        Assert.Equal(new Version(1, 8, 5), model.SelectedRelease!.Version);
        Assert.SequenceEqual(new[] { new Version(1, 8, 5), new Version(1, 8, 3), new Version(1, 7, 2) },
            model.Releases.Select(release => release.Version));
        model.SelectedRelease = model.Releases[^1];
        model.MarkPresented();
        Assert.Equal("1.7.2", settings.Updates.LastSeenChangelogVersion);
        model.SelectInstalledRelease();
        model.MarkPresented();
        Assert.Equal("1.8.5", settings.Updates.LastSeenChangelogVersion);
        Assert.Equal(false, new ChangelogViewModel(ChangelogCatalog(), settings).ShouldOpenAfterStartup(null));
        Assert.Equal(false, new ChangelogViewModel(ChangelogCatalog(new Version(1, 8, 3)), settings).ShouldOpenAfterStartup(null));
        settings.Updates.LastSeenChangelogVersion = "1.8.5.0";
        Assert.Equal(false, model.ShouldOpenAfterStartup(null));
        settings.Updates.LastSeenChangelogVersion = "unreadable";
        Assert.True(model.ShouldOpenAfterStartup(null));
        return Task.CompletedTask;
    }

    private static async Task ChangelogBuildGuardAsync()
    {
        var source = XDocument.Load(Path.Combine(FindRepoRoot(), "src", "StreamlinkVlcStudio.App.Wpf", "StreamlinkVlcStudio.App.Wpf.csproj"));
        var target = source.Root!.Elements("Target").Single(element => (string?)element.Attribute("Name") == "ValidateBundledReleaseNotes");
        Assert.Equal("PrepareForBuild", (string?)target.Attribute("BeforeTargets"));
        var root = CreateTempTestDirectory();
        try
        {
            var projectDirectory = Path.Combine(root, "src", "App");
            Directory.CreateDirectory(projectDirectory);
            var projectPath = Path.Combine(projectDirectory, "Guard.proj");
            new XDocument(new XElement("Project",
                new XElement("PropertyGroup", new XElement("VersionPrefix", "9.9.9")), new XElement(target))).Save(projectPath);
            var notesPath = Path.Combine(root, "docs", "releases", "v9.9.9.md");
            Directory.CreateDirectory(Path.GetDirectoryName(notesPath)!);
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            foreach (var (contents, error) in new (string? Contents, string? Error)[]
                     {
                         (null, "Add docs/releases/v9.9.9.md"),
                         ("", "must not be empty"),
                         (" \n\t \n", "must not be empty"),
                         ("# Stream Studio 9.9.9\n\n- It's ready: show **Changelog** after an update.\n", null)
                     })
            {
                if (contents is not null) await File.WriteAllTextAsync(notesPath, contents);
                var info = BoundedProcessRunner.CreateRedirectedStartInfo(dotnet,
                    ["msbuild", projectPath, "-target:ValidateBundledReleaseNotes", "-verbosity:minimal", "-nologo"]);
                info.WorkingDirectory = FindRepoRoot();
                var result = await new BoundedProcessRunner().RunAsync(info, TimeSpan.FromSeconds(10));
                Assert.Equal(false, result.TimedOut);
                if (error is null) Assert.Equal(0, result.ExitCode);
                else
                {
                    Assert.True(result.ExitCode != 0, "A release without usable changelog content must fail.");
                    Assert.Contains(error, result.StandardOutput + result.StandardError);
                }
            }
        }
        finally { DeleteTempTestDirectory(root); }
    }

    private static Task ChangelogFirstRunAsync()
    {
        var fresh = new AppSettings();
        var model = new ChangelogViewModel(ChangelogCatalog(), fresh);
        // The main window finishes the setup dialog before starting background services.
        fresh.SetupCompleted = true;
        Assert.Equal(false, model.ShouldOpenAfterStartup(null));
        Assert.Equal("1.8.5", fresh.Updates.LastSeenChangelogVersion);
        Assert.Equal(false, new ChangelogViewModel(ChangelogCatalog(), fresh).ShouldOpenAfterStartup(null));
        Assert.True(new ChangelogViewModel(ChangelogCatalog(new Version(1, 8, 6)), fresh).ShouldOpenAfterStartup(null));
        Assert.True(new ChangelogViewModel(ChangelogCatalog(), ChangelogSettings("")).ShouldOpenAfterStartup(null));
        var unfinishedSetup = new AppSettings();
        Assert.True(new ChangelogViewModel(ChangelogCatalog(), unfinishedSetup)
            .ShouldOpenAfterStartup(ChangelogCompletion(AppUpdateCompletionOutcome.Succeeded)));
        return Task.CompletedTask;
    }

    private static Task ChangelogCompletionOutcomesAsync()
    {
        foreach (var outcome in Enum.GetValues<AppUpdateCompletionOutcome>())
        {
            var settings = ChangelogSettings("1.8.3");
            var model = new ChangelogViewModel(ChangelogCatalog(), settings);
            Assert.Equal(outcome is AppUpdateCompletionOutcome.Succeeded or AppUpdateCompletionOutcome.SucceededRebootRequired,
                model.ShouldOpenAfterStartup(ChangelogCompletion(outcome)));
            Assert.Equal(outcome is AppUpdateCompletionOutcome.Succeeded or AppUpdateCompletionOutcome.SucceededRebootRequired,
                model.ShouldOpenAfterStartup(ChangelogCompletion(outcome), new Version(1, 8, 5)));
            Assert.Equal("1.8.3", settings.Updates.LastSeenChangelogVersion);
        }
        var current = new ChangelogViewModel(ChangelogCatalog(), ChangelogSettings("1.7.2"));
        Assert.True(current.ShouldOpenAfterStartup(ChangelogCompletion(AppUpdateCompletionOutcome.Failed, new Version(1, 8, 3))));
        Assert.Equal(false, current.ShouldOpenAfterStartup(ChangelogCompletion(AppUpdateCompletionOutcome.Succeeded, new Version(1, 8, 6))));
        Assert.Equal(false, current.ShouldOpenAfterStartup(ChangelogCompletion(AppUpdateCompletionOutcome.Canceled) with { TargetVersion = null }));
        return Task.CompletedTask;
    }

    private static async Task ChangelogPendingRepairAsync()
    {
        var root = CreateTempTestDirectory();
        var updateRoot = Path.Combine(root, "updates");
        var resultDirectory = Path.Combine(updateRoot, "results");
        Directory.CreateDirectory(resultDirectory);
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("Reading startup results must not use the network.")));
        try
        {
            var failed = ChangelogCompletion(AppUpdateCompletionOutcome.Failed);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await File.WriteAllTextAsync(Path.Combine(resultDirectory, failed.OperationId.ToString("N") + ".json"),
                JsonSerializer.Serialize(failed, options));
            using (var first = new StagedAppUpdateService(new MemoryLogger(), client, root, updateRoot,
                       getCurrentVersion: () => new Version(1, 8, 5)))
            {
                Assert.Equal(AppUpdateCompletionOutcome.Failed, (await first.ConsumeCompletionAsync())!.Outcome);
                Assert.Equal(new Version(1, 8, 5), first.PendingRepairVersion);
            }
            using (var restarted = new StagedAppUpdateService(new MemoryLogger(), client, root, updateRoot,
                       getCurrentVersion: () => new Version(1, 8, 5)))
            {
                Assert.Equal<AppUpdateCompletion?>(null, await restarted.ConsumeCompletionAsync());
                Assert.Equal(new Version(1, 8, 5), restarted.PendingRepairVersion);
                Assert.Equal(false, new ChangelogViewModel(ChangelogCatalog(), ChangelogSettings("1.8.3"))
                    .ShouldOpenAfterStartup(null, restarted.PendingRepairVersion));
            }
            var succeeded = failed with { Outcome = AppUpdateCompletionOutcome.Succeeded, Message = "Update installed." };
            await File.WriteAllTextAsync(Path.Combine(resultDirectory, succeeded.OperationId.ToString("N") + ".json"),
                JsonSerializer.Serialize(succeeded, options));
            using var repaired = new StagedAppUpdateService(new MemoryLogger(), client, root, updateRoot,
                getCurrentVersion: () => new Version(1, 8, 5));
            var completion = await repaired.ConsumeCompletionAsync();
            Assert.Equal(AppUpdateCompletionOutcome.Succeeded, completion!.Outcome);
            Assert.Equal<Version?>(null, repaired.PendingRepairVersion);
            Assert.True(new ChangelogViewModel(ChangelogCatalog(), ChangelogSettings("1.8.3"))
                .ShouldOpenAfterStartup(completion, repaired.PendingRepairVersion));
        }
        finally { DeleteTempTestDirectory(root); }
    }

    private static Task ChangelogMissingNotesAsync()
    {
        var settings = ChangelogSettings("1.8.3");
        var catalog = new ReleaseNotesCatalog(new Version(1, 8, 5),
            [new ReleaseNotes(new Version(1, 8, 3), "Earlier notes."), new ReleaseNotes(new Version(2, 0, 0), "Future notes.")]);
        var model = new ChangelogViewModel(catalog, settings);
        Assert.Equal(false, model.HasSelectedRelease);
        Assert.Equal(false, model.ShouldOpenAfterStartup(null));
        Assert.Equal(1, model.Releases.Count);
        model.SelectedRelease = model.Releases[0];
        model.MarkPresented();
        Assert.Equal("1.8.3", settings.Updates.LastSeenChangelogVersion);
        return Task.CompletedTask;
    }

    private static Task ChangelogWaitsForCompletionAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = ChangelogSettings("1.8.3");
        settings.Updates.AutomaticChecksEnabled = false;
        var completion = new TaskCompletionSource<AppUpdateCompletion?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = new ChangelogCompletionService { Completion = completion.Task };
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var main = CreateChangelogMain(settings, updater: updater,
            catalog: ChangelogCatalog(), dispatch: action => dispatcher.BeginInvoke(action));
        main.Initialize();
        main.Initialize();
        Assert.Equal(1, updater.ConsumeCount);
        Assert.Equal(false, main.IsSettingsOpen);
        completion.SetResult(ChangelogCompletion(AppUpdateCompletionOutcome.Succeeded));
        await TestWait.UntilAsync(() => main.IsSettingsOpen, TimeSpan.FromSeconds(3));
        Assert.True(main.IsChangelogSelected);
        Assert.Equal("1.8.3", settings.Updates.LastSeenChangelogVersion);
        Assert.Equal(0, updater.CheckCount);
    });

    private static Task ChangelogCompletionReadFailureAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = ChangelogSettings("1.8.3");
        settings.Updates.AutomaticChecksEnabled = false;
        var updater = new ChangelogCompletionService
        {
            Completion = Task.FromException<AppUpdateCompletion?>(new IOException("Result unavailable."))
        };
        await using var main = CreateChangelogMain(settings, updater: updater, catalog: ChangelogCatalog());
        main.Initialize();
        await TestWait.UntilAsync(() => main.IsSettingsOpen, TimeSpan.FromSeconds(3));
        Assert.True(main.IsChangelogSelected);
        Assert.Equal(0, updater.CheckCount);
    });

    private static Task ChangelogUnshownVersionAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var root = CreateTempTestDirectory();
        var service = new JsonSettingsService(Path.Combine(root, "settings.json"));
        try
        {
            var settings = ChangelogSettings("1.8.3");
            await service.SaveAsync(settings);
            await using (var main = CreateChangelogMain(settings, service, catalog: ChangelogCatalog()))
            {
                main.Initialize();
                Assert.True(main.IsSettingsOpen && main.IsChangelogSelected);
            }
            var reloaded = await service.LoadAsync();
            Assert.Equal("1.8.3", reloaded.Updates.LastSeenChangelogVersion);
            await using var restarted = CreateChangelogMain(reloaded, service, catalog: ChangelogCatalog());
            restarted.Initialize();
            Assert.True(restarted.IsSettingsOpen && restarted.IsChangelogSelected);
        }
        finally { DeleteTempTestDirectory(root); }
    });

    private static Task ChangelogMarkdownRenderingAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var notes = new ReleaseNotesText
        {
            Markdown = """
                # Stream Studio 1.8.5

                - Open **Settings > Changelog**.
                  Keep `C:\Program Files\Stream Studio` intact.
                - View [the release](https://github.com/CorontoSiete/streamlink-vlc-studio/releases/tag/v1.8.5).

                ## Updating

                Read [previous notes](v1.8.3.md). Keep [local](file:///C:/settings.json) and [script](javascript:alert) as text.

                ```text
                literal **code** <tag> & value
                ```
                """
        };
        var document = notes.Document;
        var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Assert.Contains("C:\\Program Files\\Stream Studio", text);
        Assert.Contains("literal **code** <tag> & value", text);
        Assert.DoesNotContain("**Settings > Changelog**", text);
        Assert.Contains("[local](file:///C:/settings.json)", text);
        var list = document.Blocks.OfType<System.Windows.Documents.List>().Single();
        Assert.Equal(2, list.ListItems.Count);
        Assert.Contains("Keep C:\\Program Files", new TextRange(list.ListItems.FirstListItem.ContentStart, list.ListItems.FirstListItem.ContentEnd).Text);
        var headings = document.Blocks.OfType<Paragraph>().Where(paragraph => paragraph.FontWeight == FontWeights.SemiBold).ToArray();
        Assert.Equal(2, headings.Length);
        var links = ChangelogInlines(document.Blocks).OfType<Hyperlink>().ToArray();
        Assert.Equal(2, links.Length);
        Assert.True(links.All(link => link.NavigateUri.Scheme == "https"));
        Assert.Equal("https://github.com/CorontoSiete/streamlink-vlc-studio/releases/tag/v1.8.3", links[1].NavigateUri.AbsoluteUri);
        Assert.True(notes.IsReadOnly && notes.IsDocumentEnabled);
        var changes = new ReleaseNotesText
        {
            Presentation = ReleaseNotesPresentation.Changes,
            Markdown = """
                ## Playback

                - **Choose what plays.** Pause inactive tabs.
                  Keep `C:\Program Files\Stream Studio` intact.
                - **Return to watching.** View [the release](v1.8.5.md).
                """
        };
        var featureList = changes.Document.Blocks.OfType<System.Windows.Documents.List>().Single();
        Assert.Equal(TextMarkerStyle.None, featureList.MarkerStyle);
        Assert.Equal(2, featureList.ListItems.Count);
        var featureText = new TextRange(featureList.ListItems.FirstListItem.ContentStart, featureList.ListItems.FirstListItem.ContentEnd).Text;
        Assert.Contains("Choose what plays.", featureText);
        Assert.Contains("Pause inactive tabs. Keep C:\\Program Files\\Stream Studio intact.", featureText);
        Assert.Equal(1, ChangelogInlines(featureList.ListItems.FirstListItem.Blocks).OfType<LineBreak>().Count());
        Assert.Equal(1, ChangelogInlines(changes.Document.Blocks).OfType<Hyperlink>().Count());
        return Task.CompletedTask;
    });

    private static Task ChangelogHistoryNavigationAsync()
    {
        var model = new ChangelogViewModel(ChangelogCatalog(), ChangelogSettings("1.8.5"));
        Assert.True(model.IsInstalledReleaseSelected);
        Assert.Equal("INSTALLED VERSION", model.SelectedReleaseLabel);
        Assert.Equal("1 of 3 releases", model.ReleasePositionText);
        Assert.Equal(false, model.ShowNewerReleaseCommand.CanExecute(null));
        Assert.Equal(false, model.ShowInstalledReleaseCommand.CanExecute(null));

        model.ShowOlderReleaseCommand.Execute(null);
        Assert.Equal(new Version(1, 8, 3), model.SelectedRelease!.Version);
        Assert.Equal("EARLIER RELEASE", model.SelectedReleaseLabel);
        Assert.True(model.ShowNewerReleaseCommand.CanExecute(null));
        Assert.True(model.ShowInstalledReleaseCommand.CanExecute(null));
        model.ShowNewerReleaseCommand.Execute(null);
        Assert.True(model.IsInstalledReleaseSelected);

        model.SelectedRelease = model.Releases[^1];
        Assert.Equal("3 of 3 releases", model.ReleasePositionText);
        Assert.Equal(false, model.ShowOlderReleaseCommand.CanExecute(null));
        model.ShowOlderReleaseCommand.Execute(null);
        Assert.Equal(model.Releases[^1], model.SelectedRelease);
        model.ShowInstalledReleaseCommand.Execute(null);
        Assert.True(model.IsInstalledReleaseSelected);

        var missing = new ChangelogViewModel(new ReleaseNotesCatalog(new Version(1, 8, 5), []), ChangelogSettings("1.8.3"));
        Assert.Equal(false, missing.ShowNewerReleaseCommand.CanExecute(null));
        Assert.Equal(false, missing.ShowOlderReleaseCommand.CanExecute(null));
        Assert.Equal(false, missing.ShowInstalledReleaseCommand.CanExecute(null));
        return Task.CompletedTask;
    }

    private static Task ChangelogUpdatingInstructionsAsync()
    {
        var markdown = """
            # Stream Studio 1.8.5

            - A playback improvement.

            ```text
            ## Updating
            Keep this code sample with the changes.
            ```

            ## Updating

            Choose **Restart and install**.

            ### Verification

            Downloads are verified.
            """;
        var release = new ReleaseNotes(new Version(1, 8, 5), markdown);
        Assert.Equal(markdown, release.Markdown);
        Assert.True(release.HasUpdatingInstructions);
        Assert.Contains("## Updating\nKeep this code sample", release.ChangesMarkdown);
        Assert.DoesNotContain("Restart and install", release.ChangesMarkdown);
        Assert.Contains("Restart and install", release.UpdatingMarkdown);
        Assert.Contains("### Verification", release.UpdatingMarkdown);
        Assert.Contains("Stream Studio 1.8.5", release.HeaderMarkdown);
        Assert.DoesNotContain("A playback improvement", release.HeaderMarkdown);
        Assert.Contains("A playback improvement", release.BodyMarkdown);
        Assert.Contains("## Updating\nKeep this code sample", release.BodyMarkdown);
        var withIntroduction = new ReleaseNotes(new Version(1, 8, 5), "# Notes\n\nAn authored introduction\ncontinued on another line.\n\n1. An improvement.\n");
        Assert.Contains("continued on another line.", withIntroduction.HeaderMarkdown);
        Assert.Equal("1. An improvement.", withIntroduction.BodyMarkdown);
        var withoutHeading = new ReleaseNotes(new Version(1, 8, 5), "- Keep the entire authored note.\n");
        Assert.Contains("1.8.5", withoutHeading.HeaderMarkdown);
        Assert.Equal(withoutHeading.ChangesMarkdown, withoutHeading.BodyMarkdown);
        var withoutGuide = new ReleaseNotes(new Version(1, 8, 3), "# Notes\n\n- A fix.\n\n## Updating\n");
        Assert.Equal(false, withoutGuide.HasUpdatingInstructions);
        Assert.Equal(withoutGuide.Markdown, withoutGuide.ChangesMarkdown);
        return Task.CompletedTask;
    }

    private static Task ChangelogResponsivePageAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        main.ShowChangelogCommand.Execute(null);
        var page = (FrameworkElement)window.FindName("ChangelogSettingsPage");
        var header = (ReleaseNotesText)window.FindName("ChangelogReleaseHeader");
        var notes = (ReleaseNotesText)window.FindName("ChangelogNotes");
        var versions = (ComboBox)window.FindName("ChangelogVersionSelector");
        var guide = (Expander)window.FindName("ChangelogUpdatingGuide");
        var updatingNotes = (ReleaseNotesText)window.FindName("ChangelogUpdatingNotes");
        try
        {
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                ThemeManager.ApplyTheme(theme);
                foreach (var size in new[] { new Size(1320, 820), new Size(700, 640), new Size(440, 720) })
                {
                    guide.IsExpanded = false;
                    main.ShowChangelogCommand.Execute(null);
                    LayoutStudioPolishWindow(window, size);
                    Assert.Equal(Visibility.Visible, page.Visibility);
                    Assert.Equal(main.Changelog.SelectedRelease, versions.SelectedItem);
                    AssertChangelogVersionLabel(versions, main.Changelog.SelectedRelease!.DisplayName);
                    Assert.True(notes.ActualHeight > 150, "Release notes must expand fully inside the settings scroll viewer.");
                    Assert.True(header.ActualHeight > 60, "The release title and introduction must expand inside the release card.");
                    Assert.Equal(ScrollBarVisibility.Disabled, notes.VerticalScrollBarVisibility);
                    var viewport = (ScrollViewer)window.FindName(size.Width > 760 ? "SettingsContentScrollViewer" : "SettingsViewport");
                    var notesBounds = notes.TransformToAncestor(viewport).TransformBounds(new Rect(notes.RenderSize));
                    Assert.True(notesBounds.Right <= viewport.ActualWidth + 1, $"Changelog extends beyond the viewport at {size}: {notesBounds}.");
                    var headerBounds = header.TransformToAncestor(viewport).TransformBounds(new Rect(header.RenderSize));
                    Assert.True(headerBounds.Right <= viewport.ActualWidth + 1, $"Release header extends beyond the viewport at {size}: {headerBounds}.");
                    var text = new TextRange(header.Document.ContentStart, header.Document.ContentEnd).Text;
                    Assert.Contains(main.Changelog.SelectedRelease!.Version.ToString(3), text);
                    Assert.Equal(main.Changelog.SelectedRelease!.BodyMarkdown, notes.Markdown);
                    Assert.True(!string.IsNullOrWhiteSpace(new TextRange(notes.Document.ContentStart, notes.Document.ContentEnd).Text),
                        "The selected release's changes must be rendered.");
                    WpfVisualTest.AssertSolidBrushColor(WpfVisualTest.PaletteColor(window, "StudioTextColor"), notes.Document.Foreground);
                    WpfVisualTest.AssertSolidBrushColor(WpfVisualTest.PaletteColor(window, "StudioTextColor"), header.Document.Foreground);
                    if (size.Width > 760)
                    {
                        var caption = (TextBlock)window.FindName("WorkspacePageContextTitle");
                        var fullCaption = new TextBlock
                        {
                            Text = caption.Text,
                            FontFamily = caption.FontFamily,
                            FontSize = caption.FontSize,
                            FontWeight = caption.FontWeight
                        };
                        fullCaption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        Assert.True(caption.ActualWidth >= fullCaption.DesiredSize.Width,
                            $"The changelog toolbar title must remain fully visible in {theme}.");
                    }
                    SaveResponsiveWindowImage(window, $"changelog-{theme}-{size.Width}");
                    Assert.Equal(Visibility.Visible, guide.Visibility);
                    Assert.Equal(false, guide.IsExpanded);
                    guide.IsExpanded = true;
                    LayoutStudioPolishWindow(window, size);
                    Assert.True(updatingNotes.ActualHeight > 100, "Expanded update instructions must participate in the offscreen layout.");
                    Assert.Contains("Restart and install", new TextRange(updatingNotes.Document.ContentStart, updatingNotes.Document.ContentEnd).Text);
                    viewport.ScrollToEnd();
                    LayoutStudioPolishWindow(window, size);
                    var guideBounds = guide.TransformToAncestor(viewport).TransformBounds(new Rect(guide.RenderSize));
                    Assert.True(guideBounds.Bottom <= viewport.ActualHeight + 1, $"Update instructions must remain reachable at {size}: {guideBounds}.");
                    SaveResponsiveWindowImage(window, $"changelog-updating-{theme}-{size.Width}");
                    guide.IsExpanded = false;
                    versions.SelectedItem = main.Changelog.Releases[^1];
                    LayoutStudioPolishWindow(window, size);
                    Assert.Equal(main.Changelog.Releases[^1], main.Changelog.SelectedRelease);
                    AssertChangelogVersionLabel(versions, main.Changelog.Releases[^1].DisplayName);
                    Assert.Contains(main.Changelog.Releases[^1].Version.ToString(3),
                        new TextRange(header.Document.ContentStart, header.Document.ContentEnd).Text);
                    Assert.Contains("More reliable replay pause/resume", new TextRange(notes.Document.ContentStart, notes.Document.ContentEnd).Text);
                    SaveResponsiveWindowImage(window, $"changelog-history-{theme}-{size.Width}");
                    if (size.Width < 760)
                    {
                        var categories = (ComboBox)window.FindName("CompactSettingsCategorySelector");
                        Assert.Equal(SettingsCategory.Changelog, categories.SelectedItem);
                    }
                    viewport.ScrollToEnd();
                    LayoutStudioPolishWindow(window, size);
                    var lastBounds = page.TransformToAncestor(viewport).TransformBounds(new Rect(page.RenderSize));
                    Assert.True(lastBounds.Bottom <= viewport.ActualHeight + 1,
                        $"The end of the changelog must be reachable at {size}: {lastBounds}.");
                }
            }
        }
        finally { ThemeManager.ApplyTheme(AppTheme.Dark); }
        return Task.CompletedTask;
    });

    private static Task ChangelogPresentationPersistsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var root = CreateTempTestDirectory();
        var service = new JsonSettingsService(Path.Combine(root, "settings.json"));
        var settings = ChangelogSettings("1.7.2");
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            await using (var main = CreateChangelogMain(settings, service, dispatch: action => dispatcher.BeginInvoke(action)))
            {
                main.Initialize();
                var window = new MainWindow
                {
                    DataContext = main,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    ShowActivated = false,
                    ShowInTaskbar = false
                };
                RemoveMainWindowAutomaticStartup(window);
                SetMainWindowViewModel(window, main);
                try
                {
                    LayoutStudioPolishWindow(window, new Size(1320, 820));
                    Assert.Equal("1.7.2", settings.Updates.LastSeenChangelogVersion);
                    window.Show();
                    await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert.True(((FrameworkElement)window.FindName("ChangelogSettingsPage")).IsVisible);
                    var installed = ReleaseNotesCatalog.LoadEmbedded().InstalledVersion.ToString(3);
                    Assert.Equal(installed, settings.Updates.LastSeenChangelogVersion);
                    await TestWait.UntilAsync(() => File.Exists(service.SettingsPath), TimeSpan.FromSeconds(4));
                    var saved = await service.LoadAsync();
                    Assert.Equal(installed, saved.Updates.LastSeenChangelogVersion);
                    main.ToggleSettingsCommand.Execute(null);
                    Assert.Equal(false, main.IsSettingsOpen);
                }
                finally { window.Close(); }
            }
            var reloaded = await service.LoadAsync();
            await using var restarted = CreateChangelogMain(reloaded, service);
            restarted.Initialize();
            Assert.Equal(false, restarted.IsSettingsOpen);
            restarted.ShowChangelogCommand.Execute(null);
            Assert.True(restarted.IsSettingsOpen && restarted.IsChangelogSelected);
        }
        finally { DeleteTempTestDirectory(root); }
    });

    private static Task ChangelogManualNavigationAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        main.ShowRecentHomePageCommand.Execute(null);
        main.ShowChangelogCommand.Execute(null);
        LayoutStudioPolishWindow(window, new Size(1320, 820));
        Assert.True(main.IsSettingsOpen && main.IsChangelogSelected);
        main.GoBackCommand.Execute(null);
        Assert.True(!main.IsSettingsOpen && main.IsRecentHomePageSelected);
        main.ShowChangelogCommand.Execute(null);
        main.ToggleSettingsCommand.Execute(null);
        Assert.True(!main.IsSettingsOpen && main.IsRecentHomePageSelected);
        return Task.CompletedTask;
    });

    private static AppSettings ChangelogSettings(string seen) => new()
    {
        SetupCompleted = true,
        Updates = new UpdateSettings { LastSeenChangelogVersion = seen, AutomaticChecksEnabled = false }
    };

    private static ReleaseNotesCatalog ChangelogCatalog(Version? installed = null)
    {
        var current = ReleaseNotesCatalog.Normalize(installed ?? new Version(1, 8, 5, 0));
        return new ReleaseNotesCatalog(current, new[] { new Version(1, 7, 2), new Version(1, 8, 3), current, new Version(2, 0, 0) }
            .Distinct().Select(version => new ReleaseNotes(version, $"# Stream Studio {version.ToString(3)}\n\n- Changes for this version.\n")));
    }

    private static AppUpdateCompletion ChangelogCompletion(AppUpdateCompletionOutcome outcome, Version? version = null) =>
        new(Guid.NewGuid(), outcome, outcome == AppUpdateCompletionOutcome.Failed ? 1 : 0, null,
            "Installation result.", DateTimeOffset.UtcNow, version ?? new Version(1, 8, 5));

    private static MainViewModel CreateChangelogMain(AppSettings settings, ISettingsService? settingsService = null,
        IAppUpdateService? updater = null, ReleaseNotesCatalog? catalog = null, Action<Action>? dispatch = null) =>
        new(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = settingsService ?? new FakeSettingsService(settings),
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = dispatch ?? (action => action()),
            AppUpdateService = updater,
            ReleaseNotesCatalog = catalog
        });

    private static IEnumerable<Inline> ChangelogInlines(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is System.Windows.Documents.List list)
            {
                foreach (var item in list.ListItems)
                    foreach (var inline in ChangelogInlines(item.Blocks)) yield return inline;
            }
            else if (block is Paragraph paragraph)
            {
                foreach (var inline in paragraph.Inlines) yield return inline;
            }
        }
    }

    private static void AssertChangelogVersionLabel(ComboBox selector, string expected)
    {
        var labels = FindVisualDescendants<TextBlock>(selector).Select(label => label.Text).ToArray();
        Assert.True(labels.Contains(expected, StringComparer.Ordinal), "The selected version must display its label.");
        Assert.True(labels.All(label => !label.Contains("Markdown =", StringComparison.Ordinal)),
            "The selector must never display a release object's contents.");
        Assert.True(selector.ActualHeight <= 60, "The version selector must stay a single compact row.");
    }

    private sealed class ChangelogCompletionService : IAppUpdateService
    {
        public Task<AppUpdateCompletion?> Completion { get; init; } = Task.FromResult<AppUpdateCompletion?>(null);
        internal int ConsumeCount { get; private set; }
        internal int CheckCount { get; private set; }
        public Task<AppUpdateCompletion?> ConsumeCompletionAsync(CancellationToken token = default)
        {
            ConsumeCount++;
            return Completion.WaitAsync(token);
        }
        public Task<AppUpdateCheckResult> CheckAsync(UpdateCheckReason reason, CancellationToken token = default)
        {
            CheckCount++;
            throw new InvalidOperationException("Changelog presentation must not require an update check.");
        }
    }
}
