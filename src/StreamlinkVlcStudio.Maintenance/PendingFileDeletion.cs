using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StreamlinkVlcStudio.Maintenance;

/// <summary>
/// Holds deletion access exclusively until every uninstall control file is ready.
/// Pending deletion can be canceled before closing the handle if the batch fails.
/// </summary>
internal sealed partial class PendingFileDeletion : IDisposable
{
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;
    private readonly SafeFileHandle handle;
    private bool pending;

    private PendingFileDeletion(string path, SafeFileHandle handle)
    {
        Path = path;
        this.handle = handle;
    }

    internal string Path { get; }

    internal static unsafe PendingFileDeletion Open(string path)
    {
        var handle = CreateFile(path, DeleteAccess, FileShare.None, IntPtr.Zero,
            OpenExisting, OpenReparsePoint, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw NativeFailure("reserve deletion access to", path);
            AttributeTagInformation information;
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &information, (uint)sizeof(AttributeTagInformation)))
                throw NativeFailure("inspect the deletion handle for", path);
            if (!PathSafety.IsPlainFile(information.Attributes))
                throw new IOException($"Refusing a directory or reparse point as an uninstall control file: {path}");
            return new PendingFileDeletion(path, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void MarkForDeletion()
    {
        byte disposition = 1;
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, in disposition, 1))
            throw NativeFailure("mark for deletion", Path);
        pending = true;
    }

    internal void CancelDeletion()
    {
        if (!pending) return;
        byte disposition = 0;
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, in disposition, 1))
            throw NativeFailure("cancel pending deletion of", Path);
        pending = false;
    }

    public void Dispose() => handle.Dispose();

    private static IOException NativeFailure(string operation, string path)
    {
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        return new IOException($"Could not {operation} '{path}'. {error.Message}", error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInformation
    {
        public FileAttributes Attributes;
        public uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, FileShare share,
        IntPtr securityAttributes, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        AttributeTagInformation* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        in byte information, uint size);
}
