# Native chat overlay

This directory contains the recovered source for the bundled VLC plugin and
Twitch/Kick controller. The recovered plugin binary matched the previously
pinned SHA-256 `34fddd30f9a6c163ed14e5992da6f7c8213c809ad60a66a856a017ed2d7a0318`.
The shipped controller's stale keyboard-modifier fix is now expressed in source.

Build with x64 MSVCRT MinGW-w64 GCC (verified with WinLibs GCC 16.1.0) and the
VLC 3.0.23 headers/import library:

```powershell
./scripts/build-chat-overlay.ps1 -Gcc C:/toolchain/bin/gcc.exe `
    -VlcIncludeDirectory C:/vlc-3.0.23/include `
    -VlcLibraryDirectory C:/vlc-sdk/lib
```

The script rejects additional compiler runtime DLLs, treats warnings as errors,
removes PE timestamps and maps the repository path out of compiler-generated
file names. Use `-OutputDirectory` to rebuild independently for byte comparison.
After an intentional binary update, update the lengths and SHA-256 hashes in
`dependencies/native-overlay.json`; ordinary .NET builds still verify and embed
these pinned binaries without requiring GCC.

## Overlay sizing

The overlay uses decoded-video coordinates. Resizing the app must not resize
the chat bitmap, reflow its messages, or change its saved reference panel size.
VLC displays the existing overlay with the video as before. The earlier window
and DPI compensation was removed at the user's request.

Protocol v1 event type 8 announces the decoded source height as the render scale;
it no longer depends on the video HWND's size or DPI. Source-size event type 7
continues to announce the decoded source width and height. Actual source
resolution changes still update the fonts and canvas, and manually resizing
the overlay still updates its saved size.

The compositor supplies the decoded format through a subpicture observer;
`filter->fmt_out` is not reliably populated for a sub-source. Reference-counted
metrics remain valid until both the filter and its outstanding subpictures have
been destroyed. A transparent startup subpicture initializes this observation
without requiring a mouse movement or visible chat. Source updates are limited to
20 per second and repeated twice per second for controller/replay handoffs.

The native controller and WPF replay renderer use the same scale for text,
emotes, spacing, input reserves, and saved reference panel sizes. The native
controller rebuilds its GDI/DirectWrite fonts only when their pixel size changes.
Panel and payload constraints reduce the number of visible messages rather than
shrinking the selected font. Mouse hit testing remains in VLC source coordinates.
Older plugins without type 8 retain the original source-resolution sizing.

Relevant upstream interfaces:

- [VLC 3.0.23 video/subpicture composition](https://github.com/videolan/vlc/blob/3.0.23/src/video_output/video_output.c)
- [VLC 3.0.23 subpicture updater contract](https://github.com/videolan/vlc/blob/3.0.23/include/vlc_subpicture.h)

## Verification

Shutdown waits for all controller workers before freeing shared state. If a
network worker does not stop within the shared 12-second budget, the standalone
controller exits through `TerminateProcess`; it never kills an individual worker
and then attempts cleanup using locks that worker might own. The native tests
exercise both completed workers and a child process with a blocked lock owner.
This follows the [Windows process-exit guidance](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-exitprocess).

```powershell
./scripts/test-chat-overlay.ps1 -Gcc C:/toolchain/bin/gcc.exe
$env:SVS_TEST_FILTER = 'overlay readability'
$env:SVS_TEST_VLC_DIRECTORY = 'C:/Program Files/VideoLAN/VLC'
dotnet run --project tests/StreamlinkVlcStudio.Tests --no-build
```

The native tests render deterministic messages without connecting to chat or
installing keyboard hooks. Protocol tests also feed deterministic TCP/TLS data
through the production readers. They cover buffered and incomplete TLS handshake
records, output-token cleanup, WebSocket upgrade validation, bounded fragmented
UTF-8 messages with interleaved ping/pong, malformed frames, and oversized IRC
lines. The renderer tests cover decoded source resolution changes,
font recreation, panel bounds and cached fonts during width-only resizing. Source
events go through the real named-pipe reader and event validator, including
repeated unchanged announcements. Pixel checks verify straight-alpha
DirectWrite glyph edges, translucent emotes and input backgrounds against dark
and bright video, plus grayscale GDI fallback rendering. They
also write protocol frames to `.tmp/chat-overlay-tests`. Set
`SVS_TEST_NATIVE_OVERLAY_FRAMES` to that absolute directory to feed those actual
native-rendered frames through the VLC composition test instead of WPF frames.
Set `SVS_TEST_OVERLAY_ARTIFACTS` to save full-size screenshots.

The C# tests cover source-canvas bounds, stale events, source resolution changes,
and real VLC composition through eight window sizes/transitions on a 1080p
desktop. The latter requires identical bitmap bytes and an unchanged saved
panel-size file through window resizing, letterboxing and fullscreen.
`SVS_TEST_OVERLAY_DIRECTORY` selects an independent plugin/controller build.
`SVS_TEST_OVERLAY_BASELINE=1` captures the old plugin for before/after comparison.

The TLS handshake processes `SECBUFFER_EXTRA` before receiving more TCP bytes,
as required by [Schannel's extra-buffer contract](https://learn.microsoft.com/en-us/windows/win32/secauthn/extra-buffers-returned-by-schannel).
Kick's client validates the complete HTTP upgrade and its accept key, assembles
text fragments before parsing JSON, and handles interleaved control frames
according to [RFC 6455](https://www.rfc-editor.org/rfc/rfc6455).
Message bounds apply to the assembled message. Invalid or truncated input
reconnects without delivering a partial chat message. IRC also reconnects on
line overflow, so an oversized line's suffix cannot be parsed as another command.

## Windowed text clarity (2026-09-26)

The controller previously rejected type 8 in `overlay_event_is_valid`, before
its scale handler ran. Directly setting the scale in a renderer test hid this
failure. The validator now accepts the event, and the test sends both source-size
and scale messages through the production event thread.

The shared drawing surface uses premultiplied BGRA; the wire protocol and VLC
blender expect straight RGBA. `dib_to_rgba` now removes premultiplication once.
Manually painted input rectangles use premultiplied colors too, so their opacity
and emote edges remain correct. GDI fallback fonts use grayscale antialiasing:
LCD subpixel colors cannot be preserved through video scaling. GDI drawing is
flushed before reading its DIB pixels.

## Filtered windowed scaling

The bundled DLL now includes `studio_gdi`, VLC 3.0.23's Windows GDI output with
filtered chat composition. Stock GDI discards source rows/columns with
`COLORONCOLOR` after composing the overlay, which erases thin glyph strokes and
closes gaps. The custom output receives chat separately, filters its
premultiplied pixels at display resolution with VLC's swscale converter, and
composites it over the video in RGB. The video keeps its existing fast path.
Font size, wrapping, panel dimensions and hit testing stay in their existing
decoded-video coordinates.

See [source attribution and adaptations](quality-gdi/README.md). The build also
uses the matching `g++.exe` for VLC's sensor handler, links compiler support
statically, and bundles these sources and their LGPL license with the app.

The real VLC resize test now checks 112 alternating row/column samples at 50%
scale, in addition to text visibility and stable layout. Stock GDI fails at
0/112 preserved samples; filtered GDI passes at 112/112 for both native/live
and WPF/replay frames. Evidence is in `artifacts/overlay-filtered-scaling`.
