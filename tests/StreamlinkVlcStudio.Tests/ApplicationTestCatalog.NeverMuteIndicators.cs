internal static partial class ApplicationTestCatalog
{
    internal static Task NeverMuteTabIndicatorsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new NeverMuteTabTestCatalog.MainFixture();
        var first = fixture.AddTab("albralelie").Tab;
        var second = fixture.AddTab("summit1g").Tab;
        fixture.Main.SelectedTab = second;
        using var view = new NeverMuteIndicatorView(fixture.Main);
        var firstIcon = view.WideIcon(first);
        var firstWidth = view.WideContainer(first).ActualWidth;
        view.AssertTab(first, false);
        view.AssertTab(second, false);
        view.AssertSelected(false);

        // Toggle after both templates are realized, without changing selection.
        first.NeverMute = true;
        view.Pump();
        Assert.True(ReferenceEquals(firstIcon, view.WideIcon(first)), "Toggling must update the existing tab visual.");
        view.AssertTab(first, true);
        view.AssertTab(second, false);
        view.AssertSelected(false);
        Assert.Equal(second, fixture.Main.SelectedTab);
        AssertNear(firstWidth, view.WideContainer(first).ActualWidth);
        Assert.Equal("Never mute enabled", firstIcon.ToolTip?.ToString());
        view.Save("never-mute-background-tab");

        fixture.Main.SelectedTab = first;
        view.Pump();
        view.AssertSelected(true);
        view.AssertTab(first, true);
        view.AssertTab(second, false);
        view.Save("never-mute-selected-tab");

        try
        {
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Light);
            using var lightView = new NeverMuteIndicatorView(fixture.Main);
            lightView.AssertTab(first, true);
            lightView.AssertSelected(true);
            lightView.Save("never-mute-selected-tab-light");
        }
        finally
        {
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }

        first.NeverMute = false;
        second.NeverMute = true;
        view.Pump();
        view.AssertTab(first, false);
        view.AssertTab(second, true);
        view.AssertSelected(false);
    });

    internal static Task NeverMuteGroupIndicatorsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new NeverMuteTabTestCatalog.MainFixture();
        var first = fixture.AddTab("albralelie").Tab;
        var second = fixture.AddTab("summit1g").Tab;
        var ordinary = fixture.AddTab("aceu").Tab;
        Assert.True(fixture.Main.TryMergeTabsIntoMultiView([second], first, second));
        using var view = new NeverMuteIndicatorView(fixture.Main);
        var original = fixture.Main.TabStripItems.Single(item => item.Contains(first));
        Assert.Equal(second, original.ActiveTab);
        view.AssertTab(first, false);

        first.NeverMute = true;
        view.Pump();
        view.AssertTab(first, true);
        view.AssertTab(ordinary, false);
        view.AssertSelected(true);
        Assert.Contains(first.Title, view.WideIcon(first).ToolTip!.ToString()!);
        Assert.DoesNotContain(second.Title, view.WideIcon(first).ToolTip!.ToString()!);

        // The group stays marked while any member is protected, including after
        // selecting outside the group rebuilds the tab-strip view models.
        second.NeverMute = true;
        first.NeverMute = false;
        view.Pump();
        view.AssertTab(first, true);
        Assert.Contains(second.Title, view.DropDownIcon(first).ToolTip!.ToString()!);
        Assert.DoesNotContain(first.Title, view.DropDownIcon(first).ToolTip!.ToString()!);
        fixture.Main.SelectedTab = ordinary;
        view.Pump();
        var rebuilt = fixture.Main.TabStripItems.Single(item => item.Contains(first));
        Assert.True(!ReferenceEquals(original, rebuilt), "Changing selection must exercise rebuilt group subscriptions.");
        var staleNotifications = new List<string?>();
        original.PropertyChanged += (_, args) => staleNotifications.Add(args.PropertyName);
        second.NeverMute = false;
        view.Pump();
        view.AssertTab(first, false);
        view.AssertSelected(false);
        Assert.Equal(0, staleNotifications.Count);

        second.NeverMute = true;
        view.Pump();
        Assert.Equal(first, rebuilt.ActiveTab);
        view.AssertTab(first, true);
        view.AssertTab(ordinary, false);
        view.AssertSelected(false);
        view.Save("never-mute-background-group");
    });

    private sealed class NeverMuteIndicatorView : IDisposable
    {
        private readonly MainWindow owner = new();
        private readonly MainViewModel main;
        private readonly Grid host;
        private readonly ListBox tabs;
        private readonly ComboBox selector;
        private readonly FrameworkElement dropDown;

        internal NeverMuteIndicatorView(MainViewModel main)
        {
            this.main = main;
            RemoveMainWindowAutomaticStartup(owner);
            tabs = (ListBox)owner.FindName("TabListBox");
            selector = (ComboBox)owner.FindName("CompactTabSelector");
            // Use the production controls and their templates. Lay out the popup
            // content directly so this regression never opens a desktop window.
            ((Panel)tabs.Parent).Children.Remove(tabs);
            ((Panel)selector.Parent).Children.Remove(selector);
            host = new Grid { DataContext = main, Resources = owner.Resources };
            host.Background = WpfVisualTest.PaletteBrush(owner, "StudioSurface0Brush");
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            tabs.Visibility = Visibility.Visible;
            Grid.SetColumn(tabs, 0);
            host.Children.Add(tabs);
            selector.Width = 260;
            selector.HorizontalAlignment = HorizontalAlignment.Left;
            selector.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetRow(selector, 1);
            host.Children.Add(selector);
            PumpHost();
            dropDown = (FrameworkElement)((Popup)selector.Template.FindName("PART_Popup", selector)).Child;
            Pump();
        }

        internal void Pump()
        {
            PumpHost();
            dropDown.Measure(new Size(260, 200));
            dropDown.Arrange(new Rect(0, 0, 260, Math.Max(1, dropDown.DesiredSize.Height)));
            dropDown.UpdateLayout();
        }

        private void PumpHost()
        {
            host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            host.Measure(new Size(760, 112));
            host.Arrange(new Rect(0, 0, 760, 112));
            host.UpdateLayout();
        }

        internal FrameworkElement WideContainer(StreamTabViewModel tab) =>
            (FrameworkElement)tabs.ItemContainerGenerator.ContainerFromItem(main.TabStripItems.Single(item => item.Contains(tab)));

        internal TextBlock WideIcon(StreamTabViewModel tab) =>
            FindVisualDescendants<TextBlock>(WideContainer(tab)).Single(label => label.Name == "TabNeverMuteIcon");

        internal TextBlock DropDownIcon(StreamTabViewModel tab) =>
            FindVisualDescendants<TextBlock>(dropDown).Single(label => label.Name == "TabNeverMuteIcon" &&
                label.DataContext is TabStripItemViewModel item && item.Contains(tab));

        internal void AssertTab(StreamTabViewModel tab, bool enabled)
        {
            AssertIcon(WideIcon(tab), enabled);
            AssertIcon(DropDownIcon(tab), enabled);
            if (enabled && main.SelectedTabStripItem?.Contains(tab) == true)
            {
                // Selected rows have an accent background in both layouts.
                WpfVisualTest.AssertSolidBrushColor("#FFFFFFFF", WideIcon(tab).Foreground);
                WpfVisualTest.AssertSolidBrushColor("#FFFFFFFF", DropDownIcon(tab).Foreground);
            }
        }

        internal void AssertSelected(bool enabled) => AssertIcon(
            FindVisualDescendants<TextBlock>(selector).Single(label => label.Name == "TabNeverMuteIcon"), enabled);

        private static void AssertIcon(TextBlock icon, bool enabled)
        {
            Assert.Equal(enabled ? Visibility.Visible : Visibility.Collapsed, icon.Visibility);
            if (!enabled)
            {
                Assert.Equal("", icon.ToolTip?.ToString());
                return;
            }

            Assert.Contains("Never mute enabled", icon.ToolTip!.ToString()!);
            Assert.Equal("Never mute enabled", System.Windows.Automation.AutomationProperties.GetName(icon));
            Assert.Equal(icon.ToolTip.ToString(), System.Windows.Automation.AutomationProperties.GetHelpText(icon));
            Assert.True(icon.ActualWidth > 0 && icon.ActualHeight > 0, "An enabled icon must have a visible footprint.");
            var row = (FrameworkElement)VisualTreeHelper.GetParent(icon);
            var title = FindVisualDescendants<TextBlock>(row).Single(text => !ReferenceEquals(text, icon));
            var bounds = icon.TransformToAncestor(row).TransformBounds(new Rect(icon.RenderSize));
            var titleBounds = title.TransformToAncestor(row).TransformBounds(new Rect(title.RenderSize));
            Assert.True(bounds.Right <= row.ActualWidth + 0.1 && bounds.Left >= titleBounds.Right - 0.1,
                "The icon must stay beside the title inside the available tab width.");
            // Render the realized icon at the bitmap origin; rendering this child
            // directly retains its tab-row offset and clips it outside the bitmap.
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(new VisualBrush(icon), null, new Rect(icon.RenderSize));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(icon.ActualWidth),
                (int)Math.Ceiling(icon.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            Assert.True(BitmapAssert.CountPixels(bitmap, (_, _, _) => true) > 0,
                "An enabled tab indicator must render actual glyph pixels.");
        }

        internal void Save(string name)
        {
            var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            System.IO.Directory.CreateDirectory(directory);
            SaveVisual(host, name);
            SaveVisual(dropDown, name + "-dropdown");

            void SaveVisual(FrameworkElement element, string fileName)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(WpfVisualTest.Render(element)));
                using var output = System.IO.File.Create(System.IO.Path.Combine(directory, fileName + ".png"));
                encoder.Save(output);
            }
        }

        public void Dispose() => owner.Close();
    }
}
