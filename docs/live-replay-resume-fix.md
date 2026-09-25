# Live replay resume and asynchronous HLS seeking

The later [startup presentation fix](live-replay-resume-startup-fix.md) addresses
the brief beginning/live-edge flash that remained before restoration completed.
The notes below describe the earlier final-position fix.

The resume path reloads the replay to hold the paused timestamp. The old engine treated
`libvlc_media_player_play` followed immediately by `libvlc_media_player_set_time` as a completed
seek. VLC opens HLS asynchronously, however, and initializes `can-seek` to true before opening
has finished. It also immediately echoes a requested seek into its time and position values.
Neither the initial seekability flag nor the echoed time proves playback reached that position.
See VLC 3.0.23's [input initialization and time callback](https://github.com/videolan/vlc/blob/3.0.23/src/input/var.c).

The app's local playback log showed repeated reloads followed by seek calls completing in
0–1 ms. A real libVLC test against an HLS event playlist from that log reproduced the position
loss: the pause held 120.001 seconds, but the decoder returned to 110.715 seconds while the
seekbar displayed 122.505 seconds. The initial seek also landed about ten seconds early.

An independent comparison using only VLC's unpause jumped from 120.250 to 41866.799 seconds,
the live edge. Therefore removing the replay reload would not fix position retention. VLC's
[adaptive pause implementation](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/PlaylistManager.cpp)
also distinguishes live playlists when pausing and restarting buffering.

The engine now waits for the player to finish opening and supply its duration before seeking.
For playing inputs, it waits for a nearby playback clock sample different from the immediate
command echo. Native calls stay off the WPF dispatcher, and each polling wait releases the
native lock. Cancellation, a changed player generation, terminal playback states, and a
15-second deadline prevent an obsolete restore from continuing indefinitely. Paused inputs
queue their seek against the initialized timeline; their clock cannot advance until resumed.
Seeking to the media's end is also handled.

If opening or restoring a replay fails, the view model stops the newly opened media and displays
the error. It no longer leaves playback running from its default position behind a stale seekbar.

## Verification

`ApplicationTestCatalog.PauseSeekbarVlc.cs` covers the real installed libVLC with local silent
media, delayed loopback HTTP responses, cancellation, a restore timeout, paused seeks and
timeline endpoints. These tests use hidden windows and zero volume. The deterministic pause
tests also cover failed restoration and existing Twitch/Kick clock behavior.

The opt-in HLS test exercises the actual decoder after seeking, repeated short and seven-second
pauses, and hiding/resuming a rewound tab. Enable it with `SVS_TEST_VLC_DIRECTORY` and
`SVS_TEST_RESUME_HLS_URI` (a currently accessible HLS event playlist with at least 126 seconds).
Use `SVS_TEST_FILTER='pause seekbar'` and `SVS_TEST_TIMEOUT_SECONDS=90`. The URI is supplied at
runtime and is not recorded in test output or checked into source. Local file tests alone did
not reproduce the original HLS behavior.

Before/after decoder measurements and validation logs are under `artifacts/live-resume-fix/`.
The original HLS failures are retained in `hls-before.log` and `hls-diagnosis.log`; the latter
also records the plain-unpause comparison. `native-before.log` is the passing file-loading
experiment, not a claimed reproduction of the HLS bug.

Final results:

- 18 focused checks passed: 17 in `final-focused-tests.log`, plus the native paused/endpoint
  check in `seek-edges.log`. Repeated HLS resumes held 122.900, 126.452, and 128.783 seconds;
  three seconds after each resume, VLC reported 126.452, 128.783, and 131.917 seconds.
- The complete headless suite passed 908 tests with 238 desktop-only skips and no failures
  or timeouts (`final-full-tests.log`). An earlier concurrent run hit the existing chat-promotion
  timing test; it passed both separately and in the clean full rerun. Its assertions were unchanged.
- The Release solution build passed with warnings treated as errors (`verified-build.log`).
  Formatting/analyzer verification passed (`format-verify.log`).
- The self-contained application was rebuilt at `artifacts/publish/StreamStudio/StreamStudio.exe`
  (`publish.log`; SHA-256 recorded in `published-app-hash.json`).
