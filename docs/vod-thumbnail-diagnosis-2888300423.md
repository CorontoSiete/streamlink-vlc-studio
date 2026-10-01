# Blank search thumbnail: Twitch VOD 2888300423

Diagnosed on 2026-10-01 for [the supplied Twitch VOD](https://www.twitch.tv/videos/2888300423).

The blank thumbnail has two verified causes: Twitch initially supplied an
unavailable processing-image URL, and the completed search row retains that
metadata without refreshing it. The production bindings and image decoder render
this VOD's real thumbnail successfully once Twitch supplies its URL.

## Twitch's response changed during the investigation

The earlier production-service response, fetched at **05:35:38.685594 UTC**,
contains:

```text
https://vod-secure.twitch.tv/_404/404_processing_440x248.png
```

The exact original response is retained in
[the metadata capture](../.artifacts/vod-thumbnail-diagnosis-2888300423/with-refresh/captured-processing-metadata.json).
Its SHA-256 is
`619C54679C3E71866C244447D45584B2A509C70F072EFB9D12DD6CA46AB84B9B`.
This is the unmodified response captured by the preceding live VOD investigation,
including Twitch's request ID. A fresh metadata request at 05:40:30 UTC still
returned this URL. The
[additional response saved at 05:41:37 UTC](../.artifacts/vod-thumbnail-diagnosis-2888300423/related-response.json)
also contains the processing URL.

Fetching that processing URL through the **actual static HTTP client used by
`AnimatedEmoteImage`**, including its normal user agent and image headers, returns
**HTTP 403**, `text/html`, with an **“Object not found”** response. The diagnostic
also captures the production control's own HTTP requests, which receive the same
403. See the
[response body](../.artifacts/vod-thumbnail-diagnosis-2888300423/with-refresh/captured-processing-thumbnail-response.html)
and [request events](../.artifacts/vod-thumbnail-diagnosis-2888300423/with-refresh/network-events.json).

At **05:43:20 UTC**, a new call to the production `TwitchVodService.GetVideoAsync`
returned a different URL for this same VOD:

```text
https://static-cdn.jtvnw.net/cf_vods/d1m7jfoe9zdc1j/22724ff546260b30cf12_xqc_321603111897_1790796247//thumb/thumb0-440x248.jpg
```

That URL returned **HTTP 200**, `image/jpeg`, and 21,026 bytes. The initial
diagnostic stopped at its changed-state assertion and preserved this transition
in [its measurements](../.artifacts/vod-thumbnail-diagnosis-2888300423/live/measurements.json)
and [raw response](../.artifacts/vod-thumbnail-diagnosis-2888300423/live/service-twitch-response-1.json).
The subsequent diagnostic separately verifies current live metadata and replays
the exact earlier captured metadata. Both metadata requests and current successful
image requests work without account authorization.

This establishes the thumbnail failure and subsequent availability. It does not
establish why Twitch's processing placeholder asset returns 403, or identify a
Twitch internal processing error.

## Verified production path

| Source location | Verified behavior |
| --- | --- |
| `src/StreamlinkVlcStudio.Infrastructure/Viewers/TwitchVodService.cs:29` | Requests `previewThumbnailURL(width: 440, height: 248)` for the requested video ID. |
| `src/StreamlinkVlcStudio.Infrastructure/Viewers/TwitchVodService.cs:102` | Copies Twitch's returned URL directly into `TwitchVodItem.ThumbnailUrl`. |
| `src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamSearchViewModel.cs:355` | Looks up the explicit VOD and enriches its search metadata. |
| `src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamSearchViewModel.cs:389` | Copies `video.ThumbnailUrl` into `StreamMetadataResult`. |
| `src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamSearchResultViewModel.cs:73` | Exposes the metadata's thumbnail URL without changing it. |
| `src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamSearchResultViewModel.cs:75` | `HasThumbnail` checks only that a URL is nonempty. It is true for the unavailable processing URL. |
| `src/StreamlinkVlcStudio.App.Wpf/MainWindow.xaml:2296` | Binds the search card's `AnimatedEmoteImage.ImageUrl` to `ThumbnailUrl`. A monitor glyph sits behind the image. |
| `src/StreamlinkVlcStudio.App.Wpf/Controls/AnimatedEmoteImage.cs:710` | Rejects a non-success HTTP response before decoding. |
| `src/StreamlinkVlcStudio.App.Wpf/Controls/AnimatedEmoteImage.cs:369` | Collapses the image when loading returns null, leaving the monitor glyph visible. |

The harness verifies ordinal equality from the captured response through the
production service, search result, and control's bound `ImageUrl`. In the failing
popup, the image has `Source = null`, `Visibility = Collapsed`, and no pending
load. Its 96 × 54 thumbnail border and the 22 × 22 monitor fallback remain
visible. This reproduces the thumbnail shown in the supplied screenshot.

## Rendering and recovery checks

| Check | Observed result |
| --- | --- |
| Actual search popup using earlier captured metadata and live image requests | Processing URL receives 403; image collapses; monitor fallback remains. |
| Fresh actual search using current live metadata | This VOD's JPEG receives 200 and renders through the original `ThumbnailUrl` binding. |
| Same failing image control given this VOD's currently available image URL, in memory | Renders a 440 × 248 decoded image in the same 96 × 54 slot. No layout or decoder change is required. |
| Another control loads the processing URL within the failure-cache window | Reuses the cached null result without another network request. |
| Existing failed row remains after the real 30-second cache lifetime | Remains collapsed and makes no automatic image or metadata request. |
| New control loads the processing URL after natural cache expiry | Makes a fresh request; the old processing URL still returns 403. |
| Clear the original search and paste the same VOD URL again, using live metadata for the new request | Fetches the current URL and renders the real thumbnail through the production binding. |

The source image contains 109,120 visible pixels. The search slot renders 4,888
image pixels. The current live popup is verified without an in-memory URL
substitution. The separate same-control comparison is explicitly recorded as an
in-memory diagnostic.

The failure cache is **30 seconds**, defined at
`AnimatedEmoteImage.cs:25`. Expiry is checked when `GetOrLoadImageAsync` is called;
it does not schedule a retry of an existing control. The search result's thumbnail
is derived from its stored metadata. The search runs when input changes, so cache
expiry cannot replace a processing URL with the different URL Twitch later
returns. Clearing the input before repasting triggers the new metadata lookup.

The existing VOD link metadata UI test checks `HasThumbnail` and renders the tab
avatar. It does not load and verify the search popup's thumbnail. Its nonempty-URL
assertion therefore does not cover this unavailable processing image.

## Evidence and scope

- [Earlier-state popup](../.artifacts/vod-thumbnail-diagnosis-2888300423/with-refresh/search-popup-actual-failure.png).
- [Current live popup](../.artifacts/vod-thumbnail-diagnosis-2888300423/with-refresh/search-popup-current-live.png).
- [Popup after clearing and repasting](../.artifacts/vod-thumbnail-diagnosis-2888300423/with-refresh/search-popup-after-clear-and-repaste.png).
- [Measurements and response headers](../.artifacts/vod-thumbnail-diagnosis-2888300423/with-refresh/measurements.json).
- [Actual HTTP request events](../.artifacts/vod-thumbnail-diagnosis-2888300423/with-refresh/network-events.json).
- [Fresh metadata response after clearing](../.artifacts/vod-thumbnail-diagnosis-2888300423/with-refresh/after-clear-live-twitch-response.json).
- [Screenshot comparison](../.artifacts/vod-thumbnail-diagnosis-2888300423/with-refresh/screenshot-comparison.json).
- [Build verification](../.artifacts/vod-thumbnail-diagnosis-2888300423/build-verification.log): zero warnings and zero errors with warnings treated as errors.
- [Diagnostic source](../.artifacts/vod-thumbnail-diagnosis-2888300423/Program.cs).

The diagnostic loads the production `HomeStreamSearchPopup` XAML, bindings,
resources, dark palette, and view models. It renders offscreen at 560 × 189
pixels. Playback and settings use the repository's existing test doubles; no
playback is started and no user settings are changed. The earlier-state metadata
comes from a captured real Twitch response, while image downloads and the current
and refreshed metadata requests use the live network.

The diagnostic app assembly's SHA-256 is
`8ADE33ABB6A9D9A012CD8EACEE97D9CDD5CD9FACAACA2357899808757BE91E19`,
matching the app assembly recorded by the preceding launch. Application source
files are unchanged. The supplied screenshot and earlier-state render have the
same dimensions, but differ in 5,657 pixels across the whole popup; the reproduction
is not claimed to be pixel identical.
The measured thumbnail rectangle differs in 152 pixels; its monitor fallback and
absence of decoded thumbnail pixels are independently verified by the control state.

To repeat the diagnosis from the repository root:

```powershell
.\.dotnet-sdk\dotnet.exe run `
  --project .artifacts/vod-thumbnail-diagnosis-2888300423/ThumbnailDiagnosis.csproj `
  --configuration Release -- `
  .artifacts/vod-thumbnail-diagnosis-2888300423/recheck `
  .artifacts/vod-emoji-update-2888300423/live/service-twitch-response-2.json
```

The demonstrated product correction is to recognize the processing thumbnail
state and refresh the VOD metadata with a bounded retry schedule, canceling it
when the search changes. Updating the row with Twitch's newly returned image URL
allows the existing control to render it. A visible processing state would also
explain the temporary fallback. This diagnosis does not apply that product change.
