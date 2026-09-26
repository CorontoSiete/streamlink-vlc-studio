# Browsing and playback workflow

Opening a stream that already has a tab now checks the tab before fetching live
metadata. Returning from Home does not depend on that provider request finishing.
The second tab lookup after metadata loading still prevents overlapping opens
from creating duplicate tabs or starting playback twice.

Reopening a manually paused stream or VOD preserves the player and its position.
Background opens also leave manual pause and Home intact. Tabs paused by the
inactive-tab policy still resume through that policy when selected. Stopped tabs
remain eligible to start again.

Search cleanup belongs to the search that initiated the open. The app captures
the search generation before metadata loading and checks it after playback
succeeds. Typing another query, including editing away and back to the original
text, prevents the older startup from clearing that newer search. An unchanged
search clears on success, and failed starts retain it for retry. Reopening an
already open tab also clears the initiating search; reopening a stopped tab
clears it when startup succeeds. Card opens and reloads do not clear search text.

## Regression coverage

`StreamOpenWorkflowTestCatalog` uses controlled metadata and playback providers
on an offscreen WPF dispatcher. It covers metadata waits, case-insensitive tab
reuse, manual live/VOD pause, background opening, automatic pause recovery,
overlapping metadata completions, delayed startup, edited search generations,
successful cleanup, stopped-tab retry, and failed startup.

Nine of the initial eleven checks failed against the pre-edit application and
all eleven passed after the changes. Two additional boundary checks cover
automatic pause recovery and overlapping metadata completions.

## Validation

- Final Release solution build with warnings treated as errors: zero warnings
  and errors.
- All 13 focused workflow regressions passed without skips.
- Final complete suite: 1,160 passed, 246 desktop-only skips, zero failures or
  runner timeouts. The observed 246-skip ceiling was enforced locally; the new
  workflow tests introduce no skips.
- Formatting/analyzer verification passed for the changed workflow files;
  whitespace verification also passed for the updated dispatch-guard test.

The dispatch-guard regression now invokes the public Play command and still
asserts that a rejected dispatch releases its startup guard. The first full run
also encountered a replay-chat assertion failure; that check passed in isolation
and in the final complete run without changing replay-chat code or its tests.
Interactive desktop behavior and live platform requests were not exercised.

## Reproduction

Run with the SDK pinned in `global.json`:

```powershell
dotnet build StreamlinkVlcStudio.sln --configuration Release --no-restore -warnaserror
$env:SVS_SKIP_INTERACTIVE_WINDOW_TESTS = 'true'
$env:SVS_TEST_FILTER = 'stream open workflow:'
dotnet tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll
```

Clear `SVS_TEST_FILTER` before running the complete suite. These checks require
no platform credentials or visible windows. Logs use the
`.tmp/stream-open-workflow-` prefix; copies of files changed in this pass are in
`.tmp/stream-open-workflow-before/`.
