# Browse stream workflow and resource usage

Browse now uses the same request-sharing and card-reuse behavior as the other
Home workflows, with explicit handling for failed refreshes and pagination.

- Refresh during a category's initial stream load awaits that request. Refresh
  after completion still makes a new request.
- Refresh keeps the current cards visible and usable. Successful responses reuse
  surviving cards and commands, update metadata and previews, and reconcile
  additions, removals, and ordering. Channel identities ignore case and include
  the platform. A successful empty response removes the previous cards.
- An unavailable response or exception preserves the previous cards and cursor,
  while displaying the provider error. Failed Load More requests can retry the
  same cursor; only successful pages enter the completed-cursor history.
- Refresh during Load More cancels that page and starts from the first page.
  Category/platform changes clear old results immediately. Generation checks
  reject late responses, including providers that ignore cancellation.
- Retained category commands do no work after shutdown.

The card reconciliation code is shared with Followed. Both use a common preview
version counter so refreshed images receive distinct cache requests.

## Evidence

The controlled overlapping-load fixture made two provider requests before the
change and one afterward. Five unchanged refreshes now preserve both cards and
their commands with zero collection notifications, while preview versions advance
on each successful response. Separate checks cover changed metadata, case and
platform identity, duplicate rows, ordering, removals, and opening the updated
stream target.

Seven of the twelve new regression checks failed before the application changes;
all twelve pass afterward without a visible desktop. These checks demonstrate
avoided requests and card/collection churn, not a measured reduction in total
playback CPU or resident memory.

## Validation

- Release solution build with warnings treated as errors: zero warnings/errors.
- All 12 new regression checks pass with no skips.
- Complete headless suite: 778 passed, 208 desktop-only skips, zero failures or
  runner timeouts. The existing 208-skip ceiling was enforced.
- Full formatting verification for the changed C# files and changed-line
  whitespace checks passed.
- Locked solution/runtime restores with transitive vulnerability auditing and
  the self-contained win-x64 single-file publish passed. Native overlay inputs
  were verified during the build.

The first full run had one failure in the existing replay-chat test's one-second
message wait. That check passed in isolation and the full rerun passed, without
changing replay code or weakening its test. Interactive desktop behavior and
authenticated live-provider requests were not exercised.

## Reproduction

Use the SDK pinned in `global.json`, build the solution in Release, then run:

```powershell
$env:SVS_TEST_FILTER = 'browse stream resources'
dotnet tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll
```

Clear `SVS_TEST_FILTER` before running the complete suite. Original edited files
are retained in `.tmp/browse-workflow-before/`; logs and the source diff use the
`.tmp/browse-workflow-` prefix.

The runnable build is `artifacts/browse-workflow/StreamStudio.exe`.
