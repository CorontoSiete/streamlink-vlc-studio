# Stream Studio interface redesign

The desktop interface now uses dark grey surfaces with green accents, clearer
type, quieter borders, and consistent control states. The light palette uses the
same surface hierarchy with darker secondary text. Alternate palettes retain
their character with more readable secondary text and selected navigation.
The default is labeled **Dark Grey & Green** in Settings; its persisted `Dark` value
is unchanged, so existing dark-theme users receive the new palette automatically.
The main canvas and shell reuse the original neutral `#1C1C1E` and `#232326`
greys. Cards, raised panels, borders, scrollbars, and text follow that neutral
colour ramp; green (`#4ADE80`) highlights actions, focus, and selected controls.
Primary actions use solid green with dark text, while selected navigation uses a
subtle green surface. Focus rings, sliders, search results, toolbars, and scrollbars
resolve their states from the active palette. Media scrims remain dark over video;
platform branding, chat identity colors, and danger/warning indicators retain their meaning.

Home has a library rail for Following, Discover, Past broadcasts, and Recently
watched. The search field accepts the same channel names and URLs as before.
Narrow or short windows replace the rail with an overflow-capable navigation bar
and hide the welcome introduction so search remains reachable. The empty
Following page offers working Discover and Connect accounts actions.

Live cards separate previews from channel identity, with persistent live and
viewer labels, circular avatars, and platform badges. Existing muted hover
previews and alternate stream-opening behavior remain connected. Broadcasts,
categories, search, and history use the shared card and control styling.

Settings have a consistent category rail, page introductions, grouped fields,
and a persistent save action. Chat has a roomier header and composer. Playback
controls, detached windows, and the four-step account setup use the same visual
language. All eight existing themes remain available and update at runtime.

## Implementation

- `Themes/StudioTheme.xaml` owns shared controls, typography, and interaction states.
- `Themes/StudioShell.xaml` owns navigation and page-heading primitives.
- `MainWindow.xaml` composes the shell and existing view-model commands.
- `MainWindow.StudioNavigation.cs` handles the new account shortcut.
- `MainViewModel.SelectHome` closes Settings and returns Home as one navigation
  destination, preserving the selected library page and open stream tabs.

The playback engines, account authorization, persisted settings schema, and
hover-preview controller are unchanged by this redesign.

## Verification

The `studio shell:` test renders empty and populated Home, browsing, broadcasts,
history, every settings category, all eight palettes, compact navigation, and
setup. It also verifies navigation, the account shortcut, selected navigation
colors, and rail/toolbar transitions. Set `SVS_RESPONSIVE_SCREENSHOTS` to retain
the rendered PNGs.

The interactive `responsive home and settings` test checks search, navigation,
and Save accessibility through native window resizing, including tiny and short
windows. Existing player, chat, tab, theme, and hover-preview tests continue to
exercise their production controls.

Validation for the initial layout redesign: a warning-free Release build, 1,208 headless tests
passing, 17 interactive responsive-layout tests passing, and focused theme,
settings, toolbar-state, and physical hover-preview checks. The full headless run
used `-ExpectedMaxSkips 247` because the existing in-progress hover-preview work
adds one desktop-only case beyond the development script's default ceiling of
246; that case also passed separately with `-Interactive`.

Black & Green verification: another warning-free Release build, 1,208 headless
tests passing (247 expected desktop-only skips), and 25 focused interactive
checks passing across responsive layouts, theme switching, picture-in-picture,
VOD indicators, dropdown hover, toolbar toggles, tabs, and selected navigation.
All eight palettes expose the same resource keys. The checked dark-theme text
pairs exceed 4.5:1 contrast, including muted labels and placeholder text; the
filled primary action has 11.39:1 contrast. WPF-rendered Home, settings, setup,
player, and compact-layout screenshots were reviewed under `.tmp/black-green`.
The app was also launched successfully. Windows capture was unavailable in this
session (`SetIsBorderRequired`, `0x80004002`), so visual inspection used the
production WPF render output.

Dark Grey & Green verification: a warning-free Release build and 25 focused UI
checks passed with no skips. These cover the shell and all eight palettes,
responsive layouts, dropdown hover, focus and disabled visuals, toolbar toggles,
tab selection, VOD indicators, and detached-player theme switching. All eight
palettes still provide the same 82 resource keys. Every normal text colour,
including dim and placeholder text, exceeds 4.5:1 against every neutral surface;
the lowest checked ratio is 4.55:1 and the primary action is 9.76:1.
Home, all Settings categories, browsing, broadcasts, history, setup, player,
chat, and compact layouts were visually reviewed from production WPF renders.
Screenshots, contrast measurements, and test logs are retained under
`.tmp/dark-grey-green`. The rebuilt app was launched successfully. Native desktop
capture still fails with `SetIsBorderRequired` (`0x80004002`), so the visual review
used WPF-rendered images.
