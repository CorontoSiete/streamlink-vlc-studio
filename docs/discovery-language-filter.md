# Discovery language filtering

The desktop Discover page and local web server UI share language choices, defaulting to **All languages**. Each category remembers its selected language separately for Twitch and Kick, including after restarting. For example, Twitch Just Chatting can remember English while Music remembers French. **All languages** is also a saved choice.

The desktop saves choices automatically in `AppSettings.BrowseCategoryLanguages`, keyed by platform and category ID. Web accounts save their own `preferences.categoryLanguages` in the server's account store, so choices survive browser and server restarts without affecting another account or the desktop. Opening a category restores its saved choice before loading streams. The directory's language selector supplies a default for categories without a saved choice.

Changing language clears the displayed streams and starts the category's first page again. Pagination and Refresh carry the selected language. Responses from an older selection cannot replace or append to the current list. The desktop also cancels the previous stream request. The browser offers **Show all languages** when a filtered category is empty.

The filter applies to live streams inside categories. Category search, category ordering, and category viewer totals still cover all languages; both interfaces explain the scope. Stream language choices are independent of the app's display language.

`BrowseStreamRequest.Language` is optional. Twitch receives `language` alongside `game_id`; Kick receives `language` alongside `category_id`. All languages omits the provider parameter. The local web `settings` response exposes the shared choices in `browseLanguages`, and `streams` validates and forwards the optional `language` field. This uses the existing [Twitch Get Streams endpoint](https://dev.twitch.tv/docs/api/reference/#get-streams) and [Kick livestream endpoint](https://api.kick.com/swagger/doc.json).

Validation:

- `scripts/dev.ps1 Test -Filter 'discovery language:'`: provider request encoding, shared browser choices, request validation, desktop cancellation, stale results, pagination reset, settings-file round trip, server-store reopening, and category/platform/account isolation.
- `node --test scripts/tests/web-discovery-language.tests.cjs`: browser pagination and late-response handling for both platforms.
- `node scripts/tests/web-navigation.browser.cjs --discovery-language-only`: production HTML/CSS/JS with deterministic API responses; language selection, category restoration across browser reloads, empty-state reset, pagination, platform switching, and mobile layout.

The automated provider checks use HTTP fixtures, not live account credentials.
