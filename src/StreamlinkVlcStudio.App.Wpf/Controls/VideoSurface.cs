using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public sealed partial class VideoSurface : HwndHost
{
    private const string VideoSurfaceWindowClassName = "StreamStudioVideoSurface";
    private const int CsDoubleClicks = 0x0008;
    private const int ErrorClassAlreadyExists = 1410;
    private const int BlackBrush = 4;
    private const int WmEraseBackground = 0x0014;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmCreate = 0x0001;
    private const int WmParentNotify = 0x0210;
    private const int WmSetCursor = 0x0020;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseWheel = 0x020A;
    private const int WsChild = 0x40000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExNoActivate = 0x08000000;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int SwHide = 0;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private const int SwpFrameChanged = 0x0020;
    private const int SwpNoCopyBits = 0x0100;
    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;
    private const long DirectChildResizeIntervalMilliseconds = 250;
    private const int RendererWindowRepairIntervalMilliseconds = 250;
    private const int RendererWindowRepairDurationMilliseconds = 8000;
    // Some app/window share pickers inspect visible HWNDs beyond EnumWindows. Keep
    // native video descendants from advertising an independent app-window identity.
    private const int CaptureFriendlyChildExtendedStyle = WsExToolWindow | WsExNoActivate;
    private static readonly object WindowClassGate = new();
    private static readonly NativeWindowProc RegisteredWindowProc = DefWindowProcCallback;
    private static bool windowClassRegistered;
    private IntPtr handle;
    private readonly DoubleClickTracker doubleClickTracker = new();
    private readonly HashSet<IntPtr> overlayWindows = [];
    private int lastNativeWidth = -1;
    private int lastNativeHeight = -1;
    private bool lastNativeVisible;
    private long lastDirectChildResizeAt = long.MinValue;
    private bool directChildBoundsSyncQueued;
    private bool notifyingNativeBoundsChanged;
    private DispatcherTimer? rendererWindowRepairTimer;
    private long rendererWindowRepairUntil;

    public new IntPtr Handle => handle;
    public event EventHandler<VideoSurfaceMouseWheelEventArgs>? MouseWheelScrolled;
    public event EventHandler? SurfaceMouseLeftButtonPressed;
    public event EventHandler? MouseLeftButtonDoubleClicked;
    public event EventHandler<VideoSurfaceNativeMouseEventArgs>? NativeSetCursorRequested;
    public event EventHandler<VideoSurfaceNativeMouseEventArgs>? NativeMouseLeftButtonDown;
    public event EventHandler<VideoSurfaceNativeMouseEventArgs>? NativeMouseMoved;
    public event EventHandler<VideoSurfaceNativeMouseEventArgs>? NativeMouseLeftButtonUp;
    public event EventHandler<VideoSurfaceNativeMouseEventArgs>? NativeMouseRightButtonDown;
    internal event EventHandler? NativeBoundsChanged;
    internal event EventHandler? NativeHandleDestroying;

    public VideoSurface()
    {
        IsVisibleChanged += (_, _) =>
        {
            SyncNativeBounds();
        };
        SizeChanged += (_, _) => SyncNativeBounds();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClassRegistered();

        handle = CreateWindowEx(
            CaptureFriendlyChildExtendedStyle,
            VideoSurfaceWindowClassName,
            "",
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to create the video surface window.");
        }

        ResetNativeBoundsCache();
        SyncNativeBounds();
        NativeMouseWheelTarget.RegisterWindow(handle, acceptsInactiveWheel: Window.GetWindow(this) is DetachedVideoWindow);
        return new HandleRef(this, handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            StopRendererWindowRepairTimer();
            NativeMouseWheelTarget.UnregisterWindow(hwnd.Handle);
            try
            {
                // Release managed child sources before Win32 destroys the descendants of
                // this host, so they cannot retain a dead HWND across detach/reattach.
                NativeHandleDestroying?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                overlayWindows.Clear();
                DestroyWindow(hwnd.Handle);
                handle = IntPtr.Zero;
                ResetNativeBoundsCache();
            }
        }
    }

    internal void RegisterOverlayWindow(IntPtr hwnd)
    {
        Dispatcher.VerifyAccess();
        if (hwnd == IntPtr.Zero || handle == IntPtr.Zero || GetParent(hwnd) != handle)
        {
            throw new ArgumentException("An overlay must be a child of this video surface.", nameof(hwnd));
        }

        overlayWindows.Add(hwnd);
    }

    internal void UnregisterOverlayWindow(IntPtr hwnd)
    {
        Dispatcher.VerifyAccess();
        overlayWindows.Remove(hwnd);
    }

    internal bool IsOverlayAboveRenderer(IntPtr hwnd)
    {
        Dispatcher.VerifyAccess();
        const uint gwHwndPrev = 3;
        // Other registered overlays may be above this one. Only a renderer sibling
        // above it requires repair; raising each overlay to the top on every layout
        // pass makes the seekbar and thumbnail repeatedly exchange z-order.
        for (var sibling = GetWindow(hwnd, gwHwndPrev); sibling != IntPtr.Zero;
             sibling = GetWindow(sibling, gwHwndPrev))
        {
            if (!overlayWindows.Contains(sibling)) return false;
        }
        return true;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        // WPF owns the final device-pixel bounds of the HwndHost. Use its actual client
        // rectangle so fractional DPI/layout rounding cannot leave VLC one pixel smaller.
        ResizeRendererWindowsToClient();
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmParentNotify && GetLowWord(wParam) == WmCreate)
        {
            // VLC creates its renderer window after the host has already been arranged.
            // Defer until WM_CREATE completes, then make that late child fill the host.
            QueueDirectChildBoundsSync();
            QueueRendererWindowRepairIfRenderer(lParam);
        }

        if (msg == WmEraseBackground)
        {
            FillClientArea(hwnd, wParam);
            handled = true;
            return new IntPtr(1);
        }

        if (msg == WmSetCursor && TryRaiseNativeSetCursorRequested(out var cursorResult))
        {
            handled = true;
            return cursorResult;
        }

        if (msg == WmLeftButtonDoubleClick)
        {
            ResetLastLeftButtonDown();
            // A second press on a PiP resize border still belongs to the border, even
            // when Windows reports it as a double-click on this child HWND.
            if (TryRaiseNativeMouseLeftButtonDown(hwnd, lParam, out var mouseDownResult))
            {
                handled = true;
                return mouseDownResult;
            }

            MouseLeftButtonDoubleClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (msg == WmLeftButtonDown)
        {
            if (TryRaiseNativeMouseLeftButtonDown(hwnd, lParam, out var mouseDownResult))
            {
                handled = true;
                return mouseDownResult;
            }

            _ = SetCapture(hwnd);
            SurfaceMouseLeftButtonPressed?.Invoke(this, EventArgs.Empty);
            if (IsLeftButtonDoubleClick(lParam))
            {
                ResetLastLeftButtonDown();
                MouseLeftButtonDoubleClicked?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                CaptureLastLeftButtonDown(lParam);
            }
        }
        else if (msg == WmMouseMove)
        {
            if (TryRaiseNativeMouseEvent(hwnd, lParam, NativeMouseMoved, out var mouseMoveResult))
            {
                handled = true;
                return mouseMoveResult;
            }
        }
        else if (msg == WmLeftButtonUp)
        {
            var mouseUpHandled = TryRaiseNativeMouseEvent(hwnd, lParam, NativeMouseLeftButtonUp, out var mouseUpResult);
            if (GetCapture() == hwnd)
            {
                ReleaseCapture();
            }

            if (mouseUpHandled)
            {
                handled = true;
                return mouseUpResult;
            }
        }
        else if (msg == WmRightButtonDown &&
                 TryRaiseNativeMouseEvent(hwnd, lParam, NativeMouseRightButtonDown, out var rightButtonResult))
        {
            handled = true;
            return rightButtonResult;
        }

        if (msg == WmMouseWheel)
        {
            var delta = GetWheelDelta(wParam);
            if (delta != 0)
            {
                MouseWheelScrolled?.Invoke(this, new VideoSurfaceMouseWheelEventArgs(
                    delta, new Point(GetLParamX(lParam), GetLParamY(lParam))));
                handled = true;
                return IntPtr.Zero;
            }
        }

        var result = base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        if (msg == WmWindowPosChanging && lParam != IntPtr.Zero)
        {
            // HwndHost disables pixel preservation for WPF's asynchronous rendering.
            // This host contains native video, so keep its current pixels until VLC
            // presents the next frame. Keep WPF's assigned position and size intact.
            var position = Marshal.PtrToStructure<NativeWindowPosition>(lParam);
            position.Flags &= ~SwpNoCopyBits;
            Marshal.StructureToPtr(position, lParam, false);
        }
        return result;
    }

    private bool TryRaiseNativeMouseEvent(
        IntPtr hwnd,
        IntPtr lParam,
        EventHandler<VideoSurfaceNativeMouseEventArgs>? handler,
        out IntPtr result)
    {
        result = IntPtr.Zero;
        if (handler is null)
        {
            return false;
        }

        var screenPoint = new NativePoint
        {
            X = GetLParamX(lParam),
            Y = GetLParamY(lParam)
        };
        if (!ClientToScreen(hwnd, ref screenPoint))
        {
            return false;
        }

        var args = new VideoSurfaceNativeMouseEventArgs(screenPoint.X, screenPoint.Y);
        handler(this, args);
        if (!args.Handled)
        {
            return false;
        }

        result = args.Result;
        return true;
    }

    private bool TryRaiseNativeSetCursorRequested(out IntPtr result)
    {
        result = IntPtr.Zero;
        if (NativeSetCursorRequested is not { } handler ||
            !GetCursorPos(out var screenPoint))
        {
            return false;
        }

        var args = new VideoSurfaceNativeMouseEventArgs(screenPoint.X, screenPoint.Y);
        handler(this, args);
        if (!args.Handled)
        {
            return false;
        }

        result = args.Result;
        return true;
    }

    private bool TryRaiseNativeMouseLeftButtonDown(IntPtr hwnd, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (NativeMouseLeftButtonDown is not { } handler)
        {
            return false;
        }

        var screenPoint = new NativePoint
        {
            X = GetLParamX(lParam),
            Y = GetLParamY(lParam)
        };
        if (!ClientToScreen(hwnd, ref screenPoint))
        {
            return false;
        }

        var args = new VideoSurfaceNativeMouseEventArgs(screenPoint.X, screenPoint.Y);
        handler(this, args);
        if (!args.Handled)
        {
            return false;
        }

        result = args.Result;
        return true;
    }

    public void SyncNativeBounds() => SyncNativeBounds(forceDirectChildResize: false);

    internal void ScheduleRendererWindowRepair()
    {
        Dispatcher.VerifyAccess();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        rendererWindowRepairUntil = Math.Max(
            rendererWindowRepairUntil,
            Environment.TickCount64 + RendererWindowRepairDurationMilliseconds);
        SyncNativeBounds(forceDirectChildResize: true);

        if (rendererWindowRepairTimer is null)
        {
            rendererWindowRepairTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(RendererWindowRepairIntervalMilliseconds),
                DispatcherPriority.Loaded,
                RendererWindowRepairTimerOnTick,
                Dispatcher);
        }

        if (!rendererWindowRepairTimer.IsEnabled)
        {
            rendererWindowRepairTimer.Start();
        }
    }

    private void SyncNativeBounds(bool forceDirectChildResize)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var visible = IsVisible &&
            ActualWidth > 0 &&
            ActualHeight > 0;
        if (!visible)
        {
            var visibilityChanged = lastNativeVisible;
            if (lastNativeVisible)
            {
                _ = ShowWindow(handle, SwHide);
            }

            lastNativeVisible = false;
            lastDirectChildResizeAt = long.MinValue;
            if (visibilityChanged)
            {
                RaiseNativeBoundsChanged();
            }

            return;
        }

        var source = PresentationSource.FromVisual(this);

        if (source is null)
        {
            return;
        }

        var transformToDevice = source.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var width = Math.Max(1, (int)Math.Round(ActualWidth * transformToDevice.M11));
        var height = Math.Max(1, (int)Math.Round(ActualHeight * transformToDevice.M22));

        var boundsChanged = !lastNativeVisible ||
            width != lastNativeWidth ||
            height != lastNativeHeight;
        var becomingVisible = !lastNativeVisible;
        if (boundsChanged)
        {
            _ = SetWindowPos(
                handle,
                IntPtr.Zero,
                0,
                0,
                width,
                height,
                SwpNoZOrder | SwpNoActivate | SwpShowWindow);
            lastNativeWidth = width;
            lastNativeHeight = height;
            lastNativeVisible = true;
            if (becomingVisible)
            {
                // A stopped vout has no child to paint over pixels from the previous tab.
                // Repaint once on reveal; retain pixels during ordinary video resizing.
                _ = RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0100);
            }
        }

        // VLC creates its own child window tree after playback starts. Keep a
        // small, throttled discovery window for that tree, but avoid enumerating
        // and moving it on every WPF LayoutUpdated notification.
        if (forceDirectChildResize ||
            boundsChanged ||
            lastDirectChildResizeAt == long.MinValue ||
            Environment.TickCount64 - lastDirectChildResizeAt >= DirectChildResizeIntervalMilliseconds)
        {
            ResizeRendererWindowsToClient(width, height);
            lastDirectChildResizeAt = Environment.TickCount64;
        }
    }

    private void QueueDirectChildBoundsSync()
    {
        if (directChildBoundsSyncQueued)
        {
            return;
        }

        directChildBoundsSyncQueued = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                directChildBoundsSyncQueued = false;
                SyncNativeBounds(forceDirectChildResize: true);
            }));
    }

    private void QueueRendererWindowRepairIfRenderer(IntPtr childHandle)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (handle == IntPtr.Zero ||
                    IsOverlayWindowOrDescendant(childHandle))
                {
                    return;
                }

                ScheduleRendererWindowRepair();
            }));
    }

    private void RendererWindowRepairTimerOnTick(object? sender, EventArgs e)
    {
        if (handle == IntPtr.Zero ||
            Environment.TickCount64 >= rendererWindowRepairUntil)
        {
            StopRendererWindowRepairTimer();
            return;
        }

        SyncNativeBounds(forceDirectChildResize: true);
    }

    private void StopRendererWindowRepairTimer()
    {
        if (rendererWindowRepairTimer is null)
        {
            return;
        }

        rendererWindowRepairTimer.Stop();
        rendererWindowRepairTimer.Tick -= RendererWindowRepairTimerOnTick;
        rendererWindowRepairTimer = null;
        rendererWindowRepairUntil = 0;
    }

    private void ResetNativeBoundsCache()
    {
        lastNativeWidth = -1;
        lastNativeHeight = -1;
        lastNativeVisible = false;
        lastDirectChildResizeAt = long.MinValue;
    }

    private void ResizeRendererWindows(int hostFallbackWidth, int hostFallbackHeight)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var rendererWindows = new List<RendererWindow>();
        var seenRendererWindows = new HashSet<IntPtr>();
        EnumChildWindows(
            handle,
            (childHandle, lParam) =>
            {
                if (!IsOverlayWindowOrDescendant(childHandle) &&
                    seenRendererWindows.Add(childHandle))
                {
                    rendererWindows.Add(new RendererWindow(childHandle, IntPtr.Zero));
                }

                return true;
            },
            IntPtr.Zero);
        AddOwnedRendererWindows(rendererWindows, seenRendererWindows);

        rendererWindows.Sort((left, right) => GetWindowDepth(left.Handle).CompareTo(GetWindowDepth(right.Handle)));
        foreach (var rendererWindow in rendererWindows)
        {
            var styleChanged = NormalizeRendererChildWindowStyles(rendererWindow.Handle, rendererWindow.DesiredParent);
            var parent = GetParent(rendererWindow.Handle);
            if (parent == IntPtr.Zero)
            {
                continue;
            }

            // VLC owns the layout and presentation of its inner video HWND. Resizing
            // that HWND here can reset a Direct3D swap chain between frames. Only
            // size the renderer container; VLC arranges its descendants when ready
            // to present at the new size. Style normalization still applies to all.
            if (parent != handle)
            {
                if (styleChanged)
                {
                    _ = SetWindowPos(rendererWindow.Handle, IntPtr.Zero, 0, 0, 0, 0,
                        SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
                }
                continue;
            }

            var width = 0;
            var height = 0;
            if (GetClientRect(parent, out var parentClient))
            {
                width = parentClient.Right - parentClient.Left;
                height = parentClient.Bottom - parentClient.Top;
            }
            else if (parent == handle)
            {
                width = hostFallbackWidth;
                height = hostFallbackHeight;
            }

            if (width <= 0 || height <= 0)
            {
                continue;
            }

            _ = SetWindowPos(
                rendererWindow.Handle,
                IntPtr.Zero,
                0,
                0,
                width,
                height,
                SwpNoZOrder | SwpNoActivate | SwpShowWindow | (styleChanged ? SwpFrameChanged : 0));
        }
    }

    private void AddOwnedRendererWindows(List<RendererWindow> rendererWindows, HashSet<IntPtr> seenRendererWindows)
    {
        var currentProcessId = Environment.ProcessId;
        EnumWindows(
            (windowHandle, lParam) =>
            {
                _ = GetWindowThreadProcessId(windowHandle, out var windowProcessId);
                if (windowProcessId == currentProcessId &&
                    TryGetOwnedRendererParent(windowHandle, out var desiredParent) &&
                    seenRendererWindows.Add(windowHandle))
                {
                    rendererWindows.Add(new RendererWindow(windowHandle, desiredParent));
                    EnumChildWindows(
                        windowHandle,
                        (childHandle, childParam) =>
                        {
                            if (!IsOverlayWindowOrDescendant(childHandle) &&
                                seenRendererWindows.Add(childHandle))
                            {
                                rendererWindows.Add(new RendererWindow(childHandle, IntPtr.Zero));
                            }

                            return true;
                        },
                        IntPtr.Zero);
                }

                return true;
            },
            IntPtr.Zero);
    }

    private bool TryGetOwnedRendererParent(IntPtr windowHandle, out IntPtr desiredParent)
    {
        desiredParent = IntPtr.Zero;
        if (windowHandle == IntPtr.Zero || windowHandle == handle)
        {
            return false;
        }

        var style = GetWindowLong(windowHandle, GwlStyle);
        if ((style & WsChild) != 0)
        {
            return false;
        }

        for (var owner = GetWindow(windowHandle, GwOwner);
             owner != IntPtr.Zero;
             owner = GetWindow(owner, GwOwner))
        {
            if (owner == handle)
            {
                desiredParent = handle;
                return true;
            }

            if (IsRendererWindowOrDescendant(owner))
            {
                desiredParent = owner;
                return true;
            }
        }

        var hostRoot = GetAncestor(handle, GaRoot);
        if (hostRoot != IntPtr.Zero &&
            GetAncestor(windowHandle, GaRootOwner) == hostRoot &&
            IsLikelyVlcRendererWindow(windowHandle))
        {
            desiredParent = handle;
            return true;
        }

        return false;
    }

    private static bool IsLikelyVlcRendererWindow(IntPtr windowHandle)
    {
        var className = ReadNativeWindowClassName(windowHandle);
        if (className.Contains("VLC", StringComparison.OrdinalIgnoreCase) &&
            (className.Contains("video", StringComparison.OrdinalIgnoreCase) ||
             className.Contains("Direct", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var title = ReadNativeWindowText(windowHandle);
        return title.Contains("VLC", StringComparison.OrdinalIgnoreCase) &&
            title.Contains("output", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadNativeWindowClassName(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(windowHandle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : "";
    }

    private static string ReadNativeWindowText(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(256);
        var length = GetWindowText(windowHandle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : "";
    }

    private bool IsRendererWindowOrDescendant(IntPtr windowHandle)
    {
        for (var current = windowHandle; current != IntPtr.Zero; current = GetParent(current))
        {
            if (current == handle)
            {
                return true;
            }

            if (overlayWindows.Contains(current))
            {
                return false;
            }
        }

        return false;
    }

    private static bool NormalizeRendererChildWindowStyles(IntPtr hwnd, IntPtr desiredParent)
    {
        var changed = false;
        var style = GetWindowLong(hwnd, GwlStyle);
        var wasChild = (style & WsChild) != 0;
        var normalizedStyle = (style | WsChild | WsClipChildren | WsClipSiblings) & ~WsPopup;
        if (normalizedStyle != style)
        {
            SetWindowLong(hwnd, GwlStyle, normalizedStyle);
            changed = true;
        }

        var extendedStyle = GetWindowLong(hwnd, GwlExStyle);
        var normalizedExtendedStyle = (extendedStyle | CaptureFriendlyChildExtendedStyle) & ~WsExAppWindow;
        if (normalizedExtendedStyle != extendedStyle)
        {
            SetWindowLong(hwnd, GwlExStyle, normalizedExtendedStyle);
            changed = true;
        }

        if (desiredParent != IntPtr.Zero &&
            (!wasChild || GetParent(hwnd) != desiredParent))
        {
            _ = SetParent(hwnd, desiredParent);
            changed = true;
        }

        return changed;
    }

    private void ResizeRendererWindowsToClient(int fallbackWidth = 0, int fallbackHeight = 0)
    {
        var width = fallbackWidth;
        var height = fallbackHeight;
        if (handle != IntPtr.Zero && GetClientRect(handle, out var clientRect))
        {
            width = clientRect.Right - clientRect.Left;
            height = clientRect.Bottom - clientRect.Top;
        }

        if (width > 0 && height > 0)
        {
            ResizeRendererWindows(width, height);
            // A late-created VLC child can also change sibling z-order without changing
            // the client size. Overlay hosts restore their bounds/z-order after this pass.
            RaiseNativeBoundsChanged();
        }
    }

    private bool IsOverlayWindowOrDescendant(IntPtr childHandle)
    {
        for (var current = childHandle; current != IntPtr.Zero && current != handle; current = GetParent(current))
        {
            if (overlayWindows.Contains(current))
            {
                return true;
            }
        }

        return false;
    }

    private int GetWindowDepth(IntPtr childHandle)
    {
        var depth = 0;
        for (var current = childHandle; current != IntPtr.Zero && current != handle; current = GetParent(current))
        {
            depth++;
        }

        return depth;
    }

    private void RaiseNativeBoundsChanged()
    {
        if (handle == IntPtr.Zero || notifyingNativeBoundsChanged)
        {
            return;
        }

        notifyingNativeBoundsChanged = true;
        try
        {
            NativeBoundsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            notifyingNativeBoundsChanged = false;
        }
    }

    private static void EnsureWindowClassRegistered()
    {
        lock (WindowClassGate)
        {
            if (windowClassRegistered)
            {
                return;
            }

            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                Style = CsDoubleClicks,
                WindowProc = Marshal.GetFunctionPointerForDelegate(RegisteredWindowProc),
                Instance = GetModuleHandle(null),
                BackgroundBrush = GetStockObject(BlackBrush),
                ClassName = VideoSurfaceWindowClassName
            };

            var atom = RegisterClassEx(ref windowClass);
            if (atom == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorClassAlreadyExists)
                {
                    throw new Win32Exception(error, "Failed to register the video surface window class.");
                }
            }

            windowClassRegistered = true;
        }
    }

    private static void FillClientArea(IntPtr hwnd, IntPtr deviceContext)
    {
        if (deviceContext == IntPtr.Zero ||
            !GetClientRect(hwnd, out var clientRect))
        {
            return;
        }

        _ = FillRect(deviceContext, ref clientRect, GetStockObject(BlackBrush));
    }

    private static IntPtr DefWindowProcCallback(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProc(hwnd, msg, wParam, lParam);

    private bool IsLeftButtonDoubleClick(IntPtr lParam) =>
        doubleClickTracker.IsDoubleClick(GetLParamX(lParam), GetLParamY(lParam));

    private void CaptureLastLeftButtonDown(IntPtr lParam) =>
        doubleClickTracker.Capture(GetLParamX(lParam), GetLParamY(lParam));

    private void ResetLastLeftButtonDown() => doubleClickTracker.Reset();

    private static int GetWheelDelta(IntPtr wParam)
    {
        var value = unchecked((long)wParam);
        return unchecked((short)((value >> 16) & 0xFFFF));
    }

    private static int GetLParamX(IntPtr lParam)
    {
        var value = unchecked((long)lParam);
        return unchecked((short)(value & 0xFFFF));
    }

    private static int GetLParamY(IntPtr lParam)
    {
        var value = unchecked((long)lParam);
        return unchecked((short)((value >> 16) & 0xFFFF));
    }

    private static int GetLowWord(IntPtr value) => unchecked((ushort)(value.ToInt64() & 0xFFFF));

    private delegate IntPtr NativeWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumChildWindowProc(IntPtr hwnd, IntPtr lParam);

    private readonly record struct RendererWindow(IntPtr Handle, IntPtr DesiredParent);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProc;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        public IntPtr Hwnd;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [LibraryImport("user32", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [LibraryImport("user32")]
    private static partial IntPtr SetCapture(IntPtr hwnd);

    [LibraryImport("user32")]
    private static partial IntPtr GetCapture();

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(IntPtr parentHandle, EnumChildWindowProc callback, IntPtr lParam);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumChildWindowProc callback, IntPtr lParam);

    [LibraryImport("user32")]
    private static partial IntPtr GetParent(IntPtr hwnd);

    [LibraryImport("user32")]
    private static partial IntPtr GetWindow(IntPtr hwnd, uint command);

    [LibraryImport("user32")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [LibraryImport("user32", SetLastError = true)]
    private static partial IntPtr SetParent(IntPtr childHandle, IntPtr newParentHandle);

    [DllImport("user32", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [LibraryImport("user32", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong(IntPtr hwnd, int index);

    [LibraryImport("user32", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong(IntPtr hwnd, int index, int value);

    [LibraryImport("user32")]
    private static partial int FillRect(IntPtr hdc, ref NativeRect rect, IntPtr brush);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr GetModuleHandle(string? moduleName);

    [LibraryImport("gdi32")]
    private static partial IntPtr GetStockObject(int objectType);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);
}

public sealed class VideoSurfaceMouseWheelEventArgs : EventArgs
{
    public VideoSurfaceMouseWheelEventArgs(int delta, Point? screenPoint = null)
    {
        Delta = delta;
        ScreenPoint = screenPoint;
    }

    public int Delta { get; }
    public Point? ScreenPoint { get; }
}

public sealed class VideoSurfaceNativeMouseEventArgs : EventArgs
{
    public VideoSurfaceNativeMouseEventArgs(int screenX, int screenY)
    {
        ScreenX = screenX;
        ScreenY = screenY;
    }

    public int ScreenX { get; }
    public int ScreenY { get; }
    public bool Handled { get; set; }
    public IntPtr Result { get; set; }
}
