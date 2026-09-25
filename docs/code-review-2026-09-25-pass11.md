# Repository review — September 25, 2026, pass 11

Preserved the pre-existing working-tree changes. This pass combined solution-wide
builds, formatting/analyzer verification, source-reference and duplicate-code scans,
and focused inspection of parsing, HTTP limits, asynchronous lifetimes, settings,
chat/replay, subprocess cleanup, WPF coordination, native integration, and installer
helpers. Generated outputs and third-party binaries were excluded from refactoring.

## Fixes

- **Kick history after reconnect:** startup history now owns its paging state locally.
  A canceled request that ignores cancellation can no longer hold a shared semaphore
  and block the replacement connection's history. Disconnect still waits briefly for
  the old task, then permits reconnect; late results cannot modify replacement paging
  state or emit canceled history.
- **Cancellation during history delivery:** delivery rechecks cancellation before each
  message, so closing or replacing the connection during a batch stops the remaining
  callbacks.
- **Repeated Kick VOD pages:** pagination stops when a nonempty page adds no new
  messages, even if the provider changes its cursor. It preserves partial messages
  and leaves the missing time range retryable instead of spending the full 20-request
  budget on the same page. Overlapping pages that contain new messages still progress.

All three regression cases failed before the fixes. The reconnect case timed out,
the delivery case continued after cancellation, and the repeated-page case issued
20 requests instead of two. All now pass, alongside an additional overlap regression.
The existing page-budget test now uses distinct messages so it exercises the budget
independently of repeated-page detection.

## Reuse and removal

Live startup history and VOD history share `KickRecentChatPage.ReadNewMessages`.
Removed the obsolete persistent startup-history fields, semaphore, reset routine,
transport wrappers, and associated reflection-only test scaffolding. Installer
ownership and native dependency verification now share `Get-FileSha256` in
`scripts/lib/common.ps1`; the two duplicate implementations were removed.

Production code has a net reduction of **98 lines** relative to the starting tree.

## Verification

- Release solution and separate update-probe builds with warnings treated as errors:
  zero warnings or errors.
- Full headless suite: **1,001 passed, 239 expected interactive-desktop skips**;
  no failures or timeouts, with the existing skip ceiling enforced.
- All **18 focused tests** passed, including the regression cases.
- Solution-wide formatting/analyzer verification and `git diff --check` passed.
- All **14 tooling checks** passed under both Windows PowerShell 5.1 and PowerShell 7.
- All **22 PowerShell scripts** passed Windows PowerShell syntax validation;
  source XML and JSON configuration parsing passed.
- The Release build verified both pinned native overlay inputs.

Logs, the starting source snapshot, regression results before/after the fixes, and
a patch isolating this pass from earlier work are in `artifacts/logs/review-pass11/`.

Interactive desktop tests, authenticated provider operations, actual installation
or removal, GitHub Actions execution, and rebuilding the native C plugin were not
exercised. This review does not establish that every possible defect is eliminated.
