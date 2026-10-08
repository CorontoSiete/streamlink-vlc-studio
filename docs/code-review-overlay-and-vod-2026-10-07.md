**Repository review: overlay cancellation, VOD progress, and CI inputs - October 7, 2026**

This pass fixes interrupted overlay cleanup, lost VOD progress at accelerated playback speeds, diagnostic failures that disrupt bookmark recovery, prepared replay resource leaks, and CI metadata interpreted as PowerShell code. It adds fifteen managed regression cases and four workflow checks, preserving the existing uncommitted work against a starting snapshot of 954 authored files.

The review combined repository-wide syntax, compilation, reference, duplicate-method, configuration, script, and native checks with manual inspection of cancellation, task ownership, file persistence, HTTP boundaries, playback transitions, and cleanup. It covered the WPF application, Core, infrastructure, bootstrapper, maintenance, shared code, native modules, scripts, workflows, and tests.

| Finding | Correction |
| --- | --- |
| Frame-write disposal published its completion task before cancellation, but a throwing callback prevented cleanup from starting. Invalidation and replacement of critical clear frames could fail similarly. | Reuse `CancellationSourceCleanup` for lifetime and active writes. Disposal still drains the writer, concurrent and reentrant disposal share one task, and replacements continue after callback failures. |
| A failed event-listener cancellation callback prevented `StopAsync` from joining the listener and could prevent restart. | Reuse the same cancellation helper while retaining listener ownership of source disposal. |
| Seek-image cancellation could retain an obsolete request and skip clearing the image and closing the preview. | Detach the old request before invoking safe cancellation. The image-loading request continues to own its eventual source disposal. |
| VOD bookmark validation assumed normal playback speed. After a polling interruption, valid progress at 2x speed could be rejected indefinitely; lowering speed just before a sample could also lose progress. | Reuse the replay clock's elapsed-time scaling and overflow-safe addition. Bound observed progress by the maximum supported speed, including earlier speed selections, while retaining the rejection of impossible jumps and backward resets. Save the actual decoder clock. |
| Failed bookmark read, capture, completion, or save diagnostics could change playback recovery or strand the seek worker. | Reuse `WriteSafely` so diagnostics cannot change those outcomes. Failed reads retain the protection against overwriting unknown history; failed saves retain progress for retry. |
| Prepared replay cancellation could skip its cleanup continuation. Source-disposal failures could skip cancellation-source disposal, and worker faults were discarded without observation. A failed input cleanup could also skip releasing the retained native instance. | Safely cancel, then drain and dispose on a background task outside the player lock. Observe worker failures, isolate diagnostics, and use `finally` for cancellation-source and retained-instance release. Success diagnostics also preserve already-confirmed playback. |
| GitHub refs and other context values were interpolated into PowerShell source. A legal Git tag containing an expression was evaluated before stable-tag validation. | Use GitHub's `GITHUB_*` environment variables in run scripts. Reject malformed stable tags before they can affect metadata, preserve branch and valid-tag behavior, and check every workflow's embedded PowerShell. |

Passing inline-script input through environment variables follows [GitHub's script-injection guidance](https://docs.github.com/en/actions/reference/security/secure-use#use-an-intermediate-environment-variable). The names used here are listed in the [default environment-variable reference](https://docs.github.com/en/actions/reference/workflows-and-actions/variables#default-environment-variables).

Reuse and removal:

- Removed the redundant frame-write `CancelActiveWrite` helper and event-host cancellation catch block in favor of the shared helper.
- Removed the unused `MainWindow.GetFullscreenButtonMode` forwarding method. Its existing regression now reaches the active window-mode controller through the shared test helper and runs without opening a desktop window.
- Clock scaling and safe logging reuse existing implementations. New regressions reuse VOD fixtures, private-state access, logging-failure injection, and controlled task completion.
- Workflow regressions share one script extractor and invoke the actual version-resolution step with controlled inputs. The checks run through the existing tooling suite.
- Checked reference candidates against XAML, reflection, interfaces, extension methods, script entry points, and native/COM layouts. The two import candidates in `WindowsDependencyProbe.cs` are necessary when PowerShell compiles that file independently.

| Validation | Result |
| --- | --- |
| Initial `scripts/dev.ps1 Check -ExpectedMaxSkips 275` | Passed: 1,772 tests passed; 275 interactive desktop cases skipped. Release build had zero warnings and errors. |
| Managed cases before production fixes | Fourteen failures reproduced; the guard against impossible VOD progress already passed. |
| Focused managed cases after production fixes | Fifteen passed; zero skipped. |
| Full `scripts/dev.ps1 Check -ExpectedMaxSkips 275` | Passed: **1,788 tests passed; 274 interactive desktop cases skipped**. Locked restore, PowerShell syntax, solution formatting, tooling contracts, native provenance, and Release build passed with zero warnings and errors. The existing fullscreen-mode test now runs headlessly. |
| Additional workflow review | Expression evaluation reproduced before the CI fix. Four workflow checks passed afterward in Windows PowerShell and in the integrated PowerShell 7 tooling suite. All three workflow YAML files and their embedded PowerShell parsed successfully. |
| Late forwarding-method cleanup | Targeted formatting verification passed for MainWindow and its updated test. |
| Python measurement tooling | Eight tests passed. |
| Release publication fixtures | Eleven cases passed with temporary assets and a fake CLI. |
| Native overlay, compositor, received-frame/subpicture, and hardware GDI fixtures | All passed with compiler warnings treated as errors. Hardware fixtures retained no GDI handles. |
| JavaScript | `node --check` passed for channel-points automation. |
| Final production C# audit | 401 files and 92,160 lines; zero syntax diagnostics. No exact duplicate method groups at or above 50 tokens. Two smaller groups are short adapters to shared implementations. |
| Solution reference audit | Zero workspace or project compilation errors. |
| Cross-language audit | Python, XML/XAML/project/configuration, JSON, and workflow YAML parsing passed. No unused Python-import, PowerShell-function, or native static-function candidates. All PowerShell files parsed again after adding the workflow checks. |
| Starting-snapshot comparison | Sixteen intended source/test/workflow files plus this report. Existing unrelated edits preserved. `git diff --check` passed. |

Interactive desktop cases and authenticated live-provider behavior were not exercised. Review snapshots, isolated diffs, regression logs, native logs, and audit output are in `.tmp/review-current-oct07/` relative to the repository root.
