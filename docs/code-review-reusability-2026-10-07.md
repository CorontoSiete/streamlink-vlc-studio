**Repository review: cleanup and reuse - October 7, 2026**

Fixed settings and VOD-history read races, interrupted shutdown, playback-recovery state failures, and repair-proxy ownership bugs. Eighteen new regression cases reproduce these failures and pass after the corrections. Existing uncommitted work was preserved and this pass was compared with a starting snapshot of 952 authored source, configuration, and documentation files.

The review combined repository-wide build, syntax, reference, unused-code, and duplicate-method checks with manual inspection of lifecycle, cancellation, callbacks, file access, HTTP serving, and resource ownership. It covered the application, Core, infrastructure, bootstrapper, maintenance, native code, scripts, workflows, configuration, and tests.

| Finding | Correction |
| --- | --- |
| The initial full check reproduced an autosave sharing violation even though settings readers already allowed file replacement. VOD history used the same read mode. | Both services use the shared `AtomicFile.OpenReadAsync` snapshot reader. It retries only sharing violations, with 500 ms of bounded retry delays, honors cancellation, and preserves other file errors. Reads continue to allow atomic replacement while preventing concurrent in-place writes. |
| A failed Recent thumbnail cancellation callback could skip timer disposal and collection unsubscription. | `RecentStreamsViewModel` uses the existing `CancelOperation` helper so shutdown releases the timer, source, and subscription. |
| Live-recovery cancellation could throw during stop or pause, or target an already disposed source. | `StreamTabViewModel.LiveRecovery` uses the shared `CancellationSourceCleanup` helper. Stop and pause finish their transitions and clear recovery/busy state. |
| Recovery logging could throw before the cleanup `try/finally`, leaving the tab busy with a disposed cancellation source. Other diagnostic failures could disrupt successful recovery or its retry state. | Recovery diagnostics use `WriteSafely`, and the starting diagnostic runs inside the cleanup scope. Success and retry state remain governed by playback operations. |
| Repair-proxy disposal invoked cancellation before publishing its disposal task. Callback failures could skip socket closure and request draining; a failed worker could skip semaphore and source disposal. | `TwitchMutedVodRepairProxy` uses `AsyncDisposal` to publish one task before callbacks run, safe cancellation to revoke sessions, and `finally` blocks to drain requests and release resources. Worker failures remain visible to callers. |
| Concurrent gateway disposal could return before the first cleanup completed. A failed proxy cleanup leaked the gateway's owned HTTP client. | `TwitchMutedVodPlaybackGateway` uses the same shared disposal helper. A `finally` releases its owned HTTP client even when the proxy fails; injected clients remain caller-owned. |
| Repair diagnostics could interrupt listener startup, session release, or gateway preparation after a session had been allocated. | Proxy and gateway diagnostics use the existing `WriteSafely` helper. Local serving and the repaired playback lease survive diagnostic failures. |

Microsoft documents that `ReplaceFile` opens the replacement file without sharing. The observed read failures are consistent with that exclusive handle briefly overlapping a visible destination; the exact race is an inference supported by the failing full check. The deterministic fixtures hold an exclusive handle on an otherwise valid snapshot, then verify successful reading after release and cancellation while waiting. [Microsoft ReplaceFile documentation](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).

Reuse and removal:

- Settings and history share one snapshot reader; cancellation, diagnostics, and asynchronous disposal reuse existing helpers.
- Removed `StreamTabViewModel.DisposeDetachedStreamSessionAsync`; both callers dispose the transport directly.
- Removed three unused MainWindow forwarding methods: `PositionDetachedWindow`, `RememberPictureInPictureWindowBoundsAsync`, and `ApplyTheatreModeChatToSelectedTab`. Existing desktop tests now target the active picture-in-picture and window-mode controllers through one shared test helper.
- Regression cases reuse existing fixtures, snapshot checks, diagnostic-failure setup, and proxy-resource assertions. XAML consumers, reflection, interfaces, extension methods, standalone PowerShell compilation, and native/COM layouts were checked before retaining reference-audit candidates.

| Validation | Result |
| --- | --- |
| Initial `scripts/dev.ps1 Check` | 1,753 passed; one settings sharing failure; 275 desktop tests skipped. Release build: zero warnings and errors. |
| New regressions before production fixes | Eighteen failures reproduced. |
| Focused regressions after fixes | **18 passed; zero skipped**. Release build: zero warnings and errors. |
| Final `scripts/dev.ps1 Check` | **Passed: 1,772 tests passed; 275 desktop tests skipped**. Locked restore, PowerShell syntax, solution formatting, tooling contracts, and native provenance passed. Release build: **zero warnings and errors**. |
| Python measurement tooling | Eight tests passed. |
| Release publication fixtures | Eleven cases passed with a fake CLI and temporary assets. |
| Native overlay, compositor, subpicture/received-frame, and hardware GDI fixtures | All passed with compiler warnings treated as errors. Hardware checks retained no GDI handles. |
| Final production C# syntax and duplicate scan | 401 files and 92,172 lines; zero syntax errors and zero exact duplicate method groups at the 50-token threshold. The two smaller duplicate groups are short adapters. |
| Final solution reference audit | 641 authored C# files; zero workspace or project compilation errors. Removed forwarding methods no longer appear as reference candidates. The two import candidates in `WindowsDependencyProbe.cs` remain necessary when PowerShell compiles it independently. |
| Cross-language parsing and references | Seven Python files, 34 XML/XAML/project files, 31 JSON files, and three workflow YAML files parsed successfully. No unused Python-import, PowerShell-function, or native static-function candidates in the source audit. PowerShell syntax is also checked by the full development check. |
| Starting-snapshot comparison | Seventeen intended source/test files plus this report. Existing unrelated edits preserved. `git diff --check` passed. |

The 275 skipped cases require an interactive desktop. Authenticated live-provider behavior was not exercised. Local snapshots, isolated diffs, regression logs, native logs, and audit output are in `../../.tmp/review-oct07-reusability/` relative to the repository root.
