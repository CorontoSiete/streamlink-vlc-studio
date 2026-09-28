# Library UI refinement

The library now uses quieter card borders, consistent headings and rounded controls,
a simpler search header, and a clear selection bar in navigation. Live cards have
larger avatars, two-line titles, platform badges on their previews, and a play
indicator on keyboard focus or hover when video previews are disabled. Empty
broadcast and history pages explain the next action. The toolbar identifies the
current page, and Settings labels describe the controls more directly.

Search results have distinct keyboard focus and disabled states. Up/Down move
between available results and the search input, scrolling the focused result into
view; Enter uses the result's existing open command. Escape dismisses the popup
without clearing the query, and arrow keys can reopen retained results.

## Reproduced scroll defects

Before the change, navigating from Following scrolled by 460 pixels made Past
broadcasts inherit the same offset. Returning to Following after scrolling
broadcasts replaced Following's 180-pixel position with 420 pixels. Both cases
failed against the starting implementation.

The shared library viewer now records an offset for each page and restores it
before rendering the destination. Collection replacements discard only that
page's position. Appending results and refreshing retained cards preserve the
position. Rapid navigation, Back, Settings round trips, and returning from a
Discover category are covered by regression checks.

## Validation

`.\scripts\dev.ps1 Check` passed formatting, PowerShell and tooling checks, native
dependency verification, and a Release build with zero warnings or errors.
All 1,230 headless tests passed; 252 desktop-only cases were explicitly skipped.
The baseline had 1,226 passing tests and 250 desktop-only cases. The default skip
ceiling in the development script and CI now matches the audited inventory,
including the two new desktop tests.

The existing responsive suite passed all 17 cases, the Discover scroll suite
passed all 28, and the previous UI polish suite passed all seven. The shell render
check passed. Actual-window captures of all eight palettes were also reviewed,
with pixel assertions confirming both live-card rows paint after visiting
Settings and switching themes.

All six new `studio refinement:` cases passed together with zero skips. The
physical-input case clicks the search field and verifies native foreground focus
before sending keys; WPF logical focus alone was shown to leave another native
window owning input. It checks arrow navigation, skipping unavailable results,
scrolling into view, Enter, Escape, and reopening retained results. Seven existing
desktop checks also passed: palette/focus visuals, dropdown hover, detached-window
theme changes, navigation selection, tab states, toolbar toggles, and physical
hover-preview interaction.

The main focused suites can be rerun with:

```powershell
.\scripts\dev.ps1 Test -NoBuild -Interactive -Filter 'studio refinement:'
.\scripts\dev.ps1 Test -NoBuild -Interactive -Filter 'responsive'
.\scripts\dev.ps1 Test -NoBuild -Interactive -Filter 'browse scroll'
.\scripts\dev.ps1 Test -NoBuild -Interactive -Filter 'studio polish:'
```

Tests use fixture services and channels. This verifies local layout, navigation,
input, and rendering; it does not substitute for live platform playback testing.
Local starting-source snapshots, failure evidence, logs, and PNG captures are in
`.tmp/ui-refinement`.
