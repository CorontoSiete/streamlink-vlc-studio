# Resume a rewound live stream without black buffering

The former resume path deliberately recreated the player and suppressed its output
until a precise seek completed. This prevented the previous beginning/live-edge
flash but produced the reported black interval. A native regression reproduced a
1,415 ms resume and a player-generation change from 3 to 5 on a local HLS EVENT
playlist. A normal native unpause alone was already known to lose the position.

VLC 3.0.23's adaptive `DEMUX_SET_PAUSE_STATE` handler clears the demux clock, and
`AbstractStream::setLivePause(false)` resets the segment tracker. The app now bundles
a small source-built demux filter that intercepts this particular control for
adaptive replay inputs. VLC's input/output machinery still pauses the decoder and
rebases the clock, while the adaptive demuxer retains its bounded buffer and
continues normal playlist updates. It does not turn the live playlist into a finite
snapshot, conceal a reload with a screenshot, or reduce buffering settings.

Only a filter that has actually attached acknowledges the per-player readiness
event. The engine checks that acknowledgement and unpauses under the same native
lock. Behind-live resumes then keep the same player, decoder, media source, and chat
timeline. Teardown clears the acknowledgement. If loading the plugin fails, the
input is replaced, or the demuxer is unsupported, the previous exact restoration
path remains available. Pausing at the live edge still uses the existing transition
to replay when necessary.

The plugin is embedded and extracted into the app's own directory, not the installed
VLC plugin tree. Its source, LGPL license and build instructions are in
`native/replay-pause/`. Two independent builds with the documented compiler produce
the same SHA-256: `90398339836963FEDF999C50966E9466EF2F6528F8EEADEF94DA43F22D65BC72`.

## Verification

The new tests verify:

- Twitch/Kick and hidden-tab resumes issue no new play or seek after preservation
  is confirmed; the clock excludes the paused interval.
- Loss of the acknowledgement retains exact reload/restore behavior.
- Actual libVLC keeps the same input across repeated short, seven-second, and
  hidden-tab pauses. The first fixed run measured 1–3 ms resume calls.
- VLC's video display callback presents only the expected green content after a
  seven-second pause: no black, red beginning, or blue live-edge frames. Fresh
  output submissions are timed using VLC's display callback tokens, independently
  of its cached HLS clock. This presentation refresh measured 8 ms; decoder
  advancement and seekbar synchronization are asserted separately in the tab test.
- An EVENT playlist can grow while paused, and resumed playback crosses the
  original endpoint without reopening the player.

The fixture server tolerates VLC canceling a download when seeking replaces an
input; a canceled socket no longer terminates that test server. The growth fixture
publishes two new segments so the assertion crosses the original endpoint while
leaving VLC's normal live buffer available. Request/clock traces established that
one new segment alone remained in that buffer. The check reached 42.151 seconds
after the playlist grew from 40 to 60 seconds, with no player replacement.

The initial timing assertion used `get_time`, making timing depend on publication
of the cached HLS clock; one run read 1,185 ms. The final check times a fresh output
submission through VLC's lock/display callbacks. It does not infer motion latency
from a timestamp or a repeated solid-color image. The one-second presentation
limit and strict no-black/no-wrong-content assertions remain intact.

Final validation includes 45 focused pause/playback checks plus a separate native
chat-overlay case, and 914 passing headless tests with 238 desktop-only skips.
One initial full run hit the existing replay-chat timing failure also recorded at
the same assertion in `artifacts/pause-seekbar/baseline-full-tests.log`. It passed
in isolation and in the clean full rerun; its code and assertions were unchanged.
The Release solution build passed with warnings treated as errors, and formatting/
analyzer verification passed. The self-contained app was rebuilt at
`artifacts/publish/StreamStudio/StreamStudio.exe`; its SHA-256 is recorded in
`artifacts/resume-black-screen/published-app-hash.json`.

Before/after evidence and final verification logs are retained under
`artifacts/resume-black-screen/`. These are native local HTTP/video tests, not a
claim that every external provider/CDN condition has been reproduced.
