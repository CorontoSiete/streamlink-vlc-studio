# Code review follow-up — 2026-11-02

## Scope

Repository-wide source inventory, build, unused-member and formatting checks, and regression testing, with manual review of HTTP response ownership, cancellation, persistence, playback transitions, native protocol boundaries, and developer tooling. The inventory contains 858 source and test files. This is a repository-wide review with focused manual inspection, not a claim that every source line has been exhaustively verified. Existing local edits were preserved; third-party sources and pinned native binaries were retained.

## Verified bugs fixed

- **Category images could exhaust all four download slots indefinitely.** A CDN could return headers and then stall its image body. Category artwork now uses the existing `BoundedHttpResponseSender`, retaining the HTTP timeout during body reads. The regression queues eight stalled bodies, verifies they all finish as missing artwork without canceling the browser request, then successfully fetches another image through the released slots.
- **Kick chat setup could wait indefinitely after receiving response headers.** Pusher discovery now uses the same response helper. The regression verifies both a stalled-body timeout and explicit caller cancellation. Both HTTP regressions failed before the production changes and passed afterward. With headers-only completion, .NET's client timeout otherwise stops applying once headers arrive; see [Microsoft's HTTP completion documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0).
- **Switching tabs paused browser picture-in-picture playback.** A video in picture-in-picture now remains playing when another player is selected. Entering and leaving picture-in-picture reapplies the existing playback policy, including resuming automatic pauses and preserving manual pauses. Tests cover live streams and VODs. This behavior is covered by deterministic media adapters; a real detached picture-in-picture window was not manually exercised.
- **A replaced player could send a delayed volume write.** Player disposal now cancels its volume-save timer, and closed/stopped players cannot initiate another save. A regression reproduces the delayed write and verifies its removal.

## Reuse and cleanup

- Reused the existing bounded response helper in both affected HTTP paths, rather than adding separate timeout implementations.
- Centralized per-player volume timer cancellation and reused it during flushing and disposal.
- Removed the unused `grip` icon after checking browser markup, application code, and tests.
- Removed redundant shared `state.channel` storage and resets; VOD requests now read their channel directly from the input.
- Extended the existing test catalogs and browser fixture. Corrected a pre-existing Kick VOD test race: the fake player reports playback before the tab initializes replay controls, so the check now waits for those controls before inspecting them.

## Validation

- Final Release solution build: **zero warnings and zero errors**.
- Full headless-safe Windows run: **2,388 selected; 2,111 passed; 276 interactive desktop skips; one failure; zero timeouts or unrun tests**. The failure was the existing Kick VOD test's replay-initialization race described above. After correcting that wait and rebuilding, the complete affected case passed **five consecutive runs**. This gives **2,112 distinct passing .NET cases across the full run and the corrected-case reruns**.
- Deterministic JavaScript and account-worker checks: **67 passed**, including the two new browser regressions. Both new tests failed against the pre-fix browser code.
- Both new HTTP regressions failed against the pre-fix implementations. The Kick connection group passed **all five cases**, and the full Windows run passed all category-artwork checks, including the new eight-request stalled-body test.
- Python measurement-tool tests: **8 passed**.
- All **eight PowerShell/tooling regression suites**, all PowerShell syntax checks, and pinned native dependency verification passed.
- Repository formatting and unused-member checks passed. The final changed C# files also passed whitespace verification. JavaScript syntax and `git diff --check` passed.
- Real-browser integration checks passed for authentication, navigation, account volume, synchronized chat, responsive layouts, and fullscreen. Actual detached picture-in-picture behavior and the 276 skipped interactive desktop checks remain outside this run's verified coverage.
- Untouched pre-existing diffs match the saved starting baseline. The existing browser fixture and playback tests were extended in place.

The initial restricted Windows run could not access credential storage, certificate keys, browser rendering, and a damaged-executable fixture. The full verification was repeated with normal Windows access and the machine's standard tool paths. NuGet's online advisory feed was unavailable, so the locked restore and verification used `NuGetAudit=false` only in their processes; dependency versions and repository audit defaults were preserved.
