# Twitch & Kick player Capture Extension

This extension intercepts Twitch and Kick stream-link clicks before the browser navigates. The browser stays on the current page, and the clicked channel URL is sent to the desktop app at `http://127.0.0.1:39179/capture`. The page shows a small Twitch & Kick player status message after each intercepted click, including a retry message when the desktop app is not running.

On Twitch pages, it also auto-claims visible channel-point bonus buttons. The content script looks for enabled, visible button-like controls whose accessible label is `Claim Bonus`, then clicks them when they appear. It uses a mutation observer plus a 15-second fallback scan so React page updates and background tabs are both covered without constant polling.

This does not mint points outside Twitch. You must be logged in, the Twitch stream page must be open, and Twitch must show a claimable bonus button.

## Install In Chromium Browsers

1. Open `chrome://extensions`, `edge://extensions`, or `brave://extensions`.
2. Enable developer mode.
3. Click `Load unpacked`.
4. Select this `browser-extension` folder.

Keep Twitch & Kick player running while using the extension. If it is closed, the click is still intercepted and the browser stays on the current page.

## Test

From the repo root:

```powershell
node --test browser-extension\tests\content-core.test.js
```
