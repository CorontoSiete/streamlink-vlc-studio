# Twitch & Kick player

Windows-first desktop app for watching Twitch and Kick streams through Streamlink and embedded libVLC.

## Current Features

- Browser extension capture for Twitch and Kick channel links; the extension prevents browser navigation and sends the clicked stream to the desktop app.
- Browser extension auto-claim for visible Twitch channel-point `Claim Bonus` buttons while a Twitch stream page is open.
- Streamlink external HTTP transport.
- Embedded libVLC playback in a WPF HWND surface.
- Quality presets: `best`, `source`, `1080p60`, `1080p`, `720p60`, `720p`, `480p`, `audio_only`, `worst`.
- Low-latency Streamlink defaults for Twitch/HLS.
- Platform replay seekbar for Twitch and best-effort Kick replays. Live playback keeps the existing Streamlink HTTP path; seeking behind live switches to platform VOD HLS playback in libVLC.
- Subscriber-only Twitch VOD playback: if Streamlink cannot resolve a Twitch VOD, the app falls back to a direct CloudFront playlist derived from the VOD's public storyboard metadata (TwitchNoSub technique). Pasting a `https://www.twitch.tv/videos/{id}` URL into the search box opens it directly. Very recent uploads cannot be resolved this way, and `audio_only` maps to the lowest video variant.
- Multiple tabs with add, close, rename, move left/right, reload, stop, pause, mute, volume, fullscreen, chat visibility, and an optional multi-stream grid for up to 16 streams. Only the selected tab is audible; inactive visible streams stay muted.
- Home page search for partial Twitch/Kick channel matches by streamer name, exact channel name, or channel URL.
- Home page showing live followed Twitch streams, configured Kick followed channel slugs that are currently live, Twitch/Kick VOD browsing, and recently watched streams.
- Per-tab state: target, quality, status, mute, chat visibility, logs, chat messages.
- Per-stream state: volume, VLC plugin chat overlay position, and VLC plugin chat text size are remembered by platform/channel.
- Twitch chat via anonymous read-only IRC, or authenticated IRC sending with a Twitch OAuth token.
- Kick chat via isolated public Pusher-style adapter, with OAuth chat sending through Kick's public API.
- Twitch replay chat from Twitch VOD chat GraphQL, with cached TwitchDownloader JSON files under `%APPDATA%\StreamlinkVlcStudio\replay-chat\twitch` still supported. Kick VOD replay chat uses verified official `chat.message.sent` webhooks captured into `%APPDATA%\StreamlinkVlcStudio\replay-chat\kick-official`; Kick's official REST API does not expose historical VOD chat.
- Native VLC plugin chat overlay mode using `vlc-overlay`, with draggable/resizable chat and in-overlay chat input.
- JSON settings and file logging.
- Dependency-free tests.

## Requirements

- Windows x64.
- Windows 10 or later, x64.
- Administrator permission for the per-machine installation under `C:\Program Files`.
- Internet access during the full installer so it can download the platform authorization pages and dependencies.

`StreamlinkVlcStudio-Setup.exe` is the normal installer. It contains the self-contained app, the official x64 Streamlink installer, and the official x64 VLC MSI. It installs the app and dependencies, creates the Start Menu shortcut, and then launches the first-run account wizard. The wizard never asks for a Twitch or Kick password: sign-in and consent happen in the platform browser. Streamlink and VLC are treated as shared dependencies and are left installed if the app is later removed.

`StreamlinkVlcStudio-Setup.msi` is the app-only Windows Installer package for advanced/manual use. It installs the app payload and bundled VLC overlay but does not install Streamlink or VLC. The release zip includes the separate PowerShell dependency workflow.

The self-contained GitHub release does not require the .NET SDK. Building from source requires the .NET 9 SDK selected by `global.json`, Streamlink, and VLC 64-bit with `libvlc.dll`.

## Install Latest Release

Download `StreamlinkVlcStudio-Setup.exe` from the latest GitHub release and run it. The installer walks through installing Streamlink, VLC, and the app, then offers a Launch button. Click Launch to open the setup wizard:

1. Twitch: create a Twitch developer app, set its redirect URL to exactly `http://localhost:39178`, enter its Client ID, and click Connect Twitch. The wizard requests `chat:read`, `chat:edit`, `user:read:follows`, and `channel:manage:predictions`.
2. Kick: create a Kick developer app, set its redirect URL to exactly `http://localhost:39177`, enter its Client ID and Client Secret, and click Connect Kick. The wizard requests `user:read`, `channel:read`, and `chat:write`.
3. Finish setup. Either platform can be skipped; public playback does not require an account. A connected platform is saved before the app opens normally.

The full installer uses `C:\Program Files\Streamlink VLC Studio` for the app so its Launch button is reliable. The app creates `Start Menu\Programs\Streamlink VLC Studio\Streamlink VLC Studio` and appears in Apps & features / Programs and Features.

Uninstall Streamlink VLC Studio from Apps & features / Control Panel. The bundle removes the app and shortcut but leaves shared Streamlink and VLC installations, and it leaves user settings and tokens under `%APPDATA%\StreamlinkVlcStudio`.

The release zip also provides the advanced PowerShell installer. It can install or update the app and dependencies from GitHub, or install dependencies only. GitHub install mode requires the repository and either its releases or Actions artifacts to be public. For a private repository, set `GITHUB_TOKEN` to a token with repo/actions read access before running the installer.

Useful installer options:

```powershell
# Force download from GitHub instead of using a local extracted app.
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1 -AppSource GitHub -Launch

# Install from an extracted release zip instead of GitHub.
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1 -AppSource Local -Launch

# Install or update dependencies only.
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1 -SkipApp

# Install the app to a custom folder.
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1 -InstallDir "C:\Users\you\Apps\StreamlinkVlcStudio"

# Update while the app is running by stopping it first.
powershell.exe -ExecutionPolicy Bypass -File .\install.ps1 -ForceStopApp
```

## Build And Run

From the repo root:

```powershell
$root = (Get-Location).Path
$env:TEMP = Join-Path $root ".tmp"
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
New-Item -ItemType Directory -Path $env:TEMP, $env:DOTNET_CLI_HOME -Force | Out-Null
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

& $dotnet restore StreamlinkVlcStudio.sln --ignore-failed-sources
& $dotnet build StreamlinkVlcStudio.sln --no-restore
& $dotnet run --project src\StreamlinkVlcStudio.App.Wpf\StreamlinkVlcStudio.App.Wpf.csproj --no-restore
```

## Test

```powershell
$root = (Get-Location).Path
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME -Force | Out-Null
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

& $dotnet test StreamlinkVlcStudio.sln --no-restore
node --test browser-extension\tests\content-core.test.js
```

## Browser Capture Extension

The reliable no-navigation flow uses the unpacked extension in `browser-extension`. It intercepts Twitch/Kick links before the browser routes to them, calls the local app listener at `http://127.0.0.1:39179/capture`, and leaves the browser on the current page. After each intercepted click, the page shows a small Twitch & Kick player status message so capture success or app-not-running failures are visible.

On Twitch pages, the same extension watches for the channel-point bonus control and clicks the visible `Claim Bonus` button automatically. This only acts on the Twitch page DOM: you still need to be logged in, have the Twitch stream page open, and have Twitch exposing a claimable bonus. Streamlink-only playback in VLC does not create a browser-side claim button by itself.

Install once in a Chromium browser:

1. Open `chrome://extensions`, `edge://extensions`, or `brave://extensions`.
2. Enable developer mode.
3. Click `Load unpacked`.
4. Select the repo's `browser-extension` folder, or the extracted `browser-extension` folder from the release zip.

The desktop app must be running before you click streams. If it is not running, the extension keeps the browser on the current page and shows a retry message.

## Package

Create a friend-ready release zip, app-only MSI, and full dependency installer:

```powershell
$root = (Get-Location).Path
& "$root\scripts\build-installer.ps1"
```

The package script publishes the app with the native VLC chat overlay embedded from `src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledOverlay\build` by default, stages the required sidecar `vlc-overlay\build` payload, stages only the Brave/Chromium capture extension runtime files from `browser-extension`, includes the local `install.ps1` for the advanced dependency workflow, and writes `release\StreamlinkVlcStudio-release.zip`. It fails if `build\libmyoverlay_plugin.dll`, `build\vlc_chat_overlay.exe`, or the required extension runtime files are missing.

The installer script runs the package script when `-ReleaseZip` is not supplied, builds `release\StreamlinkVlcStudio-Setup.msi` with WiX, resolves the official x64 Streamlink Windows installer from `streamlink/windows-builds`, resolves the official x64 VLC MSI from VideoLAN's `last/win64` directory, and embeds both dependencies in `release\StreamlinkVlcStudio-Setup.exe` with WiX Burn. The build fails if an unambiguous upstream asset cannot be found; it does not guess an asset. WiX 6.0.2 is installed into the repository's ignored `.tools` directory on first use. `-ProductVersion` must be a three-part numeric MSI version with each part from 0 through 255; the bundle uses the same version with a fourth `.0` field. Run `scripts\package-release.ps1` directly when only the zip is needed.

Framework-dependent Windows publish without creating a zip:

```powershell
$root = (Get-Location).Path
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME -Force | Out-Null
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

& $dotnet restore src\StreamlinkVlcStudio.App.Wpf\StreamlinkVlcStudio.App.Wpf.csproj -r win-x64 -s https://api.nuget.org/v3/index.json
& $dotnet publish src\StreamlinkVlcStudio.App.Wpf\StreamlinkVlcStudio.App.Wpf.csproj -c Release -r win-x64 --self-contained false
```

Packaging notes:

- Ship `StreamlinkVlcStudio-Setup.exe` for normal users, keep `StreamlinkVlcStudio-Setup.msi` as the app-only/manual option, and keep `StreamlinkVlcStudio-release.zip` for portable/manual inspection and the PowerShell dependency workflow.
- The local `release` directory contains those three distributable files by default.
- The MSI deliberately does not install the legacy `Uninstall.exe` or register a custom uninstall key; Windows Installer owns the MSI uninstall entry.
- The release zip includes the legacy `Uninstall.exe` only for the separate PowerShell/manual path. It is not used by the MSI.
- The release zip includes `install.ps1`, which can install/update the app and runtime dependencies from the latest upstream releases.
- The single executable embeds `vlc-overlay\build\libmyoverlay_plugin.dll` and `vlc-overlay\build\vlc_chat_overlay.exe` and extracts them on demand. The local release zip also includes a sidecar `vlc-overlay\build` copy for inspection or manual override use.
- The release zip includes `browser-extension\manifest.json` and the extension scripts; Brave/Chromium users can load that extracted folder directly.
- Do not bundle user Streamlink configs, tokens, browser cookies, or account data.
- The build and installer contain no user tokens, browser cookies, or account data. After authorization, the app stores its own settings and tokens in `%APPDATA%\StreamlinkVlcStudio\settings.json`.

## Configure Streamlink And VLC

Open Settings in the app and verify:

- Streamlink executable points to `streamlink.exe`.
- VLC directory points to the folder containing `libvlc.dll`.
- Custom Streamlink arguments contain only trusted options you want passed to Streamlink.

The app does not bypass ads, DRM, geo restrictions, or age gates. One exception: subscriber-only Twitch VODs can be played without a subscription — the app derives the public CloudFront playlist from the VOD's storyboard metadata, the same technique as the [TwitchNoSub](https://github.com/besuper/TwitchNoSub) browser extension (reimplemented in C#, no code copied). If ad behavior or access depends on Streamlink configuration, subscriptions, cookies, OAuth, or platform account state, configure those through Streamlink's supported config mechanisms and the app will pass control to Streamlink.

## Chat Sending

The full installer opens the connection wizard on first launch. You can also change the connections later under Settings > Accounts.

- Twitch typing and home: set `Twitch Client ID`, configure the Twitch developer app redirect URL as exactly `http://localhost:39178`, then click `Connect Twitch` in the wizard or Settings. The app opens Twitch OAuth, requests `chat:read chat:edit user:read:follows channel:manage:predictions`, validates the returned user access token, saves it as `Twitch OAuth token`, and uses the token login for IRC, followed streams, VOD browsing, and prediction actions. A Twitch Client ID by itself cannot send chat or load Twitch home data.
- Twitch manual token: you can still paste `Twitch OAuth token` directly. It must be an active user access token with `chat:read`, `chat:edit`, `user:read:follows`, and `channel:manage:predictions` for all authenticated features; Twitch VOD browsing also requires a valid Twitch OAuth token and matching Client ID. The `oauth:` or `Bearer` prefix is optional.
- Kick reading: no token is required. The app resolves the Kick chatroom ID from public channel metadata and connects to Kick's public Pusher-style chat feed.
- Kick typing: set `Kick Client ID` and `Kick Client Secret`, configure the Kick developer app redirect URL as exactly `http://localhost:39177`, then click `Connect Kick`. The app opens Kick OAuth, requests `user:read channel:read chat:write`, saves the returned user access/refresh tokens, and refreshes the access token when it expires. If Kick omits `channel:read` from the user token, the app uses a short-lived app token from your Client ID/Secret to resolve the channel broadcaster ID needed for user-mode chat sends.
- Kick manual token: you can still paste `Kick user access token` directly. It must be an active user access token with `chat:write`; without a refresh token it will stop working when Kick expires it. Kick Client ID/Secret alone do not enable typing in chat.
- Kick VOD chat: set `Kick Client ID` and `Kick Client Secret`, enable `Listen for official Kick chat webhooks`, and configure your Kick developer app webhook URL to a public tunnel that forwards to `http://127.0.0.1:39180/kick-webhook` (or your configured port). When a Kick stream or VOD tab starts, the app uses Kick's official event subscription API to create or verify the `chat.message.sent` webhook subscription for that broadcaster, verifies Kick's webhook signature, and caches messages for later VOD replay. This cannot backfill messages that were sent before the webhook was configured and received.
- Chat layout defaults to `Overlay`, which uses the native VLC overlay plugin and controller for the full chatbox. Use `Docked` in Settings if you want the old side panel.
- The release executable embeds the native VLC overlay plugin and controller and extracts them to local app data when needed. Leave `VLC overlay plugin directory` blank unless you want to override the bundled overlay with another valid `vlc-overlay` build.
- Tokens are saved in `%APPDATA%\StreamlinkVlcStudio\settings.json`; treat that file as account-sensitive.

## Replay Seekbar

The seekbar depends on platform VOD/replay availability. It does not record a local DVR buffer.

- Twitch replay lookup uses the saved Twitch OAuth token and Client ID to match the current live stream to a public `archive` VOD by stream ID or start time. If Twitch does not expose a public archive for the current stream, the seekbar stays disabled with the reason in its tooltip/status text.
- Seeking behind live resolves the matched VOD through `streamlink --stream-url`, plays the raw VOD HLS URL in libVLC, and uses libVLC time seeking. Dragging to the live edge or clicking `Live` restarts normal live playback.
- Twitch VODs opened from Home use the selected video URL directly, initialize the seekbar from Twitch metadata, and load replay chat by VOD ID when Twitch replay chat is available. Kick VODs opened from Home play the returned HLS source directly and load chat from the verified official Kick webhook cache when matching messages were captured while the official webhook listener was configured. Live viewer polling, live chat sending, the `Live` return action, and Recent-stream recording are disabled for explicit VOD tabs.
- Chat sending is disabled while behind live. Twitch replay chat first uses any TwitchDownloader JSON file for the VOD in `%APPDATA%\StreamlinkVlcStudio\replay-chat\twitch` as `<vodId>.json`, `<vodId>_chat.json`, `v<vodId>.json`, or `v<vodId>_chat.json`; when no cache file exists, the app fetches replay chat directly through Twitch's VOD comments GraphQL path and prefetches more chat as replay playback advances.
- Current-live Twitch DVR replays use captured-only chat until Twitch publishes the normal VOD/comments ID. Chat before this tab connected is unavailable, and captured messages appear when replay playback reaches their timestamps. Kick live seekback chat is timestamp-aligned too; after a message appears at the replay time, it remains in chat like live chat until the normal 100-message limit is reached or you seek again.
- Kick public APIs expose live/channel metadata but not stable replay-chat or replay lookup. The `Try private Kick replay lookup` setting enables best-effort website probing and Streamlink validation; failures leave live playback unchanged and explain why the seekbar is disabled.

## Home Page Stream Search

- Enter a Twitch/Kick channel URL or a channel name in the home search bar.
- Platform URLs stay scoped to that platform and channel.
- Bare searches of three or more characters discover Twitch and Kick channel matches, then rank exact, prefix, and contains matches. Twitch discovery uses the configured Twitch OAuth token and Client ID; Kick discovery uses Kick website search with exact-channel fallback.
- Results show `Live`, `Offline`, or `Unavailable`. Live rows are clickable for playback through the same Streamlink/libVLC path as browser-captured streams. Offline rows open the in-app VOD browser for that platform and streamer. Unavailable rows remain visible with the probe or configuration reason, but are not playable.
- Short bare searches keep exact Twitch/Kick candidate probing only.

## Home Page Followed Channels

- Twitch: the home page uses Twitch Helix `streams/followed`, which requires a Twitch user token with `user:read:follows`. Reconnect Twitch after upgrading if your saved token was created before this feature.
- Kick: Kick's public API exposes channel and livestream data, but not a user followed-channel list. Add your Kick followed channel slugs in Settings, one per line. The app checks those configured channels through Kick's public channel API and shows the ones that are live.
- Live followed channels load at startup and refresh every minute while the app is open, even when another page or stream tab is selected. Every applied refresh re-requests the live-card thumbnails instead of reusing the app's previous decoded images.
- Windows toast notifications are enabled by default under **Settings > General > Followed channels**; clear **Windows toast notifications** there to turn them off. The first refresh establishes which channels are already live; after that, an offline-to-live change shows a toast. Keep the app running (it can be minimized to the tray) to receive alerts.
- Click a live card to open that channel through the same Streamlink/libVLC playback path as browser-captured streams.

## Home Page VODs

- The VODs segment can search Twitch or Kick for the streamer shown in the search box.
- Twitch VOD browsing searches Helix by streamer login, resolves the broadcaster through `users`, and lists public Twitch videos through `videos`. Filters are `Past broadcasts`, `Highlights`, `Uploads`, and `All`; `Load More` appends older results using Twitch's pagination cursor.
- Kick VOD browsing reads Kick's website videos endpoint for the channel slug. It is best-effort and can fail if Kick blocks or changes the website response. Kick pagination is not exposed in the same way as Twitch, so only the returned page is listed.
- VOD cards show thumbnails, title, streamer, publish date, duration, view count, and video type/source where available.
- Opening a VOD creates an explicit VOD tab keyed by video ID/source, so multiple VODs from the same streamer can be open at once. VOD opens are not written to Recent streams.
- Twitch VOD browsing requires the saved Twitch OAuth token and Client ID from Settings. Deleted, private, expired, restricted, or otherwise unavailable videos are not returned by Twitch.

## Home Page Recent Streams

- The Recent page records a stream after playback starts successfully, so failed or offline opens are not written to history.
- Recent streams are stored in `%APPDATA%\StreamlinkVlcStudio\settings.json`, de-duplicated by platform and channel, and sorted by latest watched time.
- Recent rows store and show real platform thumbnails when available from a followed-stream card or current Twitch/Kick stream metadata; while the Recent page is open, thumbnails and live/offline indicators refresh every five minutes. If platform metadata is unavailable, the row keeps its last thumbnail and shows an unknown live status instead of guessing.
- Use the delete button on a recent row to remove that channel from the saved Recent history.
- Click a recent stream row to reopen that channel through the same Streamlink/libVLC playback path as browser-captured streams.

## Kick Chatroom And Broadcaster IDs

Kick chat discovery can fail if Kick blocks or changes the public channel metadata endpoint. Add manual IDs in:

```text
%APPDATA%\StreamlinkVlcStudio\settings.json
```

Example:

```json
{
  "Chat": {
    "KickChatroomIds": {
      "channelname": "123456"
    },
    "KickBroadcasterUserIds": {
      "channelname": "789012"
    }
  }
}
```

## Verification Checklist

- Twitch playback:
  - Load the browser extension.
  - Click a live Twitch stream from the Twitch home page.
  - Confirm the browser stays on the home page.
  - Confirm Streamlink resolves a local HTTP URL.
  - Confirm libVLC renders video in the app window.
  - Switch quality and reload.
  - Open a Twitch stream page while logged in and confirm any visible `Claim Bonus` channel-point button is claimed automatically.

- Kick playback:
  - Load the browser extension.
  - Click a live Kick stream from the Kick home page.
  - Confirm the browser stays on the home page.
  - Confirm Streamlink resolves and plays.
  - Test low-latency on/off if buffering occurs.

- Multiple tabs:
  - Click two or more live streams.
  - Enable the multi-stream grid and confirm up to 16 tabs render together in the app.
  - Click a visible tile and confirm it becomes the selected/audible stream.
  - Rename tabs.
  - Move tabs left/right.
  - Close a tab and confirm its Streamlink process exits.
  - Confirm only the selected tab has audio while inactive running tabs are muted.
  - Disable `KeepInactiveTabsRunning` and confirm tab switching pauses/resumes.

- Chat:
  - Twitch chat shows new messages without login.
  - Twitch chat sends messages when an OAuth token with chat access is configured.
  - Kick chat shows messages or displays a non-fatal chat error.
  - Kick chat sends messages when an OAuth token with chat write access is configured.
  - Overlay mode shows chat directly on top of the VLC video surface through the `myoverlay` VLC plugin.
  - Click the overlay input area and type/send a chat message without opening a separate chat window.
  - Hide/show chat.
  - Adjust chat opacity, font size, and dock width.

- VLC/libVLC:
  - Playback stays embedded in the app.
  - Pause/resume, mute, volume, stop, and fullscreen work.
  - Missing or wrong VLC path shows a clear error.

- Streamlink cleanup:
  - Start playback.
  - Close the tab.
  - Confirm the Streamlink process tree is gone.
  - Reload repeatedly and check no stale Streamlink processes accumulate.

- Error handling:
  - Try an offline channel.
  - Try an unsupported URL.
  - Temporarily set an invalid Streamlink path.
  - Temporarily set an invalid VLC directory.
  - Confirm errors appear in the tab and logs.
