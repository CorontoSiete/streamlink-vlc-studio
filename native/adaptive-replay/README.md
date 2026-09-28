# Live replay HLS module

`libstudio_adaptive_plugin.dll` is VLC 3.0.23's adaptive demuxer with the small
patch in `live-edge.patch`, licensed under LGPL-2.1-or-later. The original
corresponding sources and headers are included in
`vlc-3.0.23-adaptive-source.tar.gz`; `provenance.json` identifies and hashes both
the official upstream archive and this buildable subset. The archive contains
unmodified upstream files, including their copyright notices. The build applies
the readable patch before compiling. Local changes were made on 2026-09-26.

The patch lets VLC consume its current HLS chunk and buffered source data even
when no later segment has been published. Previously, seeking into the newest
segment suspended decoding until the playlist grew. It also refreshes a stale
live playlist before selecting a segment for a seek beyond its known range.
Playlist refresh, bounded buffering, exact seeking and end-of-media remain in
the upstream implementation.

The module has priority **zero** and requires explicit per-media selection.
The application selects it only for validated, unencrypted MPEG-TS `EVENT`
replays on VLC **3.0.23**. This build omits gcrypt and compressed MP4 support;
encrypted, fMP4, master, sliding, discontinuous and other unverified playlists
keep the installed VLC modules. Completed replays retain their existing path.
The original VLC installation is not modified. The module and the replay pause
filter are embedded and extracted together to an immutable SHA-256 directory.

## Rebuild

Use Python 3.12+, Git, x64 MinGW-w64 GCC/G++ targeting **MSVCRT**, and an x64 VLC
3.0.23 `libvlccore.a` import library. The checked build uses WinLibs GCC **16.1.0**,
MinGW-W64 x86_64-msvcrt-posix-seh, Brecht Sanders r2.

```powershell
python scripts/build-adaptive-replay.py --gcc C:/toolchain/bin/gcc.exe `
    --vlc-library-directory C:/vlc-sdk/lib
```

`--output` selects an independent rebuild path. Every invocation uses a fresh
source/object directory under `artifacts/native/adaptive-replay`, verifies the
upstream source hash, applies the patch, and checks native imports before
copying the result. PE timestamps are disabled and the image base is fixed;
two independent source/object builds produced byte-identical DLLs. No download
is needed. Normal .NET builds use the checked-in
DLL and do not require a native toolchain.

Only KERNEL32.dll, msvcrt.dll, WS2_32.dll and libvlccore.dll are imported. GCC's
runtime and standard C++ library are linked statically under the
[GCC Runtime Library Exception](https://gcc.gnu.org/onlinedocs/libstdc++/manual/license.html).
Their notices are `COPYING.GCC` and `COPYING.RUNTIME`; MinGW-w64's notice is
`COPYING.MinGW` and `COPYING.Winpthreads`. VLC's license is `COPYING.LIB`.

The native `live first seek` regressions cover newest-segment output, stale
playlists, cold opens, presented pixels, appended segments after starvation,
pause/resume, audio gates, cancellation, and the skip button's real tab handoff.
See `docs/live-skip-near-edge-2026-09-26.md` for the diagnosis and measurements.
