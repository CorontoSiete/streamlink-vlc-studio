using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class MainWindow
{
    private readonly ScaleTransform chromeScale = new(1, 1);
    private readonly ScaleTransform chatScale = new(1, 1);
    private bool isChatStacked;

    private void WorkspaceRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void UpdateResponsiveLayout()
    {
        if (PlaybackActionsToolBar is null || WorkspaceRoot.ActualWidth <= 0) return;

        var width = WorkspaceRoot.ActualWidth;
        var height = WorkspaceRoot.ActualHeight;
        // Reflow/overflow at ordinary sizes. Only the two WPF chrome rows scale at
        // extremely small dimensions; native video is always laid out at client size.
        var scale = Math.Max(0.001, Math.Min(1, Math.Min(width / 160, height / 160)));
        chromeScale.ScaleX = scale;
        chromeScale.ScaleY = scale;
        ApplyWindowChromeHitTestState();
        TitleBar.LayoutTransform = chromeScale;
        TopControlsBar.LayoutTransform = chromeScale;
        var chromeWidth = width / scale;
        WindowButtons.Width = Math.Min(138, chromeWidth);
        TitleBrand.Visibility = chromeWidth < 450 ? Visibility.Collapsed : Visibility.Visible;
        PlaybackActionsToolBar.MaxWidth = Math.Max(28, chromeWidth - 54 - Math.Min(160, chromeWidth * 0.35));
        if (!fullscreen) TopControlsRow.Height = new GridLength(54 * scale);

        var stackChat = width < 600;
        if (stackChat != isChatStacked)
        {
            isChatStacked = stackChat;
            DockPanel.SetDock(DockedChatPanel, stackChat ? Dock.Bottom : Dock.Right);
            if (stackChat)
                DockedChatPanel.Width = double.NaN;
            else
                DockedChatPanel.SetBinding(WidthProperty, new Binding("Settings.Chat.DockWidth"));
        }

        DockedChatPanel.MinWidth = stackChat ? 0 : Math.Min(ChatSettings.MinimumDockWidth, width * 0.45);
        DockedChatPanel.MaxWidth = stackChat ? width : Math.Min(ChatSettings.MaximumDockWidth, width * 0.45);
        DockedChatPanel.Height = stackChat ? Math.Max(0, PlaybackHost.ActualHeight * 0.45) : double.NaN;
        DockedChatResizeThumb.Visibility = stackChat ? Visibility.Collapsed : Visibility.Visible;
        // Below a readable chat width, shrink its WPF controls just enough to keep
        // the composer and vertical scrollbar reachable; do not scale the video.
        var chatControlScale = Math.Max(0.001, Math.Min(1, width / 220));
        chatScale.ScaleX = chatControlScale;
        chatScale.ScaleY = chatControlScale;
        DockedChatViewport.LayoutTransform = chatScale;
    }
}
