using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Logging;

public sealed class FileAppLogger : IAppLogger
{
    private readonly object gate = new();
    private readonly string logDirectory;

    public FileAppLogger(string? baseDirectory = null)
    {
        var root = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamlinkVlcStudio");

        Directory.CreateDirectory(root);
        logDirectory = root;
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
            try
            {
                var logFilePath = Path.Combine(logDirectory, $"studio-{entry.Timestamp:yyyyMMdd}.log");
                File.AppendAllText(logFilePath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never throw into callers (Write runs inside catch blocks all over
                // the app). A failed append drops the file line; EntryWritten still fires below.
            }
        }

        var handlers = EntryWritten;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<LogEntry> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, entry);
            }
            catch (Exception)
            {
                // A diagnostic subscriber must not make logging fail or block other subscribers.
            }
        }
    }
}
