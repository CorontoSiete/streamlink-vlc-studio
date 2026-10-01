# Faster Twitch category viewer counts

Twitch category cards now load Twitch's reported category viewer total through
`Game.viewersCount`. The existing four concurrent background jobs can display
each completed count immediately. Category cards continue to load separately
from their counts.

The previous provider fetched every live stream in a category from Helix, 100
streams per page, before publishing its sum. The live baseline below required
221 stream requests to populate ten categories. Twitch's documented
[Get Top Games response](https://dev.twitch.tv/docs/api/reference/#get-top-games)
does not contain a category viewer total; the stream endpoint also notes that
changing viewership can produce duplicate or missing streams during pagination.
[Get Streams documentation](https://dev.twitch.tv/docs/api/reference/#get-streams).

## Verified source

On October 1, 2026, the Twitch directory page loaded
[this directory bundle](https://assets.twitch.tv/assets/pages.browse.components.browse-directory-cdca7712e052b5f084ba.js).
Its `BrowsePage_AllDirectories` operation includes `viewersCount` on the `Game`
fragment, and its card renderer formats that field as the category viewer count.
The direct `game(id: $id) { id viewersCount }` query was verified against
`https://gql.twitch.tv/gql` before implementation. Successful single-category and
20-operation requests returned the requested IDs and integer totals. An unknown
category returned `game: null`. Public response fixtures are saved with the
measurements.

This uses the application's existing public Twitch GraphQL transport and public
website client identity. Account credentials stay on the existing authenticated
Helix path. Requests are limited to 20 categories each. The current UI keeps one
category per background job so a category needing fallback cannot hold up other
cards.

## Failure behavior

A reported total is accepted only when the response batch is complete, every
returned ID matches its request, and every count is a nonnegative integer within
the application's supported range. Missing categories, GraphQL errors, malformed
payloads, transport failures, and a two-second deadline fall back to complete
Helix stream pagination. That path retains stream deduplication, cursor and page
limits, rate-limit coordination, retries, and failure reporting. A failed scan
does not publish its partial sum. Caller cancellation stops the lookup without
starting fallback.

The website interface can change independently of Helix; validation and fallback
cover that dependency. Explicit category refreshes still fetch current counts.

## Live measurement

The probe called the actual `BrowseService` with the same ten category IDs and
four concurrent single-category jobs before and after the change. Each run first
loaded a real category page to resolve credentials and establish the Helix
connection. Timing then started at count loading. These are count-completion
measurements, not window-paint timings.

| Measurement | Before | After |
| --- | ---: | ---: |
| First count available | 1,610.3 ms | 252.9 ms |
| All ten counts available | 6,953.7 ms | 480.9 ms |
| Helix stream requests | 221 | 0 |
| Public category requests | 0 | 10 |

The measured count-loading phase was 14.46 times faster, a 93.1% reduction in
elapsed time. This is one live comparison on this machine; timing varies with
the network and Twitch. The runs finished at 14:57:13 UTC and 15:03:45 UTC, so
their live viewer values are separate snapshots.

Raw results, category IDs, public API fixtures, and the probe are in
[the measurement directory](measurements/twitch-category-viewer-counts-2026-10-01/).
The probe uses the installed account settings in memory and records only public
category metadata, counts, request totals, and loading telemetry.

After a Release build, run from the repository root:

```powershell
.\.dotnet-sdk\dotnet.exe run --project docs/measurements/twitch-category-viewer-counts-2026-10-01/Probe.csproj --configuration Release -- .tmp/twitch-category-counts/recheck docs/measurements/twitch-category-viewer-counts-2026-10-01/categories.json
```

## Validation

The Release build completed with warnings treated as errors and no warnings or
errors. The new reported-count regression failed against the original provider,
then passed after implementation. The final checks passed 106 tests:

- `Twitch category totals:` — 10 passed, no skips. Covers reported values and
  zero, batching, identity and payload validation, complete fallback sums,
  duplicate streams, incomplete batches, failed scans, transport failures,
  timeout, cancellation, missing credentials, and counts appearing in Discover
  while another category is still loading its fallback.
- `browse` — 68 passed. The headless-safe runner skipped 30 desktop tests; these
  cover physical layout and scrolling rather than the count-loading path.
- `paged refresh resources:` — 28 passed, no skips. Includes category refresh,
  count preservation across overlapping pages, navigation, and stale results.

The existing Helix count tests explicitly make the website lookup unavailable,
so they continue to verify the full pagination fallback, authentication failure,
rate-limit retries, and pagination safety limit.

```powershell
.\scripts\dev.ps1 Test -Filter 'Twitch category totals:'
.\scripts\dev.ps1 Test -Filter 'browse' -NoBuild
.\scripts\dev.ps1 Test -Filter 'paged refresh resources:' -NoBuild
```
