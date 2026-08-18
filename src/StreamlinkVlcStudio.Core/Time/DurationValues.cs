using System.Globalization;
using System.Text.RegularExpressions;

namespace StreamlinkVlcStudio.Core.Time;

/// <summary>
/// Safe conversion helpers for durations received from external APIs and playlists.
/// </summary>
public static partial class DurationValues
{
    public static bool TryParseHmsDuration(string? value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = HmsDurationPattern().Match(value.Trim());
        if (!match.Success ||
            !TryParseOptionalDurationPart(match.Groups["h"].Value, out var hours) ||
            !TryParseOptionalDurationPart(match.Groups["m"].Value, out var minutes) ||
            !TryParseOptionalDurationPart(match.Groups["s"].Value, out var seconds) ||
            (hours == 0 && minutes == 0 && seconds == 0))
        {
            return false;
        }

        return TryCreatePositive(
            (hours * 3600d) + (minutes * 60d) + seconds,
            TimeSpan.TicksPerSecond,
            out duration);
    }

    public static bool TryCreatePositive(double value, long ticksPerUnit, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (!double.IsFinite(value) || value <= 0 || ticksPerUnit <= 0)
        {
            return false;
        }

        var ticks = value * ticksPerUnit;
        // Int64.MaxValue rounds to 2^63 as a double, so comparing a double with
        // TimeSpan.MaxValue.Ticks cannot distinguish the first invalid value.
        const double exclusiveMaximumTicks = 9_223_372_036_854_775_808d;
        if (!double.IsFinite(ticks) || ticks <= 0 || ticks >= exclusiveMaximumTicks)
        {
            return false;
        }

        var roundedTicks = Math.Round(ticks, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(roundedTicks) ||
            roundedTicks <= 0 ||
            roundedTicks >= exclusiveMaximumTicks)
        {
            return false;
        }

        var convertedTicks = checked((long)roundedTicks);
        if (convertedTicks <= 0)
        {
            return false;
        }

        duration = TimeSpan.FromTicks(convertedTicks);
        return true;
    }

    private static bool TryParseOptionalDurationPart(string value, out double parsed)
    {
        if (string.IsNullOrEmpty(value))
        {
            parsed = 0;
            return true;
        }

        return double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
            double.IsFinite(parsed);
    }

    [GeneratedRegex(@"^(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s)?$", RegexOptions.CultureInvariant)]
    private static partial Regex HmsDurationPattern();
}
