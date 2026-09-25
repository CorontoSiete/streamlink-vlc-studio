# Automatic retry and manual update refresh

Automatic retries now fetch fresh release metadata instead of reusing the 24-hour cache. This lets a failed download recover when a release is replaced. The new metadata must pass the existing signature, version, protocol, and asset checks before it can be used. Routine successful background checks retain their existing cache behavior and retry backoff.

A failed refresh preserves the previously verified release's available actions: download, retry download, open the release page, or restart and install. The updater restores the matching trusted release identity as well as the visible state. Download and installation verification still apply.

Settings > Advanced > Updates now offers a separate **Check for updates** button when a release is available or staged. It performs a fresh check without downloading or installing. Manual actions share an execution gate, including the settings save that clears a version snooze. Background checks and automatic preparation respect that gate. The refresh command is unavailable during an active check, download, verification, or installation handoff.

## Validation

- Regression tests reproduced both recovery bugs against the previous implementation: retry reused version 1.8.0 after 1.9.0 became available, and failed refresh changed an available release to the failed state.
- Release solution build with warnings treated as errors: **0 warnings, 0 errors**.
- Focused `update` test run: **55 passed, 3 desktop-only skips**, no failures or timeouts, with the skip ceiling enforced.
- Six new tests cover fresh retry metadata, preserved recovery actions, rejection of invalid replacement signatures, independent manual checks, busy and failed-check recovery, and serialization while saving snooze preferences. Existing download cancellation coverage also checks the new command.
- Formatting and analyzer verification passed for all four changed C# files. Changed-file whitespace checks and XAML syntax validation also passed.

Logs: [before the fixes](../artifacts/logs/updater-refresh-before-tests.log), [Release build](../artifacts/logs/updater-refresh-build.log), [focused tests](../artifacts/logs/updater-refresh-tests.log), and [formatting](../artifacts/logs/updater-refresh-format.log).

The tests use signed local fixtures and simulated update services. They do not execute an installer. These changes update the source and local Release build; packaged installers have not been regenerated, installed, or published. Interactive desktop layout and an installed-version upgrade were not exercised.
