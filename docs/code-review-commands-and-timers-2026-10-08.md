# Code review — 2026-10-08

Reviewed authored application, setup, core/infrastructure, native, script, workflow, and test code with repository-wide syntax/reference/duplication scans and targeted manual inspection of parsing, persistence, network operations, playback/chat lifetimes, downloads, and update/install tooling. Generated outputs and binaries were excluded. Existing uncommitted work was preserved; this pass changes 20 source/project/test files and adds this note.

Changes:

- Replaced the two application/setup `RelayCommand` implementations with one shared source in `Core/Commands`. Setup links that source directly, following its existing shared-source convention. Commands now honor `CanExecute` when invoked directly and reject a null action at construction.
- Wrapped Home refresh and Twitch prediction clock timer callbacks with the existing `SafeEventDispatcher`, so a failed dispatcher or log sink cannot escape onto the timer thread. Prediction clock updates return after tab disposal.
- Dispose completed debounce timers before invoking their callbacks, while retaining generation checks and callback error handling.
- Use safe logging for Home cleanup timeouts so a failed diagnostic sink cannot interrupt observation of background cleanup.
- Added four regression tests. Timer failure tests run in isolated child processes to contain any future unhandled thread-pool exception. Standalone reproductions also demonstrated the original disabled-command, refresh-timer, and debounce-disposal failures before the fixes and passed afterward.

Verification:

| Check | Result |
| --- | --- |
| `scripts/dev.ps1 Check` | Passed: locked restore, PowerShell syntax/tooling fixtures, formatting, native dependency provenance, Release build with warnings as errors, and **1,840 tests passed; 274 skipped**. Build: **0 warnings, 0 errors**. |
| New focused regression tests | **4 passed; 0 skipped**. |
| `python -m unittest discover -s scripts/tests -p 'test_*.py'` | **8 passed**. |
| `scripts/tests/release-publication.tests.ps1` under PowerShell 7 | **11 fixture groups passed**, using a fake GitHub CLI. |
| Native fixtures | `test-chat-overlay.ps1`, `test-chat-subpictures.ps1`, `test-chat-compositor.ps1`, and `test-gdi-hardware.ps1` passed. Covered TLS/WebSocket/IRC, rendering/resources, frame bounds and ownership, compositing, color spaces, fallback/device restart, and GDI cleanup. |
| C# reference/duplication scan | **649 files parsed**. Removed both redundant command implementations and the redundant import found in the new test file. |
| Roslyn solution audit | No workspace failures or project compile errors. Standalone PowerShell `Add-Type` imports were retained. |
| `git diff --check` | Passed. |

Unreferenced-member candidates that remain are required Windows ABI fields/COM slots, WPF callbacks/attached-property accessors, or script entry points. The remaining duplicate method bodies are typed `Action`/`Func<T>` overloads around native invocation. These signatures and layouts were retained for their callers and platform contracts.

All 274 skipped tests require an interactive desktop. Authenticated live-provider operations were not exercised.

The starting snapshot, before/after reproductions, verification logs, and review-only source diff are in `.tmp/review-20261008-session/`. `review-only.diff` separates this pass from pre-existing worktree changes.
