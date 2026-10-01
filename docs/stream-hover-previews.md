# Live stream hover previews

Enable **Settings > General > Appearance & behavior > Live stream previews on hover**.
The switch applies immediately and saves automatically across restarts. Existing
settings default to off. Followed and Browse live cards, live search results, and
Recent rows confirmed live play muted video inside their thumbnail while hovered.
VODs, offline channels, and unknown Recent statuses do not start previews.

A 100 ms delay avoids opening streams while passing across cards. One shared
controller serializes previews, including transport cleanup. For ordinary Twitch
and Kick broadcasts, the preview authorizes playback directly and fetches only
the selected quality: 360p, including its 30/60 fps names, then 480p and its 30/60
fps names, then best. This avoids launching Python on each hover
and Twitch's checks of every quality. A local playlist endpoint hands the initial
validated playlist to VLC and validates subsequent reloads; video segments travel
directly from the provider to VLC. No neighboring cards are opened or prefetched.

Twitch previews use Streamlink's anonymous playback authorization identity.
Generating a fresh web device ID on each hover caused `tttcheekyttt` and
`termynater_` to return pre-roll segments and restart through Streamlink.
[The authorization diagnosis and repeated measurements](stream-hover-preview-authorization-2026-10-01.md)
record the fix and the faster visible startup on both channels.

The direct path accepts MPEG-TS and fragmented MP4 with a validated, whole MP4
initialization file. Both initialization and segment URLs retain the provider
URL checks. Ranged maps, encrypted media, and a change of initialization file or
container during playback retain Streamlink fallback. This handles streams such
as `summit1g`, whose MP4 format and `360p30` name previously caused a slow fallback
at 1080p. [The diagnosis and repeated measurements](stream-hover-preview-startup-2026-10-01.md)
record that fix.

Custom Streamlink arguments, authentication/config files, plugins, proxy settings,
and unsupported playlists retain the existing Streamlink HTTP transport. Failed
direct lookup or playback also falls back to that transport with the original
request. Twitch ad markers trigger fallback before that playlist reaches VLC,
including when an ad appears after playback has begun. Direct lookup has a
two-second budget and direct playback has a four-second first-frame deadline;
the deadline is removed once video arrives. Cancellation closes the playlist
listener and stops decoding before another transport starts.

An isolated VLC player disables audio and chat and presents 320 x 180 pixels at
up to 30 frames per second.
The network cache remains 500 ms. Native HLS uses
VLC's low-latency mode for Twitch when enabled; Kick retains conservative buffering.
Its requested live distance derives from the existing transport's segment policy
and the playlist's target duration, subject to VLC's minimum buffer.
A single pending-frame slot and a single queued UI update prevent a busy UI from
accumulating work. Frame/state notifications request WPF
rendering directly, so displaying decoded video does not wait for a polling timer.

The WPF image preserves card input, scrolling, clipping, and badges. Leaving,
clicking, disabling previews, hiding/unloading the card, deactivating/minimizing
the window, or shutting down cancels playback. Replacement waits for the previous
player and transport to finish cleanup. Failed startup, unavailable media, and
stalled video restore the still thumbnail with a short unavailable label.

VLC callbacks follow the [VLC 3 media-player API](https://videolan.videolan.me/vlc-3.0/group__libvlc__media__player.html):
aligned frame memory, presentation at the playback clock, and native playback
stopped before delegates and memory are released. No preview tab, chat connection,
watch-history entry, or main-player audio change is created.

## Verification

- Release solution build with warnings treated as errors: zero warnings/errors.
- 38 focused checks cover persistence, disabled/offline states, debounce,
  transport options, canceled/late startup, stale frames, rapid replacement,
  failure recovery, missing VLC, bounded frames, shutdown, XAML bindings, and
  physical hover/click/leave/hide/disable/unload behavior.
- The physical hover check keeps input work pending while waiting for video. It
  failed with the original background timer and passes with frame notifications.
  A burst of 1,000 decoded frames queues at most one render and displays the latest
  frame after the UI resumes.
- The installed VLC decoded changing red-to-green fixture video and stopped
  producing frames after cancellation; the transport was disposed.
- Direct-source regressions cover selected-quality requests, ordered fallbacks,
  configuration compatibility, bounded responses, URL validation, playlist reloads,
  initial and later ads, first-frame deadlines, late cancellation, and cleanup.
- Fragmented MP4 checks cover initialization URL rewriting, unsafe/malformed/ranged
  maps, changing initialization and containers, later ads, and actual native VLC
  decoding of moving fixture video across fragment boundaries.
- Before/after live timings and full validation are recorded in
  [the MP4 startup report](stream-hover-preview-startup-2026-10-01.md) and
  [the authorization startup report](stream-hover-preview-authorization-2026-10-01.md).

Run focused checks with the SDK selected by `global.json`:

```powershell
.\scripts\dev.ps1 Test -Filter 'stream hover preview:' -Interactive
```

The two additional live-provider checks opt in via `SVS_TEST_HOVER_CHANNEL`, set
to a currently live Twitch or Kick URL. They read existing transport settings but
do not save changes to them. Use a 75-second `SVS_TEST_TIMEOUT_SECONDS` for live
network tests. Live checks report transport readiness, first-frame timing, frame
gaps, and cleanup time without printing signed media URLs or authentication arguments.
Set `SVS_TEST_HOVER_DURATION_SECONDS=40` for a longer continuity check. Live-card
PNGs are under `.tmp/hover-preview-*`; current latency and regression logs are
under `docs/measurements/hover-preview-startup-2026-10-01/`.
