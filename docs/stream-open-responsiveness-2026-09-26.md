# Immediate stream opening

New live-stream tabs no longer wait for the category/profile metadata request.
The tab is created and selected when opened, and playback starts independently.
Home navigation, subsequent tab selection, and tab order remain under the user's
control while metadata loads. Opening the same stream again reuses the tab and
its in-flight lookup.

Opening metadata fills missing tab details without changing a custom title or
overwriting a newer live category poll, including a poll that clears the category.
Closing the tab cancels the lookup. A provider that completes despite cancellation
cannot update the closed tab; its cancellation source remains alive until the
request completes. Application shutdown also cancels and drains these operations.

Successful playback adds the initial Recent entry without waiting for metadata.
That entry reuses the opening lookup when it finishes. Enrichment preserves watch
order, timestamps, and quality and does not restore deleted entries. Metadata
failure leaves playback usable.

## Verification

The five initial regression cases failed against the previous implementation.
The final focused catalog has 24 passing checks with no skips: immediate playback,
foreground/background navigation, selection and quality, close/cancellation,
provider failure, current/cleared categories, Recent ordering/deletion, duplicate
opens, search ownership, manual pause, automatic pause, and stopped-tab retry.

The Release solution build passed with warnings treated as errors (zero warnings
or errors). Whitespace verification passed for all four changed C# files.
The complete suite reported 1,177 passes, 246 desktop-only skips, no timeouts, and
two packaging failures caused by child scripts finding the machine's older SDK.
Both packaging checks passed on targeted reruns with the pinned SDK directory
first on the process PATH, yielding 1,179 passing checks across validation. No
product changes were needed for those environment failures. The 246-skip ceiling
was enforced; the workflow tests introduced no skips.

These tests use controlled providers and an offscreen WPF dispatcher; they do not
measure live platform startup latency or exercise the visible desktop UI.

Run the focused checks using the SDK selected by `global.json`:

```powershell
dotnet build StreamlinkVlcStudio.sln --configuration Release --no-restore -warnaserror
$env:SVS_SKIP_INTERACTIVE_WINDOW_TESTS = 'true'
$env:SVS_TEST_FILTER = 'stream open workflow:'
dotnet tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll
```

Local logs are in `.tmp/workflow-immediate-*.log`. Copies of the files as they
were before this pass are in `.tmp/workflow-immediate-before/`.
