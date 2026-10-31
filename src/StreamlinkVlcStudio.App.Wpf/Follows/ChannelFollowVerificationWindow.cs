using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.Follows;

/// <summary>Shows the existing channel document so Twitch can receive real user input.</summary>
internal sealed class ChannelFollowVerificationWindow : IDisposable
{
    private readonly Window window;
    private bool disposed;

    internal ChannelFollowVerificationWindow(Window owner, CoreWebView2Controller controller,
        nint backgroundHost, StreamTarget channel, Action cancel, bool following = true)
    {
        var action = following ? "Follow" : "Unfollow";
        var instructions = new TextBlock
        {
            Text = $"Twitch needs browser verification. Complete any check below, then click {action} for {channel.Channel}. " +
                $"This window closes when Twitch confirms the {action.ToLowerInvariant()}.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center
        };
        var cancelButton = new Button { Content = "Cancel", Margin = new Thickness(12), Padding = new Thickness(14, 7, 14, 7) };
        var header = new DockPanel();
        DockPanel.SetDock(cancelButton, Dock.Right);
        header.Children.Add(cancelButton);
        header.Children.Add(instructions);
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(new Border());
        window = new Window
        {
            Owner = owner,
            Title = $"Complete Twitch {action.ToLowerInvariant()} — {channel.Channel}",
            Width = 1280,
            Height = 850,
            MinWidth = 760,
            MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        cancelButton.Click += (_, _) => window.Close();
        window.SourceInitialized += (_, _) =>
        {
            controller.ParentWindow = new WindowInteropHelper(window).Handle;
            Resize();
            controller.IsVisible = true;
        };
        window.SizeChanged += (_, _) => Resize();
        header.SizeChanged += (_, _) => Resize();
        window.Closing += (_, _) =>
        {
            try
            {
                controller.ParentWindow = backgroundHost;
                controller.Bounds = new Rectangle(0, 0, 1280, 800);
            }
            catch (COMException) { /* The runtime may have exited while the window was open. */ }
            catch (InvalidOperationException) { /* A closed controller no longer has a parent. */ }
        };
        window.Closed += (_, _) => { if (!disposed) cancel(); };
        window.Show();

        void Resize()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == 0) return;
            GetClientRect(handle, out var rect);
            var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M22 ?? 1;
            var top = (int)Math.Ceiling(header.ActualHeight * scale);
            controller.Bounds = new Rectangle(0, top, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom - top));
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect rect);
}
