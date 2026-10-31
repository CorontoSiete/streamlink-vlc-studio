# Kick category display latency

The existing request overlap is retained. The remaining display dependency was
in `BrowseViewModel`: no category cards were created until the service finished
all category viewer-count lookups. That also delayed starting artwork loads.

`IBrowseService` now has an optional progress overload with a default
implementation for existing providers. Kick reports provisional metadata from
the first completed source, then the merged category snapshot. The returned
result still waits for every detail lookup and supplies final counts, ordering,
pagination and availability. The [Kick OpenAPI specification](https://api.kick.com/swagger/doc.yaml)
was checked on October 21, 2026: category detail includes `viewer_count`, while
the v2 category list schema includes metadata without that total. The code
continues using the existing endpoints and four-request detail limit.

The view model uses snapshots only for a new, initially empty category list.
Refreshes preserve existing enriched cards; subsequent pages retain their
existing reconciliation. Early snapshots do not mark a page complete or enable
pagination. Final reconciliation reuses the early card instances and applies
exact totals and ordering. A failed final result removes provisional cards.
Source, query and lifetime checks reject stale or queued callbacks after
completion, replacement or shutdown.

Selecting an early card keeps its category request alive while streams load.
Category completion updates the retained cards without replacing stream-page
status or navigating away. Returning to categories therefore has completed
counts and pagination without fetching the category page again. Switching
platforms, changing the query and shutdown still cancel the request.

`KickCategoryProgressTestCatalog` exercises both source arrival orders, search,
full result equivalence, request deduplication, early navigation through the
real service, retained card identity, refreshes, failed final results, stale and
queued progress, and cancellation during shutdown. Its timing fixture runs both
arrival orders with 40/160 ms source delays and 120 ms detail delays. Each case
runs five paired trials and discards the first for setup/JIT. The comparison
uses the existing completion-only service overload for the previous display
boundary and the progress overload for the new boundary; both perform the same
underlying requests and return the same results.

The timing fixture measures when category data can populate cards, rather than
rendered frames or completed artwork downloads. It distinguishes initial data
availability from completion of all counts. It does not establish an improvement
in full network completion or authenticated Kick latency. A direct connection to `api.kick.com` was rejected
by this environment, so live performance remains unmeasured.

## Final verification

The Release solution build completed with zero warnings and zero errors.
All ten new progress checks passed without skips. Formatting verification passed
for all six changed C# files, and `git diff --check` passed for the tracked changes.

| Faster source | Previous initial data | Early initial data | All counts complete |
| --- | ---: | ---: | ---: |
| Category list | 294.7 ms | 47.8 ms | 298.2 ms |
| Top-live discovery | 295.4 ms | 47.0 ms | 297.3 ms |

Values are medians of four warm trials. Full completion is not faster in these
fixtures; initial category availability is earlier. The timing run and build
output are in `.tmp/kick-category-progress-final.log`.

The existing loading, category search, Browse, Home refresh, paged refresh,
request concurrency, search ordering and back-navigation filters all passed.
There were 145 distinct passing checks across the final progress run and these
regression runs. The Browse filter skipped 30 desktop checks under the existing
headless policy. Filter results overlap; this is not a full-suite result.
`.tmp/kick-category-progress-validation.json` records the run summaries,
measurements and assembly hashes.

Run the focused checks with the pinned SDK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 Test -NoRestore -Filter 'Kick category progress:'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 Test -NoRestore -NoBuild -Filter 'Kick category loading:'
```

The starting provider, service, interface, view model and infrastructure assembly
are saved in `.tmp/kick-category-progress-baseline/`. Verification output uses
`.tmp/kick-category-progress-*.log`.
