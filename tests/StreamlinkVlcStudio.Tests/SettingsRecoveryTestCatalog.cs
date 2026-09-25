internal static class SettingsRecoveryTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("settings recovery reports the preserved corrupt file and clears stale warnings", () => RecoveryWarningAsync(false)),
        ("settings recovery identifies the original file when a backup cannot be moved", () => RecoveryWarningAsync(true))
    ];

    private static async Task RecoveryWarningAsync(bool preventMove)
    {
        var directory = Path.Combine(Path.GetTempPath(), "StreamStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        const string malformed = """{"Theme":"invalid-theme"}""";
        try
        {
            await File.WriteAllTextAsync(path, malformed);
            var service = new JsonSettingsService(path);
            using (var heldFile = preventMove ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
            {
                var loaded = await service.LoadAsync();
                Assert.Equal("best", loaded.DefaultQuality);
                Assert.NotNull(service.LastLoadWarning);
                Assert.Contains("defaults", service.LastLoadWarning!);
                var preservedPath = preventMove ? path : Directory.GetFiles(directory, "settings.json.invalid-*").Single();
                Assert.Contains(preservedPath, service.LastLoadWarning!);
                Assert.Equal(malformed, await File.ReadAllTextAsync(preservedPath));
            }

            await service.SaveAsync(new AppSettings { DefaultQuality = "720p" });
            Assert.Equal("720p", (await service.LoadAsync()).DefaultQuality);
            Assert.Equal<string?>(null, service.LastLoadWarning);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
