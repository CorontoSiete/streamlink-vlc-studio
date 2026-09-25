# Pause/resume seekbar timing

The seekbar validates VLC timestamps against an estimated playback clock. Previously, pausing froze
the displayed snapshot but left the estimate anchored to the time before the pause. After more than
the five-second sample tolerance, resuming rejected VLC's correct timestamp and added the paused
time to the seekbar instead. This also affected the position used by replay chat.

The paused snapshot had two other problems: it was first captured by the next polling tick, allowing
live timestamps to drift before capture, and it was only cleared by a playing tick, allowing a rapid
second pause to reuse the previous pause's position.

## Change

- Capture the paused clock when the playback status changes to Paused, for manual pauses and hidden
  tabs. Use the same captured position for a live replay hold.
- Rebase the clock at resume before publishing Playing. The VLC sample filter and fallback estimate
  both exclude paused time, including when VLC temporarily cannot return its clock.
- Clear the paused snapshot on each status transition and synchronize access with the anchor lock.
- Carry a playback-state version through clock sampling, queued UI updates, and replay chat. Samples
  that began before pause/resume cannot replace the new anchor or update the seekbar afterward.

The existing stale-seek timestamp filter and live replay reload/seek behavior remain in use.

The broad regression run also exposed a replay-chat timeline defect: seeking beyond the currently
cached messages only saved an array index, so pages arriving later could display messages from
before the seek window. The timeline now retains the absolute seek boundary as well. Old messages
remain cached for backward seeking. Two deterministic tests reproduced the defect before this
small correction; a third verifies that changing sessions clears the boundary.
Live messages buffered before the initial replay metadata arrives remain available on attachment.

## Reproduction and verification

The seven initial regression tests all failed before the fix. A simulated 45-second pause advanced
both Twitch and Kick VOD seekbars from 10:00 to 10:45. Other cases reproduced late live capture and
stale snapshots during repeated pauses. Additional tests exercise queued and in-flight samples and
resuming before VLC has returned its first timestamp. That first timestamp can initialize the clock
instead of being rejected against an invented zero-position anchor.

A native test uses the installed libVLC with silent, seekable local media and the real playback
engine. Against the original code, after a seven-second pause, VLC reported **3.251 seconds** while
the seekbar reported **10.251 seconds**. With the fix, the seekbar tracks VLC again. A second native
test exercises the live resume path, including growing broadcast metadata and reload at the held
timestamp. Provider URLs are replaced by local media in these tests; they do not contact Twitch or
Kick.

All ten deterministic pause regressions and both native VLC tests pass. The Release solution build with
warnings treated as errors passes, and formatting/analyzer verification of the changed files passes.

The final full headless run passed **907 tests**, with **238 desktop-only skips**, no failures, and
no timeouts. This checkout already includes two tab-switch video-bounds desktop tests beyond the
workflow's older 236-skip ceiling, so the local run used the actual 238-test ceiling. No test was
disabled or given a weaker assertion to obtain this result.

Evidence is retained under `artifacts/pause-seekbar/`:

- `regressions-before.log`: seven reproduced failures.
- `native-before.log`: the original code's seven-second native clock mismatch.
- `final-native-tests.log`: all twelve focused tests on the final build.
- `final-build.log` and `format.log`: build and formatting checks.
- `chat-regressions-before.log` and `chat-regressions-after.log`: late-page timeline checks.
- `final-full-tests.log`: the complete final headless run.
- `changes.patch`: the source, test, and documentation changes.
