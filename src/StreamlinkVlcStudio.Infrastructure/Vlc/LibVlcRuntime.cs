using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

/// <summary>
/// Keeps a compatible libVLC instance alive until every playback engine using it has released its
/// lease. Media players and their HWNDs remain owned by individual playback engines.
/// </summary>
internal sealed class ReferenceCountedRuntimeRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeLease Acquire(string key, Func<IntPtr> create, Action<IntPtr> release)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(release);

        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                existing.ReferenceCount++;
                return new RuntimeLease(existing.Instance, () => Release(key, existing, release));
            }

            var instance = create();
            if (instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("libVLC failed to initialize.");
            }

            var entry = new Entry(instance);
            entries.Add(key, entry);
            return new RuntimeLease(instance, () => Release(key, entry, release));
        }
    }

    internal int EntryCount
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    internal int GetReferenceCount(string key)
    {
        lock (gate)
        {
            return entries.TryGetValue(key, out var entry) ? entry.ReferenceCount : 0;
        }
    }

    private void Release(string key, Entry entry, Action<IntPtr> release)
    {
        IntPtr instanceToRelease = IntPtr.Zero;
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            current.ReferenceCount--;
            if (current.ReferenceCount > 0)
            {
                return;
            }

            entries.Remove(key);
            instanceToRelease = current.Instance;
        }

        release(instanceToRelease);
    }

    private sealed class Entry
    {
        public Entry(IntPtr instance)
        {
            Instance = instance;
            ReferenceCount = 1;
        }

        public IntPtr Instance { get; }
        public int ReferenceCount { get; set; }
    }
}

internal sealed class RuntimeLease : IDisposable
{
    private Action? release;

    public RuntimeLease(IntPtr instance, Action release)
    {
        Instance = instance;
        this.release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public IntPtr Instance { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref release, null)?.Invoke();
    }
}

internal static class LibVlcRuntime
{
    private static readonly ReferenceCountedRuntimeRegistry SharedRuntimes = new();

    internal static RuntimeLease Acquire(
        string vlcDirectory,
        VideoRendererMode rendererMode,
        IReadOnlyList<string> options,
        bool share,
        string? compatibilityKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vlcDirectory);
        ArgumentNullException.ThrowIfNull(options);

        if (!share)
        {
            return CreateIsolated(options);
        }

        var key = BuildSharedRuntimeKey(vlcDirectory, rendererMode, options, compatibilityKey);
        return SharedRuntimes.Acquire(
            key,
            () => LibVlcNative.CreateInstance(options),
            LibVlcNative.libvlc_release);
    }

    internal static bool ShouldShareRuntime(bool usesNativeOverlay)
    {
        // The overlay's sub-source is configured when the libVLC instance is created and
        // contains a pipe name unique to one playback engine. Sharing that instance would
        // either omit the overlay or route multiple tabs through the wrong pipe.
        return !usesNativeOverlay;
    }

    internal static string BuildSharedRuntimeKey(
        string vlcDirectory,
        VideoRendererMode rendererMode,
        IReadOnlyList<string> options,
        string? compatibilityKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vlcDirectory);
        ArgumentNullException.ThrowIfNull(options);

        var normalizedDirectory = NormalizeDirectory(vlcDirectory);
        var normalizedCompatibilityKey = compatibilityKey?.Trim() ?? "";
        var normalizedOptions = string.Join('\u001F', options);
        return $"{normalizedDirectory}|{rendererMode}|{normalizedCompatibilityKey}|{normalizedOptions}";
    }

    private static string NormalizeDirectory(string vlcDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vlcDirectory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(vlcDirectory));
    }

    private static RuntimeLease CreateIsolated(IReadOnlyList<string> options)
    {
        var instance = LibVlcNative.CreateInstance(options);
        if (instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("libVLC failed to initialize.");
        }

        return new RuntimeLease(instance, () => LibVlcNative.libvlc_release(instance));
    }
}
