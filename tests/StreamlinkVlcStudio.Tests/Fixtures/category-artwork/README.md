Captured from Twitch's public CDN on 2026-10-22 for category-artwork regressions:

- `just-chatting-2026-10-22.jpg`: `https://static-cdn.jtvnw.net/ttv-boxart/509658-285x380.jpg`, a decoded 285 x 380 positive control.
- `twitch-missing-2026-10-22.jpg`: `https://static-cdn.jtvnw.net/ttv-static/404_boxart-285x380.jpg`, the HTTP 200 destination of both Slots (`498566`) and Games + Demos (`66082`) covers.

The production resolver must reject the missing-art response before sending its JPEG to a browser. Fixtures are test inputs, never replacement production covers.

The following real 285 x 380 JPEG covers were captured on the same date with an incorrect upstream `Content-Type: image/png` header. Browser regressions replay that header with each original payload:

| Fixture prefix | Twitch category ID |
| --- | --- |
| `league-of-legends` | 21779 |
| `world-of-warcraft` | 18122 |
| `counter-strike` | 32399 |
| `dota-2` | 29595 |
| `valorant` | 516575 |
| `irl` | 509672 |

Each filename is `<prefix>-2026-10-22.jpg`; its source is `https://static-cdn.jtvnw.net/ttv-boxart/<id>-285x380.jpg`. Production must recognize the payload's real image format, serve the corresponding content type and preserve the original bytes.
