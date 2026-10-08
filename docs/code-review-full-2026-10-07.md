**Repository code review - October 7, 2026**

The review covered the application, Core, infrastructure, bootstrapper, maintenance, native components, scripts, workflows, configuration, and tests. Repository-wide syntax, symbol-reference, unused-code, and duplicate-method scans complemented manual review of HTTP and process boundaries, parsing, credential caches, resource ownership, cancellation, playback, chat, downloads, settings, installation, and release tooling.

The starting snapshot contained 913 authored files and substantial existing uncommitted work. Changes from this review were compared with that snapshot to preserve the existing work.

| Finding | Correction |
| --- | --- |
| Media and avatar downloads accepted mixed-case HTML/JSON content types and structured JSON error documents. These could complete as unusable media or replace an avatar with an error page. | Both download paths use [HttpContentTypePolicy](../src/StreamlinkVlcStudio.Infrastructure/Http/HttpContentTypePolicy.cs), which rejects HTML, XHTML, JSON, and structured JSON documents using case-insensitive comparisons. Failed media is cleaned up; optional avatars retain their saved URL. |
| Native TLS diagnostics used one shared error buffer. Failures in another thread could overwrite the caller's error. | [tls.c](../native/chat-overlay/tls.c) uses per-thread storage. The bundled controller was rebuilt and its hash and provenance updated. The native build and test scripts statically link the required compiler threading support; the controller imports only Windows system DLLs. |
| Browse cancellation invoked callbacks under its state lock. Reentrant cancellation could block, and callback failures interrupted navigation and cleanup. | [BrowseViewModel](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/BrowseViewModel.cs) captures and clears cancellation state under the lock, then invokes cancellation outside it. Home features share callback isolation and warning reporting through [HomeFeatureViewModel](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/HomeFeatureViewModel.cs). |
| A queued viewer-count callback could start new provider work after Browse was disposed. | Viewer-count loads check disposal before collecting work and again under the state lock. |
| A failed followed-channel cancellation callback skipped timer and settings-subscription cleanup. | [FollowedChannelsViewModel](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/FollowedChannelsViewModel.cs) uses the shared Home cancellation path and continues detaching subscriptions and releasing resources. |

Removed the redundant internal token-refresh overload from [KickOAuthService](../src/StreamlinkVlcStudio.Infrastructure/Chat/KickOAuthService.cs). It had no production callers; its tests now exercise the existing [KickUserTokenCoordinator](../src/StreamlinkVlcStudio.Infrastructure/Chat/KickUserTokenCoordinator.cs) directly. XAML bindings, interface implementations, script entry points, extension methods, reflection targets, and native/COM layout members were checked against their consumers before retaining them.

Eight managed regression cases cover error-document downloads, navigation after failed cancellation, cancellation reentry, delayed work after shutdown, and followed-channel subscription cleanup. A deterministic native test holds three different errors across three threads. The download and Home cases failed before the corresponding fixes, and the native isolation assertion failed against the original shared buffer. All pass after the fixes. The navigation regression's cleanup was also tightened to preserve the original failure when the test fails.

| Validation | Result |
| --- | --- |
| Starting `scripts/dev.ps1 Check` | Passed: 1,725 tests passed; 275 desktop tests skipped. Release build: zero warnings and zero errors. |
| Download suite after fixes | 80 passed; zero skipped. Includes both providers and all new error-document cases. |
| Cleanup suite after fixes | 31 passed; zero skipped. Includes the four new Home-feature cases. |
| Token and timeout regression suites | 16 repository-review cases and 12 timeout-recovery cases passed; zero skipped. |
| Final `scripts/dev.ps1 Check` | Passed: **1,733 tests passed; 275 desktop tests skipped**. Locked restore, PowerShell syntax, complete solution formatting, tooling contracts, and native provenance passed. Release build: **zero warnings and zero errors**. |
| Python measurement tooling | Eight tests passed. |
| Release publication fixtures | Eleven cases passed using a fake CLI and temporary assets. |
| Native overlay, compositor, subpicture/received-frame, and hardware GDI fixtures | All passed with compiler warnings treated as errors. Hardware fixtures retained no GDI handles. |
| Full native rebuild | Reproduced the packaged controller exactly. Its SHA-256 is `AF5A25F4932A24A41BB49E32E04E450DEF20B8352D7245F6D7510F9F6D9F0396`. Native provenance verification passed. |
| Final production C# syntax and duplicate-method scan | 398 files and 91,616 lines; zero syntax diagnostics and zero exact duplicate method bodies at the 50-token threshold. |
| Final solution symbol-reference audit | 634 authored C# files; zero workspace or project errors. The removed refresh overload is absent from the final candidates. The two import candidates are required by standalone PowerShell compilation. |
| Cross-language audit | 504 production files; zero Python syntax/import issues, unreferenced PowerShell functions, or unreferenced native static-function candidates. |
| Final diff and whitespace checks | 22 intended authored review files plus the rebuilt native controller. Existing unrelated edits preserved. `git diff --check` passed. |

The 275 skipped tests require an interactive desktop. Authenticated live-provider behavior was not exercised. Snapshots, isolated diffs, regression logs, native build/test logs, and audit output are in `.tmp/full-code-review-20261007/` relative to the repository root.
