# Twitch category thumbnail resolution

Twitch category search supplied fixed `52x72` box-art URLs. The normal category list supplied `{width}x{height}` templates, which the app expanded to `285x380`. The mapper only replaced placeholders, so searching switched the cards to tiny images that WPF enlarged. Twitch's [Search Categories example](https://dev.twitch.tv/docs/api/reference/#search-categories) documents the fixed-size response.

The failure was reproduced before changing production code. The existing category-search test was updated to use the real response format and failed with an expected `285x380` URL and an actual `52x72` URL. A live probe then fetched Twitch's normal list and searched for Just Chatting, Rust, and Minecraft, bound the results to the actual WPF category card template, and inspected the decoded bitmaps.

| Card | Before: decoded bitmap | After: decoded bitmap | After: image layout |
| --- | --- | --- | --- |
| Normal category list: Just Chatting | 285 × 380 | 285 × 380 | 166 × 221 |
| Search: Just Chatting | 52 × 72 | 285 × 380 | 166 × 221 |
| Search: Rust | 52 × 72 | 285 × 380 | 166 × 221 |
| Search: Minecraft | 52 × 72 | 285 × 380 | 166 × 221 |

`BrowsePayloadMapper` now normalizes fixed dimensions in Twitch box-art filenames to the same `285x380` size used for templates. It preserves the returned filename, including `_IGDB` suffixes and encoded category names, and preserves query strings and fragments. Normalization applies to the exact Twitch CDN host and box-art path. Returning the same URL for the same artwork also lets browsing and searching reuse the existing image cache.

The deterministic regression tests cover fixed-size and template URLs, protocol-relative URLs, artwork identity, URL suffixes, empty artwork, and unrelated hosts and paths. The corrected category-search test also checks result order, pagination, viewer totals, and the absence of additional category requests.

Run the focused regression tests with:

```powershell
./scripts/dev.ps1 Test -Filter 'Twitch category thumbnails:'
```

Run the live probe with an existing configured Twitch account:

```powershell
$env:SVS_TEST_TWITCH_CATEGORY_THUMBNAILS_LIVE = '1'
$env:SVS_TEST_ARTIFACT_DIR = Join-Path (Get-Location) '.artifacts\twitch-category-thumbnails\live'
./scripts/dev.ps1 Test -Filter 'Twitch category thumbnails:'
```

The live probe uses offscreen WPF rendering. It saves public category response fields, decoded and layout dimensions, and a PNG of the card templates. It does not record credentials or HTTP headers. The verification runs are retained locally in `.artifacts/twitch-category-thumbnails/before` and `after`; direct CDN download measurements are in `cdn/dimensions.json`.

Verification on 2026-10-05: the Release build completed with zero warnings or errors; all four focused tests, including the live probe, passed; formatting verification and `git diff --check` passed. The complete headless suite passed 1,617 tests and skipped 275 desktop tests. Its log is retained at `.artifacts/twitch-category-thumbnails/full-suite.log`.

## Missing artwork in `war` searches (2026-10-05)

The gray controller in the reported screenshot is Twitch's missing-art image. A live scan of the first 100 `war` search results reproduced it for these entries:

| Category | Twitch ID | IGDB ID returned by Get Games |
| --- | --- | --- |
| Warpath 97 | 15188 | 77306 |
| WAR | 1050305122 | 383254 |
| WarZone | 1086918202 | 370920 |
| War | 1594951771 | 288640 |

For each entry, the original `52x72` search URL and normalized `285x380` URL return HTTP 302 to `https://static-cdn.jtvnw.net/ttv-static/404_boxart-{size}.jpg`. That destination returns HTTP 200 and a valid JPEG. The four downloaded large images match the missing-art image byte for byte, with SHA-256 `8A52C09FB9F8DAB346472275E3C9CB81EE341252ED8642481724D44CDA431F33`. World of Warcraft was a positive control: both sizes returned HTTP 200 directly with its actual artwork.

The [Get Games endpoint](https://dev.twitch.tv/docs/api/reference/#get-games), queried using the exact category IDs, returned the same unavailable artwork URLs. Twitch's website GraphQL `Game.boxArtURL` field also returned those URLs for the same IDs. Checking the CDN's existing `_IGDB` filename variant did not recover artwork. This rules out the earlier search-resolution problem and avoids substituting an image belonging to another game. Twitch currently supplies no cover for these entries; the application cannot restore absent upstream content.

The application was treating the HTTP 200 missing-art JPEG as a successful category cover, which also kept it in the successful image cache. An opt-in live regression bound all four missing categories and the positive control to the production card template and failed before changing the image loader: Warpath 97 had a decoded image despite the confirmed missing-art redirect.

`AnimatedEmoteImage` now recognizes the final missing-art URI on Twitch's exact CDN host before decoding it. It uses the existing 30-second failure cache, exposes a read-only failure state, and clears that state for a new image request or successful recovery. Category cards show **Artwork unavailable** for failed or absent artwork and keep that label hidden while a request is pending. Other image hosts, valid covers, category identities, and pagination retain their existing handling.

Category refreshes retain their card controls and URLs. The card image now observes the category-loading state and retries a failed load when a refresh completes. That checks the existing cache again, allowing an expired failure to recover on the same card and URL. Healthy images continue using their decoded sources. There are no background polling requests or generated artwork URLs.

Five deterministic regressions exercise HTTP 200 missing-art responses, failure-cache coalescing and expiry, valid covers and unrelated hosts, loading/empty/invalid/recovered card states, refresh recovery on retained controls, and late failures after a newer cover has loaded. The live regression verifies the real CDN responses and renders the actual card template, including its unavailable label. Run them using the focused and opt-in live commands above.

The current verification artifacts are retained locally under `.artifacts/twitch-category-missing-art/`: `scan/pages.json`, `scan/gql.json`, `scan/redirects.json`, and `scan/images.json` contain public response fields and download evidence; `before/` and `after/` contain rendered cards and their expected/actual image states. Credentials and authorization headers are excluded.

The Release build completed with zero warnings or errors, and all ten focused tests passed, including both live probes. The first broader run passed 1,620 tests and skipped 275, with one settings-file sharing failure in the existing pause-hotkey settings test. That test passed in its isolated recheck; the logs are `full-suite.log` and `hotkey-recheck.log` in the artifact directory.

The final headless run passed all 1,622 tests, with 275 desktop tests skipped and no failures (`full-suite-final.log`). Formatting verification for the changed C# files and `git diff --check` also passed. The generated production-card images were visually inspected before and after the fix.
