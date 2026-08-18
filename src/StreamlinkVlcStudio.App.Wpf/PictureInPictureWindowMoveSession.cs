namespace StreamlinkVlcStudio.App.Wpf;

/// <summary>
/// Tracks an application-owned picture-in-picture move. Keeping movement out of the native
/// caption-move loop prevents Windows from interpreting constrained edge dragging as Aero Shake.
/// </summary>
internal sealed class PictureInPictureWindowMoveSession
{
    private int previousScreenX;
    private int previousScreenY;

    public bool IsActive { get; private set; }

    public void Begin(int screenX, int screenY)
    {
        previousScreenX = screenX;
        previousScreenY = screenY;
        IsActive = true;
    }

    public bool TryGetNextBounds(
        NativeRectangle currentBounds,
        int screenX,
        int screenY,
        NativeRectangle workArea,
        out NativeRectangle nextBounds)
    {
        nextBounds = currentBounds;
        if (!IsActive)
        {
            return false;
        }

        var horizontalChange = (long)screenX - previousScreenX;
        var verticalChange = (long)screenY - previousScreenY;
        previousScreenX = screenX;
        previousScreenY = screenY;

        var left = currentBounds.Left + horizontalChange;
        var top = currentBounds.Top + verticalChange;
        var right = currentBounds.Right + horizontalChange;
        var bottom = currentBounds.Bottom + verticalChange;
        if (left < int.MinValue ||
            top < int.MinValue ||
            right > int.MaxValue ||
            bottom > int.MaxValue)
        {
            return false;
        }

        var proposedBounds = new NativeRectangle
        {
            Left = (int)left,
            Top = (int)top,
            Right = (int)right,
            Bottom = (int)bottom
        };
        return PictureInPictureWindowSizing.TryConstrainMoveRect(
            proposedBounds,
            workArea,
            out nextBounds);
    }

    public bool End()
    {
        if (!IsActive)
        {
            return false;
        }

        IsActive = false;
        return true;
    }
}
