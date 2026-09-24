# Reliable mouse-wheel volume input

Two independent paths could lose volume input:

- The low-level mouse hook waited 25 ms for the WPF dispatcher, then aborted a
  pending wheel operation. If the operation had already started, it could finish
  after the hook allowed native delivery, applying the same input twice.
- Main-window volume hit testing queried VLC's video size and cursor. Those
  queries fail immediately when VLC's native lock is occupied, so a valid wheel
  event could be rejected even with an otherwise responsive UI.

Video HWNDs and the volume indicator now register their native input ownership.
The hook queues each accepted wheel packet in order and suppresses its native
delivery immediately. An unhandled packet is posted once to its captured native
recipient, preserving its delta, screen coordinates, and key/button state.
Ordinary WPF controls retain their existing Windows input path. Inactive main
windows retain Windows' routing policy; detached video retains its hover behavior.

Volume hit testing uses the aspect-fitted video surface. Native chat overlay hit
testing maps the event's original screen coordinates using the last known video
dimensions. Native fallback events also retain their original coordinates rather
than consulting the pointer after a dispatcher delay.

## Regression checks

The test catalog includes blocked and slow dispatcher tests, exact ordered
delivery, native fallback, input ownership, popup input, and volume hit testing
without VLC geometry. Restoring the former timeout and geometry checks makes the
corresponding new regressions fail.

Physical-input tests use Direct3D11 and GDI VLC renderer children in main,
fullscreen, and detached windows. They inject mixed positive, negative, and
multi-notch packets during a 300 ms UI stall, move the cursor before processing
resumes, and verify the complete volume sequence, including a drain for late
duplicate delivery. Enable these optional tests with `SVS_TEST_VLC_DIRECTORY`
(a VLC directory containing `libvlc.dll`) and `SVS_TEST_VLC_MEDIA` (a local video).
Use `SVS_TEST_FILTER=volume wheel` to run the UI checks and
`SVS_TEST_FILTER=Mouse wheel dispatch` for the dispatcher checks.

Verification on Windows with the pinned .NET 10.0.302 SDK:

- Solution build: zero warnings and errors; new-file whitespace checks passed.
- `SVS_TEST_FILTER=wheel` with local VLC and video: 17 passed, zero skipped.
- Broader non-interactive suite: 652 passed, 187 interactive tests skipped.
  Three environment failures were rerun successfully with the repo SDK on
  `PATH` and a writable workspace `TEMP`/`TMP` directory.

The hook follows Microsoft's recommendation to hand off work and return promptly:
[LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc).
