**Code review — October 5, 2026**

This review covered the Core, infrastructure, WPF application, bootstrapper, maintenance utility, native modules, scripts, configuration, and tests. Automated source and reference scans included 732 files; syntax and symbol checks parsed 610 C# files. Manual review focused on parsing, bounded I/O, cancellation and cleanup, chat and provider requests, VOD discovery and downloads, updates and installation, UI input and ownership, and measurement validation. Generated outputs and bundled binary dependencies were checked through the build and provenance checks.

The starting working tree already contained installation, browse, artwork, VOD discovery, and UI changes. These were preserved. The changes below were applied on top of that baseline.

| Finding | Fix and regression evidence |
| --- | --- |
| Twitch IRC messages were labeled with the requested channel without checking the actual `PRIVMSG` target. | [TwitchIrcParser](../src/StreamlinkVlcStudio.Core/Parsing/TwitchIrcParser.cs) now requires one matching channel target, ignoring casing. The new regression rejects another channel, private-message targets, multiple targets, malformed targets, and channel-name prefixes, and accepts matching mixed case. It failed before the fix and passes afterward. |
| Measurement JSON could contain finite-looking numbers such as `1e999` that became infinity when parsed. Valid finite inputs could also overflow during calculation and produce non-standard summary JSON. | [measurement_helpers.py](../scripts/measurement_helpers.py) validates parsed floating-point values. Both summary writers reject non-finite calculated values. CLI regressions reproduced both paths before the fixes and now pass. |
| Controlled multistream trials reported audio loss while still being accepted as valid comparisons. | [summarize-multistream.py](../scripts/summarize-multistream.py) requires zero lost audio buffers, matching the live-trial validation. A trial with audio loss now fails before writing a new summary. |
| Native test scripts stopped on successful stderr diagnostics when Windows PowerShell redirected output to a log. | The shared [native-test.ps1](../scripts/lib/native-test.ps1) preserves stdout and stderr, including empty lines, and checks the executable exit code. All five native test scripts use it. Regressions cover successful diagnostics, nonzero exit status, and missing executables with an earlier exit code still present. |

The IRC target validation follows the [Twitch IRC message format](https://dev.twitch.tv/docs/chat/irc/). The native command handling addresses the redirected stderr behavior documented in Microsoft's [PowerShell preference documentation](https://learn.microsoft.com/en-ie/powershell/module/microsoft.powershell.core/about/about_preference_variables?view=powershell-7.5).

The cleanup consolidates optional bounded HTTP and stream reads through one result handler, and places visible video/VOD volume targeting in [VolumeOverlay](../src/StreamlinkVlcStudio.App.Wpf/Controls/VolumeOverlay.xaml.cs). The main and detached windows now use that shared resolver; their obsolete private resolver methods and repeated bounded-read exception paths were removed. A formatting error in the existing Kick category test was also corrected.

The reference audit found no additional ordinary production symbols that could be safely removed. Native structure fields, COM interface slots, WPF overrides and attached-property accessors remain required by layout or framework invocation. The dependency probe's application verification entry point is invoked by PowerShell installation and runtime checks.

| Validation | Result |
| --- | --- |
| `scripts/dev.ps1 Check`, PowerShell 7 | Passed: locked restore, PowerShell syntax, formatting, tooling contracts, bundled native provenance, Release build, and the full headless regression suite. Build: zero warnings and zero errors. Tests: **1,638 passed; 275 skipped**. |
| `scripts/tests/tooling.tests.ps1`, Windows PowerShell 5.1 | Passed. The native-command regressions also passed separately under both PowerShell versions with redirected logs. |
| `python -m unittest discover -s scripts/tests -p 'test_*.py'` | **8 passed**, including valid values, UTF-8 BOM input, optimized Python validation, missing counters, zero baselines, numeric overflow, calculated overflow, and lost audio. |
| Native overlay, render resources, compositor, subpictures, received frames, and hardware GDI tests | Passed with warnings treated as errors. Covered TLS and network failures, cached/fresh pixel equivalence, pipe reconnects, buffer bounds, ownership, resource cleanup, and all four BT.601/BT.709 range combinations. Standalone render filter and benchmark arguments were also exercised. |
| Production native overlay/controller and replay-pause builds | Passed into isolated `.tmp/code-review-native-build` outputs; runtime import checks passed. |
| Release publication contracts | **11 fixture tests passed**, using the test GitHub CLI stub. |
| Source/reference scan and `git diff --check` | Passed; no whitespace errors or additional confirmed dead production code. |

The 275 skipped cases require an interactive desktop. Live provider and full interactive UI behavior remain outside this headless run. Detailed local logs are in `.tmp/code-review-final-check.log`, `.tmp/code-review-python-tests.log`, `.tmp/code-review-tooling-ps51.log`, and the `.tmp/code-review-native-*.log` / `.tmp/code-review-test-*.log` files.
