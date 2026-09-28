internal static class MultistreamInitializationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [("multistream initialization: eight concurrent native players keep independent runtimes and pipes", InitializeAsync)];

    private static async Task InitializeAsync()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"svs-vlc-init-{Guid.NewGuid():N}")).FullName;
        var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
        using var ready = new CountdownEvent(8);
        var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creations = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            ready.Signal();
            await begin.Task;
            return await factory.CreateAsync(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                nativeOverlayPositionStatePath: Path.Combine(root, $"position-{index}"), rendererMode: VideoRendererMode.Gdi);
        })).ToArray();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            begin.SetResult();
            var engines = await Task.WhenAll(creations);
            Assert.True(engines.All(engine => engine.UsesNativeOverlay));
            Assert.Equal(8, engines.Select(engine => engine.NativeOverlayPipeName).Distinct(StringComparer.Ordinal).Count());
            var instance = typeof(LibVlcPlaybackEngine).GetField("instance", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var instances = engines.Select(engine => (IntPtr)instance.GetValue(engine)!).ToArray();
            Assert.True(instances.All(value => value != IntPtr.Zero));
            Assert.Equal(8, instances.Distinct().Count());
        }
        finally
        {
            begin.TrySetResult();
            try { await Task.WhenAll(creations); } catch { }
            foreach (var creation in creations) if (creation.IsCompletedSuccessfully) creation.Result.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
