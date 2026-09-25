# Home refresh workflow and resource usage

This follow-up extends the earlier Home workflow improvements to Followed cards,
Twitch/Kick VOD search, and Browse categories.

## Behavior

- Successful Followed refreshes reuse each surviving channel's card and commands.
  Only added, removed, or reordered channels change the collection. Metadata,
  elapsed live time, and versioned thumbnail requests still update. Channel
  identity includes the platform and ignores case, so matching names on Twitch
  and Kick remain separate. Open commands read the card's latest target metadata.
- Enter during an automatic VOD search and Refresh during a category load await
  the matching request already in progress. A completed or failed request can
  still be refreshed. Query, platform, or VOD filter changes replace the request;
  generation checks reject late responses from canceled requests.
- Refresh during pagination cancels that page and starts at the first page.
  Sharing an initial search does not accidentally share a Load More operation.
- VOD and category commands invoked after shutdown return without starting work
  or accessing disposed cancellation sources.

## Resource evidence

The controlled VOD and category fixtures previously made two service requests
when a manual action overlapped an automatic search. They now make one, without
canceling it. A later explicit refresh makes a new request.

The Followed fixture retains two cards and their command instances across five
unchanged refreshes, with zero collection notifications. Thumbnail requests
advance on every refresh. Separate checks cover reordering, removal, insertion,
metadata notifications, and opening a stream with its updated category.

These checks measure avoided requests and object/collection churn. They do not
establish a percentage reduction in total application CPU or resident memory.

## Validation

- Before the application changes, eight of the first nine regression checks
  failed against the original code. The existing VOD pagination behavior passed.
  The final coverage also checks Browse pagination and VOD platform changes.
- All 10 new regressions pass with no skips.
- Release solution build with warnings treated as errors: zero warnings/errors.
- Complete headless suite: 766 passed, 208 desktop-only skips, zero failures or
  timeouts. The existing 208-skip ceiling was enforced.
- Formatting completed for the changed C# files; whitespace checks passed.
- Locked self-contained runtime restore with transitive vulnerability auditing,
  and single-file win-x64 publish with warnings treated as errors: passed.

Live authenticated provider requests and interactive desktop behavior were not
exercised in this pass.

The runnable build is `artifacts/home-refresh-workflow/StreamStudio.exe`.
Original edited files are retained in `.tmp/workflow-followup-before/`; build,
baseline, focused-test, complete-suite, formatting, restore, and publish logs
use the `.tmp/home-refresh-` prefix.

## Reproduction

Use the SDK pinned by `global.json`, build the solution in Release, then run:

```powershell
$env:SVS_TEST_FILTER = 'home refresh resources'
dotnet tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll
```

The regressions use controlled services and an offscreen dispatcher; they need
no platform credentials or visible windows. Clear `SVS_TEST_FILTER` before
running the complete suite.
