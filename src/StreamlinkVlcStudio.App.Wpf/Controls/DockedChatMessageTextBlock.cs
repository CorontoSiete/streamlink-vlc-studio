using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.Chat;
using StreamlinkVlcStudio.App.Wpf.Services;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using WpfImage = System.Windows.Controls.Image;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public sealed class DockedChatMessageTextBlock : RichTextBox
{
    private static readonly Brush TimestampBrush = CreateFrozenBrush("#6F7B8C");
    private static readonly Brush UsernameFallbackBrush = CreateFrozenBrush("#8AB4F8");
    private static readonly Brush MessageBrush = CreateFrozenBrush("#E8EAED");
    private static readonly Brush LinkBrush = CreateFrozenBrush("#8AB4F8");
    private static readonly Brush NativeOverlayMessageBrush = CreateFrozenBrush("#FFFFFF");
    private static readonly Brush NativeOverlaySystemBrush = CreateFrozenBrush("#93C5FD");
    private static readonly Brush NativeOverlaySelectionBrush = CreateFrozenBrush("#CD2E78C9");
    private static readonly Brush[] NativeOverlayUsernameBrushes =
    [
        CreateFrozenBrush("#7DD3FC"),
        CreateFrozenBrush("#86EFAC"),
        CreateFrozenBrush("#FDE68A"),
        CreateFrozenBrush("#FCA5A5"),
        CreateFrozenBrush("#C4B5FD"),
        CreateFrozenBrush("#F9A8D4"),
        CreateFrozenBrush("#67E8F9"),
        CreateFrozenBrush("#FDBA74")
    ];
    private static readonly Brush BadgeDefaultBackgroundBrush = CreateFrozenBrush("#2B3442");
    private static readonly Brush BadgeDefaultForegroundBrush = CreateFrozenBrush("#F6F8FA");
    private static readonly Brush NativeOverlayBadgeBorderBrush = CreateFrozenBrush("#B40B0D12");
    private static readonly Brush BadgeModeratorBackgroundBrush = CreateFrozenBrush("#168A4A");
    private static readonly Brush BadgePrimeBackgroundBrush = CreateFrozenBrush("#7C4DFF");
    private static readonly Brush BadgeBroadcasterBackgroundBrush = CreateFrozenBrush("#D13232");
    private static readonly Brush BadgeVipBackgroundBrush = CreateFrozenBrush("#D43D8E");
    private static readonly Brush BadgeSubscriberBackgroundBrush = CreateFrozenBrush("#0D7F8C");
    private static readonly Brush BadgeStaffBackgroundBrush = CreateFrozenBrush("#E08C1A");
    private static readonly Brush BadgeVerifiedBackgroundBrush = CreateFrozenBrush("#2E7BEA");
    private static readonly Brush BadgeOgBackgroundBrush = CreateFrozenBrush("#B7791F");
    private static readonly FontFamily ChatTextFontFamily = new("Global User Interface, Segoe UI, Segoe UI Emoji, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic, Nirmala UI, Leelawadee UI, Arial Unicode MS");
    private static readonly Geometry BadgeCrownGeometry = CreateFrozenGeometry("M3,18 L21,18 L19,8 L15,12 L12,5 L9,12 L5,8 Z M5,20 H19 V22 H5 Z");
    private static readonly Geometry BadgeShieldGeometry = CreateFrozenGeometry("M12,2 L20,5.5 V11.5 C20,16.8 16.6,20.2 12,22 C7.4,20.2 4,16.8 4,11.5 V5.5 Z");
    private static readonly Geometry BadgeCameraGeometry = CreateFrozenGeometry("M4,6 H15 C16.1,6 17,6.9 17,8 V10.2 L22,7 V17 L17,13.8 V16 C17,17.1 16.1,18 15,18 H4 C2.9,18 2,17.1 2,16 V8 C2,6.9 2.9,6 4,6 Z");
    private static readonly Geometry BadgeCheckGeometry = CreateFrozenGeometry("M9.6,16.2 L5.4,12 L3.6,13.8 L9.6,19.8 L21,8.4 L19.2,6.6 Z");
    private static readonly Geometry BadgeStarGeometry = CreateFrozenGeometry("M12,2 L14.9,8.5 L22,9.2 L16.6,13.9 L18.2,21 L12,17.3 L5.8,21 L7.4,13.9 L2,9.2 L9.1,8.5 Z");
    private static readonly Geometry BadgeDiamondGeometry = CreateFrozenGeometry("M12,2 L22,9 L12,22 L2,9 Z");
    private static readonly Geometry BadgeGiftGeometry = CreateFrozenGeometry("M4,9 H20 V21 H4 Z M3,6 H21 V10 H3 Z M11,6 V21 H13 V6 Z M8.5,2 C10.4,2 12,3.8 12,6 H9 C7.3,6 6,5.1 6,4 C6,2.9 7.1,2 8.5,2 Z M15.5,2 C16.9,2 18,2.9 18,4 C18,5.1 16.7,6 15,6 H12 C12,3.8 13.6,2 15.5,2 Z");
    private static readonly Geometry BadgeDotGeometry = CreateFrozenGeometry("M12,2 C17.5,2 22,6.5 22,12 C22,17.5 17.5,22 12,22 C6.5,22 2,17.5 2,12 C2,6.5 6.5,2 12,2 Z");
    private static readonly BadgeVisual DefaultBadgeVisual = new(BadgeDotGeometry, BadgeDefaultBackgroundBrush);
    private static readonly IReadOnlyDictionary<string, BadgeVisual> BadgeVisuals = new Dictionary<string, BadgeVisual>(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = new(BadgeShieldGeometry, BadgeStaffBackgroundBrush),
        ["ambassador"] = new(BadgeCheckGeometry, BadgeVerifiedBackgroundBrush),
        ["artist_badge"] = new(BadgeStarGeometry, BadgeVipBackgroundBrush),
        ["bits"] = new(BadgeDiamondGeometry, BadgePrimeBackgroundBrush),
        ["bits_charity"] = new(BadgeDiamondGeometry, BadgePrimeBackgroundBrush),
        ["bits_leader"] = new(BadgeDiamondGeometry, BadgePrimeBackgroundBrush),
        ["bot_badge"] = new(BadgeCheckGeometry, BadgeVerifiedBackgroundBrush),
        ["broadcaster"] = new(BadgeCameraGeometry, BadgeBroadcasterBackgroundBrush),
        ["clip_champ"] = new(BadgeStarGeometry, BadgeStaffBackgroundBrush),
        ["clips_leader"] = new(BadgeStarGeometry, BadgeStaffBackgroundBrush),
        ["founder"] = new(BadgeStarGeometry, BadgeSubscriberBackgroundBrush),
        ["game_developer"] = new(BadgeStarGeometry, BadgeStaffBackgroundBrush),
        ["global_mod"] = new(BadgeShieldGeometry, BadgeModeratorBackgroundBrush),
        ["hype_train"] = new(BadgeStarGeometry, BadgePrimeBackgroundBrush),
        ["moderator"] = new(BadgeShieldGeometry, BadgeModeratorBackgroundBrush),
        ["mod"] = new(BadgeShieldGeometry, BadgeModeratorBackgroundBrush),
        ["no_audio"] = new(BadgeDotGeometry, BadgeDefaultBackgroundBrush),
        ["no_video"] = new(BadgeDotGeometry, BadgeDefaultBackgroundBrush),
        ["og"] = new(BadgeStarGeometry, BadgeOgBackgroundBrush),
        ["partner"] = new(BadgeCheckGeometry, BadgeVerifiedBackgroundBrush),
        ["premium"] = new(BadgeCrownGeometry, BadgePrimeBackgroundBrush),
        ["prime"] = new(BadgeCrownGeometry, BadgePrimeBackgroundBrush),
        ["raider"] = new(BadgeShieldGeometry, BadgeModeratorBackgroundBrush),
        ["sidekick"] = new(BadgeCheckGeometry, BadgeVerifiedBackgroundBrush),
        ["staff"] = new(BadgeShieldGeometry, BadgeStaffBackgroundBrush),
        ["sub_gift_leader"] = new(BadgeGiftGeometry, BadgeSubscriberBackgroundBrush),
        ["sub_gifter"] = new(BadgeGiftGeometry, BadgeSubscriberBackgroundBrush),
        ["subscriber"] = new(BadgeStarGeometry, BadgeSubscriberBackgroundBrush),
        ["turbo"] = new(BadgeDiamondGeometry, BadgePrimeBackgroundBrush),
        ["twitch_dj"] = new(BadgeDiamondGeometry, BadgePrimeBackgroundBrush),
        ["verified"] = new(BadgeCheckGeometry, BadgeVerifiedBackgroundBrush),
        ["vip"] = new(BadgeCheckGeometry, BadgeVipBackgroundBrush),
    };
    private volatile bool subscribed;
    private ChatMessage? catalogMessage;
    private bool rebuildInProgress;
    private bool rebuildQueued;
    private int catalogRebuildQueued;
    private bool hasNativeOverlayEmote;
    private NativeOverlayChatPresentation? nativeOverlayPresentation;
    private readonly List<AnimatedEmoteImage> animatedEmoteImages = [];
    private readonly List<string> bodyInlineImageTexts = [];
    private readonly ClipboardService clipboardService = new();
    private readonly Paragraph messageParagraph = new();
    private TextPointer? bodyStart;
    private TextPointer? bodyEnd;
    private TextRange? nativeOverlaySelectionRange;
    private bool isBuildingMessageBody;

    internal InlineCollection Inlines => messageParagraph.Inlines;
    private TextPointer ContentEnd => messageParagraph.ContentEnd;

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(ChatMessage),
        typeof(DockedChatMessageTextBlock),
        new PropertyMetadata(null, OnRenderPropertyChanged));

    public static readonly DependencyProperty ChatFontSizeProperty = DependencyProperty.Register(
        nameof(ChatFontSize),
        typeof(double),
        typeof(DockedChatMessageTextBlock),
        new PropertyMetadata(13.0, OnRenderPropertyChanged));

    public DockedChatMessageTextBlock()
    {
        FontFamily = ChatTextFontFamily;
        IsReadOnly = true;
        IsUndoEnabled = false;
        IsDocumentEnabled = true;
        Focusable = true;
        IsTabStop = false;
        Cursor = Cursors.IBeam;
        Padding = new Thickness(0);
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = new FlowDocument(messageParagraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = ChatTextFontFamily,
            FontSize = 13,
            Foreground = MessageBrush,
            LineStackingStrategy = LineStackingStrategy.MaxHeight
        };
        messageParagraph.Margin = new Thickness(0);
        SizeChanged += OnSizeChanged;
        AddHandler(Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: true);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ChatMessage? Message
    {
        get => (ChatMessage?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public double ChatFontSize
    {
        get => (double)GetValue(ChatFontSizeProperty);
        set => SetValue(ChatFontSizeProperty, value);
    }

    internal IReadOnlyList<AnimatedEmoteImage> AnimatedEmoteImages => animatedEmoteImages;

    internal TextPointer? MessageBodyStart => bodyStart;

    internal TextPointer? MessageBodyEnd => bodyEnd;

    internal void ApplyNativeOverlayPresentation(NativeOverlayChatPresentation presentation)
    {
        nativeOverlayPresentation = presentation;
        FontFamily = new FontFamily("Segoe UI");
        Document.FontFamily = FontFamily;
        Document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        Effect = new DropShadowEffect
        {
            BlurRadius = 0,
            ShadowDepth = presentation.ShadowOffset,
            Direction = 315,
            Opacity = 1,
            Color = Colors.Black,
            RenderingBias = RenderingBias.Performance
        };
        RebuildInlines();
    }

    private static void OnRenderPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is DockedChatMessageTextBlock textBlock)
        {
            if (e.Property == MessageProperty)
            {
                // Catalog events arrive on workers; do not read a dependency property there.
                Volatile.Write(ref textBlock.catalogMessage, (ChatMessage?)e.NewValue);
                textBlock.EnsureMessageCatalogs();
            }

            textBlock.FontSize = textBlock.ChatFontSize;
            textBlock.Document.FontSize = textBlock.FontSize;
            textBlock.Document.FontFamily = textBlock.FontFamily;
            textBlock.Document.Foreground = textBlock.Foreground;
            textBlock.RebuildInlines();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!subscribed)
        {
            DockedChatEmoteCatalog.Shared.CatalogChanged += OnCatalogChanged;
            DockedChatBadgeCatalog.Shared.CatalogChanged += OnCatalogChanged;
            subscribed = true;
        }

        EnsureMessageCatalogs();
        RebuildInlines();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDocumentWidth(ActualWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        UpdateDocumentWidth(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private void UpdateDocumentWidth(double controlWidth)
    {
        var availableWidth = controlWidth
            - Padding.Left - Padding.Right
            - BorderThickness.Left - BorderThickness.Right;
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        if (!double.IsFinite(Document.PageWidth)
            || Math.Abs(Document.PageWidth - availableWidth) > 0.5)
        {
            Document.PageWidth = availableWidth;
        }

        if (!double.IsFinite(Document.ColumnWidth)
            || Math.Abs(Document.ColumnWidth - availableWidth) > 0.5)
        {
            Document.ColumnWidth = availableWidth;
        }
    }

    private void EnsureMessageCatalogs()
    {
        if (Message is { } message)
        {
            DockedChatEmoteCatalog.Shared.EnsureForMessage(message);
            DockedChatBadgeCatalog.Shared.EnsureForMessage(message);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!subscribed)
        {
            return;
        }

        DockedChatEmoteCatalog.Shared.CatalogChanged -= OnCatalogChanged;
        DockedChatBadgeCatalog.Shared.CatalogChanged -= OnCatalogChanged;
        subscribed = false;
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (e is CatalogChangedEventArgs changes && !changes.Affects(Volatile.Read(ref catalogMessage)))
        {
            return;
        }

        // Catalog notifications arrive on worker threads. Coalesce before posting so a
        // burst cannot enqueue one full inline rebuild per event for every visible row.
        if (!subscribed || Interlocked.Exchange(ref catalogRebuildQueued, 1) != 0)
        {
            return;
        }

        if (!TryQueueOnDispatcher(() =>
        {
            Interlocked.Exchange(ref catalogRebuildQueued, 0);
            if (subscribed)
            {
                RequestRebuild();
            }
        }))
        {
            Interlocked.Exchange(ref catalogRebuildQueued, 0);
        }
    }

    private void RequestRebuild()
    {
        if (rebuildInProgress)
        {
            QueueRebuild();
            return;
        }

        RebuildInlines();
    }

    private void QueueRebuild()
    {
        if (rebuildQueued)
        {
            return;
        }

        rebuildQueued = true;
        if (!TryQueueOnDispatcher(() =>
        {
            rebuildQueued = false;
            RebuildInlines();
        }))
        {
            rebuildQueued = false;
        }
    }

    private bool TryQueueOnDispatcher(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            // Decoration refreshes must yield to input and rendering.
            _ = Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private void RebuildInlines()
    {
        if (rebuildInProgress)
        {
            QueueRebuild();
            return;
        }

        rebuildInProgress = true;
        try
        {
            animatedEmoteImages.Clear();
            bodyInlineImageTexts.Clear();
            bodyStart = null;
            bodyEnd = null;
            isBuildingMessageBody = false;
            hasNativeOverlayEmote = false;
            ClearNativeOverlayTextSelection();
            Inlines.Clear();
            var message = Message;
            if (message is null)
            {
                return;
            }

            if (nativeOverlayPresentation is not null)
            {
                RebuildNativeOverlayInlines(message, nativeOverlayPresentation);
                return;
            }

            AppendRun(message.Timestamp.ToString("HH:mm"), TimestampBrush);
            AppendRun(" ", MessageBrush);
            AppendBadges(message, MessageBrush);
            AppendRun(message.Username, ResolveUsernameBrush(message.Color), FontWeights.SemiBold);
            AppendRun(": ", TimestampBrush);
            AppendTrackedMessageBody(message, MessageBrush);
        }
        finally
        {
            rebuildInProgress = false;
        }
    }

    private void RebuildNativeOverlayInlines(
        ChatMessage message,
        NativeOverlayChatPresentation presentation)
    {
        var isSystem = string.Equals(message.Username, "system", StringComparison.OrdinalIgnoreCase);
        FontSize = presentation.GetFontSize(isSystem);
        FontWeight = isSystem ? FontWeights.Normal : FontWeights.Bold;
        Document.FontSize = FontSize;
        Document.FontWeight = FontWeight;

        var bodyBrush = isSystem ? NativeOverlaySystemBrush : NativeOverlayMessageBrush;
        var prefixBrush = isSystem
            ? NativeOverlaySystemBrush
            : ResolveNativeOverlayUsernameBrush(message.Username);
        AppendBadges(message, bodyBrush);
        AppendRun($"{message.Username}: ", prefixBrush);
        AppendTrackedMessageBody(message, bodyBrush, allowEmotes: !isSystem);

        var contentHeight = presentation.GetFontCellHeight(isSystem);
        if (hasNativeOverlayEmote)
        {
            contentHeight = Math.Max(contentHeight, presentation.EmoteHeight);
        }

        Document.LineHeight = contentHeight + presentation.LineGap;
    }

    private void AppendTrackedMessageBody(
        ChatMessage message,
        Brush bodyBrush,
        bool allowEmotes = true)
    {
        bodyInlineImageTexts.Clear();
        bodyStart = ContentEnd.GetPositionAtOffset(0, LogicalDirection.Backward);
        isBuildingMessageBody = true;
        try
        {
            AppendMessageBody(message, bodyBrush, allowEmotes);
        }
        finally
        {
            isBuildingMessageBody = false;
            bodyEnd = ContentEnd;
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        // TextBlock's built-in copy includes the timestamp and sender. Keep the
        // selection interaction native, but limit the copied range to this row's
        // message body.
        e.Handled = true;
        var text = GetSelectedMessageBodyText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            _ = await clipboardService.TrySetTextAsync(text);
        }
        catch (InvalidOperationException)
        {
            // The WPF dispatcher is STA; this only covers clipboard teardown
            // racing a window shutdown.
        }
    }

    private string? GetSelectedMessageBodyText()
    {
        if (Message is null || bodyStart is null || bodyEnd is null)
        {
            return null;
        }

        var selection = Selection;
        if (selection.IsEmpty)
        {
            return null;
        }

        var start = selection.Start.CompareTo(bodyStart) < 0
            ? bodyStart
            : selection.Start;
        var end = selection.End.CompareTo(bodyEnd) > 0
            ? bodyEnd
            : selection.End;
        if (start.CompareTo(end) >= 0)
        {
            return null;
        }

        return GetMessageBodyText(start, end);
    }

    internal bool TryGetMessageBodyPositionFromPoint(Point point, out TextPointer? position)
    {
        position = null;
        if (bodyStart is null || bodyEnd is null)
        {
            return false;
        }

        var hit = GetPositionFromPoint(point, snapToText: true);
        if (hit is null)
        {
            return false;
        }

        position = ClampToMessageBody(hit);
        return true;
    }

    internal bool TryGetExternalLinkAtPoint(Point point, out Uri? uri)
    {
        uri = null;
        if (bodyStart is null || bodyEnd is null)
        {
            return false;
        }

        var hit = GetPositionFromPoint(point, snapToText: false);
        if (hit is null || hit.CompareTo(bodyStart) < 0 || hit.CompareTo(bodyEnd) > 0)
        {
            return false;
        }

        return TryGetExternalLinkAtPosition(hit, out uri);
    }

    internal bool TryGetExternalLinkAtPosition(TextPointer position, out Uri? uri)
    {
        uri = null;
        if (bodyStart is null || bodyEnd is null ||
            position.CompareTo(bodyStart) < 0 || position.CompareTo(bodyEnd) > 0)
        {
            return false;
        }

        if (TryGetHyperlinkUri(position, out uri))
        {
            return true;
        }

        var previous = position.GetNextInsertionPosition(LogicalDirection.Backward);
        if (previous is not null && TryGetHyperlinkUri(previous, out uri))
        {
            return true;
        }

        var next = position.GetNextInsertionPosition(LogicalDirection.Forward);
        return next is not null && TryGetHyperlinkUri(next, out uri);
    }

    private static bool TryGetHyperlinkUri(TextPointer position, out Uri? uri)
    {
        uri = null;
        var current = position.Parent;
        while (current is not null)
        {
            if (current is Hyperlink hyperlink && ChatLinkParser.IsSupportedWebUri(hyperlink.NavigateUri))
            {
                uri = hyperlink.NavigateUri;
                return true;
            }

            current = LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    internal void SetNativeOverlayTextSelection(TextPointer? start, TextPointer? end)
    {
        ClearNativeOverlayTextSelection();
        if (start is null || end is null || bodyStart is null || bodyEnd is null)
        {
            return;
        }

        start = ClampToMessageBody(start);
        end = ClampToMessageBody(end);
        if (start.CompareTo(end) > 0)
        {
            (start, end) = (end, start);
        }

        var range = new TextRange(start, end);
        if (range.IsEmpty)
        {
            return;
        }

        range.ApplyPropertyValue(TextElement.BackgroundProperty, NativeOverlaySelectionBrush);
        nativeOverlaySelectionRange = range;
    }

    internal string GetMessageBodyText(TextPointer start, TextPointer end)
    {
        if (bodyStart is null || bodyEnd is null)
        {
            return string.Empty;
        }

        start = ClampToMessageBody(start);
        end = ClampToMessageBody(end);
        if (start.CompareTo(end) > 0)
        {
            (start, end) = (end, start);
        }

        if (start.CompareTo(end) >= 0)
        {
            return string.Empty;
        }

        var prefix = new TextRange(bodyStart, start).Text;
        var selected = new TextRange(start, end).Text;
        selected = selected.TrimEnd('\r', '\n');
        var imageIndex = prefix.Count(character => character == '\uFFFC');
        var result = new StringBuilder(selected.Length);
        foreach (var character in selected)
        {
            if (character != '\uFFFC')
            {
                result.Append(character);
                continue;
            }

            if (imageIndex < bodyInlineImageTexts.Count)
            {
                result.Append(bodyInlineImageTexts[imageIndex]);
            }

            imageIndex++;
        }

        return result.ToString();
    }

    private TextPointer ClampToMessageBody(TextPointer position)
    {
        if (bodyStart is null || bodyEnd is null)
        {
            return position;
        }

        if (position.CompareTo(bodyStart) < 0)
        {
            return bodyStart;
        }

        return position.CompareTo(bodyEnd) > 0 ? bodyEnd : position;
    }

    private void ClearNativeOverlayTextSelection()
    {
        if (nativeOverlaySelectionRange is not { } range)
        {
            return;
        }

        nativeOverlaySelectionRange = null;
        try
        {
            range.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
        }
        catch (ArgumentException)
        {
            // Rebuilding a rich-text row can invalidate the previous selection range.
        }
        catch (InvalidOperationException)
        {
            // Rebuilding a rich-text row can invalidate the previous selection range.
        }
    }

    private void AppendBadges(ChatMessage message, Brush spacingBrush)
    {
        if (message.Badges is not { Count: > 0 } badges || string.Equals(message.Username, "system", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var badge in badges.Take(8))
        {
            if (AppendBadge(message, badge))
            {
                AppendRun(" ", spacingBrush);
            }
        }
    }

    private bool AppendBadge(ChatMessage message, ChatBadge badge)
    {
        if (DockedChatBadgeCatalog.Shared.TryGet(message, badge, out var badgeImage))
        {
            AppendBadgeImage(message.Platform, badgeImage, GetBadgeToolTip(badge));
            return true;
        }

        if (message.Platform == PlatformKind.Kick)
        {
            var kickBadge = IsKickGiftBadge(badge) ? badge with { Id = "sub_gifter" } : badge;
            return AppendBadgeGlyph(kickBadge, allowDefaultVisual: false);
        }

        return AppendBadgeGlyph(badge);
    }

    private void AppendMessageBody(ChatMessage message, Brush bodyBrush, bool allowEmotes = true)
    {
        var body = message.Message;
        if (string.IsNullOrEmpty(body))
        {
            return;
        }

        if (nativeOverlayPresentation is not null)
        {
            AppendNativeOverlayMessageBody(message, bodyBrush, allowEmotes);
            return;
        }

        var emotes = allowEmotes
            ? message.Emotes?
            .Where(emote => IsValidRange(emote, body))
            .OrderBy(emote => emote.StartIndex)
            .ThenBy(emote => emote.EndIndex)
            .ToArray()
            : null;
        if (emotes is not { Length: > 0 })
        {
            AppendTextSegment(message, body, allowCatalogEmotes: allowEmotes, bodyBrush);
            return;
        }

        var cursor = 0;
        foreach (var emote in emotes)
        {
            if (emote.StartIndex < cursor)
            {
                continue;
            }

            if (emote.StartIndex > cursor)
            {
                AppendTextSegment(message, body[cursor..emote.StartIndex], allowCatalogEmotes: true, bodyBrush);
            }

            AppendEmoteOrText(message, emote, bodyBrush);
            cursor = emote.EndIndex;
        }

        if (cursor < body.Length)
        {
            AppendTextSegment(message, body[cursor..], allowCatalogEmotes: true, bodyBrush);
        }
    }

    private void AppendNativeOverlayMessageBody(
        ChatMessage message,
        Brush bodyBrush,
        bool allowEmotes)
    {
        var body = message.Message;
        var emotes = allowEmotes
            ? message.Emotes?
                .Where(emote => IsValidRange(emote, body))
                .OrderBy(emote => emote.StartIndex)
                .ThenBy(emote => emote.EndIndex)
                .ToArray()
            : null;
        var pendingSpace = false;
        if (emotes is not { Length: > 0 })
        {
            AppendNativeOverlayTextSegment(
                message,
                body,
                allowCatalogEmotes: allowEmotes,
                bodyBrush,
                ref pendingSpace);
            return;
        }

        var cursor = 0;
        foreach (var emote in emotes)
        {
            if (emote.StartIndex < cursor)
            {
                continue;
            }

            if (emote.StartIndex > cursor)
            {
                AppendNativeOverlayTextSegment(
                    message,
                    body[cursor..emote.StartIndex],
                    allowCatalogEmotes: true,
                    bodyBrush,
                    ref pendingSpace);
            }

            if (pendingSpace)
            {
                AppendRun(" ", bodyBrush);
            }

            pendingSpace = false;
            AppendEmoteOrText(message, emote, bodyBrush);
            cursor = emote.EndIndex;
        }

        if (cursor < body.Length)
        {
            AppendNativeOverlayTextSegment(
                message,
                body[cursor..],
                allowCatalogEmotes: true,
                bodyBrush,
                ref pendingSpace);
        }
    }

    private void AppendNativeOverlayTextSegment(
        ChatMessage message,
        string text,
        bool allowCatalogEmotes,
        Brush bodyBrush,
        ref bool pendingSpace)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                pendingSpace = true;
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                continue;
            }

            if (pendingSpace)
            {
                AppendRun(" ", bodyBrush);
                pendingSpace = false;
            }

            var tokenStart = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            AppendCatalogEmoteOrText(message, text[tokenStart..index], allowCatalogEmotes, bodyBrush);
        }
    }

    private void AppendTextSegment(
        ChatMessage message,
        string text,
        bool allowCatalogEmotes,
        Brush bodyBrush)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                AppendRun(" ", bodyBrush);
                continue;
            }

            var tokenStart = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            AppendCatalogEmoteOrText(message, text[tokenStart..index], allowCatalogEmotes, bodyBrush);
        }
    }

    private void AppendEmoteOrText(ChatMessage message, ChatEmote emote, Brush bodyBrush)
    {
        if (!string.IsNullOrWhiteSpace(emote.ImageUrl) &&
            Uri.TryCreate(emote.ImageUrl, UriKind.Absolute, out var directUri) &&
            string.Equals(directUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            AppendImage(new DockedChatEmoteImage(emote.Code, directUri.ToString(), 28, 28));
            return;
        }

        AppendCatalogEmoteOrText(message, emote.Code, allowCatalogEmotes: true, bodyBrush);
    }

    private void AppendCatalogEmoteOrText(ChatMessage message, string text, bool allowCatalogEmotes, Brush bodyBrush)
    {
        if (allowCatalogEmotes && DockedChatEmoteCatalog.Shared.TryGet(message, text, out var catalogEmote))
        {
            AppendImage(catalogEmote);
            return;
        }

        AppendTextWithLinks(text, bodyBrush);
    }

    private void AppendTextWithLinks(string text, Brush bodyBrush)
    {
        var cursor = 0;
        foreach (var link in ChatLinkParser.FindLinks(text))
        {
            if (link.Start > cursor)
            {
                AppendRun(text[cursor..link.Start], bodyBrush);
            }

            var hyperlink = new Hyperlink(new Run(text.Substring(link.Start, link.Length)))
            {
                NavigateUri = link.Uri,
                Foreground = LinkBrush,
                TextDecorations = TextDecorations.Underline,
                Cursor = Cursors.Hand
            };
            Inlines.Add(hyperlink);
            cursor = link.Start + link.Length;
        }

        if (cursor < text.Length)
        {
            AppendRun(text[cursor..], bodyBrush);
        }
    }

    private void AppendImage(DockedChatEmoteImage emote)
    {
        var height = nativeOverlayPresentation?.EmoteHeight ?? Math.Clamp(ChatFontSize * 1.85, 18, 34);
        var aspect = emote.Width > 0 && emote.Height > 0
            ? nativeOverlayPresentation is null
                ? Math.Clamp(emote.Width / (double)emote.Height, 0.25, 4.0)
                : emote.Width / (double)emote.Height
            : 1.0;
        var width = nativeOverlayPresentation is { } nativePresentation
            ? Math.Clamp(Math.Round(height * aspect), 4, nativePresentation.EmoteMaxWidth)
            : height * aspect;
        var image = new AnimatedEmoteImage
        {
            SizeToSourceAspectRatio = nativeOverlayPresentation is not null,
            Height = height,
            Width = width,
            MaxWidth = nativeOverlayPresentation?.EmoteMaxWidth ?? height * 4,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            ToolTip = emote.Code,
            Margin = nativeOverlayPresentation is null
                ? new Thickness(1, 0, 1, -3)
                : new Thickness(0),
            ImageUrl = emote.ImageUrl
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        animatedEmoteImages.Add(image);
        if (isBuildingMessageBody && image.ToolTip is string imageText)
        {
            bodyInlineImageTexts.Add(imageText);
        }

        hasNativeOverlayEmote |= nativeOverlayPresentation is not null;
        Inlines.Add(new InlineUIContainer(image)
        {
            BaselineAlignment = BaselineAlignment.Center
        });
    }

    private void AppendBadgeImage(PlatformKind platform, DockedChatEmoteImage badge, string toolTip)
    {
        var height = nativeOverlayPresentation?.MessageFontCellHeight ??
            (platform == PlatformKind.Kick
                ? Math.Clamp(ChatFontSize * (18.0 / 13.0), 15, 24)
                : Math.Clamp(ChatFontSize * 1.3, 15, 22));
        var aspect = badge.Width > 0 && badge.Height > 0
            ? nativeOverlayPresentation is null
                ? Math.Clamp(badge.Width / (double)badge.Height, 0.5, 3.0)
                : badge.Width / (double)badge.Height
            : 1.0;
        var width = nativeOverlayPresentation is not null
            ? Math.Clamp(Math.Round(height * aspect), 4, height * 3)
            : height * aspect;
        var image = new AnimatedEmoteImage
        {
            SizeToSourceAspectRatio = nativeOverlayPresentation is not null,
            Height = height,
            Width = width,
            MaxWidth = height * 3,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            ToolTip = string.IsNullOrWhiteSpace(toolTip) ? badge.Code : toolTip,
            Margin = nativeOverlayPresentation is null
                ? new Thickness(0, 0, 2, -2)
                : new Thickness(0),
            ImageUrl = badge.ImageUrl
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        animatedEmoteImages.Add(image);
        Inlines.Add(new InlineUIContainer(image)
        {
            BaselineAlignment = BaselineAlignment.Center
        });
    }

    private bool AppendBadgeGlyph(ChatBadge badge, bool allowDefaultVisual = true)
    {
        var visual = ResolveBadgeVisual(badge, allowDefaultVisual);
        if (visual is null)
        {
            return false;
        }

        var size = nativeOverlayPresentation?.MessageFontCellHeight ?? Math.Clamp(ChatFontSize * 1.3, 15, 22);
        var iconHost = new Grid
        {
            Width = 24,
            Height = 24
        };
        iconHost.Children.Add(new Path
        {
            Data = visual.Geometry,
            Fill = BadgeDefaultForegroundBrush,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(3)
        });

        var badgeElement = new Border
        {
            Width = size,
            Height = size,
            MinWidth = size,
            MinHeight = size,
            Background = visual.Background,
            BorderBrush = nativeOverlayPresentation is null ? null : NativeOverlayBadgeBorderBrush,
            BorderThickness = nativeOverlayPresentation is null ? new Thickness(0) : new Thickness(1),
            CornerRadius = nativeOverlayPresentation is null
                ? new CornerRadius(Math.Clamp(size * 0.22, 3, 5))
                : new CornerRadius(0),
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = iconHost
            },
            ToolTip = GetBadgeToolTip(badge),
            Margin = nativeOverlayPresentation is null
                ? new Thickness(0, 0, 2, -2)
                : new Thickness(0),
            SnapsToDevicePixels = true
        };

        Inlines.Add(new InlineUIContainer(badgeElement)
        {
            BaselineAlignment = BaselineAlignment.Center
        });
        return true;
    }

    private void AppendRun(string text, Brush brush, FontWeight? weight = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var segment in UnicodeEmojiRenderer.EnumerateTextRunSegments(text))
        {
            if (segment.UseEmojiImage)
            {
                AppendEmojiImage(segment.Text, brush, weight);
                continue;
            }

            AppendPlainRun(segment.Text, brush, weight);
        }
    }

    private void AppendPlainRun(string text, Brush brush, FontWeight? weight = null, bool useEmojiFallbackFont = false)
    {
        var run = new Run(text)
        {
            Foreground = brush
        };
        if (useEmojiFallbackFont)
        {
            run.FontFamily = UnicodeEmojiRenderer.FallbackFontFamily;
        }

        if (weight is not null)
        {
            run.FontWeight = weight.Value;
        }

        Inlines.Add(run);
    }

    private void AppendEmojiImage(string text, Brush brush, FontWeight? weight)
    {
        var source = UnicodeEmojiRenderer.GetEmojiImageSource(text, ChatFontSize);
        if (source is null)
        {
            AppendPlainRun(text, brush, weight, useEmojiFallbackFont: true);
            return;
        }

        var height = nativeOverlayPresentation?.EmoteHeight ?? Math.Clamp(ChatFontSize * 1.35, 16, 32);
        var aspect = source.Height > 0
            ? Math.Clamp(source.Width / source.Height, 0.35, 4.0)
            : 1.0;
        var image = new WpfImage
        {
            Source = source,
            Height = height,
            Width = height * aspect,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            ToolTip = text,
            Margin = nativeOverlayPresentation is null
                ? new Thickness(0, 0, 0, -2)
                : new Thickness(0)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        if (isBuildingMessageBody)
        {
            bodyInlineImageTexts.Add(text);
        }

        Inlines.Add(new InlineUIContainer(image)
        {
            BaselineAlignment = BaselineAlignment.Center
        });
    }

    internal static IReadOnlyList<(string Text, bool UseEmojiImage)> SegmentTextForTest(string text) =>
        UnicodeEmojiRenderer.SegmentTextForTest(text);

    internal static int EmojiImageCacheCountForTest => UnicodeEmojiRenderer.EmojiImageCacheCountForTest;

    internal static void AddEmojiImageCacheEntryForTest(string text, int pixelSize) =>
        UnicodeEmojiRenderer.AddEmojiImageCacheEntryForTest(text, pixelSize);

    internal static void ClearEmojiImageCacheForTest() => UnicodeEmojiRenderer.ClearEmojiImageCacheForTest();

    private static bool IsValidRange(ChatEmote emote, string text)
    {
        return emote.StartIndex >= 0 &&
            emote.EndIndex > emote.StartIndex &&
            emote.EndIndex <= text.Length;
    }

    private static Brush ResolveUsernameBrush(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return UsernameFallbackBrush;
        }

        try
        {
            if (ColorConverter.ConvertFromString(color) is Color parsed)
            {
                var brush = new SolidColorBrush(parsed);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
        }

        return UsernameFallbackBrush;
    }

    private static Brush ResolveNativeOverlayUsernameBrush(string username)
    {
        uint hash = 0;
        foreach (var value in Encoding.UTF8.GetBytes(username ?? ""))
        {
            hash = (hash * 31u + value) & 0x7fffffffu;
        }

        return NativeOverlayUsernameBrushes[hash % NativeOverlayUsernameBrushes.Length];
    }

    private static string? FirstBadgeWord(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var word = value.Trim()
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(word) ? null : word.ToUpperInvariant();
    }

    private static string GetBadgeToolTip(ChatBadge badge)
    {
        var title = string.IsNullOrWhiteSpace(badge.Title) ? FirstBadgeWord(badge.Id) ?? "Badge" : badge.Title.Trim();
        return string.IsNullOrWhiteSpace(badge.Version) ||
            title.Contains(badge.Version.Trim(), StringComparison.OrdinalIgnoreCase)
                ? title
                : $"{title} ({badge.Version})";
    }

    private static BadgeVisual? ResolveBadgeVisual(ChatBadge badge, bool allowDefaultVisual)
    {
        var id = KickBadgeIdNormalizer.Normalize(badge.Id);
        if (id.Length == 0)
        {
            return null;
        }

        if (BadgeVisuals.TryGetValue(id, out var visual))
        {
            return visual;
        }

        return allowDefaultVisual ? DefaultBadgeVisual : null;
    }

    private static bool IsKickGiftBadge(ChatBadge badge)
    {
        var id = KickBadgeIdNormalizer.Normalize(badge.Id);
        if (id is "sub_gifter" or "sub_gift_leader")
        {
            return true;
        }

        var title = badge.Title;
        return !string.IsNullOrWhiteSpace(title) &&
            title.Contains("gift", StringComparison.OrdinalIgnoreCase) &&
            title.Contains("sub", StringComparison.OrdinalIgnoreCase);
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
        brush.Freeze();
        return brush;
    }

    private static Geometry CreateFrozenGeometry(string pathData)
    {
        var geometry = Geometry.Parse(pathData);
        if (geometry.CanFreeze)
        {
            geometry.Freeze();
        }

        return geometry;
    }

    private sealed record BadgeVisual(Geometry Geometry, Brush Background);
}
