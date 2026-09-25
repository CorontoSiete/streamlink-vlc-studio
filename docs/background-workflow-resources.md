# Background refresh and diagnostic resource usage

Followed refreshes now share an unfinished request when the account credentials
and followed-channel input match. Pressing Refresh during startup or an automatic
refresh waits for that result. Pressing Refresh after completion still requests
fresh data, including after a failure.

Saving different followed channels or changing accounts schedules fresh work.
Only the latest queued configuration makes a service request, and obsolete
results cannot replace cards, status, or notification history. Requests remain
serialized. Automatic refresh intervals, background live notifications, card
identity, and thumbnail freshness are preserved.

The application log now has a bounded pending queue and at most one pending UI
delivery. It formats only entries that can survive the existing 250-line history
limit. Each delivery handles at most 250 entries and yields between batches, so
continuous diagnostics leave room for other UI work. Entries arriving during a
delivery are handled in order by the next batch. Failed or rejected dispatch can
retry on the next entry. Shutdown clears pending entries and prevents queued
callbacks from updating the disposed view model. File logging is unchanged.

## Resource evidence

The controlled 10,000-entry burst previously queued 10,000 UI callbacks. It now
queues one, and the final visible lines match the logger's latest 250 entries
exactly, including timestamps, severity, source, and message. Eight concurrent
producers also share one pending callback.

Overlapping startup/manual refresh previously made two service calls. It now
makes one. Two settings saves followed by Refresh during a pending request
produce just one additional service call using the latest configuration.

These measurements establish avoided requests, callbacks, and temporary history
work. They do not estimate total application CPU or resident-memory savings.
Video resolution, bitrate, frame rate, quality selection, decoding, audio,
chat rendering, and animation settings are unchanged.

## Validation

Nine of the initial ten regression cases failed against the previous application
code; shutdown cancellation already passed. The final catalog also covers
synchronous refresh reentry and concurrent log producers. The tests use controlled
services and an offscreen dispatcher, with no provider credentials or network
requests.

Final verification on September 25, 2026:

- Release solution build with warnings treated as errors: zero warnings/errors.
- All 12 new regressions passed, including concurrent producers and reentry.
- Complete headless suite: 982 passed, 239 expected desktop-only skips, zero
  failures or timeouts. The existing skip ceiling was enforced.
- Formatting and warning-level analyzer verification for the changed C# files,
  plus `git diff --check`: passed.
- Locked win-x64 restore with transitive NuGet auditing and self-contained,
  single-file publish with warnings treated as errors: passed.

The first complete run had one timeout assertion in the existing current-live
DVR promotion test. It passed five isolated rechecks and the final complete run;
replay code and that test were not changed in this work. Interactive desktop and
authenticated provider checks were not exercised.

The runnable build is `artifacts/background-workflow-resources/StreamStudio.exe`.

Build Release with the SDK pinned by `global.json`, then run:

```powershell
$env:SVS_TEST_FILTER = 'background workflow resources'
dotnet tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll
```

Verification logs are in `artifacts/logs/background-workflow/`.
