# Installer dependencies and update recovery

The [continuation validation](installer-updater-validation-2026-10-02.md) supersedes the local build and test counts below. It tightened same-version repair and VLC verification, and provides the [current validated local Setup](../artifacts/installer-updater-audit-2026-10-02/setup/StreamlinkVlcStudio-Setup.exe). The checks below describe the initial build.

Setup and the PowerShell installer now install and verify all three external Windows runtimes. The updater records an incomplete installation even when Setup has already replaced the application, so the same signed version remains available for repair.

## Reviewed dependency set

| Dependency | Bundled x64 installer | Required minimum | Evidence |
| --- | --- | --- | --- |
| Streamlink | 8.5.0-1, with Python 3.14 and FFmpeg | Streamlink 8.5.0 | Exact installer hash, length, and extracted CLI startup |
| VLC | 3.0.23 | 3.0.23 | VideoLAN signature, exact installer hash, x64 libraries/plugins, native initialization |
| Microsoft Edge WebView2 Runtime | 154.0.4258.53, Evergreen standalone | 152.0.4191.53 | Microsoft signatures, embedded offline manifest, inner package hash, actual browser startup |
| .NET and Windows Desktop | Self-contained application and bootstrapper | Packaged 10.0.10 | Final application and bootstrapper dependency manifests |
| Skia, HarfBuzz, and app VLC plugins | Included in the application payload | Packaged versions | Native import audit, drawing/text startup, embedded-resource hashes |

The WebView2 minimum comes from Microsoft's [release notes for SDK 1.0.4191.47](https://learn.microsoft.com/en-us/microsoft-edge/webview2/release-notes/sdk/1-0-4191-47), the SDK declared by this app. The offline installer follows Microsoft's [Evergreen distribution instructions](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution). Its outer executable reports the Edge Update version; that version does not identify the browser runtime. The signed inner manifest and browser executable establish runtime version 154.0.4258.53.

All installer URLs, byte lengths, SHA-256 values, and signature policies are recorded in [windows-installers.json](../dependencies/windows-installers.json). Streamlink's reviewed installer is unsigned; its exact bytes are pinned to the Streamlink Windows release. VLC and WebView2 require valid signatures from the recorded publishers and signer certificates. Compatible newer runtimes are retained. WebView2 is never downgraded to the bundled version.

The native audit inspected **664 PE images**, including Python `.pyd` modules, the final self-contained runtime, bootstrapper, exact extracted third-party installers, and all five native resources extracted from the final app assembly. It found **zero unresolved non-system imports**. The app's overlay uses the explicitly installed VLC runtime. The 73 imported system-library names were checked against Microsoft file metadata. The reviewed payload carries the required Python and Visual C++ runtime files; it does not require an additional system-wide Python, .NET SDK, or Visual C++ installation.

## Installation and updater behavior

- Detection checks x64 executable headers and real Streamlink output, loadable VLC libraries and essential plugins, and WebView2 registry entries against matching browser files. Corrupt, stale, incomplete, and wrong-architecture candidates do not count as installed.
- Setup embeds the three offline installers before the app MSI. The PowerShell installer verifies downloaded bytes before execution, rechecks the installed runtime, and rejects missing dependencies even when installation was explicitly skipped. An authenticated ZIP supplies its own dependency minima, so an older installation script can install a newer reviewed payload correctly.
- The installed app's bounded maintenance check initializes Streamlink, VLC, Skia, HarfBuzz, and an isolated hidden WebView2 browser. It loads local HTML and executes JavaScript on an STA thread with a message pump, following Microsoft's [WebView2 threading model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model). Failure prevents Setup from reporting success or enabling Launch. Reboot-required outcomes keep Launch disabled.
- Runtime commands have bounded output and timeouts. Windows process jobs own descendants from process creation, including under Windows PowerShell 5.1, so stalled launchers or inherited output pipes cannot keep Setup waiting indefinitely.
- Failed or canceled updates preserve verified installers within the seven-day retention window. Repair notices survive package expiration; damaged or expired packages require a fresh signed check and download. Cached bytes and helper identity are reverified before launch. Older releases cannot be installed as a repair.
- A successful health check clears an incomplete-installation notice. For repair detection, capability metadata in `release-metadata.json` prevents older app versions from receiving an unsupported maintenance command; the MSI retains this metadata. Signed release verification supports legacy two-dependency manifests while newly built releases must declare all three exact minima.

Shared Streamlink, VLC, and WebView2 installations remain installed when the app is removed. The app-only MSI remains an internal payload; use the full Setup executable for installation and repair.

## Validation performed

| Check | Result |
| --- | --- |
| Release solution build with warnings treated as errors | Passed; zero warnings and errors |
| Final complete headless-safe .NET suite | 1,582 passed; 274 interactive desktop tests skipped |
| PowerShell tooling suite, Windows PowerShell 5.1 | 45 checks passed |
| PowerShell tooling suite, PowerShell 7 | 48 checks passed, including real RSA manifest signing and verification |
| Dependency and updater regression coverage | Wrong architecture, missing/corrupt runtime, invalid minima, rejected downloads, timeout/descendant cleanup, quoting, failed same-version repair, expired/corrupt caches, capability metadata, and health-check failures |
| Final self-contained ZIP and full offline Setup | Built successfully; MSI database validation passed |
| Burn payload extraction | All three embedded installers matched the reviewed SHA-256 values and lengths; embedded MSI matched the built MSI |
| Real Setup detection on this PC | Streamlink 8.5.0, VLC 3.0.23, and WebView2 154.0.4258.48 detected as Present; no installation applied |
| App using exact extracted installers | Streamlink 8.5.0, VLC 3.0.23, WebView2 154.0.4258.53, Skia, and HarfBuzz initialized successfully |
| Installer window at minimum supported size | Rendered and inspected; all component rows and the Install action fit |
| Native dependency and source provenance checks | 664 images; zero missing imports; embedded VLC core matched its pinned SHA-256 |
| Changed C# formatting, PowerShell parsing, and whitespace | Passed |

The exact extracted WebView2 browser was selected only for the isolated verification process using Microsoft's documented [browser executable folder override](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2environment.createasync). No production runtime, settings, or account data was replaced by these checks.

Initial evidence is in [the validation artifact directory](../artifacts/installer-dependencies-2026-10-02/), including `full-tests-final.log`, `tooling-powershell5-tests.log`, `tooling-powershell7-tests.log`, `installer-detection-smoke.log`, `all-exact-runtime-startup-check.log`, `embedded-payload-proof.json`, `webview-signed-inner-proof.json`, and `native-dependencies.json`. The initial [offline Setup](../artifacts/installer-dependencies-2026-10-02/setup/StreamlinkVlcStudio-Setup.exe) uses the existing 1.8.3 identity and is a local build; use the continuation build linked above for the audited fixes.

After the final documentation was packaged, the delivery artifact was extracted again. Its application, uninstaller, and all 402 bootstrapper binaries matched the previously verified bytes. Embedded installer hashes and lengths, and the embedded MSI, were checked again. See `delivery-verification.json` and `SHA256SUMS-local.txt` in the artifact directory.

## Remaining environment validation

A fresh elevated install, real published upgrade, and reboot/resume cycle were not run on this workstation. The repository's disposable Windows runner [upgrade smoke script](../scripts/test-published-upgrade.ps1) now verifies real dependency initialization after upgrade and repair. That runner check still needs execution with signed release artifacts. No release was published, and the local app installer was not Authenticode-signed.
