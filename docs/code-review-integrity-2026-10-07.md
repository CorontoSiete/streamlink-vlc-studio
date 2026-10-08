# Code review and fixes: 2026-10-07

Fixed preview and playback failure paths, consolidated resource cleanup and HLS parsing, and removed superseded helpers and imports. Seven regression tests reproduced failures before their corresponding fixes and now pass. The final development checks pass with **1,807 tests passed, 274 skipped, zero build warnings, and zero build errors**.

## Fixes

| Failure | Result after the fix |
| --- | --- |
| Logging a failed hover preview faulted the serialized playback task; every later hover inherited that fault. | Optional diagnostics use the existing safe logging helper, allowing the next hover to play. |
| Direct-preview startup and fallback diagnostics could throw, interrupting direct playback or preventing Streamlink fallback. | Logging failures preserve direct playback, source disposal, and the original fallback request. |
| Main playback diagnostics could interrupt initialization, renderer/audio recovery, or direct-media fallback. | The engine's diagnostic writes consistently use `WriteSafely`; a failed gateway and failed logger still return the direct media source. |
| Revoking an FFmpeg media source could throw before releasing the player, media, and replay event handles. | Source revocation runs with guaranteed player/media/event cleanup. Other source adapters remain alive until playback stops. |
| An exception during playback shutdown left the engine marked disposed while retaining its runtime lease and audio worker resources. | Runtime ownership is detached and released in `finally`; worker shutdown also runs after a source cleanup failure. |
| Throwing cancellation callbacks prevented installation of audio-worker cleanup. | The existing cancellation helper isolates callback failures; semaphore and cancellation resources are released after the worker completes. |

Primary changes are in [StreamHoverPreviewController.cs](../src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamHoverPreviewController.cs), [LibVlcLivePreview.cs](../src/StreamlinkVlcStudio.Infrastructure/Vlc/LibVlcLivePreview.cs), and [LibVlcPlaybackEngine.cs](../src/StreamlinkVlcStudio.Infrastructure/Vlc/LibVlcPlaybackEngine.cs).

## Reuse and removal

- `ReleasePlayerCore` now handles native player, media, and replay-event cleanup for both normal stopping and failed creation. It detaches ownership before invoking cleanup and attempts every release through `finally` blocks.
- [HlsPlaylistPolicy.cs](../src/StreamlinkVlcStudio.Infrastructure/Hls/HlsPlaylistPolicy.cs) provides one strict initialization-map URI parser for live previews and seek previews. Both duplicate generated regex factories, their regular-expression imports, and unnecessary partial declarations were removed. Provider, encryption, and byte-range policies remain in the callers.
- The shared `MemoryLogger` fixture supplies failed diagnostic writes across existing cleanup tests and the new preview/playback regressions. The superseded catalog-specific factory and throwing callback were removed.

## Review coverage

Repository-wide audits covered 401 production C# files, including shared installation code and the independently compiled PowerShell dependency probe. A solution semantic audit examined 645 authored C# files across production and tests. A cross-language audit covered 520 source/configuration files with Python syntax/import checks, PowerShell/native function-reference checks, and parsing of JSON, YAML workflows, XAML, project files, and the application manifest. Development checks validated PowerShell syntax; native test builds validated the exercised C code. Manual review focused on playback and preview ownership, cancellation, HLS boundaries, settings, downloads, chat, updater/installer paths, and the reference/duplication candidates.

The audits found no C# syntax errors, cross-language parse errors, unused Python imports, unreferenced PowerShell/native static function candidates, or exact duplicate production method bodies of at least 50 tokens.

Sparse-reference candidates were checked against WPF bindings, virtual/interface dispatch, native struct layouts and COM method order, generated code, and alternate compilation paths. The dependency probe's explicit `System` and `System.Threading.Tasks` imports are required when PowerShell compiles it without .NET SDK global imports. No additional callable production dead-code candidate was confirmed.

## Validation

- Baseline `scripts/dev.ps1 Check`: 1,800 tests passed, 274 skipped.
- Final `scripts/dev.ps1 Check`: locked restore, formatting/unused-code diagnostics, PowerShell/tooling contracts, native provenance checks, warning-free Release build, and 1,807 tests passed with 274 skips.
- All seven added regressions passed in the full suite. The source-disposal tests cover both FFmpeg and other media adapters.
- Python measurement tooling: 8 tests passed.
- Release-publication fixtures: 11 scenario groups passed, using a fake CLI and temporary signed fixtures.
- Native tests rebuilt with `-Wall -Wextra -Werror`: TLS, networking, overlay readability/resource ownership, compositor, subpictures, received frames, and hardware GDI checks passed across eight test executables.
- `git diff --check` passed. The final diff was also reviewed against a saved copy of the existing working tree.

All 274 skipped cases reported an unavailable interactive desktop and are not verified by this run. Source and native audits complement the executed tests; they do not establish that every possible runtime condition is covered.

Review evidence, baseline snapshots, audit output, before/after regression logs, native logs, and the review-only diff are stored in `../../.tmp/review-integrity-oct07` relative to the repository. Existing working-tree edits were included in the review baseline and preserved.
