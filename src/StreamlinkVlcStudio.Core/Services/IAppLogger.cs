using StreamlinkVlcStudio.Core.Logging;

namespace StreamlinkVlcStudio.Core.Services;

public interface IAppLogger
{
    event EventHandler<LogEntry>? EntryWritten;

    void Write(AppLogLevel level, string source, string message, Exception? exception = null);
}
