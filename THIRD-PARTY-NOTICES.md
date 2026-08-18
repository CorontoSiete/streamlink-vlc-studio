# Third-Party Notices

## Bundled VLC chat overlay binaries

`libmyoverlay_plugin.dll` and `vlc_chat_overlay.exe` are unsigned opaque native
inputs whose original source and reproducible build recipe are not available in
this workspace. They are not represented as reviewed source. Their exact sizes,
SHA-256 values, and signature state are pinned in
`dependencies/native-overlay.json`, verified during build and packaging, and
included in release artifacts as `native-overlay-provenance.json`.

## Resolved NuGet graphics and text stack

The release dependency graph resolves these packages from the upstream
SkiaSharp repository metadata:

- SkiaSharp, SkiaSharp.HarfBuzz, and SkiaSharp.NativeAssets.Win32 3.119.4
- HarfBuzzSharp and HarfBuzzSharp.NativeAssets.Win32 8.3.1.5

Upstream: `https://github.com/mono/SkiaSharp` (package commit
`f568ac94dd768ef9a2f593537cfde2dd0d348ef5`). These packages are MIT licensed.

Copyright (c) 2015-2016 Xamarin, Inc.
Copyright (c) 2017-2018 Microsoft Corporation.

## Windows Community Toolkit notifications

Microsoft.Toolkit.Uwp.Notifications 7.1.3 is resolved from the Windows
Community Toolkit (`https://github.com/CommunityToolkit/WindowsCommunityToolkit`,
package commit `72205c9add7c3fc1ed63bb77e6fc101e39f1ac33`). It is MIT licensed.

Copyright (c) .NET Foundation and Contributors. All rights reserved.

## Self-contained .NET runtime

The Windows x64 release includes the self-contained Microsoft.NETCore.App and
Microsoft.WindowsDesktop.App runtime packs 10.0.10, plus
Microsoft.Windows.SDK.NET.Ref 10.0.19041.57. The resolved runtime-pack versions
are reconstructed from the publish `.deps.json` and project assets when the
SBOM is generated and independently checked during SBOM verification.

The .NET runtime and Windows Desktop components are distributed under the MIT
license and include third-party components covered by the notices maintained
with the .NET distribution (`https://github.com/dotnet/runtime`).

## MIT license for the packages above

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Tabler Icons

The Twitch and Kick platform glyph path data used in `src/StreamlinkVlcStudio.App.Wpf/MainWindow.xaml` is adapted from Tabler Icons.

MIT License

Copyright (c) 2020-2026 Pawel Kuna

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## TwitchDownloader

The Twitch replay chat JSON model and reader in `src/StreamlinkVlcStudio.Infrastructure/Replay/TwitchDownloader` are adapted from TwitchDownloader.

The MIT License

Copyright (c) lay295

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
