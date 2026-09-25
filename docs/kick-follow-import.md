# Kick follow detection

Settings > General > Followed channels > Detect Kick follows opens Kick in an isolated WebView2 profile. Sign in on Kick, then select Import follows. The browser closes after all pages have been read successfully; settings are saved and the live cards refresh immediately. Both live and offline follows are imported. Existing manual entries remain separate. Re-run detection to pick up follows and unfollows. The existing one-minute polling checks the saved channels' live status, not changes to the website follow list.

Kick API credentials are still needed for the existing public-API live-status checks. Website sign-in and Kick OAuth are separate sessions. Browser session data lives in `%LOCALAPPDATA%\StreamStudio\KickFollowsWebView2`; Clear Kick sign-in clears that profile. No website access token is copied into settings, returned through the browser message bridge, or logged.

## Verified website contract

Checked on 2026-09-25 against primary sources, rather than third-party endpoint lists:

- [Kick public Swagger](https://api.kick.com/swagger/doc.json) exposes channels and livestreams but no authenticated user's followed-channel list.
- [Kick website](https://kick.com/) loaded [channel queries](https://assets.kick.com/main/_next/static/chunks/3f4a4e01ll_c0.js). `followedChannelsForSidebar` calls `/api/v2/channels/followed` with a `cursor` query parameter. Its infinite query reads `nextCursor` until it is absent or falsey.
- The website's [sidebar](https://assets.kick.com/main/_next/static/chunks/2hjoqzpwzl__6.js) flattens `pages[].channels`, deduplicates by `channel_slug`, and renders both `is_live` and offline entries. Detection uses this list, not recommendations, search results, or a guessed public endpoint.
- The site's [fetch implementation](https://assets.kick.com/main/_next/static/chunks/1zjh0c747t7_m.js) uses the `session_token` cookie for the Bearer header, `credentials: include`, and `x-app-platform: web`. The [cookie registry and environment](https://assets.kick.com/main/_next/static/chunks/24vax2xd1yhg9.js) define `AUTH_TOKEN` as `session_token` and the base URL as `https://kick.com`.

The asset filenames are deployment-specific references to the inspected source, not runtime dependencies. The app executes a bounded GET inside the signed-in Kick origin and receives only the response body/status. A per-import session fingerprint prevents pages from different browser sessions being combined. Each response is limited to 2 MiB, with 1,000-page and 50,000-channel limits, repeated-cursor rejection, strict channel validation, and cancellation. A partial list is never saved.

## Validation

`KickFollowImportTestCatalog` covers pagination (including escaped cursors), offline follows, deduplication, malformed and looping pages, limits, cancellation, settings round trips, legacy settings, batching the union with manual channels, empty lists, successful/repeated imports, failed saves, cancellation, and shutdown. A real WebView2 test intercepts all network requests and checks browser cookies/headers, pagination, HTTP errors, changed accounts, and missing sessions. It uses synthetic credentials in a unique temporary profile and sends nothing to Kick.

Authenticated end-to-end validation against a real Kick account requires signing in through the detection window. The automated fixtures validate the implementation, not the current response for a particular user's account.
