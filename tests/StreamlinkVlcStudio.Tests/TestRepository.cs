/// <summary>Finds source fixtures consistently from the repository or a test output directory.</summary>
internal static class TestRepository
{
    internal static string FindRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "scripts", "package-release.ps1")) &&
                    File.Exists(Path.Combine(directory.FullName, "StreamlinkVlcStudio.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
