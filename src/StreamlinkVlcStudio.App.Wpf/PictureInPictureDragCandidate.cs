namespace StreamlinkVlcStudio.App.Wpf;

internal sealed class PictureInPictureDragCandidate
{
    private int startX;
    private int startY;

    public bool IsActive { get; private set; }
    public int StartScreenX => startX;
    public int StartScreenY => startY;

    public void Begin(int screenX, int screenY)
    {
        startX = screenX;
        startY = screenY;
        IsActive = true;
    }

    public bool TryStartDrag(int screenX, int screenY, int horizontalThreshold, int verticalThreshold)
    {
        if (!IsActive ||
            (Math.Abs(screenX - startX) < Math.Max(1, horizontalThreshold) &&
             Math.Abs(screenY - startY) < Math.Max(1, verticalThreshold)))
        {
            return false;
        }

        IsActive = false;
        return true;
    }

    public bool Cancel()
    {
        if (!IsActive)
        {
            return false;
        }

        IsActive = false;
        return true;
    }
}
