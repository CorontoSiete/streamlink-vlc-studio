# Kick category search

Verified against live Kick responses on October 3, 2026.

Discover used `/public/v2/categories?name=...` for typed category searches. That filter misses words inside a category name. These requests reproduced the failure using the app's configured Kick API credentials:

| Query | v2 `name` results | v1 `q` search results |
| --- | --- | --- |
| `hot tubs` | Empty | ID 16, `Pools, Hot Tubs & Bikinis` |
| `Chatting` | Empty | ID 15, `Just Chatting` |

Kick's website search, `/api/search?searched_word=hot%20tubs`, independently returned category ID 16, its banner, the `IRL` tag, and its viewer total. The v2 `name=Pools` request returned ID 16, confirming that the category exists in the v2 directory too.

Typed searches now use the official `/public/v1/categories?q=...&page=...` search endpoint. The [Kick OpenAPI specification](https://api.kick.com/swagger/doc.yaml) documents `q` as the search query and `page` as pagination, with up to 100 results per API page. This endpoint is marked deprecated, but remains the documented public search endpoint and returned the verified matches. The v2 directory endpoint continues to serve searches with an empty query. A failed search is reported as unavailable rather than silently returning an incomplete v2 name-filter result.

Search category IDs, names, and thumbnails come from the API response. Existing category-detail requests supply tags and current viewer totals. Missing detail metadata retains the discovered category and reports an unknown viewer total. No category names or IDs are embedded in the search implementation.

The app continues to request ten categories at a time. Its search continuation stores both the API page number and the offset within that page. Remaining matches are reached before advancing to the next API page; later pages are not prefetched, and detail requests cover only the displayed result page. Re-reading the current API page keeps this continuation stateless. Duplicate or invalid rows do not prevent advancing when a full API page was returned. Search refresh starts again at page one.

`KickCategorySearchTestCatalog` covers the reported search, another match inside a category name, query encoding, small pages across the 100-result boundary, duplicates, invalid responses, HTTP failures, missing metadata, cancellation, and the discovery-card-to-category-stream flow. Run it with:

```powershell
.\scripts\dev.ps1 Test -Filter 'Kick category search:'
```

The optional live probe uses the app's saved Kick credentials in memory and prints category results only:

```powershell
$env:SVS_TEST_KICK_CATEGORY_SEARCH_LIVE = '1'
.\scripts\dev.ps1 Test -Filter 'Kick category search:' -NoBuild
Remove-Item Env:\SVS_TEST_KICK_CATEGORY_SEARCH_LIVE
```

Validation on October 3, 2026: the Release solution build completed with zero warnings or errors; all nine new regression tests and 70 existing browse tests passed. Thirty tests requiring an interactive desktop were skipped by the headless runner. The live probe passed for `hot tubs`, `HOT TUBS`, `tubs`, the full category name, and `Chatting`; opening category 16 returned 29 live streams, all mapped to that category.
