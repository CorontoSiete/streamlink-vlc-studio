# Web UI Twitch dark theme

The browser client uses Twitch-style neutral charcoal surfaces, purple actions, light text, and red LIVE badges. The theme is applied in `src/StreamlinkVlcStudio.Infrastructure/LocalWeb/Assets/app.css`, which the local web server embeds and serves to browsers.

The reference is the user-supplied Twitch screenshot. Pixel sampling established these values:

| Element | Sampled color |
| --- | --- |
| Page | `#0E0E10` |
| About panel | `#18181B` |
| Secondary action | `#2F2F35` |
| Subscribe action | `#9146FF` |
| Tag | `#29292E` |

The desktop application retains its original theme, fonts, control shapes, LIVE badge styling, and theme names. The desktop changes from the initial implementation were reverted using saved pre-change files. All four restored WPF files match those originals byte for byte; earlier unrelated desktop changes remain intact. The Dark theme is again named **Dark Grey & Green**.

Browser colors are centralized in CSS variables across navigation, pairing, search, cards, preferences, dialogs, playback menus, and chat. Channel actions retain pill corners. Provider artwork and semantic connection/open-stream statuses retain their own colors. This is a theme change; channel content comes from the existing provider integration.

## Validation

Verified on 2026-10-30:

- Release solution build: zero warnings and errors.
- Existing desktop design checks: 2 passed, zero skipped. These render Following, Browse, and Settings across all eight palettes and wide/compact sizes, and verify text contrast in Dark and Light.
- Existing JavaScript chat layout, volume, and overlay geometry checks: 19 passed, zero skipped.
- Existing Chromium navigation/playback regression: passed at 11 watch widths from 320 to 1920 pixels, with zero JavaScript errors.
- Existing Chromium search regression: passed at nine widths from 320 to 1920 pixels, with zero JavaScript errors.
- A one-off Chromium render probe confirmed computed page, panel, search/button, follow, and subscribe colors against the sampled values, and checked phone overflow.
- Browser screenshots and the restored desktop Settings screen were visually reviewed. The browser uses deterministic local provider/media fixtures; these checks do not establish live provider behavior.
- Self-contained Windows x64 publish succeeded at `.artifacts/twitch-theme/app/StreamStudio.exe`.

Browser screenshots and computed-color measurements are in `.artifacts/twitch-theme/browser`. The original files are preserved in `.artifacts/twitch-theme/before`. The desktop restoration audit and new desktop renders are in `.artifacts/twitch-theme/desktop-scope-correction`. Initial `.artifacts/twitch-theme/desktop` captures show the superseded desktop change. Build and test logs are collected in `.artifacts/twitch-theme/validation`.
