# Window Sharing During Playback

**September 26 update:** the bundled Studio GDI compositor now receives chat
separately from the decoded video. It supports automatic GPU decoding while
retaining GDI window presentation. The software-decoding repair below remains
the fallback policy for custom/older overlay plugins or VLC without swscale.
See [the playback resource measurements](stream-playback-resources-2026-09-26.md).

## Reproduced Cause

The app selected GDI and logged that choice, but its real VLC child window reported
`VLC (Direct3D11 output)`. This reproduced with both ordinary playback and the native
chat overlay using the installed VLC 3.0.23. The new integration assertions failed
before the fix and passed after it.

Discord's local capture log for the reported session showed a 1320x820 WPF D3D9
target on Home, then DXGI shared-texture capture immediately after video started.
Window styles alone cannot prevent this renderer-hook capture from choosing the
video's separate presentation target.

VLC 3's `libvlc_media_player_set_hwnd` resets the player's `vout` variable to an empty
string. That masks the instance's `--vout` setting. Media-level options do not restore
it: VLC creates the video output under the media player, outside the media input's
variable inheritance chain.

Primary references:

- [VLC 3 HWND binding](https://github.com/videolan/vlc/blob/3.0.x/lib/media_player.c)
- [VLC 3 video-output ownership](https://github.com/videolan/vlc/blob/3.0.x/src/input/resource.c)
- [VLC 3 checked variable API](https://github.com/videolan/vlc/blob/3.0.x/include/vlc_variables.h)
- [Discord's capture methods](https://support.discord.com/hc/en-us/articles/9410427556375--Windows-Capturing-Application-Window-for-Screen-Share-and-Go-Live)

## Implementation

`LibVlcVideoOutputBinding` restores `vout` immediately after every HWND binding,
including video-host reconstruction. It calls VLC 3's exported `var_SetChecked`
plugin ABI, which is the function behind VLC's own `var_SetString`. The adapter
checks the loaded major version before touching that ABI and reports unsupported
or unidentified versions as playback errors. It does not assume compatibility with
VLC 4. VLC 3's eight-byte `vlc_value_t` union and player/object layout are required.

Automatic output now selects GDI even with docked chat. Explicit Direct3D11 remains
available, with the existing GDI fallback. Automatic hardware decoding remains
enabled without native chat; GDI uses CPU presentation instead of a separate GPU presentation target.
Graphics-hook sharing of explicit Direct3D11 can still capture only the video.

## Native Chat Composition Repair

The original renderer test checked the child-window title but did not check whether
native chat pixels reached that window. A real plugin frame reproduced an invisible
overlay with VLC 3.0.23: the pipe received the frame, but VLC logged
`no matching alpha blending routine (chroma: RGBA -> DX11)`.

VLC's GDI path blends subpictures before converting hardware decoder surfaces to
RGB. Its software blender cannot modify those surfaces. Overlay playback now uses
software decoding; ordinary playback retains automatic hardware decoding. This
preserves whole-window composition at the cost of additional CPU work in overlay mode.

Setting only `--avcodec-hw=none` on the instance does not fix the problem:
`libvlc_media_player_set_hwnd` resets the player's `avcodec-hw` as well as `vout`.
The same checked-string setter now restores both settings after every HWND binding,
including player recreation and reattachment. The compatibility policy is shared
between instance options, player binding, and the startup log.

Primary references:

- [VLC 3 HWND binding and decoder reset](https://github.com/videolan/vlc/blob/3.0.x/lib/media_player.c)
- [VLC 3 early subpicture blending](https://github.com/videolan/vlc/blob/3.0.x/src/video_output/video_output.c)

## Verification

`ApplicationTestCatalog.WindowSharingVlc.cs` checks actual native output titles,
including explicit Direct3D11 and native-overlay playback. The native-overlay test
also sends a local green RGBA frame through the real plugin and verifies visible
pixels over the video on initial playback, detachment, and reattachment. It also starts one
Windows Graphics Capture session on the main window at Home and keeps that same
session through playback, Home during playback, return to playback, resize, and
detach/reattach. Pixel checks require title text, toolbar controls, and nonblank
video in an image matching the whole app's dimensions.

The verified frames remain 1100x760 through navigation and 850x600 after resizing.
Set `SVS_TEST_VLC_DIRECTORY`, `SVS_TEST_VLC_MEDIA`, and `SVS_TEST_ARTIFACT_DIR` as
described in the README, then run with `SVS_TEST_FILTER='window sharing'`. These
desktop tests must run on the interactive desktop with Windows Graphics Capture
access. They do not start or transmit a Discord screen share.

The overlay repair passed all five window-sharing checks with the real VLC 3.0.23
runtime, including visible native chat frames before and after output rebinds.
The full automated run passed 693 tests and skipped 207 desktop-dependent tests;
the Release solution build and self-contained application publish completed without
warnings or errors. The local fixed executable is in `artifacts/overlay-chat-fix`.

The September 24 verification run selected 909 tests: 900 passed, seven failed,
and two could not acquire foreground activation. Rerunning those nine checks in
fresh processes passed eight. The remaining explicit-Direct3D11 Back-hotkey test
failed at its overlay hit-test assertion (line 116) on both the changed build and
the earlier unmodified `repo-sync` Release build. The corresponding real GDI Back
test passed. The Release single-file publish completed without warnings or errors.
