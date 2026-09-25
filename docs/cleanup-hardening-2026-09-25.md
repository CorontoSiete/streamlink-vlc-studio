# Additional uninstall and update cleanup fixes

This extends the existing uninstall and update cleanup changes in the working tree.

- ZIP uninstall removes all empty ancestor folders belonging to managed files, up to the installation root. Previously, only the immediate parent folder was removed, leaving paths such as `runtimes/win-x64` behind. Unknown folders, including empty folders, are preserved. Deletion checks ancestors for reparse points and never recursively removes the installation directory.
- Update cache deletion clears read-only attributes on ordinary files and directories, removes links without traversing them, and continues with other files when one is locked. Locked remnants of failed downloads are marked for later cleanup.
- Startup package expiration also checks the original `VerifiedAt` timestamp. Replacing a helper or touching the package directory cannot extend the seven-day retention period across restarts.
- Each cache area is cleaned independently. An inaccessible or unsafe area cannot prevent cleanup of the other areas, and housekeeping errors do not abort routine release checks. Startup cleanup still runs if reading an update completion fails. Unsafe package paths remain ineligible for restoration or installation.

## Verification

- Four added regression tests reproduced failures before the fixes and passed afterward: empty managed ancestors, read-only cache files, locked-file siblings, and refreshed timestamps on expired packages.
- A fifth test uses real Windows junctions to verify that operations, results, and logs are isolated and that unrelated target files remain intact.
- 83 distinct focused .NET tests passed across update, maintenance, ZIP cleanup, personal-data cleanup, and installer checks. Three desktop-only checks were skipped.
- 14 PowerShell tooling checks passed.
- Release solution build completed with warnings treated as errors: zero warnings and errors.
- NativeAOT uninstaller packaging passed. Its first test run selected the system's older SDK; rerunning with the installed .NET 10.0.302 SDK on `PATH` passed.
- C# whitespace verification and `git diff --check` passed.

Logs are in `artifacts/logs/cleanup-hardening-*`. Tests used temporary fixtures; no real installation, uninstall, or release publication was performed. The previously generated packages linked in the earlier cleanup notes predate these additional fixes.
