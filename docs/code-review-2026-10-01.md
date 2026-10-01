# Code review — October 1, 2026

Reviewed the repository with source and reference scans, focused inspection, compilation, and managed, native, and tooling tests. The scan covered 586 C# files, plus the XAML, native overlay code and VLC patches, PowerShell and Python scripts, build files, release tooling, and workflows. Existing uncommitted work was captured before changes and preserved; bundled native binaries were not changed.

## Bugs fixed

| Area | Problem | Result |
| --- | --- | --- |
| Playback cleanup | A throwing logger or display-name callback prevented completed cleanup tasks from leaving the shutdown queue. | Error reporting cannot strand cleanup or fault its unobserved continuation. |
| Direct Twitch VOD playback | The fast resolver bypassed custom proxy, certificate, and authentication environment settings. | Direct VOD and live-preview resolution share the same Streamlink configuration checks and preserve those settings by using Streamlink. |
| Replay seek commits | Ending the seek preview allowed a clock refresh to replace the slider value before the requested seek was captured. | The bounded requested timestamp is captured before notifications and passed directly to the seek operation. Two existing tests also use their intended timestamp directly. |
| Chat links | Every unmatched trailing delimiter rescanned the whole URL twice. | Delimiters are counted once and adjusted while trimming, preserving balanced URLs without quadratic work. |
| Measurement execution | Python optimization removed assertions containing playback, environment, pipe-write, and statistics calls. | Native calls and input validation always execute. Partial VLC initialization is cleaned up on failure. |
| Measurement summaries | Missing stream counters could pass validation; PowerShell UTF-8 BOM files and zero resource baselines caused failures. | Shared readers accept both UTF-8 encodings, require complete stream results, and report undefined percentage reductions as unavailable. Controlled trials also require sufficient samples and stable, nonempty process sets. |
| Development and CI checks | The skip ceiling was stale, so a successful headless run failed the command. | The shared ceiling allows the audited 274 desktop skips and two additional decoder skips when VLC is absent. CI uses the development runner and runs the Python regression tests. |

## Reusable code and removals

- Added `StreamlinkConfigurationPolicy` for configuration files, custom plugins, HTTP environment settings, and netrc detection, replacing the duplicated direct-resolution policies.
- Consolidated Twitch credential resolution across categories, streams, and category viewer counts. Removed the redundant asynchronous request-forwarding wrapper.
- Extracted `ResetReplayControls` for the repeated replay cancellation and control-reset operations.
- Added `measurement_helpers.py` for validation, JSON decoding, stream-result completeness, percentage calculation, and formatting.
- Removed the obsolete URL counting helper and replaced the duplicated configuration, reset, and measurement logic.

The final reference scan found nine apparent unused members. They are required WPF overrides, attached-property setters, or COM vtable slots and were retained. Compiler and analyzer checks found no unused-code warnings.

## Verification

| Check | Result |
| --- | --- |
| Release solution build with warnings treated as errors | Passed; zero warnings and errors. |
| UpdateProbe project build | Passed; zero warnings and errors. |
| Full C# headless suite | All 1,544 tests passed; 274 desktop-dependent tests skipped. No failures, timeouts, or unrun selected tests. |
| Full solution formatting verification | Passed with no changes required. |
| Python measurement regression tests | All five passed, including optimized execution, missing counters, BOM input, and zero baselines. |
| PowerShell tooling tests | 51 passing groups covering development, release contracts, mocked publication, installer lifecycle, and native provenance. |
| Native tests | Eight test executables passed, covering network parsing, overlay rendering and resources, subpictures, compositor behavior, and hardware GDI colors, fallback, and cleanup. |
| Native builds | Chat overlay/controller and replay-pause builds passed with warnings treated as errors. Outputs stayed in the temporary review directory. |
| Source and configuration parsing | 37 PowerShell scripts, seven Python files, 33 JSON files, 33 XML files, and three YAML workflows passed. |
| NuGet vulnerability audit | No known vulnerable direct or transitive packages reported by the configured source for the six solution projects. |
| Patch whitespace | `git diff --check` passed. |

The cleanup, HTTP environment, and replay commit regressions were reproduced before their fixes. The measurement regressions also failed before the script fixes and passed afterward.

Desktop-dependent UI tests require an interactive Windows desktop and were skipped in this run. The opt-in visible-window CPU benchmark was inspected but not executed. The full vendored VLC core/adaptive source archives were not rebuilt; native component builds, runtime tests, and pinned dependency verification were completed.

The initial snapshot, review-only patch, scans, and verification logs are in the ignored `.tmp/code-review-2026-10-01/` directory. `review-only.patch` isolates this review from the work already present when it began.
