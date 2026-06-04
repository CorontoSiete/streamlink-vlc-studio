using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.App.Wpf;

internal sealed partial class LowLevelMouseHookPump : IDisposable
{
    private const int HcAction = 0;
    private const int WhMouseLl = 14;
    private const int WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;

    private readonly LowLevelMouseHookDispatcher dispatcher;
    private readonly object gate = new();
    private Thread? hookThread;
    private LowLevelMouseProc? hookCallback;
    private IntPtr hookHandle;
    private int hookThreadId;
    private volatile bool disposed;

    public LowLevelMouseHookPump(LowLevelMouseHookDispatcher dispatcher)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Start()
    {
        lock (gate)
        {
            if (disposed || hookThread is not null)
            {
                return;
            }

            hookThread = new Thread(RunHookThread)
            {
                IsBackground = true,
                Name = "Streamlink VLC Studio mouse hook"
            };
            hookThread.SetApartmentState(ApartmentState.STA);
            hookThread.Start();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        int threadId;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            thread = hookThread;
            threadId = hookThreadId;
        }

        if (thread is not null)
        {
            if (threadId != 0)
            {
                _ = PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            }

            if (!ReferenceEquals(thread, Thread.CurrentThread))
            {
                _ = thread.Join(TimeSpan.FromSeconds(1));
            }
        }
    }

    private void RunHookThread()
    {
        try
        {
            hookThreadId = (int)GetCurrentThreadId();
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);

            hookCallback = HookCallback;
            hookHandle = SetWindowsHookEx(WhMouseLl, hookCallback, GetModuleHandle(null), 0);

            if (hookHandle == IntPtr.Zero)
            {
                return;
            }

            while (!disposed && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            var handle = hookHandle;
            hookHandle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(handle);
            }

            hookCallback = null;
            hookThreadId = 0;
            lock (gate)
            {
                hookThread = null;
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == HcAction)
            {
                var hook = Marshal.PtrToStructure<MouseLowLevelHookStruct>(lParam);
                var hookEvent = new LowLevelMouseHookEvent(
                    wParam.ToInt32(),
                    hook.Point.X,
                    hook.Point.Y,
                    hook.MouseData);

                if (dispatcher.ProcessEvent(hookEvent))
                {
                    return new IntPtr(1);
                }
            }
        }
        catch (Exception)
        {
        }

        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseLowLevelHookStruct
    {
        public readonly NativePoint Point;
        public readonly int MouseData;
        public readonly int Flags;
        public readonly int Time;
        public readonly IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [LibraryImport("user32", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hmod, int dwThreadId);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32")]
    private static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref NativeMessage lpMsg);

    [LibraryImport("user32", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessage(ref NativeMessage lpMsg);

    [LibraryImport("user32", EntryPoint = "PeekMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(int idThread, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr GetModuleHandle(string? lpModuleName);
}
