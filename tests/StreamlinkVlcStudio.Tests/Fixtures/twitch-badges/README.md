# Twitch badge evidence, 2026-10-22

These are public Twitch responses captured during the Alinity live-chat badge investigation.
No OAuth tokens, account settings, or chat message contents are included.

- `alinity-helix-2026-10-22.json`: authenticated Get Channel Chat Badges response for broadcaster `38718052`; 30 subscriber versions across three tiers.
- `*-public-2026-10-22.json`: anonymous Twitch GraphQL `user { id broadcastBadges { setID version title imageURL(size: DOUBLE) } }` responses. Alinity has 30 entries, Pokimane (`44445592`) has 58, and shroud (`37402112`) has 27. The latter two also exercise channel bits badges.
- `alinity-0.png` and `alinity-84.png`: actual 36-pixel CDN images from the Helix response, covering Alinity's base subscriber image and seven-year image. Deterministic browser tests reuse these two representative files; `--live-cdn` instead downloads the actual URL of every Alinity version.

The C# tests compare every captured version's exact image URL and title, including the tier-two and tier-three IDs. They also verify isolation between rooms, missing credentials, rejected Helix requests, malformed responses, backoff, global bundled overrides, and quiet-chat recovery.

Twitch's documented channel/global badge lookup model: https://dev.twitch.tv/docs/chat/send-receive-messages/
The public GraphQL fallback follows the app's existing public Twitch web API usage; it does not require or forward an account token.
