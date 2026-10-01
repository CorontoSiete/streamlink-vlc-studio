# Playback-rate control improvements — 2026-10-01

## Reproduced problems

The first twelve focused regression cases were run against the existing control
and engine. Four passed and eight failed. A newer selection remained blocked by
an obsolete recovery wait; reloading did not cancel that wait; an old player's
completion overwrote the new player's default selection. A repeated 2x request
took 513.6 ms. Pending native requests survived a stop, an explicit seek timed
out, and both completed and muted VODs stopped presenting advancing video on
the 2x-to-0.5x transition.

Evidence: [initial regressions](../.artifacts/playback-rate-control/controls-before.log).

The earlier elapsed-time correction for delayed changes after watching at 2x
is retained. Its original investigation is in
[the existing stuck-speed report](vod-playback-speed-stuck-2026-10-01.md).

## Control and engine behavior

A new selection cancels an obsolete pending request. The displayed selection
updates immediately, while the applied selection changes only after native
submission succeeds. A native call that already committed before cancellation
still supplies the applied value used if the next selection is rejected.
Invalid selections preserve a valid pending request.

Reloading resets both selections and invalidates completions from the old
player, including a completion blocked while reading its clock. Seeking cancels
pending selections and disables the control until the seek finishes. Playing
and paused inputs permit speed changes. Automatic live-edge recovery checks
the selection generation associated with its clock sample and respects a
newer manual selection.

The native engine serializes rate requests and checks the captured player and
seek generations before submission. Unchanged rates avoid another seek or
recovery wait. Explicit seeks invalidate pending requests. Clock recovery and
rate submission share one native critical section, with the lock released
between recovery polls. Recovery remains bounded and cancellable, and its
advance window includes elapsed media time at the current rate.

For an uncorrected VLC core, a completed FFmpeg replay recovers at 1x before
committing a high-to-slow transition. Cancellation or a seek restores the
previous native rate; replacement media inherits the previous requested rate.
Retained rates are applied after an opening replay establishes its requested
position. Video-only inputs avoid an audio recovery seek.

## VLC clock correction

The exact VLC 3.0.23 source shows that `EsOutDecodersStopBuffering` combines a
wall-time PTS delay with stream-time seek preroll and extra buffering, then
subtracts that mixed interval from the system clock. These units agree at 1x
but diverge at other rates.

The patch converts PTS delay to stream time before the buffering comparison,
then converts the full interval to wall time before rebasing the clock.
`input_clock_ChangeSystemOrigin` already subtracts `ClockGetTsOffset`; timestamp
conversion adds it back. Accounting for that existing compensation keeps the
decoder delay in wall time when the preroll is released. Both conversions are
required. The 1x calculation is unchanged.

The corrected path sets the selected rate before the same-position audio
recovery seek. The next selection waits for that seek's clock to recover.
Growing and adaptive HLS retain their audio recovery path.

The published [source patch](../native/vlc-core/replay-preroll-rate.patch)
applies cleanly to the pinned upstream archive. Two independent clean builds
produced SHA-256
`11405e61822376a94db5fec69138fb1f646cf0b38a8cce21e929ddcbed7e5745`.
The native build verifies source, compiler, contrib, reference DLL hashes,
exports, and PE imports. Runtime selection enables this capability only for
the exact verified installed VLC DLL pair. Other pairs retain their installed
core and the managed compatibility path. See the
[native build instructions](../native/vlc-core/README.md) and
[build result](../.artifacts/playback-rate-control/native-core-final-build.log).

## Windows audio measurements

A generated completed H.264/AAC HLS replay exercises the production source
gateway, FFmpeg demuxer, precise seek filter, video, and DirectSound together.
Windows loopback records output from the start of the request through at least
three seconds after completion. The baseline tone is checked before each
transition. RMS detects low output; the existing 100 ms limit is unchanged.
Other audio was paused for these measurements.

All twelve completed-replay transitions passed, including 2x to 0.5x and 2x to
0.75x. The longest measured low-output interval was 29.5 ms. Rate submissions
took 1.0–5.3 ms when no preceding recovery was pending; the two requests that
waited for a preceding 0.5x recovery took about 566 ms while audio continued.
First audio was observed within 9.6 ms in every transition.

The existing file and HTTP HLS checks each passed their ten transitions. Their
longest low-output interval was 43.0 ms. The native PCM check also passed, with
zero silent blocks after the rate change.

Evidence: [completed replay capture](../.artifacts/playback-rate-control/final-completed-audio.log)
and [existing audio checks](../.artifacts/playback-rate-control/final-legacy-audio.log).

## Playback and repository verification

All nineteen focused control tests passed with zero skips. They cover eight
control races and eleven native cases, including interruption of the managed
compatibility path. Unchanged 2x submission took 0.8 ms. Stop, seek, cancellation,
and replacement cases retain the rate actually applied.

Native speed measurements fit three seconds of media-clock samples against
elapsed wall time. Each measurement requires the same player generation,
increasing displayed pictures, and newly decoded video frames. This checks
actual progress in addition to VLC's requested-rate property. The option
sequence tests 2x followed by 0.5x before the remaining rates.

| Selected | Completed A/V VOD | Muted-segment A/V VOD | Video-only VOD |
| --- | --- | --- | --- |
| 0.5x | 0.521x | 0.522x | 0.522x |
| 0.75x | 0.754x | 0.754x | 0.755x |
| 1x | 0.983x | 0.981x | 0.985x |
| 1.25x | 1.236x | 1.235x | 1.235x |
| 1.5x | 1.492x | 1.492x | 1.494x |
| 1.75x | 1.747x | 1.716x | 1.739x |
| 2x | 1.924x | 1.957x | 1.960x |

The tolerance is 0.12x, smaller than half the gap between adjacent options.
Every one of these twenty-one measurements included newly decoded frames.
Evidence: [control and decoder checks](../.artifacts/playback-rate-control/final-controls-decoder.log).

All three delayed-change regressions passed with newly decoded frames: completed
and muted VODs plus growing replay. After more than twelve seconds of media
progress at 2x, each return to 1x succeeded in about 1 ms and decoded at the
selected rate. Evidence: [delayed changes](../.artifacts/playback-rate-control/final-delayed-decoder.log).

The final native core also passed fourteen fast-resume cases, seventeen growing
replay startup and seek cases, thirty-four pause/seekbar cases, and both rapid
live-replay speed and published-edge cases, all with zero skips. Ten pause
continuity cases also passed, including retained native inputs, correct resumed
pixels, playlist growth, and reconnecting at the held position. The two
physical speed-selection checks passed in normal and topmost windows.

Evidence: [fast resume](../.artifacts/playback-rate-control/final-fast-resume-native.log),
[growing replay](../.artifacts/playback-rate-control/final-live-first-seek-native.log),
[pause and seekbar](../.artifacts/playback-rate-control/final-pause-seekbar-native.log),
[pause continuity](../.artifacts/playback-rate-control/final-pause-continuity-native.log),
[live replay speed](../.artifacts/playback-rate-control/final-live-rate-native.log),
and [physical selection](../.artifacts/playback-rate-control/physical-speed-selection-alone.log).

`scripts/dev.ps1 Check -NoRestore` passed formatter verification, PowerShell
syntax and tooling contracts, pinned native dependency verification, and the
Release solution build with warnings as errors. The complete headless suite
passed 1,494 cases and intentionally skipped 269 desktop checks.
Evidence: [complete repository check](../.artifacts/playback-rate-control/final-full-check.log).

The tests use generated local media and the real installed VLC decoder. This
work does not claim a new measurement against a public CDN archive. The
installed StreamStudio executable was not replaced during this change.
