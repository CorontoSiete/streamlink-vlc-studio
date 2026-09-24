using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.Maintenance;

internal static partial class NativeDialog
{
    private const uint MbOk = 0x00000000;
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconError = 0x00000010;
    private const uint MbIconQuestion = 0x00000020;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbDefaultButton2 = 0x00000100;
    private const int IdYes = 6;

    internal static bool Confirm(string message)
    {
        return MessageBox(
            IntPtr.Zero,
            message,
            "Streamlink VLC Studio Maintenance",
            MbYesNo | MbIconQuestion | MbDefaultButton2) == IdYes;
    }

    internal static void ShowInformation(string message)
    {
        _ = MessageBox(
            IntPtr.Zero,
            message,
            "Streamlink VLC Studio Maintenance",
            MbOk | MbIconInformation);
    }

    internal static void ShowError(string message)
    {
        _ = MessageBox(
            IntPtr.Zero,
            message,
            "Streamlink VLC Studio Maintenance",
            MbOk | MbIconError);
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr window, string text, string caption, uint type);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ScheduleDeleteOnReboot(string existingFileName, string? newFileName, uint flags);
}
