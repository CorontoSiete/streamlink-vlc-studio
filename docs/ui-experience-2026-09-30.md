# Studio interface and navigation refresh

The studio now uses a charcoal and mint default palette, a vector brand mark, more readable stream cards, consistent controls and context menus, and quieter status presentation. The light palette has stronger status colors. Toolbar and tab accents follow theme changes immediately.

## Everyday navigation

- The visible Back button returns to the previous page or stream, including from Settings.
- Library navigation shows live followed-channel and download counts. Settings shows its saving, saved, or error state beside the navigation.
- Right-click a live card to open it or **Open and keep browsing**. The tooltip exposes the full broadcast title and the existing Ctrl+click shortcut.

## Downloads

The download form supports Enter, presents unsupported-link and queue errors beside the URL, and clears the error when the URL changes. A failed older request cannot overwrite feedback for a newer URL. Completed, retryable, and active downloads have distinct status presentation. An empty library offers a direct route to Past broadcasts.

## Validation

- Complete repository check: `scripts/dev.ps1 Check` passed, including locked dependency restore, formatting, PowerShell tooling contracts, native dependency verification, and all headless-safe tests. Result: 1,450 passed and 263 skipped in headless mode, with no failures or timeouts. The final log is `.artifacts/quick-switch-removal/check.log`.
- Release build: zero warnings and zero errors.
- Focused experience checks: four passed, covering palette changes, compact navigation, background opening, picture-in-picture colors, download error recovery, and duplicate Enter handling.
- Studio UI checks: 17 passed; three tests requiring an interactive desktop were skipped.
- Compiled-resource and settings-compatibility checks passed. Older settings retain existing custom hotkeys and save only supported actions.
- Download settings remains visible beside Settings at 440 pixels in both dark and light layouts. The toolbar allocates space using the controls' measured widths.
- Picture-in-picture title chrome was checked in all eight palettes. The updated main and settings layouts were rendered offscreen, including all eight palettes and compact navigation. The dark, light, compact, and Hotkeys layouts were visually inspected.
- Rendered previews and the complete validation logs are saved locally under `.artifacts/quick-switch-removal/`.

The desktop automation runtime failed to start even after a reset: `windows sandbox failed: helper_unknown_error: setup refresh had errors`. Physical keyboard interaction and placement above a playing native VLC surface could not be verified through that helper. The rendered layouts and command behavior were verified through the WPF test harness.
