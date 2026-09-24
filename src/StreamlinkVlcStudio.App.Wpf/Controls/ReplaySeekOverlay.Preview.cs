using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using StreamlinkVlcStudio.App.Wpf.ViewModels;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public partial class ReplaySeekOverlay
{
    private ReplaySeekPreviewImages previewImages = new();
    private CancellationTokenSource? previewCancellation;
    private Rect replayOverlayBounds;
    private Point hoverPoint;
    private long previewVersion;
    private double hoverSeconds = double.NaN;
    private ReplaySeekPreviewSource? hoverSource;
    private long previewRequestedAt;

    // Injectable at the image boundary so UI regressions never depend on public network access.
    internal Func<ReplaySeekPreviewSource, double, CancellationToken, Task<BitmapSource?>>? PreviewImageLoader { get; set; }
    internal bool IsSeekHoverOpen => SeekPreviewHost.IsOpen;

    private void InitializeSeekHover()
    {
        SeekPreviewChrome.Tag = this;
        SeekPreviewHost.PlacementInvalidated += (_, _) => UpdateSeekHoverPlacement();
        ReplaySeekSlider.MouseEnter += (_, e) => UpdateSeekHover(e.GetPosition(ReplaySeekSlider));
        ReplaySeekSlider.MouseLeave += (_, _) =>
        {
            if (!ReplaySeekSlider.IsMouseCaptureWithin) HideSeekHover();
        };
        ReplaySeekSlider.IsEnabledChanged += (_, _) =>
        {
            if (!ReplaySeekSlider.IsEnabled) HideSeekHover();
        };
    }

    private void SampleSeekHover(Point screenPosition, bool overControls)
    {
        if (!OverlayHost.IsOpen || (!overControls && !ReplaySeekSlider.IsMouseCaptureWithin))
        {
            HideSeekHover();
            return;
        }
        UpdateSeekHover(ReplaySeekSlider.PointFromScreen(screenPosition));
    }

    internal void UpdateSeekHover(Point sliderPosition)
    {
        if (!CanDisplay || !OverlayHost.IsOpen || fading || !ReplaySeekSlider.IsEnabled ||
            DataContext is not StreamTabViewModel { CanSeekReplay: true } tab ||
            !double.IsFinite(sliderPosition.X) || !double.IsFinite(sliderPosition.Y) ||
            (!ReplaySeekSlider.IsMouseCaptureWithin &&
             !new Rect(ReplaySeekSlider.RenderSize).Contains(sliderPosition)) ||
            ReplaySeekSlider.Template.FindName("PART_Track", ReplaySeekSlider) is not Track track)
        {
            HideSeekHover();
            return;
        }
        var seconds = track.ValueFromPoint(ReplaySeekSlider.TranslatePoint(sliderPosition, track));
        if (!double.IsFinite(seconds)) { HideSeekHover(); return; }
        seconds = Math.Clamp(seconds, ReplaySeekSlider.Minimum, ReplaySeekSlider.Maximum);
        var pointerMoved = hoverPoint != sliderPosition;
        hoverPoint = sliderPosition;
        SeekPreviewTimestamp.Text = StreamViewModelHelpers.FormatClockTime(TimeSpan.FromSeconds(seconds));
        var source = tab.ReplayPreviewSource;
        // A growing timeline moves under a stationary pointer. Let its outstanding request
        // finish instead of continually cancelling a slow download on every clock tick.
        var timeChanged = Math.Floor(seconds) != Math.Floor(hoverSeconds) &&
            (source?.PlaylistUri is null || pointerMoved || previewCancellation is null);
        var sourceChanged = source != hoverSource;
        var changed = !SeekPreviewHost.IsOpen || timeChanged ||
            sourceChanged || (source?.PlaylistUri is not null && previewCancellation is null &&
                Environment.TickCount64 - previewRequestedAt >= 5_000);
        hoverSeconds = seconds;
        hoverSource = source;
        if (changed)
        {
            CancelPreviewImage();
            // Keep the displayed frame and preview height while its replacement loads.
            // Clearing on every pointer move collapses and expands the native window.
            // Never carry a frame across different replay sources or hover sessions.
            if (sourceChanged || !SeekPreviewHost.IsOpen) SeekPreviewImage.Source = null;
        }
        SeekPreviewHost.Open(UpdateSeekHoverPlacement);
        if (changed && SeekPreviewHost.IsOpen && source is not null)
        {
            var cancellation = new CancellationTokenSource();
            previewRequestedAt = Environment.TickCount64;
            previewCancellation = cancellation;
            _ = LoadSeekHoverImageAsync(tab, source, seconds, previewVersion, cancellation);
        }
    }

    private async Task LoadSeekHoverImageAsync(StreamTabViewModel tab, ReplaySeekPreviewSource source, double seconds,
        long version, CancellationTokenSource cancellation)
    {
        try
        {
            // Coalesce pointer movement before fetching or decoding a sprite sheet.
            await Task.Delay(120, cancellation.Token);
            var loader = PreviewImageLoader ?? previewImages.GetAsync;
            var bitmap = await Task.Run(() => loader(source, seconds, cancellation.Token), cancellation.Token);
            CompleteSeekHoverImage(tab, source, version, cancellation, bitmap);
        }
        catch (OperationCanceledException)
        {
            // A provider timeout may not cancel our request token. Treat it as an
            // unavailable frame; cancelled/obsolete requests fail the completion guard.
            CompleteSeekHoverImage(tab, source, version, cancellation, null);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Http.HttpRequestException or
            InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // A missing thumbnail leaves the already-visible timestamp usable.
            CompleteSeekHoverImage(tab, source, version, cancellation, null);
        }
        finally
        {
            if (ReferenceEquals(previewCancellation, cancellation)) previewCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CompleteSeekHoverImage(StreamTabViewModel tab, ReplaySeekPreviewSource source, long version,
        CancellationTokenSource cancellation, BitmapSource? bitmap)
    {
        if (cancellation.IsCancellationRequested || version != previewVersion || !SeekPreviewHost.IsOpen ||
            !ReferenceEquals(tab, DataContext) || tab.ReplayPreviewSource != source) return;
        SeekPreviewImage.Source = bitmap;
        UpdateSeekHoverPlacement();
    }

    private void UpdateSeekHoverPlacement()
    {
        var size = SeekPreviewHost.TargetSize;
        if (size.IsEmpty || !OverlayHost.IsOpen) { HideSeekHover(); return; }
        var availableHeight = replayOverlayBounds.Top - 6;
        // Keep a very short PiP's seek controls reachable.
        if (availableHeight < 30) { HideSeekHover(); return; }
        var imageWidth = Math.Min(192, Math.Min(size.Width - 26, (availableHeight - 34) * 16 / 9));
        SeekPreviewImageFrame.Visibility = SeekPreviewImage.Source is not null && imageWidth >= 80
            ? Visibility.Visible : Visibility.Collapsed;
        SeekPreviewImage.Width = Math.Max(1, imageWidth);
        SeekPreviewImage.Height = Math.Max(1, imageWidth * 9 / 16);
        SeekPreviewChrome.Measure(new Size(Math.Max(1, size.Width - 16), availableHeight));
        var desired = SeekPreviewChrome.DesiredSize;
        var pointerX = replayOverlayBounds.Left + ReplaySeekSlider.TranslatePoint(hoverPoint, OverlayChrome).X;
        var left = Math.Clamp(pointerX - desired.Width / 2, 8, Math.Max(8, size.Width - desired.Width - 8));
        SeekPreviewHost.SetBounds(new Rect(left, Math.Max(0, availableHeight - desired.Height), desired.Width, desired.Height));
    }

    private void CancelPreviewImage()
    {
        previewVersion++;
        previewCancellation?.Cancel();
        // The request owns disposal, including when a decoder is still finishing in the background.
        previewCancellation = null;
    }

    private void HideSeekHover()
    {
        CancelPreviewImage();
        hoverSeconds = double.NaN;
        hoverSource = null;
        SeekPreviewImage.Source = null;
        SeekPreviewImageFrame.Visibility = Visibility.Collapsed;
        SeekPreviewHost.Close();
    }
}
