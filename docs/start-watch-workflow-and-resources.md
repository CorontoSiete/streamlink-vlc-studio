# Starting and watching streams: cancellation and resource ownership

## Confirmed problems

Regression tests reproduced these behaviors before the changes:

- Stop waited behind startup's lifecycle lock. A pending transport lookup or a
  player waiting for its first frame kept Stop waiting too.
- A queued or dispatched start could still allocate a transport and player after
  Stop. A quick Stop/Play sequence could let the old request interfere with the
  replacement.
- Twitch replay discovery kept startup, its concurrency slot, and the playback
  transition lock occupied after live playback had begun. Pending discovery held
  up chat connection and automatic suspension when leaving the tab.
- A suspended live tab kept posting playback-health callbacks to the dispatcher:
  two callbacks during the controlled 2.2-second observation.

## Behavior after the change

Each start has its own cancellation registration from the time it is queued.
Stop and close cancel that registration before waiting for lifecycle work. A
replacement Play owns a separate registration, and completion of the old request
cannot clear it. Already-cancelled requests allocate no player or transport.

Startup checks cancellation at resource handoffs, including while waiting for a
transport and before publishing successful playback. If a transport provider
ignores cancellation and finishes late, tracked cleanup owns its eventual result
and disposes it once. The provider's cancellation source remains valid until it
finishes; Stop does not wait for that late result to release the current player.

Live playback and chat can proceed while optional Twitch or Kick replay discovery
is pending. Replay discovery also runs independently after automatic resume.
Suspension and Stop cancel it, and both completed results and queued UI callbacks
check their request generation before changing replay controls. Starting a fresh
availability check retains an already resolved replay URL for first-seek prewarm.

Playback-health timers stop while a tab is paused, suspended, or playing replay.
They restart when eligible live playback resumes. Timer callbacks check whether
monitoring is still applicable before posting to the dispatcher.

## Verification and measured limits

`StartWatchWorkflowTestCatalog` adds 15 deterministic regression tests covering
pending transport and first-frame cancellation, startup-slot queues, delayed
dispatch, rapid Stop/Play, cancelled callers, late resource cleanup, stale replay
callbacks, chat availability, suspension/resume, and playback-health monitoring.
All 15 passed. The suspended-tab check measured **zero dispatcher callbacks in
2.2 seconds**, then verified that live health sampling resumed after selection.

The complete `scripts/dev.ps1 Check` passed: formatting, PowerShell/tooling
contracts, pinned native inputs, and a Release build with zero warnings or
errors. Its headless test run passed **1,245 tests**, with **252 desktop tests
skipped** under that configuration. The native test below ran separately on the
interactive desktop with no skips.

An additional opt-in test uses the production WPF window, Streamlink service,
and libVLC engine against a local, advancing HLS broadcast. It uses in-memory
settings and a deliberately pending replay resolver. It verifies advancing native
frame counters and a 64-by-64 video size, player release on Home, resumed output
with the same Streamlink process, mute retention, process exit on Stop, and fresh
output after Play in the same tab. Closing the test also waits for its restarted
Streamlink process to exit.

Two existing native VLC tests also passed with zero skips: recovery after a
truncated HTTP response while the server remains available, and automatic replay
preparation followed by a seek that adopts the prepared player and produces
frames at the requested position. These exercise the health-monitor and replay
paths affected by this change.

One successful local run on 2026-09-26 recorded:

| Operation | Observed elapsed time |
| --- | ---: |
| Open through confirmed advancing native frames | 2,329 ms |
| Home through native player release | 31 ms |
| Return through confirmed advancing frames, same transport | 4,489 ms |
| Stop through native player release and Streamlink exit | 58 ms |

These are lifecycle checks using a tiny local fixture. They are not CPU or memory
benchmarks, public Twitch/Kick startup measurements, or before/after latency
comparisons. No percentage resource or latency improvement is inferred. The
native check verifies frame counters and process lifetimes; a separate attempt
at physical desktop input was unavailable because the desktop automation helper
could not provide window geometry.

## Reproduction

Run the focused regression suite and normal repository checks from the repository
root:

```powershell
.\scripts\dev.ps1 Test -Filter 'start watch'
.\scripts\dev.ps1 Check
```

For the opt-in native check, install Streamlink and VLC and generate the short HLS
fixture below. This example uses FFmpeg bundled with the installed Streamlink.
The test serves a rolling four-segment playlist and continuously advances its
media sequence; it repeats the fixture at an HLS discontinuity. The generated
playlist file is not used as a static live broadcast.

```powershell
New-Item -ItemType Directory -Path .tmp/start-watch-live-hls -Force | Out-Null
& 'C:\Program Files\Streamlink\ffmpeg\ffmpeg.exe' `
  -nostdin -hide_banner -loglevel error -y `
  -i tests/StreamlinkVlcStudio.Tests/Fixtures/replay-position-colors.mp4 `
  -f lavfi -i 'anullsrc=r=48000:cl=stereo' -t 60 -map '0:v' -map '1:a' `
  -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 4 -sc_threshold 0 `
  -c:a aac -b:a 32k -hls_time 2 -hls_list_size 0 -hls_playlist_type event `
  -hls_segment_filename '.tmp/start-watch-live-hls/segment%06d.ts' `
  .tmp/start-watch-live-hls/index.m3u8

$env:SVS_TEST_START_WATCH_NATIVE = '1'
$env:SVS_TEST_START_WATCH_HLS_DIRECTORY = Join-Path (Get-Location).Path '.tmp\start-watch-live-hls'
$env:SVS_TEST_TIMEOUT_SECONDS = '120'
try {
  .\scripts\dev.ps1 Test -Filter 'start watch native:' -Interactive
} finally {
  Remove-Item Env:SVS_TEST_START_WATCH_NATIVE
  Remove-Item Env:SVS_TEST_START_WATCH_HLS_DIRECTORY
  Remove-Item Env:SVS_TEST_TIMEOUT_SECONDS
}
```

The native test requires an interactive Windows desktop and permits no skips.
`SVS_TEST_VLC_DIRECTORY` can select a different VLC installation. The normal test
catalog does not include the opt-in native test unless explicitly enabled.

The existing native recovery and replay-preparation checks can be run separately:

```powershell
$env:SVS_TEST_VLC_DIRECTORY = 'C:\Program Files\VideoLAN\VLC'
try {
  .\scripts\dev.ps1 Test -Filter 'live recovery native VLC' -Interactive
  .\scripts\dev.ps1 Test -NoBuild -Filter 'live first seek: tab startup automatically prepares and adopts replay' -Interactive
} finally {
  Remove-Item Env:SVS_TEST_VLC_DIRECTORY
}
```
