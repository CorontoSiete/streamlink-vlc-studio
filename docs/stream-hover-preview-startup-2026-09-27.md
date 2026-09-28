# Hover preview startup, 2026-09-27

The production card now reaches visible video in roughly half the time on the
two tested live broadcasts. Three alternating before/after pairs measured these
hover-to-image times, in milliseconds:

| Provider | Before, median (range) | Updated, median (range) | Reduction in median wait |
| --- | ---: | ---: | ---: |
| Twitch | 2,463 (2,362–2,563) | 1,160 (1,055–1,169) | 52.9% |
| Kick | 2,332 (2,227–2,436) | 1,052 (847–1,058) | 54.9% |

[Individual measurements](measurements/hover-preview-startup-2026-09-27/paired-results.json)
and the `pair-*.log` files in the same directory contain all twelve runs.
These are local live-network measurements, with a 100 ms observation interval,
not a guarantee for every connection or broadcast.

## Cause and change

The baseline already had a 100 ms hover dwell and immediate UI notifications.
Timings inside the existing live-card test identified approximately 650 ms of
Streamlink startup followed by provider resolution. On Twitch, resolution also
loads each quality's media playlist before the selected preview starts.

Ordinary live previews now obtain the provider's playback authorization/URL,
parse the master playlist, and fetch only the selected quality. The initial
media playlist is handed to VLC through a loopback endpoint. Subsequent playlist
reloads are checked there, while media bytes travel directly to VLC. The
same `360p,480p,best` preference, 320 × 180 output, mute, 30 fps presentation cap,
100 ms dwell, and 500 ms network-cache setting remain in use.

The request formats and Twitch ad markers were checked against the installed
Streamlink 8.4.0 plugins and their upstream source:
[Twitch](https://github.com/streamlink/streamlink/blob/8.4.0/src/streamlink/plugins/twitch.py),
[Kick](https://github.com/streamlink/streamlink/blob/8.4.0/src/streamlink/plugins/kick.py).
Kick's IVS media domain is limited to HTTPS provider endpoints, including the
documented [Amazon IVS playback domain](https://docs.aws.amazon.com/ivs/latest/LowLatencyUserGuide/create-channel-cli.html).

VLC's native HLS defaults include a 15-second live distance and a minimum buffer.
The preview derives its requested distance from the existing transport's segment
policy and the actual target duration, accounting for VLC's safety segment.
Twitch uses native low-latency mode when enabled; Kick retains the native minimum
buffer. Native HLS buffering can differ from a full Streamlink tab.
The calculation follows [VLC 3.0.23's buffering implementation](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/logic/BufferingLogic.cpp).

## Compatibility and lifetime

Custom arguments, Streamlink authentication/configuration, sideloaded plugins,
custom proxy/trust settings, and unsupported media use the original transport.
The fast path retains provider authorization; it does not reuse stale signed
URLs or open neighboring cards in advance. Failed direct lookup or first video
falls back to Streamlink with the unchanged request. Lookup is bounded to two
seconds, and first video to four seconds; successful playback removes that
startup deadline.

Every playlist reload is bounded and validated. Twitch ad markers, encrypted
media, unsupported initialization sections, and unsafe media locations trigger
fallback before that playlist reaches native playback. The direct decoder and
playlist listener finish cleanup before the replacement transport starts.
Cancellation also closes a listener created by a late resolver completion.

## Method and validation

The baseline was built from the incoming working tree, including its existing
changes, at Git revision `12e0adbb8476aa3ede6353d1be7701f47761a358`.
Its binaries were preserved before production edits. Each trial launches a fresh
test process, moves the pointer into the production card, observes its first
bitmap, verifies changing pixels, and verifies that disabling previews clears
the card and finishes cleanup. Pair order is before/after, after/before,
before/after. Builds and other tests were stopped during these measurements.

The public broadcasts were Twitch `alinitytv247` and Kick `cuffem`. The test used
the configured Streamlink 8.4.0, installed VLC 3.0.23, .NET SDK 10.0.302, and the
existing low-latency setting. Earlier attempts with `xqc` returned offline on
both providers; those failures are not startup-time samples. Test settings are
loaded in memory and are not saved.

- Release solution build with warnings treated as errors: zero warnings/errors.
- Final focused interactive run: **28 passed, zero skipped**. This includes real
  VLC fixture playback, physical hover/click/leave/hide/disable/unload, selected
  quality requests, configuration fallback, initial/later ads, deadlines, URL
  and payload checks, late cancellation, and listener/transport cleanup.
- Full headless suite: **1,306 passed, 254 desktop-only skips, zero failures**.
  This run preceded the final native live-distance adjustment; all focused
  hover checks and the twelve paired live-card trials ran again on the final build.
- Formatting verification passed for the changed C# files.
- Final native playback continued for 40 seconds on each provider: Twitch
  delivered 637 frames with a largest presentation gap of 133 ms and 14 ms cleanup;
  Kick delivered 641 frames with a largest gap of 108 ms and 15 ms cleanup.
  Both verified changing video across segment boundaries. The corresponding
  `continuity-*.log` files are in the measurement directory.
- A self-contained Windows x64 single-file build was published successfully to
  `artifacts/hover-preview-startup/StreamStudio.exe`.

Run the focused checks with:

```powershell
.\scripts\dev.ps1 Test -Filter 'stream hover preview:' -Interactive
```

To include a currently live broadcast and a longer continuity check:

```powershell
$env:SVS_TEST_HOVER_CHANNEL = 'https://www.twitch.tv/CURRENTLY_LIVE_CHANNEL'
$env:SVS_TEST_TIMEOUT_SECONDS = '75'
$env:SVS_TEST_HOVER_DURATION_SECONDS = '40'
.\scripts\dev.ps1 Test -Filter 'stream hover preview:' -Interactive
```

Local working logs and the baseline binaries are in
`.tmp/hover-preview-startup-2026-09-27/`. Saved measurement logs are under
`docs/measurements/hover-preview-startup-2026-09-27/`.
