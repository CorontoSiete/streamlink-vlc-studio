# In-app updates for ZIP installations

The running installation was version 1.8.5 at `%LOCALAPPDATA%/Programs/StreamStudio`, with valid ZIP ownership records. A separate MSI installation existed in Program Files. The updater correctly identified the running folder as ZIP, but its check, download, installation, and automatic-download eligibility rules deliberately excluded ZIP installations. The UI consequently offered **Open release page**.

This was reproduced using the installed executable's real signed GitHub check. It authenticated v1.8.6 and returned `IsUpdateAvailable=true`, `IsNotifyOnly=true`, and phase `NotifyOnly`. The release feed and signature verification were working.

Owned ZIP installations now use the complete in-app update flow. They download the ZIP asset authenticated by the signed manifest, retain and reverify it across restarts, and support the existing automatic-download preference. **Restart and install** waits for the parent to close, validates and locks the package, extracts a bounded payload while rejecting unsafe Windows paths, links, duplicate paths, and file/directory conflicts, then runs the authenticated payload's atomic installer. The helper verifies the installed version and runtimes and restarts `StreamStudio.exe`. Cancellation, reboot requirements, failed installation, and same-version recovery retain the existing completion and repair behavior.

Setup installations continue using the existing Setup asset and helper arguments. Protocol 1, its signing key, and the existing serialized `setupPath` property remain compatible. Cached packages additionally record their type; signed release identity comparisons include ZIP assets and dependency minimums. Unowned development folders remain notification-only. Automatic downloading remains opt-in, and installation still uses the explicit restart action.

Validation used the pinned .NET SDK 10.0.302:

- Release build with warnings as errors: zero warnings and errors.
- Ten new ZIP updater regression tests passed with no skips. They cover the actual view-model commands without browser navigation, signed-asset substitution, corrupted downloads, cached-package recovery, installation-type changes, interrupted same-version repair, archive validation, locked payloads, argument boundaries, real PowerShell success/cancellation/failure/reboot, and correct executable relaunch.
- Complete headless suite: 1,864 passed, 274 desktop tests skipped, no failures.
- Installer lifecycle and PowerShell tooling checks passed.
- Scoped C# whitespace/style verification and Git whitespace checks passed.
- The packaged updater downloaded and verified the actual 91,091,291-byte signed v1.8.6 ZIP, then performed a real in-place upgrade from the patched 1.8.5 installation. The restarted target authenticated the release, reported phase `Completed`, and offered no further update.
- Structured comparisons confirmed preservation of every settings value, watch-history data, installation identity, and a personal-file fixture through the signed upgrade and final local installation.

The installed application was then replaced with the fixed local build, informational version `1.8.6+zip-updater-fix`, and reopened. Its executable matches the verified local package byte for byte; its real signed check identifies ZIP ownership and returns `IsNotifyOnly=false`.

Evidence, packaged builds, and the original installation/preferences backup are under `artifacts/updater-zip-fix/`. The backup is `backup/StreamStudio-1.8.5`; verification results are in `state-preservation.json`, `installed-upgrade.json`, `fixed-installed-probe.log`, and `full-headless-tests.log`. No GitHub release was published; these source changes must be included in the next release.
