# Additional repository review — September 25, 2026

This pass reviewed the current source after the [earlier review](code-review-2026-09-25.md),
with repository-wide reference/duplication scans, compiler and analyzer checks, and further
inspection of provider parsing, chat and playback lifetimes, caches, settings, native audio,
hotkeys, and release tooling. Generated files and bundled third-party binaries were excluded
from refactoring. The current tree contains 284 production C# files and 81 C# test files.

## Fixes

- **Followed-stream health:** missing or malformed response arrays, invalid channel rows, and
  malformed Twitch pagination no longer count as a successful refresh. Valid rows already received
  are retained in the result, with their profile images, while the platform is marked incomplete.
  This preserves the notification baseline
  through malformed responses instead of falsely declaring channels offline and announcing
  them again on recovery. Valid empty arrays and explicit offline Kick streams remain successful.
- **Channel identity:** Kick followed results are restricted to requested channels. Twitch
  Browse and Followed require string logins rather than coercing JSON numbers into channel names.
- **Overlapping pages:** followed results are deduplicated by platform/channel, ignoring case,
  so a channel repeated across pages cannot generate duplicate results and live notifications.
- **VOD playlists:** muted-segment rewriting changes only media filenames. Encryption key and
  initialization-map URLs, signed query parameters, directories, and tag metadata retain their
  original text. URI validation and absolute URL resolution still use the shared playlist rewriter.

## Reuse and dead code

- Twitch Browse and Followed share stream-field mapping.
- Kick viewer counts, metadata, and followed streams share live/offline stream-shape interpretation.
- Immediate and deferred VLC audio restoration share one implementation, retaining their different
  track-restoration conditions.
- Both native overlay queue limits use the same frame-eviction helper, retaining eviction order
  and protected-request handling.
- Removed the obsolete `TabNavigationKeyPolicy` file. Its unused fixed-key entry point was only
  referenced by tests; active navigation and text-input rules now reside in `HotkeyBindingPolicy`.
  Existing tests now exercise those production rules.

Production code is **45 lines smaller** overall. No dependency versions or lock files changed.

## Regression evidence

Eight regression cases were added. Seven reproduced failures before their fixes: malformed
followed envelopes, malformed rows, malformed later pages, malformed pagination, unrelated Kick
channels, overlapping Twitch pages, and playlist text corruption. The eighth protects valid
empty/offline behavior. The initial full suite also caught a regression in profile enrichment
for partial results; it was corrected and the new partial-result tests now assert profile images.
Failure reproductions and final regression logs are preserved with the patch.

## Verification

- Final full headless suite: **802 passed, 236 skipped, zero failures or timeouts**. The existing
  ceiling of 236 desktop skips was enforced.
- **Three silent native-VLC audio integration tests passed** using the installed VLC decoder:
  muting preserves its decoder, background playback starts silently, and rapid switching leaves
  only the selected player audible. These capture PCM in memory without playing test tones.
- All eight new regression cases passed. Existing followed-stream and notification tests passed.
- Release solution build with warnings treated as errors: **zero warnings and errors**.
- Final whole-solution formatting and analyzer verification: passed.
- Locked dependency restore and transitive vulnerability audit: passed; all three lock files unchanged.
- All 14 PowerShell tooling checks passed, and all 19 PowerShell scripts parsed successfully.
- JavaScript syntax and source XAML/project/application-manifest XML parsing passed.
- The build verified both pinned native overlay inputs. Added production lines have no trailing whitespace.

## Local artifacts

- [Source snapshot before this pass](../artifacts/review-20260925-pass2/before.zip)
- [Patch for this pass](../artifacts/review-20260925-pass2/changes.patch)
- [Regression failures before fixes](../artifacts/review-20260925-pass2/regressions-before.log)
- [Pagination failure before its fix](../artifacts/review-20260925-pass2/pagination-before.log)
- [Release build log](../artifacts/review-20260925-pass2/final-build.log)
- [Full regression log](../artifacts/review-20260925-pass2/final-tests.log)
- [Native audio test log](../artifacts/review-20260925-pass2/native-audio.log)
- [Tooling checks](../artifacts/review-20260925-pass2/tooling.log)

Interactive desktop/input tests, authenticated live-provider requests, release packaging, and
installer execution were not exercised. The source snapshot and patch are provided because the
workspace has no Git metadata.
