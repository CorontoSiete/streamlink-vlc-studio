# NiceWigg avatar rendering, 2026-10-01

NiceWigg's transparent avatar revealed the default account icon underneath it.
The live card, offline followed card, VOD card, and tab each rendered that icon
unconditionally behind `AnimatedEmoteImage`.

The public Twitch profile response identified login `nicewigg`, display name
`NiceWigg`, and user ID `415954300`. Both returned avatar URLs downloaded with
HTTP 200 and `image/png`. The unmodified
[150-pixel avatar](https://static-cdn.jtvnw.net/jtv_user_pictures/a10b288f-886d-4cc5-b406-9f42213dadb4-profile_image-150x150.png)
is a 7,703-byte RGBA PNG with 19,942 fully transparent pixels out of 22,500.
The application's decoded pixels match an independent load of that PNG exactly.

The four production templates now hide the account icon by default and show it
when the avatar control's decoded `Source` is null. This covers pending, failed,
and empty loads, as well as replacing a previously loaded image. The image's
transparency is preserved, and the change applies to every channel using these
templates.

Regression tests use the saved real CDN PNG and apply the production templates
through a `ContentControl`. They compare the complete rendered avatar against a
reference containing only the PNG and its surrounding border. The test host loads
each production color palette locally and asserts the actual background color
before checking pixels. Rendering captures the complete local canvas, including
avatars that are centered inside larger cards.

| Surface | Different pixels before, dark | After, dark | After, light |
| --- | ---: | ---: | ---: |
| Live card | 81 | 0 | 0 |
| Offline followed card | 124 | 0 | 0 |
| VOD card | 71 | 0 | 0 |
| Tab | 43 | 0 | 0 |

The original templates failed all four image comparisons. After the fix, all
eight avatar regressions pass, including pending requests, unsuccessful file
loads, empty URLs, and loading the same avatar again from the completed cache.
The broader profile run passes all 27 tests with zero skips. The related emote
run passes 23 tests; 13 desktop tests are skipped in headless mode. The Release
solution build reports zero warnings and zero errors.

```powershell
.\scripts\dev.ps1 Test -Filter 'profile'
.\scripts\dev.ps1 Test -Filter 'emote' -NoBuild
```

The downloaded profile response, HTTP headers, original PNGs, before/after
renderings, and test logs are preserved locally under
`.artifacts/nicewigg-avatar-diagnosis/`. To save new renderings, set
`SVS_TEST_ARTIFACT_DIR` before running the tests. The permanent PNG fixture and its
SHA-256 provenance are under `tests/StreamlinkVlcStudio.Tests/Fixtures/profile-images/`.
