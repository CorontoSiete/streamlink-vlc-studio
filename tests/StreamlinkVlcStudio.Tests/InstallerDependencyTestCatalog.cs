using StreamStudio.Installation;

internal static class InstallerDependencyTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("installer dependencies: architecture probes reject x86 ARM64 corrupt and truncated executables", ArchitectureValidation),
        ("installer dependencies: version probes reject partial malformed and overflowing values", VersionValidation),
        ("installer dependencies: WebView2 detection requires matching x64 browser and core files", WebView2FileValidation),
        ("installer dependencies: VLC detection rejects incomplete plugins and libraries without VLC exports", VlcFileValidation),
        ("installer dependencies: application finds registered custom VLC paths and retains environment selection", VlcDirectorySelection),
        ("installer dependencies: VLC output modules cannot conceal missing playback modules with the same name", VlcModuleCapabilityValidation),
        ("installer dependencies: malformed maintenance commands fail without normal application startup", MalformedMaintenanceArguments)
    ];

    private static string NewFixtureDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudio-probe-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] VersionedX64Image()
    {
        var executable = typeof(ApplicationDependencyVerifier).Assembly.Location;
        Assert.True(WindowsDependencyProbe.IsX64PortableExecutable(executable));
        return File.ReadAllBytes(executable);
    }

    private static Task ArchitectureValidation()
    {
        var directory = NewFixtureDirectory();
        try
        {
            var valid = VersionedX64Image();
            var path = Path.Combine(directory, "candidate.exe");
            File.WriteAllBytes(path, valid);
            Assert.True(WindowsDependencyProbe.IsX64PortableExecutable(path));
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(valid.AsSpan(0x3c, 4));
            foreach (var architecture in new ushort[] { 0x014c, 0xaa64 })
            {
                var modified = valid.ToArray();
                BinaryPrimitives.WriteUInt16LittleEndian(modified.AsSpan(peOffset + 4, 2), architecture);
                File.WriteAllBytes(path, modified);
                Assert.Equal(false, WindowsDependencyProbe.IsX64PortableExecutable(path));
            }
            var invalid = valid.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(peOffset + 24, 2), 0x10b);
            File.WriteAllBytes(path, invalid);
            Assert.Equal(false, WindowsDependencyProbe.IsX64PortableExecutable(path));
            foreach (var offset in new[] { -1, 0, int.MaxValue })
            {
                invalid = valid.ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(0x3c, 4), offset);
                File.WriteAllBytes(path, invalid);
                Assert.Equal(false, WindowsDependencyProbe.IsX64PortableExecutable(path));
            }
            File.WriteAllBytes(path, valid[..32]);
            Assert.Equal(false, WindowsDependencyProbe.IsX64PortableExecutable(path));
            File.WriteAllText(path, "not a PE file");
            Assert.Equal(false, WindowsDependencyProbe.IsX64PortableExecutable(path));
            File.Delete(path);
            Assert.Equal(false, WindowsDependencyProbe.IsX64PortableExecutable(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task VersionValidation()
    {
        Assert.Equal("8.5.0.0", WindowsDependencyProbe.NormalizeVersion("8.5.0-1"));
        Assert.Equal("152.0.4191.53", WindowsDependencyProbe.NormalizeVersion("v152.0.4191.53"));
        Assert.Equal("3.0.0.0", WindowsDependencyProbe.NormalizeVersion("3.0"));
        foreach (var value in new[] { "", "8", "prefix 8.5.0", "8.5.0 beta", "8.5.0-preview", "8.5.0.1.2", "99999999999999.0", " 8.5.0 " })
            Assert.Equal("", WindowsDependencyProbe.NormalizeVersion(value));
        return Task.CompletedTask;
    }

    private static Task WebView2FileValidation()
    {
        var directory = NewFixtureDirectory();
        try
        {
            var image = VersionedX64Image();
            var version = WindowsDependencyProbe.NormalizeVersion(FileVersionInfo.GetVersionInfo(typeof(ApplicationDependencyVerifier).Assembly.Location).FileVersion!);
            Assert.True(version.Length > 0);
            var runtime = Path.Combine(directory, version);
            Directory.CreateDirectory(runtime);
            var browser = Path.Combine(runtime, "msedgewebview2.exe");
            var core = Path.Combine(runtime, "msedge.dll");
            Assert.Equal("", WindowsDependencyProbe.ReadWebView2Installation(directory, version));
            File.WriteAllBytes(browser, image);
            Assert.Equal("", WindowsDependencyProbe.ReadWebView2Installation(directory, version));
            File.WriteAllBytes(core, image);
            Assert.Equal(version, WindowsDependencyProbe.ReadWebView2Installation(directory, version));
            Assert.Equal("", WindowsDependencyProbe.ReadWebView2Installation(directory, "999.0.0.0"));
            Assert.Equal("", WindowsDependencyProbe.ReadWebView2Installation("relative-folder", version));
            Assert.Equal("", WindowsDependencyProbe.ReadWebView2Installation(directory, "0.0.0.0"));
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3c, 4));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4, 2), 0x014c);
            File.WriteAllBytes(core, image);
            Assert.Equal("", WindowsDependencyProbe.ReadWebView2Installation(directory, version));
        }
        finally { Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task VlcFileValidation()
    {
        var directory = NewFixtureDirectory();
        try
        {
            var image = VersionedX64Image();
            Assert.Equal("", WindowsDependencyProbe.ReadVlcVersion(directory));
            File.WriteAllBytes(Path.Combine(directory, "libvlc.dll"), image);
            File.WriteAllBytes(Path.Combine(directory, "libvlccore.dll"), image);
            Assert.Equal("", WindowsDependencyProbe.ReadVlcVersion(directory));
            foreach (var relative in new[]
            {
                "access/libfilesystem_plugin.dll", "access/libhttp_plugin.dll", "access/libhttps_plugin.dll",
                "codec/libavcodec_plugin.dll", "demux/libadaptive_plugin.dll", "demux/libmp4_plugin.dll",
                "demux/libts_plugin.dll", "audio_output/libdirectsound_plugin.dll", "misc/libgnutls_plugin.dll",
                "video_chroma/libswscale_plugin.dll", "video_filter/libadjust_plugin.dll",
                "video_output/libdirect3d11_plugin.dll", "video_output/libwingdi_plugin.dll"
            })
            {
                var path = Path.Combine(directory, "plugins", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, image);
            }
            Assert.Equal("", WindowsDependencyProbe.ReadVlcVersion(directory));
            File.WriteAllText(Path.Combine(directory, "plugins", "demux", "libadaptive_plugin.dll"), "broken plugin");
            Assert.Equal("", WindowsDependencyProbe.ReadVlcVersion(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task MalformedMaintenanceArguments()
    {
        foreach (var args in new[]
        {
            new[] { ApplicationDependencyVerifier.Command, "--streamlink-path" },
            new[] { ApplicationDependencyVerifier.Command, "--vlc-directory", "fixture" },
            new[] { ApplicationDependencyVerifier.Command, "wrong-option", "fixture", "--vlc-directory", "fixture" }
        })
        {
            Assert.True(MaintenanceModeRunner.TryRun(args, out var result));
            Assert.Equal(1, result);
        }
        return Task.CompletedTask;
    }

    private static Task VlcDirectorySelection()
    {
        var directory = NewFixtureDirectory();
        try
        {
            var registered = Path.Combine(directory, "registered-custom-location");
            var fallback = Path.Combine(directory, "default-location");
            var configured = Path.Combine(directory, "environment-location");
            foreach (var candidate in new[] { registered, fallback, configured })
            {
                Directory.CreateDirectory(Path.Combine(candidate, "plugins"));
                File.WriteAllText(Path.Combine(candidate, "libvlc.dll"), "path selection fixture");
            }
            Assert.Equal(registered, ExecutableResolver.FindVlcDirectory(null, () => registered, fallback));
            Assert.Equal(configured, ExecutableResolver.FindVlcDirectory($"\"{Path.Combine(configured, "plugins")}\"",
                () => throw new InvalidOperationException("An explicit environment selection must precede registry lookup."), fallback));
            File.Delete(Path.Combine(registered, "libvlc.dll"));
            Assert.Equal(fallback, ExecutableResolver.FindVlcDirectory(null, () => registered, fallback));
            Assert.Equal<string?>(null, ExecutableResolver.FindVlcDirectory(null, () => registered));
        }
        finally { Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }

    private static Task VlcModuleCapabilityValidation()
    {
        var modules = new HashSet<(string Name, string Capability)> { ("mp4", "sout mux"), ("ts", "demux") };
        try
        {
            ApplicationDependencyVerifier.AssertVlcModule(modules, "mp4", "demux");
            throw new InvalidOperationException("The MP4 output module concealed the missing playback demuxer.");
        }
        catch (InvalidDataException ex) { Assert.Contains("capability 'demux'", ex.Message); }
        modules.Add(("mp4", "demux"));
        ApplicationDependencyVerifier.AssertVlcModule(modules, "mp4", "demux");
        return Task.CompletedTask;
    }
}
