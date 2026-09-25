# Browse category scroll restoration

The category grid and live-stream list share `HomeContentScrollViewer`. Previously,
a category button's Click handler scheduled a scroll to the top, but returning to
categories kept the stream list's scroll offset. Navigation through commands also
bypassed that Click handler.

`MainWindow.BrowseScroll.cs` now observes Browse page transitions, captures the
category offset before layout changes, and restores it after the category grid is
laid out, before its first rendered frame. New stream pages start at the top.
Back, Browse, the selected platform, and the return-to-categories command use
the same behavior.

Pending scroll operations are canceled when another page is selected or the
window closes. A replacement category list resets its saved position; pagination
and viewer-count updates keep it. Automatic pagination waits for restoration so
the inherited stream offset cannot trigger an unwanted category request.

## Regression evidence

- Before the fix, all 16 return-navigation cases failed in actual WPF windows:
  the category offset was 4,822 instead of the saved 0 or 360.
- After the fix, all 28 new window tests pass with no skips. They cover both
  providers, four return routes, repeated visits, delayed and canceled stream
  loads, rapid navigation, category pagination, platform/search changes, and
  resizing that requires clamping the saved offset.
- The existing category-opening and responsive Browse layout tests also pass.
- The complete headless suite passes: 783 passed, 236 configured desktop skips,
  zero failures or timeouts. An initial run's two packaging failures were caused
  by child processes finding the older system SDK; the complete rerun passed
  after putting the pinned SDK first on PATH.
- Release solution build and self-contained win-x64 publish pass with warnings
  treated as errors. Formatting verification for changed C# files and PowerShell
  parser checks pass.

The new interactive tests are registered separately from the characterized test
catalog. Headless CI's skip allowance increases by exactly those 28 window tests;
the interactive desktop workflow continues to allow zero skips.

## Restoration timing

The restoration now runs at the dispatcher's DataBind priority, after navigation
notifications and ahead of rendering. Waiting until ContextIdle previously let
the categories page render at the inherited stream offset before correcting it.
The layout checks and pagination suppression remain in place.

The 16 return-navigation cases now observe `CompositionTarget.Rendering` and
require the first category frame to have the saved offset. All 16 failed with
the idle scheduling and pass with the earlier scheduling. All 28 scroll tests,
the existing category-opening test, and the responsive Browse layout test pass.
The Release solution build and changed-file formatting verification also pass.
These timing checks assert frame ordering, without a machine-dependent latency
threshold. Timing follow-up logs use `.tmp/browse-scroll-speed-`.

To run the new tests with the SDK pinned in `global.json`:

```powershell
dotnet build StreamlinkVlcStudio.sln -c Release --no-restore -warnaserror
$env:SVS_TEST_FILTER = 'browse scroll'
$env:SVS_EXPECTED_MAX_SKIPS = '0'
dotnet tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll
```

Local validation logs use `.tmp/browse-scroll-`. The runnable build is
`artifacts/browse-scroll/StreamStudio.exe`.
