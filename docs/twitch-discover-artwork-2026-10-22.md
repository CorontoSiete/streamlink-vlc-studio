# Twitch Discover category artwork

## Valid covers rejected by their upstream content type

The first missing-art loader required `Content-Type: image/jpeg`. Twitch returns valid JPEG bytes with an `image/png` header for categories including League of Legends, World of Warcraft, Counter-Strike, Dota 2, VALORANT and IRL. Those healthy covers were incorrectly returned as 404. The original live positive control, Just Chatting, had a matching JPEG header and did not expose this regression.

The regression was reproduced before changing production code: a captured League of Legends response failed the new test with expected 200, actual 404. A live normal directory page plus Slots and Games + Demos searches checked 52 category IDs and reproduced 16 false unavailable covers. The public CDN headers, exact encoded payloads and before/after results are retained under `.artifacts/twitch-discover-artwork/regression/`.

The loader now validates the encoded image signature and serves the corresponding content type, independent of Twitch's header. The byte cache retains both the payload and its detected type. Signature detection is shared with cached profile images, preserving their existing behavior. Missing-art redirect detection, provider URL validation, byte limits and cache expiration still apply. Unknown/non-image bodies remain unavailable even when labeled as images.

Deterministic tests replay the six actual mislabeled covers, check JPEG/PNG/WebP payloads with incorrect or absent headers, reject non-image bodies, and check cached response types. The HTTPS/WebView2 regression decodes each captured cover in actual cards. The opt-in live check now independently downloads and decodes every cover in the normal 50-category page, checks the exact bytes served by the production workspace, and loads a fresh-cache 50-card page through the authenticated HTTPS server and real browser at desktop and phone widths.

## Confirmed missing artwork

Live investigation on 2026-10-22 confirmed the source of the default controller image:

| Category | Twitch ID | Requested artwork | CDN result |
| --- | --- | --- | --- |
| Slots | 498566 | `/ttv-boxart/498566-285x380.jpg` | 302 to `/ttv-static/404_boxart-285x380.jpg` |
| Games + Demos | 66082 | `/ttv-boxart/66082-285x380.jpg` | Same missing-art redirect |
| Just Chatting | 509658 | `/ttv-boxart/509658-285x380.jpg` | 200, actual 285 x 380 cover |

Twitch's website GraphQL `Game.boxArtURL` returned the same numeric cover URLs. The website's Games + Demos metadata used `66082-272x380.jpg`, which also redirected to missing art. Name-based CDN probes did not recover either cover: Games + Demos redirected, while Slots returned the controller image directly. These were diagnostic probes, not production replacements.

The redirected missing-art JPEG returns HTTP 200, 5,691 bytes, SHA-256 `8A52C09FB9F8DAB346472275E3C9CB81EE341252ED8642481724D44CDA431F33`. A browser `<img>` therefore fires `load`, not `error`; the existing error handler could not distinguish it from real artwork. Twitch currently supplies no usable cover through these sources. The fix does not invent replacement artwork.

Twitch covers returned by the web workspace now receive an opaque, same-origin `/category-artwork/` route. The authenticated server fetches the registered provider URL, checks redirects before downloading a missing-art JPEG, and returns 404 with no image bytes for unavailable artwork. The browser then displays category initials and **Artwork unavailable**. Pending images retain their loading state, actual covers load normally, and missing/empty/broken images do not affect viewer totals or category navigation. Search and pagination use the same handling. Kick artwork keeps its existing path.

Only the exact HTTPS Twitch CDN host and box-art path are fetchable; redirects cannot select another host. Browser cookies and account credentials are never forwarded. Downloads are lazy, limited to four at once and 256 KiB per image. The shared LRU cache holds up to 128 entries, retains successes for ten minutes and failures for 30 seconds. Responses use `no-store` so refreshing after the failure interval can recover artwork at the same URL. Category listing performs no image requests and remains independent of image download latency.

Run deterministic and actual HTTPS/WebView2 browser regressions:

```powershell
./scripts/dev.ps1 Test -Filter 'local web: category artwork'
```

Include the live Helix/CDN/workspace check with an existing configured Twitch account:

```powershell
$env:SVS_TEST_TWITCH_DISCOVER_ARTWORK_LIVE = '1'
$env:SVS_TEST_ARTIFACT_DIR = Join-Path (Get-Location) '.artifacts/twitch-discover-artwork'
./scripts/dev.ps1 Test -Filter 'local web: category artwork'
```

The live check compares each web response with an independent direct CDN request. On the investigation date both reported categories produced 404 with zero cover bytes; Just Chatting produced 200 with 25,276 bytes. The expanded scan also found a second category named Slots (`1429674651`) returning the same missing-art redirect. It records only public artwork metadata. The browser regression uses captured public JPEG fixtures to verify pending, missing, valid and simulated recovered states, refresh, search, viewer totals, category navigation, desktop and phone layouts, and absence of script/CSP errors. Its simulated recovery uses a positive-control JPEG as a test input; production always uses the exact cover Twitch returned.

Local evidence is under `.artifacts/twitch-discover-artwork/`; rendered browser captures are `.artifacts/local-web-design/category-artwork-desktop.png`, `category-artwork-mobile.png` and `category-artwork-recovered.png`.

Verification after the content-type correction: Release solution build succeeded with zero warnings/errors. All 35 local web tests, including the expanded live test, and all eight existing Twitch category-thumbnail regressions passed, with zero skips. The direct/workspace scan checked 52 IDs: 49 healthy covers matched the CDN byte for byte and three entries had confirmed missing-art redirects (two IDs named Slots and Games + Demos). The fresh-cache HTTPS browser page rendered all 50 cards: 49 decoded covers, one confirmed missing Slots cover, zero falsely unavailable covers, and no failed image requests beyond that expected 404. JavaScript syntax, changed C# whitespace formatting and `git diff --check` passed. Desktop and phone captures were visually inspected.

The expanded results are `.artifacts/twitch-discover-artwork/regression/after/live-category-artwork.json` and `live-browser-category-artwork.json`; the full suite log is `regression/web-suite.log`. Live browser captures are `.artifacts/local-web-design/category-artwork-live-directory-desktop.png` and `category-artwork-live-directory-mobile.png`.
