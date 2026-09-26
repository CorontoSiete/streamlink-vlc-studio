# Stream playback CPU usage — September 26, 2026

**Follow-up:** [HLS replay testing](replay-green-screen-2026-09-26.md) found a
D3D11 surface-download failure after decoder resets. GDI now selects DXVA2
hardware decoding and uses a presentation gate for replay preparation. The
D3D11 measurements below describe the earlier benchmark build, not the final
decoder policy. The chat allocation/cache improvements remain in place.

The bundled chat compositor now allows hardware video decoding while
retaining GDI window presentation. It also reuses unchanged chat images instead
of repeating premultiplication and scaling for every video frame. Selected stream
quality, source resolution, bitrate, video frame rate, audio policy, chat animation
timing, alpha blending and filtering are unchanged.

## Measured result

Four simultaneous **1920×1080 H.264 streams at 60 fps** used **60–67% less
process CPU time** in the local playback benchmark (median of three fresh-process
runs per variant/workload). VLC reported **zero lost pictures in all twelve
runs**. This measures playback and plugin composition, not total application CPU
with live Streamlink/network/chat-controller activity.

| Four streams, 12-second measurement | Original CPU time | Updated CPU time | Reduction |
| --- | ---: | ---: | ---: |
| Static chat | 24.125 s | 7.922 s | 67.2% |
| Animated chat, 20 updates/s | 18.703 s | 7.406 s | 60.4% |

Measurements used this machine's Intel Core i9-13900K (24 reported logical CPUs),
NVIDIA RTX 4080 and VLC 3.0.23. CPU seconds sum all process threads, so they can
exceed wall-clock seconds. The original plugin ran with software decoding; the
updated plugin ran with automatic hardware decoding. A separate verbose probe
confirmed VLC selected D3D11VA on the RTX 4080. Both used identical local media,
chat payloads, window sizes and existing late-frame settings. No other test or
build ran during these measurements.

Original static CPU times were 17.875, 24.125 and 24.766 seconds; updated times were
7.484, 7.922 and 9.516 seconds. Original animated times were 18.703, 16.781 and
19.188 seconds; updated times were 7.297, 9.094 and 7.406 seconds. The variation is
why the comparison uses medians. Savings depend on hardware, codec and workload;
this does not predict a fixed number of additional streams on every computer.

The separate unchanged-chat microbenchmark also improved: median composition
time fell from 1.099 to 0.056 ms/frame at the 1080p source scale, and from 2.626
to 0.105 ms/frame at the 2160p scale (three runs, 200 measured frames each).
These are wall-time measurements for chat composition, not additional whole-app
CPU savings to add to the playback percentages above.

## Implementation and compatibility

Studio GDI already advertises RGBA subpictures to VLC. That bypasses the early
software subtitle blender: VLC downloads/converts decoded video to RGB, then
passes video and chat separately to the output. The former blanket software-only
policy is therefore unnecessary for this compositor. This follows VLC 3.0.23's
[direct subpicture composition path](https://github.com/videolan/vlc/blob/3.0.23/src/video_output/video_output.c).

The app verifies the overlay DLL against the embedded binary's SHA-256 and checks
for swscale in the loaded VLC 3 module bank. Only that combination enables
automatic GPU decoding. Unknown/custom plugins and installations missing swscale
keep the software policy and stock GDI fallback. The accelerated output explicitly
requires Studio GDI, so it cannot silently fall back to a renderer that loses chat.
VLC still chooses software decoding when hardware decoding is unsupported.
The final policy is applied after every HWND bind, including replay preparation,
promotion, player recreation and window reattachment. GPU decoding does not add
a separate Direct3D presentation window.

Each visible chat layer keeps one extra RGBA snapshot for exact byte comparison.
Unchanged pixels reuse the premultiplied/scaled bitmap; changed pixels, including
alpha-only animation, refresh it immediately. Output-size changes rescale the
image; position/opacity changes still apply on every blend. Pixel comparison
handles both reused picture addresses and identical content in new pictures.
Snapshots are released when layers disappear or the compositor closes. A 340×292
layer adds about 388 KiB of visible pixel storage, plus scanline padding.

## Verification

- Release solution build: zero warnings/errors.
- Full headless suite: 1,181 passed, 246 desktop-dependent skips, zero failures.
- Actual VLC/window capture: five passed, including visible native chat after
  detach/reattach and one continuous whole-window capture across navigation and
  resizing with hardware-capable native chat enabled.
- Overlay readability: four passed; all 112 thin-stroke coverage samples preserved
  through eight window-size transitions. The same real-rendering test also passed
  separately with the previous plugin supplied as a custom overlay.
- Native-overlay HLS pause continuity: passed; retained the same input across three
  pauses/resumes. Fast VOD resume: 13 passed, covering bookmark pixels, long GOPs,
  pause, seeking, muted segments, cancellation, fallback and long timelines.
- Native compositor regressions: unchanged images skip scaling; cached output is
  byte-identical to fresh composition after source mutation, picture replacement,
  alpha changes, cropping, movement, resizing and hide/show. GDI handles return to
  their initial count after cleanup.
- Self-contained single-file publish: `artifacts/stream-cpu/StreamStudio.exe`.

## Additional native subpicture optimization

The sub-source now reuses immutable region images, including hidden chat and
hover controls. It also avoids allocating a full RGBA bitmap just to replace it
with the already available chat picture. VLC 3's
[region constructor](https://github.com/videolan/vlc/blob/3.0.x/src/misc/subpicture.c)
supports allocating region metadata without pixels via its text format; the
plugin then restores the original RGBA/sRGB metadata and holds the existing
picture. Tests compare this metadata with VLC's normal RGBA constructor.

Every video frame still receives a newly timed subpicture and format observer.
Chat updates, animation, hover, scrolling, dragging, resizing, hiding/showing and
source-resolution changes invalidate the cached visual state immediately.
Replacing or releasing the cache cannot change pictures still queued in VLC.
Allocation failures leave the next frame able to retry. No decoder, stream,
video frame-rate, audio or scaling settings change in this additional update.

The focused native benchmark used 20,000 frames per case with a 340×292 panel at
the 1080p source scale, comparing the previous sub-source with the updated one.
Median of three runs, using the same VLC and compiler:

| Overlay state | Before, µs/frame | After, µs/frame | Pixel-buffer allocations before → after |
| --- | ---: | ---: | ---: |
| Visible chat | 0.789 | 0.337 | 20,000 → 0 |
| Chat with hover controls | 9.197 | 0.557 | 80,000 → 0 |
| Hidden chat | 12.614 | 0.305 | 20,000 → 0 |

For eight streams at 60 fps, the removed visible-chat allocations total about
226 MiB/s including VLC scanline/alignment padding. This is **allocation churn**,
not resident-memory savings or bytes necessarily written. The timings measure
only subpicture preparation, not decoding, composition or whole-app CPU, and
must not be added to the earlier playback percentages. Small per-frame region
and observer allocations remain. The cache retains one set of region images per
filter; outstanding subpictures share those pixels by reference.

The native regression compares every visible byte and region format with fresh
rendering across state changes, verifies zero pixel allocations after warmup,
tests retry after failed allocations, and retains old subpictures through cache
replacement and owner teardown. Run `scripts/test-chat-subpictures.ps1` with the
same GCC/VLC arguments as `scripts/test-chat-compositor.ps1`. Benchmark sources
and raw before/after results are in `.tmp/multistream-resources/`.

Additional-update verification:

- Release build: zero warnings/errors. Full headless suite: 1,181 passed,
  246 desktop-dependent skips, zero failures.
- Native subpicture and compositor regressions passed. Nine desktop checks
  passed, including native chat after detach/reattach and all 112 thin-stroke
  coverage samples across eight window-size transitions. An initial concurrent
  desktop run captured a terminal over the video and failed; running the desktop
  checks alone passed without changing the implementation or test assertions.
- Eight local 1920×1080 H.264/60 fps streams with chat updated 20 times per second:
  zero VLC-reported lost pictures in a 12-second measurement. Process CPU time was
  13.375 seconds; private bytes at the end were 1,212,100,608. This includes the
  playback benchmark process, not live Streamlink/network/controller activity.
- Sixteen-stream stress runs overloaded this setup with both the preceding
  plugin and the updated plugin, with substantial lost pictures. Those runs
  are not quality-preserving performance results, and this change does not
  establish support for 16 smooth live streams on this machine.
- Self-contained single-file build: `artifacts/multistream-resources/StreamStudio.exe`.

## Reproduce the playback benchmark

Generate a local moving 1080p60 H.264 fixture with at least 25 seconds of content:

```powershell
ffmpeg -f lavfi -i "testsrc2=size=1920x1080:rate=60" -t 45 -c:v libx264 -preset fast -crf 18 -threads 6 -pix_fmt yuv420p -an 1080p60.mp4
python scripts/measure-playback-cpu.py --media 1080p60.mp4 --overlay-directory src/StreamlinkVlcStudio.Infrastructure/Vlc/BundledOverlay --hw any --output playback.json
```

Use `--chat-hz 20` for animated chat, `--hw none` for software decoding and
`--overlay-directory` to select an isolated original plugin build. The script
opens four 640×360 video surfaces, sends deterministic 340×292 RGBA chat images
through the real plugin pipe, warms up for six seconds and measures for twelve.
It records source dimensions, process CPU time and VLC presented/lost pictures.
It also records process private bytes and working-set bytes before and after the
measurement; those memory counters are separate from allocation volume.
It rejects runs without continuing video output. Run trials sequentially in fresh
processes without other builds or tests competing for CPU. These measurements
exclude Streamlink, network traffic and chat-controller/WPF rendering.

Run `scripts/test-chat-compositor.ps1` for exact cached/fresh pixel comparisons
and the repeated-composition microbenchmark. Real VLC tests use the environment
variables documented in the main README and filters `window sharing`,
`overlay readability`, `pause continuity: native overlay` and `VOD fast resume:`.
Raw logs, comparison data and captures for this run are in `.tmp/stream-cpu/`.
