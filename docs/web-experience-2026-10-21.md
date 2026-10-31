# Browser experience refresh

This is a historical verification record from 2026-10-21. Current sign-in uses a
username and password, and browser preferences are saved per web account. See
[LAN browser player](local-web-server.md) for current behavior.

The LAN browser client uses a Twitch-inspired charcoal and purple visual system. A full-width 64-pixel masthead brings together the brand, Following/Browse navigation, search and stream opening. The 240-pixel desktop sidebar includes followed channels, live status and compact viewer counts. Phone navigation sits at the bottom of the screen, while tablets use an icon rail. All assets and icons are served locally; Twitch and Kick retain their own provider colors. Provider titles and chat still use DOM text nodes.

## Workflow changes

- The connection screen explains where to find the PIN and provides expandable setup instructions and inline connection errors.
- **Open stream** opens a focused dialog for a Twitch/Kick URL or channel. Its errors stay beside the input.
- **Following** can filter Twitch/Kick and live channels, or sort by viewer count and channel name, without another provider request. Live channels stay ahead of compact offline rows. Counts use returned channel data. Offline channels lead to their videos. Sorting stays confined to Following so watch history keeps its original order.
- The live spotlight pairs a large, playable thumbnail with a separate channel information panel, then stacks them on phones. Cards use sharper corners, red LIVE badges, explicit viewer labels and a purple lift effect on hover. Platform filters use underlined tabs.
- The followed-channel rail uses the existing Following response, remains available while browsing other pages, and clears on disconnect. Live channels open their player; offline channels open their videos. Header and sidebar navigation share the current page indicator.
- Each library page explains its purpose. Loading placeholders, page-specific empty states and retry actions replace generic notices. A response from an earlier page cannot overwrite a newer selection.
- Category pagination keeps existing results and avoids duplicate cards. Video cards show watched/resume information. Kick hides the Twitch-only video-type filter.
- Playback keeps common controls visible and groups reload, clip, follow, predictions and tab management under **Stream options**. A single player fills its grid. Chat, fullscreen, quality, playback speed and tab naming retain their existing service behavior.
- Download progress refreshes while the download library is visible. Refresh pauses while a card control has focus so keyboard users keep their place. Host-side deletion still asks for confirmation.
- Preferences distinguish shared desktop defaults from device-only appearance and multi-stream behavior. Range controls show their current values; unsaved, saved and failed-save states are explicit. Controls are disabled during a save.
- Navigation has accessible names, visible focus, a skip link and keyboard support. `/` focuses search and Ctrl/Command+K opens a stream. The dialog also closes when its backdrop is clicked. Motion respects the reduced-motion preference. The phone toolbar includes its own disconnect action, and bottom navigation targets are at least 44 by 44 pixels at tested phone widths.
- Opening a desktop sign-in link in an existing tab consumes and clears its fragment, including when the browser treats the navigation as a fragment change. A reused credential returns to PIN entry. Faded chat controls retain their reserved hit areas so a first press can begin a drag, resize or message submission.

## Validation and review evidence

Run `scripts/dev.ps1 Test -Filter 'local web:' -NoRestore` to build Release and run the LAN, authentication, media, real-playback and browser workflow regressions.

Verified on 2026-10-21: Release solution build succeeded with zero warnings/errors; **22 web tests passed, zero skipped**. JavaScript syntax, 11 volume checks and nine overlay geometry checks passed. Desktop and phone screenshots were visually reviewed, with overflow assertions at 320, 390, 768, 1024 and 1920 pixels and desktop captures at 1440 pixels.

The browser regression drives the actual embedded assets over HTTPS in WebView2. It covers incorrect and correct PIN entry, provider/live filters, channel sorting, search and stream-opening keyboard shortcuts, navigation during a delayed request, category pagination, VOD resume cards, automatic download progress, provider-error recovery, empty search, the open-stream dialog, playback/close, preference persistence and responsive library navigation at five viewport widths from 320 to 1920 pixels. It checks horizontal overflow, phone navigation target sizes and JavaScript/CSP errors. Separate browser regressions exercise real video decoding, remembered per-channel volume, desktop automatic sign-in and trusted pointer/touch/keyboard chat interactions through phone, grid and fullscreen layouts.

Evidence is written to `.artifacts/local-web-design/`: desktop and phone screenshots of the connection screen, following, categories, videos, downloads, preferences and stream opening, plus viewport/font metrics. The existing decoded-video and hostile-metadata regression continues to write `.artifacts/local-web/`.

Screenshots use deterministic test-only channel data and illustrative thumbnail fixtures. They establish interface and local media behavior, not live provider availability or physical TV/Linux compatibility. Production cards continue to use the provider's returned artwork and metadata. No account setup, production profile, network binding, certificate trust or server authentication policy is changed by this refresh.

## Twitch-inspired layout follow-up verification

The follow-up Release solution build succeeds in `.artifacts/twitch-ui/build/`, leaving the running desktop app untouched. NuGet reports NU1900 warnings because its vulnerability feed is unavailable in this session. JavaScript syntax, all 11 volume checks and all nine overlay geometry checks pass.

The HTTPS WebView2 experience regression is blocked at Windows test-certificate key import, before loading the page. A separate headless Chrome fixture runs the actual HTML, CSS and JavaScript with synthetic channel data and intercepted illustrative artwork. It checks all six library pages at 320, 390, 650, 768, 900, 1024, 1180, 1440 and 1920 pixels: 54 combinations with no horizontal overflow or JavaScript errors. Checks also cover phone navigation target sizes, platform/live filters, persistent followed channels, header navigation, offline-channel videos, saved preferences, stream opening and disconnect cleanup. Both the new sidebar links and spotlight preview open and decode the local HLS video fixture.

Desktop and phone renders were visually reviewed. Screenshots, viewport metrics, build output and the separate HTTPS test failure are in `.artifacts/twitch-ui/`. The disposable fixture uses a loopback HTTP server and test-only browser process flags; it does not verify production TLS, CSP, authentication, live provider availability, or physical-device compatibility. Application certificate handling and server security remain unchanged.
