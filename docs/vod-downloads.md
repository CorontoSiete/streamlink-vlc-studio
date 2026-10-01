# Twitch and Kick VOD downloads

## Using the library

1. Choose **Download quality** in **Settings > Downloads**, then open **Past broadcasts** and click **Download** on the bottom-right of a Twitch or Kick VOD thumbnail. Alternatively, open **Downloads**, paste a Twitch `/videos/{id}` or Kick `/{channel}/videos/{id}` page URL, and click **Download VOD**.
2. The card button shows **Queued**, **Preparing…**, the actual segment-completion percentage with an integrated progress bar, and **Finishing…** while the offline files are committed. It changes to a green-check **Downloaded** button only after completion; click it to watch offline. Downloads run in the background while other tabs play. Changing download quality does not change an existing tab's playback quality.
3. Use **Watch offline** from the Downloads library, including after restarting the app without internet. The tab title includes `(offline)`.
4. **Cancel** removes unfinished media. **Retry** refreshes the provider URL with the current Streamlink settings and starts the whole download again. **Delete** asks for confirmation and removes only that download. Close its offline playback tab before deleting or retrying it.

Card status follows the selected download quality and the saved library, including downloads started by URL, new search results, and app restarts. Active downloads cannot be queued again from their card. Failed, canceled, or interrupted entries show **Retry** on the card; retry reuses that entry. Deleting the entry restores the card's **Download** action. Hover over the button for quality, progress, and failure details; manage cancellation and deletion in **Downloads**.

Offline playback supports pause, seek, reload, and saved VOD resume positions. Online and offline copies have separate tabs but share the same platform/video history. Replay chat is not included and is not fetched during offline playback.

Downloaded VOD tabs retain the broadcaster's profile picture. When the selected VOD includes an avatar, new downloads save a bounded local copy alongside the media so it also appears after restarting without internet. Existing downloads keep their saved profile-image URL and do not require downloading the video again. A missing or unavailable avatar never prevents the video from downloading or playing.

The configured Streamlink executable is needed to select the online rendition. Completed packages need only the existing VLC playback installation, not Streamlink or an online provider lookup.

## Storage and completeness

**Settings > Downloads** saves the download quality, bandwidth limit, and destination automatically. Both the library and Past broadcasts link to this page. Download quality is independent of playback quality and applies when a new download is queued. Existing and retried entries retain their original quality.

The bandwidth limit is in **MB/s** (1 MB = 1,000,000 bytes). **0** means unlimited. Decimal limits are supported, and the limit is shared across all concurrent VOD media transfers, including muted-segment repair. Changes apply to active downloads without limiting live playback or replay chat. Cancellation remains responsive while a transfer waits for bandwidth. Network reads still time out if they stall, but deliberate throttling does not exhaust the HTTP request deadline.

Use **Browse...** to choose a local download folder and **Open folder** to inspect it. New downloads use the selected folder immediately. Existing, active, queued, and retried downloads keep their original folders; files are not moved or deleted. Those folders are remembered so the complete library remains available after restarting, including without internet. Network shares and symbolic-link library roots are rejected.

The default location is the Windows **Videos** folder, under `StreamStudio VODs`. **Open download folder** shows the actual location. If Videos is missing or redirected to a network share, the app chooses a local Videos/profile or Local AppData location instead; network shares cannot be used for offline storage.

Each entry has an independent GUID-named folder:

```text
StreamStudio VODs/
  <download-id>/
    download.json
    media/
      index.m3u8
      package.json
      profile-image
      asset-000000.ts
      asset-000001.ts
```

Additional `.key`, `.mp4`, `.m4s`, or other assets are included when needed. These are self-contained HLS packages, not single remuxed MP4 files. Keep the complete folder together when backing up the library. The local playlist never depends on expiring CDN links, remote encryption keys, or the provider continuing to retain the VOD.

Downloads are written to `.partial` first. Only after every resource succeeds and the local playlist and inventory are written does the app promote the directory to `media`. A failed segment cannot silently produce a completed VOD. Restarted apps recover a fully committed package even if the last metadata update was interrupted; incomplete jobs become **Interrupted** and require Retry.

At library load and before playback, the app checks the package format, playlist hash, relative references, file existence and lengths, and AES key sizes. Missing/truncated files, changed playlists, path traversal, and directory junctions inside the library are rejected. File lengths are checked, not a full per-segment checksum scan of multi-gigabyte media.

Connection options are not serialized into download records. Records retain the canonical VOD page rather than signed CDN URLs, and stored failure diagnostics redact tokens and configured argument values. The library is outside the app installation and ordinary settings directory; removing/updating the app does not erase these media folders.

## Provider handling and limits

- Only completed media playlists with `EXT-X-ENDLIST` are accepted. Live streams, clips, still-growing VODs, missing segments, and playlists with explicit gaps are not saved as complete downloads.
- Streamlink selects the requested rendition. The downloader preserves segment order, sequence, discontinuities, initialization data, byte ranges, and identity AES-128 keys. It does not concatenate transport streams or require FFmpeg.
- Clear Twitch `-muted.ts` segments use the existing timestamp repair while downloading. Muted audio remains muted; the repair does not reconstruct removed audio.
- The existing subscriber-only Twitch fallback is supported, including its local source playlist with approved HTTPS media references. Its existing availability and quality-selection limitations still apply.
- Kick browser cards can supply their recording source directly. Pasted Kick pages and retried/expired sources resolve actual channel metadata and the website recording URL before quality selection, without relying solely on an older Streamlink Kick API route. If Kick changes its website format, the configured Streamlink plugin is tried instead.
- DRM/sample encryption, unsupported external-resource tags, oversized resources, and ambiguous byte ranges fail explicitly rather than guessing or producing incomplete packages.
- One VOD job runs at a time, with up to four concurrent asset transfers and three attempts for transient transfer failures. Safety bounds are 200,000 assets, 512 MiB per asset, and 14 days of media duration. Leave sufficient local disk space; large VODs can require many gigabytes.
- `audio_only` saves the selected audio rendition when the provider exposes one. The subscriber-only Twitch fallback retains its existing mapping to a low video variant.

## Validation

Run the offline subsystem tests through the existing development script:

```powershell
.\scripts\dev.ps1 Test -Filter 'VOD downloads:' -ExpectedMaxSkips 0
```

To include real libVLC playback tests and render the wide/compact library:

```powershell
$env:SVS_TEST_VLC_DIRECTORY = 'C:\Program Files\VideoLAN\VLC'
$env:SVS_RESPONSIVE_SCREENSHOTS = Join-Path $PWD 'artifacts\vod-downloads\ui'
.\scripts\dev.ps1 Test -Filter 'VOD downloads:' -ExpectedMaxSkips 0
```

The native tests download complete generated video/audio and tone fixtures, deny further provider HTTP access, and check actual decoding, seek, pause, resume/history restoration, local AES keys, audio-only media, and paths containing spaces and Unicode. Other tests cover offline reload, persistence, atomic recovery, cancellation, retries, safe deletion, byte ranges, malformed/truncated responses, URL validation, credential redaction, and the real Kick page/API data shapes.

An explicit opt-in compatibility probe resolves a real public VOD, validates its complete selected playlist, and reads only a bounded prefix of its first segment. It does **not** claim to download a full public VOD:

```powershell
$env:SVS_TEST_VOD_PROBE_URL = '<currently available public Twitch or Kick VOD page>'
$env:SVS_TEST_VOD_PROBE_STREAMLINK = 'C:\Program Files\Streamlink\bin\streamlink.exe'
$env:SVS_TEST_TIMEOUT_SECONDS = '60'
.\scripts\dev.ps1 Test -Filter 'compatibility probe' -ExpectedMaxSkips 0
```
