# Repository code review - 2026-09-30

This review covered repository-owned C#, XAML, native C/C++, PowerShell, Python,
and workflow code through static checks, builds, tests, and focused source review
of resource ownership, cancellation, HTTP boundaries, download integrity, and
native loading. Existing working changes were the baseline and were preserved.
A source/configuration snapshot is available in `.tmp/review-20260930-baseline`.

The fixes are:

- Offline downloads reject HLS delta playlists containing `#EXT-X-SKIP`.
  Previously, missing earlier segments could produce an incomplete video marked
  as completed.
- Kick VOD channel discovery validates all decoded Next.js Flight records before
  accepting a channel ID. Later conflicting metadata and truncated records can
  no longer be hidden by an early successful record.
- Encryption keys whose source paths end in `-muted.ts` receive ordinary `.key`
  asset names. These downloads now reopen successfully and remain completed
  after restarting the download service.
- Managed Kick HTML requests use the same 4 MiB limit as HTML normalization and
  the curl fallback. JSON retains its 2 MiB limit.
- Replay URL validation checks cancellation before doing work and after DNS
  resolution, including resolvers that return successfully after cancellation.
- VLC loading finishes hash reads and other fallible initialization before
  publishing process-wide handles. A failed read cannot leave freed handles or
  partially configured native state behind.
- Download shutdown drains remaining queued job generations after the worker
  exits, releasing cancellation sources even if shutdown precedes its first read.
- HTTP deadline setup releases its linked cancellation source if an invalid
  timeout causes timer initialization to fail.
- Workflow PowerShell error messages correctly interpolate the failing script's
  filename.

`OfflineHlsAssetName` now owns asset naming and validation, removing duplicated
extension rules and the validation allocation. `FileHash` replaces three separate
SHA-256 file helpers. Unused VLC diagnostic properties and their backing fields
were removed. Framework callbacks, COM interface slots, and native structure
fields required for binary layouts were retained.

Validation completed:

- `scripts/dev.ps1 Check`: formatting, PowerShell syntax, development and installer
  contracts, bundled native integrity, and Release build all passed. The build
  reported zero warnings and zero errors. **1,431 application tests passed;
  263 interactive desktop tests were skipped.** The starting source passed
  1,425 tests with the same skip count.
- Six tests were added. Five reproduced failures against the starting source;
  the sixth covers cancellation-source disposal across queued cancel/retry jobs.
  The download group passes all 61 tests, and both HTTP lifecycle tests pass.
- Seven native test executables passed across the controller, compositor,
  subpicture, and frame-receipt suites. A separate controller/plugin build in
  `.tmp/review-20260930-native-build` succeeded with warnings treated as errors.
- Release publication contract tests passed using mocked GitHub operations and
  a workspace temporary directory.
- Python scripts compiled successfully; `git diff --check` passed. The source
  audit parsed all 374 production C# files without syntax errors.

The headless run does not exercise the 263 desktop cases or the opt-in public VOD
compatibility probe. Verification logs are in `.tmp/review-20260930-*.log`.
