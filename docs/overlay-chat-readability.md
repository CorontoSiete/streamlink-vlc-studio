# Overlay chat clarity and sizing

## Cause and fix

The screenshot's windowed letter distortion was reproduced in the actual VLC
output. VLC 3.0.23's stock Windows GDI renderer blends chat into the source video,
then uses `StretchBlt(COLORONCOLOR)` to fit the window. That mode discards pixel
rows and columns, removing thin strokes and joining neighboring strokes. The
previous alpha-edge correction could not prevent this later loss of detail.

The bundled plugin now includes `studio_gdi`. It receives chat as a separate
RGBA subpicture, converts it to premultiplied BGRA, filters it at its displayed
size, then blends it over the video in RGB. Area filtering preserves pixel
coverage on reduction; bilinear interpolation handles enlargement. A single
final GDI blit presents the combined image. The video retains its existing
fast scaling path, and colored chat avoids video chroma subsampling.

Font size, message wrapping, source-coordinate panel bounds, input hit testing,
and saved dimensions remain unchanged. This does not restore the earlier
window/DPI font compensation. The existing straight-alpha controller output
and grayscale fallback text fixes remain active.

The app selects `studio_gdi,wingdi` for native overlays both at startup and
after a video HWND rebind. Older custom overlay DLLs retain the stock fallback.
No installed VLC files are changed. The source and LGPL license accompany app
builds; see [native implementation](../native/chat-overlay/quality-gdi/README.md).

## Verification

The real VLC test sends alternating one-pixel strokes through the production
pipe and measures their final on-screen coverage. At half scale the old output
fails with **0/112** correct samples; the new output passes with **112/112**.
The same test displays the screenshot's username and text, and native live-chat
frames, across eight sizes/transitions: fractional resizing, letterboxing,
fullscreen-sized playback, and return to the original size. It requires
identical incoming bitmap bytes and an unchanged saved panel-size file.

Native compositor tests use the installed VLC scaler and cover invisible
saturated colors, partial opacity, dark/bright backgrounds, source cropping,
clipped positions, odd output widths, cached allocation reuse and zero GDI
handle growth through repeated resizing and teardown. The native controller
suite covers font resources, source resolution, clipping, actual event-pipe
validation and alpha conversion.

Measured chat composition on this machine (200 frames each, including
premultiplication, filtering and blending): about **1.1 ms/frame** for a 1080p
source panel and **2.6 ms/frame** for its 2160p counterpart in a 720p window.
Only the chat panel is filtered. These are compositor measurements, not a
benchmark of decoding, networking or the full application.

Final checks: all 153 overlay regressions, both live/replay resize captures,
the native controller and compositor suites, five window-sharing/rebind tests,
and 14 tooling checks passed. Continuous resizing captured 517 frames with no
blank video frames. The Release build and self-contained publish succeeded.
An independent rebuild matched the bundled plugin SHA-256
`fa6d7adae97e95cfd8fe7d49fb9860f29baa7c6ac202b9cde977752555d4033a`.

Builds, test logs, reproducible native binaries and full VLC captures are in
`artifacts/overlay-filtered-scaling`. The runnable self-contained build is
`artifacts/overlay-filtered-scaling/app/StreamStudio.exe`.

## Upstream references

- [VLC Windows GDI output](https://github.com/videolan/vlc/blob/3.0.23/modules/video_output/win32/wingdi.c)
- [VLC subpicture delivery](https://github.com/videolan/vlc/blob/3.0.23/src/video_output/video_output.c)
- [VLC scaling modes](https://github.com/videolan/vlc/blob/3.0.23/modules/video_chroma/swscale.c)
- [Win32 stretch-mode behavior](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setstretchbltmode)
