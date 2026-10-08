**Code review follow-up - October 7, 2026**

The review covered authored application, Core, infrastructure, bootstrapper, maintenance, native, and tooling code, along with configuration and tests. The starting snapshot contained 907 authored files. Repository-wide syntax, symbol reference, unused-code, and duplicate-method scans complemented manual review of parsing, HTTP and process boundaries, cancellation and resource ownership, playback and replay, chat, downloads, settings, installation, and release tooling.

The starting tree contained substantial uncommitted work. Authored files were snapshotted before editing, and the final changes are compared with that snapshot to preserve the existing work.

Nine new regressions failed against the corresponding starting implementations: seven preview cases in [StreamHoverPreviewTestCatalog.Cleanup](../tests/StreamlinkVlcStudio.Tests/StreamHoverPreviewTestCatalog.Cleanup.cs) and two transport cases in [CodeCleanupTestCatalog.Transports](../tests/StreamlinkVlcStudio.Tests/CodeCleanupTestCatalog.Transports.cs).

| Finding | Correction |
| --- | --- |
| A failed cancellation callback escaped preview replacement and prevented the next hovered stream from starting. | [StreamHoverPreviewController](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamHoverPreviewController.cs) uses the shared cancellation helper, reports callback failures, and continues replacing the session. |
| Preview cancellation invoked callbacks while holding both controller and session state locks. | Replacement, settings changes, shutdown, and session stopping capture state under their locks, then invoke cancellation outside those locks. |
| Reentrant preview shutdown could throw before returning its cleanup task. | The controller publishes one disposal task before invoking callbacks. All callers share it and wait for playback cleanup. |
| One failed frame/state subscriber prevented later subscribers from receiving notifications and could escape the decoder callback. | The session uses [SafeEventDispatcher](../src/StreamlinkVlcStudio.Infrastructure/Chat/SafeEventDispatcher.cs), extended with a parameterless action overload. State and frame delivery continue after subscriber failures. Stopping is idempotent. |
| Playlist shutdown published its task after starting cleanup. A failed lifetime callback could skip draining an outstanding refresh. | [LivePreviewPlaylistSession](../src/StreamlinkVlcStudio.Infrastructure/Previews/LivePreviewPlaylistSession.cs) publishes its disposal task first, cancels safely, and waits for its serving worker. |
| An unexpected playlist worker failure leaked both cancellation sources. | Resource release runs in a cleanup block even when the worker faults; the original failure still reaches the disposal caller. |
| A failed fallback cancellation subscriber turned a supported fallback condition into a worker fault. | Fallback uses the shared cancellation helper. The local listener closes normally, and shutdown releases its sources. Stopping after disposal is also safe. |
| Streamlink transport shutdown invoked process-error logging callbacks under its disposal lock before publishing the task. Reentrant callers received different tasks. | [StreamlinkExternalHttpSession](../src/StreamlinkVlcStudio.Infrastructure/Streamlink/StreamlinkExternalHttpSession.cs) publishes one shared task before stopping the process or invoking diagnostics. |
| A logging failure during transport shutdown skipped waiting for stdout and stderr pumps and released the process owner prematurely. | Output observation and owner release run in cleanup blocks after process stopping, including on failure. The original logging failure still reaches the disposal caller. |

Reuse and dead-code cleanup:

- Preview and transport owners reuse [AsyncDisposal](../src/StreamlinkVlcStudio.Infrastructure/Threading/AsyncDisposal.cs). Both preview owners also reuse [CancellationSourceCleanup](../src/StreamlinkVlcStudio.Infrastructure/Threading/CancellationSourceCleanup.cs), avoiding separate copies of task publication and callback isolation.
- Removed seven parser forwarding methods from [ReplayResolver](../src/StreamlinkVlcStudio.Infrastructure/Replay/ReplayResolver.cs) that had no production callers. Existing tests now exercise the Twitch and Kick provider implementations directly.
- Removed the Twitch duration forwarding method and call [DurationValues.TryParseHmsDuration](../src/StreamlinkVlcStudio.Core/Time/DurationValues.cs) directly from the provider and its tests.
- Removed the unused production overload of [LivePreviewPlaylist.Rewrite](../src/StreamlinkVlcStudio.Infrastructure/Previews/LivePreviewPlaylist.cs). Its tests use the active overload and explicitly discard the initialization URI when it is irrelevant.
- Retained XAML bindings, native/COM layout members, reflection targets, interface entry points, and standalone PowerShell compilation imports after checking their consumers.

| Validation | Result |
| --- | --- |
| Starting `scripts/dev.ps1 Check` | Passed: 1,713 tests passed; 275 desktop tests skipped. Release build: zero warnings and zero errors. |
| New regressions before fixes | Seven preview cases failed, followed by two transport cases failing. Zero passed, skipped, or timed out. |
| Preview suite after fixes | 44 passed; one desktop test skipped. Includes all seven regressions and native VLC TS and fragmented MP4 decoding, changing frames, cancellation, and transport/listener release. Release build: zero warnings and zero errors. |
| Transport and existing cleanup suite after fixes | 24 passed; zero skipped. Includes both transport regressions. Release build: zero warnings and zero errors. |
| Final `scripts/dev.ps1 Check`, PowerShell 7 | Passed: **1,722 tests passed; 275 desktop tests skipped**. Locked restore, PowerShell syntax, complete solution formatting, tooling contracts, and native provenance passed. Release build: **zero warnings and zero errors**. |
| Python measurement tooling | Eight tests passed. |
| Release publication fixtures | Eleven cases passed using a fake CLI and temporary assets. |
| Native overlay, compositor, subpicture/received-frame, and hardware GDI fixtures | All passed with compiler warnings treated as errors. Hardware checks retained no GDI handles. |
| Final production C# syntax/duplicate scan | 396 files and 91,544 lines; zero syntax diagnostics and zero exact duplicate method bodies at the scan's 50-token threshold. |
| Final solution reference audit | 629 C# files; zero workspace or project errors. Removed facade/overload candidates are absent. The two import candidates remain necessary for standalone PowerShell compilation of the dependency probe. |
| Diff against the starting snapshot | 18 intended review files; existing unrelated edits preserved. `git diff --check` passed. |

The 275 skipped cases require an interactive desktop. The headless checks do not exercise complete interactive desktop behavior or authenticated live-provider behavior. Review snapshots, regression logs, native logs, audit output, and the diff against the starting snapshot are in `../../.tmp/review-oct07-followup/`, relative to the repository root.
