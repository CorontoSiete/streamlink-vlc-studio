# Follow-up code review - September 24, 2026

Changes were applied to `streamlink-vlc-studio`. The adjacent workspace copies were left intact.
This pass combined solution-wide compiler and formatting diagnostics, C#/PowerShell reference
scans, duplication scans, browser and tooling checks, and manual review focused on shared runtime
boundaries. Generated build output and bundled third-party binaries were excluded from source
refactoring; the pinned native inputs were verified by the build.

| Area | Fix |
| --- | --- |
| Processes | Already-canceled requests are rejected before launching a subprocess. Invalid timeout and redirection settings are checked before startup, preventing initialization failures from leaving a child running. |
| Buffered input | Text and IRC readers honor cancellation even when their next line is already buffered. Both readers now share buffering, cancellation checks, and stream disposal in `BufferedByteReader`. |
| Shared requests | Already-canceled live-channel and Twitch/Kick credential requests no longer start work or return cached results. Cancellation of an individual active waiter remains independent of shared work. |
| Live-channel caching | HTTP error responses are evicted instead of being reused for five seconds. Successful responses retain the existing cache lifetime. |
| Local HTTP | Header names with surrounding whitespace, control characters, and malformed request targets are rejected. One-to-one header decoding preserves invalid bytes for validation. |
| Playlists | URI rewriting parses attribute boundaries outside quoted strings, preserves quoted metadata containing `URI=`, and rejects ambiguous duplicate URI attributes. Both Twitch fallback and muted-segment repair use the corrected helper. |
| Maintenance | Containment checks handle drive and UNC share roots correctly. Filename validation rejects Windows device names padded with spaces before an extension. |
| Replay chat | Twitch and Kick reject overflowing pagination ranges before network access. Frontier calculations share checked duration addition and do not throw at the duration limit. |
| Reuse and dead code | OAuth pages share one response writer and response cleanup path. Removed the unused `LocalHttpRequest.GetHeader`, the redundant viewer HTTP-client wrapper, duplicate OAuth state validation, and duplicated byte-reader machinery. |

`ReviewFollowupTestCatalog` adds 15 regression tests. Each test reproduced a failure before its
corresponding fix. The cases cover process admission, buffered cancellation, cold and warm caches,
failed responses, HTTP syntax, quoted playlist attributes, maintenance paths, and replay arithmetic.
The baseline suite passed 693 tests with 207 desktop-only skips before edits.

Final validation:

- Release solution build with warnings as errors: passed, zero warnings and zero errors.
- Locked NuGet restore with transitive vulnerability auditing and audit warnings as errors: passed.
- Full headless suite through `dotnet test --no-build --no-restore`: 708 passed, 207 skipped,
  zero failures or timeouts. The existing maximum of 207 skips was enforced.
- Full solution formatting verification: passed; the final replay changes and their tests also
  passed a subsequent focused formatting check.
- All 24 browser tests and syntax checks for all five JavaScript files: passed.
- Parser checks for all 20 PowerShell files, generated route verification, tooling contracts,
  and pinned native input verification: passed.

Interactive desktop/window/input tests, authenticated live-provider behavior, and production
installer execution were not validated in this pass.

The pre-edit source snapshot is `.tmp/review-followup-before.zip`. The reviewable source/test
diff is `.tmp/review-followup.patch`. Validation and failure-reproduction logs use the
`.tmp/review-followup-*` prefix. These generated artifacts remain local and are ignored by the
repository's existing ignore policy.
