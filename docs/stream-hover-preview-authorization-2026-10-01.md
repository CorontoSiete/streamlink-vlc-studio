# Uneven hover preview authorization, 2026-10-01

The previews reported as slow, Twitch `tttcheekyttt` and `termynater_`, now start
visible video in median **1,692 ms** and **1,492 ms**, respectively. Three
alternating old/new pairs per channel measured a **64.44%** and **62.70%**
reduction in the wait. This baseline already includes the earlier MP4 and
frame-rate quality fix.

## Verified cause

The latest application log showed repeated `InvalidDataException` fallbacks for
these two channels after the earlier fix. The preserved current .NET resolver
reproduced rejection in `LivePreviewPlaylist.Rewrite` for both. Fresh provider
responses established that `tttcheekyttt` publishes `360p30` MP4, and
`termynater_` publishes `360p` MPEG-TS. Both initially returned real pre-roll
segments: their segment titles contained `Amazon`, and their date ranges
identified `twitch-stitched-ad`.

The direct preview borrowed the general web GraphQL transport and generated a
new `X-Device-Id` on every authorization request. Streamlink's anonymous
`PlaybackAccessToken` request omits that header. Instrumenting the installed
Streamlink 8.4.0 showed ordinary live segments for the same channel, without ad
segments or a stitched-ad range. The installed behavior agrees with the
[upstream Twitch plugin](https://github.com/streamlink/streamlink/blob/8.4.0/src/streamlink/plugins/twitch.py).

Controlled HTTP comparisons then isolated the differing field. For each
channel, three alternating device-present/device-absent pairs changed only
`X-Device-Id`, retaining the token operation, variables, quality, user agent,
other headers and playback URL parameters:

| Channel | With fresh device ID, each of 3 trials | Without device ID, each of 3 trials |
| --- | --- | --- |
| `tttcheekyttt` | 4 MP4 ad segments, 1 stitched-ad range | 14 live segments, 0 ad segments/ranges |
| `termynater_` | 3 TS ad segments, 1 stitched-ad range | 15 live segments, 0 ad segments/ranges |

Changing the JSON content type or removing fetch metadata headers while retaining
the device ID continued to return ads. Earlier comparisons of referrer, origin
and user agent did not resolve the reproducible `termynater_` case. The first
`tttcheekyttt` comparison temporarily returned live content in all cases; the
subsequent isolated comparisons reproduced its device-dependent response.
The signed tokens continued to have `adblock=false`, `hide_ads=false`,
`server_ads=true` and `show_ads=true` in both modes.

[All isolated comparisons](measurements/hover-preview-authorization-2026-10-01/authorization-isolation.json),
[the initial comparisons](measurements/hover-preview-authorization-2026-10-01/initial-request-context-comparison.json),
and [the instrumented Streamlink trace](measurements/hover-preview-authorization-2026-10-01/streamlink-http-tttcheekyttt.log)
retain the evidence without device values, playback tokens or signed URLs.

The original preview rejected the ad playlist, launched Python, authorized the
channel again and checked every available quality. In the first paired trial,
`tttcheekyttt` rejected the direct path at 1,034 ms, finished Streamlink resolution
at 4,210 ms and displayed video at 5,180 ms. `termynater_` rejected it at 900 ms,
finished resolution at 3,333 ms and displayed video at 4,123 ms.

## Change

`LivePreviewSourceResolver` now requests Twitch playback authorization with
Streamlink's anonymous identity. `TwitchGraphQlTransport` accepts an explicitly
null device ID and omits that header; callers that supply their web identity and
OAuth token continue to send them. Empty supplied device IDs remain invalid.

The preview still obtains a fresh provider authorization, requests its selected
quality, validates all playlists and uses the existing decoder. The 100 ms hover
dwell, quality preference, output size, buffering, URL restrictions, deadlines
and cleanup are unchanged. Provider ads, failed authorization, unsupported
playlists and custom Streamlink configuration retain the existing fallback.

## Visible startup measurements

Each trial used a fresh process and physically hovered the production WPF card,
observing its first bitmap at 100 ms intervals. Every trial verified changing
pixels, then disabled previews and verified image clearing and completed cleanup.
The order was before/after, after/before, before/after. No builds or other tests
ran during these measurements. These are local results for the current broadcasts.

| Channel | Pair | Order | Original visible video | Updated visible video |
| --- | --- | --- | ---: | ---: |
| `tttcheekyttt` | 1 | Before, after | 5,180 ms | 1,701 ms |
| `tttcheekyttt` | 2 | After, before | 4,758 ms | 1,593 ms |
| `tttcheekyttt` | 3 | Before, after | 4,663 ms | 1,692 ms |
| `termynater_` | 1 | Before, after | 4,123 ms | 1,493 ms |
| `termynater_` | 2 | After, before | 3,793 ms | 1,492 ms |
| `termynater_` | 3 | Before, after | 4,000 ms | 1,486 ms |

All six original runs started Streamlink fallback; all six updated runs used the
direct path. [Individual runs and binary hashes](measurements/hover-preview-authorization-2026-10-01/paired-results.json)
and all twelve `card-*.log` files are saved beside this report. Captured updated
card images were visually checked and showed the channels' live game video.

Both updated native players also continued across playlist reloads for 40 seconds:

| Channel | Selected quality | Changing frames | Largest presentation gap | Cancellation/cleanup | Fallbacks |
| --- | --- | ---: | ---: | ---: | ---: |
| `tttcheekyttt` | `360p30` | 641 | 147 ms | 74 ms | 0 |
| `termynater_` | `360p` | 628 | 169 ms | 74 ms | 0 |

The corresponding `continuity-*-after.log` files record authorization, quality
selection, every media-playlist refresh and the final counters. These continuity
probes measure decoded frames separately from the visible-card trials.

Both builds use the incoming working tree at Git revision
`933bb5f24c0aafaa336d7227defc6fddc4394ba1`, .NET SDK 10.0.302, Streamlink 8.4.0
and installed VLC 3.0.23. Original binaries were preserved before production
edits. Test settings were loaded in memory; the saved settings file's SHA-256
was identical before and after the measurements.

## Verification

- Release solution build with warnings treated as errors: zero warnings/errors.
- Formatting verification and whitespace checks passed for the changed files.
- Full headless suite: **1,486 passed, 269 skipped, zero failures or timeouts**.
  The complete result is saved in `full-after.log` beside the measurements.
- **38 focused tests passed, zero skipped**, including physical card interaction,
  real native TS and MP4 decoding, ad handling, deadlines, cancellation and cleanup.
- The new TS and MP4 authorization regressions both fail with the preserved
  original infrastructure DLL and pass with the updated DLL. The original run
  passed the other 36 checks. Both test runs are saved as `focused-*.log`.
- Additional regressions verify fallback after GraphQL, HTTP, malformed JSON and
  empty-token failures, and preserve existing web device/OAuth identity.
- All **12 live production-card trials passed**, plus both 40-second native
  continuity probes.

Run the focused checks with:

```powershell
.\scripts\dev.ps1 Test -Filter 'stream hover preview:' -Interactive
```

To exercise both reported channels with the existing live-provider tests:

```powershell
$env:SVS_TEST_TIMEOUT_SECONDS = '75'
$env:SVS_TEST_HOVER_DURATION_SECONDS = '40'
foreach ($channel in @('tttcheekyttt', 'termynater_')) {
    $env:SVS_TEST_HOVER_CHANNEL = "https://www.twitch.tv/$channel"
    .\scripts\dev.ps1 Test -NoBuild -Filter 'stream hover preview:' -Interactive
}
```

Local preserved builds, diagnostic scripts, complete working logs and captured
card images are in `.tmp/hover-preview-variants-2026-10-01/`.
