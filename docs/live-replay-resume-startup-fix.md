# Prevent the brief VOD restart on resume

The later [pause continuity fix](live-replay-pause-continuity.md) removes the black
reload interval for supported adaptive replay inputs while retaining this startup
protection for actual media opens and fallback restoration.

The reported symptom was a brief return to the beginning, followed by a return to
the paused position. The previous fix checked the final restored clock, so its
tests passed despite that visible interruption.

The resume path first unpaused the old live HLS input, then opened a new replay at
its default position, then sought after initialization. Sampling the installed
VLC 3.0.23 with the broadcast URL from the local playback log reproduced both
transients: a pause at 12002.785 seconds visited 42802.465 seconds and zero before
returning to the held position. See `artifacts/resume-restart/overlay-before.log`.

## Change

The view model decides whether a replay restore is necessary while the old input
is still paused. A restore uses `PlayFromAsync` and publishes Playing only after
restoration succeeds. It does not unpause the old live input first.

The engine attaches `start-time` to the new media before opening it, then restores
the position against the initialized timeline. Output remains black and silent
until an advancing native clock confirms playback at or after the target. This
also prevents earlier frames from the target HLS segment from flashing before the
precise seek finishes. The usual seekbar tolerance alone is insufficient here.

The temporary video hold uses VLC's per-player adjustment filter, configured before
the first video output is created; it is removed after confirmation. Volume and
mute writes remain suppressed during preparation without changing the user's
requested audio state. The latest volume/mute request is applied when ready.
These settings do not affect other players or later media. There is still a brief
buffering interval, but it no longer plays the beginning or earlier preroll content.

Native startup pause is deliberately avoided: the real live HLS input stops its
downloader while paused and resets its segment selection on unpause, overriding
the seek. The reproduction and native-clock trace are retained in
`hls-startup-clock.log` and `paused-readiness.log`.
Cancellation, player generation checks, timeout, and the view model's stop-on-error
behavior remain in effect.

This follows VLC's [input initialization and control loop](https://github.com/videolan/vlc/blob/3.0.23/src/input/input.c)
and [adaptive unpause behavior](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/Streams.cpp).
The supported [video adjustment API](https://github.com/videolan/vlc/blob/3.0.23/lib/video.c)
sets the hold before newly created video outputs inherit the player's settings.

## Verification

Setting `start-time` alone was insufficient: MP4 presented its beginning and HLS
presented earlier frames from the selected segment (`frame-after.log` and
`precision-frames.log`). The final native regression uses a small generated video:
red before 34 seconds, green from 34 to 40 seconds, then blue. The restore target
is 35.25 seconds, inside an HLS segment that also contains red frames. It observes
the video **display** callback, requires black frames during preparation and green
content afterward, and rejects any red or blue flash rather than accepting a timestamp
or a decoded buffer as proof of presentation. Both MP4 and a loopback-served HLS
event playlist are covered. The fixtures and regeneration commands are in
`tests/StreamlinkVlcStudio.Tests/Fixtures/`.

Additional tests cover manual and hidden-tab resume without unpausing the old
input, repeated short and seven-second pauses, real broadcast HLS with docked and
native overlay chat, delayed HTTP loading, cancellation, timeout, playback after
cancellation, failed restoration, audio changes during loading, and existing
Twitch/Kick seekbar behavior.

Run the native checks with `SVS_TEST_VLC_DIRECTORY` and
`SVS_TEST_FILTER='pause seekbar'`. The optional external HLS tests additionally use
`SVS_TEST_RESUME_HLS_URI` and `SVS_TEST_TIMEOUT_SECONDS=120`. The overlay variant
checks a position at 12000 seconds; provide a broadcast longer than that for this
optional check. The local presentation regressions do not need an external URL.

Verification logs and the rebuilt application's hash are retained under
`artifacts/resume-restart/`.

Final validation:

- All 27 focused checks passed, including real broadcast playback, content-frame
  capture, audio during preparation, and cancellation/timeout handling
  (`final-focused-tests.log`).
- The full headless suite passed 910 tests with 238 desktop-only skips and no
  failures or timeouts (`verified-full-tests.log`). An old test's blanket ban on
  media options was replaced with direct checks of overlay runtime/pipe isolation;
  packaging subprocesses were run with the pinned SDK on PATH.
- The Release solution build passed with warnings treated as errors
  (`verified-build.log`).
