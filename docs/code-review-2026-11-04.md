# Code review — 2026-11-04

Repository-wide syntax, unused-member, and duplication checks were combined with targeted review of playback, chat, downloads, settings, browser workspaces, account handling, and installation/update code. The review covered authored managed, native, browser, hosting, and build-script sources. Generated output, downloaded tools, and third-party minified assets were excluded. Existing uncommitted work was preserved.

## Fixes

- **Overlapping language saves could lose the latest choice.** If the saved language was English, a queued change to French followed by English could discard the last request because it matched the previously committed value. The comparison now runs inside the shared serialized save operation. Unchanged account snapshots skip serialization and disk writes. Regression coverage queues both requests, checks memory and a reopened store, and verifies that an unchanged request succeeds while the account file denies replacement.
- **The pinned build tools used a vulnerable runtime/compiler package.** The initial build failed its dependency audit for `Microsoft.DotNet.ILCompiler` 10.0.10. The SDK is now pinned to 10.0.403, which selects the patched 10.0.14 runtime and native compiler. See the [Microsoft advisory](https://github.com/dotnet/runtime/security/advisories/GHSA-p3jp-9v83-76w9) and [official release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json).
- **Release version settings now have shared sources.** Release metadata reads the SDK version from `global.json`; its runtime version and the CI checks share `release-contract.json`. CI also checks the ASP.NET runtime bundled for the browser server. The setup instructions point to the SDK pin instead of repeating a version that can become stale. Dependency lock files were refreshed.
- **Native builds could fail with a long machine search path.** The installer and uninstaller tests exposed a real NativeAOT build failure: stale search-path entries caused Microsoft's compiler setup batch file to exceed the Windows command-length limit. A shared helper now removes missing and duplicate directories for the native publish, preserving search order and restoring the original path in `finally`. Tooling coverage verifies directory precedence and quoted paths; both real packaging/native-uninstaller tests pass with the original long machine path.

No additional unused production members were confirmed for deletion. The apparent unused native fields, COM methods, WPF callbacks, and attached-property accessors are required by their external contracts. Duplicate delegate adapters were retained because they share their actual implementation already.

## Verification

- Release solution build: zero warnings and errors.
- Full solution formatting: passed with normal Windows access; modified-file whitespace checks also passed.
- Managed suite: 2,135 tests passed across the complete run and focused reruns; 276 interactive-desktop tests skipped, no timeouts or unexecuted cases. The complete run found the two native-build failures described above; both passed after the script fix with the original machine search path.
- Dependency restore: full vulnerability audit enabled, no advisory warnings.
- Packaged-app restore: .NET, Windows Desktop, and ASP.NET runtime packs all verified as 10.0.14.
- New language-save regression: passed.
- Browser/worker/OAuth fixtures: 86 passed.
- Python tests: 8 passed.
- Release-publication fixtures: 11 groups passed using the fixture GitHub client.
- Native fixtures: all 8 programs passed with warnings treated as errors; rendering and hardware checks retained no additional GDI handles.
- Syntax/reference checks covered production and test C#, 27 JavaScript files, 7 Python files, and 29 native source files.
- PowerShell tooling, native dependency verification, changed configuration parsing, and `git diff --check`: passed.

The restricted Windows runner could not access profile storage and DPAPI; the managed-suite results above come from the rerun with normal Windows access. Live provider accounts and interactive desktop input were not exercised. Logs and the original working-tree patch are in the ignored `.tmp/review-2026-11-04` directory.
