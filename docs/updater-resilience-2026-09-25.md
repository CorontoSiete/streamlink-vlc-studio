# Automatic and manual update resilience

Package downloads now stop if a response body delivers no data for 60 seconds. The idle timer resets after each read, so progressing transfers can still use the existing 30-minute package deadline. A stalled transfer reports a connection/retry message, disposes its response, removes partial files, and remains eligible for the automatic retry backoff. Explicit cancellation still returns to the available state without reporting a network failure.

Failed and canceled installation results retain the staged installer instead of deleting it immediately. The next signed check must match its release metadata and verify its bytes before offering **Restart and install** again. Tampered packages are rejected, successful installations still remove their staging directory, and retained packages remain subject to the seven-day expiration and existing cache limits. This also avoids another installer download after canceling elevation.

Disabling automatic checks or downloads still cancels an active automatic download. Re-enabling the preferences now allows that version to download on the next automatic check. Only the explicit **Cancel download** action suppresses automatic downloading of the version for the remainder of the session.

Malformed cached release asset lists are treated as invalid cache entries, allowing a fresh signed check instead of repeatedly failing with a null-reference exception.

## Validation

- Regression tests reproduced all three recovery bugs against the previous binaries: installer deletion after an unsuccessful attempt, malformed-cache recovery, and downloading after preferences are re-enabled.
- Release build with warnings treated as errors succeeded. The focused update run passed **49 tests**, with **3 desktop-only skips**.
- Formatting and analyzer verification passed for all four changed C# files; whitespace diff checks also passed.
- The full headless run recorded **929 passed, 2 failed, 238 skipped**, with no timeouts and the existing skip ceiling enforced. The failures were `resuming after pausing while behind live holds the rewound position` and `opening Kick VOD resolves HLS tab without recents`; both passed immediate individual reruns. Their playback code was not changed. The full run therefore was not clean, despite successful targeted reruns.
- Four new regression tests cover installer retention/revalidation/expiration, malformed release cache recovery, stalled-transfer cleanup and cancellation, and slow progressing transfers. Existing preference tests now exercise re-enabling both automatic checks and downloads. Tests use local signed fixtures and simulated services; they do not execute an installer.

Logs: [before the fixes](../artifacts/logs/updater-resilience-before.log), [focused update tests](../artifacts/logs/updater-resilience-tests.log), [full suite](../artifacts/logs/updater-resilience-full-tests.log), [replay recheck](../artifacts/logs/updater-resilience-replay-recheck.log), [VOD recheck](../artifacts/logs/updater-resilience-vod-recheck.log), and [formatting](../artifacts/logs/updater-resilience-format.log).

These changes update the source and local Release build. Packaged installers have not been regenerated, installed, or published. Actual elevation and an installed-version upgrade still need an installation smoke test.
