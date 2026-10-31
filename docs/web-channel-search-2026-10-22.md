# Automatic browser search

The LAN browser masthead searches while typing and presents suggestions in an anchored dropdown. The Search button and channel results-page replacement are removed. The current library, filters, scroll position and playback remain in place until a result is selected.

Channel suggestions use the existing selected-platform search endpoint and registered stream keys. Rows show returned profile pictures with initials as a fallback, safely highlighted channel names, broadcast titles, live/offline status and available viewer counts. Live rows open their player; offline/unavailable rows open channel videos. On Videos, channel selection opens the selected channel’s back catalog. Discover offers category suggestions that open the selected category. Pasted URLs offer an explicit Open action, or Download on the Downloads page. Typing or pasting never starts a stream or download by itself. Enter without a selected channel retains the channel dropdown; Discover and Videos retain their explicit Enter search behavior.

Channel suggestions put live channels first, ordered by viewer count from highest to lowest. Live channels with unavailable counts follow those with known counts, including zero. Equal counts and offline/unavailable channels keep their provider relevance order. This applies to Twitch and Kick channel suggestions, including the Videos page. Keyboard and pointer selection use the same sorted list.

Provider requests wait for a 250 ms pause. New input cancels pending timers and aborts superseded fetches. Generation checks independently reject late successful responses, including after Escape, navigation, a provider change or disconnect. Search uses the existing serialized command queue and bounded busy retries. Empty input makes no search request. IME composition waits until text is committed.

The combobox exposes its expanded state, controlled listbox and active option. Keyboard focus stays in the input while Up/Down navigate and scroll suggestions; Enter activates the selected row. Escape, clicking outside and tabbing out dismiss the dropdown. Search announcements, skeleton loading, empty feedback and inline error/retry feedback are included. Artwork and metadata reuse the existing safe rendering helpers. Motion follows the reduced-motion preference.

The charcoal panel, purple focus/selection, provider accents, quiet keyboard footer and responsive spacing fit the existing masthead. The list scrolls within the viewport and preserves touch targets on phones.

## Reproduce verification

```powershell
node --check src/StreamlinkVlcStudio.Infrastructure/LocalWeb/Assets/app.js
node scripts/tests/web-navigation.browser.cjs --search-only
node scripts/tests/web-navigation.browser.cjs
node scripts/tests/web-navigation.browser.cjs --category-viewers-only
node --test scripts/tests/web-volume.tests.cjs scripts/tests/web-overlay-geometry.tests.cjs scripts/tests/web-chat-layout.tests.cjs
```

The search regression runs production HTML/CSS/JavaScript in isolated Chromium with deterministic provider/media fixtures. It covers trusted typing, debounce, stable library elements, actual decoded video, accessible combobox/options, keyboard and touch selection, cancellation and ignored-abort late responses, platform switching, no matches, errors/retry, hostile metadata, IME, focus dismissal, URLs/downloads, categories, channel videos and disconnect cleanup. Nine widths from 320 to 1920 pixels verify dropdown bounds and 30-result scrolling. Screenshots and viewport metrics are written to `.artifacts/web-search/` and have been visually reviewed at desktop and phone sizes.

The viewer-order regression supplies deliberately unsorted Twitch and Kick responses with live, offline and unavailable channels, equal counts, zero viewers, null counts and omitted counts. It failed against the original application, then passed with the ordering change. It checks keyboard and pointer selection against the sorted server keys and confirms Videos still opens the selected channel's catalog. The sorted desktop dropdown is captured in `.artifacts/web-search/channel-dropdown-ranked.png` and was visually reviewed.

Verified on 2026-10-22: Release solution build succeeded with zero warnings/errors; all 34 `local web:` tests passed with zero skipped, including the real HTTPS and WebView2 workflows and the updated automatic-search empty-state check. The dedicated search, existing player-navigation and category-viewer Chromium regressions passed with zero JavaScript errors. All 11 volume checks, seven chat-layout checks and nine overlay-geometry checks passed. Build and server-test logs are in `.artifacts/web-search/build.log` and `.artifacts/web-search/local-web-tests.log`.

Fixture results establish UI and local playback behavior; they do not establish live provider availability or physical-device compatibility. A Release solution build embeds the changed assets. The installed running desktop app must use that new build before its web server can serve the updated UI.
