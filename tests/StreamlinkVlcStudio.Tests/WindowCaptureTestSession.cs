using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.18362")]
internal sealed class WindowCaptureTestSession : IDisposable
{
    private readonly IDirect3DDevice device;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;

    internal WindowCaptureTestSession(IntPtr hwnd)
    {
        device = CreateDevice();
        try
        {
            item = CreateItem(hwnd);
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            try
            {
                session = pool.CreateCaptureSession(item);
                session.StartCapture();
            }
            catch { pool.Dispose(); throw; }
        }
        catch { device.Dispose(); throw; }
    }

    internal async Task<System.Drawing.Bitmap> NextFrameAsync(bool continuous = false)
    {
        // Discard queued frames so a transition assertion cannot inspect an old frame.
        if (!continuous)
            while (pool.TryGetNextFrame() is { } stale) stale.Dispose();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null) { await Task.Delay(continuous ? 1 : 25); continue; }
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            var length = checked(bitmap.PixelWidth * bitmap.PixelHeight * 4);
            var buffer = new Windows.Storage.Streams.Buffer((uint)length);
            bitmap.CopyToBuffer(buffer);
            var pixels = new byte[length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            var result = new System.Drawing.Bitmap(bitmap.PixelWidth, bitmap.PixelHeight,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = result.LockBits(new System.Drawing.Rectangle(0, 0, result.Width, result.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, result.PixelFormat);
            try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
            finally { result.UnlockBits(data); }
            if (continuous && (frame.ContentSize.Width != bitmap.PixelWidth || frame.ContentSize.Height != bitmap.PixelHeight))
            {
                var contentSize = frame.ContentSize;
                frame.Dispose();
                pool.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
            }
            return result;
        }
        throw new TimeoutException("Windows Graphics Capture did not produce a new window frame.");
    }

    public void Dispose()
    {
        session.Dispose();
        pool.Dispose();
        device.Dispose();
    }

    internal void Resize() => pool.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);

    private static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var className));
        try
        {
            var iid = typeof(ICaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref iid, out var factory));
            try
            {
                var interop = (ICaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
                try
                {
                    var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                    var pointer = interop.CreateForWindow(hwnd, ref itemId);
                    try { return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer); }
                    finally { Marshal.Release(pointer); }
                }
                finally { Marshal.ReleaseComObject(interop); }
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(className); }
    }

    private static IDirect3DDevice CreateDevice()
    {
        const uint bgraSupport = 0x20;
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, bgraSupport,
            IntPtr.Zero, 0, 7, out var native, out _, out var context));
        try
        {
            var iid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var projected));
                try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(projected); }
                finally { Marshal.Release(projected); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(context); Marshal.Release(native); }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr hwnd, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [DllImport("combase", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string text, int length, out IntPtr value);
    [DllImport("combase")]
    private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase")]
    private static extern int RoGetActivationFactory(IntPtr className, ref Guid iid, out IntPtr factory);
    [DllImport("d3d11")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driver, IntPtr software, uint flags,
        IntPtr levels, uint levelCount, uint version, out IntPtr device, out int level, out IntPtr context);
    [DllImport("d3d11")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);
}
