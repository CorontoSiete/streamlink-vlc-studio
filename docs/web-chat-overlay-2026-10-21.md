# Web server chat overlay

In browser **Preferences > Chat appearance**, select **Over the video**. Drag
the **Move chat** button to reposition the overlay, or drag its lower-left corner
to change its width and height. Mouse and touch use pointer capture. Either
focused handle accepts arrow keys (Shift for larger steps); Home resets that
handle's position or size. Escape cancels a drag, as do touch cancellation and
unexpected pointer capture loss. Resizing keeps the top-right corner fixed
after moving. Size and position are saved per platform and channel in the signed-in
web account's preferences. They survive app restarts and updates,
browser storage clearing, and server address changes. Position is relative
to the available space, so each player's overlay stays inside its video area
when changing viewport, fullscreen or grid layouts. The default size leaves
media controls clear; moving or resizing can reach the video's bottom edge.

See [per-stream layout persistence](web-chat-layout-persistence-2026-10-22.md)
for the storage and regression details.

The overlay uses a compact, centered **Move chat** pill with a four-way arrow.
The lower-left **Resize chat** button uses a diagonal arrow
and displays the current width × height while dragging. Both controls highlight
on hover, keyboard focus, and active adjustment. Touch devices get 44 px targets;
compact overlays reduce spacing to keep large chat text readable and the message
input clear of the resize button. Keyboard instructions are available in the
controls' tooltips and accessible descriptions.

At every background opacity, the move button, resize button, and message form
stay invisible until the pointer hovers over the chat box. They retain their
space so messages do not jump. Keyboard focus and active dragging also reveal
the controls; devices without hover keep them visible for touch access. The
desktop move button is 36 × 24 px, reducing to 32 × 20 px in short overlays.

Control redesign validation: the Release solution build passed with zero warnings
or errors, and all 16 local web tests passed with zero skips. The browser checks
also verify the live dimensions during resizing and 44 px touch targets at 320 px
with large chat text. See `.artifacts/local-web-controls-tests.log` and
`.artifacts/local-web-design/overlay-resizing.png`, `overlay-touch.png`, and
`overlay-grid.png` for the results and screenshots.

**Overlay background opacity** accepts 0–100%. At 0%, messages and images stay
opaque while the panel border, input, and move/resize handles disappear. Hovering or
focusing chat reveals the controls. Message shadows improve readability over
video; connection errors remain visible.

The server now resolves badges and emotes through the existing desktop catalogs.
This includes Twitch global/channel badges, Kick bundled/channel subscriber
badges, tagged Twitch/Kick emotes, Twitch BTTV/FFZ/7TV catalogs, and Kick global
third-party emotes and channel 7TV sets. Kick channel lookup uses the broadcaster
user ID from channel metadata, independently of its chatroom ID. Catalog loads
remain asynchronous and retry through the existing bounded coordinator.

Twitch image URLs use the animated-capable default format. Browser emote ranges
now match the parsers' exclusive UTF-16 end positions, preserving following
punctuation and accepting emotes at the end of a message, including after emoji.
Missing images retain readable text. Catalog changes update existing rows and
buffered replay messages without adding duplicate rows or resetting playback.
Each player retains at most 2,000 source messages for these updates.

Bundled badge PNGs are served through an authenticated `/chat-assets/` route.
The browser receives opaque asset IDs; arbitrary file paths and credentials
are not accepted by that route. External images remain HTTPS and do not receive
the server's account credentials.

Validation uses actual HTTPS/WebView2 playback with deterministic provider
payloads: mouse, touch and keyboard movement/resize, clamping at every edge,
Escape/touch cancellation and capture loss, per-player position isolation,
device preference restoration,
320/390/768 px layouts, fullscreen/grid, opaque text at zero background opacity,
delayed badge/emote loading, failed images, emoji offsets and rendered GIF frame
changes. Workspace tests cover live/replay enrichment, isolation, refresh
versions and bounded message retention. Screenshots are written under
`.artifacts/local-web-design/overlay-*.png`. These fixtures do not establish
live account-specific provider availability or physical TV compatibility.

The movement regression also checks moving at zero background opacity,
resizing after moving, preserved messages/drafts, saved position after a page
reload, and independent positions after grid selection and viewport changes.

Movement verification: Release solution build and self-contained Windows x64
publish succeeded with warnings treated as errors. All 16 local web tests
passed with zero skips. Logs are `.artifacts/local-web-movement-tests.log` and
`.artifacts/local-web-movement-publish.log`. The runnable build is
`.artifacts/web-chat-movable/app/StreamStudio.exe`; movement evidence includes
`.artifacts/local-web-design/overlay-moved.png`.

Bottom-edge correction: movement and resizing previously shared a video height
minus 60 px limit, which left an unavoidable 52 px gap below chat. Their maximum
height now reserves only the 8 px top inset, allowing a zero bottom gap. The CSS
maximum matches that bound. The initial size still leaves room for media controls
and uses the existing 140 px minimum where space permits. Normalized positions
continue to survive viewport changes and device preference restoration.

Run `node scripts/tests/web-overlay-geometry.tests.cjs` for nine regressions that
execute the actual application geometry and pointer/keyboard handlers with small
DOM substitutes. They reproduce the original 52 px gap and verify zero-gap
movement, CSS/resize agreement, cancellation, edge clamping, player isolation and
saved positions. These checks do not establish browser rendering or trusted input.
The existing WebView2 regression now additionally requires bottom alignment for
default-size chat, mouse/touch/keyboard movement, resizing, phone sizes,
player/grid fullscreen and reload.

Bottom-edge validation in the restricted 2026-10-21 session: the nine code-level
regressions and JavaScript syntax check passed. The Release solution build passed
with zero warnings/errors, as did the targeted C# whitespace check. Self-contained
Windows x64 publication succeeded, and both published embedded web assets exactly
match the corrected source. The broader local-web run finished with 9 passes and
7 failures: six temporary TLS-key import failures and one chat-image catalog
timeout. The HTTPS browser regression could not start because
Windows denied its temporary user-key import; isolated WebView2 initialization
also timed out and headless Chrome could not run its renderer in this session.
No browser pass or visual verification is claimed for this correction. Logs:
`.artifacts/local-web-bottom-geometry.log`, `local-web-bottom-build.log` and
`local-web-bottom-red.log`; the broader results and publication are in
`.artifacts/local-web-bottom-tests.log` and `local-web-bottom-publish.log`.
The corrected runnable build is
`.artifacts/web-chat-bottom/app/StreamStudio.exe`.

Provider references: [Twitch emote CDN and formats](https://dev.twitch.tv/docs/api/reference/#get-channel-emotes),
[Kick chat payloads](https://github.com/KickEngineering/KickDevDocs/blob/main/events/event-types.md),
and [7TV platform user lookup](https://github.com/SevenTV/SevenTV/blob/main/apps/api/src/http/v3/rest/users.rs).
