using System.Runtime.InteropServices;
using System.Windows;

namespace StreamlinkVlcStudio.App.Wpf.Services;

internal readonly record struct ClipboardWriteResult(bool Succeeded, Exception? Error)
{
    internal static ClipboardWriteResult Success { get; } = new(true, null);
}

/// <summary>Writes text to the Windows clipboard with a bounded retry for clipboard contention.</summary>
internal sealed class ClipboardService
{
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(60);

    private readonly Action<string> setText;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;

    internal ClipboardService(
        Action<string>? setText = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.setText = setText ?? Clipboard.SetText;
        this.delayAsync = delayAsync ?? Task.Delay;
    }

    internal async Task<ClipboardWriteResult> TrySetTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                setText(text);
                return ClipboardWriteResult.Success;
            }
            catch (ExternalException) when (attempt < MaximumAttempts)
            {
                await delayAsync(RetryDelay, cancellationToken);
            }
            catch (ExternalException ex)
            {
                return new ClipboardWriteResult(false, ex);
            }
        }

        return new ClipboardWriteResult(false, new InvalidOperationException("Clipboard retry limit was reached."));
    }
}
