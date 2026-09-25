# Installed dependency detection

Setup incorrectly displayed **Will be installed** for this PC's existing Streamlink 8.5.0 and VLC 3.0.23 installations. The previous setup log recorded both packages as absent.

Two detection bugs caused this:

- Streamlink's executable path was read with `GetVariableString`, leaving the `[ProgramFiles64Folder]` token unexpanded before `File.Exists`. Setup now uses `FormatString` on the variable reference. The installed application path uses the same correction for maintenance and launch operations. Formatting the reference also lets Burn preserve paths stored as literal strings; see the [WiX variable behavior documentation](https://docs.firegiant.com/wix/whatsnew/faqs/).
- VLC was checked through `HKLM\SOFTWARE\VideoLAN\VLC\Version`, which is absent on this PC despite the runtime being installed. Setup now reads the 64-bit `InstallDir` registry value and uses a subsequent [WiX file-version search](https://docs.firegiant.com/wix/schema/util/filesearch/) on `libvlc.dll`. The default search directory remains `Program Files\VideoLAN\VLC` when no install directory is registered.

The component status now distinguishes **Already installed**, **Will be updated**, and **Will be installed**. The existing minimum-version comparisons and shared-dependency retention behavior remain in effect.

Validation:

- Release build: zero warnings and errors.
- Nine bootstrapper tests passed, including formatted executable paths and installed/older/missing dependency status cases.
- Formatting and analyzer verification passed for the changed C# files.
- Full installer build, MSI database validation, and pinned dependency verification passed.
- The rebuilt setup was opened on the affected PC. Its real Burn detection log reports Streamlink `8.5.0` and VLC `3.0.23.0`, both with package state `Present` and successful version conditions. No installation was applied. See [the detection log](../artifacts/logs/installer-detection-smoke.log).

The UI automation helper could not attach to the setup window, so no screenshot was verified. Its error was `window id 42471800 no longer belongs to {6D809377-6AF0-444B-8957-A3773F02200E}\Google\Play Games\Bootstrapper.exe; current owner is {6D809377-6AF0-444B-8957-A3773F02200E}\Google\Play Games\Bootstrapper.exe`. A fresh window lookup reproduced the error. Setup was left open for review; verification used the real installer log and the status regression tests.

The [rebuilt local installer](../artifacts/installer-updater/setup/StreamStudio-Setup.exe) uses the existing 1.7.0 identity and the existing application ZIP payload. Its [local checksum](../artifacts/installer-updater/SHA256SUMS-local.txt) was refreshed. It is not a published release.
