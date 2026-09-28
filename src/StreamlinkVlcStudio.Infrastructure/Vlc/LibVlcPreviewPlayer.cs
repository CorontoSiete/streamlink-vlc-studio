using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

/// <summary>Owns a memory-video player, its aligned plane, and its native callback roots.</summary>
internal sealed class LibVlcPreviewPlayer : IDisposable
{
    private readonly IntPtr memory;
    private readonly IntPtr buffer;
    private readonly int byteCount;
    private readonly SemaphoreSlim frameGate = new(1, 1);
    private readonly LibVlcNative.PreviewLockCallback lockFrame;
    private readonly LibVlcNative.PreviewUnlockCallback unlockFrame;
    private readonly LibVlcNative.PreviewDisplayCallback displayFrame;
    private readonly TaskCompletionSource callbackFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr media;
    private IntPtr player;
    private bool disposed;

    internal LibVlcPreviewPlayer(IntPtr instance, Uri uri, int width, int height,
        Func<bool> shouldCapture, Action<byte[]> present, bool captureOnDecode = false)
    {
        var pitch = checked(width * 4);
        byteCount = checked(pitch * height);
        // VLC requires aligned planes and may pad the final scanline block.
        memory = Marshal.AllocHGlobal(checked(pitch * ((height + 31) & ~31) + 31));
        buffer = new IntPtr((memory.ToInt64() + 31) & ~31L);
        lockFrame = (_, planes) =>
        {
            frameGate.Wait();
            Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        };
        unlockFrame = (_, _, _) =>
        {
            try { if (captureOnDecode) Capture(locked: true); }
            finally { frameGate.Release(); }
        };
        displayFrame = (_, _) => Capture(locked: false);
        try
        {
            media = LibVlcNative.libvlc_media_new_location(instance, uri.AbsoluteUri);
            if (media == IntPtr.Zero) throw new InvalidOperationException("VLC could not open the preview.");
            LibVlcNative.libvlc_media_add_option(media, ":no-audio");
            player = LibVlcNative.libvlc_media_player_new_from_media(media);
            if (player == IntPtr.Zero) throw new InvalidOperationException("VLC could not create the preview player.");
            LibVlcNative.libvlc_video_set_callbacks(player, lockFrame, unlockFrame,
                captureOnDecode ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(displayFrame), IntPtr.Zero);
            LibVlcNative.libvlc_video_set_format(player, "RV32", (uint)width, (uint)height, (uint)pitch);
        }
        catch { Dispose(); throw; }

        void Capture(bool locked)
        {
            try
            {
                if (!shouldCapture()) return;
                var pixels = new byte[byteCount];
                if (!locked) frameGate.Wait();
                try { Marshal.Copy(buffer, pixels, 0, pixels.Length); }
                finally { if (!locked) frameGate.Release(); }
                present(pixels);
            }
            // Exceptions cannot cross the unmanaged callback boundary.
            catch (Exception ex) { callbackFailure.TrySetException(ex); }
        }
    }

    internal Task CallbackFailure => callbackFailure.Task;
    internal LibVlcNative.MediaPlayerState State => LibVlcNative.libvlc_media_player_get_state(player);
    internal bool Start() => LibVlcNative.libvlc_media_player_play(player) == 0;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // stop joins native playback and callbacks. Neither the plane, the semaphore,
        // nor the delegates may be released until this completes.
        if (player != IntPtr.Zero)
        {
            LibVlcNative.libvlc_media_player_stop(player);
            LibVlcNative.libvlc_media_player_release(player);
            player = IntPtr.Zero;
        }
        if (media != IntPtr.Zero) { LibVlcNative.libvlc_media_release(media); media = IntPtr.Zero; }
        Marshal.FreeHGlobal(memory);
        frameGate.Dispose();
        GC.KeepAlive(lockFrame);
        GC.KeepAlive(unlockFrame);
        GC.KeepAlive(displayFrame);
    }
}
