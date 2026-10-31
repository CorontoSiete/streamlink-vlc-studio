# Home and Discover request concurrency

Twitch Home now loads live follows and the complete followed-channel inventory
concurrently after validating the account. Each endpoint retains its own bounded
pagination. Offline classification waits for all live pages to finish, so an
incomplete or malformed live response cannot incorrectly mark channels offline.
Profile images, ordering, notification health and offline metadata use the same
completed results as before.

Kick Discover now overlaps the category list and top live category discovery on
the first unfiltered page. Search and subsequent pages keep their existing
request paths. Category merging, viewer totals, detail lookup concurrency and
pagination remain intact.

Both paths own their additional request through completion. Caller cancellation
and failures stop and drain the other request before returning. Normal successful
loads use the same endpoints and request counts as before; the requests overlap
instead of waiting for one another. A primary failure can now occur after the
independent request has started; its result is discarded and the request drained.

## Verification

`LoadingConcurrencyTestCatalog` exercises the actual services with controlled
HTTP responses. Two requests are independently held open until the test releases
them, proving overlap without relying on elapsed-time thresholds. Other cases
cover cancellation, failed and malformed primary responses, repeated Twitch
cursors, live/offline exclusion, profile images, category merging and cursors.

The timing fixtures delay each of the two independent responses by 150 ms. They
run four trials per workflow, exclude the first for JIT/setup, and print the
remaining samples and median. These are reproducible service-path measurements,
not live-provider timings or total process startup measurements.

| Workflow | Before median | After median |
| --- | ---: | ---: |
| Twitch Home | 311.1 ms | 154.6 ms |
| Kick Discover | 320.5 ms | 160.9 ms |

The original code passed the two measurement fixtures and failed all six new
overlap/lifecycle checks because it never started both requests together. The
revised code passed all eight checks without skips. The solution Release build
completed with warnings treated as errors: zero warnings and zero errors.

The existing incomplete-live-page regression now expects the inventory request
to start, while retaining its assertions that no offline cards are produced and
the platform is not marked successful. The new malformed-live and cancellation
checks additionally hold that inventory open and verify it is drained.

The final targeted runs passed the eight new concurrency checks, eight existing
loading-workflow checks, all 65 selected Browse checks (30 desktop checks skipped),
nine Kick category searches, ten Home refresh checks and 24 stream-opening
workflow checks. Offline followed channels passed 46 of 47 checks; the remaining
pin-persistence failure also occurs with the saved original infrastructure
assembly. The wider category filter passed 59 checks, skipped three desktop checks
and exposed a settings-autosave failure that also reproduces with the baseline.

Broader substring filters also matched unrelated browser/OAuth tests. They
encountered browser failures and a browser timeout that stopped one process before
23 later entries could run. The same OAuth, follow, follow-import and clip-browser
failures/timeouts were reproduced in fresh processes using the saved original
assembly. These broader runs are not clean full-suite results. No credential
protection, browser assertions or production behavior was disabled to make them
pass. Authenticated live-provider performance and total process startup remain
unmeasured.

Before/after output and build logs are in `.tmp/loading-concurrency-*.log`.
Original source and the original infrastructure assembly are preserved in
`.tmp/loading-concurrency-baseline/` for comparison.
Final focused results use `.tmp/loading-concurrency-verified-*.log`; the earlier
workflow checks use `.tmp/loading-concurrency-regression-*.log`. Baseline failure
comparisons use `.tmp/loading-concurrency-baseline-*.log`.
`.tmp/loading-concurrency-validation.json` records the selected runs, unique
passing checks, known failures, timing medians and before/after assembly hashes.

Run the new regressions using the pinned SDK:

```powershell
.\scripts\dev.ps1 Test -Filter 'loading concurrency:'
```
