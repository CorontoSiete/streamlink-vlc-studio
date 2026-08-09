using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using StreamlinkVlcStudio.App.Wpf.Chat;
using StreamlinkVlcStudio.Core.Models;
using IoMemoryStream = System.IO.MemoryStream;
using WpfImage = System.Windows.Controls.Image;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public sealed class DockedChatMessageTextBlock : TextBlock
{
    private static readonly Brush TimestampBrush = CreateFrozenBrush("#6F7B8C");
    private static readonly Brush UsernameFallbackBrush = CreateFrozenBrush("#8AB4F8");
    private static readonly Brush MessageBrush = CreateFrozenBrush("#E8EAED");
    private static readonly Brush NativeOverlayMessageBrush = CreateFrozenBrush("#FFFFFF");
    private static readonly Brush NativeOverlaySystemBrush = CreateFrozenBrush("#93C5FD");
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
    private static readonly FontFamily EmojiFallbackFontFamily = new("Segoe UI Emoji");
    private static readonly Lazy<SKTypeface?> EmojiTypeface = new(CreateEmojiTypeface);
    private static readonly object EmojiImageCacheLock = new();
    private static readonly Dictionary<EmojiImageCacheKey, ImageSource?> EmojiImageCache = [];
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
    private bool subscribed;
    private bool rebuildInProgress;
    private bool rebuildQueued;
    private bool hasNativeOverlayEmote;
    private NativeOverlayChatPresentation? nativeOverlayPresentation;
    private readonly List<AnimatedEmoteImage> animatedEmoteImages = [];

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
        TextWrapping = TextWrapping.Wrap;
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

    internal void ApplyNativeOverlayPresentation(NativeOverlayChatPresentation presentation)
    {
        nativeOverlayPresentation = presentation;
        FontFamily = new FontFamily("Segoe UI");
        TextWrapping = TextWrapping.WrapWithOverflow;
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
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
            textBlock.FontSize = textBlock.ChatFontSize;
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

        RebuildInlines();
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
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            RequestRebuild();
            return;
        }

        _ = TryQueueOnDispatcher(RequestRebuild);
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
            _ = Dispatcher.BeginInvoke(action);
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
            hasNativeOverlayEmote = false;
            Inlines.Clear();
            var message = Message;
            if (message is null)
            {
                return;
            }

            DockedChatEmoteCatalog.Shared.EnsureForMessage(message);
            DockedChatBadgeCatalog.Shared.EnsureForMessage(message);

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
            AppendMessageBody(message, MessageBrush);
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

        var bodyBrush = isSystem ? NativeOverlaySystemBrush : NativeOverlayMessageBrush;
        var prefixBrush = isSystem
            ? NativeOverlaySystemBrush
            : ResolveNativeOverlayUsernameBrush(message.Username);
        AppendBadges(message, bodyBrush);
        AppendRun($"{message.Username}: ", prefixBrush);
        AppendMessageBody(message, bodyBrush, allowEmotes: !isSystem);

        var contentHeight = presentation.GetFontCellHeight(isSystem);
        if (hasNativeOverlayEmote)
        {
            contentHeight = Math.Max(contentHeight, presentation.EmoteHeight);
        }

        LineHeight = contentHeight + presentation.LineGap;
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
            AppendTextSegment(body, allowCatalogEmotes: allowEmotes, bodyBrush);
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
                AppendTextSegment(body[cursor..emote.StartIndex], allowCatalogEmotes: true, bodyBrush);
            }

            AppendEmoteOrText(emote, bodyBrush);
            cursor = emote.EndIndex;
        }

        if (cursor < body.Length)
        {
            AppendTextSegment(body[cursor..], allowCatalogEmotes: true, bodyBrush);
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
            AppendEmoteOrText(emote, bodyBrush);
            cursor = emote.EndIndex;
        }

        if (cursor < body.Length)
        {
            AppendNativeOverlayTextSegment(
                body[cursor..],
                allowCatalogEmotes: true,
                bodyBrush,
                ref pendingSpace);
        }
    }

    private void AppendNativeOverlayTextSegment(
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

            var token = text[tokenStart..index];
            if (allowCatalogEmotes &&
                DockedChatEmoteCatalog.Shared.TryGet(token, out var catalogEmote))
            {
                AppendImage(catalogEmote);
            }
            else
            {
                AppendRun(token, bodyBrush);
            }
        }
    }

    private void AppendTextSegment(string text, bool allowCatalogEmotes, Brush bodyBrush)
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

            var token = text[tokenStart..index];
            if (allowCatalogEmotes &&
                DockedChatEmoteCatalog.Shared.TryGet(token, out var catalogEmote))
            {
                AppendImage(catalogEmote);
            }
            else
            {
                AppendRun(token, bodyBrush);
            }
        }
    }

    private void AppendEmoteOrText(ChatEmote emote, Brush bodyBrush)
    {
        if (!string.IsNullOrWhiteSpace(emote.ImageUrl) &&
            Uri.TryCreate(emote.ImageUrl, UriKind.Absolute, out var directUri) &&
            string.Equals(directUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            AppendImage(new DockedChatEmoteImage(emote.Code, directUri.ToString(), 28, 28));
            return;
        }

        if (DockedChatEmoteCatalog.Shared.TryGet(emote.Code, out var catalogEmote))
        {
            AppendImage(catalogEmote);
            return;
        }

        AppendRun(emote.Code, bodyBrush);
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

        foreach (var segment in EnumerateTextRunSegments(text))
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
            run.FontFamily = EmojiFallbackFontFamily;
        }

        if (weight is not null)
        {
            run.FontWeight = weight.Value;
        }

        Inlines.Add(run);
    }

    private void AppendEmojiImage(string text, Brush brush, FontWeight? weight)
    {
        var source = GetEmojiImageSource(text, ChatFontSize);
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

        Inlines.Add(new InlineUIContainer(image)
        {
            BaselineAlignment = BaselineAlignment.Center
        });
    }

    private static IEnumerable<TextRunSegment> EnumerateTextRunSegments(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var segmentText = new StringBuilder();
        bool? segmentUsesEmojiImage = null;

        while (enumerator.MoveNext())
        {
            var textElement = enumerator.GetTextElement();
            var useEmojiImage = UsesEmojiImage(textElement);
            if (segmentUsesEmojiImage is not null && segmentUsesEmojiImage.Value != useEmojiImage)
            {
                yield return new TextRunSegment(segmentText.ToString(), segmentUsesEmojiImage.Value);
                segmentText.Clear();
            }

            segmentText.Append(textElement);
            segmentUsesEmojiImage = useEmojiImage;
        }

        if (segmentText.Length > 0 && segmentUsesEmojiImage is not null)
        {
            yield return new TextRunSegment(segmentText.ToString(), segmentUsesEmojiImage.Value);
        }
    }

    private static bool UsesEmojiImage(string textElement)
    {
        var hasEmojiPresentationSelector = false;
        var hasTextPresentationSelector = false;
        var hasEmojiBase = false;
        var hasEmojiSequenceRune = false;

        foreach (var rune in textElement.EnumerateRunes())
        {
            if (rune.Value == 0xFE0F)
            {
                hasEmojiPresentationSelector = true;
                continue;
            }

            if (rune.Value == 0xFE0E)
            {
                hasTextPresentationSelector = true;
                continue;
            }

            hasEmojiSequenceRune |= IsEmojiSequenceRune(rune.Value);
            hasEmojiBase |= IsEmojiBaseRune(rune.Value);
        }

        if (hasTextPresentationSelector)
        {
            return false;
        }

        if (hasEmojiPresentationSelector)
        {
            return true;
        }

        return hasEmojiBase || hasEmojiSequenceRune;
    }

    private static ImageSource? GetEmojiImageSource(string text, double chatFontSize)
    {
        var pixelSize = Math.Clamp((int)Math.Ceiling(chatFontSize * 2.4), 28, 96);
        var key = new EmojiImageCacheKey(text, pixelSize);
        lock (EmojiImageCacheLock)
        {
            if (EmojiImageCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var rendered = RenderEmojiImageSource(text, pixelSize);
        lock (EmojiImageCacheLock)
        {
            if (!EmojiImageCache.ContainsKey(key))
            {
                EmojiImageCache[key] = rendered;
            }

            return EmojiImageCache[key];
        }
    }

    private static ImageSource? RenderEmojiImageSource(string text, int pixelSize)
    {
        try
        {
            var typeface = EmojiTypeface.Value;
            if (typeface is null)
            {
                return null;
            }

            // GDI+ treats Segoe UI Emoji as a monochrome outline font. Skia's HarfBuzz
            // shaper understands the font's color glyph tables and also keeps ZWJ,
            // skin-tone, flag, and keycap sequences as one rendered emoji.
            var canvasWidth = pixelSize * 8;
            var canvasHeight = pixelSize * 3;
            using var font = new SKFont(typeface, pixelSize);
            using var shaper = new SKShaper(typeface);
            using var paint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };
            using var bitmap = new SKBitmap(
                canvasWidth,
                canvasHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawShapedText(
                    shaper,
                    text,
                    pixelSize,
                    pixelSize * 2,
                    font,
                    paint);
            }

            var bounds = GetVisibleBounds(bitmap);
            if (bounds is not { } visibleBounds)
            {
                return null;
            }

            using var cropped = new SKBitmap(
                visibleBounds.Width,
                visibleBounds.Height,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            if (!bitmap.ExtractSubset(cropped, visibleBounds))
            {
                return null;
            }

            using var data = cropped.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new IoMemoryStream();
            data.SaveTo(stream);
            stream.Position = 0;

            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = stream;
            source.EndInit();
            if (source.CanFreeze)
            {
                source.Freeze();
            }

            return source;
        }
        catch (Exception ex) when (ex is ArgumentException or
            InvalidOperationException or
            DllNotFoundException or
            EntryPointNotFoundException or
            TypeInitializationException)
        {
            return null;
        }
    }

    private static SKTypeface? CreateEmojiTypeface()
    {
        try
        {
            return SKTypeface.FromFamilyName("Segoe UI Emoji");
        }
        catch (Exception ex) when (ex is DllNotFoundException or
            EntryPointNotFoundException or
            TypeInitializationException)
        {
            return null;
        }
    }

    private static SKRectI? GetVisibleBounds(SKBitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
        {
            return null;
        }

        const int padding = 1;
        left = Math.Max(0, left - padding);
        top = Math.Max(0, top - padding);
        right = Math.Min(bitmap.Width - 1, right + padding);
        bottom = Math.Min(bitmap.Height - 1, bottom + padding);
        return new SKRectI(left, top, right + 1, bottom + 1);
    }

    private static bool IsEmojiSequenceRune(int scalar)
    {
        return scalar is
            0x200D or // Zero-width joiner.
            0x20E3 or // Combining enclosing keycap.
            >= 0x1F3FB and <= 0x1F3FF; // Emoji skin tone modifiers.
    }

    private static bool IsEmojiBaseRune(int scalar)
    {
        return scalar is
            >= 0x1F000 and <= 0x1FAFF or
            0x231A or
            0x231B or
            >= 0x23E9 and <= 0x23EC or
            0x23F0 or
            0x23F3 or
            0x25FD or
            0x25FE or
            0x2614 or
            0x2615 or
            >= 0x2648 and <= 0x2653 or
            0x267F or
            0x2693 or
            0x26A1 or
            0x26AA or
            0x26AB or
            0x26BD or
            0x26BE or
            0x26C4 or
            0x26C5 or
            0x26CE or
            0x26D4 or
            0x26EA or
            0x26F2 or
            0x26F3 or
            0x26F5 or
            0x26FA or
            0x26FD or
            0x2705 or
            0x270A or
            0x270B or
            0x2728 or
            0x274C or
            0x274E or
            >= 0x2753 and <= 0x2755 or
            0x2757 or
            >= 0x2795 and <= 0x2797 or
            0x27B0 or
            0x27BF or
            0x2B1B or
            0x2B1C or
            0x2B50 or
            0x2B55 or
            0x3030 or
            0x303D or
            0x3297 or
            0x3299;
    }

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
        var id = NormalizeBadgeStyleId(badge.Id);
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
        var id = NormalizeBadgeStyleId(badge.Id);
        if (id is "sub_gifter" or "sub_gift_leader")
        {
            return true;
        }

        var title = badge.Title;
        return !string.IsNullOrWhiteSpace(title) &&
            title.Contains("gift", StringComparison.OrdinalIgnoreCase) &&
            title.Contains("sub", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBadgeStyleId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "";
        }

        var normalized = id.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "channel_host" => "broadcaster",
            "creator" => "broadcaster",
            "gift_sub" => "sub_gifter",
            "gift_subs" => "sub_gifter",
            "gift_subscriber" => "sub_gifter",
            "gift_subscription" => "sub_gifter",
            "gifted_sub" => "sub_gifter",
            "gifted_subs" => "sub_gifter",
            "gifted_subscriber" => "sub_gifter",
            "gifted_subscription" => "sub_gifter",
            "gifter" => "sub_gifter",
            "subgifter" => "sub_gifter",
            "sub_gift" => "sub_gifter",
            "sub_gifter_badge" => "sub_gifter",
            "sub_gifts" => "sub_gifter",
            "subscriber_gifter" => "sub_gifter",
            "subscription" => "subscriber",
            "subscription_gift" => "sub_gifter",
            "subscription_gifts" => "sub_gifter",
            "sub" => "subscriber",
            _ => normalized
        };
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
    private sealed record TextRunSegment(string Text, bool UseEmojiImage);
    private sealed record EmojiImageCacheKey(string Text, int PixelSize);
}
