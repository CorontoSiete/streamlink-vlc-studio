# Installer, updater, and uninstall cleanup

ZIP upgrades now confirm the application has stopped before copying files outside the managed-file manifest. This preserves the latest files present after shutdown. A failed forced shutdown aborts before replacement. Empty user-created directories are also carried into the new installation, including directories underneath an old managed parent or replacing an old managed file. Obsolete managed directories are removed. A new package that conflicts with a user file or directory aborts before replacing the installation.

Installer cleanup clears read-only attributes, rejects ancestor junctions, unlinks direct junctions without following their targets, and continues removing other files when one is locked. Temporary stage, backup, and download cleanup retries transient failures five times. An exhausted cleanup reports the exact leftover directory without hiding an installation result or the original failure.

The updater reports an installation's completion even if its result file cannot be deleted or its operation directory cannot be safely cleaned. Locked result files are retried without showing the same completion repeatedly during that app session. Existing package expiration, failed-install retries, signed manifest checks, package hashes, and install ownership checks remain enforced.

The update helper and PowerShell installer's launch option leave the app closed when Windows must restart. The recorded update result remains available on the next launch. This follows Windows Installer's meanings for exit codes 3010 and 1641: [Microsoft's system reboot documentation](https://learn.microsoft.com/en-us/windows/win32/msi/system-reboots).

ZIP uninstall only purges personal data after managed application removal succeeds. A failed removal retains settings, uninstall ownership, and registration for retry, and restores notification registration when possible. Cleanup retries failures while checking or clearing file attributes, removes read-only directories, reports inaccessible paths instead of treating them as missing, and continues with independent personal-data roots. Shortcut removal requires a registration matching the installation being removed; another installation's registration is preserved.

## Verification

- Regression tests reproduced eight failures before the fixes: three PowerShell upgrade/cleanup cases and five .NET completion/uninstall cases.
- Release solution build with warnings treated as errors: zero warnings and errors.
- 108 distinct focused .NET tests passed across update, cleanup, bootstrapper, maintenance, and NativeAOT uninstaller checks. Five desktop-only checks were skipped.
- 25 PowerShell checks passed, including six installer lifecycle scenarios, existing tooling checks, and developer-command tests.
- Changed C# files passed formatting and analyzer verification. Changed PowerShell files passed Windows PowerShell syntax parsing. Changed tracked files passed `git diff --check`.
- Tests use temporary installation trees, local signed release fixtures, real file locks and junctions, and simulated process launches. They do not install or uninstall the real application.
- The self-contained ZIP, NativeAOT uninstaller, internal MSI, and complete Setup bundle built successfully. MSI database and stream checks and pinned dependency verification passed.
- A startup hook inspected the packaged executable before app startup: the default updater used its apphost directory and detected ZIP ownership. Packaged app and uninstaller versions were 1.7.9.0, and the ZIP's installer scripts matched the source files.

Logs: [initial regressions](../artifacts/logs/installer-cleanup-regressions-before.log), [initial PowerShell regressions](../artifacts/logs/installer-lifecycle-before.log), [build and cleanup tests](../artifacts/logs/installer-cleanup-tests.log), [focused suites](../artifacts/logs/installer-update-focused-tests.log), [PowerShell checks](../artifacts/logs/installer-cleanup-tooling.log), and [format verification](../artifacts/logs/installer-cleanup-format.log).

Actual elevation, an installed-version upgrade, and reboot behavior still require an installation smoke test. Release identity and signing keys were not changed.

## Local builds

- [Setup EXE](../artifacts/installer-cleanup-2026-09-26/StreamlinkVlcStudio-Setup.exe)
- [Advanced installation ZIP](../artifacts/installer-cleanup-2026-09-26/StreamlinkVlcStudio-release.zip)
- [SHA-256 checksums](../artifacts/installer-cleanup-2026-09-26/SHA256SUMS-local.txt)
- [Setup build log](../artifacts/logs/installer-cleanup-package.log)
- [Final ZIP build log](../artifacts/logs/installer-cleanup-package-final-zip.log)
- [Packaged updater probe](../artifacts/logs/installer-cleanup-package-probe.json)

These are local, Authenticode-unsigned 1.7.9 validation builds. They have not been installed or published. Distribution through automatic updates still requires a newer stable release and the existing signed-manifest workflow.
