# Code review follow-up — 2026-09-30

Reviewed the current working tree, preserving the changes already present at the
start of this review. The baseline source/configuration snapshot is in
`.tmp/review-followup-20260930-baseline`; the earlier review report remains intact.

The review combined compiler analysis, source searches, focused manual review,
builds, and existing test suites across C#, XAML, native C/C++, PowerShell, Python,
JavaScript, shared configuration, and workflows. The final production C# inventory
contains 375 files and 88,267 lines, excluding generated build output. Manual
review focused on playlist parsing, HTTP limits, cancellation and disposal,
download integrity, settings/history persistence, native resource ownership,
process cleanup, and installer/update/release transactions.

## Fixes

- **Incomplete HLS manifests:** Duration calculation, long replay rebasing, seek
  previews, live preview playback, direct VOD startup, and DVR discovery now reject
  delta playlists containing `#EXT-X-SKIP`. A shared `HlsPlaylistPolicy` serves
  these paths and the existing offline-download validation. Delta updates omit
  segment metadata and require information from a previous playlist; standalone
  readers cannot safely reconstruct their timeline or initialization state.
  [HLS specification, EXT-X-SKIP](https://datatracker.ietf.org/doc/html/draft-pantos-hls-rfc8216bis#section-4.4.5.2).
  The existing preview/Streamlink fallbacks handle unsupported manifests.
- **Malformed headers:** DVR discovery and seek-preview parsing require the exact
  `#EXTM3U` header. DVR discovery also rejects mixed master/media content and tags
  that merely resemble `#EXTINF`. Embedded HTML or `#EXTM3UX` no longer qualifies
  as a valid playlist.
- **UTF-8 BOM handling:** Seek previews accept a BOM-prefixed header and skip the
  validated header during segment parsing. Previously it could be treated as a
  segment URI and cause an otherwise valid playlist to be rejected.
- **Release cleanup:** Temporary release-note deletion uses `File.Delete` and
  reports cleanup failures as nonfatal warnings. A locked file can no longer turn
  successful publication into a failure or replace the original upload error,
  including when the caller treats warnings as errors.

Eight C# regression tests cover the playlist cases. The mocked release-publication
suite adds locked-file scenarios for both successful and failed uploads. The
duration/rebasing/preview, VOD-startup, and DVR failures were reproduced before
their fixes; the BOM regression also exposed the header/segment distinction.
Publication tests use a fake GitHub command and do not publish a release.

## Cleanup and reuse

Removed 355 unused imports: 17 from the small replay payload helper and 338
identified by compiler diagnostics across 65 additional application/test files.
The cleanup preserved comments and source bodies and formatted the affected files.
Removed two overwritten assignments in native chat-overlay rendering while
preserving the video-size recording call. `IDE0059` is now a build warning so
similar assignments fail the repository's warnings-as-errors check.

The final audit checks linked source files in every project that compiles them.
Two `System.IO` imports needed by the bootstrapper were restored after the first
cleanup pass; the project's different implicit imports make these necessary.

The shared HLS policy replaces repeated skip-tag handling across seven consumers.
WPF callbacks, interface overrides, and native ABI layout fields were retained
where their framework or marshaling role requires them.

## Validation

- Baseline full check: **1,431 passed, 263 desktop tests skipped**.
- Final full check: **1,439 passed, 263 desktop tests skipped**; Release build
  completed with **zero warnings and zero errors**.
- Final compiler import audit: **no workspace failures, compilation errors, or
  unused imports**, including all compilation contexts for linked source files.
- Focused VOD startup suite: **18 passed**. The seek-header/BOM regression passes.
- Native overlay/plugin rebuilt with `-Wall -Wextra -Werror` into a separate
  temporary output directory; bundled application binaries were preserved.
- Seven native test programs passed: TLS handshake, network framing, readability,
  render resources, compositor, subpictures, and received frames.
- Installer lifecycle, development-tooling contracts, and mocked release
  publication passed.
- Python syntax, XML/XAML/project/installer syntax, JavaScript syntax, and
  `git diff --check` passed.
- Production C# syntax audit: **375 files, no syntax errors**.

The full check runs locked restore, formatting verification, PowerShell syntax,
tooling/native-input contracts, a Release build with warnings as errors, and the
managed test suite. Desktop-dependent tests use the repository's headless skip
profile; this review does not claim those interactive UI scenarios were exercised.
Logs and compiler/audit results are in `.tmp/review-followup-20260930-*`.
