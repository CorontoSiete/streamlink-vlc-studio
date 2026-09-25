# Repository review - September 24, 2026

This pass preserves the working tree's existing changes. It combined solution-wide compiler
and code-style analysis, C#/PowerShell reference and duplication scans, browser and release-tooling
checks, and manual review of runtime boundaries in Core, Infrastructure, WPF, Bootstrapper, and
Maintenance. Generated output, image assets, and bundled third-party executables were excluded
from refactoring.

## Changes

| Area | Result |
| --- | --- |
| Kick credential cache | Requests resolve against a captured credential set. An old response cannot be cached under credentials edited during acquisition or overwrite those edits. Each surviving shared waiter receives rotated refresh credentials; canceled waiters can recover them from the cache on their next request. Cached results do not retain client-secret copies. |
| Direct Kick refresh | Already-canceled requests stop before returning a token. Refresh requests use captured credentials and discard their results when the selected account changes. The cache and direct refresh share `KickCredentialSnapshot`. |
| WPF credential updates | Queued token callbacks check cancellation, timeout, and credential changes before applying results. Background chat-settings changes dispatch collection access and command notifications to the UI. |
| Shutdown | Tab startup and the updater share `AsyncOperationGate`. Disposal stops admission, lets active leases finish, and retains the semaphore until outstanding users release it. Cleanup no longer replaces an upstream error with `ObjectDisposedException`. |
| Optional catalogs | Unsupported response charsets return the normal unavailable result instead of escaping from best-effort badge/emote loading. |
| Twitch request reuse | Thirteen request-construction sites across nine components use `TwitchApiRequest` for consistent token and client-ID headers. Redundant local request factories and unused imports were removed. |
| Update results | The update helper uses the existing atomic file writer, including unique temporary files and failure cleanup. |
| Browser routes | Channel-prefix stripping cannot turn `@.` or `@..` into a captured Kick channel. These invalid routes now agree with the desktop parser. |
| Dead code and whitespace | Removed the unreferenced throwing OAuth query-parser wrapper; the validated `TryParseQueryString` path remains in use. Removed trailing whitespace from the main-window XAML. |

## Regression coverage

`RepositoryReviewTestCatalog` adds 14 tests for credential isolation, shared/canceled refreshes,
direct refresh, delayed UI work, dispatcher use, active/queued startup disposal, updater disposal,
and optional catalog decoding. Twelve of these tests were run before their corresponding fixes
and reproduced failures; the other two extend coverage for timeouts and captured direct refreshes.
The new browser route test also reproduced its failure before the fix.

The baseline headless suite passed 708 tests with 207 desktop-only skips.

## Final verification

- Release solution build with warnings treated as errors: passed, zero warnings and errors.
- Locked NuGet restore with transitive vulnerability auditing and audit warnings treated as errors: passed.
- Full headless suite through `dotnet test --no-build --no-restore`: 722 passed, 207 skipped,
  no failures or timeouts. The existing maximum of 207 skips was enforced.
- Full solution `dotnet format --verify-no-changes --no-restore`: passed.
- All 25 browser tests and syntax checks for all five JavaScript files: passed.
- Parsing for all 20 PowerShell scripts, tooling contracts, and generated browser-route verification: passed.
- Both pinned native overlay inputs: verified by the Release build.
- `git diff --check`: passed.

Interactive desktop/window/input behavior, authenticated live-provider requests, and executing
the production installer are outside the validation performed by this pass.

## Local review artifacts

- Pre-edit source snapshot: `.tmp/review-comprehensive-before.zip`.
- Changes made by this pass: `.tmp/review-comprehensive.patch`.
- Validation and reproduction logs: `.tmp/review-comprehensive-*`.

These generated artifacts are local and ignored by the existing repository policy.
