# Playback speed and replay clock consistency — 2026-10-01

## Reproduced failures

The working tree already contained native playback-rate recovery and selection
cancellation changes. All nineteen existing control and decoder tests passed
before this follow-up: [baseline results](../.artifacts/playback-rate-followup/baseline-controls.log).

Three additional regressions failed against that implementation:

- After the engine accepted 1.5x, a replay-clock read exception restored the
  displayed and remembered speed to 1x. A subsequent rejected request therefore
  restored a speed different from the one the engine was running.
- A clock sample started before a speed change could still become an accepted
  replay sample after the new speed and anchor had been committed.
- An older clock update already queued on the UI dispatcher could overwrite the
  seekbar after the speed changed. The controlled case moved the seekbar from
  600 to the obsolete 601-second sample.

The two asynchronous cases use explicit synchronization barriers and a held UI
dispatch queue. Their ordering does not depend on a fortunate timer tick.
Evidence: [all three failures](../.artifacts/playback-rate-followup/clock-all-before.log).

## Changes

`StreamTabViewModel` treats clock sampling as optional after a successful native
rate submission. A failed sample is logged separately and does not turn the
successful rate change into a rejected selection.

`ReplayClockState.CommitPlaybackRateChange` publishes the applied rate and its
anchor under the existing clock lock. If sampling failed, it estimates the
current position using the previous rate before publishing the new rate. This
keeps elapsed time before the change from being rescaled at the new speed.

The commit also advances the playback-state generation already checked by
replay sampling, seekbar dispatch, chat pumping, completion, and bookmark
capture. Older samples are discarded; a fresh sample continues normal updates.
The existing player/reset guards still prevent a completion from an old player
from committing after a reload.

The three new cases and eight existing control races passed after the fix:
[control results](../.artifacts/playback-rate-followup/controls-isolated-after.log).

## Verification

All 23 focused control tests passed with zero skips. The new application-level
test selects every rate through `StreamTabViewModel`, uses the production
completed-HLS FFmpeg transport, and keeps regular seekbar polling running.
Each measurement requires newly decoded video frames and the same player
generation. The seekbar must follow the native clock within 1.5 seconds.

| Selected | Measured decoder rate |
| --- | --- |
| 0.5x | 0.520x |
| 0.75x | 0.754x |
| 1x | 0.981x |
| 1.25x | 1.234x |
| 1.5x | 1.489x |
| 1.75x | 1.742x |
| 2x | 1.930x |

A paused 0.75x selection preserved the confirmed paused position exactly;
resumed playback measured 0.753x. These are three-second native-clock fits,
using the existing 0.12x tolerance. The sequence includes 2x followed by 0.5x.
Evidence: [complete speed-control results](../.artifacts/playback-rate-followup/final-native-controls.log).

All three delayed 2x regressions passed with zero skips: completed ordinary and
muted VODs plus growing replay. Each return to 1x completed in 1 ms after more
than twelve seconds of media progress, and subsequent measurements decoded
new frames at approximately 0.99x. Evidence:
[delayed-rate results](../.artifacts/playback-rate-followup/final-delayed-rates.log).

The full headless suite passed all 1,497 tests. Its 269 skipped checks require an
interactive desktop. Evidence:
[full-suite results](../.artifacts/playback-rate-followup/final-headless-safe-temp.log).

Formatting verification, PowerShell syntax checks, tooling contract tests, and
pinned native dependency verification also passed.

The normal Release output was locked by a running Stream Studio process.
Verification uses the SDK's `--artifacts-path` option, with separate output and
intermediate directories at `.artifacts/playback-rate-followup/build`.
The full suite uses the repository's `.tmp` directory for both `TEMP` and `TMP`,
matching `scripts/dev.ps1`.
The final solution build treats warnings as errors:
[build result](../.artifacts/playback-rate-followup/final-build.log).
