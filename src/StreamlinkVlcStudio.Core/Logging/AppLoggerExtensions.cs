using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Core.Logging;

public static class AppLoggerExtensions
{
    /// <summary>Writes optional diagnostics without allowing a logging failure to interrupt the operation.</summary>
    public static void WriteSafely(this IAppLogger? logger, AppLogLevel level, string source,
        string message, Exception? exception = null)
    {
        try
        {
            logger?.Write(level, source, message, exception);
        }
        catch (Exception)
        {
            // Logging sinks and subscribers must not change the result of the operation being reported.
        }
    }
}
