# Code review — 2026-11-02

## Scope

Repository-wide source inventory, reference and unused-member checks, formatting, build and regression testing, with focused manual review of parsing, persistence, HTTP/media handling, downloads, request coordination, playback transitions, input handling, native integration and developer tooling. Existing uncommitted changes were preserved. Bundled third-party sources and pinned native inputs were retained.

## Bugs fixed

- **Stop could lose the latest volume.** Stop now captures volume and VOD progress before releasing media, finishes both saves before closing the server player, and still releases playback if a save fails. Delayed volume events from stopped players cannot save through a released player.
- **Failed volume saves were forgotten, and overlapping saves could acknowledge newer edits.** A volume change stays dirty until that edit's save succeeds; a failed preliminary reload save can therefore be retried without changing the slider again. Revision checks preserve the latest change even when the slider returns to an earlier level while requests are pending. Closing an already stopped tab does not retry against its released server player.
- **Refreshing a VOD could keep stale playback metadata.** Existing library keys now receive the refreshed title, duration, playback URL and chat metadata even when the provider omits the profile picture. The previous picture is retained when a refresh omits it.
- **Double-click detection used a rectangle's full size as a radius.** The shared video/window handler now tests the actual centered Windows rectangle, including its edge behavior. Coordinate comparisons also avoid integer subtraction overflow. The Windows behavior is described in [Mouse Input Overview](https://learn.microsoft.com/windows/win32/inputdev/about-mouse-input#double-click-messages) and [GetSystemMetrics](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getsystemmetrics).

## Reuse and cleanup

- Shared player-state saving between stop, close and reload; removed the repeated save/error paths.
- Derived browser quality choices from `QualityOption.Defaults` and exposed a read-only list, removing the duplicate list and preventing accidental mutation.
- Removed the unused `.heading-dot` CSS rule after checking markup, scripts and browser tests. Retained the dynamically generated provider-logo styles.
- Extended existing regression fixtures and catalogs for volume persistence, failure/retry behavior, VOD refresh, immutable quality choices and double-click boundaries.

## Validation

- Release solution build: **zero warnings and zero errors**.
- Full headless-safe Windows suite: **2,386 selected; 2,110 passed; 276 interactive desktop skips; zero failures, timeouts or unrun tests**.
- After the final volume-save refinement, rebuilt the embedded browser assets and reran the entire affected browser-server group: **40 passed, zero skipped**. This includes real browser playback, account switching, volume persistence, chat, fullscreen, responsive navigation and mobile layouts.
- Deterministic JavaScript/account-worker suite: **65 passed**, including three new volume regressions and the extended reload-retry assertion.
- Python measurement-tool tests: **8 passed**.
- All eight PowerShell/tooling regression suites, PowerShell syntax checks and pinned native dependency verification passed.
- Repository formatting and unused-member checks passed; changed C# files also passed whitespace verification. JavaScript syntax and `git diff --check` passed. Pre-existing changes in untouched files were compared with the saved baseline and preserved.

The initial restricted Windows run could not use credential storage, certificate keys, native cache folders and browser rendering, and stopped at the damaged-executable fixture. Verification was repeated with normal Windows access and the machine's standard tool paths. NuGet's online advisory feed was unavailable; auditing was disabled only in the verification processes, with locked dependency versions and repository audit defaults preserved. Interactive desktop input tests are outside the headless run.
