namespace StreamlinkVlcStudio.Core.Settings;

public sealed class PictureInPictureFullscreenScreen
{
    public PictureInPictureFullscreenScreen()
    {
    }

    public PictureInPictureFullscreenScreen(string deviceName, double left, double top, double width, double height)
    {
        DeviceName = deviceName;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public string DeviceName { get; set; } = "";

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}
