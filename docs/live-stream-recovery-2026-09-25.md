# Long-running live stream freeze

## Evidence from the running app

The affected installed app was PID 31240, version 1.7.6, using libVLC 3.0.23.
The app and its three open tabs were left running during diagnosis. A read-only
ClrMD inspection was restricted to tab state, playback objects, and each
transport's bounded diagnostic queue. No credentials were inspected.

Shroud's transport queue contained this sequence, matching the shared log:

- 2026-09-25 19:59:00 -04:00: segment 9714 download failed with
  `Response ended prematurely`.
- 19:59:01: `HTTP connection closed`.
- 19:59:02: `Stream ended`.

Shroud's Python transport (PID 35264) still listened on loopback port 55190, but
had no established local connection to the app. Hal (port 55055) and xQc (port
53920) had established local player connections. Hal's separate segment 11709
error did not terminate his connection. Shroud's in-memory tab still reported
`PlaybackStatus.Playing`, with `desiredPaused=false`, no replay, and no suspended
resource services. The user's `KeepInactiveTabsRunning` setting was enabled.

This establishes an ended shroud playback connection that the app failed to
detect or recover. The evidence does not establish why the upstream segment
response was truncated (for example, a provider or network interruption).

## Cause

The app assigned `Playing` after the native play request and did not monitor
later native EOF/error states or output progression. Streamlink's continuous
external HTTP mode intentionally keeps its listener alive after a stream ends;
it waits for another player request. A live process therefore did not establish
live playback. See the [Streamlink option contract](https://github.com/streamlink/streamlink/blob/master/src/streamlink_cli/argparser.py).

The native video host also preserved existing screen pixels when shown. Without
a functioning renderer to replace them, switching to the failed tab could leave
the previous tab's image visible. The computer-use helper could read the window's
accessibility tree, but its screenshot capture failed with
`SetIsBorderRequired failed: No such interface supported (0x80004002)`; the
reported Hal image was not independently captured during this diagnosis.

## Change

- Sample actual VLC state and decoded/displayed output once per second, including
  hidden tabs configured to keep playing. EOF/error/stopped states recover on the
  next sample; 30 seconds without advancing video output also triggers recovery.
- Count both new decoded frames and displayed pictures. Repeatedly displaying
  the last decoded frame does not count as recovery. Audio-only quality uses
  decoded audio. A visually static scene still produces new frames.
- Stop the failed transport and resolve a fresh Streamlink session, then recreate
  the VLC input. Preserve the tab, chat, quality/startup profile, volume, and mute
  preferences. Require advancing native output before reporting success.
- Retry unsuccessful attempts with exponential delays capped at 60 seconds.
  Bound transport/startup attempts and output-readiness waits. Reset retry
  history after stable playback rather than after a few frames.
- Serialize recovery with playback lifecycle operations. Stop, close, reload,
  manual pause, automatic off-grid pause, and seeking cancel an active retry.
  Manual pauses, replay playback, and intentionally suspended tabs are excluded.
- Hide the stale native surface and show reconnect/retry status in the main
  window and picture-in-picture. Repaint the video host once when revealing it.
- Include the channel in transport log lines so future stream failures can be
  attributed without inspecting memory.

## Verification

The native regression serves MPEG-TS over local HTTP, truncates a response after
real video has displayed, and keeps the server alive. With monitoring suspended,
it checks the original mismatch: native VLC `Ended`, tab `Playing`, one transport
start. Re-enabling the production timer must create a new connection and produce
repeatedly advancing decoded/displayed output. The test also hides the video
host during recovery, as the UI does.

Deterministic tests cover terminal states, stalled video with advancing audio,
static scenes, repeated old frames, player generation changes, audio-only
streams, retry delays, preserved audio settings, failed startup, replay exclusion,
manual/automatic pause, cancellation, and late transport disposal after stop/close.

The broad suite completed with 1,057 passes and 240 interactive skips. Two
packaging checks initially failed because child commands found the system .NET
SDK rather than the pinned user SDK; both passed when rerun with the corrected
PATH (1,059 passing checks in total). All nine focused recovery checks, including
the real native VLC test, passed. The final Release build had zero warnings and
zero errors.

The installed executable was replaced through the app's graceful maintenance
shutdown at 2026-09-26 03:36:49 UTC. Its product version is
`1.7.6+live-recovery`, and its SHA-256 is
`F8043B5D7DEF7AF608F1C475CB147F0210A98C66D303F299DDBAB860243C15B5`.
It was restarted from its original Program Files path. The reviewed replacement,
install script, checksum report, and previous executable are in
`artifacts/live-stream-recovery/`. Existing settings and sign-in were preserved.

After restarting the installed build, the user reopened Shroud and confirmed
that the video was playing normally.

Validation logs and the original diagnostic evidence are retained locally under
`.tmp/live-freeze-diagnosis/`. This fix addresses indefinite failure to reconnect;
it cannot prevent external provider/network outages or make an offline channel live.
