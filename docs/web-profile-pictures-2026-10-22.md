# Web profile pictures, 2026-10-22

The web UI rendered initials without image elements in the sidebar, spotlight
and library cards. Its card responses also omitted the provider models' existing
`ProfileImageUrl`. VOD target construction discarded the broadcaster's image,
and recent-history normalization did not retain a separate profile-image field.

The API now preserves profile URLs across following, search, category streams,
videos, downloads and player creation. Recent-history settings retain the image
through normalization, serialization and desktop metadata updates. Channel-profile
refreshes update the open-player sidebar image and saved recent history.

All avatar locations use the same renderer. It retains initials until an image
loads, hides them after successful decoding (including transparent pictures), and
keeps them visible for missing, rejected or failed URLs. Images fill the circular
avatar without changing the open-stream status marker. Requests omit referrers.
Remote image URLs must use HTTPS without embedded credentials.

An acquired downloaded-video player can serve its adjacent `profile-image` cache
through `/media/{player}/profile`, using the existing authenticated session and
player lifetime. The response accepts bounded raster image bytes and reports their
actual image content type. No file URL or host filesystem path reaches the browser.

## Verification

- Before the fix, the real Chromium regression failed waiting for sidebar image
  elements despite valid profile URLs in its responses:
  `.artifacts/profile-pictures-before.log`.
- The production `LocalWebChannelProfileClient` resolved NiceWigg's Twitch PNG
  and xQc's Kick WebP. Both CDN requests returned HTTP 200 with image content
  types: `.artifacts/profile-pictures-live-probe.log`. The Kick fixture's URL and
  SHA-256 are recorded in `tests/StreamlinkVlcStudio.Tests/Fixtures/profile-images/README.md`.
- `node scripts/tests/web-navigation.browser.cjs` passes with the production
  HTML/CSS/JS. Its profile assertions cover sidebar, spotlight, cards, recent
  history, videos, downloads, refreshed player images, transparent PNGs, Kick
  WebP decoding, delayed loads, broken images and rejected credential-bearing
  URLs. Existing navigation/playback checks cover 11 widths from 320 to 1920
  pixels. Screenshots were visually reviewed:
  `.artifacts/player-navigation/profile-pictures-desktop.png` and
  `profile-pictures-fallbacks.png`.
- The focused workspace regression covers provider-to-JSON propagation, VOD
  playback, downloads, saved history, unsafe URLs, and cached PNG/WebP bytes:
  `.artifacts/profile-pictures-focused-tests.log`.
- All 24 local-web tests pass with zero skips, including real HTTPS, session
  isolation and browser playback: `.artifacts/profile-pictures-local-web-tests.log`.
- All nine recent-history resource regressions pass:
  `.artifacts/profile-pictures-recent-tests.log`. Web volume and overlay geometry
  regressions also pass.
- The Release solution build passes with zero warnings and zero errors:
  `.artifacts/profile-pictures-isolated-build.log`.

The normal Release output is locked by the running desktop app. The verified
build is isolated at
`.artifacts/profile-pictures-build/bin/Release/net10.0-windows10.0.19041.0/StreamStudio.exe`.
Close the existing app before launching that build and starting its web server.
The browser fixture uses deterministic responses; the separate live probe verifies
the production provider client and real CDN bytes.
