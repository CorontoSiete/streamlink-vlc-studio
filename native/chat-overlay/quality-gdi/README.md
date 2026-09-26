# Filtered Windows GDI output

The window/output support files come from VideoLAN VLC **3.0.23**,
[`modules/video_output/win32`](https://github.com/videolan/vlc/tree/3.0.23/modules/video_output/win32),
and retain their original LGPL-2.1-or-later notices. See `COPYING.LIB`.

The stock output composes chat into the source image, then uses
`StretchBlt(COLORONCOLOR)` to fit the video window. This deletes pixel rows and
columns: narrow glyph stems disappear and gaps between strokes close. The
modified output receives RGBA subpictures separately, filters the chat at its
displayed size, then blends it onto the video in an offscreen GDI bitmap. One
final blit presents the combined image. Video retains its existing fast path;
only chat incurs filtering work. At 1:1, no chat resampling is needed.

`compositor.h` converts straight RGBA to premultiplied BGRA before filtering,
preserving text/emote coverage without dark or colored fringes. `scaler.h` uses
VLC's installed swscale converter in area mode for downscaling and bilinear
mode for enlargement. DIB scanlines are padded for SIMD alignment. Input
buffers, output bitmaps and converters are cached by layer geometry, and
released when layers disappear, resize, or the output closes. Chat is blended
in RGB after video scaling, avoiding video chroma subsampling of colored text.

The module is registered as `studio_gdi` inside `libmyoverlay_plugin.dll`.
The app requests `studio_gdi,wingdi` when using native chat, including after
`libvlc_media_player_set_hwnd` resets output options. Old custom overlay DLLs
can still fall back to stock `wingdi`. The custom output also declines loading
if the VLC installation lacks swscale. No installed VLC files are modified.

Upstream adaptations are limited to:

- `wingdi.c`: separate subpicture composition using `compositor.h` and
  `scaler.h`; open/close callbacks renamed and registered in the overlay's
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
