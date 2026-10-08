**Code review - October 7, 2026, additional pass**

The review combined repository-wide syntax, duplicate-method, reference, and unused-code scans with manual checks of parsing, HTTP and process boundaries, resource ownership, playback and replay, chat, downloads, settings, installation, and release tooling. The starting snapshot contains 933 authored files and fixtures. Existing uncommitted work was preserved and the review changes were compared with that snapshot.

Three new regressions failed against the starting implementations. They now pass in [CodeCleanupTestCatalog.ReleaseNotes](../tests/StreamlinkVlcStudio.Tests/CodeCleanupTestCatalog.ReleaseNotes.cs).

| Finding | Correction |
| --- | --- |
| Release-note splitting toggled code state on any line beginning with three backticks. Tilde fences, shorter nested fences, and apparent closing fences with trailing text could hide real updating instructions or extract sample text as instructions. | [ReleaseNotes](../src/StreamlinkVlcStudio.App.Wpf/Services/ReleaseNotesCatalog.cs) uses the shared [MarkdownCodeFence](../src/StreamlinkVlcStudio.App.Wpf/Services/MarkdownCodeFence.cs) state machine. It tracks marker type, opening length, valid closing lines, and unclosed samples. |
| The changelog renderer recognized only backticks and could interpret fenced sample headings, emphasis, and links as regular Markdown. | [ReleaseNotesText](../src/StreamlinkVlcStudio.App.Wpf/Controls/ReleaseNotesText.cs) uses the same fence rules and renders the entire sample as literal text. |
| Leading blank code lines disappeared because newline insertion depended on the accumulated text length. Section trimming also removed opening indentation and trailing blanks inside unclosed samples. | The renderer joins recorded code lines and removes only the opening fence's permitted indentation. Shared section joining preserves indentation and blank lines within code. |

Fence recognition and indentation follow the [CommonMark fenced-code rules](https://spec.commonmark.org/0.31.2/#fenced-code-blocks). The regression cases cover both marker types, longer closing fences, shorter nested fences, trailing text, invalid openings, unclosed samples, and whitespace through both release-note sections and the rendered document.

Reuse and removal:

- Removed `CatalogLoadCoordinator.RaiseSafely` and its separate subscriber loop. [CatalogChangeNotifier](../src/StreamlinkVlcStudio.App.Wpf/Chat/CatalogChangeNotifier.cs) now uses [SafeEventDispatcher](../src/StreamlinkVlcStudio.Infrastructure/Chat/SafeEventDispatcher.cs), extended with an `EventHandler` adapter and optional logging. Subscriber failures remain isolated and the shared dispatcher avoids allocating an invocation-list array.
- The existing catalog notification regression now exercises the notifier itself, including a throwing subscriber followed by a successful one.
- Removed redundant imports from the new partial test file, using the test project's existing global imports.
- Reference candidates used by XAML, native and COM layouts, reflection, interfaces, or regression diagnostics were retained. The standalone dependency probe's two explicit imports remain necessary for PowerShell compilation.

| Validation | Result |
| --- | --- |
| Starting `scripts/dev.ps1 Check` | Passed: 1,722 tests passed; 275 desktop tests skipped. Release build: zero warnings and errors. |
| New regressions before fixes | Three failed; zero passed, skipped, or timed out. |
| Cleanup suite after fixes | 27 passed; zero skipped. Release build: zero warnings and errors. |
| Final `scripts/dev.ps1 Check` | Passed: **1,725 tests passed; 275 desktop tests skipped**. Locked restore, PowerShell syntax, complete solution formatting, tooling contracts, and native provenance passed. Release build: **zero warnings and errors**. |
| Release build after import cleanup | Passed with zero warnings and errors. |
| Python measurement tooling | Eight tests passed. |
| Release publication fixtures | Eleven cases passed using a fake CLI and temporary assets. |
| Native overlay, compositor, subpicture/received-frame, and hardware GDI fixtures | All passed with compiler warnings treated as errors. VLC-dependent fixtures used the current bundled core; hardware checks retained no GDI handles. |
| Production C# syntax and duplicate scan | 399 C# files, including standalone probes; zero syntax diagnostics. Three duplicate groups contain only small adapters below 50 tokens. |
| Solution reference audit | 631 C# files; zero workspace or project errors. Five import candidates were checked: three redundant test imports removed, two standalone-probe imports retained. |
| Script and native reference scans, Python syntax, XML/XAML and JSON parsing | No unused-function/import candidates or parsing errors. MPEG transport-stream fixtures were excluded from text parsing. |
| Starting-snapshot comparison | Nine intended code/test files and this report. Existing unrelated edits preserved. `git diff --check` passed. |

The 275 desktop-dependent cases require an interactive session. Headless checks do not exercise complete interactive UI behavior or authenticated live-provider behavior. Review snapshots, regression logs, native logs, audit output, and the review-only diff are in `.tmp/codex-code-review-20261007/` at the repository root.
