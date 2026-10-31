# Offline channel cards

Offline followed channels have a compact banner header, OFFLINE badge, avatar, channel name,
and relative last-live indicator. The pin button and video-browsing command remain independent.
Cards use the current theme, including when artwork is absent or fails to download.

The last-live indicator uses an actual broadcast's start time. Its tooltip gives the latest
available broadcast's local date and time. Missing, invalid, sentinel, and future timestamps
show `Last live unavailable`. An empty recording list does not establish that a channel has
never streamed. Kick history can omit broadcasts that were not saved, were deleted, or are
private; the app can also retain a newer start time that it directly observed while live.

## Verified sources

Checked against live platform responses on 2026-10-20:

- Twitch's [Get Users API](https://dev.twitch.tv/docs/api/reference/#get-users) supplies
  `offline_image_url` alongside the avatars already requested by the followed-channel service.
  Banners reuse those batched requests.
- Twitch's web GraphQL endpoint, `https://gql.twitch.tv/gql`, accepts the following query with
  its public web client ID. The query was executed with `shroud`, `lirik`, `monstercat`,
  `sodapoppin`, `chopp`, `genburten`, and `zer0`. This is a website contract, so optional
  failures retain usable channel cards and banners from the official users endpoint.

  ```graphql
  query OfflineFollowedChannels($logins: [String!]!) {
    users(logins: $logins) {
      id
      login
      offlineImageURL
      bannerImageURL
      lastBroadcast { startedAt }
    }
  }
  ```

  Returned identities must match requested logins. This reads broadcast metadata directly
  without requiring an available VOD or treating an upload time as a broadcast start.
  A live request with 100 distinct logins also confirmed the batch size and nullable entries
  for unavailable users.
  `chopp`, `genburten`, and `zer0` return null `offlineImageURL` but valid `bannerImageURL`
  values; those exact profile banner URLs returned HTTP 200. The offline card uses this real
  channel banner when no offline image is configured. Offline images retain priority when
  available. The original query omitted `bannerImageURL`, which left these cards blank even
  though the platform supplied usable artwork.
- Kick's [Get Channels API](https://docs.kick.com/apis/channels) documents `banner_picture`.
  This is an initial fallback, not a reliable offline-artwork source. Live checks found six
  followed channels whose returned banner URL gave HTTP 403 `AccessDenied`, including
  `actuallyangel`, `biancita`, `fqnos`, `lailabugg`, `loustreams`, and `punch`. Adding the
  website referrer did not make those objects available.
- Kick's website channel endpoint, `https://kick.com/api/v2/channels/{slug}`, returns
  `offline_banner_image: { src, srcset }` and optional `banner_image: { url }`. The returned
  slug must match the requested channel. Prefer an advertised responsive offline image:
  the smallest valid width of at least 640 pixels, or the largest available smaller image.
  These WebP URLs returned HTTP 200 and decoded successfully for the affected channels with
  artwork. Several `src` fullsize JPEG URLs also returned 403 while their advertised `srcset`
  images worked, so constructing a URL from `banner_picture` or merely switching to `src`
  would not repair the issue. No image URL is inferred from a filename.
  If responsive artwork is absent, use `src`, then the website's `banner_image.url`.
  A confirmed absence of both website images uses the theme fallback; for example,
  `loustreams` and `westenchamplin` returned no artwork. An unavailable or mismatched website
  response retains previously confirmed artwork, or the official API fallback on first load.
- Kick's offline stream object does not provide reliable history; a zero `start_time` is ignored.
- Kick's [website video endpoint](https://kick.com/api/v2/channels/xqc/videos?limit=1&page=1)
  returned a JSON array with `is_live` and UTC `start_time` values. Unlike `created_at`,
  `start_time` describes the broadcast. The service chooses the newest valid, non-live start
  from the returned history. An empty array was also verified for `sodapoppin`.

## Refresh and resources

### Web server UI

The web Following page and its channel sidebar also display `Last live … ago` for offline
channels. Hovering the label shows the latest available broadcast's full local date and time.
The initial status response includes confirmed cached timestamps. After rendering the list,
the browser requests `following/history`, which uses the same platform history enrichment as
the desktop. It accepts no client channel selection and reads only that device's server-side
offline snapshot. A per-workspace semaphore bounds history work to one active lookup. This
authenticated, CSRF-protected metadata request runs independently of playback/chat commands.

History updates existing labels in place, preserving focus, sorting, platform filters, and
artwork. Page changes, refreshes, and disconnects cancel older lookups; generation checks also
reject already completed responses that arrive late. Failed optional lookups retain the
snapshot's cached times. Missing, malformed, sentinel, and future dates show
`Last live unavailable`. Labels advance once a minute and when the page becomes visible.

Anonymous live checks on 2026-10-23 reconfirmed Twitch's `lastBroadcast.startedAt` response for
`shroud`, `lirik`, and `monstercat`, and Kick's `xqc` history array with separate `start_time`
and `created_at` fields. The UI uses broadcast start times, never recording creation times.

The `local web: offline last-live` regressions exercise the workspace/HTTPS path and the actual
browser UI, including delayed enrichment, cached/unknown history, identity across platforms,
invalid dates, local-date tooltips, advancing labels, focus, filtering, desktop/mobile layouts,
provider failure, cancellation, and stale responses after navigation. Browser evidence is saved
under `.artifacts/local-web-design/offline-last-live-{desktop,mobile}.png`.
Validation on 2026-10-23 passed all 37 `local web:` tests and all 47
`offline followed channels:` tests, with zero skips. The Release solution build completed
with zero warnings/errors; JavaScript syntax and scoped C# whitespace checks also passed.

### Desktop refresh

Channel status loads independently of optional website metadata. The app displays live and
offline cards, enables their open/pin actions, and finishes the list's loading state before
requesting banner and broadcast-history details. Cached details are included in that first
snapshot. Enrichment then updates existing offline cards in place, preserving their commands
and the user's current pin order. Live notifications use the status snapshot immediately.
Superseded refreshes and shutdown cannot apply late enrichment results. The existing shared
refresh operation tracks enrichment through completion so repeated refreshes do not duplicate
metadata requests and shutdown still cancels and drains the work.

Successful optional metadata is cached for five minutes; failed and missing lookups retry
after thirty seconds. A live channel bypasses older offline cache data for its next offline
refresh. Unfollowed channels are removed from the cache. Transient failures preserve confirmed
history. All work observes caller cancellation.

Twitch metadata requests batch up to 100 logins. Kick artwork and history lookups use at most four
concurrent requests and the existing bounded website reader and curl fallback. An eight-second
deadline bounds the entire optional metadata phase per platform, preserving the existing
live/offline classification if enrichment times out. Artwork and broadcast history failures
are handled independently: a history failure or deadline retains an already loaded banner,
and an artwork failure still allows history to load. Partial failures retry after thirty seconds.

Banner bitmaps retain at most 640 pixels of width, with independent copied pixels so a visible
small card does not keep the original full-HD bitmap alive. The image cache distinguishes
decode sizes; existing avatar and emote callers continue to preserve original pixels. Failed
banner images retry through the existing image refresh and negative-cache behavior.

## Validation

The `offline followed channels:` tests cover real response shapes, identity matching, batching,
cache reuse and recovery, live transitions, invalid history, Kick UTC timestamps, bounded
concurrency, deadlines, cancellation, elapsed-time labels, and independent pin activation.
The Twitch and Kick startup regressions hold actual service banner requests open and verify that the live
and offline lists and their actions are already usable, then verifies artwork updates the
same pinned card when the request is released. Before the loading fix, the same regression
failed with zero live cards, zero offline cards, and the loading state still active. Additional
cases verify immediate cached artwork, updated Twitch profile banners after cache expiration,
late results during account changes and shutdown, and optional metadata failure isolation.
The 2026-10-21 validation passed 103 distinct tests across the `followed`, `home refresh resources:`,
and `background workflow resources:` selections, with no skips. The Release solution build
completed with zero warnings or errors. These controlled network-gate tests establish that optional
requests cannot hold up the list; their timings are not measurements of total app launch time.
Kick banner regressions also cover responsive size selection, URL validation, identity
matching, stale official URLs, confirmed missing artwork, cache recovery, removal, and
history deadlines after artwork has loaded.
The Twitch fallback regression reproduces chopp's null offline-image response, verifies the
profile banner field is actually requested, preserves offline-image priority, and retains
cached artwork during a later metadata failure.
Offscreen renders exercise loaded, missing, and failed artwork in every theme, plus the actual
Following page at widths 320, 360, and 1320. Set `SVS_RESPONSIVE_SCREENSHOTS` to save renders.
Optional local `SVS_OFFLINE_BANNER_FIXTURE` and `SVS_OFFLINE_AVATAR_FIXTURE` paths allow visual
review with downloaded public channel artwork without network-dependent tests.

The final live check on 2026-10-20 passed 83 banner URLs through both downloading/decoding
and the production image loader, including chopp's returned profile banner. Seven offline
channels had no artwork from their platform. One remaining upstream failure, Kick's
`lailabugg`, returned website HTTP 404 and an official API image URL that returned HTTP 403;
the app retains its normal failed-image fallback and retry behavior. Real downloaded chopp
PNG and fqnos WebP artwork also passed the card-render checks in every theme.

```powershell
.\scripts\dev.ps1 Test -Filter 'offline followed channels:'
```
