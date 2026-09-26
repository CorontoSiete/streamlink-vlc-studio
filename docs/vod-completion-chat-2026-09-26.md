# xQc VOD completion and chat source

The reported archive is Twitch video `2881171681`, the September 22 xQc
broadcast, duration `14:03:13.316`. Diagnostics and test logs are in the ignored
`artifacts/vod-end-chat` directory.

## Completion evidence

The application log records seeks to `50593.316` seconds at 02:29:32,
02:29:49, and 02:30:36 on September 26. The long-VOD adapter correctly opens
the last two segments with a `50573.316` second timeline offset.

A regression using the real view model and libVLC 3.0.23 reproduced the
failure against the same CDN playlist. After the seek, VLC reported Playing
at `50594283` ms, then `50603283` ms ten seconds later. Its decoded and displayed
frame counts remained at 231 and 94. The existing completion screen correctly
requires VLC's Ended state, which never arrived. A clean synthetic fourteen-hour
playlist passed the same test.

Downloaded segments `5047-muted.ts` and `5048-muted.ts` each contain invalid
PCR/PTS values of `2^33-1` (`95443.717677...` seconds), among otherwise valid
timestamps. Each segment required eight optional timestamp fields to be removed.
Replaying the same local bytes after the existing sanitizer removed those fields
produced an actual Ended state at the 20-second endpoint. The unmodified local
segments reproduced the failure. Media payload bytes were unchanged.

The repair gateway previously bypassed VLC 3.0.18 and newer. That cutoff was
based on successful playback through a muted intro; it did not cover seeking to
a muted ending. Repair now depends on the playlist's muted segments regardless
of VLC version. The existing validated proxy, bounded streaming repair, and
lease cleanup are reused. Other segments still come directly from the provider.
The long-VOD adapter retains the proxy lease and the full absolute timeline.

No duration threshold, stalled-frame timer, forced Finished status, or skipped
final second was added. The completion screen still follows real decoder EOF.

## Chat evidence

At 02:30:21 the application log shows its native overlay connecting to Twitch
IRC while this archive was open. Playback startup excluded explicit VODs, but
chat restarts/settings/visibility changes had other entry points. The native
controller checked transient replay state, which is false while a VOD starts
or lacks replay metadata. Docked chat restart could create a live client even
after the VOD session was established.

VOD identity now excludes live clients and the native live controller throughout
their startup paths. Incoming live callbacks are also excluded for VOD targets.
Chat restarts retain the archive's timeline and repaint its replay overlay;
the mode label remains REPLAY CHAT while metadata is pending. Refreshing overlay
settings preserves visible replay frames and still clears them when switching
to docked chat.

## Regression coverage

Five initial chat regressions failed before the change and passed after it.
They cover Twitch/Kick, docked/overlay layout, restarts, visibility, and unresolved
archive startup. Two additional tests read actual rendered/blank frame messages
from the native overlay pipe through restart, hide/show, and layout changes.

Native completion regressions cover a fourteen-hour local TS timeline, muted
timestamp repair through the production gateway, backward seeking after EOF,
and the actual xQc CDN media with native overlay enabled. The actual-media tests
passed for exact-end seeking, playing the final five seconds, and reopening the
finished archive at its final frame. They also seek backward and verify playback
resumes. Set `SVS_TEST_VLC_DIRECTORY` to enable native tests and
`SVS_TEST_LONG_VOD_URI` to opt into the real archive tests.

The existing main/detached template tests verify the finished screen renders
and seek controls remain usable. Public media availability and later upstream
changes are outside this regression's guarantees.

The native-enabled full run executed 1,414 tests with no skips or timeouts:
1,406 passed initially. Six desktop pixel/capture checks were configured with
the square replay-position fixture instead of the required steady widescreen
fixture; all six passed with a 960x540 steady MJPEG fixture. A replay-scroll
timing check passed in isolation. One log assertion still expected the removed
claim that updating VLC fixes all muted playback; its assertion now checks the
actual timestamp-repair diagnostic and passes. These initial failures remain
in `full-tests.log`; their individual reruns are recorded in `rechecks.json`.

The final Release solution build with warnings treated as errors completed
with zero warnings/errors. The self-contained publish and `git diff --check`
also passed.

Final focused verification: **27 passed, zero failures/skips** (eight individual
reruns, seven chat-routing checks, and twelve long-VOD checks, including the real
archive's saved position and three completion scenarios).

The verified self-contained executable was installed at
`C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe`.
Its SHA-256 matches the published build:
`3B858429B53CFEC4A67DBCED489882A8B565F25B7B4B4162D08DA6CD8C5C9A73`.
The previous executable was backed up to
`artifacts/vod-end-chat/installed-before.exe`; its hash matches the previously
installed build. Settings and VOD history were not replaced.
