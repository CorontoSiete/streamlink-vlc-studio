# Code review — VOD response handling — 2026-10-08

Repository-wide scans covered the authored C# solution, native modules, PowerShell/Python tooling, XAML, and configuration. Manual review focused on parsing, HTTP boundaries, downloads, cancellation, playback/chat ownership, UI state, and build/release paths. Generated output and downloaded toolchains were excluded. Existing uncommitted work was preserved; changes from this pass are compared with a separate starting snapshot.

## Fixed bugs

- Kick VOD pages now read video arrays inside nested `data` envelopes, matching the pagination envelopes already supported by the service. Previously these pages returned a cursor alongside an empty video list.
- Kick responses without a supported video collection now follow the existing unavailable-result path. Previously malformed objects and scalar responses were reported as successful empty searches.
- Twitch responses without an array-valued `data` property now fail validation. Previously these responses appeared to be successful empty pages and could replace previously loaded cards during refresh.

Genuine empty collections remain valid. Three new regression groups cover nested and bare Kick arrays, pagination, malformed responses, mixed array items, and legitimate empty pages. All three failed against the original production code and pass after the fixes.

## Reuse and cleanup

- Both VOD card constructors share one command-initialization path, with the provider model assigned before the download-command factory runs.
- Kick collection reading reuses the existing JSON property helpers and `OfType<KickVodItem>`. Removed the obsolete element iterator and its repeated enumeration loops.
- Replay timeline cleanup reuses `AtomicFile.TryDeleteTemporaryFile`. Removed the redundant private deletion helper.

Production C# decreased by 21 lines. This pass changes four production files and one existing test catalog, plus this note.

## Verification

| Check | Result |
| --- | --- |
| Starting headless suite | 1,851 passed; 274 skipped. |
| Regression run before fixes | 10 existing cases passed; all 3 new cases failed as expected. |
| `scripts/dev.ps1 Check -NoRestore` | Passed: formatting, PowerShell syntax/tooling contracts, pinned native provenance, Release build with zero warnings/errors, and 1,854 tests passed; 274 skipped. The starting run completed the locked restore. |
| Python measurement tests | All 8 passed. |
| PowerShell 7 release-publication fixtures | All 11 groups passed using a fake GitHub CLI. |
| Native protocol/rendering, subpicture/pipe, compositor, and hardware fixtures | Passed, including strict compiler warnings and resource-lifetime checks. |
| Source audits | No syntax errors, exact duplicated method bodies at the 50-token threshold, or additional removable cross-language symbols. Solution semantic audit reported no workspace or compilation errors. |
| `git diff --check` | Passed. |

Unused-member candidates were checked against WPF bindings, framework hooks, native layouts, and script entry points. The dependency probe's explicit imports are required when compiled independently by PowerShell.

The 274 managed desktop tests were skipped by the headless policy. Authenticated live-provider sessions were not exercised.

The starting snapshot, before/after logs, source audits, native results, and review-only diff are in `.tmp/code-review-2026-10-08-current/`.
