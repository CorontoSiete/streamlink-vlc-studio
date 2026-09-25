# Chat catalog workflow and resource usage

Badge and emote changes now refresh only the chat rows and replay overlays that can
use those decorations. Opening or updating one channel no longer rebuilds chat in
unrelated channels. Global catalogs still refresh their whole platform. Twitch
channel badges follow the message's room ID, including after a channel rename.
An overlay whose broadcaster ID is not yet known remains eligible for Twitch room
updates so it cannot miss a newly available badge.

Notifications combine all affected scopes in a pending burst. Concurrent changes,
changes during delivery, and eviction of an older channel's cached decorations
remain observable. The pending scope set is bounded to 256 entries; larger bursts
request a complete refresh. Worker callbacks use a published message snapshot
instead of reading WPF dependency properties from the wrong thread. Reused rows
switch that snapshot when their message changes.

The video, audio, quality-selection, refresh-frequency, image-decoding, and
animation-timing paths are unchanged.

## Measured result

The controlled fixture learns one animated emote while displaying one affected row
and 100 unrelated rows:

| Measurement | Before | After |
| --- | ---: | ---: |
| Posted UI callbacks | 101 | 1 |
| Unrelated rows rebuilt | 100 | 0 |
| Unrelated inline trees retained | 0 | 100 |

The affected row still resolves the emote's original image and selects the expected
blue animation frame at 150 ms, with 150 ms remaining until the next frame. The
fixture uses cached images and controlled catalog state, with no provider account
or network dependency. The original application failed the row-retention assertion.

This measures avoided UI callbacks and rendering work. It does not estimate total
application CPU or resident-memory savings.

## Validation

The 12 regression cases cover the measured fixture, concurrent scope merging,
global updates, overflow, reentrant notifications, failing subscribers, room
identity, reused rows, overlay invalidation, and both message/catalog eviction.

Verification on September 25, 2026:

- Release solution build with warnings treated as errors: zero warnings/errors.
- All 12 focused regression cases passed, without skips.
- Complete headless suite: 1,051 passed, 240 expected desktop-only skips, zero
  failures or timeouts. The existing CI skip ceiling of 240 was enforced.
- Formatting and warning-level analyzer verification for the changed C# files,
  plus `git diff --check`: passed.
- Locked win-x64 restore with transitive NuGet auditing and self-contained,
  single-file publish with warnings treated as errors: passed. Pinned native
  overlay inputs were verified during the build.

Interactive desktop and live-provider playback checks were not exercised.
The runnable build is `artifacts/chat-catalog-workflow/StreamStudio.exe`.

Run with the SDK pinned by `global.json` after a Release build:

```powershell
$env:SVS_TEST_FILTER = 'chat catalog resources'
dotnet tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll
```

Logs, including the failing baseline, are in `artifacts/logs/chat-catalog-workflow/`.
Pre-edit copies of the modified source files are in
`.tmp/chat-catalog-workflow-before/`; existing workspace changes were preserved.
