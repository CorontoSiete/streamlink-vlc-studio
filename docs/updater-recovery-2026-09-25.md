# Automatic and manual update recovery

The scripted installer now pins the same RSA public key and key ID as the application, committed public-key file, and release contract. Its previous pin did not match the release contract, causing it to fail before installation. A regression test compares both updater pins with the committed signing key, including the actual public-key bytes.

Update checks now restore the previously verified release identity and prepared-operation reference when a refresh fails or is canceled. This keeps an already downloaded update usable when a newer release is discovered but the refresh does not finish.

Missing or truncated cached installers and invalid staged paths are skipped so the updater can download a fresh, verified package. Completion processing also skips invalid record lengths, null records, mismatched operation IDs, unknown outcomes, and empty messages before consuming a valid result. An unrelated cancellation while reading a completion record no longer ends automatic discovery for the session; application shutdown cancellation still stops the controller.

Signature, length, hash, helper, and installation-path verification remain enforced. Automatic downloads remain opt-in, and installing still requires **Restart and install**.

## Validation

- Before the fixes, the regression run reproduced five failures: mismatched trust roots, damaged staged-package recovery, malformed completion recovery, completion cancellation ending discovery, and loss of prepared-release identity after a canceled refresh.
- Release update-filter run: **45 passed, 3 desktop-only skips**. This includes four new tests and expanded completion-record coverage.
- Release solution build with warnings treated as errors: **0 warnings, 0 errors**.
- PowerShell tooling: **14 passed**. The installer script also passes Windows PowerShell syntax parsing.
- Formatting and analyzer verification passed for every changed C# file.
- The tests use signed local fixtures and simulated services; they do not launch Setup or install software.

Logs: [regressions before the fixes](../artifacts/logs/updater-recovery-before-tests.log), [update tests](../artifacts/logs/updater-recovery-update-tests.log), [build](../artifacts/logs/updater-recovery-build.log), [tooling](../artifacts/logs/updater-recovery-tooling.log), and [formatting](../artifacts/logs/updater-recovery-format.log).

These are source changes and a local Release build. Existing packaged installers have not been regenerated, installed, or published. A real installed-version upgrade and interactive desktop verification remain outside this validation.
