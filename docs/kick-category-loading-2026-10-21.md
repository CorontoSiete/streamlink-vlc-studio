# Kick category loading

The first unfiltered Kick category page previously waited for both the category
list and top-live discovery before starting any category detail requests. Those
two source requests already overlapped, but a faster source's category IDs sat
unused until the slower source finished.

Detail requests now start when either source supplies IDs. One request-scoped
loader shares requests by category ID and keeps the existing limit of four
active detail requests. It retains the response payload until both category
sources have been merged, then applies the details to the merged category.
Missing detail fields therefore retain the same discovery/list metadata
regardless of arrival order. Categories with an already supplied viewer total
skip detail requests. A lookup started before the other source supplies a total
remains owned and drained by that page.
Final sorting, pagination and authorization failures retain their existing
behavior. Search and subsequent pages continue using their existing source
paths and the same bounded detail loader.

Every speculative request belongs to that page load. Caller cancellation and
failed page loads cancel and drain outstanding details before returning. HTTP
responses and JSON documents are disposed, and queued requests check
cancellation before contacting Kick. The loader does not reuse viewer totals
across refreshes.

The [official Kick OpenAPI specification](https://api.kick.com/swagger/doc.yaml),
checked October 21, 2026, lists tags and thumbnails on the v2 category response;
the v1 category detail response includes the category viewer total. The change
continues fetching those totals and retains unknown counts when details fail.
It does not derive totals from the limited top-stream sample.

The regression fixtures hold one source open until detail requests from the
other source have started and completed. They check both arrival orders,
deduplication, the four-request bound, partial and invalid detail payloads,
merged metadata, exact ordering, pagination, authorization failures, and cleanup
after cancellation and failed or malformed source responses.

The timing fixture uses the actual BrowseService with controlled HTTP responses:
40 ms and 340 ms source delays, eight overlapping category IDs, and 120 ms per
detail response. Each case runs five trials, discards the first for setup/JIT,
and reports the median of four warm samples. These are service-path fixture
measurements, not authenticated Kick or total application startup measurements.

The final comparison used the identical rebuilt test assembly with the saved
original infrastructure assembly in an isolated runner directory, followed by
the revised infrastructure assembly. No build ran during the comparison.

| Faster source | Original median | Revised median |
| --- | ---: | ---: |
| Category list | 598.4 ms | 358.4 ms |
| Top-live discovery | 647.9 ms | 353.0 ms |

The final Release solution build completed with zero warnings and errors.
Formatting verification passed for all four changed C# files. All 13 new
category-loading checks passed without skips. Focused regression runs passed
65 browse checks (30 desktop checks skipped), nine category searches, ten Home
refresh resource checks, 28 paged refresh checks, and eight existing loading
concurrency checks. Some filters overlap; these counts are per run.

Authenticated live timing was attempted using a private copy of saved settings.
Windows DPAPI could not decrypt/protect the copied secrets, so the live probe
failed before sending category requests. No live speedup is claimed.

An exploratory `Browse` substring filter also selected unrelated browser tests.
That run reported 68 passes, two failures, one timeout, 30 skips, and 23 unrun
entries. OAuth WebView2 exited with `GpuProcessExited`, Kick follow-import timed
out, and the clip browser fixture timed out without draining. That broad run is
not a clean suite result. Narrower category and browse checks avoid selecting
these browser fixtures; their assertions and skip policy are unchanged.

Run the focused checks with the pinned SDK:

```powershell
.\scripts\dev.ps1 Test -NoRestore -Filter 'Kick category loading:'
.\scripts\dev.ps1 Test -NoRestore -NoBuild -Filter 'Kick category search:'
.\scripts\dev.ps1 Test -NoRestore -NoBuild -Filter 'browse '
.\scripts\dev.ps1 Test -NoRestore -NoBuild -Filter 'loading concurrency:'
```

The optional live probe prints request paths, HTTP statuses, header wait times,
total service times and result counts. It does not print credentials or bodies:

```powershell
$env:SVS_TEST_KICK_CATEGORY_LOADING_LIVE = '1'
.\scripts\dev.ps1 Test -NoRestore -NoBuild -Filter 'Kick category loading: live request timing'
Remove-Item Env:\SVS_TEST_KICK_CATEGORY_LOADING_LIVE
```

The original provider source and infrastructure assembly are preserved in
`.tmp/kick-category-speed-baseline/`. The original assembly SHA-256 is
`524607D6301D49A3AC98E315E287F633098B89088AD6814B3D6AFF40D8C6FC61`.
Logs use `.tmp/kick-category-speed-*.log`. The original implementation failed
all eight new overlap/cleanup regressions in the initial 12-test baseline run;
its existing failure handling and timing checks passed.
`.tmp/kick-category-speed-validation.json` records the final samples, assembly
hashes, test runs, and verification limits.
