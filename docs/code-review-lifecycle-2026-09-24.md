# Code review: application and chat lifecycles

## Scope

This pass combined solution-wide compiler/analyzer and formatting checks with source-reference,
duplication, and cancellation scans. The starting snapshot contained 343 C# files (278 production
files), plus browser code, PowerShell tooling, XAML, project files, and release configuration.
Manual review focused on application startup/shutdown, replay/chat lifetimes, settings, request
deadlines, credential caches, stream metadata, playback cleanup, and installer boundaries.
Generated output and bundled third-party binaries were excluded from refactoring.

The supplied folder has no Git metadata. A pre-edit source snapshot and a unified patch are kept
under `artifacts/review-20260924/` so this pass can be reviewed independently.

## Bugs fixed

| Area | Previous behavior | Change |
| --- | --- | --- |
| Second application launch | The secondary instance set the shared maintenance-shutdown event during exit, potentially closing the running primary instance. | Exit only cancels the local listener. Shared activation and shutdown events are no longer signaled during cleanup. |
| VOD request timeout | Any provider `OperationCanceledException`, including an HTTP deadline, permanently stopped the chat pump. | Only cancellation of the session stops the pump; provider timeouts enter the normal retry path. |
| Seeking during chat fetch | An old completed page could set `Exhausted` after a seek, even though its frontier update was rejected. | Completion, failure, and unsupported status now obey the same generation check as the frontier. Valid messages from the same broadcast remain reusable. |
| DVR-to-VOD promotion | An in-flight unsupported response for the live DVR could overwrite the promoted session's state. | Promotion advances the generation, and each request captures its source, settings, offset, and generation together. |
| Chat shutdown | Stopped/replaced pumps were not awaited on disposal, their token sources were not disposed, and a second disposal call returned before the first finished. | Track active sessions, publish each pump atomically, let the pump dispose its source, and share one disposal completion across callers. Result application and timeline reads also remain inside the session lock. |
| Twitch EventSub reconnect | If disposal rejected a newly opened socket, the local socket variable had already been replaced, so final cleanup missed the previous socket. | Transfer local ownership only after active-socket replacement succeeds. |

## Reuse and dead code

- Live-channel response caching uses the existing length-prefixed credential fingerprint helper,
  removing its separate hashing implementation and temporary credential concatenation.
- Application startup and maintenance shutdown share one single-instance mutex name.
- Removed the replay-preview helper that had no production callers. Its existing test now drives
  `ReplaySeekSliderValue`, the property used by the actual controls.
- Removed the bootstrapper's redundant readiness event and duplicate join/error branch. The
  caller always joins the UI thread, which already supplies the required synchronization.
- Removed superseded VOD state-forwarding methods and unused HTTP-header imports.

## Regression evidence

Seven lifecycle tests were added. Five VOD cases reproduced failures before the changes; the
replacement-drain case was strengthened to keep the retired request outstanding after stopping
its replacement. The isolated application-exit test also failed before the fix. It uses unique
named events and opens no windows, so it cannot signal a user's running player.

All seven new tests pass. The focused VOD run also passed all 22 selected tests, including the
existing timeline, paging, seeking, promotion, and live-chat-capture coverage.

## Validation

- Release solution build with warnings as errors: zero warnings and errors.
- Full headless regression suite: 735 passed, 207 desktop-only skips, no failures or timeouts.
  The existing ceiling of 207 skips was enforced; both packaging tests passed with the corrected PATH.
- Locked restore with transitive NuGet vulnerability auditing: passed.
- Solution-wide code-style and analyzer checks reported no code diagnostics. One new test's
  whitespace was corrected; the final solution-wide whitespace verification passed.
- Browser tests: 25 passed; JavaScript syntax checks passed.
- All 20 PowerShell scripts parsed; release-tooling contracts and generated browser routes passed.
- Both pinned native overlay inputs were verified by the build.

The first full baseline run passed 726 tests with 207 desktop skips; two packaging tests failed
because child processes found the system .NET 8/9 host instead of the installed, pinned .NET
10.0.302 SDK. Final validation puts that SDK first on the process PATH.

Interactive window/input behavior, authenticated live-provider requests, and executing the
production installer are outside this pass's validation. Automated checks and focused manual
review cannot establish that every runtime defect has been eliminated.

## Artifacts

- `artifacts/review-20260924/before.zip`: source before this pass.
- `artifacts/review-20260924/changes.patch`: normalized source/test/document diff.
- `artifacts/review-20260924/`: baseline, reproduction, build, analysis, and regression logs.
