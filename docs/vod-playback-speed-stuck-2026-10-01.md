# Playback speed becomes stuck after watching at 2x — 2026-10-01

## Observed failure

The installed app was playing xQc archive `2888300423` through the completed,
muted MPEG-TS VOD transport, using VLC 3.0.23 and its FFmpeg demuxer. Its log
recorded five rejected speed requests while video kept playing.

The first failure at 06:11:33 EDT reported `state=Playing`,
`timeMs=13825201`, and `targetMs=13792994`. Playback had advanced 32.207 seconds
beyond the previous rate-change resynchronization position. Subsequent failures
retained the same target while the current clock advanced further. The view model
logged that it could not apply the requested 1x or 1.75x rate.

Evidence: [installed failures](../.artifacts/playback-speed/installed-failures-before.log)
and [original executable hash](../.artifacts/playback-speed/installed-build-before.json).

## Cause

`TrySetPlaybackRateAsync` waits for the previous HLS audio resynchronization
before calling VLC's rate setter. The pending recovery flag was checked only
when another speed request arrived. Recovery required the native clock to be
between 250 ms and 10,000 ms ahead of the saved position, after at least 500 ms
of settling time.

That fixed upper bound rejected ordinary playback after ten seconds of media
time, approximately five seconds of watching at 2x. Once playback passed the
window, every subsequent request waited five seconds and failed, even though
the decoder was playing normally. The native rate setter was never reached,
and the view model restored the previously applied selection.

## Correction

The upper bound now includes the media time that can have elapsed since the
resynchronization, at the current requested rate, plus the existing ten-second
clock tolerance. The 500 ms settling interval, minimum 250 ms advancement,
transient-zero protection, implausible-forward-jump check, cancellation,
paused-player handling, published-edge handling, and audio resynchronization
remain in effect. The wait still releases the native lock between samples.

## Regression checks

The new native tests actually decode more than twelve seconds of media after
selecting 2x, then request 1x. They cover completed ordinary and muted VODs,
growing adaptive replay, and an opt-in check against the reported archive.
All four reproduced the five-second rejection against the unchanged engine,
with zero skips. The same requests succeeded in approximately 1 ms after the
correction.

The actual-archive check also selects every available rate, confirms continued
video at the held position on the same decoder generation, and measures native
clock advancement against wall time. Several seconds of samples are fitted to
a slope so stepped clock updates cannot make a stuck neighboring rate pass.
This check uses the real CDN media, repair gateway, VLC decoder, and native
overlay; it creates its own player and does not modify the user's watch history.

Evidence: [local failures](../.artifacts/playback-speed/regressions-before.log),
[actual archive failure](../.artifacts/playback-speed/actual-vod-before.log),
and [corrected native measurements](../.artifacts/playback-speed/regressions-after-measured.log).

The actual archive's five-second fitted clock rates were:

| Selected rate | Measured rate |
| --- | --- |
| 0.5x | 0.499x |
| 0.75x | 0.752x |
| 1x, final return | 1.000x |
| 1.25x | 1.254x |
| 1.5x | 1.506x |
| 1.75x | 1.757x |
| 2x | 1.997x |

The existing native rapid-change and published-edge checks both passed, including
an observed transient zero clock and continuing replay pixels without a frame
from the VOD beginning. The native PCM continuity check passed with zero silent
blocks after changing speed. The view-model clock-reset and queued wheel-selection
checks also passed. These five focused checks had zero skips.

Evidence: [rapid changes and published edge](../.artifacts/playback-speed/rapid-and-edge-after.log),
[PCM continuity](../.artifacts/playback-speed/audio-after.log),
[view-model transient clock](../.artifacts/playback-speed/transient-clock-after.log),
and [queued selections](../.artifacts/playback-speed/pending-selection-after.log).

`scripts/dev.ps1 Check -NoRestore` passed formatter verification, PowerShell
tooling contracts, pinned native dependency verification, and the complete
Release solution build with warnings treated as errors. The full headless-safe
suite passed 1,486 tests and intentionally skipped 269 desktop checks. The nine
focused playback checks above ran separately with zero skips.

Evidence: [complete repository check](../.artifacts/playback-speed/full-check.log).

Set `SVS_TEST_VLC_DIRECTORY` to an installed 64-bit VLC directory and run
`scripts/dev.ps1 Test -Filter 'replay playback speed remains changeable'` to
enable the local native checks. The actual-archive check additionally requires
`SVS_TEST_PLAYBACK_RATE_VOD_URI` to contain the resolved playlist and optionally
`SVS_TEST_PLAYBACK_RATE_VOD_POSITION` to specify the start position in seconds.
The CDN URL is resolved from the observed application log at execution time
and is not stored in source.

## Installed update

The corrected Release build was published as a self-contained Windows x64
single-file executable with the bundled native VLC overlay. The installed
`C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe` was replaced
atomically after the original app closed normally, then restarted from that
same location. The running process responds normally.

The installed executable's SHA-256 matches the tested published build:
`EB73F8B0342B72C3BC717B04A72B0AA4EEE08D70AA1DA1ED9EFF38E76B0EB55B`.
The original executable was retained as an independently hash-verified rollback
copy in `.artifacts/playback-speed/installed-original-39c1b67509e946e583d45c12413fe83c.exe`.

Normal shutdown saved the user's archive bookmark at `04:40:38.967` of
`10:17:17.183`. The restarted app's search resolves the same archive. Reopening
that result needs a manual click because the computer-control tool reports
`coordinate input geometry is unavailable`; screen capture also times out.
The successful native tests above are separate from this app-control limitation.

Evidence: [publication log](../.artifacts/playback-speed/publish.log),
[installation result](../.artifacts/playback-speed/installation-result.json),
and [saved bookmark](../.artifacts/playback-speed/bookmark-after-shutdown.json).
