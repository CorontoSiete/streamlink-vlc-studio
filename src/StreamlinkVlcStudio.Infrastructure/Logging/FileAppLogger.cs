using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Logging;

public sealed class FileAppLogger : IAppLogger
{
    private readonly object gate = new();
    private readonly string logFilePath;

    public FileAppLogger(string? baseDirectory = null)
    {
        var root = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamlinkVlcStudio");

        Directory.CreateDirectory(root);
        logFilePath = Path.Combine(root, $"studio-{DateTimeOffset.Now:yyyyMMdd}.log");
    }

    public event EventHandler<LogEntry>? EntryWritten;

    public void Write(AppLogLevel level, string source, string message, Exception? exception = null)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, source, message, exception);
        var line = $"{entry.Timestamp:O} [{entry.Level}] {entry.Source}: {entry.Message}";
        if (exception is not null)
        {
            line += $" | {exception.GetType().Name}: {exception.Message}";
        }

        lock (gate)
        {
            File.AppendAllText(logFilePath, line + Environment.NewLine);
        }

        EntryWritten?.Invoke(this, entry);
    }
}
