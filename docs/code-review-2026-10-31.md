# Code review — 2026-10-31

## Scope

Repository-wide build, formatting, unused-code checks and regression testing, with manual review of shared services, parsing, persistence, playback, process ownership, browser sessions and resource cleanup. Reviewed application code, native integration and build/test tooling; generated outputs and bundled third-party code were preserved. Existing uncommitted browser design changes and their tests were preserved.

## Fixes

- Inactive VODs now follow their own pause preference independently of the live-stream preference.
- Streams with **Always keep audio on** remain playing when another tab is selected. Manual mute and manual pause retain their established behavior.
- Reload takes its operation guard before awaiting volume/history saves, preventing overlapping replacements. A failed preliminary save releases the guard and preserves usable playback.
- Closing during reload also releases a server player returned after the original player was closed.
- Queued requests and late responses belong to the browser session that created them. Signing out discards queued client work and ignores late replies; a new session has its own request queue.
- A VOD completion sample within the existing five-second tolerance is clamped to the duration before saving, so the history service accepts the completion bookmark.
- Malformed time, ID, string, boolean, sequence/version and prediction-outcome values are reported as invalid input instead of provider failures.

## Reuse and cleanup

- Added `LocalWebRequestValues` for strict input parsing shared by browser commands and account preferences; removed the superseded local readers.
- Reused the existing public image URL validator instead of keeping another implementation in channel profiles.
- Consolidated player disposal for close and disconnect, including observers, pointer gestures, controls timers, pending panel layout and media.
- Extended the existing deterministic browser fixture for delayed requests and added nine playback/session regression tests. Extended the real workspace test for completion overshoot and malformed inputs.
- Unused-member candidates were checked against framework callbacks, XAML attached properties and test hooks before deletion. Those live entry points were retained.

## Validation

- Final Release solution build: **zero warnings and zero errors**.
- Full headless-safe suite: **2,383 selected**, **2,105 passed initially**, two packaging failures, **276 interactive desktop tests skipped**, zero timeouts and zero unrun tests. Both packaging cases passed separately with the machine's standard tool paths, giving **2,107 passing cases**. The failures came from this session's oversized PATH overflowing the native toolchain's batch initialization.
- After the final shared-reader cleanup and browser changes, rebuilt the solution and reran the entire local web group: **40 passed, zero skipped**.
- Deterministic JavaScript and account-worker checks: **57 passed**. The nine new playback/session tests also passed independently.
- Python measurement-tool checks: **8 passed**.
- Real Chrome: navigation/playback, sign-in, preferences, multistream, shortcuts and mobile controls passed. Coverage includes decoded video, fullscreen, account switching, responsive layouts and zero JavaScript errors.
- Repository formatting, PowerShell syntax/tooling tests and bundled native dependency verification passed; changed C# files received a final formatting check. `git diff --check` passed.

The restricted initial run could not use Windows credential storage, certificates and browser rendering. These checks were rerun with normal Windows access. NuGet's online advisory feed remained unavailable, so `NuGetAudit=false` was set only in the verification process; repository audit defaults and dependency versions were preserved. Interactive desktop tests remain outside this run's coverage.

Reproduce the deterministic browser and Python checks from the repository root:

```powershell
node --test scripts/tests/*.tests.cjs scripts/tests/*.tests.mjs hosting/cloudflare/worker.test.mjs
python -m unittest discover -s scripts/tests -p test_*.py
.\scripts\dev.ps1 Test
```
