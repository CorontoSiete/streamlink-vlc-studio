# Twitch VOD startup latency (2026-09-26)

## Diagnosis

The app log showed roughly three seconds between starting Streamlink URL
resolution and starting VLC for the recently opened Twitch archives. A timed
probe of the installed Streamlink CLI located the work: Python/CLI setup,
playback authorization, the master playlist, then **all six quality media
playlists**, fetched and parsed sequentially. On archive `2881171681`, URL
resolution alone took 2.2–3.2 seconds. Player creation took only 48–119 ms.

The probe wraps HTTP calls for timing without changing their requests or results.
Its source and output are in the ignored `artifacts/vod-startup-speed` directory.
The behavior also matches the installed Streamlink 8.4.0/8.5.0 sources and the
upstream [Twitch plugin](https://github.com/streamlink/streamlink/blob/master/src/streamlink/plugins/twitch.py).

## Change

Ordinary Twitch VODs now use an in-process resolver. It obtains a fresh playback
token, reads the authorized master playlist, selects the requested quality with
Streamlink's naming/ranking rules, and downloads and validates that media
playlist. The shared HTTP client retains connections; signed URLs and tokens
are not cached or logged. Reads and redirects use the existing bounded HTTP
and public provider URL validation. Segment pairing, durations and segment/key/
initialization URLs are checked before the result is accepted.

Unknown master formats, unavailable qualities, authorization errors, invalid
responses and network failures fall back to the configured Streamlink executable.
An unsuccessful direct attempt has a four-second total budget. Cancellation
propagates without launching a fallback. A failed Streamlink attempt still reaches
the existing subscriber-only VOD fallback in the tab.

Custom command-line arguments, active Streamlink configuration options, plugin
directories and timestamp-bearing URLs keep the original resolver. The stock
installer's `ffmpeg-ffmpeg` setting is allowed because it does not affect direct
HLS URL selection. Config locations follow Streamlink's
[Windows configuration documentation](https://streamlink.github.io/cli/config.html).
Live playback and Kick retain their current paths.

VLC buffering, output gating, exact resume confirmation, the long-VOD timeline
adapter, muted-segment repair and chat timing retain their existing behavior.

## Measurement and validation

The opt-in native tests resolve an actual Twitch VOD and open it with production
libVLC 3.0.23, muted-segment repair and the native chat overlay. They measure
resolution through an advancing playback clock and a newly displayed picture
after opening/resume confirmation. They also check full duration and continued
output. These timings exclude navigation to the VOD card and closing old tabs.

Set `SVS_TEST_STARTUP_VOD_ID`, `SVS_TEST_VLC_DIRECTORY` and
`SVS_TEST_STREAMLINK_PATH`; run the test executable with
`SVS_TEST_FILTER='VOD startup: provider resolution'`. Setting
`SVS_TEST_STARTUP_STREAMLINK_ONLY=1` selects the original resolver for comparison.
Each test makes three opens. Run the two modes sequentially without other native
playback tests to avoid measurement contention.

The optional `VOD startup: selected qualities` test compares exact media URLs
against the installed Streamlink resolver for `best`, `720p60`, `480p`, and
`worst`. Offline regressions cover current/legacy master formats, exact quality,
audio exclusion from best/worst, configuration preservation, malformed media,
authorization failures, redirect/DNS checks, response limits, cancellation,
fallback timeout, repeated fresh authorization, and fallback error propagation.

The final comparison used the user's configured Streamlink 8.4.0 executable,
archive `2881171681`, source quality and three opens per scenario. The first
trial in each process includes cold client/player initialization. Each measured
open was followed by duration, position and advancing output assertions.

| Scenario | Before (ms) | After (ms) | Mean before → after |
| --- | --- | --- | --- |
| From the beginning | 4065, 3722, 3736 | 1835, 1482, 2063 | 3.84 s → 1.79 s (53% faster) |
| Resume at 601.217 s | 5934, 6185, 5303 | 3165, 3336, 2809 | 5.81 s → 3.10 s (47% faster) |

Resolution itself took 2770–2841 ms before, 720 ms on the first optimized
request, and 267–326 ms on subsequent requests. Remaining startup time includes
network segment loading and VLC's decode/resume confirmation. These are measured
runs against an external CDN, not a latency guarantee. Logs are
`configured-before.log`, `configured-after.log`, and `measurements.json`.

Validation passed:

- 244 VOD tests, with native VLC enabled; no failures, skips or timeouts. This
  includes long VOD timelines, first correct output pixels, muted segments,
  completion, chat, watch progress, resume and seeking.
- All 14 new offline startup tests and 20 Streamlink-related tests in the final
  build; no failures or skips.
- Exact media URL comparison against the installed Streamlink 8.4.0 for all
  four tested qualities.
- Both final three-trial playback scenarios in each resolver mode.
- Release solution build with warnings treated as errors, whitespace format
  verification, and `git diff --check`.
- Self-contained Windows x64 publish.

The verified executable was installed at
`C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe`.
Its SHA-256 matches the published artifact:
`44FF8EB796C309700211BF034827C687EACF935A9B21147909BCA3E563018B4B`.
The previous executable is saved as
`artifacts/vod-startup-speed/installed-before.exe`; its hash was checked against
the original before replacement. Installation uses an atomic file replacement
and refuses to proceed if the app is running or either executable changes.
