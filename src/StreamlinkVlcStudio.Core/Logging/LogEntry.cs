namespace StreamlinkVlcStudio.Core.Logging;

public sealed record LogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Source, string Message, Exception? Exception = null);
