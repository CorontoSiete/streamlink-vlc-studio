# Working on this source

Extract the ZIP and open `StreamlinkVlcStudio.sln` inside the
`streamlink-vlc-studio` folder. This package includes the current source
changes, tests, assets, build scripts, dependency manifests,
and required bundled VLC overlay binaries.

## Build and run

Use 64-bit Windows 10 or Windows 11. Install **.NET SDK 10.0.302 (x64)**,
the exact version selected by `global.json`. A .NET runtime alone is not
enough. Internet access is needed for the first NuGet restore.

Open PowerShell in the folder containing the solution:

```powershell
.\scripts\dev.ps1 Run -Configuration Debug
```

This restores locked dependencies, builds the app, and launches it. The command
finds the pinned SDK even when an older SDK is first on PATH. A custom SDK path
can be supplied with `-DotNetPath 'C:\SDKs\dotnet\dotnet.exe'`. Use
`.\scripts\dev.ps1 Build` to build the entire solution without launching.

For stream playback, also install Streamlink and 64-bit VLC with `libvlc.dll`.
See `README.md` for account setup and installer builds.
Account credentials are configured separately on your own computer.

## Tests

Run the headless-safe suite with one command:

```powershell
.\scripts\dev.ps1 Test

# Focus on one subsystem; omit -NoBuild after changing source.
.\scripts\dev.ps1 Test -Filter 'stream open workflow:' -NoBuild

# Verify formatting, scripts, native dependencies, build, and the full suite.
.\scripts\dev.ps1 Check
```

Tests restore and build automatically unless `-NoBuild` is supplied. Use
`-Interactive` to include desktop tests that open windows and send input.
See [README.md](README.md#test) for skip limits, SDK selection, and test fixtures.

## Why this package is small

Downloaded SDKs, NuGet/tool caches, temporary files, logs, previous installers,
and `bin`/`obj` output are omitted. Restoring and building regenerates the
needed outputs, so the working folder will grow again during development.

Keep `src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledOverlay\build`: its
DLL and EXE are required build inputs, pinned by `dependencies\native-overlay.json`.
Their source and rebuild instructions are in `native/chat-overlay/README.md`.

Git history and machine-specific Git configuration are omitted from the ZIP.
You can use `git init` in the extracted folder to start a new repository.
