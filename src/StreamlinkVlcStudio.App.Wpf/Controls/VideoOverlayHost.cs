using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>
/// Hosts WPF controls in a transparent child of the video HWND. Windows moves and clips
/// the controls with the video; there is no independent desktop window to reposition.
/// </summary>
[ContentProperty(nameof(Child))]
public sealed partial class VideoOverlayHost : FrameworkElement
{
    private const int WsChild = 0x40000000;
    private const int WsClipSiblings = 0x04000000;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private FrameworkElement? child;
    private VideoSurface? target;
    private HwndSource? source;
    private Rect bounds;
    private Int32Rect? appliedPixelBounds;
    internal event EventHandler? PlacementInvalidated;

    public FrameworkElement? Child
    {
        get => child;
        set
        {
            if (ReferenceEquals(child, value)) return;
            Close();
            if (child is not null) RemoveLogicalChild(child);
            child = value;
            if (child is not null) AddLogicalChild(child);
        }
    }

    // Retain resource, namescope and DataContext inheritance while the visual is rendered
    // in another HwndSource. Adding it to the normal visual tree would hit HWND airspace.
    protected override IEnumerator LogicalChildren => child is null
        ? Array.Empty<FrameworkElement>().GetEnumerator()
        : new[] { child }.GetEnumerator();

    internal VideoSurface? PlacementTarget
    {
        get => target;
        set
        {
            if (ReferenceEquals(target, value)) return;
            Close();
            if (target is not null) target.NativeHandleDestroying -= OnTargetDestroying;
            target = value;
            if (target is not null) target.NativeHandleDestroying += OnTargetDestroying;
        }
    }

    internal bool IsOpen => source is { IsDisposed: false };

    internal Size TargetSize
    {
        get
        {
            if (target is null || !GetClientRect(target.Handle, out var client)) return Size.Empty;
            var scale = VisualTreeHelper.GetDpi(target);
            return new Size(Math.Max(0, client.Right - client.Left) / scale.DpiScaleX,
                Math.Max(0, client.Bottom - client.Top) / scale.DpiScaleY);
        }
    }

    internal void SetBounds(Rect placement)
    {
        bounds = placement;
        if (IsOpen) ApplyBounds();
    }

    internal void Open(Action updatePlacement)
    {
        if (IsOpen) { updatePlacement(); return; }
        if (child is null || target is not { Handle: var parent } || parent == IntPtr.Zero) return;
        var parameters = new HwndSourceParameters("Stream Studio video controls")
        {
            ParentWindow = parent,
            WindowStyle = WsChild | WsClipSiblings,
            UsesPerPixelTransparency = true,
            // This child handles keyboard input itself rather than forwarding it through
            // the native VLC HWND (which is not a WPF keyboard input sink).
            TreatAsInputRoot = true,
            PositionX = 0,
            PositionY = 0,
            Width = 1,
            Height = 1
        };
        source = new HwndSource(parameters) { SizeToContent = SizeToContent.Manual };
        try
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            source.DpiChanged += OnDpiChanged;
            source.RootVisual = child;
            target.RegisterOverlayWindow(source.Handle);
            // Removing RootVisual suspends WPF layout. Reattach it before measuring,
            // especially when the video changed to compact size while controls were hidden.
            updatePlacement();
            ApplyBounds();
        }
        catch
        {
            Close();
            throw;
        }
    }

    internal void Close()
    {
        var previous = source;
        source = null;
        appliedPixelBounds = null;
        if (previous is null) return;
        if (!previous.IsDisposed)
        {
            target?.UnregisterOverlayWindow(previous.Handle);
            previous.DpiChanged -= OnDpiChanged;
            previous.RootVisual = null;
            previous.Dispose();
        }
    }

    private void OnTargetDestroying(object? sender, EventArgs e) => Close();

    private void OnDpiChanged(object sender, HwndDpiChangedEventArgs e)
    {
        var changedSource = source;
        // HwndSource raises this event before updating its composition transform. Let
        // that update complete, then measure and place against the new video client.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (changedSource is not null && ReferenceEquals(source, changedSource) && IsOpen)
                PlacementInvalidated?.Invoke(this, EventArgs.Empty);
        }));
    }

    private void ApplyBounds()
    {
        if (source is not { IsDisposed: false } || target is null) return;
        var scale = VisualTreeHelper.GetDpi(target);
        // All coordinates are relative to the video client, never to the desktop. A move
        // of any ancestor therefore carries this HWND even while the dispatcher is busy.
        var left = (int)Math.Round(bounds.Left * scale.DpiScaleX);
        var top = (int)Math.Round(bounds.Top * scale.DpiScaleY);
        var right = (int)Math.Round(bounds.Right * scale.DpiScaleX);
        var bottom = (int)Math.Round(bounds.Bottom * scale.DpiScaleY);
        var pixels = new Int32Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        var aboveRenderer = target.IsOverlayAboveRenderer(source.Handle);
        // Hover sampling and video bounds repair can repeat without a pixel changing.
        // Leave layered HWNDs alone in that case, and preserve sibling z-order when
        // merely moving the preview. Still repair a late-created VLC renderer above us.
        if (appliedPixelBounds == pixels && aboveRenderer) return;
        var flags = SwpNoActivate;
        if (aboveRenderer) flags |= SwpNoZOrder;
        if (appliedPixelBounds is { } previous)
        {
            if (previous.X == pixels.X && previous.Y == pixels.Y) flags |= SwpNoMove;
            if (previous.Width == pixels.Width && previous.Height == pixels.Height) flags |= SwpNoSize;
        }
        else flags |= SwpShowWindow;
        if (!SetWindowPos(source.Handle, IntPtr.Zero, pixels.X, pixels.Y, pixels.Width, pixels.Height, flags))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to position the video controls.");
        appliedPixelBounds = pixels;
    }

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out NativeRect rect);
}
