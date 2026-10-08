# Installer, updater, and uninstaller recovery

Setup can repair a missing or damaged application executable, interrupted managed updates remain repairable, and ZIP installation and removal coordinate access to the same application folder.

## Setup and application shutdown

Setup and the ZIP uninstaller signal the application directly through its Windows shutdown event and wait for its single-instance mutex. They do not depend on launching the installed executable to close the app. The shared implementation recognizes both the current Stream Studio signals and the names used by release 1.7.0. Missing shutdown events are rechecked while an instance holds the mutex, including the startup window before its event exists. Waiting uses a monotonic clock and a bounded timeout.

A failed shutdown prevents planning or file removal. The ZIP uninstaller retains every application file, its ownership records, uninstall registration, and personal data. A missing or damaged application can still be repaired. Uninstall continues if the closed application's executable cannot unregister notifications, and reports a cleanup warning with a nonzero exit code instead of trapping the user in an unremovable installation.

The installer builder accepts `-DependencyCacheDirectory` to reuse already downloaded installers. It checks the copied payload against the original locked length, SHA-256, Authenticode status, signer, publisher, and product before promoting it. A corrupt or untrusted cached file stops the build without replacing prior output; a missing cached file follows the reviewed download path. The cache is retained, reparse points are rejected, and the cache cannot sit inside the temporary build directory that is cleared during packaging.

## ZIP installation and removal

The PowerShell installer and native uninstaller share a mutex identity derived from the normalized installation directory. Case, directory separators, and trailing separators resolve to the same identity. The installer holds its lease through application replacement, dependency setup, registration, and settings updates. Its atomic payload helper also acquires a lease when used independently.

An overlapping operation stops before shutdown or file changes and can be retried after the first operation finishes. An abandoned lease is recoverable. The uninstaller rereads its ownership records after acquiring the lease so it cannot delete files using a manifest loaded before an upgrade. Acquisition and release remain on the same thread, following the [Microsoft .NET mutex ownership rules](https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex?view=net-10.0).

## Managed update recovery

Before launching the update helper, the updater atomically writes and flushes two recovery records: an operation marker beside the verified installer and a persistent repair notice outside the expiring package cache. Failure to save either record prevents helper launch and keeps the verified installer available for another attempt.

If the helper terminates or Windows shuts down before a completion can be written, the next signed release check can still offer repair of the installed target version. Valid cached installers are reverified and reused; damaged or expired packages require a new verified download. A successful installed-runtime check or recorded successful completion clears the repair notice. A failed helper launch preserves the installer and recovery records for retry. Completion writes are also flushed before relaunch. The helper rejects nonpositive parent PIDs and its own PID.

## Uninstall reporting

Quiet argument errors return exit code 87 without opening a dialog, including an invalid argument that precedes `/quiet`. Invalid application executables produce a logged maintenance failure rather than an unhandled Windows process-launch exception. Start Menu shortcut cleanup retries transient locks, continues with other shortcuts, and returns an incomplete-cleanup result if a shortcut remains. The staged maintenance log records the final uninstall exit code. Uninstall messages consistently identify Streamlink, VLC, and WebView2 as retained shared dependencies.

ZIP removal runs in a staged child process. The original executable's successful exit confirms that staging started; the maintenance log records the child's final uninstall result.

## Validation

Checks used the pinned .NET SDK 10.0.302. Logs and generated artifacts are in `artifacts/installer-updater-uninstaller-2026-10-05/`.

| Check | Result | Evidence in the artifact directory |
| --- | --- | --- |
| Release solution build, warnings as errors | Passed; zero warnings and errors | `build.log` |
| Complete headless test suite | 1,637 passed, 275 skipped, zero failures | `full-tests-final.log` |
| Lifecycle and packaging tooling | 53 checks passed in Windows PowerShell 5; 56 in PowerShell 7 | `tooling-powershell5.log`, `tooling-powershell7.log` |
| Scoped C# formatting and Git whitespace checks | Passed | `formatting.log`; `git diff --check` |
| Real runtime verification | Nine healthy, damaged, and recovered Streamlink/VLC/WebView2 scenarios passed | `runtime-verification/results.json` |
| Packaged single-file updater | Uses the executable's directory and recognizes ZIP ownership | `packaged-updater.log` |
| ZIP uninstaller and bundled NativeAOT helper | Invalid arguments exit 87 with `/quiet` in either argument order | `native-uninstaller-smoke.json`, `bundled-maintenance-smoke.json` |
| ZIP and offline Setup build | Passed, including MSI database and stream-size validation | `package.log`, `setup-final.log` |
| Compiled bundle and MSI inspection | All three pinned runtime installers embedded, permanent, and repairable; bundled MSI matches validated output; MSI contains the compatible application filename/version and excludes ZIP removal tools | `compiled-bundle-checks.json`, `msi-checks.json` |

The first Setup build encountered a VLC mirror timeout. The successful build reused the reviewed local installers through `-DependencyCacheDirectory`, with all original integrity and publisher checks enabled. Cache tests cover verified reuse, corrupt hashes, rejected signatures, preservation of prior output, temporary-file cleanup, and missing-file download fallback.

Generated artifacts retain the existing 1.8.5 version and are unsigned local validation builds:

- `setup/StreamlinkVlcStudio-Setup.exe`: full offline installer, 469,898,156 bytes.
- `package/StreamlinkVlcStudio-release.zip`: advanced ZIP installation, 91,018,765 bytes.
- `setup/StreamStudio-Setup.msi`: internal bundle input, 89,157,632 bytes.

`artifacts.json` records lengths and SHA-256 hashes; `SHA256SUMS.local.txt` contains the checksums. No release was published.

Regression scenarios cover competing installation processes, abandoned and nested leases, delayed and missing shutdown events, stalled current and legacy instances, damaged executables, quiet errors, locked shortcuts, locked recovery records, missing update completions, corrupted or expired installers, helper launch failure, and successful recovery.

The complete suite exposed an unrelated race in the browse-category ordering test fixture. Its mock dispatcher now uses the fixture's STA dispatcher, keeping collection refreshes and assertions on the same thread; production browsing code was retained.

A fresh elevated installation, published upgrade, and real reboot/resume cycle require a disposable Windows environment. The local checks do not replace this workstation's installed application or shared runtimes.
