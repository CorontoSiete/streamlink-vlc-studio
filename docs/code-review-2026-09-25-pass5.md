# Repository review - September 25, 2026, fifth pass

This pass combined repository-wide build, analyzer, reference, duplication, and syntax checks
with manual review focused on provider input, HTTP boundaries, replay/chat lifetimes, settings,
native integration, and update/maintenance tooling. The resulting tree contains 288 production
C# files, 94 C# test files, 20 PowerShell files, and one JavaScript file. Generated output and
third-party binaries were excluded from refactoring. This is not a claim that every possible
defect has been eliminated.

The changes address these concrete problems:

- Twitch EventSub could throw on valid JSON with a non-object root or notification body.
  It also accepted missing metadata and consumed deduplication IDs before validating messages.
  Malformed messages are now rejected without suppressing a later valid delivery with the same ID.
- Prediction responses could produce an empty prediction from an unidentified row and stop
  before a usable row. The shared prediction reader now requires a nonblank string ID.
- Prediction channel lookup, clip creation, and native Twitch chat setup could use the first
  returned user without matching the requested login. One `TwitchUserPayloadReader` now handles
  channel selection and ID validation for those paths and VOD browsing. Clip creation stops
  before its POST when the requested broadcaster cannot be resolved.
- Native chat metadata requests could buffer an entire HTTP body before enforcing the existing
  size limit. Twitch now uses the bounded sender. Kick uses the existing bounded HTTP/curl reader,
  including fallback after provider timeouts while preserving caller cancellation. A direct
  response without a chatroom still proceeds to the fallback.
- CI's headless skip ceiling had not been updated for two existing native tab-switch tests.
  Their names were compared against the previous review log; the exact ceiling is now 238.

Production code is **110 lines smaller** overall. Removed code includes the separate Kick
HTTP/curl implementations and their unused process runner, the old Twitch room-ID parser,
the duplicate clip string reader, and the thumbnail compatibility wrapper. Tests retain the
thumbnail size-limit assertions by calling the shared byte reader with the production limit.
Reference-only native structure fields and WPF accessors remain because native layout and
binding conventions require them. Package versions and lock files are unchanged.

Nine new regression cases all failed against the original implementations and passed after
the fixes. Existing fallback tests now also exercise an HTTP timeout. The broad suite exposed
two existing test races: a promotion assertion ran before fetched chat reached the display,
and a fake dispatcher could overwrite a slider before the test read its intended seek target.
The tests share a bounded wait for actual displayed chat, seek to the captured target, and
dispose the promotion tab even when an assertion fails. The seek case passed ten consecutive
isolated runs after correction; its playback-position and chat assertions remain.

Validation records:

- Final complete headless suite: **923 passed, 238 expected desktop-only skips, zero failures
  or timeouts**, with the corrected skip ceiling enforced.
- Release solution build with warnings treated as errors: zero warnings and errors.
- Whole-solution formatting/analyzer verification passed. Final test-only edits also received
  formatting checks and a warning-free build.
- Locked restore with transitive NuGet auditing enabled passed.
- All 14 PowerShell tooling tests passed; all 20 PowerShell files parsed.
- All 27 source XML files, eight JSON files, and the JavaScript file passed parsing/syntax checks.
- Both pinned native overlay inputs were verified by the build; no new trailing whitespace.

Interactive desktop tests, authenticated provider calls, production installer execution,
and rebuilding the native plugin were not exercised. The native plugin source was inspected
but unchanged. The baseline's only failure was its obsolete skip ceiling: 914 tests passed,
238 were skipped, and none failed individually or timed out.

There is no Git metadata in this workspace. Review and recovery artifacts are preserved here:

- [Original source/configuration snapshot](../artifacts/review-20260925-pass5/before.zip)
- [Code and test patch](../artifacts/review-20260925-pass5/changes.patch)
- [Regressions before fixes](../artifacts/review-20260925-pass5/regressions-before.log)
- [Regressions after fixes](../artifacts/review-20260925-pass5/regressions-after.log)
- [Final build](../artifacts/review-20260925-pass5/final-build.log)
- [Full regression suite](../artifacts/review-20260925-pass5/final-tests.log)
- [Repeated seek regression](../artifacts/review-20260925-pass5/seek-repeat-tests.log)
- [Formatting/analyzers](../artifacts/review-20260925-pass5/final-format.log)
- [Dependency restore/audit](../artifacts/review-20260925-pass5/restore-audit.log)
- [PowerShell tooling](../artifacts/review-20260925-pass5/tooling.log)

The first and second full-run failure logs are retained alongside these artifacts to record
the replay-test timing investigation.
