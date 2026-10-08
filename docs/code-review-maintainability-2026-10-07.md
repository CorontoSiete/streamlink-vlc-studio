# Code review and maintainability fixes (2026-10-07)

The review found chat recovery failures, diagnostic failures that interrupted HTTP retries and fallback, and integer overflow in native animated-emote timing. The fixes reuse existing cancellation and logging utilities, centralize chat disconnection, and remove three redundant forwarding methods. Existing workspace edits were preserved against a snapshot taken before this review.

| Finding | Resulting behavior | Regression evidence |
| --- | --- | --- |
| Twitch IRC and Kick WebSocket disconnection could stop recovery when a cancellation callback or optional diagnostic threw. | Shared cancellation and logging utilities preserve state reset, Twitch prediction cleanup, and notification of the reconnect supervisor. | Four recovery regressions cover both clients; two failed in the initial managed run and two additional Kick cases failed in the isolated probe before the fix. |
| Twitch and Kick repeated the same disconnect lifecycle-gate logic. | Both clients use one helper that rejects newly submitted requests once disposal starts, allows previously admitted cleanup requests to drain, and releases the gate on every exit. | Two concurrency tests protect queued cleanup and rejection of new requests; the existing concurrent Kick send/disconnect/disposal test is preserved. |
| Twitch browse retry logging could throw before the rate-limited response was disposed, interrupting the retry and leaking the response. | Optional diagnostics use the existing safe logging helper; retry responses are released before waiting and sending again. | A new test verifies retry success, request count, and disposal of the first response body with a failing logger. |
| Kick website diagnostics could interrupt direct-HTTP fallback or turn malformed fallback content into an exception. | The shared reader safely reports HTTP failures, timeouts, unreadable or malformed payloads, and curl failures while preserving fallback and cancellation behavior. | Five new tests cover failed diagnostics during HTTP error, timeout, connection failure, malformed direct content, and malformed fallback content. |
| Native GIF delay conversion, total duration, and frame selection used 32-bit arithmetic that overflowed for long or malformed delays. | Delay conversion saturates at the supported per-frame maximum; loop duration, elapsed position, and frame boundaries use 64-bit arithmetic. | A real GDI+ GIF-metadata regression failed before the fix and passes afterward, covering long boundaries, looping, and overflowing delay metadata. |
| The existing Kick VOD-completion test assumed one nonblocking completion sample must finish immediately. | The test holds the playback transition gate before exposing EOF, verifies completion defers while the gate is held, then waits for the normal completion sampler. | The initial full check reproduced the failure; the corrected completion tests and final suite pass. |

The duplicate Twitch/Kick lifecycle-gate implementation now lives in `ChatConnectionCleanup.DisconnectAsync`. Existing `CancellationSourceCleanup.Cancel` and `AppLoggerExtensions.WriteSafely` provide cancellation and diagnostic isolation. Both chat clients now use safe logging for optional diagnostics, including startup cleanup, token-validation/read-only fallback, prediction setup, channel lookup, token refresh, and history backfill. The new recovery tests also share supervisor setup. Removed forwarding methods are `KickChatClient.ResolveSendTokenAsync`, `StreamInputParser.IsKnownNonChannelPath`, and the parameterless `StreamTabViewModel.BeginReplaySeekPreview` overload. Callers use the existing shared operations directly.

The rebuilt `vlc_chat_overlay.exe` is included in the bundled overlay directory, and `dependencies/native-overlay.json` records its new length, SHA-256, and timing fix. The unchanged overlay plugin was retained.

Review coverage includes production C#, shared code, native overlay and VLC integration, PowerShell/Python tooling, XAML, configuration, workflows, and embedded browser JavaScript. Roslyn syntax and normalized method-body scans covered 401 production C# files and 92,148 lines; semantic analysis covered 645 authored solution C# files. Manual checks concentrated on cancellation, reconnects, disposal, bounded HTTP/process reads, playback transitions, native ownership, and release tooling.

The final duplicate scan found one group of short typed adapters in `SafeEventDispatcher`; all three already call the same implementation. No duplicate method-body groups of 50 or more normalized tokens remain. Reference candidates were checked against interface implementations, extension-method invocation, native/COM layouts, XAML bindings, generated code, and test usage before removal. Layout fields, interface contracts, and standalone PowerShell `Add-Type` imports were retained where required.

| Validation | Result |
| --- | --- |
| Initial full project check | 1,787 passed, 1 failed, 274 skipped; reproduced the VOD-completion test timing assumption. |
| New bug regressions against starting code | Eight failed in the initial managed run; two additional Kick recovery cases failed in the isolated probe before the fix. |
| New managed regressions after fixes | All 12 passed, including two tests preserving the existing disconnection/disposal contract. |
| Final `scripts/dev.ps1 Check` | 1,800 passed, 274 skipped; zero build warnings or errors. Locked restore, formatting, PowerShell syntax, tooling fixtures, and native provenance checks passed. |
| Native controller build | GCC warning-as-error compilation and dependency/import checks passed. |
| Native overlay regression suite | Passed rendering equivalence, animation timing, cancellation/reconnect, generation ownership, buffer growth, and GDI-resource checks. |
| Native compositor, subpicture/pipe, and hardware suites | Passed with the pinned bundled VLC core and installed VLC 3.0.23 plugins, including BT.601/709 full/limited color cases and fallback/lifetime checks. |
| Python measurement-summary tests | All 8 passed. |
| Release-publication fixture tests | All 11 scenario groups passed using the fake release API. |
| Cross-language/configuration scan | 520 production files checked; no parse errors or unused Python imports, PowerShell function candidates, or native static-function candidates. |
| Embedded JavaScript syntax | All 5 extracted snippets passed `node --check` with fixture substitutions for C# interpolation. |
| C# syntax/reference audit | No syntax, workspace, or project compilation errors. |
| Whitespace check | `git diff --check` passed. |

274 interactive desktop tests were skipped by the headless test mode. Authenticated live Twitch/Kick sessions and release publication were not exercised.

Local evidence, the pre-edit snapshot, and an isolated review diff are stored outside the repository at `../../.tmp/review-oct07-maintainability/`. Relevant files include `baseline-manifest.json`, `baseline-check.log`, `regressions-before.log`, `regressions-after.log`, `kick-recovery-before.log`, `kick-recovery-after.log`, `native-regression-before.log`, `native-regression-after.log`, `final-check.log`, `source-final.json`, `semantic-final.json`, `cross-language-audit.json`, `changed-files.json`, and `review-only.diff`.
