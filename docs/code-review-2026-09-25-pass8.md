# Repository review — September 25, 2026, pass 8

This pass preserves the changes that were already present in the working tree.

## Fixes and reuse

- Dependency discovery skips a path containing only a quote instead of throwing while removing the quotes. A malformed PATH entry no longer prevents discovery in later entries. The same normalization is used for the Streamlink and VLC environment overrides.
- Stop and Pause capture the tab they operate on. Switching to Home while either command waits no longer throws a null-reference exception, and completion no longer overwrites another tab's status.
- Async commands created by the main view model reject new work after disposal begins. Clip creation receives the window lifetime token and checks it before opening the browser, including when a provider returns a result after cancellation.
- Account authorization receives the window lifetime token through authorization, optional account lookup, and settings persistence. Late authorization results are checked before applying them. Both OAuth entry points honor an already canceled request before opening a listener or browser.
- Kick username lookup is optional and now handles HTTP deadlines as lookup failures. A valid authorization can still be saved by Settings or the setup wizard. Caller cancellation continues to propagate. Both callers use the same lookup/error handling, removing their duplicate exception blocks.
- Play and Reload share one implementation; the two artificial async/completed-task bodies are gone. Kick website direct reads no longer go through a redundant private forwarding method.

Eleven focused regressions cover these paths. Six failed against the original code; two additional tests reproduced the Stop/Pause null-reference errors before those fixes. All eleven pass after the changes.

## Review and validation

Repository-wide build, analyzer, reference, duplicate-block, and script/configuration checks were combined with targeted inspection of core parsing and settings, HTTP and process boundaries, chat and replay, UI commands and lifetimes, native integration, and installer/update cleanup. Generated files and third-party binaries were excluded from refactoring. Remaining unreferenced-name candidates are required WPF/framework entry points and native structure fields.

- Complete headless suite: **993 passed, 239 expected desktop-only skips, zero failures or timeouts**. The skip ceiling was enforced.
- Release solution and separate update-probe builds: zero warnings or errors, with warnings treated as errors.
- Locked restore with transitive NuGet auditing passed; dependencies are unchanged.
- Whole-solution formatting and warning-level analyzer verification passed.
- All 14 PowerShell tooling tests passed. Syntax validation covered 22 PowerShell scripts, 29 XML files, and seven JSON files (package lock files excluded from the syntax scan).
- Both pinned native overlay inputs passed verification.

Validation logs and the before snapshot are in `artifacts/logs/review-pass8/`. This review does not establish that every possible defect has been eliminated. Interactive desktop tests, authenticated platform operations, real installation/removal, and rebuilding the native C plugin were not exercised.
