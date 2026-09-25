# Repository review, September 25, 2026 — third pass

This pass combined repository-wide compilation, reference and duplication scans with manual
inspection of provider parsing, HTTP boundaries, chat/playback lifetimes, settings, UI helpers,
bootstrapper/maintenance code, and installation scripts. The tree contains 285 production C# files,
82 C# test files after this pass, 19 PowerShell scripts, and one JavaScript file. XAML and project
configuration were also checked. Generated output and third-party assets were excluded from
refactoring. This was a further review of the current tree, building on the two earlier reports.

## Fixes

- **Twitch VOD search:** malformed optional access-label responses, response-body timeouts,
  disconnected reads, oversized bodies, unsupported charsets, and invalid text encoding now leave
  the retrieved videos and pagination usable, with access marked unknown. Explicit caller
  cancellation still cancels the search.
- **Channel selection:** Twitch VOD broadcaster lookup requires the requested string login.
  Kick public and website replay lookup require a matching string slug, so unidentified rows
  cannot supply another stream's ID/start time. Kick chat broadcaster lookup applies the same
  requirement and rejects nonpositive IDs, continuing to a usable matching row when available.
- **Overlay shutdown:** synchronous and asynchronous stop paths share one cancellation snapshot.
  They tolerate the listener disposing its cancellation source between the snapshot and the
  cancellation request. Asynchronous stop still drains the captured listener task.
- **Setup dependency detection:** the Streamlink version probe uses the shared bounded process
  runner. It no longer performs unbounded `ReadToEndAsync` waits after the parent process exits,
  and rejects truncated output. The runner drains both pipes, limits retained output, and bounds
  process cleanup. Setup links the existing source files without adding the application's native
  dependencies to its payload.

## Reuse and removal

- Kick replay reuses the live-channel payload selector already used by metadata and viewer counts.
  Public and website replay metadata share their timestamp/ID projection.
- Optional HTTP JSON failure classification and Twitch device-ID generation are shared.
- Docked/native chat text and explicit-emote fallback use one catalog-emote rendering helper;
  their different whitespace behavior is preserved.
- Streamlink log events use the existing subscriber-isolation helper, including logger isolation.
- Removed the unused production overloads for the old nongeneration playback-policy callback and
  the preview decoder without an initialization argument. Existing tests now call the active
  production signatures; their behavioral assertions remain.

Production code and project configuration are **118 lines smaller** overall. Dependency versions
and all three package lock files are unchanged.

## Verification

- Release solution build with warnings treated as errors: **zero warnings and errors**.
- Full headless suite: **812 passed, 236 expected desktop-only skips, zero failures/timeouts**;
  the existing skip ceiling was enforced.
- Ten new regression cases passed. Seven reproduced failures before the fixes. The additional
  cases cover caller cancellation, positive matching Kick broadcaster IDs, and 200 rounds of
  concurrent native-overlay stop requests. Existing process timeout/output-bound and chat-rendering
  tests also passed in the full suite.
- All 14 PowerShell tooling checks passed; all 19 PowerShell scripts parsed successfully.
- JavaScript syntax and all 25 source XAML/project/application-manifest/build-props XML files passed.
- Locked solution restore with transitive NuGet auditing enabled passed.
- Whole-solution formatting and analyzer verification passed with no changes required.
- The build verified both pinned native-overlay inputs; added production lines have no trailing
  whitespace.

The initial baseline had two packaging/maintenance test failures because child scripts resolved
the system .NET 8/9 host instead of the installed .NET 10.0.302 SDK. Prepending that SDK to the test
process's PATH fixed the environment; both tests passed in the final run. No SDK pin was changed.

Interactive desktop/input tests, authenticated live-provider requests, and execution of the
production installer were not exercised. Passing checks establish the tested behavior, not the
absence of every possible defect.

## Review artifacts

The workspace has no Git metadata. The source snapshot and patch preserve a reviewable record:

- [Source before this pass](../artifacts/review-20260925-pass3/before.zip)
- [Complete patch](../artifacts/review-20260925-pass3/changes.patch)
- [New regressions before fixes](../artifacts/review-20260925-pass3/regressions-before.log)
- [Release build](../artifacts/review-20260925-pass3/final-build.log)
- [Full regression suite](../artifacts/review-20260925-pass3/final-tests.log)
- [Tooling checks](../artifacts/review-20260925-pass3/tooling.log)
- [Restore/audit log](../artifacts/review-20260925-pass3/restore-audit.log)
- [Formatting/analyzer log](../artifacts/review-20260925-pass3/format.log)
