# Replay pause continuity module

`replay_pause.c` is a small VLC 3.0 `demux_filter`, licensed under LGPL-2.1-or-later
(see `COPYING.LIB`). It attaches only to the adaptive demuxer and only when the
application supplies a per-input readiness event. It intercepts
`DEMUX_SET_PAUSE_STATE`; all other controls and demux calls pass through.

VLC's input loop still pauses the output clock and audio/video decoders. The
adaptive downloader retains its bounded buffer and its segment position instead
of resetting them on unpause. Its existing playlist updates, maximum buffering,
seeking, teardown, and end-of-stream behavior remain active.

The module acknowledges attachment through a named Windows event owned by that
media player. Closing the input clears the event and closes the native handle.
The application checks and unpauses under its native player lock. Missing modules,
incompatible VLC versions, non-adaptive media, and replacement inputs cannot
inherit the acknowledgement; they retain the existing restoration fallback.

The checked-in DLL is embedded into the .NET assembly and extracted into a
directory named by its SHA-256 under the application's local data directory.
It does not modify the installed VLC plugins. Source and license accompany app
builds and publishes under `native/replay-pause`.

## Rebuilding

Use the VLC **3.0.23** public source headers and an import library for its x64
`libvlccore.dll`, and x64 MinGW-w64 GCC targeting **MSVCRT**. The verified compiler
is WinLibs GCC **16.1.0**, MinGW-W64 x86_64-msvcrt-posix-seh, Brecht Sanders r2.
The DLL imports only KERNEL32.dll, msvcrt.dll and libvlccore.dll.

From the repository root:

```powershell
./scripts/build-replay-pause.ps1 -Gcc C:/toolchain/bin/gcc.exe `
    -VlcIncludeDirectory C:/vlc-3.0.23/include `
    -VlcLibraryDirectory C:/vlc-sdk/lib
```

The script disables PE timestamps and fails on compiler warnings. An optional
`-OutputPath` allows an independent rebuild for byte comparison. Ordinary .NET
builds use the checked-in module and do not need GCC or VLC headers.

Upstream interfaces and behavior:

- [VLC 3.0.23 public plugin headers](https://github.com/videolan/vlc/tree/3.0.23/include)
- [Input pause and unpause](https://github.com/videolan/vlc/blob/3.0.23/src/input/input.c)
- [Adaptive pause reset](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/PlaylistManager.cpp)
- [Adaptive segment reset](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/Streams.cpp)

Native regression checks use the installed libVLC and local HTTP EVENT playlists.
Set `SVS_TEST_VLC_DIRECTORY` and run the test executable with
`SVS_TEST_FILTER='pause'`. They verify player continuity, real presented pixels,
long pauses, growing playlists, teardown acknowledgement, and the existing
startup, cancellation, audio, and timestamp restoration checks.
