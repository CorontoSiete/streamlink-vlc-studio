internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ResponsiveClippingTests =>
    [
        ("responsive clipping settings controls remain completely reachable in every category", () =>
            WithResponsiveWindowAsync(withVideo: false, (window, viewModel) =>
            {
                var failures = new List<string>();
                viewModel.IsSettingsOpen = true;
                foreach (var size in new (int Width, int Height)[]
                    { (320, 240), (480, 320), (1000, 200), (1000, 160), (200, 160), (120, 100), (1100, 760) })
                {
                    var client = ResizeResponsiveWindow(window, size.Width, size.Height);
                    foreach (var category in Enum.GetValues<SettingsCategory>())
                    {
                        viewModel.SelectedSettingsCategory = category;
                        PumpResponsiveLayout(window);
                        ((ScrollViewer)window.FindName("SettingsViewport")).ScrollToHome();
                        ((ScrollViewer)window.FindName("SettingsContentScrollViewer")).ScrollToHome();
                        PumpResponsiveLayout(window);
                        SaveResponsiveWindowImage(window, $"settings-fields-{category}-{size.Width}x{size.Height}");
                        var page = (FrameworkElement)window.FindName(category + "SettingsPage");
                        Assert.True(page.IsVisible, $"The {category} settings page did not open.");
                        var controls = FindVisualDescendants<Control>(page)
                            .Where(control => control.IsVisible && control.TemplatedParent is null &&
                                control is ButtonBase or TextBox or PasswordBox or ComboBox or Slider)
                            .ToArray();
                        Assert.True(controls.Length > 0, $"No controls inspected on {category}.");
                        foreach (var control in controls.Append((Control)window.FindName("SettingsSaveButton")))
                        {
                            RecordResponsiveClippingFailure(failures, $"{size.Width}x{size.Height} {category}", () =>
                                AssertResponsiveControlCompletelyReachable(window, control, client,
                                    (FrameworkElement)window.FindName("SettingsViewport")));
                        }
                    }
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
                return Task.CompletedTask;
            })),
        ("responsive clipping home and window actions fit narrow and short wide windows", () =>
            AssertResponsiveChromeWithoutClippingAsync(withVideo: false)),
        ("responsive clipping playback and window actions fit narrow and short wide windows", () =>
            AssertResponsiveChromeWithoutClippingAsync(withVideo: true)),
        ("responsive clipping docked chat send label remains fully visible", () =>
            WithResponsiveWindowAsync(withVideo: true, (window, viewModel) =>
            {
                viewModel.Settings.Chat.Layout = ChatLayout.Docked;
                viewModel.SelectedTab!.IsChatVisible = true;
                viewModel.SelectedTab.IsDockedChatPanelVisible = true;
                foreach (var size in new (int Width, int Height)[] { (320, 240), (480, 320), (1100, 760) })
                {
                    var client = ResizeResponsiveWindow(window, size.Width, size.Height);
                    var chat = (FrameworkElement)window.FindName("DockedChatPanel");
                    var send = FindVisualDescendants<Button>(chat).Single(button => Equals(button.Content, "Send"));
                    var label = FindVisualDescendants<TextBlock>(send).Single(text => text.Text == "Send");
                    send.BringIntoView();
                    PumpResponsiveLayout(window);
                    AssertResponsiveControlFullyVisible(label, client);
                }
                return Task.CompletedTask;
            }))
    ];

    private static Task AssertResponsiveChromeWithoutClippingAsync(bool withVideo) =>
        WithResponsiveWindowAsync(withVideo, (window, _) =>
        {
            var failures = new List<string>();
            foreach (var size in new (int Width, int Height)[] { (320, 240), (480, 320), (1000, 200), (1000, 160) })
            {
                var client = ResizeResponsiveWindow(window, size.Width, size.Height);
                var windowButtons = (FrameworkElement)window.FindName("WindowButtons");
                var home = FindVisualDescendants<Button>(window)
                    .Single(button => Equals(button.ToolTip, "Home"));
                var actions = FindVisualDescendants<Button>(windowButtons).Append(home).ToList();
                var toolbar = (ToolBar)window.FindName(withVideo ? "PlaybackActionsToolBar" : "HomeNavigation");
                actions.AddRange(toolbar.Items.OfType<Button>().Where(button => button.Visibility == Visibility.Visible));
                var settings = ((ToolBar)window.FindName("PlaybackActionsToolBar")).Items.OfType<Button>()
                    .Single(button => Equals(button.ToolTip, "Open settings"));
                if (!actions.Contains(settings)) actions.Add(settings);
                foreach (var action in actions)
                {
                    RecordResponsiveClippingFailure(failures, $"{size.Width}x{size.Height}", () =>
                        AssertResponsiveActionWithoutClipping(window, action, client));
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            return Task.CompletedTask;
        });

    private static void AssertResponsiveActionWithoutClipping(MainWindow window, Button button, Rect client)
    {
        if (!ToolBar.GetIsOverflowItem(button))
        {
            AssertResponsiveControlFullyVisible(button, client);
            AssertResponsiveActionLabelsFullyVisible(button, client);
            return;
        }

        var toolbar = ItemsControl.ItemsControlFromItemContainer(button) as ToolBar ?? button.Parent as ToolBar;
        Assert.NotNull(toolbar);
        var overflow = toolbar!.Template.FindName("OverflowButton", toolbar) as FrameworkElement;
        Assert.NotNull(overflow);
        AssertResponsiveControlFullyVisible(overflow!, client);
        toolbar.IsOverflowOpen = true;
        try
        {
            PumpResponsiveLayout(window);
            // An overflow popup may extend beyond the main window, but its own visual
            // ancestors still must not clip any part of the action.
            AssertResponsiveControlFullyVisible(button, GetResponsiveScreenBounds(button));
            AssertResponsiveActionLabelsFullyVisible(button, GetResponsiveScreenBounds(button));
        }
        finally
        {
            toolbar.IsOverflowOpen = false;
        }
    }

    private static void AssertResponsiveActionLabelsFullyVisible(Button button, Rect client)
    {
        foreach (var label in FindVisualDescendants<TextBlock>(button).Where(label => label.IsVisible))
        {
            AssertResponsiveControlFullyVisible(label, client);
        }
    }

    private static void AssertResponsiveControlCompletelyReachable(
        MainWindow window, FrameworkElement control, Rect client, FrameworkElement? availableViewport = null)
    {
        Assert.True(control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0,
            $"{ResponsiveControlDescription(control)} has no visible layout.");
        control.BringIntoView();
        PumpResponsiveLayout(window);
        var fullBounds = GetResponsiveScreenBounds(control);
        var clip = GetResponsiveEffectiveClip(control, client);
        if (ResponsiveBoundsContain(clip, fullBounds)) return;

        // A control that fits the available Settings area must be visible in full;
        // an accidentally tiny nested scroller must not satisfy this test by exposing
        // only a sliver. Only use smaller regions on axes where the whole control
        // is larger than the available area, as with a tall editor in a tiny window.
        var regionWidth = Math.Min(4, control.ActualWidth);
        var regionHeight = Math.Min(4, control.ActualHeight);
        if (availableViewport is not null)
        {
            var availableBounds = GetResponsiveScreenBounds(availableViewport);
            availableBounds.Intersect(GetResponsiveEffectiveClip(availableViewport, client));
            if (availableViewport is ScrollViewer scrollViewer)
            {
                var scrollArea = ResponsiveRectToScreen(scrollViewer,
                    new Rect(0, 0, scrollViewer.ViewportWidth, scrollViewer.ViewportHeight));
                availableBounds.Width = Math.Min(availableBounds.Width, scrollArea.Width);
                availableBounds.Height = Math.Min(availableBounds.Height, scrollArea.Height);
            }
            if (fullBounds.Width <= availableBounds.Width + 1) regionWidth = control.ActualWidth;
            if (fullBounds.Height <= availableBounds.Height + 1) regionHeight = control.ActualHeight;
        }
        foreach (var x in new[] { 0, (control.ActualWidth - regionWidth) / 2, control.ActualWidth - regionWidth }.Distinct())
        {
            foreach (var y in new[] { 0, (control.ActualHeight - regionHeight) / 2, control.ActualHeight - regionHeight }.Distinct())
            {
                var region = new Rect(x, y, regionWidth, regionHeight);
                control.BringIntoView(region);
                PumpResponsiveLayout(window);
                var regionBounds = ResponsiveRectToScreen(control, region);
                clip = GetResponsiveEffectiveClip(control, client);
                Assert.True(ResponsiveBoundsContain(clip, regionBounds),
                    $"{ResponsiveControlDescription(control)} cannot reveal region {region}; " +
                    $"screen region {regionBounds}, effective clip {clip}, control {GetResponsiveScreenBounds(control)}.");
            }
        }
    }

    private static void AssertResponsiveControlFullyVisible(FrameworkElement control, Rect client)
    {
        Assert.True(control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0,
            $"{ResponsiveControlDescription(control)} has no visible layout.");
        var bounds = GetResponsiveScreenBounds(control);
        var clip = GetResponsiveEffectiveClip(control, client);
        Assert.True(ResponsiveBoundsContain(clip, bounds),
            $"{ResponsiveControlDescription(control)} is partly clipped: control {bounds}, effective clip {clip}.");
    }

    private static Rect GetResponsiveEffectiveClip(FrameworkElement element, Rect client)
    {
        var clip = client;
        for (DependencyObject? ancestor = element; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is not FrameworkElement frameworkElement) continue;
            // GetClip includes WPF's generated layout clip, which can be smaller
            // than ActualWidth/ActualHeight even when ClipToBounds is false.
            var geometry = VisualTreeHelper.GetClip(frameworkElement);
            if (geometry is not null)
            {
                if (geometry.Bounds.IsEmpty) return Rect.Empty;
                clip.Intersect(ResponsiveRectToScreen(frameworkElement, geometry.Bounds));
            }
        }
        return clip;
    }

    private static Rect ResponsiveRectToScreen(FrameworkElement element, Rect rectangle) =>
        new(element.PointToScreen(rectangle.TopLeft), element.PointToScreen(rectangle.BottomRight));

    private static bool ResponsiveBoundsContain(Rect clip, Rect bounds)
    {
        const double tolerance = 1;
        return !clip.IsEmpty && bounds.Left >= clip.Left - tolerance && bounds.Top >= clip.Top - tolerance &&
            bounds.Right <= clip.Right + tolerance && bounds.Bottom <= clip.Bottom + tolerance;
    }

    private static string ResponsiveControlDescription(FrameworkElement control) =>
        $"{control.GetType().Name} '{control.Name}' " +
        (control is ContentControl { Content: string content } ? content : control.ToolTip?.ToString());

    private static void RecordResponsiveClippingFailure(List<string> failures, string context, Action assertion)
    {
        try { assertion(); }
        catch (InvalidOperationException error) { failures.Add($"{context}: {error.Message}"); }
    }
}
