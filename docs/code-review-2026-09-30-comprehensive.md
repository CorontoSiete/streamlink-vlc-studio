# Code review — September 30, 2026

This review preserved the existing working-tree changes. Automated analysis covered all 375 production C# files. Manual review focused on HTTP and cancellation handling, download/replay lifetimes, settings and persistence, process/update/install paths, WPF coordination, and native overlay sources. Scripts, project files, XAML, contracts, and native dependencies were also checked.

The changes fix these defects:

- Playlists beginning with a UTF-8 BOM could lose their published duration or fail preview/variant parsing. Eight playlist readers now share header normalization through `HlsPlaylistPolicy.SplitLines`. Only the header's BOM is removed.
- Muted-segment repair mislabeled independent source cancellation as its own idle timeout. It now preserves the original cancellation token and only converts expiration of its own idle deadline into `TimeoutException`.
- VOD downloads did not retry `TimeoutException`, including repair timeouts. These failures now use the existing bounded retry policy, while caller cancellation still stops the download.
- The repair proxy used a single deadline for an entire media response, cutting off transfers that were still progressing. It now reuses the shared HTTP reader's per-read budget and logs independent read cancellations with their original cause.

Three repeated stream-settings normalizers were replaced with one generic helper. This preserves key trimming, case-insensitive lookup, value clamping, and rejection of non-finite font sizes. Redundant normalization loops and BOM workarounds were removed. Tests reuse the existing download fixtures and paced/stalled streams; unnecessary stream boilerplate was removed from the cancellation fixture.

The unused-member audit retained WPF callbacks, interface/COM methods, and native structure fields required by framework dispatch or ABI layout. These are not safe dead-code deletions.

Validation results:

- Baseline full check: **1,439 passed, 263 skipped**.
- Five initial regressions failed before their fixes. Two additional proxy regressions failed before the proxy fix. The final focused run passed **all seven**.
- Final full check: **1,446 passed, 263 interactive desktop tests skipped**. The Release build completed with **zero warnings and zero errors**.
- Compiler import audit across all compilation contexts: **no workspace failures, compilation errors, or unused imports**.
- Native overlay controller/plugin and replay-pause plugin rebuilt with `-Wall -Wextra -Werror` into separate temporary outputs.
- Seven native test programs passed: TLS handshake, network framing, readability, render resources, compositor, subpictures, and received frames.
- Release-publication tests passed using their mock GitHub CLI. Installer/development, release-contract, and native-dependency checks passed in the full managed check.
- Syntax validation passed for five Python scripts, one JavaScript file, 29 XML/XAML files, and 11 JSON configurations/contracts. The production C# syntax audit found no errors; `git diff --check` found no whitespace errors.

Interactive desktop and hardware smoke checks were not run. Test logs, source audits, the baseline snapshot, and a diff against that snapshot are retained in `.tmp/review-comprehensive-20260930/`. The final managed command is `scripts/dev.ps1 Check -NoRestore`, using the repository's pinned .NET SDK 10.0.302; locked restore succeeded earlier in this review.
