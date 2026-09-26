# Saved VOD decoding latency (2026-09-26)

Saved Twitch VODs already use fresh direct resolution, playlist handoff and a
precise FFmpeg opening seek. On the production native overlay path, the remaining
startup work includes software decoding of the segment preceding the bookmark.
VLC 3.0.23 normally caps automatic H.264 decoding at six workers, even on this
machine's 24 available processors. See the [VLC decoder implementation](https://github.com/videolan/vlc/blob/3.0.23/modules/codec/avcodec/video.c).

For the validated completed MPEG-TS replay path with the native overlay, opening
a saved position now sets a per-media decoder worker count to the available
processor count, capped at VLC's H.264 limit of sixteen. CPUs with six or fewer
available processors retain VLC's automatic setting. The setting is recreated
when the replay output moves to another window.

This uses more decoder parallelism and associated frame buffers. It does not
reduce network buffering, discard additional frames, change the bookmark,
release the black/audio gate earlier, or change the exact-seek filter. Live
playback, ordinary opens from the beginning, hardware decoding and unsupported
replay formats retain their existing settings. Faster startup depends on the
CPU, media and CDN; the change does not make network playback instantaneous.

## Verification

The first-presented-pixel regressions now apply the production decoder policy to
their software-decoded memory output, including the long-GOP fixture whose next
keyframe has a different color and the buffered-pause check. The native overlay
requires window output, so the separate production playback regression checks
the full overlay path and requires it to load successfully.

The provider benchmark includes fresh authorization/resolution, playlist
preparation, player creation, confirmed restoration and a subsequent displayed
picture. Each open also checks full duration, position, five seconds of advancing
clock/video and bounded stop. It excludes UI navigation and closing an old tab.
No settings or playback-history files are modified by these tests.

Comparison logs and a sequential reproduction script are retained under the
ignored `artifacts/vod-resume-opening` directory. The baseline uses a saved copy
of the infrastructure assembly taken before this change, with the same test
harness and native modules.

The final comparison used Twitch archive `2875113048` at `05:36:16.717` with
the installed VLC 3.0.23 and native overlay. Groups of three opens ran in
before / after / after / before order, without concurrent playback or builds:

| Path | First displayed output, six opens (ms) | Mean |
| --- | --- | ---: |
| Original automatic decoder workers | 3303, 2692, 2740, 3222, 2723, 2707 | 2898 ms |
| Updated decoder workers | 2776, 2307, 2443, 2820, 2279, 2226 | 2475 ms |

The measured reduction is **423 ms (14.6%)**. Warm opens averaged 2716 ms
before and 2314 ms after. All twelve opens passed position, duration, sustained
video and stop assertions. This includes cold initialization and normal CDN
variation; an earlier exploratory comparison showed a larger gain, which is not
used for the final performance claim.

Final validation passed: Release solution build with warnings as errors; all
13 fast-resume regressions with native VLC enabled; an additional long-GOP pixel
run with `DOTNET_PROCESSOR_COUNT=4` to exercise the default-worker selection;
scoped C# whitespace verification; and `git diff --check`. The native coverage
includes ordinary/muted segments, first correct pixels, pause, forward/backward
seeks, window moves, completion, missing-filter fallback, stalled-read
cancellation and the 28-hour timeline crossing the MPEG-TS timestamp rollover.

The self-contained Windows x64 publish also passed with warnings as errors.
The installed executable at
`C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe` was replaced
atomically and verified against the publish. Its SHA-256 is
`D2A4223FD1ED0B33297564424920FB17C75DE7AAA9BA646A8BE161E89C4BC532`.
The verified previous executable remains in
`artifacts/vod-resume-opening/installed-before.exe`, with SHA-256
`5D5C802B73CD465A0696AC4B27CE26B586D616449CDED6A9C1E0E6F669867927`.
