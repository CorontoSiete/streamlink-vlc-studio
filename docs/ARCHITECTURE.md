# Architecture Plan

## Stack Choice

The current implementation is Windows-first .NET 10 WPF with a clean core/infrastructure split.

Tradeoffs considered:

- Electron: strong UI ecosystem and easy subprocess control, but native libVLC embedding inside Chromium windows is awkward and often devolves into external-window or native-addon work.
- Tauri: lighter than Electron and strong process management through Rust, but libVLC window ownership inside a webview still adds platform-specific complexity.
- Qt/QML: excellent fit for libVLC embedding and cross-platform native UI, but C++/QML packaging raises the implementation and maintenance cost for a Windows-first personal desktop app.
- Avalonia: good long-term cross-platform .NET UI candidate, but it adds external package/runtime dependencies and libVLC surface integration is still more moving parts than WPF on Windows.
- WPF: best Windows embedding path because it exposes stable HWND hosting, works with the installed .NET SDK, and lets the app stay dependency-light. Cross-platform portability is preserved by keeping stream, chat, playback, settings, and logging behind interfaces.

## Layering

- `StreamlinkVlcStudio.Core`
  - Platform/stream models.
  - Settings model.
  - Service contracts.
  - Followed live stream models.
  - Stream search and VOD browsing contracts.
  - Stream input parsing.
  - Advanced Streamlink argument tokenization.
  - Twitch IRC and Kick Pusher payload parsing.

- `StreamlinkVlcStudio.Infrastructure`
  - File logger.
  - JSON settings persistence.
  - Streamlink executable discovery.
  - Safe Streamlink process spawning with `ProcessStartInfo.ArgumentList`.
  - Streamlink external HTTP transport lifecycle.
  - Direct libVLC P/Invoke playback engine.
  - Shared libVLC runtime leases plus atomic audio-request state; `LibVlcPlaybackEngine` remains the playback façade.
  - `vlc-overlay` plugin preparation for chat-on-video mode.
  - Followed live streams adapter for Twitch Helix and configured Kick channel slugs.
  - Twitch/Kick channel search adapter and Twitch/Kick VOD adapters.
  - Twitch subscriber-only VOD/replay fallback resolver using public storyboard-derived CloudFront playlists.
  - Twitch IRC adapter with anonymous read-only mode and OAuth send mode.
  - Isolated Kick chat adapter with public Pusher reading and OAuth API sending.
  - Official Kick webhook listener, signature verifier, replay-chat cache, and event subscription manager.
  - One Twitch GraphQL transport and one Kick website JSON/fallback reader shared by replay, chat, VOD, and browsing callers.
  - `ReplayResolver`, `ReplayChatProvider`, and `BrowseService` remain compatibility façades; browse payload mapping, Twitch rate-limit coordination, replay URL validation, and Kick webhook authentication/replay protection are separate components.

- `StreamlinkVlcStudio.App.Wpf`
  - WPF shell.
  - Embedded HWND video surface.
  - Browser extension capture of canonical, direct Twitch/Kick channel routes through a loopback HTTP listener; VOD and other non-channel routes are rejected. The native low-level mouse hook/UI Automation browser-click fallback is only enabled if that listener cannot start.
  - Per-tab view models.
  - Single-stream and paged multi-stream video layout for up to 16 streams per page.
  - Playback controls.
  - Settings drawer.
  - Home page for stream search, live followed channels, platform VOD browsing, and recently watched streams.
  - Docked chat rendering.
  - Internal lifecycle controllers keep search cancellation/debounce, VOD and Browse pagination
    generations, recent-stream transient state, tab grouping, inactive-tab playback policy,
    background operation draining, tab-start throttling, deferred playback cleanup, and
    tab lifecycle boundaries out of the UI-facing state adapters. The view models still
    own WPF collections and public commands, while the controllers provide reusable, shutdown-safe
    operation boundaries.
  - `MainViewModelDependencies` and `StreamTabViewModelDependencies` are the required composition
    records used by application startup. Tests use centralized `TestViewModels` builders so service
    wiring is defined once instead of relying on positional constructor adapters.
  - Playback teardown is isolated in `PlaybackResourceCoordinator`; chat event subscription and
    Twitch prediction wiring are isolated in `ChatClientEventCoordinator`; native overlay capability
    checks use the cancellable `NativeOverlayCapabilityProbe`. These coordinators own operation
    lifetimes, not WPF collections or serialized settings.
  - Native PiP hit testing goes through `IWindowHitTester` and `WindowHitTestPolicy`, allowing z-order
    decisions to be tested without calling `user32` and keeping detached-window teardown predictable.
  - Home middle-button scroll calculations are owned by `HomeAutoScrollController`; the code-behind keeps compatibility forwarding methods for the existing event and test surface.
  - Native overlay transport uses one complete-message codec with a 32 MiB limit, bounded control-reserved write budgeting, generation/sequence-checked resize persistence, and identity-keyed capability probes.

- `StreamlinkVlcStudio.Tests`
  - Dependency-free console test runner for pure logic and config round trips.

## Playback Flow

1. User clicks a Twitch/Kick channel URL in a supported desktop browser.
2. The content script prevents browser navigation and posts the channel URL to the desktop app's loopback capture listener.
3. The WPF shell passes the URL to `StreamInputParser`.
4. `StreamlinkService` starts Streamlink with `--player-external-http` bound to `127.0.0.1`.
5. The service reads Streamlink stdout/stderr, captures the generated local HTTP URL, and returns a managed session.
6. `LibVlcPlaybackEngine` initializes libVLC from the configured VLC directory.
7. libVLC is pointed at the WPF `VideoSurface` HWND and plays Streamlink's local HTTP transport URL.
8. Closing/reloading a tab disposes libVLC and kills the Streamlink process tree.

This keeps Streamlink responsible for platform stream resolution and HLS transport behavior while libVLC owns rendering and audio/video controls.

## Home Followed Streams Flow

- Twitch:
  - Reuses the Twitch OAuth token and Client ID from chat settings.
  - Requests `chat:read chat:edit user:read:follows channel:manage:predictions clips:edit` during Twitch authorization.
  - Validates the token and calls Twitch Helix `streams/followed` with pagination to load all live followed channels for the authorized user.

- Kick:
  - Uses Kick's public channel API with either a Kick user token or app token.
  - Kick's public API does not expose the authorized user's followed-channel list, so the app stores explicit Kick followed channel slugs under `FollowedChannels.KickChannelSlugs`.
  - Queries configured slugs in batches of 50 and shows only entries whose channel response includes a live stream.

Followed streams load once at startup and refresh every minute while the app is open, regardless of the selected Home page segment, active stream tab, or minimized/tray state.

Clicking a home card opens the same `StreamTarget` flow used by browser capture and manual stream input.

## Home Stream Search Flow

- Exact Twitch/Kick URLs are parsed into a single platform candidate.
- Bare queries of three or more characters call `IStreamSearchService`, which searches Twitch channels through Helix when Twitch OAuth and Client ID are configured, and searches Kick channels through Kick website search with a curl fallback when normal HTTP is blocked.
- Short bare queries keep exact candidate probing only.
- Results are deduplicated by platform/channel and ranked by exact match, prefix match, contains match, live state, then source order.
- Live or unknown candidates are probed through Streamlink before playback is enabled. Offline candidates remain visible without Streamlink probing.
- The WPF home search dropdown renders `Live`, `Offline`, and `Unavailable` rows. Live rows open normal playback. Offline rows switch to the VOD page, select the matching platform, set the streamer/channel search text, and run the VOD search. Unavailable rows show the probe/configuration reason but do not execute playback.

## Home Recent Streams Flow

- A stream is added to recent history only after its tab reaches `PlaybackStatus.Playing`.
- The history is stored in `AppSettings.RecentStreams`, de-duplicated by platform and channel, and ordered by `LastWatchedAtUtc` descending.
- Thumbnail URLs are persisted with recent entries when they come from a live followed-stream card or from platform metadata fetched after successful playback.
- Opening the Recent page starts a five-minute metadata refresh timer; each tick refreshes visible Recent row thumbnails and live/offline indicators through platform metadata, preserves existing thumbnails when metadata is unavailable, and shows an unknown live status rather than inferring one.
- Deleting a recent row removes that platform/channel from `AppSettings.RecentStreams`, clears its transient live-status cache entry, rebuilds the Recent view models, and saves settings.
- Clicking a recent row opens the stored `StreamTarget` through the same candidate/open path used by followed-stream cards and browser capture.

## Home VOD Flow

- The VOD page keeps a selected platform state and reuses the streamer/channel text box for Twitch login or Kick slug searches.
- Twitch VOD browsing uses Helix `users` and `videos`, supports type filters and cursor-based load more, and requires Twitch OAuth plus a matching Client ID.
- Kick VOD browsing reads `kick.com/api/v2/channels/{slug}/videos` with browser-style headers and the same curl fallback pattern used for Kick website reads. It is best-effort because the endpoint is part of Kick's website surface.
- VOD rows are represented by a platform-aware view model that builds explicit `StreamTargetKind.TwitchVod` or `StreamTargetKind.KickVod` targets.
- Twitch VOD tabs resolve the selected Twitch URL through Streamlink `--stream-url` before libVLC playback, then load replay chat by VOD ID when available.
- Live tabs use the same storyboard-derived CloudFront fallback when seeking into a subscriber-only matching Twitch VOD and Streamlink cannot resolve it.
- Kick VOD tabs play the returned HLS source directly in libVLC without Streamlink URL resolution. When the VOD item includes a start time, replay chat is loaded from the verified official Kick `chat.message.sent` webhook cache under `%APPDATA%\StreamlinkVlcStudio\replay-chat\kick-official`.
- Explicit VOD tabs disable live viewer polling, live chat sending, return-to-live behavior, and Recent-stream writes.

## Clip Flow

- The top `Clip` button is bound to `MainViewModel.CreateClipCommand`, so it acts only on the selected tab.
- The command is enabled only for a live Twitch target. Twitch VOD and Kick tabs remain disabled.
- `TwitchClipService` validates the Twitch user token, requires `clips:edit`, resolves the selected channel to a broadcaster ID through Helix `users`, and starts a 30-second clip through Helix `clips`.
- Twitch clip creation is asynchronous. The service polls Helix `clips?id=...` for up to 60 seconds, then opens the returned public clip URL with the system browser.
- Kick has no official clip-creation path in this application, so no private Kick website endpoint is called.

## Chat Flow

Live Twitch and Kick connections are supervised after the initial connection. EOF, provider reconnect
requests, remote close, and transient failures retry with jittered 1/2/4/8/16/30-second backoff.
Sixty stable seconds reset the backoff; explicit disconnect/disposal cancels and drains it.

- Twitch:
  - Anonymous read-only IRC over TLS when no token is configured.
  - Runs Twitch OAuth implicit flow on `http://localhost:39178` to acquire a user access token with `chat:read chat:edit user:read:follows channel:manage:predictions clips:edit` from the configured Twitch Client ID.
  - Authenticated IRC over TLS when a Twitch OAuth token is configured.
  - Requests Twitch tags/commands capability.
  - Validates configured tokens and falls back to read-only chat when they are unusable.
  - Parses `PRIVMSG` tags for display name, color, and badges.
  - Sends chat with `PRIVMSG` through the authenticated IRC connection.

- Kick:
  - Isolated adapter.
  - Attempts public channel metadata lookup for chatroom ID and broadcaster user ID, with a `curl.exe` fallback for Kick's anti-bot blocks against .NET/PowerShell HTTP clients.
  - Supports manual chatroom and broadcaster user ID overrides in settings.
  - Connects to Kick's public Pusher-style chat channel.
  - Runs Kick OAuth authorization-code + PKCE on `http://localhost:39177` to acquire a user token with `chat:write`.
  - Stores Kick refresh tokens and refreshes expiring access tokens before docked or native overlay chat sending.
  - Resolves broadcaster user IDs with Kick's public channel API, falling back to a client-credentials app token when the user token lacks `channel:read`.
  - Validates the configured user access token before docked chat sending.
  - Sends docked chat through Kick's public chat API using the configured user access token.
  - Native VLC overlay reading works anonymously. Native VLC overlay typing uses the current Kick user access token when one is configured.
  - Official Kick VOD chat is cache-backed: a local listener accepts only signed `chat.message.sent` webhooks, stores them by channel/day, and replay tabs align cached messages by VOD start time. When the listener is enabled, Kick tabs also create or verify the official event subscription for the broadcaster through `/public/v1/events/subscriptions` using the configured Kick app credentials. Kick's current official REST/OpenAPI surface has no historical VOD chat/messages endpoint.
  - Failure is non-fatal and shown as a system chat message.

Kick's public chat surface changes more often than Twitch IRC. That is why the adapter is small, replaceable, and not allowed to block playback.

## Settings

Settings live at:

```text
%APPDATA%\StreamlinkVlcStudio\settings.json
```

Non-secret settings remain readable JSON. `Chat.TwitchOAuthToken`,
`Chat.KickOAuthToken`, `Chat.KickRefreshToken`, and `Chat.KickClientSecret` are
removed from the serialized `Chat` object and stored in the top-level
`ProtectedSecrets` envelope. That envelope uses Windows DPAPI with current-user
scope and application-specific entropy, so it is not portable to another Windows
user profile. Loading a legacy file migrates plaintext values into the envelope.
If an existing envelope cannot be decrypted, the service preserves a timestamped
backup, clears the account secrets, and saves the remaining settings so the user
can reconnect the accounts.

Important settings:

- `StreamlinkPath`
- `VlcDirectory`
- `DefaultPlatform`
- `DefaultQuality`
- `StreamVolumes`
- `StreamVlcOverlayFontSizes`
- `LowLatency`
- `KeepInactiveTabsRunning`
- `MultiStreamEnabled`
- `RecentStreams`
- `CustomStreamlinkArguments`
- `Chat.KickChatroomIds`
- `Chat.KickBroadcasterUserIds`
- `Chat.TwitchUsername`
- `Chat.TwitchClientId`
- `Chat.TwitchOAuthToken`
- `Chat.TwitchTokenExpiresAtUtc`
- `Chat.KickUsername`
- `Chat.KickOAuthToken`
- `Chat.KickRefreshToken`
- `Chat.KickTokenExpiresAtUtc`
- `Chat.KickClientId`
- `Chat.KickClientSecret`
- `Chat.KickSendAsBot`
- `Chat.Layout`
- `FollowedChannels.KickChannelSlugs`

- `Chat.VlcOverlayDirectory`
- `Chat.VlcOverlayFontSize`

When `Chat.Layout` is `Overlay`, the app resolves a valid `vlc-overlay` directory from `Chat.VlcOverlayDirectory`, the bundled `vlc-overlay` folder beside the executable, or the embedded overlay bundle extracted from the single executable. A valid overlay directory must contain `build\libmyoverlay_plugin.dll` and `build\vlc_chat_overlay.exe`. The app prepares the plugin in a writable local plugin cache, starts libVLC with `--sub-source=myoverlay`, and launches the native controller with a per-tab pipe name. The overlay is therefore composited by VLC itself instead of WPF, avoiding HWND airspace problems and allowing direct in-overlay chat input. The VLC plugin position state path is stable per platform/channel, so dragged chat position and size are restored for that stream.

Kick channel/chatroom dictionaries retain their public JSON shape, but all reads, copy-on-write updates,
and persistence pass through one identity store so concurrent callbacks and saves never enumerate a
mutating dictionary.

## Error Handling

- Missing Streamlink/VLC paths produce visible UI errors.
- Streamlink startup timeout includes recent Streamlink output.
- Offline/restricted streams surface as tab errors.
- Chat failures are chat/system messages and logger entries.
- Streamlink process cleanup uses `Kill(entireProcessTree: true)`.
- All short-lived redirected processes use `BoundedProcessRunner`: stdout and stderr are drained,
  timeout is returned as data, caller cancellation is rethrown, and child trees are terminated.
- Infrastructure and UI HTTP integrations obtain clients from `HttpClientFactory` with an explicit
  positive timeout. Injected clients remain caller-owned; clients created by chat services are
  disposed by the service that created them.

## Build and generated output policy

- `bin/`, `obj/`, `artifacts/`, `.tools/`, `.wix/`, `.audit-*`, and `.codex-*` are generated or
  machine-local output and are ignored. Verification builds should use output roots outside the
  repository when the source tree must remain clean.
- `src/StreamlinkVlcStudio.Infrastructure/Vlc/BundledOverlay/build` is intentional runtime input:
  its `libmyoverlay_plugin.dll` and `vlc_chat_overlay.exe` are embedded and staged into releases.
  Cleanup must never remove those two files.
- The dependency-free test executable remains the test entry point. Subsystem-specific additions
  are registered from separate test files and can be filtered with `SVS_TEST_FILTER`; a timeout or
  failure returns a non-zero exit code.
- `.dependency-audit/` and `.nuget/` are ignored generated caches. The repository must not be initialized
  as a Git checkout merely to run validation.
- `shared/release-contract.json` defines the one valid payload root, required browser/native/runtime
  files, canonical output paths, and exact six-asset release set. Package, installer, staging, and CI
  entrypoints consume that contract rather than maintaining independent asset lists.
- Windows dependency manifests use `length` as the canonical byte-count field. Native overlay staging
  verifies a closed manifest set, including hidden files, before copying exactly the verified provenance.

## Future Portability

A future Avalonia or Qt UI can reuse the Core and most Infrastructure services. The WPF-specific surface is limited to:

- `VideoSurface`
- WPF view models/bindings
- window/dialog code
