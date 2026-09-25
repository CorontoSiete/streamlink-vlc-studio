# VOD and category refresh workflow and resources

VOD searches on Twitch/Kick and Browse category refreshes keep their existing cards
usable while a fresh first page loads. A successful refresh reuses surviving cards
and commands, updates their metadata, and reconciles additions, removals, and order.
Only new identities create view models and commands. Identical metadata does not
raise property notifications. Changed thumbnails and VOD playback details still update.

Failed or unavailable refreshes keep the previous results and pagination while showing
the error. Failed Load More requests do not consume a cursor and can retry that page.
Successful empty refreshes remove stale cards. Query, platform, and VOD filter changes
still clear the old results; cancellation and generation checks reject late responses.
Refreshing during Load More returns to the first page and replaces that request.

Overlapping pages retain existing cards and metadata, including category viewer counts
loaded separately. Explicit category refreshes still retrieve fresh counts. Kick category
refreshes deduplicate and sort incoming results before reconciling the list, avoiding
temporary moves through the provider's unsorted order. Selecting a retained category
during refresh cancels the refresh and opens that category normally.

## Evidence

Twenty of the initial 26 regression cases failed before the application changes.
All 28 final focused cases pass, including additional coverage for overlapping category
counts and Kick sorting with duplicate IDs and missing names.

The unchanged-refresh fixture keeps two cards and their command instances across five
refreshes with zero collection or card-property notifications on each of the Twitch VOD,
Kick VOD, and Twitch category surfaces. The Kick category fixture also records zero
collection notifications when the provider sends the same unsorted list again.

The overlap fixture makes one viewer-count request for the first category and one for
the newly appended category, preserving the count already loaded for the overlapping
row. Separate checks ensure explicit refresh retrieves new counts and updates retained
cards. Metadata checks also open the retained commands to verify current VOD duration/title
and category name are used.

These checks establish card/command reuse, avoided binding and collection work, and
request behavior. They do not measure a percentage change in total playback CPU or memory.

## Validation

- Release solution build with warnings treated as errors: zero warnings and errors.
- All 28 focused regressions passed without skips.
- Final full headless suite: 840 passed, 236 desktop-only skips, no failures or runner
  timeouts. The existing skip ceiling of 236 was enforced.
- Formatting/analyzer verification for all changed C# files passed. Added lines have
  no trailing whitespace.
- Self-contained win-x64 single-file publish passed with warnings treated as errors;
  pinned native-overlay inputs were verified during the build.

An intermediate full run hit the existing Kick replay-chat test's 500 ms message wait
while formatting was running concurrently. That test passed immediately in isolation,
and the complete rerun without concurrent formatting passed. Replay-chat code and
test timeouts were not changed. Interactive desktop behavior and live provider requests
were not exercised.

## Reproduction

Use the SDK pinned in `global.json`, build the solution in Release, then run:

```powershell
$env:SVS_TEST_FILTER = 'paged refresh resources'
dotnet tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll
```

The checks use controlled providers and an offscreen dispatcher; they require no platform
credentials or visible windows. Clear `SVS_TEST_FILTER` to run the complete suite.

Pre-edit source copies, the patch, and validation logs are retained in
`artifacts/workflow-resources-20260925/` because this workspace has no Git metadata.
The runnable build is `artifacts/workflow-resources-20260925/app/StreamStudio.exe`.
