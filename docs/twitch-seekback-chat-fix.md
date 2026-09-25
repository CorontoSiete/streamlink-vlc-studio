# Twitch chat stopping after seeking back

Diagnosed on September 25, 2026.

## Evidence

The existing local `studio.log` recorded `VodChat: VOD chat reached the end of
rogue (2883719165)` at 13:33:20.895 (UTC-04:00), between the first-seek transition
starting at 13:33:20.710 and playback opening at 13:33:23.034. Similar messages
appeared during seeks on other live channels.

Anonymous, read-only requests to Twitch's `VideoCommentsByOffsetOrCursor` query
confirmed that this VOD belonged to the ongoing broadcast. Its owner still had a
live stream; the VOD creation time was 16:55:44 UTC and the stream creation time
was 16:55:39 UTC.

Responses from that same comments endpoint demonstrated that `hasNextPage=false`
does not mean a current broadcast has finished producing chat:

| Observation (UTC) | Requested offset | Messages | First offset | Last offset | hasNextPage |
| --- | ---: | ---: | ---: | ---: | --- |
| 17:38:58.753 | 2400 | 36 | 2366 | 2519 | false |
| 17:38:58.943 | 2519 | 26 | 2425 | 2586 | false |
| 17:39:15.937 | 2586 | 18 | 2484 | 2609 | false |

Offsets are seconds since broadcast start. These observations use only page
metadata; no account credentials or chat message text are included here.

## Failure and correction

`TwitchVodChatFetcher` translates a page with `hasNextPage=false` into
`VodChatFetchOutcome.Completed`. `VodChatController` previously marked every
such response as permanently exhausted. The playback clock could keep advancing,
but the controller would never request more chat. Seeking could replay the small
cached page, then leave chat silent until another operation restarted fetching
or playback reached locally captured live messages.

The view model now tells the controller when it is following a current
broadcast. For that source, completion means temporarily caught up: the controller
waits five seconds and continues from the fetched boundary. Overlapping messages
remain deduplicated. An empty final page cannot advance the boundary by the
fetcher's synthetic 30-second step, because that would skip comments published
later in that interval.

Seeking outside fetched coverage and promoting a DVR session clear the retry
deadline. An old in-flight response cannot install a deadline on a newer seek.
Finished archives retain permanent completion, and live capture continues while
the provider is waiting. The log now distinguishes temporarily caught-up chat
from the end of a finished archive.

This does not create historical comments for a DVR source without a published
VOD id. Such a source still uses chat captured while the tab was connected.

## Regression verification

`GrowingVodChatTestCatalog.SeekbackContinuesAsync` runs the real view model,
provider, Twitch response parser, controller, and playback chat delivery with a
controlled HTTP response and fake player. It seeks to 600 seconds, displays
messages at 590 and 600, and then makes a message at 601 available without
another seek, live IRC message, or reconnect. Before the fix, it failed waiting
for message 601 after eight seconds. Existing VOD-chat tests all passed before
the fix, demonstrating the missing coverage.

Additional cases cover delayed empty pages, retry pacing, finished archives,
seeks during the retry interval, stale responses, and live capture during a wait.

All six regressions pass. The Release solution build passes with warnings treated
as errors (zero warnings/errors); formatting/analyzer verification for the changed
C# files, PowerShell syntax validation, and `git diff --check` also pass.

The first full-suite run exposed two packaging checks selecting the older system
SDK. Both pass with the already-installed SDK from `global.json` on `PATH`. Its
skip-limit check also exposed one pre-existing test added since the previous
review: `replay seek overlay Kick VOD preview displays a frame without seeking
playback`. Comparing the skip names against
`artifacts/logs/review-pass11/final-tests.log` identifies that exact additional
desktop test. The final local run therefore uses a 240-test limit, with no changes
to the repository's CI configuration. This fix adds no skipped tests.

Final full headless suite: **1,009 passed, 240 desktop-only skips, zero failures or
timeouts**, with the audited local skip limit enforced. The player in the new
regression is simulated; interactive desktop playback was not exercised.

Validation logs, including the failing regression before the fix, are retained in
`artifacts/logs/twitch-seekback-chat/`.
