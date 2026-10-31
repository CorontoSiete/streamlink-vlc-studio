# Twitch subscriber badge recovery

The reported symptom was a boxed **Subscriber** label for some messages in Alinity's live chat in Brave.
The original server session had stopped, so its initial failing network request could not be recovered. The investigation reproduced the same persistent text fallback using the original application renderer in an isolated Brave profile and one controlled CDN HTTP 503. This establishes the application recovery defect without attributing the original request failure to Brave Shields, credentials, or a missing Twitch asset.

## Evidence

- Twitch identifies Alinity as broadcaster `38718052`. Both Helix and the public Twitch web catalog returned 30 subscriber badge versions.
- The saved token validated successfully and its client ID matched the configured ID. Authenticated channel badge requests succeeded. Credentials were used only in memory and were not written to the evidence files.
- All 30 current 36-pixel image URLs returned HTTP 200 with PNG bytes and decoded successfully in both Chrome and Brave.
- `badges.twitch.tv`, the former fallback hostname, failed DNS resolution. The replacement public query was tested against Twitch with both channel IDs and global badges.
- With the original renderer, a single failed CDN request produced the boxed label and exactly one image request. Presenting the identical message again did not retry: `renderChatRow` returns early for an unchanged message. Screenshot: `.artifacts/twitch-badges/brave-before/transient-failure.png`.
- A second recovery gap was in `chat/presentation`: an unchanged catalog version skipped catalog loading entirely. Failed catalog loads never increment that version, leaving quiet channels unable to retry after the coordinator's backoff expired.

## Changes

The shared browser image renderer now retries after 1, 5, and 15 seconds, preserves readable fallback text while loading, and installs a successfully decoded image into the existing row. Detached or replaced message fallbacks stop retries. Permanent failures have a finite four-request budget. Healthy unchanged rows preserve their existing images.

The shared Twitch catalog keeps Helix as its preferred source and replaces the dead fallback with Twitch's anonymous web badge catalog. Channel responses must match the requested room ID. Exact version IDs, channel scope, existing numeric lookup rules, bundled global overrides, byte limits, deduplication, expiration and failure backoff remain in effect. Account authorization is never sent to the public fallback.

Presentation polls now ensure catalog loading once per distinct message scope before comparing versions. This permits a failed catalog to retry in quiet live chat while retaining the existing response optimization and retry delays. The same shared path serves other streamers and replay chat.

## Verification

The captured fixtures cover all 115 channel badge entries from Alinity, Pokimane and shroud, including subscriber tiers and bits. Tests exercise exact lookup, channel isolation, anonymous fallback after 401/403/503 responses, malformed public replies, retry backoff, global bundled overrides, and recovery of an existing live message without new chat traffic.

The real-browser regression executes production HTML, CSS and JavaScript with only authentication startup replaced by a renderer test entry point. Controlled CDN replies cover transient and permanent failures, unchanged messages, removed/replaced rows, text-node emote fallbacks, unsafe URLs and a virtual-time check that no further requests occur after the retry budget. `--live-cdn` additionally loads all 30 actual Alinity CDN URLs through the production renderer. It does not attach to a user's browser profile or claim to reproduce the original network outage.

Commands:

```powershell
$env:SVS_TEST_LIVE_TWITCH_BADGES = 'true'
./scripts/dev.ps1 Test -Filter 'Twitch badge recovery:' -ExpectedMaxSkips 0
./scripts/dev.ps1 Test -Filter 'local web:' -NoBuild
./scripts/dev.ps1 Test -Filter 'chat catalog' -NoBuild
$env:SVS_WEB_TEST_BROWSER = 'C:/Program Files/BraveSoftware/Brave-Browser/Application/brave.exe'
$env:SVS_BADGE_TEST_LABEL = 'brave-live-cdn-final'
node scripts/tests/web-chat-images.browser.cjs --live-cdn
```

The live catalog test is opt-in because Twitch may later change its images or versions. Its exact comparisons intentionally detect that drift instead of silently accepting a generic subscriber image.

## Completed validation

- Release solution build: zero warnings and errors; formatting and JavaScript syntax checks passed.
- All 35 local-web tests passed, including actual HTTPS/WebView2 playback, chat, quiet-channel recovery, overlay layout, fullscreen, mobile layouts and volume. Zero skipped.
- All 16 shared chat-catalog tests and all six badge-recovery tests passed. The latter included current live Twitch catalog responses for all three broadcasters. Zero skipped.
- The existing volume, layout-persistence and overlay-geometry JavaScript regressions passed.
- Chrome and Brave passed the controlled image-failure browser checks. The final Brave run additionally passed all 30 actual CDN URLs, emote recovery, removed/replaced rows, and a further two minutes of virtual time after exhausting the permanent-failure retry budget.
- Self-contained Windows x64 single-file publication succeeded. Runnable build: `.artifacts/twitch-badges/app/StreamStudio.exe` (SHA-256 `31DD9965BB2BA1B0C24A974CA3D16FF1492B1E500DE1FEBF28CB41B2D4346BC9`). Start that build's web server and reload the browser to use the updated embedded assets.

Logs are in `.artifacts/twitch-badges/{catalog-tests,chat-catalog-tests,web-tests,brave-tests,publish}.log`.
The final live-image screenshot is `.artifacts/twitch-badges/brave-live-cdn-final/recovered-badges.png`; its last row deliberately retains text for the injected permanently failing test URL.
