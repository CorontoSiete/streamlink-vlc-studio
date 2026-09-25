# Repository review — September 25, 2026

The review combined solution-wide compiler, analyzer, formatting, reference, and duplication
checks with manual inspection of parsing, HTTP and chat boundaries, playback and UI lifetimes,
settings, bootstrapper/maintenance code, and installation/release tooling. The starting source
contained 362 C# files, 19 PowerShell scripts, and one JavaScript file. Build and regression checks
also covered the XAML and project configuration. Generated output and bundled third-party binaries
were excluded from refactoring.

The supplied directory has no Git metadata. The original source snapshot and a unified patch are
preserved under `artifacts/review-20260925/`.

## Fixes

| Area | Result |
| --- | --- |
| Channel identity | Viewer counts and stream metadata require a matching string channel identity. An unidentified response row cannot replace the requested Twitch or Kick channel. |
| Live/offline status | Malformed response rows and missing Kick stream fields are unavailable, rather than incorrectly reported as offline. Empty valid results, explicit null streams, and `is_live: false` retain their offline behavior. |
| Viewer counts | Missing, malformed, overflowing, and negative counts are unavailable. Zero remains a valid live count. Valid metadata remains usable when the count is unavailable. |
| Settings recovery | Loading defaults from invalid or oversized settings now records a warning identifying the backup, or the original path if moving it failed. A later successful load clears that warning. |
| Installation paths | Ownership manifests use the shared Windows path validator, rejecting trailing dots/spaces, alternate data streams, device names, and noncanonical segments. Both the PowerShell reader and maintenance executable normalize directory separators before duplicate checks. The writer deduplicates separator aliases. |
| Locked restore | Normal restore previously failed with NU1004 because the checked-in lock file included the SDK's publishing tool dependency. Enabling the single-file analyzer consistently gives normal builds and publishing the same dependency graph. No package versions or lock files changed. |
| Test results | A failed isolated test process takes precedence over any skip message it printed before exiting. Skip-output parsing is performed once. |

## Reuse and dead code

- `LiveChannelPayloadReader` owns channel selection and live/offline interpretation for both
  viewer-count and metadata services. This replaces repeated array/identity/stream-shape logic.
- Home opening paths share one resolved-stream startup method. Existing direct-open tests now
  exercise the same implementation used by Search, Followed, Browse, Recent, VODs, and notifications.
- Picture-in-picture dragging and tab-strip grouping share group selection.
- Invalid settings and protected-secret recovery share backup-path generation and file handling.
- Release contracts and installation ownership share `Test-SafeWindowsRelativePath`.
- Removed the unused replay shortcut policy file, HTTP header accessor, update-start result type,
  and redundant scroll, rate-limit, VLC-option, and muted-playlist wrappers. Tests call the retained
  production implementations directly; their assertions were preserved.
- Corrected obsolete browser-extension playback documentation and an existing test-formatting error.

Production source and shared scripts have 250 fewer lines overall, including the new shared parser.

## Regression evidence

Eleven .NET cases were added. Eight reproduced failures before their corresponding fixes:
five live-response cases, two settings recovery cases, and duplicate installation-path aliases.
The other three cover valid provider behavior and isolated-process result classification.
The tooling regression also demonstrated that the old ownership writer accepted `app.exe.`.
Two tooling cases now cover invalid Windows paths and separator normalization/deduplication.

The baseline suite passed 783 tests with 236 desktop-only skips.

## Verification

- Final full headless suite: **794 passed, 236 skipped, zero failures or timeouts**. The existing
  ceiling of 236 skips was enforced.
- Final Release solution build, including single-file analysis: **zero warnings and errors**.
- Full solution formatting/analyzer verification: passed.
- Locked restore and transitive vulnerability auditing: passed for the solution, self-contained
  Windows restore, and single-file publishing restore. All three dependency lock files are unchanged.
- All 19 PowerShell files parsed; all 14 tooling checks passed.
- JavaScript syntax check and XML parsing of all 24 XAML/project/application-manifest files passed.
- Both pinned native overlay inputs were verified by the build.
- Review patch contains no added trailing whitespace.

Interactive desktop/input tests, authenticated live-provider requests, and execution of the
production installer were not exercised. The publishing configurations were restored and analyzed;
this review did not produce or install a release package.

## Local artifacts

- [Original source snapshot](../artifacts/review-20260925/before.zip)
- [Complete patch](../artifacts/review-20260925/changes.patch)
- [Build log](../artifacts/review-20260925/final-build.log)
- [Full regression log](../artifacts/review-20260925/final-tests.log)
- [Tooling log](../artifacts/review-20260925/tooling-after.log)

The same artifact directory contains failure reproductions, restore/audit logs, analyzer results,
and before/after reference and duplication scans.
