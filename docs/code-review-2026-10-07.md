**Code review — October 7, 2026**

The review covered application source, Core, infrastructure, the bootstrapper and maintenance utility, native modules, scripts, configuration, and tests. Repository-wide source and reference scans inspected 747 authored files. Manual review focused on cancellation, resource ownership, playback and replay, chat, VOD downloads, input parsing, bounded I/O, settings, installation, updates, and native boundaries.

The starting tree contained substantial uncommitted work. Its authored files were snapshotted before editing. Changes from this review were compared against that snapshot to preserve the existing work.

Nine regression cases were observed failing before their corresponding production fixes. They now pass in [CodeCleanupTestCatalog.Lifetimes](../tests/StreamlinkVlcStudio.Tests/CodeCleanupTestCatalog.Lifetimes.cs).

| Finding | Correction |
| --- | --- |
| A download cancellation callback could throw out of `CancelAsync`, interrupting cancellation and retry handling. | [VodDownloadService](../src/StreamlinkVlcStudio.Infrastructure/Vod/VodDownloadService.cs) reports callback failures and continues cleanup. The regression cancels a blocked resolver and successfully retries the download. |
| Download cancellation invoked callbacks while holding the library semaphore. A callback querying the library could deadlock. | The service captures the job under the semaphore, releases it, and cancels that captured generation. The regression verifies a callback can immediately query the library. |
| Failed cancellation callbacks during download shutdown could skip worker draining, leave queued downloads active on disk, and leak resources. | Shutdown drains the worker and releases queued job sources, interrupted records, and owned resources through cleanup blocks that run after failures. |
| One failed tab-start cancellation callback prevented remaining registrations from being canceled. | [TabStartController](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/TabStartController.cs) continues cancellation of every registration. |
| Home feature disposal published its task after invoking callbacks, and failures could skip background work or resource release. | [HomeFeatureViewModel](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/HomeFeatureViewModel.cs) publishes one task before cleanup starts. It drains both operation sets and releases its lifetime source even when draining or resource release fails. Two regressions cover callback reentry and failure during draining/release. |
| Main and stream-tab disposal could be reentered before their disposal task was published. Failed lifetime callbacks also interrupted cleanup. | [MainViewModel](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/MainViewModel.cs) and [StreamTabViewModel](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamTabViewModel.cs) share the task publication and cancellation helpers. Two regressions verify reentrant callers receive the same task and lifetime sources are released. |
| A failed background update or Home cleanup could prevent Main shutdown from starting tab disposal and draining tracked work. | Main starts tab cleanup before awaiting background, Home, and preview cleanup. Tracked work and detached tab disposal are drained through a cleanup block before lifetime resources are released. The regression injects a failed task and unfinished tracked work, then verifies draining, tab lifetime release, and propagation of the original failure. |

Reuse and cleanup:

- [AsyncDisposal](../src/StreamlinkVlcStudio.Infrastructure/Threading/AsyncDisposal.cs) moves generic task publication out of chat-specific cleanup and replaces repeated disposal implementations across chat, downloads, and view models. Cleanup starts outside the state lock.
- [CancellationSourceCleanup](../src/StreamlinkVlcStudio.Infrastructure/Threading/CancellationSourceCleanup.cs) replaces repeated cancellation wrappers. Failed callbacks and diagnostic reporting cannot prevent remaining cleanup. Stream-tab polling still drains its task after a callback failure, and active-start callbacks run outside the video-surface lock.
- Removed the replaced chat disposal wrapper/completion routine, debounce cancellation helper, stream-tab cancellation helper, and an unused live playback test import. Required XAML bindings, native layouts, COM members, reflection targets, and standalone PowerShell compilation imports were retained after checking their callers.
- Download fixture disposal now releases its HTTP client and temporary directory even when service disposal fails. The initial formatting failure in the live playback test was corrected.

| Validation | Result |
| --- | --- |
| Starting `scripts/dev.ps1 Check` | Stopped at existing whitespace errors in the live playback test; the build and complete suite were not reached. |
| Regression evidence before fixes | Six download/tab/Home cases failed, followed by two view-model cases and the parent shutdown case failing. No test timeouts or skipped cases. |
| Focused cleanup suite after fixes | **22 passed; 0 skipped**, including all nine new cases. Release build: **zero warnings and zero errors**. |
| Final `scripts/dev.ps1 Check`, PowerShell 7 | Passed: **1,713 tests passed; 275 skipped**. Locked restore, PowerShell syntax, complete solution formatting, tooling contracts, and native provenance passed. Release build: **zero warnings and zero errors**. |
| Python measurement tooling | **8 passed**. |
| Native overlay, compositor, subpictures/received frames, and hardware GDI fixtures | All passed with compiler warnings treated as errors. Hardware checks reported no retained GDI handles. |
| Final source scan | 747 authored files, 398 production C# summaries, no syntax diagnostics. Remaining duplicate candidates are small event/interface wrappers or code in independent projects. |
| Solution reference audit | 627 C# source files; no workspace or project errors. The remaining two import candidates are required when PowerShell compiles the standalone dependency probe. |
| Diff and starting snapshot comparison | 20 intended review files; existing unrelated edits preserved. `git diff --check` passed. |

The 275 skipped cases require an interactive desktop. Live provider requests and complete interactive UI behavior were not exercised by the headless checks. Local snapshots, regression logs, native logs, audit output, and the diff against the starting snapshot are in `.tmp/code-review-2026-10-07/` at the repository root.
