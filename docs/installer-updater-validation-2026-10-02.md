# Installer and updater continuation validation

The continuation audit strengthened same-version repair and verified the rebuilt application against the reviewed runtime payloads. This report supersedes the local installer and test counts in the [initial dependency report](installer-updater-dependencies-2026-10-02.md).

Running an already registered Setup package now plans repair, including passive launches from older update helpers. The three external dependency packages have explicit repair commands. Compiled repair conditions retain working versions newer than the bundled installers. The application MSI remains part of the full offline bundle. These changes are covered by bootstrapper regression tests and by inspection of the final compiled Burn manifest. WiX documents the role of [ExePackage repair arguments and conditions](https://docs.firegiant.com/wix/schema/wxs/exepackage/).

Streamlink verification now imports the Twitch and Kick providers using offline URL checks with configuration disabled and a shared timeout. VLC detection requires loadable x64 libraries and the module ABI export in all 13 required plugin files. Application verification also initializes the actual playback core, embedded chat controller, overlay, and replay plugins, with plugin caching disabled. It checks module names together with their capabilities. The damage test found that VLC's MP4 reader and writer share a name: accepting the writer concealed a missing reader. The corrected verifier rejects that case.

The PowerShell installer rejects a 32-bit host before installation work. Dependency-only repair reads the installed application's dependency manifest, including newer requirements. The application's VLC fallback now respects a custom machine registration while retaining explicit environment selection. Portable installation instructions include WebView2 and the required runtime versions.

The dependency pins remain in [windows-installers.json](../dependencies/windows-installers.json): Streamlink 8.5.0-1, VLC 3.0.23, and WebView2 154.0.4258.53, with WebView2 minimum 152.0.4191.53. The final app and bootstrapper both include .NET and Windows Desktop 10.0.10. The Streamlink payload includes Python and FFmpeg; the extracted FFmpeg executable also passed a local frame-processing check.

| Verification | Result and evidence |
| --- | --- |
| Release solution build, warnings treated as errors | Zero warnings and errors; [build and full suite](../artifacts/installer-updater-audit-2026-10-02/full-tests-capabilities.log) |
| Complete headless-safe .NET suite | 1,586 passed; 274 interactive desktop tests skipped |
| Windows PowerShell 5.1 tooling | [48 checks passed](../artifacts/installer-updater-audit-2026-10-02/tooling-powershell5.log) |
| PowerShell 7 tooling | [51 checks passed](../artifacts/installer-updater-audit-2026-10-02/tooling-powershell7.log), including RSA manifest signing and verification |
| Final packaged app with real runtimes | [All nine runtime tests passed](../artifacts/installer-updater-audit-2026-10-02/real-runtime-tests/results.json) |
| Self-contained ZIP and offline Setup | [Rebuilt successfully](../artifacts/installer-updater-audit-2026-10-02/package-and-installer-final.log); MSI database validation passed |
| Embedded dependency installers | [Exact reviewed lengths, SHA-256 values, and signature policies passed](../artifacts/installer-updater-audit-2026-10-02/embedded-payload-proof.json); compiled repair commands and conditions passed |
| Application native resources | [All five embedded resources](../artifacts/installer-updater-audit-2026-10-02/embedded-native-resource-proof.json) match the verified source binaries; [native provenance check passed](../artifacts/installer-updater-audit-2026-10-02/native-source-verification.log) |
| ZIP/MSI delivery comparison | [All 78 ZIP files and 64 MSI files match](../artifacts/installer-updater-audit-2026-10-02/delivery-verification.json), including current instructions and dependency verification metadata |
| Real Setup detection on this PC | [All three shared runtimes detected as Present](../artifacts/installer-updater-audit-2026-10-02/installer-detection-result.log); the smoke check closed Setup after detection |
| Native imports applicable to this x64 PC | [661 x64/x86 native images](../artifacts/installer-updater-audit-2026-10-02/native-dependencies.json); zero unresolved non-system imports |
| Imported Windows DLLs | [98 architecture-matched paths](../artifacts/installer-updater-audit-2026-10-02/native-system-library-proof.json) have valid Microsoft signatures and metadata |
| Formatting, PowerShell syntax, and whitespace | Full formatting verification passed after the final C# change; Windows PowerShell 5.1 parsing and `git diff --check` passed |

The [real runtime test script](../scripts/test-runtime-dependencies.ps1) accepted the reviewed payloads, private copies, and recovered copies. It rejected a broken Streamlink provider import, a missing VLC audio plugin, a library without VLC module exports, the wrong MP4 module despite a stale plugin cache, wrong VLC architecture, and an incomplete WebView2 browser folder. Damage was confined to temporary private copies. The app ran with SDK/runtime override variables cleared and a process-scoped selection of the exact extracted WebView2 browser. The health check exercised browser startup and local JavaScript, native drawing and text, and the bundled VLC components.

The architecture inventory accounts for 664 native images in total. Three are signed Microsoft ARM64 COM registration/proxy helpers inside the reviewed WebView2 updater package. Their imports, hashes, metadata, and signatures are [recorded separately](../artifacts/installer-updater-audit-2026-10-02/native-auxiliary-architecture-proof.json); ARM64 execution and ARM64 Windows DLL resolution were not tested on this x64 workstation. The x64/x86 import audit checks the matching System32/SysWOW64 binary architecture and rejects required Visual C++ imports that are supplied only by a global installation.

The current local [offline Setup](../artifacts/installer-updater-audit-2026-10-02/setup/StreamlinkVlcStudio-Setup.exe) and [ZIP](../artifacts/installer-updater-audit-2026-10-02/package/StreamlinkVlcStudio-release.zip) retain version 1.8.3. Their hashes are in [SHA256SUMS-local.txt](../artifacts/installer-updater-audit-2026-10-02/SHA256SUMS-local.txt). The local Setup is not Authenticode-signed, and no release was published.

A fresh elevated installation, real published upgrade, and reboot/resume cycle still require a disposable Windows environment. The runner-only [published upgrade smoke script](../scripts/test-published-upgrade.ps1) now includes a real same-version passive repair that deletes an app-private plugin and requires Setup to restore its exact bytes. That scenario was added but was not executed on this workstation; the repair-plan regression tests and compiled package checks passed here.
