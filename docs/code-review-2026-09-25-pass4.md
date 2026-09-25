# Repository review — September 25, 2026, fourth pass

Repository-wide compilation, analyzer, reference, and duplication checks covered the current
source tree: 286 production C# files, 86 C# test files after this pass, 19 PowerShell scripts,
the channel-points JavaScript, and source XAML/project/build configuration. Manual inspection
focused on provider parsing, replay chat, polling and cancellation, playback transitions,
HTTP/process boundaries, settings, update/maintenance paths, and UI helpers. Generated output
and bundled third-party binaries were excluded from refactoring.

## Fixes

- Twitch replay chat no longer mistakes missing or malformed comment envelopes, edge arrays,
  or pagination metadata for the end of a VOD. These results remain retryable. Valid empty
  pages retain their existing completed/continuing behavior.
- Kick history rejects malformed message envelopes and cursor types instead of treating them
  as empty or exhausted history. The shared parser serves both live-chat backfill and VOD chat.
- Kick VOD chat no longer marks a full time range as loaded when a later page fails, repeats
  a cursor, or exhausts the page budget. Valid messages already fetched remain usable, but
  the missing range stays eligible for retry. The existing 20-page request limit remains;
  persistently incomplete provider pages can still prevent full history from being loaded.
- Kick cursor conversion preserves microsecond precision, rounding fractional upper bounds
  upward instead of truncating to milliseconds. This prevents gaps at fractional time boundaries.
- Replacing replay-clock, viewer-count, or aspect-ratio polling now cancels and observes the
  old task and disposes its token source only after it finishes. Tokens are captured before
  scheduling work, avoiding access to a disposed source when a worker starts late.
- Late viewer responses and queued callbacks from stopped/replaced polls cannot overwrite
  current count, category, or title. All three fields apply in one guarded UI callback.
- Isolated test processes use the existing bounded process runner. Output retention, inherited
  pipe drainage, and timeout cleanup are bounded; the outer timeout allows cleanup to finish.

## Reuse and dead code

- One playback transition helper owns gate acquisition, cancellation, and release for all three
  pause/resume entry points.
- Three pollers share replacement and cleanup logic; successful and failed viewer updates share
  the same cancellation check at UI dispatch.
- Both VOD fetchers share overflow-safe timestamp addition through `DurationValues`.
- Hotkey serialization and display formatting share modifier ordering and assembly.
- Removed three unused legacy identity constants after checking source, tests, scripts, and XAML.
  Referenced legacy migration identifiers remain in use.

Production code is 56 lines smaller overall. Package versions and lock files are unchanged.

## Verification

The original baseline built without warnings and passed 880 headless tests, with 236 expected
desktop-only skips. Fourteen focused regression cases were added. Nine cases were run against
the original implementations and reproduced failures; their logs are retained below. The others
cover valid empty pages, cancellation, cursor validation, and a single UI update callback.

- Release solution build with warnings treated as errors: zero warnings and errors.
- Whole-solution formatting/analyzer verification passed; the final changed files were rechecked.
- Locked restore with transitive NuGet auditing enabled passed.
- All 14 PowerShell tooling tests passed; all 19 PowerShell scripts parsed.
- JavaScript syntax, 26 source XML files, and 10 source JSON/WiX files passed parsing checks.
- The build verified both pinned native overlay inputs. No new trailing whitespace was found.
- Final complete headless suite: **894 passed, 236 expected desktop-only skips, zero failures
  or timeouts**. The existing skip ceiling was enforced. All 14 new regression cases passed.

Interactive desktop/input tests, authenticated provider requests, and execution of the production
installer are outside this verification. Passing checks do not establish the absence of every defect.

## Review artifacts

This workspace has no Git metadata, so the original source snapshot and patch preserve the changes.

- [Original source snapshot](../artifacts/review-20260925-pass4/before.zip)
- [Patch](../artifacts/review-20260925-pass4/changes.patch)
- [Chat regressions before fixes](../artifacts/review-20260925-pass4/regressions-before.log)
- [Polling regressions before fixes](../artifacts/review-20260925-pass4/polling-before.log)
- [Release build](../artifacts/review-20260925-pass4/final-build.log)
- [Full tests](../artifacts/review-20260925-pass4/final-tests.log)
- [Formatting/analyzers](../artifacts/review-20260925-pass4/format.log)
- [Final formatting/analyzers](../artifacts/review-20260925-pass4/final-format.log)
- [Locked restore and dependency audit](../artifacts/review-20260925-pass4/restore-audit.log)
- [PowerShell tooling tests](../artifacts/review-20260925-pass4/tooling.log)
