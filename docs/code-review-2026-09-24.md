# Code review — September 24, 2026

Work was applied to `streamlink-vlc-studio`. The other workspace copies were left intact.
The review combined repository-wide compiler diagnostics, reference and duplication scans,
manual inspection of shared runtime paths, and the existing regression suites.

## Fixes

| Area | Corrected behavior |
| --- | --- |
| Settings | Valid UTF-8 files with a BOM retain saved preferences. Duplicate JSON properties, including differently cased settings and secret names, recover through the existing invalid-file backup path instead of crashing or ambiguously overriding values. |
| Atomic writes | Cancellation before writing or before committing preserves the destination and removes the temporary file. Settings now reuse the same helper as updates and replay playlists, with durable flushing enabled. |
| HTTP | Streaming response bodies retain the request timeout after headers arrive. Both request cancellation and body-read cancellation reach the underlying stream. |
| Catalogs | Badge and emote JSON requests share size limits, strict decoding, BOM handling, timeout handling, and response disposal through one helper. |
| Updates | Checks and downloads leave their busy state after cancellation or timeout. Update responses reuse the bounded HTTP sender and byte reader. Signed payload bytes remain unmodified. |
| Playback cleanup | A failing engine disposal no longer prevents disposal of its parking surface and cancellation source. Late cleanup failures are observed even without an optional tracking callback. |
| Command-line parsing | Portable parsing uses Windows argument separators, preserving embedded newlines and nonbreaking spaces. |
| Dead code | Removed the unused bootstrapper numeric-variable helper and an unused browser-worker function dependency. |
| CI and tests | Corrected the expected headless skip limit to include the three window-sharing tests, applied the configured formatting, and removed a process test's assumption that a child prints within 100 milliseconds. |

## Regression coverage

`CodeReviewTestCatalog` adds ten tests covering cancellation and failed writes, BOM decoding,
duplicate settings, streaming deadlines, caller cancellation, catalog reads, updater state,
playback disposal, and 5,000 deterministic comparisons with Windows command-line parsing.
The first five tests were run against the original implementation and each reproduced a failure.

The pre-edit source snapshot is retained in `.tmp/review-before.zip`. Verification logs use
the `.tmp/review-*` prefix.

## Final verification

- Locked NuGet restore with transitive vulnerability auditing and audit warnings treated as errors: passed.
- Release solution build with warnings treated as errors: passed, zero warnings and zero errors.
- Solution formatting verification: passed; the subsequent process-test edit also passed its formatting check.
- Full headless test run: 693 passed, 207 desktop-only tests skipped, zero failures or timeouts.
- Browser JavaScript syntax checks and all 24 extension tests: passed.
- PowerShell parsing, generated browser-route verification, tooling contracts, and pinned native dependency verification: passed.

Interactive window/input checks and authenticated live-provider behavior were not validated in this run.
