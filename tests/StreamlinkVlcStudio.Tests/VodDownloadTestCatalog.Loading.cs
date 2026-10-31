internal static partial class VodDownloadTestCatalog
{
    private static Task StartupLibraryScanAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var external = Path.Combine(fixture.Root, "previous-target");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "preserve previous files");
        var junction = Path.Combine(fixture.Root, "previous-library");
        try
        {
            var info = BoundedProcessRunner.CreateRedirectedStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-Command",
                    "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:SVS_TEST_JUNCTION_PATH -Target $env:SVS_TEST_JUNCTION_TARGET | Out-Null"]);
            info.Environment["SVS_TEST_JUNCTION_PATH"] = junction;
            info.Environment["SVS_TEST_JUNCTION_TARGET"] = external;
            var result = await new BoundedProcessRunner().RunAsync(info, TimeSpan.FromSeconds(10));
            Assert.True(!result.TimedOut && result.ExitCode == 0, result.StandardError);

            var uiThread = Environment.CurrentManagedThreadId;
            var scanThreads = new List<int>();
            fixture.Logger.EntryWritten += (_, entry) =>
            {
                if (entry.Message.StartsWith("A previous download folder is unavailable", StringComparison.Ordinal))
                    scanThreads.Add(Environment.CurrentManagedThreadId);
            };
            await using var service = new VodDownloadService(fixture.Library, fixture.Resolver, fixture.Logger,
                previousDownloadDirectories: [junction]);
            // Settings apply the format while constructing the main view model. This
            // path must yield before enumerating/validating any library directories.
            await service.ChangeDownloadFormatAsync(VodDownloadFormat.Hls, fixture.Options);
            Assert.Equal(1, scanThreads.Count);
            Console.WriteLine($"LOADING initial library scan: UI thread={uiThread}, scan thread={scanThreads.Single()}");
            Assert.True(scanThreads.Single() != uiThread, "Initial library scanning blocked the WPF dispatcher.");
            Assert.Equal(0, (await service.GetDownloadsAsync()).Count);
            Assert.Equal(1, scanThreads.Count);
            Assert.Equal("preserve previous files", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            // Delete only the link itself before the fixture removes its own root.
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    });
}
