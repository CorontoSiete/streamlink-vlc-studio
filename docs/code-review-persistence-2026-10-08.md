# Code review: persistence and native overlay boundaries

Reviewed the authored solution, native modules, scripting, UI bindings, and build/release configuration. The initial inventory contained 969 authored text files, including 541 production files and 138,073 production lines. Generated output, downloaded toolchains, and previous temporary review artifacts were excluded. A fresh snapshot separates this pass from the extensive changes already present in the working tree.

## Fixed bugs

| Trigger | Previous behavior | Corrected behavior |
| --- | --- | --- |
| Settings feedback throws while another edit awaits persistence | Autosave stops before writing the later edit; shutdown completes despite the pending change | The existing safe callback dispatcher isolates feedback failures, keeps writes serialized, and flushes the final edit before shutdown |
| Settings feedback throws after a failed write | Reporting can escape the save loop and interfere with retry state | Failed writes remain dirty and can be retried; successful writes become clean even when feedback fails |
| Large overlay dimensions are normalized to reference pixels | Integer multiplication or conversion overflows and produces minimum-sized panels | Scaling uses floating-point multiplication, clamps the result, and then converts to an integer |
| A VLC cache manifest has a null or missing plugin inventory | A null-reference exception disables native overlay preparation | Malformed inventory invalidates the cache so preparation can rebuild it or let VLC scan the plugins |
| A diagnostic logger throws during overlay preparation | Logging interrupts successful preparation or escapes fallback handling | All logging in overlay extraction and preparation uses the existing safe logging extension |

Four separate regression probes reproduced autosave data loss, incorrect dimensions, null-inventory failure, and diagnostic failure against the baseline binaries. All four pass after the fixes.

## Reuse and cleanup

- Added one shared `BoundedFile` implementation, linked into Infrastructure and Maintenance. It checks length and reads through the same handle, permits atomic replacement, and preserves text BOM detection.
- Applied bounded reads to VLC manifests (1 MiB), overlay position/size state (4 KiB), installation ownership state (the existing 16 MiB limit), and maintenance handoff tokens (128 bytes).
- Moved overlay position reading into `NativeOverlaySizing`, alongside size reading, and removed the duplicate private window reader. Invalid, missing, and oversized state files return false. Removed Maintenance's obsolete size-check/read helper.
- Replaced the hand-written VLC cache manifest writer with `AtomicFile.WriteAsync`; removed its redundant temporary-file cleanup and unused text-encoding import.
- Reused streaming SHA-256 comparison for bundled VLC core and replay plugins. Existing replay cache files no longer require whole-file allocations for hash verification.
- Reused best-effort temporary-file cleanup during bundled native extraction so cleanup failures cannot replace the extraction result.

The final reference/duplicate scan covered 651 C# files and introduced no unused-member candidates. Remaining candidates are native layout fields, framework overrides, WPF binding hooks, or script entry points. The small `Action`/`Func<T>` native-work overloads require different typed task results and remain. Cross-language scans found no additional removable Python imports, PowerShell functions, or native static functions.

## Validation

- Baseline `scripts/dev.ps1 Check`: 1,840 tests passed, 274 skipped.
- Final `scripts/dev.ps1 Check`: 1,845 tests passed, 274 skipped; formatting, tooling contracts, native provenance, and the Release build passed with zero warnings or errors.
- After the final invalid-path handling and import cleanup: formatting verification and a zero-warning Release rebuild passed; all 11 focused autosave, overlay, and cache-manifest tests passed with no skips.
- Regression probes: 4 passed, 0 failed.
- Added five tests for autosave feedback, overlay preparation, sizing boundaries, and bounded overlay state; expanded the existing cache-manifest test for null, missing, malformed, and oversized inventory data and atomic writer cleanup.
- Native overlay, subpicture/pipe receipt, compositor, and hardware GDI suites passed with GCC warnings treated as errors; handle-lifetime checks passed.
- Python unit suite: 8 passed.
- PowerShell 7 release-publication and signed-update-manifest fixtures passed using temporary fixtures.
- JSON/XML/Python parsing and PowerShell syntax checks passed; `git diff --check` passed.
- Roslyn workspace audit: no workspace failures or compilation errors. Removed a redundant test import already provided by global usings. The two apparent unused imports in `WindowsDependencyProbe.cs` are required when PowerShell compiles that file without SDK implicit usings.

Validation logs and the diff against this pass's snapshot are in `.tmp/review-boundaries-20261008/`. Desktop-dependent tests remain subject to the standard headless suite's skip policy.
