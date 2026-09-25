# Recent refresh workflow and resource use

Recent now updates surviving cards in place when checking live status, receiving metadata,
recording playback, or deleting another card. Cards retain their commands and thumbnail
bindings; additions, removals and changes in watch order update only the affected rows.
Platform and channel identify each row, ignoring channel case. Rows snapshot mutable
settings values so edits still notify bindings correctly.

Metadata appears as each channel responds. A slow request no longer holds back live/offline
indicators, display names, categories or thumbnails for other channels. The existing pool
of at most four workers bounds requests and queued tasks. Changed metadata is saved once
after the refresh; status-only updates do not write settings. The settings list is replaced
as one batch so an overlapping save can safely read the previous snapshot. Deleting a row
during refresh also preserves the completed previews on surviving cards. Failures preserve stored
metadata, failed channels remain eligible for retry, and completed results cannot restore
a deleted row. New watch times, quality choices and ordering survive a pending refresh.

## Evidence

In the controlled 50-card fixture, five refreshes produced 510 collection notifications
before the change and zero afterward. The same cards and all three commands on each card
survive the five refreshes. Unchanged thumbnail URLs emit no property notifications.
This measures avoided card/command creation and collection/binding work, not a percentage
reduction in total playback CPU or resident memory.

The controlled two-channel fixture holds one response open and verifies that the other
channel's status and metadata are already visible, with no settings writes until both
finish and exactly one write afterward. Seven of the first eight regressions failed against
the original application; shutdown cancellation already passed. A ninth regression covers
deleting a row while completed previews await the settings commit. The cases also cover
failed providers, deletion, changed settings, retained commands, platform identity and
watch history changes during a request.

## Validation

- Release solution build with warnings treated as errors: zero warnings and errors.
- All nine focused regressions passed without skips.
- Final complete headless suite: 849 passed, 236 expected desktop-only skips, zero
  failures or timeouts. The existing 236-skip ceiling was enforced.
- Formatting completed; final whitespace verification for all changed C# files passed.
- Locked win-x64 runtime restore with transitive NuGet auditing and self-contained,
  single-file publish with warnings treated as errors passed. The build also verified
  the pinned native overlay inputs.

Interactive desktop behavior and authenticated live-provider requests were not exercised.
The executable is `artifacts/recent-workflow-resources-20260925/app/StreamStudio.exe`.

## Reproduction

Use the SDK pinned by `global.json`, build in Release, then run:

```powershell
$env:SVS_TEST_FILTER = 'recent refresh resources'
dotnet tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll
```

The checks use controlled providers and an offscreen dispatcher, without account credentials
or visible windows. Clear `SVS_TEST_FILTER` before running the complete suite.

Pre-edit files, the patch and validation logs are retained in
`artifacts/recent-workflow-resources-20260925/`, because this workspace has no Git metadata.
