# Repository review — 2026-09-25, pass 7

This review preserves the updater, maintenance, documentation, and test changes already present in the working tree.

## Fixes and cleanup

- **Replay promotion and seeking:** publishing a live DVR broadcast as a VOD could change its identity while a seek waited for the replay transition. The seek then returned without moving playback. Promotion now shares the existing replay transition gate; a seek that follows promotion adopts the VOD for the same broadcast. This also prevents chat and playback URL resolution from switching sources midway through a seek. Waiting promotion remains cancellable.
- **Preview request retries:** HTTP deadlines now use the existing metadata and sprite retry delays (60 and 30 seconds). Repeated hovering no longer bypasses those delays. Caller cancellation still permits an immediate retry, and unavailable storyboard images still fall back to live previews.
- **Preview validation:** malformed GraphQL roots and durations are rejected before requesting the storyboard. A calculated sampling interval that underflows to zero is rejected before frame indexing.
- **Reuse and removal:** removed the redundant chat-start forwarding method, unused private parameters, a completed-task wrapper around synchronous overlay queuing, the superseded preview timeout catch, and overwritten/repeated picture-in-picture sizing calculations. Promotion polling now uses the existing cancellation helper that tolerates completion races.
- **Test reliability:** a captured-chat test now waits for asynchronous UI delivery and checks that future messages remain hidden. Its tab is disposed even if an assertion fails.

Nine regression cases cover previews and promotion. Four preview cases and two deterministic promotion cases failed against the previous behavior. The original intermittent promotion test and the corrected chat-delivery test each passed ten consecutive focused runs.

## Verification

- Complete headless suite: **970 passed, 239 expected desktop-only skips, zero failures or timeouts**, with the skip ceiling enforced.
- Release solution build and the separate update-probe project: zero warnings or errors, with warnings treated as errors.
- Locked dependency restore with transitive NuGet auditing passed; dependency versions are unchanged.
- Warning-level whole-solution analyzer/format verification, whitespace verification, and `git diff --check` passed.
- All 14 PowerShell tooling checks passed. Syntax validation covered 22 PowerShell, 29 XML, 10 JSON, and one JavaScript file.
- The portable argument tokenizer matched Windows parsing for all 78,125 generated inputs.
- Both pinned native overlay inputs were verified by the build.

The optional informational analyzer scan also completed. It reported existing modernization/style suggestions, with no warning or error diagnostics; those suggestions were not applied wholesale.

## Coverage and limits

Repository-wide compilation, analyzer checks, reference/duplicate scans, and script/configuration validation were combined with targeted inspection of parsing, settings, HTTP and process boundaries, chat, replay, UI lifetimes, native integration, and maintenance/update paths. Remaining unreferenced-name candidates were required native struct fields or WPF/framework entry points.

Interactive desktop tests, authenticated provider calls, actual installation/removal, and rebuilding the native C plugin were not exercised. This review does not establish that every possible defect has been eliminated.

Validation logs and review probes are under `artifacts/logs/review-current/`, including `preview-before.log`, `preview-after.log`, `promotion-before.log`, `promotion-after.log`, `final-build.log`, and `verified-tests.log`.
