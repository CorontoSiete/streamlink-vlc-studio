using System.Text;

namespace StreamlinkVlcStudio.Maintenance;

internal sealed class MaintenanceLog : IDisposable
{
    private readonly StreamWriter writer;

    private MaintenanceLog(string path, bool append)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        writer = new StreamWriter(
            new FileStream(Path, append ? FileMode.Append : FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public string Path { get; }

    public static MaintenanceLog Create()
    {
        var directory = GetLogDirectory();
        Directory.CreateDirectory(directory);
        if (PathSafety.ContainsReparsePoint(directory))
        {
            throw new IOException($"Maintenance log directory contains a reparse point: {directory}");
        }

        var path = System.IO.Path.Combine(
            directory,
            $"uninstall-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        return new MaintenanceLog(path, append: false);
    }

    public static MaintenanceLog OpenExisting(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var expectedDirectory = GetLogDirectory();
        if (!PathSafety.IsSameOrUnder(fullPath, expectedDirectory) ||
            PathSafety.PathsEqual(fullPath, expectedDirectory) ||
            PathSafety.ContainsReparsePoint(System.IO.Path.GetDirectoryName(fullPath)!) ||
            !PathSafety.TryGetAttributes(fullPath, out var attributes) ||
            !PathSafety.IsPlainFile(attributes))
        {
            throw new InvalidDataException("The staged maintenance log path is outside the durable log directory.");
        }

        return new MaintenanceLog(fullPath, append: true);
    }

    public void Write(string message)
    {
        writer.WriteLine($"{DateTime.UtcNow:O} {message}");
    }

    public string WriteRetainedPathReport(string heading, IEnumerable<string> retainedPaths)
    {
        var reportPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path)!,
            $"retained-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
        var lines = new List<string>
        {
            heading,
            $"Generated UTC: {DateTime.UtcNow:O}",
            $"Log: {Path}",
            ""
        };
        lines.AddRange(retainedPaths.Distinct(StringComparer.OrdinalIgnoreCase));
        File.WriteAllLines(reportPath, lines, new UTF8Encoding(false));
        Write($"Retained-path report: {reportPath}");
        return reportPath;
    }

    public void Dispose()
    {
        writer.Dispose();
    }

    private static string GetLogDirectory()
    {
        return System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StreamlinkVlcStudio-Maintenance-Logs"));
    }
}
