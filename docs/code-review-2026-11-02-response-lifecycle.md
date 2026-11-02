# Code review — response lifecycle and persistence, 2026-11-02

## Scope

Repository-wide build, syntax, source-reference, duplicate-code and formatting checks, with focused manual review of parsing, settings/history, HTTP/media handling, download progress and cleanup, process ownership, playback transitions, browser persistence and native protocol boundaries. The source inventory covered 885 files before this pass; the C# syntax/reference scan covered 442 production files. This combines broad automated checks with focused manual inspection, rather than establishing that every source line is bug-free.

Existing local work was preserved. All 43 starting tracked diff sections outside the browser application and player fixture extended by this pass remain identical to the saved baseline. Third-party sources and pinned native binaries were retained.

## Fixes

- A failed volume or VOD bookmark save prevented browser sign-out. Sign-out now attempts to revoke the session even when saving fails, and releases local playback after the server confirms logout. Saving failures still reach the existing error handler.
- A failed periodic bookmark save skipped the remaining players. Periodic and browser-lifecycle saves now share one batch helper that attempts every selected bookmark before reporting a failure. The existing five-second interval for each player's progress saves is preserved.
- Twitch bonus response observation bounded acquisition of a response stream but omitted a body-read deadline and cleanup of streams delivered after cancellation or timeout. A shared response reader now owns acquisition, bounded reading and late cleanup. Follow and Kick Clip verification use the same reader, retaining their byte limits, text decoding and user-facing oversized-response errors.

Both new browser regressions failed before the production changes and passed afterward. Three new C# cases exercise response acquisition and body-read timeouts, caller cancellation, late stream disposal, exact-limit payloads and oversized-payload cleanup. These are deterministic stream tests; they do not simulate a live provider outage inside WebView2.

## Reuse and cleanup

- Removed the two duplicate late-response disposal methods and their separate response-copy loops. The shared reader reuses the existing bounded byte reader.
- Replaced the narrow browser progress wrapper with one batch helper used by periodic saves and session flushing.
- Extended the existing browser fixture to drive production polling callbacks directly.
- Reviewed low-reference candidates before deletion. The remaining candidates are required native structure fields, COM interface slots, WPF overrides and attached-property accessors, or an extension-method container; removing them would break their consumers or native layout.

## Validation

- Final Release solution build: **zero warnings and zero errors**.
- Full Windows headless suite against the updated code: **2,393 selected; 2,117 passed; 276 interactive desktop skips; zero failures or timeouts**. All three new response-reader cases passed, as did the existing Follow, Clip and Twitch bonus checks.
- Final deterministic JavaScript/account-worker suite: **76 passed**, including both new persistence regressions. Each new regression failed against the original browser behavior before the fixes.
- Real Chrome playback/navigation checks passed with decoded video, stream replacement, grid audio, fullscreen, dialog navigation and 11 viewport widths from 320 to 1920 pixels; zero JavaScript errors.
- Python measurement-tool tests: **8 passed**.
- Native overlay TLS, network, readability and rendering tests built with warnings treated as errors and passed. Rendering checks retained the same GDI handle count before and after cleanup.
- All **eight PowerShell/tooling suites** passed, including publication fixtures; no release was published.
- Repository formatting verification, C# syntax/reference inspection, 45 PowerShell and 23 JavaScript syntax checks, Python/XML syntax checks, pinned native dependency verification and `git diff --check` passed.
- Compared the final tracked changes with the saved starting patch: all 43 untouched starting diff sections remain identical.

The 276 interactive desktop tests were not exercised. The initial restricted test run could not access Windows credentials and native cache folders; a normal-access run also exposed an inherited tool-path issue in installer fixtures. Final verification used the machine's standard tool paths and passed the full selected suite. NuGet's online advisory feed was unavailable, so audit lookups were disabled only in verification processes; package versions and repository audit defaults were preserved.
