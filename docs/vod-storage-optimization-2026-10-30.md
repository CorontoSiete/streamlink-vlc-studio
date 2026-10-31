# Quality-preserving VOD storage

The MP4 download format now remuxes without re-encoding and removes H.264 filler
NAL units (type 12) when present. Filler contains no coded picture. FFmpeg's
`h264_metadata` filter first limits this path to H.264; if that path cannot
complete, the converter retries its ordinary stream-copy path. Other codecs
continue to use the ordinary path. Conversion still validates the complete
duration and package before replacing the playable original.

The previous full-VOD investigation retained measurements for Twitch VOD
`2867261553` under `artifacts/mp4-conversion-diagnosis-20261015/`:

| Measurement | Bytes |
| --- | ---: |
| Original HLS media | 14,640,902,132 |
| Ordinary stream-copied MP4 | 14,202,589,524 |
| H.264 filler inside that MP4 | 1,318,248,087 |

Ordinary remuxing saved 438,312,608 bytes (2.99% of HLS media). The measured
filler was a further 9.28% of the ordinary MP4. The full media files are no
longer present, so the size of a newly optimized copy of that VOD has **not**
been measured. Savings on other VODs depend on their container and filler
content; no fixed reduction is promised.

A generated four-second H.264/AAC HLS sample with CBR filler measured
1,533,400 bytes as an ordinary MP4 and 729,884 bytes as the optimized MP4.
SHA-256 stream hashes matched for the decoded video and audio. The encoded
H.264 stream hash also matched after excluding filler from both sides. A
separate HEVC sample confirmed that the H.264 filter rejects that codec and
ordinary MP4 stream copying succeeds. The 37 VOD format tests passed with no
skips.

The configured library's current 2:38:20, 1080p60 H.264/AAC MP4 was also
measured end to end. Its file size is 9,328,923,394 bytes. A full H.264 NAL
scan found **zero filler units** and 9,041,104,786 bytes of coded picture NAL
payload. The remaining non-picture H.264 NAL payload totals only 5,823,124
bytes. FFmpeg reported about 254,850,048 bytes of AAC packets and about
20,278,018 bytes (0.22%) of MP4 overhead, with packet totals rounded to KiB.
Strict audio/video decoding passed at the start, midpoint, and end. There is
no large filler or container saving left in this copy. Its earlier HLS source
is not available, so this measurement does not establish how much its original
conversion saved.

A five-second clip from the middle of that MP4 was also copied and encoded
with x265 lossless HEVC and libaom lossless AV1 while copying its AAC audio:

| Same clip | Bytes |
| --- | ---: |
| Original H.264/AAC MP4 | 6,797,257 |
| Lossless HEVC/AAC Matroska | 14,893,137 |
| Lossless AV1/AAC Matroska | 18,901,645 |

Decoded video-frame and audio-sample SHA-256 stream hashes matched all three
files. Both lossless re-encodes were substantially larger for this measured
clip. This is a sample result, not a claim about every possible VOD.

FFmpeg documents [stream copying](https://ffmpeg.org/ffmpeg.html#Streamcopy)
as avoiding decoding and re-encoding and documents the
[`filter_units` bitstream filter](https://ffmpeg.org/ffmpeg-bitstream-filters.html#filter_005funits)
for removing specific NAL types. The
[H.264 specification](https://www.itu.int/rec/T-REC-H.264) identifies type 12
as filler data and specifies no picture decoding process for it.
