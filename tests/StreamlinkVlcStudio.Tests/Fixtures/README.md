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
