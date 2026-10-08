# Code review: playlist validation and reuse, 2026-10-07

This review used the current working tree as its starting point and preserved the existing local changes. It includes repository-wide syntax, reference, duplication, build, and test checks, with manual review of media parsing, download recovery, cancellation, resource ownership, UI lifetimes, native rendering and protocols, and packaging scripts.

The fixes address these concrete failures:

- Offline downloads rejected completed playlists when `EXT-X-ENDLIST` appeared before the final segment or when `EXT-X-TARGETDURATION` appeared after media. The parser now treats completion as a playlist property and checks for unfinished segments after reading the complete input. It rejects duplicate or malformed completion markers.
- Offline downloads rejected valid byte ranges placed before `EXTINF`. The range now stays pending until the next segment URI. Existing bounds and implicit-offset validation still apply. Twitch and Kick integration regressions verify the requested ranges, downloaded bytes, and playable local playlist.
- Long-VOD seek rewriting could discard an early completion marker or global playback tags. The rewritten playlist now collects global playback headers from the whole input, declares one VOD playlist type, and ends with one completion marker.
- Seek previews accepted repeated duration tags and unfinished final segments, allowing an ambiguous timeline. Those playlists now fail validation.
- Duration readers disagreed about accepted numeric notation. A shared span-based parser handles segment durations in the offline downloader, published-duration reader, seek rewriter, live preview reader, and seek preview reader. It preserves fractional precision, enforces each caller's limit, and rejects signs, exponents, whitespace, and embedded control characters in durations.

`EXT-X-ENDLIST` may occur anywhere in a media playlist; target duration applies to the playlist, and a byte range applies to the next segment URI. These placement rules informed the fixes. [RFC 8216](https://www.rfc-editor.org/rfc/rfc8216.html)

The two Home refresh features now share timer creation and shutdown in `HomeFeatureViewModel`. Their intervals, service checks, locking, and disposal behavior are preserved. This removes the duplicated timer implementations. Repeated duration parsing and obsolete end-marker validation branches were also removed.

Unused-member candidates were checked against XAML bindings, attached properties, installer calls, legacy compiler branches, extension methods, native layouts, and test access. No additional dead declarations were confirmed safe to remove. The scan identified two imports needed by the legacy PowerShell compiler, so they remain.

Validation:

- Baseline comprehensive check: 1,824 passed, 274 interactive tests skipped.
- New regression tests: the initial run reproduced 10 failures among 11 tests; all 11 pass after the fixes.
- C# syntax and duplication scans: 401 production files, no syntax errors, no exact method duplicates of at least 50 tokens, and no identifier-normalized duplicates of at least 60 tokens after the timer refactor.
- Cross-language inventory and checks: 525 source/configuration files, no detected parse errors or unused Python imports, PowerShell functions, or native static functions. Six embedded or standalone JavaScript programs pass Node syntax validation.
- Native overlay, compositor, subpicture/pipe receipt, and hardware GDI suites pass, including resource cleanup and BT.601/BT.709 full/limited color checks.
- Eight Python tests and eleven release-publication fixture scenarios pass.
- Final `scripts/dev.ps1 Check` passes: locked restore, PowerShell syntax, solution formatting, tooling fixtures, native provenance, Release build with warnings as errors, and 1,835 passing tests. The build reports zero warnings and errors; 274 interactive desktop tests are skipped in headless mode.
- Final semantic reference analysis covers 647 C# source files with no workspace or project errors. Its unused-member candidates match the starting scan; no new unused members are introduced.
