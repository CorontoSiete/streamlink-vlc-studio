# Live multistream chat resource usage — September 27, 2026

The native chat helper shared by Twitch and Kick now avoids full catalog scans,
reuses unchanged static frames, sizes its pixel buffers to the chat panel, and
releases references acquired when measuring cached DirectWrite text.
Stream quality, video decoding, frame rate, audio policy, panel dimensions,
font size, filtering, emote timing and the existing pipe heartbeat are unchanged.

## Reproduced problems

- Every word lookup scanned the entire emote/badge catalog, including ordinary
  text that was not an emote. Message layout performs these lookups repeatedly.
- The render thread rebuilt and converted static chat on every 80 ms heartbeat.
- A 340×292 chat panel at the 1080p source scale reserved two 1920×1080 buffers:
  **16,588,800 bytes** in total, despite needing 397,120 visible bytes per image.
- Each successful cached width measurement acquired a DirectWrite reference
  without releasing it. The native regression measured a reference count of
  **2 → 1,002 after 1,000 calls** on the original code.

## Measurements

These are **native chat renderer measurements**, excluding video decoding,
Streamlink, network traffic, WPF and pipe transmission. Four independent render
contexts use the same deterministic 100-message history and a 4,000-entry local
catalog. Each context processes 400 frames at an explicit 80 ms animation-clock
step. The panel is 340×292 with the existing 15-pixel reference font; animations
use a real GIF with three frames and unequal delays. No assets are downloaded.

Median process CPU time from three fresh-process runs of each workload/version:

| Workload, 1,600 total frames | Before | After | Reduction |
| --- | ---: | ---: | ---: |
| Unchanged static chat | 5.453125 s | 0.015625 s | 99.7% |
| A new message on every tick | 5.609375 s | 1.671875 s | 70.2% |
| Animated emotes with unchanged text | 5.218750 s | 1.562500 s | 70.1% |

The quiet case avoids all 1,600 redundant render/conversion calls. Its remaining
measurement includes the benchmark's pixel hashing and is close to the Windows
process CPU counter's sampling granularity. Busy and animated cases still render
all 1,600 frames. The indexed catalog removes their repeated linear scans.
These percentages are not predictions of total application CPU savings.

Sampled RGBA output hashes match before and after in **all 18 runs**:

| Workload | Cumulative FNV-1a hash |
| --- | --- |
| Quiet | `f32ead498bac9945` |
| Busy | `ef57cbe4d461674d` |
| Animated | `708b67e0b672cca5` |

Each tested panel now reserves **983,040 bytes** for its two pixel buffers,
down from 16,588,800 bytes. Four contexts save **62,423,040 bytes (59.53 MiB)**
of pixel-buffer capacity. Each separate helper process also adds a 256 KiB
catalog index. These are allocation capacities, not guaranteed reductions in
Task Manager's working set. Raw process memory counters are retained alongside
the timings; Direct2D/driver allocations varied between fresh processes.

Measurements used this machine's Intel Core i9-13900K, 24 reported logical CPUs,
NVIDIA RTX 4080 and x64 MSVCRT GCC 16.1.0 with `-O2`. Trials ran sequentially,
alternating version order, without concurrent builds or tests. CPU seconds sum
all threads in the benchmark process. The explicit animation clock permits
identical output comparisons without relying on network or wall-clock timing.

## Implementation details

- A fixed 65,536-bucket index maps exact, case-sensitive codes to stable catalog
  indices. It uses the catalog's existing lock, compares full codes on hash
  collisions and stays at most half full under the existing 32,768-entry limit.
  Replacement and queued image-load indices retain their existing semantics.
- Pixel buffers start near the requested panel dimensions, rounded to 64 pixels.
  Subsequent growth reserves geometric spare capacity to avoid allocating at
  every mouse move. If spare capacity would exceed the 32 MiB per-buffer limit,
  allocation falls back to the exact valid request. Fonts and layouts survive
  ordinary panel resizing. Failed growth preserves the previous buffers.
- Static-frame reuse stores no extra image. It retains the existing RGBA buffer
  until the render generation changes. Catalog changes now request rendering,
  including late catalog loads that previously relied on polling. Repeated
  identical catalog entries do not invalidate the frame.
- Visible animated emotes, active input, notices and drawing failures continue
  to use the original heartbeat. A notice remains eligible for repaint until
  its disappearance has been rendered. Events arriving during drawing remain
  pending because the generation is sampled before drawing.
- The pipe still receives the current image on every heartbeat, including
  static images. A restarted VLC input receives the cached frame normally.
- Cached text measurements balance the reference returned by the lookup.
  The regression now observes **2 → 2**, then verifies release on cache eviction
  and renderer teardown. This follows Microsoft's
  [COM reference ownership contract](https://learn.microsoft.com/en-us/windows/win32/api/unknwn/nf-unknwn-iunknown-addref).
  GDI pixel reads retain the existing
  [required flush](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-createdibsection).

## Validation

`scripts/test-chat-overlay.ps1` now includes the new `render-resources` regression
alongside the TLS, WebSocket, IRC, shutdown and readability tests. The native
checks passed, including:

- Byte-for-byte comparison against fresh rendering after messages, scrolling,
  late assets, input edits, hover, caret changes and notice expiry on both providers.
- Real GIF frames at their exact boundaries and after looping; animated history
  outside the displayed tail does not keep static chat repainting.
- Actual render-thread pipe delivery, a broken/recreated server, unchanged-frame
  delivery after reconnect, and newly arriving chat after reconnect.
- A change injected during a Direct2D draw, failed-draw retry, and failed bitmap
  growth retaining the old frame and resources.
- Growth, shrinking, odd scanline pitches and source-scale changes. The process
  had **3 GDI handles before and 3 after** repeated render/resize/teardown checks.
- Hash collisions, case-sensitive names, replacement, local files, stable indices,
  maximum catalog capacity and missing entries in a full catalog.
- Cached DirectWrite reference counts, eviction and teardown.

The bundled controller was rebuilt with warnings treated as errors. Its import
table uses the expected Windows/MSVCRT libraries, and the updated size and SHA-256
are recorded in `dependencies/native-overlay.json`. The existing plugin DLL is
unchanged by this update.

Additional application validation passed:

- `scripts/dev.ps1 Check`: formatting, PowerShell/tooling checks, native integrity
  validation and a Release build with **zero warnings/errors**. The full headless
  suite passed **1,261 tests**, with **252 desktop-dependent skips**.
- All **four overlay readability checks** passed on the desktop with no skips,
  using frames emitted by the updated native renderer. All 112 thin-stroke
  coverage samples survived filtering through the tested window sizes.
- All **five window-sharing checks** passed with no skips, including visible
  native chat after detach/reattach and continuous capture across Home, playback,
  resizing and reattachment.
- Four local **1920×1080 H.264/60 fps** videos played concurrently through VLC
  **3.0.23 with DXVA2**, while the real plugin received chat updates at 20 Hz.
  In the 12-second sample, VLC reported **zero lost pictures** on every player
  and 705–720 displayed pictures per player. This is a playback/compositor smoke
  test with synthetic pipe frames; it does not include the live chat controller
  or Streamlink and is not a whole-application performance comparison.

A self-contained Release application was also published successfully to
`artifacts/multistream-chat-2026-09-27/StreamStudio.exe` with warnings treated as
errors. The source and licenses accompany it in the same output directory.

## Reproduction

Correctness checks:

```powershell
.\scripts\test-chat-overlay.ps1 -Gcc C:\toolchain\bin\gcc.exe
```

One benchmark trial (repeat in fresh processes for each workload):

```powershell
.\scripts\test-chat-render-resources.ps1 -Gcc C:\toolchain\bin\gcc.exe `
    -Benchmark busy -Frames 400 -CatalogEntries 4000
```

Use `quiet` or `animated` for the other workloads. `-CatalogEntries 0` measures
the empty-catalog case. `-BaselineSource` accepts a preserved original
`vlc_chat_overlay.c`, with its `protocol.h` and `tls.h` beside it; use a separate
`-OutputDirectory` to retain both executables. The harness renders every frame
with the original code and calls the production cache path with the new code.
`--references` and `--capacity` on the original benchmark executable reproduce
the failing resource checks; `--pipe` checks the production pipe loop separately.

Raw measurements, preserved original sources, frame hashes and the JSON comparison
for this run are under `.tmp/multistream-2026-09-27/`. The original controller
source SHA-256 is `e43ce0ee0481d57e46ba45a996ebc6288529a2763825d43950e53b573f730ee0`;
the measured updated source is
`559ae69667ac9c8377641195b7d5d485b6a856132e9859ca7070175081338eb3`.
