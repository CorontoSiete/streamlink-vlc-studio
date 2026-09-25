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
