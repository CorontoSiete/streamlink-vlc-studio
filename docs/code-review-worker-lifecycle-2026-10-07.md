# Code review: chat state and worker lifecycle, 2026-10-07

Reviewed the repository's managed code, native code and patches, PowerShell/Python tooling, embedded JavaScript, XAML, project configuration, installer definitions, and workflows. The changes fix replay metadata refresh, cancellation, replacement, disposal, and diagnostic failure paths in three production files. Existing uncommitted work was captured before editing and preserved.

| Finding | Fix |
| --- | --- |
| A VOD provider's throwing cancellation callback could interrupt stop/replacement or let disposal skip a fetch still performing cleanup. | Use the existing `CancellationSourceCleanup` helper and await every retained pump. Reentrant and concurrent disposal calls share the existing `AsyncDisposal` helper's completion task. |
| Failed logging could stop VOD retries, prevent polling growing chat, or break duration fallback and live-message capture. | Use the existing `WriteSafely` extension throughout the controller's diagnostic paths. |
| Refreshing metadata for the same replay left the session's broadcast start time unchanged, stranding live messages buffered before that timestamp arrived. | Refresh the session's replay metadata while retaining its timeline and fetch state, then file buffered messages using the newly available broadcast time. |
| Replacing an overlay listener canceled the old listener before publishing the replacement. A cancellation callback could start another listener that the outer start then overwrote. | Publish and track the replacement under the lifecycle lock, then cancel its predecessor outside the lock. |
| Overlay disposal waited for only the latest listener, skipped replaced listeners still dispatching, and permitted another start after disposal. | Reuse `BackgroundOperationController` to retain every outstanding listener. Stop captures a task snapshot before cancellation; disposal shares one completion task and rejects new listeners. An intentional restart during an ordinary stop remains supported. |
| Overlay diagnostic failures could terminate pipe retries or interrupt resize-file failure cleanup. | Reuse `WriteSafely` for listener and resize diagnostics, including timer callback error reporting. |
| A failed logging sink could make bounded background-operation draining throw during shutdown. | Keep timeout and failure diagnostics from interrupting cleanup. |

The production changes are in [VodChatController](../src/StreamlinkVlcStudio.App.Wpf/Chat/VodChatController.cs), [NativeOverlayReplayEventHost](../src/StreamlinkVlcStudio.App.Wpf/Chat/NativeOverlayReplayEventHost.cs), and [BackgroundOperationController](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/BackgroundOperationController.cs). Removed the VOD-specific cancellation wrapper and duplicate disposal-task publisher, the overlay's unnecessary async stop wrapper, and its custom logging exception guard. Snapshot draining and idle draining share their completion helper. Production C# shrank by 23 lines overall.

Added 17 regressions using existing provider, logger, timing, reflection, and native pipe/protocol fixtures. Sixteen failure cases reproduced their bugs before their respective fixes; the remaining case verifies that stopping still permits an intentional reentrant restart. All 17 pass after the fixes. The tests cover workers whose cancellation or dispatch cleanup remains pending, shared disposal completion, callback reentry, cancellation-source disposal, retries after provider errors/timeouts, growing chat, logging failures, and buffered messages awaiting broadcast metadata.

Validation results:

| Check | Result |
| --- | --- |
| Baseline `scripts/dev.ps1 Check` | 1,807 passed; 274 interactive tests skipped. |
| Final `scripts/dev.ps1 Check` | 1,824 passed; 274 interactive tests skipped. Locked restore, formatting, PowerShell syntax/tooling, and native provenance passed. Release build: zero warnings/errors. |
| Focused new regressions | 17 passed. Release build: zero warnings/errors. |
| Production C# syntax and normalized method bodies | 401 files, 92,154 lines; no syntax errors or exact duplicate method-body groups of at least 50 tokens. |
| Solution semantic/reference audit | 647 authored C# files; no workspace failures or project errors. Two imports in the standalone installer probe are required when PowerShell compiles it without project implicit usings. |
| Configuration and script review | 525 source/configuration files scanned; Python, YAML, JSON, XML/XAML/projects, and both WiX installer definitions parsed successfully. PowerShell and native function reference scans found no unused-function candidates. |
| Browser JavaScript | Five extracted snippets with explicit interpolation fixtures plus the authored channel-points script passed `node --check`. |
| Python measurement/tooling tests | 8 passed. |
| Release-publication fixtures | 11 scenario groups passed using a fake CLI and temporary artifacts. |
| Native overlay checks | TLS, networking, readability, rendering/resources, compositing, subpictures, real pipe receipt, and hardware GDI checks passed. Native test builds used warnings as errors. |
| Whitespace | `git diff --check` passed. |

Unused-member candidates were checked against UI bindings, interface dispatch, callback/vtable layouts, standalone PowerShell compilation, and tests before considering deletion. No additional callable production member was confirmed safe to remove. WPF attached-property accessors, native structure fields/vtable methods, interface contracts, and installer probe imports remain necessary. The review concentrates manual inspection on resource ownership, cancellation/reentry, bounded network/process reads, replay/download transitions, settings persistence, and release tooling; the scans cover the broader source inventory.

The full check uses the repository's default headless mode. Interactive desktop coverage and authenticated live-provider flows were not exercised. Native tests used the pinned bundled VLC core and verified native provenance; third-party VLC source was not rebuilt in this pass.

Review evidence is outside the repository at `../../.tmp/review-oct07-final-audit` relative to the repository root. It includes the original 1,508-file snapshot, isolated `review-only.diff` and `review-changes.json`, before/after regression logs, `final-check.log`, the source/semantic/configuration audit results, JavaScript fixtures, and native/tooling test logs. Existing unrelated edits and artifacts were retained.
