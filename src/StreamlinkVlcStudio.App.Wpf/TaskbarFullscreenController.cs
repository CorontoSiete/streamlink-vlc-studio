using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.App.Wpf;

internal interface ITaskbarFullscreenController
{
    bool TrySetFullscreen(IntPtr windowHandle, bool fullscreen);
}

internal sealed class WindowsTaskbarFullscreenController : ITaskbarFullscreenController
{
    private static readonly Guid TaskbarListClassId = new("56FDF344-FD6D-11D0-958A-006097C9A090");

    public static WindowsTaskbarFullscreenController Instance { get; } = new();

    private WindowsTaskbarFullscreenController()
    {
    }

    public bool TrySetFullscreen(IntPtr windowHandle, bool fullscreen)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        ITaskbarList2? taskbarList = null;
        try
        {
            var taskbarListType = Type.GetTypeFromCLSID(TaskbarListClassId);
            if (taskbarListType is null)
            {
                return false;
            }

            taskbarList = Activator.CreateInstance(taskbarListType) as ITaskbarList2;
            if (taskbarList is null)
            {
                return false;
            }

            taskbarList.HrInit();
            taskbarList.MarkFullscreenWindow(windowHandle, fullscreen);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        finally
        {
            if (taskbarList is not null && Marshal.IsComObject(taskbarList))
            {
                Marshal.FinalReleaseComObject(taskbarList);
            }
        }
    }

    [ComImport]
    [Guid("602D4995-B13A-429B-A66E-1935E44F4317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        void HrInit();

        void AddTab(IntPtr windowHandle);

        void DeleteTab(IntPtr windowHandle);

        void ActivateTab(IntPtr windowHandle);

        void SetActiveAlt(IntPtr windowHandle);

        void MarkFullscreenWindow(IntPtr windowHandle, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    }
}
