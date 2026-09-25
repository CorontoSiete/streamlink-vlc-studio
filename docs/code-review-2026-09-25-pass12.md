# Repository review — September 25, 2026, pass 12

Preserved the existing working-tree changes. This pass combined repository-wide
builds, analyzer and formatting checks, reference and duplication scans, and
targeted inspection of settings, provider parsing, chat/replay, asynchronous
lifetimes, subprocesses, UI collections, notifications, maintenance, and tooling.
Generated outputs and third-party binaries were excluded from refactoring.

## Fixes

- Notification tags no longer collapse Kick names such as `channel.name`,
  `channel-name`, and `channel_name` into the same identifier. Long names retain
  their full identity, and casing or surrounding whitespace does not create a
  second identity for the same channel. The tag hashes the canonical platform
  and channel into 16 hexadecimal characters. This also respects the tag limit
  on older supported Windows builds. See Microsoft's
  [ToastNotification.Tag documentation](https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotification.tag).
- The isolated test runner now requires exactly one result for the requested
  test. A successful process exit with no result, a result for a similar name,
  or conflicting/duplicate results can no longer produce a false pass or skip.
- Fixed CI's headless skip ceiling from 239 to 240. The baseline comparison
  identified the existing Kick VOD hover-window test as the additional expected
  skip; no runnable test was disabled.

The three notification regressions failed before the fix. The two runner
regressions exposed an accepted missing result and a conflicting result that was
incorrectly reported as a skip. All now pass.

## Reuse and removal

Followed, Browse, and VOD card refreshes share the existing reconciliation logic.
Browse pagination now creates cards only for new identities, preserving existing
cards, commands, metadata, and ordering. Removed the duplicate append routine and
the separate live-card reconciliation implementation.

Thumbnail storage uses the existing atomic-file writer, removing its duplicate
temporary-file cleanup code. Notification tags and thumbnail filenames share a
hash helper; existing thumbnail filenames are preserved. Removed the redundant
HTTP-client factory wrapper and obsolete tag-length constant.

Production code has a net reduction of **80 lines** relative to the starting tree.

## Verification

- Release solution and separate update-probe builds: zero warnings or errors,
  with warnings treated as errors.
- Full headless suite: **1,022 passed, 240 expected interactive-desktop skips**,
  no failures or timeouts, with the corrected skip ceiling enforced.
- Eight focused notification/runner tests passed. After tightening the tag length
  to support older Windows builds, rebuilt the solution and rechecked all three
  notification regressions, including the 16-character limit.
- Solution-wide formatting/analyzer verification and `git diff --check` passed.
- NuGet audit, including transitive packages, reported no known vulnerabilities
  in the restored solution dependencies.
- All **14 PowerShell tooling checks**, syntax validation for **22 PowerShell
  scripts**, JavaScript syntax, and parsing of **28 XML and 10 JSON files** passed.
- The build verified both pinned native overlay inputs.

Interactive desktop behavior, actual notification display, authenticated provider
operations, installation/removal, GitHub Actions execution, and rebuilding the
native C plugin were not exercised. This review does not establish that every
possible defect is eliminated.

Logs, regression results, the starting source snapshot, and a patch isolating this
pass are under `artifacts/logs/review-pass12/`.
