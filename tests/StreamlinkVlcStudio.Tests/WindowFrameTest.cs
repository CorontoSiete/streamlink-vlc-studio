using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Graphics = System.Drawing.Graphics;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Rectangle = System.Drawing.Rectangle;

internal static class WindowFrameTest
{
    internal static Bitmap Capture(IntPtr handle) => Capture(NativeWindowTest.GetWindowBounds(handle));

    internal static Bitmap Capture(Rectangle bounds)
    {
        var frame = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(frame);
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    internal static IEnumerable<(int X, int Y, Color Color)> EdgePixels(Bitmap frame)
    {
        foreach (var fraction in new[] { 0.25, 0.5, 0.75 })
        {
            var x = (int)(frame.Width * fraction);
            var y = (int)(frame.Height * fraction);
            yield return (0, y, frame.GetPixel(0, y));
            yield return (frame.Width - 1, y, frame.GetPixel(frame.Width - 1, y));
            yield return (x, 0, frame.GetPixel(x, 0));
            yield return (x, frame.Height - 1, frame.GetPixel(x, frame.Height - 1));
        }
    }
}
