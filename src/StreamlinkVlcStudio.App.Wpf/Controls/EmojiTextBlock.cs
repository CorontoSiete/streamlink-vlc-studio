using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public sealed class EmojiTextBlock : TextBlock
{
    private readonly List<InlineUIContainer> emojiInlines = [];
    private bool inlineClipsDirty;

    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(EmojiTextBlock),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, OnSourceTextChanged));

    static EmojiTextBlock()
    {
        // TextBlock trims text during rendering but still arranges InlineUIContainer
        // children on hidden lines. Clip the entire visual subtree so those emoji
        // cannot paint beyond the title's final layout bounds.
        ClipToBoundsProperty.OverrideMetadata(typeof(EmojiTextBlock),
            new FrameworkPropertyMetadata(true));
        FontSizeProperty.OverrideMetadata(typeof(EmojiTextBlock),
            new FrameworkPropertyMetadata(OnFontSizeChanged));
    }

    public string? SourceText
    {
        get => (string?)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public EmojiTextBlock()
    {
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override Geometry? GetLayoutClip(Size layoutSlotSize)
    {
        // This hook runs for reflow even when the title's final size stays the same.
        // Defer text-rectangle queries until the completed layout is valid.
        inlineClipsDirty = true;
        return base.GetLayoutClip(layoutSlotSize);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RebuildInlines();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new EmojiTextBlockAutomationPeer(this);

    private static void OnSourceTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs arguments)
    {
        var block = (EmojiTextBlock)target;
        block.RebuildInlines();
        if (string.IsNullOrEmpty(AutomationProperties.GetName(block)) &&
            UIElementAutomationPeer.FromElement(block) is { } peer)
        {
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty,
                arguments.OldValue ?? "", arguments.NewValue ?? "");
        }
    }

    private static void OnFontSizeChanged(DependencyObject target, DependencyPropertyChangedEventArgs arguments) =>
        ((EmojiTextBlock)target).RebuildInlines();

    private void RebuildInlines()
    {
        emojiInlines.Clear();
        inlineClipsDirty = true;
        Inlines.Clear();
        if (string.IsNullOrEmpty(SourceText))
            return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var segment in UnicodeEmojiRenderer.EnumerateTextRunSegments(SourceText))
        {
            if (!segment.UseEmojiImage)
            {
                Inlines.Add(new Run(segment.Text));
                continue;
            }

            var source = UnicodeEmojiRenderer.GetEmojiImageSource(segment.Text, FontSize, pixelsPerDip);
            if (source is null)
            {
                Inlines.Add(new Run(segment.Text) { FontFamily = UnicodeEmojiRenderer.FallbackFontFamily });
                continue;
            }

            var aspect = source.Height > 0 ? Math.Clamp(source.Width / source.Height, 0.35, 4.0) : 1.0;
            var image = new Image
            {
                Source = source,
                Height = FontSize,
                Width = FontSize * aspect,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            AutomationProperties.SetName(image, segment.Text);
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var inline = new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center };
            emojiInlines.Add(inline);
            Inlines.Add(inline);
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs arguments)
    {
        if (!inlineClipsDirty || emojiInlines.Count == 0 || !IsMeasureValid || !IsArrangeValid)
        {
            return;
        }

        inlineClipsDirty = false;
        foreach (var inline in emojiInlines)
        {
            // WPF places collapsed inline objects at a zero-width text rectangle,
            // but still draws their Image children over the ellipsis. Hide those
            // children without changing measurement, and restore them after reflow.
            var image = (Image)inline.Child;
            var clip = GetRectanglesCore(inline).Any(rectangle => rectangle.Width > 0 && rectangle.Height > 0)
                ? null
                : Geometry.Empty;
            if (!ReferenceEquals(image.Clip, clip))
                image.Clip = clip;
        }
    }

    private sealed class EmojiTextBlockAutomationPeer(EmojiTextBlock owner) : TextBlockAutomationPeer(owner)
    {
        protected override string GetNameCore()
        {
            var explicitName = AutomationProperties.GetName(Owner);
            return string.IsNullOrEmpty(explicitName) ? ((EmojiTextBlock)Owner).SourceText ?? "" : explicitName;
        }
    }
}
