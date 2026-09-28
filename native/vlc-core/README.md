# VLC 3.0.23 Windows condition-wait fix

This folder contains the reproducible source patch and build configuration for
the bundled `libvlccore.dll` used with one exact official VLC 3.0.23 Windows
distribution. The runtime loads it only when both installed VLC DLLs match the
SHA-256 values in `provenance.json`. Other VLC builds keep their own core.

VLC 3.0.23 looks up `WaitOnAddress` and its wake functions only in
`kernel32.dll`. On the verified Windows build those exports are available from
`kernelbase.dll`, so VLC silently selects a fallback that wakes every thread
sharing one of 32 hashed wait buckets. The included patch tries `kernelbase.dll`
before retaining VLC's original fallback. It leaves the Windows 7 compatibility
target in place and does not import newer APIs directly.

The upstream VLC source is VideoLAN VLC 3.0.23 from the exact archive and hash
in `provenance.json`. The patch changes only `src/win32/thread.c`; that upstream
source is LGPL-2.1-or-later and its `COPYING.LIB` is included.

The Windows DLL statically links `libintl.a`, `libiconv.a`, `libidn.a`,
`libgcrypt.a`, and `libgpg-error.a`, plus GCC's `libgcc.a` and MinGW-w64's
`libwinpthread.a`. The package archives, exact linked-library hashes, selected
library license choices, compiler archive and runtime hashes are pinned in
`provenance.json`. The `libidn` library is selected under LGPL-3.0-or-later;
the combined core therefore uses LGPL-3.0-or-later. The other linked library
components use their LGPL terms. `COPYING.LESSER` contains the selected LGPLv3
text, while `COPYING.GCC`, `COPYING.RUNTIME`, `COPYING.MinGW`, and
`COPYING.Winpthreads` contain the applicable toolchain/runtime notices.

The core source and this build script accompany the binary. To use a modified
core with the exact VLC 3.0.23 pair, rebuild the DLL, update the embedded
resource and expected SHA-256 in `BundledVlcCoreRuntime.cs`, then rebuild the
app. The app uses the installed core for other VLC DLL pairs.

## Build and verify

Python 3.12+, the pinned upstream source archive, x64 WinLibs GCC 16.1.0
(`x86_64-w64-mingw32`, MSVCRT), and the pinned MSYS2 contrib package archives
are required. Their checksums and the hashes of the linked `.a` files are in
`provenance.json`; GNU and GnuPG project pages describe the library licenses
and source releases for [gettext](https://www.gnu.org/software/gettext/),
[libiconv](https://www.gnu.org/software/libiconv/),
[libidn](https://www.gnu.org/software/libidn/),
[libgcrypt](https://gnupg.org/software/libgcrypt/), and
[libgpg-error](https://gnupg.org/related_software/libgpg-error/). The
application build uses the checked-in DLL and does not require a native
compiler.

```powershell
python scripts/build-vlc-core.py `
  --source-archive artifacts/live-skip-first/vlc-3.0.23.tar.gz `
  --gcc .tools/native-review-msvcrt/mingw64/bin/gcc.exe `
  --toolchain-archive .tools/native-review-msvcrt/winlibs-16.1.0-msvcrt-r2.7z `
  --contrib-prefix .tmp/gpu-resource-2026-09-27/core-contrib/prefix/mingw64 `
  --contrib-archive-dir .tmp/gpu-resource-2026-09-27/core-contrib `
  --reference-libvlc 'C:/Program Files/VideoLAN/VLC/libvlc.dll' `
  --reference-core 'C:/Program Files/VideoLAN/VLC/libvlccore.dll' `
  --output src/StreamlinkVlcStudio.Infrastructure/Vlc/BundledVlcCore/libvlccore.dll
```

The build verifies the source and compiler archive hashes, GCC executable and
runtime hashes, all contrib package and linked-library hashes, official VLC DLL
hashes, exported symbol set, PE imports, and a second clean build's
byte-for-byte identity before replacing the output. It disables linker
timestamps. It does not modify the VLC installation.

The real multistream regression confirms the core is loaded by VLC's own
`vlc_cond_*` APIs. On this host the original core consumed 1.50 CPU cores in an
eight-worker idle-wakeup reproducer; the patched core consumed no measurable
CPU over five seconds and produced only the 170 expected wait iterations.
