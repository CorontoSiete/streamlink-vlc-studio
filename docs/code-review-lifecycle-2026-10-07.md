**Repository code review: lifecycle and persistence — October 7, 2026**

The review covered the application, Core, infrastructure, bootstrapper, maintenance, native code, scripts, workflows, configuration, and tests. Repository-wide syntax, reference, unused-code, and duplicate-method audits complemented manual review of lifecycle, callback, process, HTTP, parsing, and persistence boundaries.

The starting snapshot preserved 972 authored source, configuration, and documentation files. Existing uncommitted work was retained; this review's changes were compared with that snapshot.

| Finding | Correction |
| --- | --- |
| A settings reader could collide with an autosave's atomic replacement. The initial full check reproduced this as a sharing violation in the settings/hotkey test. VOD history had the same read mode. | Settings and history readers permit `FileShare.Delete`, so Windows can replace the directory entry while each reader finishes reading its complete snapshot. A deterministic regression holds the DELETE access used by replacement without modifying the file. |
| Failed logging interrupted automatic update completion handling and retries. | [AutomaticUpdateController](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/AutomaticUpdateController.cs) uses the shared `WriteSafely` helper, preserving its retry schedule. |
| Failed logging could prevent a confirmed Twitch bonus from being saved or stop polling. Cancellation and page-disposal failures could skip other workers. | [TwitchChannelPointsController](../src/StreamlinkVlcStudio.App.Wpf/Twitch/TwitchChannelPointsController.cs) uses shared safe logging and cancellation, and one reusable page-release method. Pages detach before disposal callbacks; cleanup continues across failing workers and releases the browser and lifetime source. |
| Native overlay cancellation could reenter disposal before the disposal task was published, or interrupt cleanup. Failures in one participant could skip later resources. | [NativeChatOverlayController](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/NativeChatOverlayController.cs) uses the existing `AsyncDisposal` helper to publish one task before callbacks run. Cleanup guarantees event-host, renderer, input-release, frame-write, and lifetime cleanup through `finally` blocks. |
| Native overlay startup invoked cancellation callbacks under its state lock. Animation cancellation failures could leave image pins and retained state behind. | Startup publishes its next operation under the lock, then cancels the previous operation outside it. Startup, warmup, and animation paths share `CancellationSourceCleanup`; optional diagnostics use `WriteSafely`. |
| Logging could interrupt recovery after a damaged VOD history file had been backed up. | [JsonVodPlaybackHistory](../src/StreamlinkVlcStudio.Infrastructure/Vod/JsonVodPlaybackHistory.cs) isolates the warning callback so recovery completes and new bookmarks can be saved. |

Removed three redundant helpers: the native controller's custom cancellation wrapper, the stream-tab animation-delay forwarding method, and the boolean live-replay policy forwarding method. Callers now use existing shared cleanup or the active implementation directly. XAML attached-property setters, interface implementations, extension methods, reflection targets, test hooks, and native/COM layout members were checked against their consumers before retaining them.

Thirteen new regression cases cover four native-overlay lifecycle paths, four Twitch bonus paths, two updater diagnostic paths, settings/history sharing, and damaged-history recovery. The first twelve failed against the starting implementations; the additional recovery case independently reproduced its logging failure before correction. All thirteen passed in the final full check. Tests reuse existing fixtures and cleanup assertions.

| Validation | Result |
| --- | --- |
| Initial `scripts/dev.ps1 Check` | 1,740 passed; one settings sharing failure; 275 desktop tests skipped. Release build: zero warnings and errors. |
| Full check after the first twelve regressions | Passed: 1,753 tests passed; 275 desktop tests skipped. Locked restore, PowerShell syntax, solution formatting, tooling contracts, native provenance, and build passed. |
| Final `scripts/dev.ps1 Check` | Passed: **1,754 tests passed; 275 desktop tests skipped**, including all thirteen new regressions. Locked restore, PowerShell syntax, solution formatting, tooling contracts, native provenance, and build passed. Release build: **zero warnings and zero errors**. |
| Python measurement tooling | Eight tests passed. |
| Release publication fixtures | Eleven cases passed with a fake CLI and temporary assets. |
| Native overlay, compositor, subpicture/received-frame, and hardware GDI fixtures | All passed with compiler warnings treated as errors. Hardware fixtures retained no GDI handles. |
| Final production C# syntax and duplicate-method scan | 399 files and 91,538 lines; zero syntax errors and zero exact duplicate method bodies at the 50-token threshold. |
| Final solution reference audit | 640 authored C# files; zero workspace or project compilation errors. The two import candidates are required when `WindowsDependencyProbe.cs` is compiled independently by PowerShell. Remaining candidates were checked against XAML, extensions, reflection, interface dispatch, standalone scripts, and native layouts. |
| Cross-language audit | 503 production files; zero Python syntax/import issues, XML/JSON parse errors, unreferenced PowerShell functions, or unreferenced native static-function candidates. |
| Diff checks | Nineteen intended review files compared with the starting snapshot; existing unrelated edits preserved. `git diff --check` passed. |

The 275 skipped tests require an interactive desktop. Authenticated live-provider behavior was not exercised. Local snapshots, isolated diffs, regression logs, native logs, and audit output are in `../../.tmp/review-oct07-session/` relative to the repository root.
