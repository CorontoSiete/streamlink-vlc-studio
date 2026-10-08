**Code review — October 6, 2026**

The review covered the Core library, infrastructure, WPF application, bootstrapper, maintenance utility, native modules, scripts, configuration, and tests. Repository-wide source and reference scans covered 732 files; C# semantic checks inspected 614 source files. Manual review focused on parsers, bounded I/O, resource ownership and cancellation, chat, offline VOD storage, provider requests, installation, updates, UI controllers, and native rendering. Bundled native dependencies were checked through the build and provenance checks.

The starting tree already contained substantial uncommitted work. A snapshot of its authored files was taken before editing, and the changes below were compared against that snapshot. Existing work was preserved.

| Finding | Fix and regression evidence |
| --- | --- |
| Offline package validation accepted absolute URLs, network-path references, and root-relative paths when their resolved address resembled the validator's placeholder origin. Playback still used the original references. | [OfflineHlsPlaylist](../src/StreamlinkVlcStudio.Infrastructure/Vod/OfflineHlsPlaylist.cs) checks original references against the generated local file names when validating a package. The shared parser covers segments, encryption keys, and initialization sections. The regression accepts a valid encrypted package and rejects five invalid reference forms for each resource type. |
| Chat supervisor cancellation callbacks could prevent worker draining and disposal; a faulted worker could also bypass resource disposal. | [LiveChatConnectionSupervisor](../src/StreamlinkVlcStudio.Infrastructure/Chat/LiveChatConnectionSupervisor.cs) drains its worker and releases its cancellation source and reconnect semaphore through cleanup blocks that execute after failures. Repeated disposal shares one task. |
| A supervisor shutdown failure could skip the actual Twitch or Kick disconnection. | [ChatConnectionCleanup](../src/StreamlinkVlcStudio.Infrastructure/Chat/ChatConnectionCleanup.cs) provides one stop-and-disconnect path used for connection replacement, failed connection cleanup, disconnection, and disposal in both clients. A regression injects a failed worker into both clients and verifies state clearing, resource release, and subsequent disconnection. |
| EventSub cancellation failures released resources before its worker finished and could skip socket abort. | [TwitchPredictionEventSubClient](../src/StreamlinkVlcStudio.Infrastructure/Chat/TwitchPredictionEventSubClient.cs) attempts abort and drains its worker before releasing the socket and cancellation source, including when cancellation fails. |
| A failed cancellation callback prevented a debounce replacement from returning, leaving its new operation tracked without an owner. It could also interrupt cancellation of other operations. | [CancellationDebounceCoordinator](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/CancellationDebounceCoordinator.cs) reports callback failures through the existing debug diagnostic path and continues cancellation. The regression verifies replacement, cancellation, completion, and draining. |
| Twitch prediction cancellation failures skipped chat disconnection and outstanding request draining. | [TwitchChatClient](../src/StreamlinkVlcStudio.Infrastructure/Chat/TwitchChatClient.cs) performs both cleanup stages before releasing prediction resources. A regression verifies draining and reentrant disposal when a cancellation callback fails. |
| Twitch prediction admission acquired its request lock before the disposal state lock, while disposal acquired them in the opposite order. Concurrent admission and shutdown could deadlock. | Shared disposal publishes its task and closes admission under the state lock, then executes cleanup outside that lock. A regression holds the prediction request lock and confirms shutdown leaves the state lock available while waiting. |

Seven regression cases were added in [CodeCleanupTestCatalog.BoundaryCases](../tests/StreamlinkVlcStudio.Tests/CodeCleanupTestCatalog.BoundaryCases.cs). Six were observed failing before their corresponding fixes and then passing. The seventh checks the lock-order correction. All seven pass in the complete suite.

Cleanup and reuse also include:

- Shared disposal task publication across both chat clients, their supervisor, and EventSub, replacing repeated lifecycle code and the old EventSub completion wrapper.
- Removal of the unused `Select-ReleaseAsset` installer function and `AppAssetPatterns` option. The installer regression now exercises the active exact-name selector, including duplicate assets and non-HTTPS URLs.
- Removal of fourteen redundant test imports and the updater's unreachable dependency-dictionary fallback. Offline reference validation now lives in the parser instead of repeating resolved-address checks in package validation.
- Removal of two unused replay parser forwarding methods and the unused replay-overlay screen hit-test method. The active provider parsers and HWND-based overlay routing remain covered by existing tests.
- Removal of the unused `AppIdentity.DisplayName` and `AppIdentity.ManifestFileName` constants after checking code, XAML, and script callers.
- Reuse of one [InstallOwnership](../src/StreamlinkVlcStudio.Maintenance/InstallOwnership.cs) object while validating its file inventory, avoiding one temporary object per file.

Reference candidates used by XAML bindings, native structure layouts, COM interfaces, reflection-driven tests, or PowerShell entry points were retained. Extension-method calls and conditional Framework-only implementations were checked against their source callers. The standalone dependency probe also retains imports required when PowerShell compiles it without the application's global imports.

| Validation | Result |
| --- | --- |
| Starting `scripts/dev.ps1 Check` | Passed: **1,643 tests passed; 275 skipped**. Build had zero warnings and errors. |
| Focused cleanup regressions | **12 passed**, including the first six new cases and the six existing cleanup cases. |
| Updated installer selector regression | **1 passed**, including exact selection, setup rejection, duplicate/HTTP rejection, and temporary-path confinement. |
| Final `scripts/dev.ps1 Check`, PowerShell 7 | Passed: **1,650 tests passed; 275 skipped**. Locked restore, PowerShell syntax, formatting, tooling contracts, and native provenance passed. Release build: **zero warnings and zero errors**. |
| Verification after final unused-method removals | Passed: **1,650 tests passed; 275 skipped**. Complete `Check` passed, including the Release build with zero warnings and errors. |
| Build and formatting after unused identity-constant removal | Passed. The final Release solution build had **zero warnings and zero errors**; the Core whitespace check passed. |
| Tooling contracts, Windows PowerShell 5.1 | Passed. |
| Python measurement tool tests | **8 passed**. |
| Native overlay/render resources, compositor, subpictures, received frames, and hardware GDI fixtures | Passed with compiler warnings treated as errors. Hardware checks reported no retained GDI handles. |
| Release publication contracts | **11 fixture tests passed**, using the test GitHub CLI stub. |
| Source/reference audits and `git diff --check` | Passed. The final semantic audit reported no workspace or project errors. The final source comparison contains the twenty intended review files and preserves the starting edits. |

The 275 skipped tests require an interactive desktop. Live provider requests and complete interactive UI behavior were not exercised by this headless run. Local evidence, the starting snapshot, and the review-only diff are in `../../.tmp/review-20261006/` relative to the repository root.
