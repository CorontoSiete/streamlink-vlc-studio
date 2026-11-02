# Code review follow-up — 2026-10-31

## Scope

Repository-wide build, source-reference, formatting and regression checks, with focused manual review of parsing, persistence, HTTP/media handling, downloads, playback transitions, shared request coordination, native integration and developer tooling. Existing local changes were preserved. Bundled third-party sources and pinned playback binaries were retained.

## Verified bugs fixed

- Concurrent opens using the same library key, pasted input or offline download ID could allocate multiple players. Source identity now has one shared reader; pending opens share one operation, and subsequent opens select the existing player. Failed operations release their pending entry so retry still works. Session changes clear pending entries without allowing an old operation to erase a new one.
- Stopping while a reload was saving progress or resolving media could start the replacement afterward. Stop now invalidates that replacement immediately and releases local media while its saved progress finishes. A late replacement closes its server player instead of starting playback. Reload remains available afterward.
- Reloading or changing quality restarted manually paused playback and reset its speed. Replacement preserves manual pause and both media speed properties. A real Chrome test verifies these values after media initialization, which resets `playbackRate` from `defaultPlaybackRate`.
- The test runner invoked each case before establishing its timeout. Synchronous work could therefore block the whole suite indefinitely. Invocation now runs inside the observed task; synchronous exceptions and asynchronous failures share the existing reporting path. The existing timeout regression covers both synchronous and asynchronous stalls.
- Queued category-viewer and offline-history requests could enter their provider after cancellation when an active request released its slot first. Both paths now reuse `AsyncOperationGate`, which checks cancellation after admission and defers disposal until every lease is released. Existing cancellation regressions exposed both races under the corrected runner.

## Reuse and cleanup

- Extracted source identity and replacement cancellation checks for shared use across playback entry points.
- Centralized release of the reload guard in `finally`, removing redundant error-path assignments.
- Removed the runner's superseded synchronous exception wrapper; its task already captures invocation failures.
- Extended the existing browser fixture and timeout test instead of adding parallel test infrastructure.
- Corrected the volume browser fixture to request its second channel explicitly; reopening the first input now correctly selects the existing player.
- Checked low-reference C# members across 748 source files against XAML, tests and framework/native contracts. The eight remaining method/property candidates are required COM layout entries, WPF overrides or attached-property accessors; deleting them would break live behavior. No further safe member deletions were identified.
- Corrected the README's stale headless skip ceiling to match the development command and its existing contract test.

## Validation

- Final Release solution build: **zero warnings and zero errors**.
- Full headless-safe Windows run: **2,385 selected**, **2,106 passed**, **276 interactive desktop skips**, and three failures; zero timeouts and zero unrun tests. The failures exposed the two cancellation races and the outdated volume fixture described above. After fixing them, rebuilt and reran the entire browser-server group: **40 passed, zero skipped**, covering all three original failures. This gives **2,109 distinct passing .NET cases across the full run and affected-group rerun**.
- Deterministic JavaScript/account-worker suite: **62 passed**, including five additional playback regression cases.
- Python measurement-tool tests: **8 passed**.
- Real Chrome navigation/playback and multi-stream suites passed, including preserved pause/speed after actual media initialization, responsive layouts, decoded video, fullscreen, and one through sixteen streams.
- All **eight PowerShell regression suites**, all PowerShell syntax checks, and bundled native dependency verification passed.
- Repository whitespace verification, changed C# style checks and `git diff --check` passed.

The initial restricted Windows run could not access credential storage, certificates and browser rendering, and blocked while starting a deliberately damaged executable fixture. That maintenance case passed with normal Windows access. The complete run and affected-group rerun used normal Windows access and the machine's standard tool paths. The online NuGet advisory feed was unavailable, so auditing was disabled only for those verification processes; repository dependency versions and audit defaults were preserved. Interactive desktop checks remain outside the headless run.
