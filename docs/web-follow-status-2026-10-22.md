# Web follow status

Opening a live channel now checks its follow state through the same website account service as the desktop Follow control. The button shows `Checking…` while the read is pending, then `Following` or `Follow` for the verified broadcaster and platform. Clicking `Following` retains the existing unfollow confirmation. Unverified sessions and failed reads stay unknown, with an explanatory tooltip and no false `aria-pressed` state.

Each web player owns one background status task. Short authenticated `follow/status` requests poll it, keeping the server command gate available for navigation, playback and chat. Closing the player or disconnecting cancels the check. Confirmed mutations update every matching player and replace pending status results, so late reads cannot restore an older state. VODs keep their existing hidden follow controls and perform no status read.

Validation:

- `node scripts/tests/web-navigation.browser.cjs --follow-only` drives production HTML/CSS/JavaScript in Chromium with deterministic API fixtures. It covers both platforms, delayed reads during navigation, separate channels and duplicate players, confirmed/canceled/unconfirmed mutations, stale library data, reload, failed/unverified reads, closed-player cleanup and VOD exclusion. The regression failed on the original initial button state before the client fix. Screenshot: `.artifacts/player-navigation/following-button.png`.
- The workspace regression verifies both states and unknown results, wrong broadcaster/platform rejection, read-only operation, device isolation, one check per player, background completion, confirmed-state precedence, cancellation and reopening.
- Release solution build: zero warnings/errors. All 28 local web tests and 49 channel follow tests passed with no skips. Full Chromium navigation, all 11 volume checks and nine overlay geometry checks also passed.

Browser fixtures establish application behavior; they do not establish a live account's sign-in or provider availability. The production dependency invokes `IChannelFollowService.GetFollowStatusAsync` on the desktop dispatcher, preserving its existing server verification and no-sign-in-prompt behavior.
