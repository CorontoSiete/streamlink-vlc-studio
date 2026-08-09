using System.Windows.Input;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Shared helpers for the home/browse item view models. Consolidates the input and formatting
/// helpers that were previously duplicated across several item view models.
/// </summary>
internal static class StreamViewModelHelpers
{
    /// <summary>
    /// True when Ctrl is held, signalling that an open command should keep the home view active.
    /// </summary>
    public static bool ShouldStayOnHomeForOpenCommand()
    {
        return (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
    }

    /// <summary>Formats a viewer count with K/M suffixes (e.g. <c>1.2K</c>, <c>3.4M</c>).</summary>
    public static string FormatViewerCount(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}K";
        }

        return value.ToString();
    }

    /// <summary>Formats an elapsed live duration (e.g. <c>2h 3m</c> or <c>5m</c>).</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        }

        return $"{Math.Max(1, (int)elapsed.TotalMinutes)}m";
    }

    /// <summary>
    /// Formats a duration clock-style (e.g. <c>1:02:45</c> or <c>5:08</c>); negative values
    /// clamp to <c>0:00</c>.
    /// </summary>
    public static string FormatClockTime(TimeSpan value)
    {
        value = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }
}
