# Per-stream web chat layout persistence

Completed overlay moves and resizes save width, height and relative position
in the signed-in web account's preferences, keyed by platform and channel.
Opening a player returns that user's saved `chatLayout`. A different channel
or web account starts with the responsive default. Listed archives, replay and
downloaded videos retain their known channel identity.

The authenticated `chat/layout` command accepts only a player belonging to that
browser session. It validates finite dimensions and normalized coordinates before
updating the account file, `%APPDATA%\StreamStudio\web-users.json`, through an
atomic, flushed-to-disk writer. The file is outside the installed application
directory, so installing a new version preserves it.

Canceled gestures never submit a save. Home on the resize handle saves null
dimensions to restore responsive sizing; Home on the move handle saves top-right
alignment. Viewport, grid and fullscreen clamping do not overwrite preferred
dimensions. The command queue orders writes before player close/reopen, dispatched
saves use fetch keepalive, and save errors are visible with retry instructions.

Chat appearance preferences also belong to the signed-in web account.

The validation record below describes the original 2026-10-22 implementation. The
current account-scoped implementation is covered by `local web:` tests.

Historical validation:

- `scripts/tests/web-chat-layout.tests.cjs` runs the full production JavaScript
  through deterministic DOM/media/server adapters: seven persistence, identity,
  reset, viewport, ordering and failure regressions.
- The nine production geometry/gesture regressions and eleven volume regressions
  pass, including cancellation without committing geometry.
- The C# workspace regression saves with `JsonSettingsService`, discards settings
  and workspace instances, reloads from disk, and verifies independent channels,
  platforms, listed VODs, defaults, malformed input and session isolation.
- The installer lifecycle suite executes the real `Update-AppSettings` function
  against temporary APPDATA and verifies that an upgrade preserves layouts,
  responsive null sizes, unrelated preferences and protected account data.
- All 29 local web tests pass with zero skips, including real HTTPS/WebView2 mouse,
  touch, keyboard, fullscreen/grid and restoration after browser storage clearing.
  Evidence: `.artifacts/web-chat-layout-tests.log` and
  `.artifacts/local-web-design/overlay-grid.png`.
- The Release solution build passes with zero warnings/errors, and self-contained
  Windows x64 publication succeeds. Logs: `.artifacts/web-chat-layout-build.log`
  and `.artifacts/web-chat-layout-publish.log`. The runnable build is
  `.artifacts/web-chat-layout/app/StreamStudio.exe`.

These provider fixtures verify application behavior without changing the user's
installation or account data.
