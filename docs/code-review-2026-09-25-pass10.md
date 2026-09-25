# Repository review — September 25, 2026, pass 10

Existing working-tree changes were preserved. This pass used repository-wide builds,
format/analyzer checks, reference and duplication scans, configuration/script validation,
and targeted manual inspection of chat, replay, settings, provider transports, UI operation
lifetimes, native integration, and release/maintenance boundaries. Generated files and
third-party binaries were excluded from refactoring.

## Fixes

- **Replay chat after eviction:** the timeline now records the latest offset removed by its
  40,000-message limit. Seeking into an evicted range starts a fresh fetch session instead
  of trusting stale coverage. Restarting also makes room for older messages and prevents
  retired requests from repopulating the replacement cache. Seeking within retained history
  still reuses it. Streams that only support live capture preserve their surviving messages.
- **Duplicate Kick startup history:** overlapping pages now contribute each message once.
  A page with no new messages stops pagination even if its cursor changes, avoiding repeated
  requests for the same history. The newest-message limit and chronological delivery remain.
- **Reusable message identity:** startup history and Kick VOD history use the same identity
  function, including a culture-independent fallback for messages without an ID.

The eviction regression failed before the fix because no replacement fetch occurred.
Two Kick regressions also failed before the fix: overlapping pages delivered 25 entries
instead of 20 unique messages, and repeated pages delivered the same message 25 times.
All three now pass. Additional tests cover retained-cache reuse, capture-only history,
and cancellation of an active startup-history request during disposal.

## Dead code removal

The application fetches replay chat through `IVodChatProvider`; source-reference checks found
no application callers of `IChatHistoryBackfillClient`. Removed that obsolete interface,
its result type, the old `KickChatHistoryBackfillService`, range-fetching client methods,
unused state/cancellation plumbing, and the unused `start_time` transport option.
Live startup history and the current VOD fetcher continue using `KickChatTransport`.
Also removed the unused timeline `LastOffset` property.

Removed twelve tests specific to the deleted API and its obsolete fake-client scaffolding;
added six regression/lifecycle tests for active behavior. The characterized-catalog count
was reduced by twelve; the interactive skip ceiling remains unchanged. Application source
has a net reduction of **720 lines** relative to this pass's initial snapshot.

## Verification

- Full headless suite: **997 passed, 239 expected interactive-desktop skips**, no failures
  or timeouts, with the skip ceiling enforced.
- Release solution and separate update-probe builds: **zero warnings or errors**, with
  warnings treated as errors. Both pinned native overlay inputs passed verification.
- Full solution format/analyzer verification and `git diff --check` passed.
- Locked dependency restore with transitive NuGet auditing passed; dependency pins are unchanged.
- All **14 PowerShell tooling checks** passed. Syntax validation passed for **22 PowerShell
  scripts, 28 XML files, and 10 JSON files**.

The initial test run had two packaging failures because child processes selected the older
system SDK. The final run used the already-installed pinned SDK on `PATH` and passed both.

Validation logs, the source snapshot, regression results before/after the fixes, and a patch
isolating this pass from pre-existing changes are under `artifacts/logs/review-pass10/`.

Interactive desktop tests, authenticated provider operations, actual installation/removal,
GitHub Actions execution, and rebuilding the native C plugin were not exercised. These
checks do not establish that every possible defect has been eliminated.
