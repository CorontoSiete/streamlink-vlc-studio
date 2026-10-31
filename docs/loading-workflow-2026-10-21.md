# Loading and refresh responsiveness

Home now displays confirmed Twitch and Kick followed-channel results as each
provider finishes. A slow provider no longer holds back the other provider's
cards. The status names the provider still loading, and existing cards for that
provider remain available until its replacement result arrives.

Refreshes continue to share an in-flight request. Surviving cards retain their
commands and pin state, including artwork loaded during the previous refresh.
The completed list keeps the existing viewer-count ordering. Live-notification
baselines and the last-updated timestamp advance only after the status refresh
finishes. Optional banner/history enrichment still follows status loading.
Account changes and shutdown reject late snapshots. Disposing an unfinished
provider enumeration cancels and observes its outstanding requests.

Initial download-library discovery now runs on a worker thread while the
existing service gate still protects it. This includes synchronous directory
enumeration and package validation, even when subsequent file reads complete
from cache. Applying the saved download format during view-model construction
therefore yields to the WPF dispatcher. Validation, recovery, format conversion,
and preservation of unavailable previous directories still run normally.
Already-loaded library operations avoid scheduling another scan.

## Evidence and limits

The two delayed-provider regressions failed against the original implementation:
no cards appeared during the 500 ms observation period while the other provider
was held pending. With progressive loading, the first run displayed usable
Twitch cards in 102.4 ms and Kick cards in 2.0 ms while the other provider was
still blocked. These numbers include local initialization and scheduling; they
are controlled fixture observations, not live-provider benchmarks or a claimed
percentage improvement in total process startup time.

The library regression applies the saved format from a real WPF dispatcher and
records the thread that validates an unavailable previous library. It also
checks that the junction is rejected, its target files survive, and subsequent
library reads do not repeat the scan.
Against the baseline, both the caller and scanner ran on WPF thread 9 and the
regression failed. After rebuilding the revised code, scanning ran on worker
thread 6 while the caller remained on WPF thread 9; the regression passed.

The final focused Release run passed all eight loading-workflow regressions
without skips. The solution build had zero warnings and errors, and formatting
verification passed for the changed C# files.

The self-contained win-x64 single-file publish succeeded with warnings treated
as errors. The repository's startup-hook probe launched the actual packaged
executable, loaded its bundled infrastructure and updater, verified its
installation-directory resolution, and exited with code 0 before opening the
main UI or contacting update servers. This does not substitute for authenticated
playback or an interactive desktop test.

The full headless suite encounters environment restrictions: a standalone
Windows DPAPI probe also fails outside the application, native caches cannot
write under the user's AppData directory, and local OAuth listeners/browser
fixtures cannot initialize reliably. These checks were run without disabling
credential protection or changing production cache behavior.
The original runner stopped when a browser fixture exceeded its timeout and
failed to drain. Unrun tests were continued in fresh processes with the original
catalog and runner, retaining their assertions, timeouts, and headless skip
policy. A scratch startup hook only selects the catalog entries following each
interrupted test; it is removed from the environment before child fixtures run.

Across the original run and two continuations, all 2,297 selected entries were
accounted for: **1,952 passed, 68 failed, 2 timed out, and 275 skipped**. This is
not a clean full-suite result. Failures include DPAPI, denied native-cache
directories, local OAuth listener errors, WebView2/browser timeouts, and
persistence/browser assertions. The eight new loading checks also passed within
these broader runs. Authenticated live playback and interactive desktop behavior
remain unverified in this environment.

`artifacts/loading-workflow/validation.json` records the per-run counts, limits,
and SHA-256 of the executable. The combined failure headers are preserved in
`.tmp/loading-full-suite-failures.txt`.

Reproduce the focused checks with the pinned SDK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 Test -NoRestore -Filter 'loading workflow:'
```

Baseline source snapshots are in `.tmp/loading-workflow-baseline/`. Provider
before/after logs are `.tmp/loading-workflow-before.log` and
`.tmp/loading-workflow-after.log`; the separate library baseline is
`.tmp/loading-disk-before.log`.
The rebuilt final focused run is `.tmp/loading-focused-verified.log`. The initial
full headless run is `.tmp/loading-full-suite.log`; continuations are
`.tmp/loading-remaining-*.log`. The scratch continuation hook is in
`.tmp/loading-resume-probe/`.
Publish output is `artifacts/loading-workflow/StreamStudio.exe`; publish and
packaged-probe logs use `.tmp/loading-publish.log` and
`.tmp/loading-packaged-smoke.*.log`.
