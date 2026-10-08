# SableOC black video with audio — 2026-10-07

The input selected by Streamlink contained only audio. The saved `studio.log`
records `Available streams: audio_only (worst, best)` and
`Opening stream: audio_only (hls)` on every SableOC open between 06:33 and 06:41
EDT. VLC received that audio transport, and the live-health monitor reloaded it
after finding no video output for 30 seconds. The same audio-only selection was
reproduced directly with both installed Streamlink versions, 8.4.0 and 8.5.0.

Anonymous Twitch playback authorization returned `maximum_resolution=FULL_HD`
and `AUTHZ_NOT_LOGGED_IN` for QUAD_HD and ULTRA_HD. Its signed master playlist
contained only an AAC audio rendition. Requesting H.264, all three supported
codecs, multiple video groups, and different player types all retained that
audio-only master. This ruled out an omitted HEVC codec or a portrait-stream
parser problem as the cause of this incident.

The connected app account's token validated successfully against Twitch's OAuth
validation endpoint, but Twitch's website playback API rejected it with HTTP
401. The existing Twitch website cookie in the app's WebView2 profile was
accepted by the same API. Its signed master playlist then exposed the source:

```text
RESOLUTION=2304x1440
FRAME-RATE=60.000
CODECS="avc1.640034,mp4a.40.2"
IVS-NAME="1440p60"
IVS-VARIANT-SOURCE="source"
```

There was no lower-resolution video rendition. Twitch's anonymous resolution
limit therefore removed the entire video choice. The distinction between
website playback authentication and anonymous higher-resolution access also
appears in the [Streamlink maintainer's explanation](https://github.com/streamlink/streamlink/discussions/6991).

## Change

The production Streamlink service recognizes an audio-only live Twitch result
before handing a transport to VLC when video was requested. It disposes that
process, reads the existing website cookie on the WPF dispatcher, and retries
once with Twitch's documented `--twitch-api-header` authentication. It uses the
same persistent website session as the existing bonus sign-in; no new login,
copied settings secret, codec restriction change, or quality substitution is
needed for this machine.

Ordinary video keeps its existing Streamlink path. Explicit audio-only playback
remains supported, and explicit custom Twitch API identity arguments take
precedence. Missing or rejected website sessions provide sign-in instructions.
Process output is redacted before reaching either tab diagnostics or the logger.
Website session lookup honors cancellation and has a bounded timeout.

## Evidence and reproduction

Credential-free evidence is saved under `artifacts/sableoc-black-screen`:

- `manifest-comparison.json`: anonymous codec, multigroup, and player-type controls.
- `account-comparison.json`: validated app account versus the existing website session.
- `authentication-tests.log`, `browser-session-test.log`, and the related regression logs.
- `live/studio.log`, `live/live-playback.json`, and `live/source-frame.png`: production Streamlink/VLC playback.

Run the deterministic tests with:

```powershell
.\scripts\dev.ps1 Test -Filter 'Twitch video authentication:'
$env:SVS_TEST_TWITCH_BONUS_BROWSER = 'true'
.\scripts\dev.ps1 Test -Filter 'Twitch video authentication: real browser' -NoBuild -Interactive -ExpectedMaxSkips 0
```

The opt-in live probe uses the current user's existing Twitch website session.
SableOC must still be live with this source rendition:

```powershell
$env:SVS_TEST_SABLEOC_LIVE = 'true'
$env:SVS_TEST_TIMEOUT_SECONDS = '90'
$env:SVS_SABLEOC_ARTIFACT_DIRECTORY = Join-Path (Get-Location) 'artifacts\sableoc-black-screen\live'
.\scripts\dev.ps1 Test -Filter 'Twitch video authentication: live SableOC' -NoBuild -Interactive -ExpectedMaxSkips 0
```

## Verification

The live probe passed through the production Streamlink service, stream tab,
WebView2 cookie provider, VLC engine, and native chat-overlay renderer using the
current user's settings. Anonymous playback again returned only audio; one
website-authenticated retry exposed and opened `1440p60` at the requested `best`
quality. VLC reported a 2304x1440 source. Its native snapshot was inspected and
showed SableOC's Apex Legends stream.

Two health samples five seconds apart retained playback generation 1:

| Counter | First sample | Five seconds later |
| --- | ---: | ---: |
| Decoded video | 184 | 1394 |
| Displayed pictures | 74 | 677 |
| Decoded audio | 112 | 586 |

The selected renderer was GDI with DXVA2 enabled when available, native overlay
enabled, and hardware overlay composition enabled. The 60 fps designation is
from the source playlist; these counters demonstrate continuing playback rather
than a measured display refresh rate.

All 51 playback and related checks passed without skips: nine authentication
cases, one real browser-session case, ten owned-process cases, eight live-health
recovery cases, 22 Twitch bonus cases, and the live SableOC case. The solution
build completed with zero warnings and errors. A further 20 installer dependency
checks passed after the installation issue described below was corrected.

The installed application was then opened normally and SableOC selected through
its search UI. Its own log confirmed the same one-retry transition from
`audio_only` to `1440p60`. After 93.7 seconds, it had produced no new video-health
recovery warnings; the previous failure recurred every 30 seconds. The running
process path matched the fixed per-user installation, and the UI exposed the
native `VLC (WinGDI output)` pane with active playback controls. This evidence is
saved in `installed-playback.log` and `installed-playback-health.json`.

Windows desktop capture timed out for the player window. `live/source-frame.png`
is the actual VLC frame captured by the native live probe, and is separate from
a desktop screenshot.

## Local installation

The verified build was packaged and installed with the application's normal
per-user installer at:

```text
C:\Users\ComputerGuy\AppData\Local\Programs\StreamStudio\StreamStudio.exe
SHA-256: 2EBAEC35EB5A861E8393A3859E5EFAF11DC7B9A8FC34E5A3A0248D589C137CDE
```

Windows denied writes to the previous Program Files installation. Its executable
remains unchanged and a verified copy is saved in `installed-backup`. The normal
installer created a Start Menu shortcut, ownership records, and registered
uninstaller. The existing user taskbar shortcut was backed up and updated to the
fixed executable; its saved target was read back and verified.

Installation verified the already installed Streamlink 8.5.0, VLC 3.0.23,
WebView2 154.0.4258.62, Skia, and HarfBuzz. It selected the existing compatible
Streamlink installation in settings. Both installed Streamlink versions had
reproduced the original audio-only result, so that selection does not explain
the playback repair. The published and installed executable hashes match.
`installed-fix.json` records the paths, hashes, original backups, and source
revision. The local build retains version 1.8.5 and includes the working tree's
existing changes.

The first per-user installation attempt exposed a separate installer startup
error. The typed `[string]$DependencyManifest` path parameter collided with the
`$script:DependencyManifest` parsed-object cache. PowerShell converted the valid
manifest object to a string, causing the next validation to reject it. Renaming
the internal cache to `$script:ParsedDependencyManifest` preserves the public
parameter and its validation. A regression case runs the real installer entry
point with both default and explicit manifest paths and an invalid installation
target, proving it reaches directory validation before any installation work.
It failed before the fix and passed afterward along with all 19 related cases.

Run those installer checks with:

```powershell
.\scripts\tests\dependency-installation.tests.ps1
```
