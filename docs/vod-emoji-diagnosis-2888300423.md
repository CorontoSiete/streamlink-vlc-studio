# Emoji diagnosis for Twitch VOD 2888300423

Verified on 2026-10-01, with live metadata requests at 05:30:04 UTC.
VOD: [https://www.twitch.tv/videos/2888300423](https://www.twitch.tv/videos/2888300423).

Follow-up: the correction was applied to both visible search strings and the
updated Release build was launched on 2026-10-01 at 05:37:40 UTC. The new search
popup regression fails against the original controls and passes with the fix;
all twelve emoji title tests pass. A live VOD check of the rebuilt template
measures 345 colored title pixels and 304 colored summary pixels, with the
original eleven lock graphemes retained. See the
[fixed popup](../.artifacts/vod-emoji-update-2888300423/live/search-popup-fixed.png),
[verification measurements](../.artifacts/vod-emoji-update-2888300423/live/measurements.json),
and [launch record](../.artifacts/vod-emoji-update-2888300423/launch.json).
The diagnosis below records the original build before that correction.

The successful VOD search popup uses ordinary WPF `TextBlock` controls for its
summary and result title. Those controls draw the lock glyph's outline with the
text foreground brush. They bypass the app's existing `EmojiTextBlock` color
renderer. This accounts for the white/gray outline locks in the supplied image.

## Confirmed data and rendering path

The public Twitch metadata endpoint returned HTTP 200 for the requested video,
owned by `xqc`, with a title containing eleven `U+1F512` lock characters. Every
lock is followed by `U+FE0F`. Unicode defines that selector as a request for emoji
presentation. [Unicode UTS #51, ED-9](https://www.unicode.org/reports/tr51/#def_emoji_presentation_selector).

The diagnostic harness called the actual `TwitchVodService.GetVideoAsync` and
then submitted the VOD URL to the actual `MainViewModel` automatic search. It
checked ordinal string equality through all of these steps:

1. Twitch response title to `TwitchVodItem.Title`.
2. Service title to `StreamSearchResultViewModel.Target.DisplayTitle`.
3. Service title to `StreamSearchResultViewModel.DisplayName`.
4. The complete title in `MainViewModel.StreamSearchStatus`, following the
   `Twitch VOD found: ` prefix.

All eleven lock characters and selectors survived. The fetched title contains
zero replacement characters (`U+FFFD`) and zero text-presentation selectors
(`U+FE0E`).

Relevant source locations:

| Location | Observed behavior |
| --- | --- |
| `src/StreamlinkVlcStudio.Infrastructure/Viewers/TwitchVodService.cs:99` | Reads the JSON title directly. |
| `src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamSearchViewModel.cs:380` | Copies the VOD title into the search target. |
| `src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamSearchResultViewModel.cs:69` | Uses the explicit VOD target's tab title as the result display name. |
| `src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamSearchViewModel.cs:611` | Includes that title in the search summary. |
| `src/StreamlinkVlcStudio.App.Wpf/MainWindow.xaml:2146` | Summary uses `TextBlock.Text` bound to `StreamSearchStatus`. |
| `src/StreamlinkVlcStudio.App.Wpf/MainWindow.xaml:2329` | Result title uses `TextBlock.Text` bound to `DisplayName`. |

The harness loaded the actual `HomeStreamSearchPopup` XAML and rendered its
content offscreen at 560 pixels wide. It retained the real bindings, resources,
dark palette, typography, and search view model. Playback and settings services
used the repository's existing test doubles; metadata and search processing
used production code and live Twitch responses. No playback was started.

Inspection of `VisualTreeHelper.GetDrawing` confirmed the actual lock glyph runs
selected `C:\WINDOWS\FONTS\SEGUIEMJ.TTF`, family `Segoe UI Emoji`. The title's
lock runs had foreground brush `#FFF2F4F8`; the summary's had `#FFA0ACBD`.
Microsoft documents `GlyphRunDrawing.ForegroundBrush` as the brush that paints
the glyph run. [Microsoft API documentation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.glyphrundrawing.foregroundbrush?view=windowsdesktop-10.0).

These observed glyph runs and their rendered pixels establish that the installed
emoji font is selected successfully, while this WPF text path paints the outlines
with the text color.

## Controlled comparison

The harness replaced the two visible text controls **in memory** with the app's
existing `EmojiTextBlock`, moving the original text bindings to `SourceText`.
It copied the resolved fonts, foreground colors, trimming, and layout values.
The product source files were not edited.

| Actual popup element | Font | Rendered size, pixels | Current colored pixels | Color-control comparison |
| --- | --- | --- | --- | --- |
| Result title | 14, SemiBold | 297 × 19 | 0 | 345 |
| Search summary | 11, Normal | 534 × 13 | 0 | 304 |

For these measurements a colored pixel has alpha greater than 32 and a maximum
minus minimum RGB channel value greater than 32. The title and summary were
rendered individually, so platform badges and category accents cannot contribute
to the counts.

The replacement title created eleven inline images, each retaining the exact
lock-plus-selector grapheme. Each source bitmap contained 457 colored pixels;
five title images remained visible after the original ellipsis layout. The
complete reconstructed title equaled the live Twitch title.

A separate comparison of the exact first grapheme at font size 28 produced:

| Rendering path | Colored pixels |
| --- | --- |
| Ordinary `TextBlock`, app font | 0 |
| Ordinary `TextBlock`, explicit `Segoe UI Emoji` | 0 |
| Existing `EmojiTextBlock` | 228 |

This directly verifies that assigning the emoji font alone does not restore its
palette in the tested WPF text path. The existing Skia/HarfBuzz renderer loaded
the installed `Segoe UI Emoji` typeface and rendered yellow locks successfully.

Both the supplied screenshot and the offscreen popup render are 560 × 189
pixels. Their RGBA comparison has 5,609 differing pixels, so the reproduction is
not claimed to be pixel identical. The observed outline locks, source controls,
glyph runs, and controlled comparison establish the rendering failure.

## Coverage and correction

The solution built with .NET SDK 10.0.302 with zero warnings and zero errors.
All eleven existing `emoji titles:` tests passed, with zero skips. Those tests
cover VOD cards, live cards, downloads, tabs, tooltips, Unicode sequences, DPI,
wrapping, and ellipsis. They do not render this search popup. The VOD metadata
tests verify title strings and tab avatars without checking the search title's
emoji pixels.

The demonstrated correction is to use `controls:EmojiTextBlock` with `SourceText`
for the successful popup's summary and result title at the two XAML locations
above. The empty-state summary at line 2188 also uses an ordinary `TextBlock`;
it is a separate hidden element during this successful VOD lookup. A regression
should render the actual search popup with the captured title and check both
visible text elements, original text, and ellipsis behavior.

## Reproduction and evidence

From the repository root, rebuild the current app and existing tests:

```powershell
.\scripts\dev.ps1 Test -Filter 'emoji titles:' -ExpectedMaxSkips 0
```

Verify the fixed build with live metadata, retaining the captured title's eleven
locks and selectors:

```powershell
.\.dotnet-sdk\dotnet.exe run --project .artifacts/vod-emoji-diagnosis-2888300423/EmojiDiagnosis.csproj --configuration Release -- .artifacts/vod-emoji-update-2888300423/live --verify-fixed
```

Evidence is in `.artifacts/vod-emoji-diagnosis-2888300423/`:

- [Popup comparison](../.artifacts/vod-emoji-diagnosis-2888300423/search-popup-comparison.png).
- [Exact lock rendered through three paths](../.artifacts/vod-emoji-diagnosis-2888300423/same-lock-three-renderers.png).
- [Measurements, glyph runs, live-request times, and assembly hash](../.artifacts/vod-emoji-diagnosis-2888300423/measurements.json).
- [First production-service Twitch response](../.artifacts/vod-emoji-diagnosis-2888300423/service-twitch-response-1.json) and [search metadata response](../.artifacts/vod-emoji-diagnosis-2888300423/service-twitch-response-2.json).
- [Diagnostic source](../.artifacts/vod-emoji-diagnosis-2888300423/Program.cs) and [run log](../.artifacts/vod-emoji-diagnosis-2888300423/diagnostic-run.log).
- [Supplied screenshot](../.artifacts/vod-emoji-diagnosis-2888300423/user-screenshot.png) and [pixel comparison](../.artifacts/vod-emoji-diagnosis-2888300423/screenshot-comparison.json).
