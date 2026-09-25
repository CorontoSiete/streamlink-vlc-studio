# Repository review — September 25, 2026, pass 9

Existing working-tree changes were preserved. This pass combined repository-wide builds,
analyzer/format verification, reference and duplication scans, and script/configuration
validation with targeted inspection of HTTP/process boundaries, caches, settings, chat,
replay, UI lifetimes, native integration, and update/maintenance code. Generated output
and third-party binaries were excluded from refactoring.

## Fixes and cleanup

- **Chat drafts:** completing a send or credential retry no longer clears text edited
  while the request was pending. A revision counter also preserves a draft cleared and
  retyped with identical text. Unchanged drafts still clear after successful sends.
- **Chat startup:** sending waits for the current connection operation even after the
  client has been published. Previously, sending during a slow initial connection could
  report “not connected” or unnecessarily retry authentication.
- **Chat lifetime:** sends and credential retries receive the tab's cancellation token.
  Replay/disposal state is rechecked after connection waits, before retries, and before
  applying results. A late response cannot append a local echo to a replaced client or
  overwrite a closed tab's draft. Both send paths share one local-echo and completion routine.
- **VOD quality:** explicit supported rendition names, including `480p30`, `360p30`, and
  `160p30`, select their requested quality instead of falling back to the highest rendition.
  Existing display aliases and nearest-quality fallback behavior are preserved.
- **HTTP parsing:** fragmented request headers are searched incrementally, retaining the
  three-byte overlap needed for delimiters split across reads. Removed the obsolete manual
  scanning loop. A local Release benchmark with a 32 KiB header delivered one byte at a time
  improved from **361.294 ms to 0.882 ms** (median of three measured runs after warmup).
  This is a parser microbenchmark, not an end-to-end playback measurement.
- **Reuse:** provider search and UI probe summaries now use the same formatter. Removed
  duplicated formatting branches and an unused namespace import. Remaining single-reference
  scan candidates are required WPF/framework entry points and native structure fields.

## Verification

- Full headless suite: **1,003 passed, 239 expected interactive-desktop skips, zero failures
  or timeouts**, with the skip ceiling enforced.
- Added ten chat lifecycle cases. Eight of the first nine failed against the prior behavior;
  the final case additionally checks switching to replay during credential reconnection.
- Expanded quality-selection coverage reproduced `480p30` incorrectly selecting `chunked`
  before the fix. Expanded HTTP coverage checks fragmentation, split delimiters, request-size
  boundaries, and incomplete headers.
- Release solution and separate update-probe builds: zero warnings or errors, with warnings
  treated as errors. Both pinned native overlay inputs passed build verification.
- Locked dependency restore with transitive NuGet auditing passed; dependencies are unchanged.
- Whole-solution format/analyzer verification and `git diff --check` passed.
- All 14 PowerShell tooling checks passed. Syntax checks covered 22 PowerShell scripts,
  29 XML files, and seven JSON files, excluding package-lock JSON from this syntax scan.

Validation logs, the initial source snapshot, this pass's patch, and the parser benchmark
are in `artifacts/logs/review-pass9/`.

Interactive desktop tests, authenticated provider operations, real installation/removal,
GitHub Actions execution, and rebuilding the native C plugin were not exercised. This
review does not establish that every possible defect has been eliminated.
