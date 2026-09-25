# Installer and automatic update reliability

Managed installations can now opt into **Download verified updates automatically** under **Settings > Advanced > Updates**. This preference defaults to off, including when loading older settings. Automatic downloads honor the automatic-check preference, version snoozes, and installation ownership. ZIP and unmanaged installations remain notification-only. Installing still requires **Restart and install**.

Both automatic and manual downloads use the same progress and cancellation controls. Canceling a download suppresses automatic downloading of that version for the rest of the session, while a manual retry remains available. Turning off automatic checks or downloads stops an active automatic download. Background download failures use the existing retry backoff. Failed downloads also expose **Retry download** without requiring another release check.

A failed or canceled refresh preserves an already prepared installer. Installation still validates the previously verified release identity, package hash, helper, and target version before starting a process. Snoozed prepared updates stay hidden even when a failed check restores the ready state.

Setup now provides **Try again** for recoverable failures and cancellations. It repeats component detection and returns to install or maintenance choices before applying anything. Downgrades, cleanup warnings, and noninteractive setup do not offer this action. Cancellation during preparation or while an apply callback is queued prevents planning or elevation. During rollback, setup stops sending cancellation requests and disables Cancel. WiX exposes rollback through [ExecutePackageBeginEventArgs.ShouldExecute](https://docs.firegiant.com/wix/api/wixtoolsetmbacore/executepackagebegineventargs/). Setup also withholds the Launch button when Windows must restart.

## Validation

- Release solution build completed with zero warnings and errors.
- Full headless run: **879 passed, 236 desktop-only skips**, with the existing skip ceiling enforced.
- After the final snooze regression: **40 update-filter tests passed, 3 desktop-only skips**.
- Installer engine regressions: **15 passed**. They simulate maintenance and engine calls; they do not install or remove software.
- PowerShell tooling: **14 passed**.
- The self-contained app ZIP, internal MSI, NativeAOT maintenance helper, and complete dependency bundle built successfully. Packaging validated the MSI and pinned dependency payloads.
- Final formatting and analyzer verification passed for all changed C# files.
- Fifteen new regression tests cover automatic download eligibility, settings persistence, cancellation, retry backoff, prepared-state recovery, snoozes, rollback, retry detection, and reboot handling.

Logs: [build](../artifacts/logs/updater-improvements-build.log), [full suite](../artifacts/logs/updater-improvements-full-tests.log), [final update tests](../artifacts/logs/updater-improvements-update-tests.log), [tooling](../artifacts/logs/updater-improvements-tooling.log), and [packaging](../artifacts/logs/updater-improvements-package.log).

Production elevation, an actual installed-version upgrade, rollback of installed files, reboot behavior, and interactive desktop layout require a Windows installation smoke test. The source retains the existing **1.7.0** identity and pinned release-signing key. Publishing an automatic update requires a newer stable version and the existing protected signing workflow.

## Local artifacts

- [Setup EXE](../artifacts/update-reliability/StreamStudio-Setup.exe)
- [Advanced installation ZIP](../artifacts/update-reliability/StreamStudio-release.zip)
- [SHA-256 checksums](../artifacts/update-reliability/SHA256SUMS-local.txt)

These are local validation artifacts. The setup reports version **1.7.0.0** and Authenticode status **NotSigned**. They have not been installed or published.
