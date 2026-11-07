# Streamer search click crash — November 4, 2026

Windows Application events identify three crashes of the installed Stream Studio
1.8.10 executable: November 3 at 23:13:20 and November 4 at 04:37:56 and 05:07:41
(America/New_York). All three .NET Runtime events (1026) contain the same exception:

```text
System.InvalidOperationException: 'System.Windows.Documents.Run' is not a Visual or Visual3D.
  System.Windows.Media.Visual.IsAncestorOf(DependencyObject descendant)
  MainWindow.MainWindowPreviewMouseDown(Object sender, MouseButtonEventArgs e)
```

The paired Application Error events (1000) identify
`%LOCALAPPDATA%\Programs\StreamStudio\StreamStudio.exe`, version 1.8.10.0,
running .NET 10.0.10. The installed release metadata identifies commit
`c80cc848e8cacfa983a1f3b57fb933840d4e2a41`. The event records, timestamps and
original source are preserved under
`.artifacts/streamer-search-crash-2026-11-04/`.

The window's preview mouse handler checks whether a click came from inside the
search popup before dismissing it. Its `Visual.IsAncestorOf` call accepted any
`DependencyObject` from `OriginalSource`. Streamer names use `EmojiTextBlock`,
whose ordinary text is a `Run`. A `Run` participates in routed input but is not
a visual. Consequently, clicking its text threw before the stream-open command
could execute. The exception escaped WPF's input dispatch and terminated the
process. The same assumption also affected inline emoji content and outside text
while search was open. The failure does not require a particular game, streamer,
provider response, or video decoder.

The popup containment check now follows content, logical and visual parents,
using the same parent rules as the existing tab input ancestor helper. The
shared `GetInputParent` preserves that helper's behavior. It never passes a
content element to a visual-only API. This fixes containment itself without
catching and suppressing arbitrary exceptions or consuming the click.

Microsoft documents why content elements differ from visuals in
[Trees in WPF](https://learn.microsoft.com/dotnet/desktop/wpf/advanced/trees-in-wpf#trees,-content-elements,-and-content-hosts)
and provides their parent API in
[ContentOperations.GetParent](https://learn.microsoft.com/dotnet/api/system.windows.contentoperations.getparent?view=windowsdesktop-10.0).

## Verification and repair

The three new regressions were first run against the unchanged production
handler. All three reproduced the visual/content exception in the recorded
handler (`baseline-routed-tests.log`). They exercise the actual XAML result
template and route input from its `Run`, nested text and inline image content,
and from text outside the popup. Offscreen tests stop the synthetic event after
the production window handler; native screen-coordinate behavior is covered
separately by the desktop test.

The desktop test targets a character rectangle in the actual result name,
verifies that hit testing and `OriginalSource` identify its `Run`, right-clicks
to open the result menu, dismisses search outside, reopens it, and left-clicks
the text to select playback. The seven search dismissal/desktop tests passed
with zero skips (`physical-tests.stdout.log`). Initial runs from the command
runner could not obtain foreground activation; they are retained as skipped
runs rather than counted as passes. Running on the interactive desktop resolved
that environment issue.

The installed repair is built from an archive of the installed release commit
with only `MainWindow.xaml.cs` changed. Existing unrelated working-tree edits
are excluded. The self-contained application restore and publish pass with
warnings treated as errors. Its built-in dependency check successfully starts
Streamlink, VLC and WebView2 and checks Skia/HarfBuzz rendering
(`candidate-health.stdout.log`). The repair keeps version 1.8.10 for updater
compatibility and identifies itself as
`1.8.10+search-click-fix.20261104` in its informational version.

The complete test-project build also reports an existing NU1903 advisory for
the maintenance project's pinned `Microsoft.DotNet.ILCompiler` 10.0.10. This
warning is retained in the logs and is the only warning exempted from promotion
to an error for that test-project build. The repaired desktop application's own
strict restore/publish passes without that exemption. No dependency versions
were changed for this click-handling repair.

The local installation procedure verifies the candidate hash, base commit and
installation ownership, backs up the executable and ownership records, replaces
the executable atomically, and updates the existing managed-file hashes. It
restores the backup if replacement or ownership verification fails. Settings
and account data are outside these operations. Backup paths and executable
hashes are recorded in `installation-result.json` in the evidence directory.

Final results:

| Check | Result | Evidence file |
| --- | --- | --- |
| Original handler, crash reproduction | 3 failed with the recorded content/visual exception | `baseline-routed-tests.log` |
| Final working-tree input regressions | 3 passed, zero skips | `final-routed-tests.log` |
| Exact repair assemblies, search and native pointer checks | 10 passed, zero skips | `candidate-search-final.stdout.log` |
| Exact repair assemblies, tab input and text focus | 5 passed, zero skips | `candidate-tab-final.stdout.log` |
| Complete headless suite | 2,131 passed, 276 desktop cases skipped, zero failures | `full-headless-tests.log` |
| Installed executable dependency verification | Exit code 0; all five dependency groups ready | `installed-health.stdout.log` |
| Installed version and managed-file hash | Match the validated repair | `installation-result.json` |

The final three input regressions were rerun after making their inline-image
fixture independent of native emoji font availability. Earlier exact-assembly
tests run from the deeply nested build directory encountered emoji rendering
fallback. The same test binaries passed after copying the complete host to the
shorter `candidate-test-host` directory, including the existing tab emoji test.
The file lists and repair assembly hashes match between hosts; the product
source was unchanged. The final five tab input cases ran from that shorter host.
An initial exact-build playback click overlapped the headless window tests;
all ten search checks passed after those tests finished, without changing the
product or test binaries.

The first installation attempt hit PowerShell's conversion of a null backup
path to an empty string in `File.Replace`. Its rollback restored all three
original files byte-for-byte. The corrected installation uses an explicit
replacement backup and verified the existing backup before retrying. Installation
completed November 4, 2026 at 16:28 America/New_York. The installed executable is
107,312,664 bytes and has SHA-256
`2088582fb0dcbab9b7696ccb03acf0780eccf70feabcf4edf7d2ac3ded54f295`.
The original executable, both ownership files, and an additional atomic-replace
backup remain in `installed-backup/` under the evidence directory.
