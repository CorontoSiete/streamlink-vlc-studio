namespace StreamlinkVlcStudio.Core.Settings;

public sealed class PictureInPictureWindowLocation
{
    public PictureInPictureWindowLocation()
    {
    }

    public PictureInPictureWindowLocation(double left, double top, double width = 0, double height = 0)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool IsFullscreen { get; set; }

    public PictureInPictureFullscreenMode FullscreenMode { get; set; } = PictureInPictureFullscreenMode.StreamOnly;

    public PictureInPictureFullscreenScreen? FullscreenScreen { get; set; }
}
