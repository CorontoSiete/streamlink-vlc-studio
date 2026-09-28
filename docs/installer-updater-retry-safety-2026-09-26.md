# Installer and updater retry safety

The PowerShell installer registers the copied application and creates its Start Menu shortcut before installing shared dependencies. If Streamlink or VLC setup fails or is canceled, the application remains registered for removal. Dependency settings and automatic launch still wait for successful dependency setup.

ZIP uninstall now reserves deletion access to the application, uninstaller, and both ownership records before removing any of them. A reader that denies deletion preserves the entire set for retry. If a later file cannot be marked for deletion, earlier pending deletions are canceled before closing the handles. This also handles a mapped file whose ordinary file handle has already closed. The implementation uses Windows' [file disposition API](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle), with exclusive handles and checks that reject directories and reparse points. Managed-file cleanup retries remain bounded, and incomplete application removal preserves personal data.

Downloaded updates expire at seven days even if the app has remained open throughout that time. Expiration clears the prepared state before cache cleanup, so a failed or canceled refresh cannot restore an expired **Restart and install** action. The last verified release remains available to download again after a failed refresh. Installation rejects expired and future-dated verification times, and restoring a package uses the same retention rule.

After Setup exits, the update helper retries temporary result-file locks up to five times. Its relaunch runs even if saving the result still fails. Reboot-required outcomes continue to leave the app closed. A persistent write failure is returned as a helper error; the next launch can still check the installed version.

## Verification

- Four initial regression cases failed before their fixes: one PowerShell registration scenario and three .NET uninstall/expiration scenarios.
- Seven new regression tests cover dependency failure registration, deletion-sharing locks on each uninstall control file, mapped-file rollback, expiration during a running session, prelaunch timestamp validation, and persistent/transient completion-write locks.
- Release solution build with warnings treated as errors passed with zero warnings and errors.
- 116 distinct focused .NET tests passed across updates, cleanup, maintenance, bootstrapper, and installer validation. Four desktop-only checks were skipped.
- 26 PowerShell checks passed, including installer lifecycle, development tooling, and packaging contracts.
- The NativeAOT uninstaller build and its existing validation test passed.
- Changed C# files passed formatting and analyzer verification. Both changed PowerShell scripts passed Windows PowerShell syntax parsing, and `git diff --check` passed.
- The self-contained ZIP, NativeAOT maintenance helpers, internal MSI, and complete Setup bundle built successfully. MSI database/stream checks and pinned dependency verification passed.
- A startup hook verified that the packaged updater uses its executable directory and detects ZIP ownership, then exited before app startup. The packaged app, uninstaller, and Setup report version 1.7.9.0; installer scripts and `install.txt` match their source hashes.
- Packaging stage and probe extraction directories were removed after verification.

Tests use temporary installations, signed local update fixtures, real file locks and mappings, and simulated app launches. They do not install or uninstall the real application. Actual elevation, an installed-version upgrade, and reboot behavior still need an installation smoke test.

Logs: [initial .NET regressions](../artifacts/logs/installer-resilience-dotnet-before.log), [initial PowerShell regression](../artifacts/logs/installer-resilience-powershell-before.log), [Release build](../artifacts/logs/installer-resilience-final-build.log), [update tests](../artifacts/logs/installer-resilience-update-tests.log), [maintenance and installer tests](../artifacts/logs/installer-resilience-maintenance-tests.log), and [PowerShell checks](../artifacts/logs/installer-resilience-tooling.log).

## Local packages

- [Setup EXE](../artifacts/installer-resilience-2026-09-26/StreamlinkVlcStudio-Setup.exe)
- [Advanced installation ZIP](../artifacts/installer-resilience-2026-09-26/StreamlinkVlcStudio-release.zip)
- [SHA-256 checksums](../artifacts/installer-resilience-2026-09-26/SHA256SUMS-local.txt)
- [Setup build log](../artifacts/logs/installer-resilience-package.log)
- [Packaged updater validation](../artifacts/logs/installer-resilience-package-probe.log)

These are local, Authenticode-unsigned 1.7.9 validation builds. They have not been installed or published. Release identity and signing keys were unchanged; distributing an automatic update still requires a newer stable release through the existing signed-manifest workflow.
