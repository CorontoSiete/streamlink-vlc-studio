# Kick WebUI playback failure — November 4, 2026

The failure was reproduced with xQc's live Kick broadcast. The production
WebUI displayed the exact reported message, “Playback stopped. Reload the
quality or reopen this stream.” hls.js reported a fatal `manifestLoadError`:
the media playlist request returned HTTP 400, with no decoded video.

Streamlink resolved the stream successfully. Sending the same resolved
playlist through `LocalWebMedia.ServeAsync` raised
`InvalidDataException: Unsupported absolute URL in HLS metadata.`
The captured playlist contains two absolute `#EXT-X-PREFETCH:` segment hints.
The proxy recognized `#EXT-X-TWITCH-PREFETCH:` but did not recognize Kick/IVS's
`#EXT-X-PREFETCH:` spelling. Its guard against unproxied URLs consequently
rejected the entire playlist before video could load. This was neither an
offline broadcast nor a codec failure.

`LocalWebMedia.RewritePlaylist` now maps those references through the existing
resource registry and URI validation, preserving the tag. It does not turn
prefetch hints into complete segments, alter timing, reduce quality, or relax
the guard on unknown metadata. The [upstream LHLS proposal](https://video-dev.github.io/hlsjs-rfcs/docs/0001-lhls.html#prefetch-media-segments)
describes this tag and its URI format.

## Verification

Evidence is under `.artifacts/kick-web-playback-2026-11-04/`.

- The live baseline returned HTTP 400 and the exact reported browser message;
  `baseline-live.png` records it. The raw playlist captures retain the provider
  tag structure. Their CDN signatures expire.
- The new rewrite and unsafe-reference tests failed against the unchanged
  proxy (`baseline-tests.log`). The final browser fixture also failed with
  the exact message and `manifestLoadError` (`baseline-browser.log`). The
  first browser-fixture draft did not append hints because its source fixture
  has no ENDLIST tag; it was corrected and rerun before the product fix.
- The repaired working-tree test build passed, with the existing NU1903
  advisory for the maintenance project's pinned ILCompiler package retained
  in the build log. No dependency versions were changed.
- All **46 WebUI tests passed, zero skipped**, including real HTTPS/browser
  playback, URI safety, account boundaries, offline media, VOD chat and overlays
  (`fixed-web-tests.log`).
- All **51 existing Node playback/mobile/volume tests passed**
  (`node-playback-tests.log`).
- The exact installation repair's managed assemblies passed all **seven
  prefetch-related tests, zero skipped**, including the three new regressions
  (`candidate-prefetch-tests.log`). Tests cover absolute and relative signed
  references, fetching an opaque prefetch resource, stable identity on playlist
  refresh, discontinuity preservation and unsafe/malformed references.
- The exact repair's media assembly played the actual live broadcast at
  **1920×1080** for **32 seconds**, advancing **31.94 seconds**, decoding
  **2,007 frames with zero dropped frames**. All 17 playlist and 26 segment
  requests returned HTTP 200, with no hls.js errors. See
  `live-verification.json` and `candidate-live.png`.

The live probe uses the real embedded WebUI and production HTTPS server/media
proxy on localhost, with a temporary test certificate and test account. It
fetches real live provider media. It does not establish provider-chat behavior
or playback on a physical remote browser.

## Installed repair

The repair is built from installed release commit
`c80cc848e8cacfa983a1f3b57fb933840d4e2a41`, with the existing search-click repair
preserved and only the two-line Kick prefetch change added. Unrelated working
tree edits are excluded. Strict locked restore and self-contained publish
pass with warnings treated as errors (`candidate-restore.log`,
`candidate-publish.log`). The executable passes all built-in Streamlink, VLC,
WebView2, Skia and HarfBuzz dependency checks (`candidate-health.stdout.log`).

The installation script verifies the base commit, prior repair hash,
installation ownership and validated candidate hash. It backs up the existing
executable and ownership records, replaces the executable atomically, verifies
its hash and updates the managed-file record. Failure restores the backup.
The installed informational version is
`1.8.10+search-click-kick-web-fix.20261104`; release/update identity remains
1.8.10. `installation-result.json` records executable hashes and the backup
directory.

Installation completed successfully. The installed executable's SHA-256 is
`EE3AFDD5FFAB8BBC07DFD2688E5EC12CE556A47ACE167770DE7BB02683154966`;
its product version matches the validated combined repair. The installed
executable's dependency verification also exits 0
(`installed-health.stdout.log`). Changed-file whitespace verification passed.
