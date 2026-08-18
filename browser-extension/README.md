# Twitch & Kick player Capture Extension

On supported Twitch and Kick pages, this extension intercepts an unmodified left-click only when the destination is a direct channel route such as `https://www.twitch.tv/{channel}` or `https://kick.com/{channel}`. The bare, `www`, and mobile platform hosts are accepted and normalized to a canonical channel URL. VOD, clip, directory/category, settings, reserved platform pages, multi-segment URLs, and modified or non-left clicks are not captured and continue through the browser normally.

For a captured channel click, the browser stays on the current page and the canonical URL is sent to the desktop app at `http://127.0.0.1:39179/capture`. The page shows a small Twitch & Kick player status message after each intercepted click, including a retry message when the desktop app is not running.

On direct Twitch channel routes, it also auto-claims visible channel-point bonus buttons. The content script looks for enabled, visible button-like controls whose accessible label is exactly `Claim Bonus`, then clicks them when they appear. Mutation handling scans only the added or changed subtree, with a 15-second fallback scan for background-tab updates; SPA navigation tears the observer down on VOD, directory, settings, and other non-channel routes and restarts it on the next direct channel route.

This does not mint points outside Twitch. You must be logged in, a direct Twitch channel page must be open, and Twitch must show a claimable bonus button.

## Install In Chromium Browsers

1. Open `chrome://extensions`, `edge://extensions`, or `brave://extensions`.
2. Enable developer mode.
3. Click `Load unpacked`.
4. Select this `browser-extension` folder.

Keep Twitch & Kick player running while using the extension. If it is closed, a direct channel click is still intercepted and the browser stays on the current page.

## Test

From the repo root:

```powershell
node --test browser-extension\tests\content-core.test.js
```
