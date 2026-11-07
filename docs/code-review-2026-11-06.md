# Code review follow-up — 2026-11-06

This pass combined repository-wide syntax, unused-member, and duplication checks with targeted review of parsing, settings/history, chat, discovery, downloads, browser workspaces, playback cleanup, account handling, native overlays, and build/update tooling. Authored application and test sources were included; generated output, downloaded toolchains, and third-party minified assets were excluded. Existing uncommitted changes were preserved.

## Fixes

- **Sending browser chat could erase the next draft.** A successful request unconditionally cleared the input, including text entered while that request was pending. The input now tracks edits and clears only the submitted draft. This also preserves a newly entered draft identical to the previous message. Repeated submits share one pending operation, failed sends keep the draft and allow retry, and stopped or closed players cannot send through a released server player.
- **Browser shortcut recording and server validation disagreed.** The browser accepts one Unicode character, while the server counted UTF-16 code units and rejected supplementary characters. Validation now accepts a single Unicode scalar and applies the browser's character-category exclusions and exact function-key syntax. Regression coverage saves and reopens ordinary Unicode, supplementary letters, symbols, and function keys; malformed keys are rejected.

## Reuse and cleanup

- Chat, prediction creation, and opening streams share one asynchronous form-submission helper, including pending-operation protection, button state, accessibility state, and cleanup after failure.
- Five separate implementations for finding the repository root now use `TestRepository.FindRoot`. The superseded implementations were removed, and all callers were updated.
- No additional unused production members were confirmed for deletion. Framework callbacks, native/COM layout members, and independent reference implementations used for comparison tests were retained.

## Verification

- `scripts/dev.ps1 Check -NoRestore` passed: full-solution formatting, PowerShell syntax/tooling, native dependency verification, and the Release build with zero warnings or errors.
- Final managed suite: 2,136 passed; 276 interactive-desktop tests skipped. No failures, timeouts, or unexecuted cases.
- Final JavaScript browser/worker/OAuth fixtures: 90 passed, including four new chat regression tests.
- Python measurement-tool tests: 8 passed.
- Native fixtures: all 8 programs passed with compiler warnings treated as errors; rendering and hardware checks retained no additional GDI handles.
- PowerShell tooling and release-publication fixtures passed.
- Syntax checks: 765 authored C# files, 28 JavaScript files, 7 Python files, and all 45 PowerShell scripts passed. The C# count includes production and test sources.
- `git diff --check` passed.

The initial restricted Windows run could not access profile storage, DPAPI, certificates, and some browser facilities; those cases passed in the full run with normal Windows access. A rebuild overlapped that run and encountered locked output files; final checks run the build and tests sequentially. Live provider accounts and interactive desktop input were not exercised.

Logs, the initial working-tree patch, original copies of edited files, and an isolated diff for this pass are preserved in the ignored `.tmp/review-followup` directory.
