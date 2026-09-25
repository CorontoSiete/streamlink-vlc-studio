# Repository review — September 25, 2026, pass 13

Preserved the existing working-tree changes. This pass used a source inventory,
solution-wide compilation and diagnostics, reference and duplication scans, and
targeted inspection of provider parsing, replay/playlists, chat catalogs, asynchronous
lifetimes, settings, process management, UI coordination, native integration,
maintenance, and release tooling. Generated files and third-party binaries were
excluded from refactoring.

## Fixes

- **Redirected Twitch playlists:** subscriber-only VOD fallback now resolves media,
  encryption-key, and initialization-map URLs against the final response location.
  Previously, a redirect to another directory or host left relative URLs pointing at
  the original location. Muted-segment repair also resolves each response before
  publishing it, including when a growing playlist redirects elsewhere on refresh.
- **Twitch badge credentials:** badge loading now shares the OAuth normalization and
  authenticated request builder used by other Twitch integrations. Tokens with
  `oauth ` or `Bearer ` prefixes no longer produce invalid Authorization headers.
  Equivalent token spellings also retain the loaded catalog instead of invalidating it.
- **Catalog eviction race:** a scope cannot reload while its previous payload is being
  removed. Otherwise, cleanup could erase freshly loaded badges or emotes after the
  coordinator had already marked their replacement catalog as loaded. The admission
  guard is released even if the cleanup callback throws.
- **Test timing:** the Kick unavailable-chat test previously allowed exactly the
  replay clock's 500 ms polling interval. It now allows the next poll to run and
  disposes its tab even when an assertion fails. Its behavior assertions remain intact.
- Corrected existing formatting violations in the Kick follow importer and its tests.

All six new regressions failed before their corresponding fixes and pass afterward.
The existing badge tests also caught the loss of the parameterless catalog constructor
during dependency injection; that constructor is retained.

## Reuse and removal

Playlist readers share a bounded, validated download helper that returns the response
location with the text. Removed the obsolete full-download branch from the range-probe
routine and the separate muted-playlist download implementation. Badge requests use
the existing shared credential and HTTP helpers; removed local credential helpers,
a redundant subscriber-badge wrapper, a redundant change flag, and alias branches
that duplicated the candidate normalization already performed by the caller.

## Verification

- Release solution and separate update-probe builds: zero warnings or errors, with
  warnings treated as errors. Both pinned native overlay inputs passed verification.
- Full headless suite: **1,039 passed, 240 expected interactive-desktop skips**, with
  no failures or timeouts and the skip ceiling enforced.
- All **14 PowerShell tooling checks** passed; syntax validation passed for **22
  PowerShell scripts**, **29 XML files**, **10 JSON files**, and the JavaScript source.
- NuGet audit, including transitive dependencies, reported no known vulnerabilities
  from the configured source.
- Solution-wide formatting/analyzer verification and `git diff --check` passed.

The restricted-session test attempt encountered PowerShell execution-policy and local
HTTP listener restrictions. The approved rerun outside the sandbox passed. No tests
were disabled to obtain that result.

Interactive desktop behavior, authenticated provider operations, actual installation
or removal, GitHub Actions execution, and rebuilding the native C plugin were not
exercised. This review does not establish that every possible defect is eliminated.

Logs, failing regressions, the initial source snapshot, and a patch isolating this pass
from the pre-existing changes are under `artifacts/logs/review-pass13/`.
