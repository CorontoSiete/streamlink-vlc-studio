# Filtered Windows GDI output

Replay output can be gated by the player variable `studio-replay-output-ready`,
which names a per-player manual-reset Windows event. The renderer opens its own
synchronization handle and presents black while it is unsignalled. Decoding and
frame accounting continue, allowing the managed engine to confirm the seek and
release the gate. This avoids inserting VLC's CPU brightness/saturation filter
into a hardware-surface pipeline. An invalid event prevents output startup;
release or teardown closes the renderer's handle. The engine retains its handle
until that player stops, so recreated outputs inherit the correct released state.
Players without the variable display normally. See the
[replay regression diagnosis](../../../docs/replay-green-screen-2026-09-26.md).

The window/output support files come from VideoLAN VLC **3.0.23**,
[`modules/video_output/win32`](https://github.com/videolan/vlc/tree/3.0.23/modules/video_output/win32),
and retain their original LGPL-2.1-or-later notices. See `COPYING.LIB`.

The stock output composes chat into the source image, then uses
`StretchBlt(COLORONCOLOR)` to fit the video window. This deletes pixel rows and
columns: narrow glyph stems disappear and gaps between strokes close. The
modified output receives RGBA subpictures separately, filters the chat at its
displayed size, then blends it onto the video in an offscreen GDI bitmap. One
final blit presents the combined image. At 1:1, no chat resampling is needed.

`compositor.h` converts straight RGBA to premultiplied BGRA before filtering,
preserving text/emote coverage without dark or colored fringes. `scaler.h` uses
VLC's installed swscale converter in area mode for downscaling and bilinear
mode for enlargement. DIB scanlines are padded for SIMD alignment. Input
buffers, output bitmaps and converters are cached by layer geometry, and
released when layers disappear, resize, or the output closes. Chat is blended
in RGB after video scaling, avoiding video chroma subsampling of colored text.

Each layer also retains a snapshot of its visible RGBA bytes. Identical content
reuses the premultiplied and scaled bitmap; video still gets a fresh blend on
every frame. Byte comparison handles pictures reused or changed in place by
VLC, and new pictures with identical content. Cropping, size changes, and even
a single changed alpha byte invalidate the appropriate work. This adds one
source-sized RGBA snapshot per visible layer, released with that layer.

With the verified VLC **3.0.23** runtime, the app enables
`studio-gdi-gpu-scaling`. `hardware.h` accepts progressive eight-bit SDR DXVA2
pictures directly. A DXVA2 video processor converts and scales them to the
visible tile size, and only that RGB target is downloaded into the GDI canvas.
The source resolution and frame cadence stay unchanged. The final GDI blit,
filtered chat and replay output gate are shared with the existing path. There
is no Direct3D presentation call or video swapchain presented over the window.

The decoder borrows the output's device through a shared 16x16 NV12 descriptor
surface. Pool pictures do **not** allocate another full-size surface each. The
real decoded surface belongs to VLC's picture context. Its private layout in
`dxva2-picture.h` is extracted from VLC 3.0.23's
[`d3d9_fmt.h`](https://github.com/videolan/vlc/blob/3.0.23/modules/video_chroma/d3d9_fmt.h)
and [`va_surface.h`](https://github.com/videolan/vlc/blob/3.0.23/modules/codec/avcodec/va_surface.h),
retaining their LGPL attribution. The managed runtime gates this path on the
exact version and bundled plugin hash. Other VLC 3 versions use the original
converter. Decoder restarts are checked against the current surface's device;
resources from one device are never used to read another device's frames.

BT.601/709 and full/limited range are passed to the video processor. Ten-bit,
HDR, unsupported chroma locations and rotated sources keep VLC's converter.
Missing GPU capabilities also negotiate the original RGB path at startup. If
readback later fails, `HardwareDownload` creates VLC's existing full-resolution
DXVA2-to-RGB converter chain, retaining hardware decoding and chat. Its colors
match the prior VLC converter, including VLC 3's legacy BT.601 RGB conversion;
the primary GPU path honors source color metadata. Both paths release pictures,
device references, filters and GDI resources on close. Hidden zero-size outputs
do not trigger an unnecessary fallback download.

The app uses one FFmpeg frame worker for this hardware configuration, including
prepared replay inputs. VLC's DXVA2 decoder still allocates the codec's required
reference surfaces; it avoids the additional surfaces for parallel CPU frame
workers. Software/custom-overlay replay retains its prior worker policy. No
source quality setting, audio policy, frame skipping or CPU decoding switch is
used to obtain the savings. Measurements and hardware limitations are recorded
in `docs/multistream-gpu-resources-2026-09-27.md` in the repository.

The app requires swscale and explicitly selects `studio_gdi` with accelerated
native chat, preventing an invisible-chat fallback to stock GDI's early blender.

The module is registered as `studio_gdi` inside `libmyoverlay_plugin.dll`.
The app requests `studio_gdi` with accelerated native chat, or
`studio_gdi,wingdi` in software compatibility mode, including after
`libvlc_media_player_set_hwnd` resets output options. Old custom overlay DLLs
can still fall back to stock `wingdi`. The custom output also declines loading
if the VLC installation lacks swscale. No installed VLC files are modified.

Upstream adaptations are limited to:

- `wingdi.c`: separate subpicture composition using `compositor.h` and
  `scaler.h`, and display-sized DXVA2 readback using `hardware.h`;
  open/close callbacks renamed and registered in the overlay's
  module descriptor. Coordinate mapping follows VLC's Direct3D output.
- `common.c` / `common.h`: omitted the unused DirectDraw/D3D plane-update helper
  and its chroma-copy dependency; GDI owns its bitmap.
- `events.c`: corrected the window procedure's return type to pointer-sized
  `LRESULT` on x64, marked existing switch fallthroughs for GCC 16, and removed
  a disabled cursor-debugging block.
- `build-config.h`: definitions normally provided by VLC's generated config.

Window creation, mouse events, fullscreen, clipping, aspect ratio, gestures,
sensors and lifetime handling otherwise use upstream code. Compile through
`scripts/build-chat-overlay.ps1` with matching MinGW-w64 GCC/G++ (MSVCRT) and
VLC headers/import library. The output uses VLC's release `NDEBUG` configuration;
its debug-only host-window assertions must not terminate the embedding app.
Compiler support libraries are statically linked;
the build rejects external compiler-runtime DLL dependencies. The script allows
VLC 3's deprecated `manage` callback and treats other warnings as errors.

The actual VLC rendering regression in `ApplicationTestCatalog.OverlayReadability.cs`
feeds opaque alternating one-pixel rows/columns through the production pipe.
At 50% scale all 112 interior samples must preserve their half-covered gray
value. Stock GDI fails with 0/112; this output passes with 112/112. The same
test captures live/native and replay/WPF text through eight window transitions,
requiring stable overlay bytes and saved panel size.

`scripts/test-chat-compositor.ps1` tests the actual compositor with VLC's
installed scaler: transparent saturated colors, partial alpha, source crops,
off-window clips, odd-width resize alignment, cache reuse and GDI handle
cleanup. It also measures repeated chat composition on 1080p and 2160p sources.

`scripts/test-gdi-hardware.ps1` requires a real DXVA2 GPU and VLC 3.0.23. It checks
all four BT.601/709 and full/limited-range combinations against known colors,
fractional resizing, crops, separately composed chat, format/interlace guards,
fallback pixels after a different-device frame, continued frame changes, late
picture ownership, and GDI handle cleanup. An unavailable hardware path fails
the test rather than silently skipping it.
