# Emoji in stream titles

Live cards, Twitch/Kick VOD cards, downloaded VODs, regular/compact tabs,
picture-in-picture headers, pasted-VOD search titles and summaries, and stream-detail tooltips use
`Controls/EmojiTextBlock.cs` with a binding to `SourceText`.

The original title is not rewritten or replaced with a list of example emoji.
Normal text remains in WPF runs. Emoji graphemes are rendered from the installed
Segoe UI Emoji font through the existing SkiaSharp/HarfBuzz dependencies and
inserted as inline images. This is the same renderer used by docked chat,
extracted into `Controls/UnicodeEmojiRenderer.cs` rather than duplicated.

## Rendering and binding

- Bind `SourceText`, not inherited `Text`: rebuilding WPF inlines also changes
  `Text` and must not replace the source binding.
- Grapheme boundaries keep skin tones, ZWJ sequences, and keycaps intact.
  Text-presentation selectors remain normal text.
- Emoji retain the font palette, including naturally neutral glyphs.
- Native WPF wrapping, ellipsis, and font inheritance remain in use. Emoji size
  tracks the title font size. The control clips its complete visual subtree to
  its layout bounds, including inline image children.
- After layout, zero-width native text rectangles identify emoji collapsed by
  ellipsis. Their images receive an empty visual clip, which is removed when
  reflow makes them visible again. This does not change measurement or the title.
- Font-size and DPI changes refresh images. Frozen sources are shared in a
  bounded LRU cache (512 entries and 32 MiB).
- The automation peer exposes the full original title, including emoji.
- Font/native-renderer failures fall back to readable text rather than blank
  titles. No network emoji-image downloads are needed.

## Verification

Run the offscreen title regressions:

```powershell
.\scripts\dev.ps1 Test -Filter 'emoji titles:'
```

Set `SVS_EMOJI_TITLE_ARTIFACTS` to a directory to save rendered card, tooltip,
Unicode-sequence, and wrapping/ellipsis PNGs. The tests load the real templates
and check pixels, bindings, accessibility, styling, clipping, and DPI transitions
without showing windows.

The search-popup regression uses the captured title of Twitch VOD `2888300423`,
including all eleven lock-plus-VS16 graphemes, and renders the real popup at normal
and compact widths. It checks color pixels and the full accessible title in both
the result title and the summary, so a plain `TextBlock` cannot silently bypass
the color renderer in those views.

## Long-title overflow regression

WPF `TextBlock` arranges inline image children on every measured line, while its
text renderer skips lines beyond the available height. Without subtree clipping,
the text ends with an ellipsis but images from the next line paint over the card's
metadata. The same separation leaves collapsed images at zero-width positions on
the ellipsis, where their full image can still paint.

The regression renders the entire real live-card template with a long,
lock-separated title. Before the fix it detects 165 yellow emoji pixels below
the 36-DIP title area. Cropping a bitmap to the title itself misses this failure.
The tests cover Twitch/Kick followed and browse cards, repeated narrowing and
widening, and 100%, 125%, 150%, and 200% DPI. Separate ellipsis checks cover both
trimming modes, both text directions, resizing, and source-title replacement.

Framework source: [TextBlock layout and rendering](https://source.dot.net/PresentationFramework/System/Windows/Controls/TextBlock.cs.html)
and [inline-object arrangement](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/Text/ComplexLine.cs).
