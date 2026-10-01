# Replay position fixture

`replay-position-colors.mp4` is generated test media with no audio: 34 seconds of
red, six seconds of green, then 20 seconds of blue, 64 x 64 pixels at two frames per second.
The native resume regression checks the first **presented content** frame at 35.25 seconds,
independently of VLC's playback clock. It must be green; red is a restart and blue is the live edge.

Recreate with FFmpeg:

```text
ffmpeg -f lavfi -i color=red:s=64x64:r=2:d=34 -f lavfi -i color=lime:s=64x64:r=2:d=6 -f lavfi -i color=blue:s=64x64:r=2:d=20 -filter_complex "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]" -map "[v]" -an -c:v mpeg4 -q:v 2 -g 2 replay-position-colors.mp4
ffmpeg -i replay-position-colors.mp4 -an -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 2 -sc_threshold 0 -hls_time 10 -hls_list_size 0 -hls_playlist_type event -hls_flags omit_endlist replay-position-event/index.m3u8
```

The HLS variant deliberately has `PLAYLIST-TYPE:EVENT` and no `ENDLIST`, like an
ongoing broadcast replay. Both variants are checked at the presentation callback.
Frames during preparation must be black; all content frames after restoration must
be green. The target HLS segment begins at 30 seconds and contains red preroll,
so displaying that segment before the precise seek completes also fails the test.

`replay-position-audio` adds silent AAC to the same generated colors. It checks
that background live replay preparation initializes the audio clock with zero
decoded/displayed video, pauses without disturbing live playback, and subsequently
presents green at 35.25 seconds. Generate it with:

```text
ffmpeg -i replay-position-colors.mp4 -f lavfi -i anullsrc=r=48000:cl=stereo -t 60 -map 0:v -map 1:a -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 2 -sc_threshold 0 -c:a aac -b:a 32k -hls_time 10 -hls_list_size 0 -hls_playlist_type event -hls_flags omit_endlist replay-position-audio/index.m3u8
```

`replay-position-long-gop` uses the same colors and frame rate with a keyframe
only every ten seconds. Opening at 35.25 seconds must present green. FFmpeg's
HLS seek without decoder preroll skips forward to the blue keyframe at 40 seconds.
This completed playlist checks the fast resume path independently of its clock.
Generate it with the second command above, replacing `-g 2` with `-g 20`, using
`-hls_playlist_type vod`, removing `-hls_flags omit_endlist`, and writing to
`replay-position-long-gop/index.m3u8`.

`playback-rate-tone` is an eight-second, one-segment HLS VOD with a continuous
440 Hz MP2 tone in an MPEG-TS segment. The native audio-output regression plays
this through VLC's HLS demuxer from both a file and a loopback HTTP server, then
measures Windows loopback output while changing speed. Keep `index.m3u8` and
`tone.ts` together so the playlist's relative segment URL resolves in both cases.

`hover-preview-fmp4` contains twelve seconds of generated moving test patterns,
160 x 90 at 30 fps, in two-second fragmented MP4 segments with a whole `init.mp4`.
It exercises the hover preview's validated map, native HLS initialization, changing
video across segment boundaries, and cancellation without live-provider access.
Generate it with:

```text
ffmpeg -f lavfi -i testsrc2=size=160x90:rate=30:duration=12 -an -c:v libx264 -preset fast -crf 28 -pix_fmt yuv420p -g 60 -sc_threshold 0 -f hls -hls_time 2 -hls_list_size 0 -hls_flags omit_endlist -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 -hls_segment_filename hover-preview-fmp4/segment%d.m4s hover-preview-fmp4/index.m3u8
```
