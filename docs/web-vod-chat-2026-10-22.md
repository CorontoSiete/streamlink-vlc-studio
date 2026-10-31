# Browser VOD chat

The local web player fills archived Twitch and Kick chat ahead of playback and
releases each message using the browser video's current time. Pause, playback
speed, forward seeks and backward seeks therefore use the same media clock.
Buffered messages continue to appear while the next network request is pending.

Previously, the browser fetched one page every two seconds and waited for that
request before rendering buffered messages. A busy Twitch page can advance by
only one second, so that loop could stay behind the video indefinitely. It also
discarded the oldest buffered messages when it reached 2,000 entries, including
messages playback had not reached. The new pump pages immediately to a 45-second
look-ahead and pauses fetching when the pending buffer is full. Pages overlap;
message IDs suppress duplicates, including partial pages that are retried.

Seeking clears the visible history and pending messages at the `seeking` event,
cancels the old request and loads the preceding 30 seconds at the new position.
Responses belong to a seek generation. Closing, stopping and disconnecting also
cancel pending chat. The API queue retries the brief HTTP 429 race between a
browser cancellation and the server releasing its command gate. Password sign-in
attempts are excluded from that retry.

The server returns the provider's actual failure/unavailability reason. A failed
page keeps its original offset for retry; completed archives stop fetching.

Pasted Kick UUID VOD links now resolve through the public video resource and
retain `livestream.start_time`, the duration in milliseconds and channel ID.
Those values align the historical message timestamps with video time. Video,
channel and HTTPS recording-host checks remain mandatory. ULID links retain
their existing website resolution path. Missing timestamps are reported rather
than inferred from a publication date.

## Verified provider responses

On 2026-10-22, requests to the providers themselves confirmed:

- Twitch's existing `VideoCommentsByOffsetOrCursor` persisted query works
  anonymously. Its hash was kept unchanged. Offset requests return a page around
  the requested time, with `contentOffsetSeconds`, message fragments, IDs and
  `hasNextPage`.
- [Kick's UUID video resource](https://kick.com/api/v1/video/e22f14c4-1803-43c9-98f7-748f68546796)
  supplies the source and original broadcast clock for xQc's 2026-10-21 archive.
- [Kick's messages resource](https://kick.com/api/v2/channels/668/messages?cursor=1792613197000000)
  accepts a Unix-microsecond backward cursor and returns historical messages.

## Validation

The deterministic WebView2 regression runs production browser assets through the
real HTTPS/authentication server and decodes actual HLS fixture video. It failed
on the previous implementation. It checks both platform identities with
one-second pages, overlapping boundaries, pause and 2x playback, backward seeks,
buffered delivery during a held request, a late response after seeking, empty
pages, partial-page failure/retry and archive completion. Separate workspace and
Kick metadata tests check failure reasons and reject mismatched video/channel
metadata and unsafe recording URLs.

The opt-in browser probe uses real provider chat, real remote VOD video and the
production media proxy. Its surrounding library/profile UI is a fixture. It
checks displayed message IDs against the actual returned content offsets at
600, 1,800, 300 and 360 seconds, including quiet intervals, and saves browser
screenshots under `.artifacts/local-web-design/*-vod-chat-live.png`.

Both public probes passed on 2026-10-22. Twitch archive `2906978606` decoded video
and showed 4, 3, 0 and 1 messages respectively in those 30-second windows. The
empty window at 300 seconds matched the actual provider response. Kick archive
`e22f14c4-1803-43c9-98f7-748f68546796` decoded 1920-pixel video and showed 38, 65,
44 and 29 messages. No future messages appeared while the video was paused.
All 27 local-web tests, 51 VOD-chat tests and 70 download regressions passed with
zero skips. The Release solution build completed with zero warnings or errors.

```powershell
.\scripts\dev.ps1 Test -Filter 'local web:' -Interactive
.\scripts\dev.ps1 Test -Filter 'VOD chat' -Interactive -NoBuild
.\scripts\dev.ps1 Test -Filter 'VOD downloads:' -NoBuild

# Public URLs are retention-limited; substitute an available archive as needed.
$env:SVS_TEST_VOD_CHAT_URL = 'https://www.twitch.tv/videos/2906978606'
$env:SVS_TEST_VOD_CHAT_STREAMLINK = 'C:\Program Files\Streamlink\bin\streamlink.exe'
.\scripts\dev.ps1 Test -Filter 'opt-in public VOD plays' -Interactive -NoBuild
$env:SVS_TEST_VOD_CHAT_URL = 'https://kick.com/xqc/videos/e22f14c4-1803-43c9-98f7-748f68546796'
.\scripts\dev.ps1 Test -Filter 'opt-in public VOD plays' -Interactive -NoBuild
Remove-Item Env:SVS_TEST_VOD_CHAT_URL, Env:SVS_TEST_VOD_CHAT_STREAMLINK
```

The opt-in probe is absent from the normal suite unless its URL variable is set.
Provider endpoints are website interfaces and can change; archived chat still
depends on what each provider retains and makes available.
