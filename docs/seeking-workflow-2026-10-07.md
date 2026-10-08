# Seeking workflow (2026-10-07)

Replay controls previously disabled seeking while a seek was in progress. The
skip commands also rejected subsequent presses before the first seek yielded,
and direct callers could queue a full sequence of obsolete targets behind the
player transition gate.

Each tab now keeps one active request and one waiting request. A new target
replaces the waiting request. Multiple requests delivered before the dispatcher
resumes submit only their final target. An active open or seek finishes normally,
so the next request can reuse its decoder, media source, and connections. There
is no extra timer or debounce delay on an ordinary seek.

The timeline and skip commands stay enabled during this work. The slider and
elapsed text show the newest requested target; a thin progress indicator shows
that it is loading. Relative skips use that requested target until the player
catches up. A new drag retains its own preview when an older seek confirms, and
canceling that drag restores the confirmed position or newest waiting target.

Caller cancellation removes a waiting request immediately. Stop, playback
replacement, Go live, and disposal cancel the active operation before waiting
for the player transition gate, and discard waiting requests. An intentionally
canceled replay open stops its incomplete input without reporting a playback
error. A later seek can open it again. Paused seeks remain paused, and bookmarks
continue to use confirmed playback rather than preview positions.

If the input reaches EOF before the completion poll notices, an in-place seek
can no longer use it. That terminal state now permits reopening the cached source
at the newest target. Ordinary in-place failures still preserve the existing
player and position. Both a queued-EOF fixture and actual native EOF are covered.

Deterministic delayed-player regressions cover rapid input, active and waiting
requests, both skip directions, paused playback, preview overlap, reentrant
notifications, cancellation, restarts, disposal, cold live opens, and return to
live. Twelve requests before dispatcher resume require one engine seek. Twelve
requests arriving during a blocked seek require the active seek and final target
only. These are request-count checks; individual CDN and decoder latency still
depends on the media.

Run these regressions with:

```powershell
.\scripts\dev.ps1 Test -Filter 'seeking workflow:'
.\scripts\dev.ps1 Test -Filter 'skip hotkeys:' -NoBuild
```

The final Release build completed with zero warnings or errors. All 17 workflow
regressions passed with native VLC enabled, including seeking backward after EOF
before the completion poll notices. All 17 existing native first-seek checks also
passed, including prepared and cold inputs, growing broadcasts, pause, audio
activation, cancellation, and moving the video surface. Their logs are
`artifacts/seeking-workflow/focused-final.log` and
`artifacts/seeking-workflow/live-first-seek-final.log`.

The full headless run passed 1,691 tests with 275 environment-dependent skips
and no failures. Its log is `artifacts/seeking-workflow/headless-final.log`.
Scoped `dotnet format --verify-no-changes` and `git diff --check` also passed.

Full native-suite validation remains incomplete. Its interrupted run covered
ordinary and rebased HLS seeking, offline playback, precise output, playback-rate
synchronization, and VOD completion before the test process crashed. Windows
Application event 1000 identifies the installed VLC 3.0.23
`plugins/access/librtp_plugin.dll` with exception `0x40000015`. Evidence is retained
in `artifacts/seeking-workflow/native-runtime-fault.txt`; the interrupted test log
is `artifacts/seeking-workflow/full-tests.log`. This run also found two transient
clock-probe failures. The helper now waits for a readable native sample while
retaining its position, duration, and advancing-output assertions; the focused
first-seek rerun above passes with that correction.

Native runs use a 90-second test-runner timeout, because the
playback-rate fixture measures eight rates for three seconds each, with startup,
video confirmation, and pause/resume between measurements. The application's
15-second seek timeout is unchanged. An initial run that hit the runner's
default 30-second limit is retained in `full-tests-30s-timeout.log` beside the
interrupted log.

```powershell
$env:SVS_TEST_VLC_DIRECTORY = 'C:\Program Files\VideoLAN\VLC'
$env:SVS_TEST_TIMEOUT_SECONDS = '90'
.\scripts\dev.ps1 Test -NoBuild
```

For the focused native first-seek checks, use
`.\scripts\dev.ps1 Test -Filter 'live first seek:' -NoBuild` with the same
environment variables.

## Follow-up: avoiding redundant seeks

A request could still become obsolete while waiting for a pause/resume action,
replay promotion, or URL resolution. It now checks for a newer request before
submitting work to VLC. URL resolution completed for a superseded request moves
to the newest waiting request, including subsequent replacements. Reuse requires
the same target, replay ID, quality, Streamlink path, and custom arguments. Chat
is anchored when the target is ready for the player, avoiding obsolete chat
windows while URLs load. A native open already started continues normally.

A successfully confirmed seek also satisfies an identical timestamp that was
already waiting when playback confirmed. A fresh request after confirmation
still runs, including while saving a bookmark. This saves a second native seek
without accepting a failed operation as success. Explicit reloads and distinct
exact-position/return-to-live intents still run.
The request worker already yields to WPF, so its seeks no longer yield a second
time. Direct pause/resume transitions retain their yield.

Canceling a preview commit before submission now restores the confirmed position
or newest requested target immediately, including when playback is paused.
Restoration also preserves the original seconds value. A native pause check
exposed an unnecessary double-to-TimeSpan-to-double conversion changing 2.151
seconds to 2.1509999, so the slider could differ from the confirmed clock by one
tick. A deterministic millisecond-position regression now covers that case.

Before these corrections, five of the first 24 workflow regressions failed.
The blocked-playback, notification replacement, and repeated-target cases each
submitted two native seeks where one was sufficient. A canceled preview showed
240 seconds while playback remained at 120 seconds. The initial evidence is
`artifacts/seeking-followup/baseline.log`.

Additional regressions check slow URL resolution without a second resolution,
quality changes, failed duplicate retries, explicit reloads, distinct live-edge
intents, fresh requests after confirmation, and real VLC cold opens with
duplicate requests in both playing and paused states. Follow-up validation logs
are in `artifacts/seeking-followup`.

The broad native pause run also exposed a fragile instantaneous clock probe.
Native clock reads intentionally return unavailable rather than blocking behind
audio or output work. The live-resume test now waits up to one second for an
available sample, then applies its unchanged five-second position tolerance;
its seven-second resume budget and displayed-video assertions remain in place.
The diagnostic rerun resumed from 42.827 to 42.920 seconds in 1,203 ms. Initial
pause failures and the diagnostic are retained in `pause.log`, `pause-final.log`,
and `live-resume-diagnostic.log` in the same artifact directory.

Final Release compilation completed with zero warnings or errors. All 32
workflow regressions passed, including the clock precision case and three real
VLC checks. The final native pause/seekbar run passed all 34 tests, and both
live-DVR resume checks passed. The existing native first-seek and VOD latency
runs passed 17 and three tests respectively. Scoped formatting verification and
`git diff --check` passed. See `workflow-final.log`, `pause-seekbar-final.log`,
`live-resume-final.log`, `live-first-seek.log`, `vod-seek-latency.log`, and
`format-latest.log` for the corresponding results.

The final headless suite passed 1,704 tests with 275 environment-dependent skips,
zero failures, and zero timeouts. Its complete output is `headless-final.log`.
