# Continuous playback speed changes — 2026-10-20

Speed changes previously submitted a seek to the current timestamp to refresh
queued audio. That seek also flushed the video decoder and interrupted picture
presentation. With the verified bundled VLC core, video playback now changes
rate on the existing input without seeking, restarting, or clearing its output.

The native clock anchors a new rate to the end of queued audio. Earlier frames
retain their previous timestamp mapping until that boundary; timestamp conversion,
demux pacing, and PCR lateness checks use the same mapping. This prevents a large
slowdown from dropping old pictures or incorrectly reentering buffering. Pause
and resume shift the boundaries with the clock, while seeking and stream
discontinuities clear them.

Decoded audio is submitted up to 250 ms ahead of presentation, rather than two
seconds, so the new rate takes effect promptly. Encoded network caching remains
unchanged. A new player inherits a retained rate before playback begins, avoiding
an initial buffer at 1x. Audio-only adaptive HLS retains its audio resynchronization
path. Unverified VLC DLL pairs retain the existing compatibility behavior.

The source patch is [playback-rate-continuity.patch](../native/vlc-core/playback-rate-continuity.patch).
Two independent clean builds produced SHA-256
`d1dbe4b571422df97c73e25ce357cc28c82d703b475f8dc5b8c6c7203da4549c`.
The build verifies pinned source, compiler, contrib libraries, exports, imports,
and reproducibility. Runtime selection still requires the exact verified official
VLC 3.0.23 DLL pair. The installed VLC files are unchanged.

## Verification

- The Release solution build passed with zero warnings and errors.
- The final headless suite passed all 1,933 tests; 274 desktop-dependent cases
  were skipped. The final app build also passed with warnings treated as errors.
- Both completed and growing replay continuity cases passed every speed, including
  2x to 0.5x and rapid selections 75 ms apart. Newly presented frames retained the
  same player and contained no black frames. The longest gap was 94.6 ms across
  ordinary transitions and rapid selection bursts; the limit is 250 ms.
- All 23 speed control cases passed, covering actual decoder rates, seekbar
  synchronization, paused changes, cancellation, stop, seek, replacement, and the
  compatibility recovery path. Rate measurements retain the existing 0.12x tolerance.
- All three delayed speed changes, both live replay speed cases, and all 14 fast
  resume cases passed.
- All ten pause continuity cases passed, including a seven-second pause with
  newly presented held content after resume.
- Windows loopback audio passed all 12 completed VOD transitions with no silent
  packets or low-output runs. File and HTTP audio-only HLS passed all 20 transitions,
  with a maximum low-output interval of 41.2 ms against the existing 100 ms limit.
  The native PCM case also passed without silent blocks.
- Source patch application, pinned native dependency verification, Python syntax,
  PowerShell tooling contracts, and whitespace checks passed.

Evidence: [build](../.artifacts/playback-rate-continuity/final-build.log),
[final app build](../.artifacts/playback-rate-continuity/final-app-build.log),
[headless suite](../.artifacts/playback-rate-continuity/final-headless.log),
[speed controls](../.artifacts/playback-rate-continuity/final-controls.log),
[replay continuity and recovery](../.artifacts/playback-rate-continuity/final-replay.log),
[audio-only HLS](../.artifacts/playback-rate-continuity/final-legacy-audio.log),
[completed VOD audio](../.artifacts/playback-rate-continuity/final-completed-audio.log),
and [pause continuity](../.artifacts/playback-rate-continuity/final-pause.log).

These checks use generated local media, the production replay gateways, and the
real installed VLC decoder. They do not measure a public CDN stream. Pixel
continuity uses software video callbacks; the existing speed and audio cases
also exercise the production native output path.
