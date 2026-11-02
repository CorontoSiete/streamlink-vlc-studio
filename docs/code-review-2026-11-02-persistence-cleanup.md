# Code review — persistence and cleanup, 2026-11-02

## Scope

Repository-wide build, source-reference, duplicate-code, formatting and syntax checks, with manual review of core parsing, settings/history, downloads, HTTP/media handling, cancellation, process ownership, playback/replay, chat, browser state and native resource ownership. This combines broad automated checks with focused manual inspection; it does not establish that every source line is bug-free.

Existing edits were preserved. All 27 starting tracked diff sections outside the two previously modified files extended by this pass remain identical. Third-party sources and pinned playback binaries were retained.

## Fixes

- A failed volume save previously skipped subsequent players, and backgrounding could skip all VOD bookmarks after that failure. A shared save-completion helper now waits for every independent save before reporting the first error. Logout and browser lifecycle handlers use one session-state flush.
- Page exit omitted VOD progress, and dispatched bookmark requests could be canceled during navigation. Page exit now saves progress, and progress requests use the same `keepalive` behavior as the existing preference and volume saves.
- Volume flushing now samples the current media volume even if the browser has not yet delivered its queued `volumechange` event.

The three added JavaScript regressions failed before these production changes and passed afterward. They cover subsequent players after a failed save, VOD bookmarks after volume or progress failures, and page exit before a volume event is delivered. These tests verify request scheduling and options; they cannot guarantee delivery after abrupt browser or machine termination.

## Reuse and dead code

- Reused the shared save-completion helper for player state, all volumes, all bookmarks and session flushing.
- Removed the replay clock's unused seek-confirmation flag and accepted-sample timestamp, their snapshot properties and assignments, and a forwarding method in the tab view model. Neither removed snapshot property had a consumer.
- Removed test writes and calls that only manipulated the obsolete flag. Renamed the remaining clock-aging helper to describe its actual behavior.

## Validation

- Full baseline headless Windows suite: **2,112 passed; 276 interactive desktop tests skipped; zero failures or timeouts**.
- Final Release build: **zero warnings and zero errors**.
- Against the final build, all five affected test groups passed: browser server **41**, pause **64**, playback speed **11**, skip hotkeys **8** (two interactive skips), and replay **161** (64 interactive skips). These groups overlap; their counts are not unique test totals.
- Final JavaScript/account-worker suite: **69 passed**, including the three new regressions.
- Real Chrome playback/navigation suite passed, including decoded video, completion, reloads, grid audio, fullscreen and 11 viewport widths; zero JavaScript errors.
- Native overlay TLS, network, readability and rendering suites built with warnings treated as errors and passed.
- Python measurement tests: **8 passed**. All eight PowerShell/tooling regression suites passed.
- Repository formatting, PowerShell/JavaScript/Python/XML syntax checks, source-reference checks and `git diff --check` passed.

The first rebuild overlapped the baseline test process and encountered Windows file locks. The successful final rebuild ran after that process exited. No dependencies or repository audit defaults were changed.
