# Web UI keyboard shortcuts

Preferences → Keyboard shortcuts configures these actions for the signed-in web account:

| Action | Default |
| --- | --- |
| Go back to the previous page | Escape |
| Next stream tab | ] |
| Previous stream tab | [ |
| Toggle Multi-stream grid | G |
| Toggle always keep audio on for the selected stream | A |
| Mute / unmute the selected stream | M |

Focus a shortcut field and press the desired key, combination, or mouse side button (Mouse4/Mouse5). Arrow keys can be assigned to Next/Previous stream tab, including without modifiers. Shortcut changes apply immediately and save to the web account. Clear disables that action immediately; Restore defaults applies and saves all six defaults. Duplicate bindings and reserved search/playback keys are rejected. Tab continues moving between controls. Browser/system shortcuts may be intercepted outside the page. A failed account save is reported in the browser.

The 2026-10-24 persistence fix removed a separate shortcut draft. That historical regression records Mouse4 and Left/Right, leaves Preferences without submitting the form, exercises Back and both stream directions, then checks the bindings after reload. Current shortcut edits use the same account save path as the other web preferences.

Back retraces application navigation: Following → Downloads → Back returns to Following. Repeated Back continues through earlier pages without creating a loop. Category context, video channel, provider and library filters are restored. Entering a player records its library page; leaving a player for another page can be undone without closing its streams. Stream selection itself is controlled separately by Next/Previous, which wrap in the current stream order and transfer keyboard focus. Navigation history is bounded to 100 entries and cleared on disconnect/reconnect or page reload. In fullscreen, Back first exits fullscreen.

Grid uses the existing Multi-stream action and keeps every player's menu switch synchronized, including fullscreen layout changes. Always keep audio on uses the selected stream's existing checkbox. Manual mute takes precedence over that setting. Muting does not change the saved volume, and the mute shortcut works on the selected stream even when another page is visible. Audio and grid behavior remain governed by the existing inactive-stream playback preferences.

Shortcuts do not run while typing, using a select/range control, composing text or using a modal dialog. Holding a key does not repeatedly toggle an action. Chat move/resize handles retain their keyboard navigation and Escape cancellation. Video Space/K playback and F fullscreen shortcuts continue working; video M is handled by the configurable selected-stream binding, so changing or disabling it also disables the old key on a focused video.

Checkboxes and switches allow navigation shortcuts while focused. A Brave regression was reproduced with Mouse4 bound to Back and Left/Right bound to stream selection: clicking Live only or Multi-stream left its checkbox focused, and the generic input guard silently blocked the shortcuts. The guard now distinguishes those controls from text fields, sliders, radio groups and selects. Trusted pointer/key regressions retain the checkbox's actual focus, check Back and both stream directions on library/player views, and verify that Space still toggles the switch. The earlier fixture moved focus to the library between actions and missed this case.

Mouse side buttons support the same modifiers, conflict checks, clear/reset and saved preferences as keyboard shortcuts. Recording works while the shortcut field has focus, even if the pointer moves elsewhere. A bound side button runs once per press and suppresses browser Back/Forward through release; it also suppresses browser history when a text field or dialog pauses that shortcut. Unassigned side buttons retain their browser behavior. A side-button shortcut does not select the stream under the pointer. Go back exits fullscreen first, just as it does from the keyboard.

Regression commands:

```powershell
node scripts/tests/web-navigation.browser.cjs --hotkeys-only
node scripts/tests/web-navigation.browser.cjs
node scripts/tests/web-navigation.browser.cjs --search-only
node --test scripts/tests/web-volume.tests.cjs scripts/tests/web-chat-layout.tests.cjs scripts/tests/web-overlay-geometry.tests.cjs
```

To run the browser regressions in Brave, set `SVS_WEB_TEST_BROWSER` to its installed executable before running the commands, for example:

```powershell
$env:SVS_WEB_TEST_BROWSER = 'C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe'
node scripts/tests/web-navigation.browser.cjs --hotkeys-only
```

The JavaScript is embedded in the desktop app's infrastructure assembly. After rebuilding, run the updated app, restart its LAN server, and reload the browser page to load the fix.

The checkbox-focus fix passed the expanded hotkey fixture in Brave and Chrome, the full navigation and search fixtures in Brave, and the volume/chat-layout/overlay-geometry regressions. The Release solution build completed with zero warnings/errors; all 37 local-web HTTPS/WebView2 tests passed with zero skips. Before the fix, the new trusted-pointer regression failed when Mouse4 followed a click on Live only; a separate Brave diagnostic also reproduced both arrow bindings and Mouse4 being blocked after clicking Multi-stream.

The current hotkey fixture uses production HTML/CSS/JS, isolated Chromium and decoded HLS media. It checks history, category/channel/filter restoration, wraparound, reordering, closed streams, focus, audio precedence, fullscreen migration, input/dialog protection, composition/repeats, reserved/duplicate bindings, modifier matching, remapping, account save failure and retry, persistence after reload, and reconnect cleanup. Trusted side-button events cover Mouse4/Mouse5 recording, duplicates, modifiers, persistence, one Back per press, browser-history suppression and pointer selection. Screenshots cover shortcut controls at 320, 390, 768 and 1440 pixels in `.artifacts/web-hotkeys/`.

Verified on 2026-10-23: Release solution build with warnings treated as errors completed with zero warnings/errors. All 37 local-web HTTPS/WebView2 tests passed with zero skips. The hotkey, existing navigation and search Chromium fixtures passed with zero JavaScript errors. Volume, per-stream chat layout and overlay geometry regressions also passed. Shortcut screenshots were visually reviewed at phone and desktop sizes.

The Mouse4/Mouse5 and arrow-key extension was checked with the expanded hotkey Chromium fixture, existing navigation/search fixtures, volume/chat-layout/overlay-geometry regressions and a Release solution build with warnings treated as errors. All passed; the build had zero warnings/errors. Updated help text was visually reviewed at 320px and 1440px.

Verified the immediate-save fix on 2026-10-24: the expanded hotkey fixture passed in Brave and Chrome, including the reported Mouse4/arrow workflow without Save preferences, reloads, failed desktop-settings saves, independent preference drafts, clear/reset and unavailable storage. The original script failed the new regression before the fix. Brave navigation and search fixtures passed with zero JavaScript errors; all 19 volume/chat-layout/overlay-geometry checks passed. The Release solution build completed with zero warnings/errors; all 37 local-web HTTPS/WebView2 tests passed with zero skips. The rebuilt desktop infrastructure assembly contains the exact updated JavaScript and HTML. Shortcut screenshots were reviewed at 320px and 1440px. Evidence is in `.tmp/web-hotkeys-immediate-*.log`, with original JavaScript retained at `.tmp/web-hotkeys-before-immediate-save.js`. The user's live tab was not inspected because the browser automation helper failed and the LAN server stopped during diagnosis.
