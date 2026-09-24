# Working on this source

Extract the ZIP and open `StreamlinkVlcStudio.sln` inside the
`streamlink-vlc-studio` folder. This package includes the current source
changes, tests, assets, browser extension, build scripts, dependency manifests,
and required bundled VLC overlay binaries.

## Build and run

Use 64-bit Windows 10 or Windows 11. Install **.NET SDK 10.0.302 (x64)**,
the exact version selected by `global.json`. A .NET runtime alone is not
enough. Internet access is needed for the first NuGet restore.

Open PowerShell in the folder containing the solution:

```powershell
dotnet --version
dotnet restore StreamlinkVlcStudio.sln
dotnet build StreamlinkVlcStudio.sln --no-restore
dotnet run --project src\StreamlinkVlcStudio.App.Wpf\StreamlinkVlcStudio.App.Wpf.csproj --no-restore
```

For stream playback, also install Streamlink and 64-bit VLC with `libvlc.dll`.
See `README.md` for account setup, the browser extension, and installer builds.
Account credentials are configured separately on your own computer.

## Tests

After restoring packages:

```powershell
dotnet test StreamlinkVlcStudio.sln --no-restore
```

Browser extension tests additionally require Node.js:

```powershell
node --test browser-extension\tests\content-core.test.js
```

## Why this package is small

Downloaded SDKs, NuGet/tool caches, temporary files, logs, previous installers,
and `bin`/`obj` output are omitted. Restoring and building regenerates the
needed outputs, so the working folder will grow again during development.

Keep `src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledOverlay\build`: its
DLL and EXE are required build inputs, pinned by `dependencies\native-overlay.json`.
They cannot be recreated from this repository's source.

Git history and machine-specific Git configuration are omitted from the ZIP.
You can use `git init` in the extracted folder to start a new repository.