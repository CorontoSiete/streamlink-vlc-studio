# MP4 conversion failure in muted Twitch segments

The reported conversion failure was reproduced against the saved Twitch VOD
`2867261553`, with 1,386 segments and a playlist duration of `03:51:27.184`.
The installed converter was FFmpeg `n8.1.2-20260623` from the configured
Streamlink installation.

## Cause

The original converter supplied `-xerror`. FFmpeg documents this option as
[stopping on an error](https://ffmpeg.org/ffmpeg.html#Advanced-options).
It also stops stream copying when a demuxer marks a packet corrupt, even if the
encoded video and audio remain usable.

The unmodified conversion reproduced the reported `-1094995529` exit at
`01:36:17.598667`, while reading `asset-000577-muted.ts`. The detailed trace
identifies input stream 1 as H.264 video. Debugging the preceding muted segment
shows continuity-check failures for PIDs 256 and 257 in trailing adaptation-only
transport packets: their counters advance even though they contain no payload.
The MPEG-TS demuxer consequently flags the buffered video packet as corrupt.

A complete remux using FFmpeg's normal recovery behavior finished successfully.
The payload comparisons below establish that these warnings did not require
discarding media or changing encoded video or audio.

## Change

`FfmpegVodMediaConverter` omits `-xerror` during stream copying and retains warning
diagnostics in its bounded error buffer. It does not enable `discardcorrupt` or
re-encode media. `-abort_on empty_output+empty_output_stream` rejects empty output;
nonzero process exits and the existing complete-duration check still reject
failed or incomplete conversions. Package validation and the existing atomic
commit continue to protect the playable original.

The generated-media regression changes only transport continuity counters in
one segment. It first proves that strict FFmpeg fails with the same corrupt-packet
error, then requires successful offline conversion and equal hashes of every
decoded video frame and audio sample. This test failed before the fix and passed
afterward. Additional real-FFmpeg tests cover an output file locked against writes
and media whose actual duration is shorter than its source playlist.

The format-test service also uses the fixture's deterministic public-address DNS
result alongside its fake HTTP transport. A test run exposed a timeout before
conversion began; the old factory used the shared live DNS resolver. Provider
URL and public-address validation still run, without depending on external DNS.

## Verification on the reported download

- The complete converted MP4 contains `14,202,589,524` bytes. FFmpeg's final output
  time is `03:51:27.165667`, within 19 milliseconds of the playlist duration.
- All H.264 NAL-unit counts, byte counts, and SHA-256 hashes match after removing
  Annex B versus MP4 start-code framing. This includes 826,232 non-IDR slices,
  6,960 IDR slices, and all parameter sets, SEI, access-unit delimiters, and filler.
- The complete AAC payload hashes match after the standard ADTS-to-ASC container
  conversion: `8029060ad1547d6e80cf2f64442b43dd2f2fd67ebc45b85f369ef267b2386949`.
- Strict audio/video decoding succeeds without diagnostics around the first
  failing muted boundary, in a later muted region, and at the end of the MP4.
- The actual `VodDownloadService`, with online Streamlink access prohibited,
  converted and committed the user's existing library entry in approximately
  46 seconds. It is saved as completed MP4, with no error or pending conversion;
  the original HLS files were retained. No redownload was needed.
- That committed MP4 is byte-identical to the fully inspected candidate:
  SHA-256 `aaf59474f3d6ffeabd0157b75dafaf2c60c8d5664a363f82e32f26149d99c091`.

The 20 format tests and 64 download tests passed with zero skips. The Release
build passed with warnings treated as errors, and whitespace verification passed.
The rebuilt self-contained application at `artifacts/vod-formats/app/StreamStudio.exe`
passed its dependency verification for Streamlink, VLC and the bundled core,
WebView2, Skia, and HarfBuzz.

Local logs, payload hashes, the original failure record, and production conversion
reports are retained under `artifacts/mp4-conversion-diagnosis-20261015/`.
