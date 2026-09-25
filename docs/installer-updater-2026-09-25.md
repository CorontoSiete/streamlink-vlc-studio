# Installer and updater improvements

The installer now distinguishes removal requested by another bundle from a standalone uninstall. Related removals skip app shutdown, notification unregistration, and personal-data cleanup. Normal installs and repairs request a graceful app shutdown before changing files. Setup also applies its declared command-line variable overrides, including `PurgeUserData=0`, blocks closing during preparation and cleanup, restores notification registration after failed uninstall planning, and exits passive downgrade attempts instead of waiting indefinitely.

This distinction follows Burn's [related-bundle upgrade behavior](https://www.firegiant.com/blog/2022/2/24/wix-online-meeting-230-highlights/): the old bundle is removed as part of an upgrade. A production upgrade smoke test must also exercise the cached uninstaller from the previously distributed release; changing this source does not patch that older executable.

The updater now:

- Restores a verified installer after restarting the app, without downloading it again. It binds cached operation metadata to a signed release check, rehashes the package, and refreshes the helper from the running executable. The existing seven-day retention policy still applies.
- Cleans up unsuccessful and canceled downloads immediately. Progress stays visible in the update banner, and both the banner and Settings offer **Cancel download**. Conflicting actions are disabled during downloads.
- Allows up to 30 minutes for package transfers while keeping the existing bounded response reader and signed size/hash checks.
- Rejects future-dated check caches, current-version installations, modified helpers, and invalid operation paths. Failed prelaunch verification offers a fresh check so the user can recover.
- Skips malformed completion records, distinguishes failed installs from success, recognizes Windows reboot/cancellation results, and closes the app only after the helper reports that it started.

Automatic discovery continues throughout an app session, reusing signed release metadata for 24 hours. Failed checks retry after 15 minutes, then back off to a maximum of six hours. Disabled checks and active downloads are respected, and completion-record errors do not stop subsequent checks. Downloading and restarting still require the user's update actions.

## Validation

- Release solution build: zero warnings and errors.
- Locked restore with transitive NuGet vulnerability auditing: passed.
- Headless suite: **863 passed, 236 desktop-only skips**, with the existing skip ceiling enforced.
- After the final prelaunch-recovery and error-banner adjustments: **31 update-filter tests passed, 3 desktop skips**.
- Installer engine tests: **7 passed**; the additional scripted-installer test passed in the full suite.
- PowerShell tooling checks: **14 passed**.
- Formatting/analyzer verification passed for the changed C# files; final whitespace verification also passed.
- Fourteen behavior regressions were added, including simulated installer-engine tests that cannot invoke real maintenance processes.
- Self-contained app ZIP, NativeAOT maintenance helper, internal MSI, and the full dependency bundle were built successfully. MSI database/stream validation and pinned dependency verification ran during the build.

The first broad test run found two child-process SDK resolution failures and a replay-chat timing failure. With the pinned SDK on `PATH`, the complete rerun passed. Production installer execution, elevation, reboot behavior, and interactive desktop tests were not exercised on this PC.

## Local build

- [Full setup EXE](../artifacts/installer-updater/setup/StreamStudio-Setup.exe)
- [Advanced installation ZIP](../artifacts/installer-updater/package/StreamStudio-release.zip)
- [Local SHA-256 checksums](../artifacts/installer-updater/SHA256SUMS-local.txt)
- [Full test log](../artifacts/logs/installer-updater-full-tests-pinned-sdk.log)
- [Final update tests](../artifacts/logs/installer-updater-tests.log)

These are local, Authenticode-unsigned validation artifacts using the existing **1.7.0** build identity. They were not installed or published. Distributing this as an automatic update requires a newer stable release and its manifest signed by the existing protected release workflow; the signing key and trust policy were not changed.
