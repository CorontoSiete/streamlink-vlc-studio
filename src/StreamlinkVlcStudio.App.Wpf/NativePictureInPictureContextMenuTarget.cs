using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.App.Wpf;

/// <summary>Retains the PiP that owned a right click before it leaves the native hook thread.</summary>
internal static partial class NativePictureInPictureContextMenuTarget
{
    private static readonly ConcurrentDictionary<IntPtr, Registration> Windows = new();

    internal static void RegisterWindow(IntPtr hwnd, Action<LowLevelMouseHookEvent> openMenu)
    {
        ArgumentNullException.ThrowIfNull(openMenu);
        if (hwnd != IntPtr.Zero)
        {
            Windows[hwnd] = new Registration(hwnd, openMenu);
        }
    }

    internal static void RegisterAlias(IntPtr alias, IntPtr owner)
    {
        if (alias != IntPtr.Zero && Windows.TryGetValue(owner, out var registration) &&
            registration.Window == owner)
        {
            Windows[alias] = registration;
        }
    }

    internal static void UnregisterWindow(IntPtr hwnd) => Windows.TryRemove(hwnd, out _);

    internal static Action? CaptureRoute(LowLevelMouseHookEvent hookEvent)
    {
        var target = WindowFromPoint(new WindowPoint { X = hookEvent.ScreenX, Y = hookEvent.ScreenY });
        var root = GetAncestor(target, 2); // GA_ROOT includes VLC/seekbar children, but not owned menus.
        if (root == IntPtr.Zero || GetWindowThreadProcessId(root, out var processId) == 0 ||
            processId != (uint)Environment.ProcessId || !IsWindowEnabled(root) ||
            !Windows.TryGetValue(root, out var registration) || !IsCurrent(registration))
        {
            return null;
        }

        var ownerThreadId = GetWindowThreadProcessId(registration.Window, out var ownerProcessId);
        if (ownerThreadId == 0 || ownerProcessId != processId)
        {
            return null;
        }

        return () =>
        {
            // Do not hit-test again after a UI stall: the pointer, z-order, or an OSD
            // may have changed. Validate the stable PiP owner even if its OSD expired.
            // A closed/re-registered PiP must not inherit old input.
            if (IsCurrent(registration) &&
                GetWindowThreadProcessId(registration.Window, out var currentProcessId) == ownerThreadId &&
                currentProcessId == ownerProcessId)
            {
                registration.OpenMenu(hookEvent);
            }
        };
    }

    private static bool IsCurrent(Registration registration) =>
        Windows.TryGetValue(registration.Window, out var current) && ReferenceEquals(current, registration) &&
        IsWindowEnabled(registration.Window);

    private sealed record Registration(IntPtr Window, Action<LowLevelMouseHookEvent> OpenMenu);

    [LibraryImport("user32")]
    private static partial IntPtr WindowFromPoint(WindowPoint point);

    [LibraryImport("user32")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowEnabled(IntPtr hwnd);
}
