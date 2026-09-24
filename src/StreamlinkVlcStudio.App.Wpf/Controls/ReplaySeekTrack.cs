using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>Maps replay pointer input independently of pending thumb layout.</summary>
public sealed class ReplaySeekTrack : Track
{
    public override double ValueFromPoint(Point point)
    {
        // Track.ValueFromPoint adds a distance from the last arranged thumb center
        // to the current Value. A move-to-point click changes Value before layout;
        // mouse capture can immediately deliver another move and apply that distance
        // twice. Both Slider's click handler and captured scrubbing use this absolute
        // mapping, which depends only on the track's geometry and value range.
        var horizontal = Orientation == Orientation.Horizontal;
        var length = horizontal ? RenderSize.Width : RenderSize.Height;
        var thumbLength = Thumb is null ? 0 :
            horizontal ? Thumb.DesiredSize.Width : Thumb.DesiredSize.Height;
        thumbLength = Math.Clamp(thumbLength, 0, length);
        var travel = length - thumbLength;
        var coordinate = horizontal ? point.X : point.Y;
        if (travel <= 0 || !double.IsFinite(coordinate)) return Value;

        var fraction = Math.Clamp((coordinate - thumbLength / 2) / travel, 0, 1);
        // Points are already track-local, including any DPI/layout/flow transform.
        if (horizontal ? IsDirectionReversed : !IsDirectionReversed)
            fraction = 1 - fraction;
        return Minimum + fraction * (Maximum - Minimum);
    }
}
