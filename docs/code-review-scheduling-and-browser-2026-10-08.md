# Scheduling and browser lifetime review — 2026-10-08

Reviewed the C# projects and tests, XAML/configuration, PowerShell/Python tooling, native chat rendering/networking, and build/release paths using repository-wide audits and focused manual inspection. This pass was compared against a snapshot of the starting source files, preserving existing uncommitted work.

## Fixes and cleanup

- **Debounce scheduling:** an invalid replacement delay previously advanced the callback generation before timer construction failed, silently discarding valid pending work. Commit the timer and generation only after successful construction. The new regression covers both negative and oversized delays; a standalone reproduction failed before the fix and passed afterward.
- **Twitch sign-out:** browsing-data cleanup previously had an unbounded wait. Apply the existing browser operation timeout and caller cancellation, and check cancellation before reporting completion.
- **Browser lifetime reuse:** Kick clip creation and Twitch bonus pages now share `WebView2ControllerLifetime`. It retains the native parent until noncancellable controller creation finishes, closes late controllers after timeout/cancellation, and disposes the parent on creation failure.
- **Idle task reuse:** playback cleanup, tab playback policy, and Twitch prediction request draining use `Task.CompletedTask` before any work starts. Active operations retain their asynchronously completed task sources.

Removed five redundant private helpers: two duplicate late-controller cleanup methods and three completed-task-source factories. Seven production files and two test catalog files changed in this pass.

## Verification

| Check | Result |
| --- | --- |
| `scripts/dev.ps1 Check` | Passed: locked restore, formatting, PowerShell syntax/tooling fixtures, pinned native provenance, Release build, and full headless-safe suite. Build: zero warnings/errors. Tests: **1,836 passed; 274 skipped**. |
| Isolated opt-in Twitch WebView2 fixtures | Canceled creation cleanup and session-cookie sign-out both passed, with zero skips and separate test profiles. |
| Kick native browser lifecycle fixtures | Hidden publication, failure handling, and cancellation including late creation passed in the main suite. |
| Python measurement tooling | All 8 tests passed. |
| Release publication tooling | All 11 fixture groups passed using the fake GitHub CLI. |
| Native overlay, subpicture, compositor, and hardware GDI fixtures | Passed protocol, rendering, allocation/ownership, cache, resize, and resource cleanup checks. Strict native compilation passed. |
| C# audits | Parsed 649 authored C# files. Solution semantic audit reported no workspace or compilation errors. Duplicate scanning retained only the intentional typed libVLC scheduling overloads. |
| Other source/configuration audits | No syntax errors or removable Python import, PowerShell function, or native static-function candidates. |
| `git diff --check` | Passed. |

The dependency probe's explicit imports remain necessary for standalone PowerShell compilation. Native layout members and externally invoked interface methods remain required.

The compositor and hardware fixtures initially lacked the VLC plugin directory; both passed after supplying the installed VLC 3.0.23 plugin path. This required no production code changes.

The 274 skipped tests require an interactive desktop. Authenticated live-provider operations were not exercised. Validation logs and the diff against the starting snapshot are in `.tmp/review-current-session`.
