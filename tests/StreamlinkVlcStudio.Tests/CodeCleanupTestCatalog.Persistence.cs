internal static partial class CodeCleanupTestCatalog
{
    private static async Task OverlayPreparationDiagnosticsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"svs-overlay-diagnostics-{Guid.NewGuid():N}");
        var vlcDirectory = Path.Combine(root, "vlc");
        var overlayDirectory = Path.Combine(root, "overlay");
        Directory.CreateDirectory(vlcDirectory);
        Directory.CreateDirectory(Path.Combine(overlayDirectory, VlcOverlayDirectoryResolver.BuildDirectoryName));
        try
        {
            File.WriteAllBytes(Path.Combine(vlcDirectory, "libvlc.dll"), [1, 2, 3]);
            File.WriteAllBytes(VlcOverlayDirectoryResolver.GetPluginPath(overlayDirectory), [4, 5, 6]);
            File.WriteAllBytes(VlcOverlayDirectoryResolver.GetControllerPath(overlayDirectory), [7, 8, 9]);
            var runtime = await VlcOverlayPluginRuntimeFactory.TryPrepareAsync(vlcDirectory, overlayDirectory,
                MemoryLogger.WithWriteFailure(), appDataDirectory: Path.Combine(root, "state"));
            Assert.NotNull(runtime);
            Assert.True(File.Exists(runtime!.PluginPath));
            Assert.True(File.Exists(runtime.ControllerPath));
            Assert.Equal(false, File.Exists(Path.Combine(runtime.PluginRoot, "plugins.dat")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task OverlaySizingOverflowAsync()
    {
        Assert.Equal((NativeOverlaySizing.MaxWidth, NativeOverlaySizing.MaxHeight),
            NativeOverlaySizing.NormalizeToReferenceSize(int.MaxValue, int.MaxValue, 1080));
        Assert.Equal((NativeOverlaySizing.MaxWidth, NativeOverlaySizing.MaxHeight),
            NativeOverlaySizing.NormalizeToReferenceSize(int.MaxValue, int.MaxValue, 1));
        Assert.Equal((1080, 1080),
            NativeOverlaySizing.NormalizeToReferenceSize(int.MaxValue, int.MaxValue, int.MaxValue));
        Assert.Equal((NativeOverlaySizing.MinWidth, NativeOverlaySizing.MinHeight),
            NativeOverlaySizing.NormalizeToReferenceSize(int.MinValue, int.MinValue, 1080));
        Assert.Equal((500, 600), NativeOverlaySizing.NormalizeToReferenceSize(1000, 1200, 2160));
        return Task.CompletedTask;
    }

    private static Task OverlayStateBoundsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"svs-overlay-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "overlay.size");
        try
        {
            foreach (var invalidPath in new[] { "", " ", "\0" })
            {
                Assert.Equal(false, NativeOverlaySizing.TryReadIntFile(invalidPath, out _));
                Assert.Equal(false, NativeOverlaySizing.TryReadSizeFile(invalidPath, out _, out _, out _));
            }

            Assert.Equal(false, NativeOverlaySizing.TryReadIntFile(path, out _));
            File.WriteAllText(path, "");
            Assert.Equal(false, NativeOverlaySizing.TryReadSizeFile(path, out _, out _, out _));

            File.WriteAllText(path, "400 700 reference", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Assert.True(NativeOverlaySizing.TryReadSizeFile(path, out var width, out var height, out var reference));
            Assert.Equal((400, 700, true), (width, height, reference));
            File.WriteAllText(path, "-12:34,1");
            Assert.True(NativeOverlaySizing.TryReadIntFile(path, out var position));
            Assert.SequenceEqual(new[] { -12, 34, 1 }, position);

            File.WriteAllText(path, "400 700".PadRight(4096));
            Assert.True(NativeOverlaySizing.TryReadSizeFile(path, out width, out height, out reference));
            Assert.Equal((400, 700, false), (width, height, reference));
            File.AppendAllText(path, " ");
            Assert.Equal(false, NativeOverlaySizing.TryReadSizeFile(path, out width, out height, out reference));
            Assert.Equal((0, 0, false), (width, height, reference));
            Assert.Equal(false, NativeOverlaySizing.TryReadIntFile(path, out position));
            Assert.Equal(0, position.Length);
        }
        finally { Directory.Delete(root, recursive: true); }

        return Task.CompletedTask;
    }
}
