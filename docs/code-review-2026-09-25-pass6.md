# Repository review - September 25, 2026, sixth pass

Fixed HTTP timeout recovery across credential lookup, replay discovery, and chat. Several
best-effort operations rejected every `OperationCanceledException`, even when the caller's
token was still active. Since HTTP deadlines also use that exception, a timeout bypassed the
existing fallback or failure result. Caller-requested cancellation still propagates.

- Twitch archive lookup now continues to current-live DVR probing after a GraphQL timeout.
  Kick public live metadata lookup can continue to website metadata after a timeout.
- Twitch credential validation reports a failed lookup and remains retryable. Shared Kick
  credential acquisition reports a failed lookup through its existing short failure cache;
  individual waiters can still cancel independently.
- Kick user-token refresh can retain the current token after a transient timeout, with the
  existing credential snapshot check still protecting account changes. App-token acquisition
  can continue to its existing user-token fallback.
- Optional live-chat token validation, broadcaster lookup, and prediction setup preserve
  their read-only or unavailable behavior after HTTP deadlines. Twitch VOD chat returns a
  retryable failed page, and Kick VOD channel lookup can retain its known channel candidates.

Kick history's direct HTTP and curl passes now share page handling, empty-range tracking,
and result selection. Three superseded forwarding methods and unreachable empty-result
fallback construction were removed. Cursor and start-time requests share one direct-read
helper. Six Twitch callers share the public web client ID in the GraphQL transport.
Production code is **101 lines smaller** overall.

The repository-wide reference scan found no additional safely removable unused members.
Native structure fields, WPF attached-property accessors, and framework overrides remain
because their layouts and conventions require them.

Review coverage combined whole-solution compilation/analyzers, source reference and duplicate
block scans, and script/configuration parsing with manual inspection of core helpers, settings,
HTTP/process boundaries, replay and live chat, UI lifetimes, native integration, and
maintenance/installer paths. Generated output and bundled third-party binaries were excluded
from refactoring. This is not a claim that every possible defect has been eliminated.

Twelve focused regression cases cover timeout recovery and cancellation. Six recovery cases
were run against the original implementations and reproduced failures; the five original
cancellation cases already passed. A further case covers shared Kick acquisition.

Validation:

- Locked restore with transitive NuGet auditing passed; dependency versions are unchanged.
- All 14 PowerShell tooling tests passed; all 20 PowerShell files parsed.
- All 27 source/build XML files, ten JSON files, and the JavaScript file passed syntax checks.
- Release solution build with warnings treated as errors: zero warnings and errors; both
  pinned native overlay inputs verified.
- Whole-solution formatting/analyzer verification passed.
- Complete headless suite: **949 passed, 238 expected desktop-only skips, zero failures or
  timeouts**. The skip ceiling was enforced. All twelve new regressions passed, as did both
  packaging checks that failed in the original SDK environment.

The baseline had 935 passing tests, 238 expected desktop skips, and two packaging failures:
child processes selected the system SDK, which lacks the pinned .NET 10 SDK. Validation uses
the already installed `C:\Users\ComputerGuy\.dotnet` SDK, with that directory on `PATH` and
`DOTNET_ROOT` set for child processes. The repository's SDK pin was retained.

Interactive desktop tests, authenticated provider calls, actual installation/removal, and
rebuilding the native C plugin were not exercised.

There is no Git metadata in this workspace. The original snapshot and patch preserve recovery
and review information:

- [Original source/configuration snapshot](../artifacts/review-20260925-pass6/before.zip)
- [Code and test patch](../artifacts/review-20260925-pass6/changes.patch)
- [Changed files](../artifacts/review-20260925-pass6/changed-files.json)
- [Regressions before fixes](../artifacts/review-20260925-pass6/regressions-before.log)
- [Initial focused regressions after fixes](../artifacts/review-20260925-pass6/regressions-after.log)
- [Final Release build](../artifacts/review-20260925-pass6/final-build.log)
- [Final complete headless suite](../artifacts/review-20260925-pass6/final-tests.log)
- [Whole-solution formatting/analyzers](../artifacts/review-20260925-pass6/final-format.log)
- [Locked restore/audit](../artifacts/review-20260925-pass6/restore-audit.log)
- [PowerShell tooling tests](../artifacts/review-20260925-pass6/tooling.log)
