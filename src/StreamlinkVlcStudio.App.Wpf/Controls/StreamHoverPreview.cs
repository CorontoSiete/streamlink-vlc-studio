using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>WPF video pixels preserve card clipping, scrolling, badges, and normal button input.</summary>
public sealed class StreamHoverPreview : Grid
{
    public static readonly DependencyProperty ControllerProperty = DependencyProperty.Register(
        nameof(Controller), typeof(StreamHoverPreviewController), typeof(StreamHoverPreview),
        new PropertyMetadata(null, PreviewOptionChanged));
    public static readonly DependencyProperty IsPreviewEnabledProperty = DependencyProperty.Register(
        nameof(IsPreviewEnabled), typeof(bool), typeof(StreamHoverPreview),
        new PropertyMetadata(false, PreviewOptionChanged));
    private readonly Image video = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock status = new() { Foreground = Brushes.White, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Border statusBadge;
    private ButtonBase? card;
    private Window? window;
    private INotifyPropertyChanged? observedItem;
    private StreamHoverPreviewSession? session;
    private WriteableBitmap? bitmap;
    private bool suppressUntilLeave;
    private int renderQueued;

    public StreamHoverPreview()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        statusBadge = new Border
        {
            Child = status,
            Background = new SolidColorBrush(Color.FromArgb(220, 16, 16, 18)),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed
        };
        Children.Add(video);
        Children.Add(statusBadge);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateHover();
        DataContextChanged += (_, _) => { ObserveItem(); Stop(); UpdateHover(); };
    }

    public StreamHoverPreviewController? Controller
    {
        get => (StreamHoverPreviewController?)GetValue(ControllerProperty);
        set => SetValue(ControllerProperty, value);
    }

    public bool IsPreviewEnabled
    {
        get => (bool)GetValue(IsPreviewEnabledProperty);
        set => SetValue(IsPreviewEnabledProperty, value);
    }

    internal static StreamTarget? GetLiveTarget(object? item) => item switch
    {
        LiveStreamCardViewModel live => live.Target,
        StreamSearchResultViewModel { IsLive: true, CanPlay: true } search => search.Target,
        RecentStreamViewModel { LiveStatusKey: "Live" } recent => recent.Target,
        _ => null
    };

    private static void PreviewOptionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var preview = (StreamHoverPreview)sender;
        preview.Stop();
        preview.UpdateHover();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(this);
        while (parent is not null && parent is not ButtonBase) parent = VisualTreeHelper.GetParent(parent);
        card = parent as ButtonBase;
        if (card is not null)
        {
            card.MouseEnter += OnEnter;
            card.MouseLeave += OnLeave;
            card.PreviewMouseDown += OnCardClick;
        }
        window = Window.GetWindow(this);
        if (window is not null)
        {
            window.Deactivated += OnDeactivated;
            window.Activated += OnActivated;
            window.StateChanged += OnActivated;
        }
        ObserveItem();
        UpdateHover();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Stop();
        if (card is not null)
        {
            card.MouseEnter -= OnEnter;
            card.MouseLeave -= OnLeave;
            card.PreviewMouseDown -= OnCardClick;
            card = null;
        }
        if (window is not null)
        {
            window.Deactivated -= OnDeactivated;
            window.Activated -= OnActivated;
            window.StateChanged -= OnActivated;
            window = null;
        }
        if (observedItem is not null) observedItem.PropertyChanged -= ItemChanged;
        observedItem = null;
    }

    private void ObserveItem()
    {
        if (observedItem is not null) observedItem.PropertyChanged -= ItemChanged;
        observedItem = IsLoaded ? DataContext as INotifyPropertyChanged : null;
        if (observedItem is not null) observedItem.PropertyChanged += ItemChanged;
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e) => UpdateHover();
    private void OnEnter(object sender, MouseEventArgs e) { suppressUntilLeave = false; UpdateHover(); }
    private void OnLeave(object sender, MouseEventArgs e) { suppressUntilLeave = false; Stop(); }
    private void OnCardClick(object sender, MouseButtonEventArgs e) { suppressUntilLeave = true; Stop(); }
    private void OnDeactivated(object? sender, EventArgs e) => Stop();
    private void OnActivated(object? sender, EventArgs e) => UpdateHover();

    private void UpdateHover()
    {
        var target = GetLiveTarget(DataContext);
        if (!IsLoaded || !IsVisible || !IsPreviewEnabled || suppressUntilLeave || card?.IsMouseOver != true ||
            window?.IsActive != true || window.WindowState == WindowState.Minimized || target is null || Controller is null)
        {
            Stop();
            return;
        }
        if (session?.Target.TabIdentityKey == target.TabIdentityKey && session.Target.Url == target.Url) return;
        Stop();
        session = Controller.Begin(target);
        if (session is not null)
        {
            session.Changed += QueueRender;
            // Startup may publish a state/frame before this subscription is attached.
            QueueRender();
        }
    }

    private void QueueRender()
    {
        // Wake the dispatcher when video arrives instead of waiting for a polling timer.
        // Coalesce notifications: one queued render reads the newest frame/current session.
        if (Interlocked.CompareExchange(ref renderQueued, 1, 0) != 0) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            Interlocked.Exchange(ref renderQueued, 0);
            return;
        }
        var operation = Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref renderQueued, 0);
            RenderFrame();
        }, DispatcherPriority.Render);
        if (operation.Status == DispatcherOperationStatus.Aborted) Interlocked.Exchange(ref renderQueued, 0);
    }

    private void RenderFrame()
    {
        if (session is null || session.State == StreamHoverPreviewState.Stopped) { Stop(); return; }
        var frame = session.TakeFrame();
        if (frame is not null)
        {
            if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
                bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
            video.Source = bitmap;
        }
        var state = session.State;
        status.Text = state switch
        {
            StreamHoverPreviewState.Loading => "Loading preview…",
            StreamHoverPreviewState.Playing => "Preview · Muted",
            StreamHoverPreviewState.Unavailable => "Preview unavailable",
            _ => ""
        };
        statusBadge.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (state == StreamHoverPreviewState.Unavailable)
        {
            video.Source = null;
            bitmap = null;
        }
    }

    private void Stop()
    {
        if (session is not null)
        {
            session.Changed -= QueueRender;
            session.Stop();
        }
        session = null;
        video.Source = null;
        bitmap = null;
        statusBadge.Visibility = Visibility.Collapsed;
    }
}
