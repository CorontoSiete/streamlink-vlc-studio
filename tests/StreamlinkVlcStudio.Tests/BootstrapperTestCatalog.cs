internal static class BootstrapperTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Bootstrapper entry point leaves COM apartment selection to WiX", EntryPointLeavesComApartmentSelectionToWix),
        ("Bootstrapper read-only version run bindings are one-way", ReadOnlyVersionRunBindingsAreOneWay)
    ];

    private static Task EntryPointLeavesComApartmentSelectionToWix()
    {
        var programPath = Path.Combine(
            FindRepoRoot(),
            "src",
            "StreamlinkVlcStudio.Bootstrapper",
            "Program.cs");
        var lines = File.ReadLines(programPath);

        Assert.Equal(
            false,
            lines.Any(line => string.Equals(line.Trim(), "[STAThread]", StringComparison.Ordinal)));
        return Task.CompletedTask;
    }

    private static Task ReadOnlyVersionRunBindingsAreOneWay()
    {
        var xamlPath = Path.Combine(
            FindRepoRoot(),
            "src",
            "StreamlinkVlcStudio.Bootstrapper",
            "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("{Binding Version, Mode=OneWay}", xaml);
        Assert.Contains("{Binding StreamlinkVersion, Mode=OneWay}", xaml);
        Assert.Contains("{Binding VlcVersion, Mode=OneWay}", xaml);
        return Task.CompletedTask;
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "StreamlinkVlcStudio.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
