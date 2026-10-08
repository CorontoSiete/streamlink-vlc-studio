# Code review — playlist parsing — 2026-10-08

Reviewed authored application, core, infrastructure, setup/maintenance, native, script, workflow, and test sources with repository-wide syntax/reference/duplication scans and targeted manual inspection of parsing, downloads, network validation, persistence, cancellation, playback, and chat lifetimes. Generated output and downloaded toolchains were excluded. Existing uncommitted work was preserved.

Confirmed defects and fixes:

- Seek previews previously split encryption attributes at every comma. A valid quoted key URL containing `METHOD=NONE` could therefore make an encrypted segment appear unencrypted. Preview parsing now reads the actual method and rejects malformed or duplicate encryption declarations.
- Offline downloads accepted unquoted key/map URLs and embedded quotes in unquoted values. Those declarations now fail validation before assets are downloaded. Attribute value types are also checked for byte ranges, IVs, and encryption format attributes.
- Master-playlist selection accepted whitespace/control characters inside unquoted attributes. Invalid lists now trigger the existing Streamlink fallback.

One shared, bounded `HlsAttributeList` parser now serves master selection, offline downloads, seek-preview keys, and preview initialization maps. It preserves quoted commas and quote information and rejects duplicate names, malformed separators, and invalid attribute names, following the attribute rules in [RFC 8216](https://www.rfc-editor.org/rfc/rfc8216). Removed the two obsolete attribute-parser methods, their unused generated regex, and the separate map-URI parsing logic. VOD resolution and replay policy now reuse the existing segment-duration helper instead of splitting and parsing durations independently.

Added six regression tests covering the failures and valid quoted commas, quality selection, encryption transitions, and initialization byte ranges. Against the original production code, five failed and the valid-input test passed; all six pass with the fixes.

| Check | Result |
| --- | --- |
| `scripts/dev.ps1 Check -NoRestore` | Passed: formatting, PowerShell syntax, 18 tooling fixture groups, native dependency verification, Release build with 0 warnings/errors, and 1,851 tests passed; 274 skipped. |
| Focused playlist regressions | 6 passed; 0 skipped. |
| C# source audit | 403 production files scanned; no syntax errors or exact duplicate method bodies at the scanner's 50-token threshold. |
| Roslyn solution audit | No workspace failures or project compile errors. Unused-member candidates were checked against XAML, script entry points, and native layout requirements. |
| Native build and fixtures | Warnings-as-errors build and overlay, subpicture, compositor, and GDI hardware fixtures passed. |
| Python script tests | 8 passed. |
| Release-publication fixtures | 11 groups passed under PowerShell 7 using a fake GitHub CLI. |
| Whitespace check | `git diff --check` passed. |

The 274 skipped tests require an interactive desktop and were excluded by headless mode. Authenticated live-provider behavior was not exercised.

The starting snapshot, before/after regression logs, native results, and review-only diff are stored in `C:\Users\ComputerGuy\Documents\streamlink3\.tmp\review-20261008-current`. The review-only diff separates this pass from pre-existing worktree changes.
