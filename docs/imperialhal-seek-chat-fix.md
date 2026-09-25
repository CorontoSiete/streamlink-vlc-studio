# ImperialHal chat after seeking into the middle of a live stream

Diagnosed on September 25, 2026, after the reported channel was narrowed to
`imperialhal__`. This is distinct from the [growing archive completion bug](twitch-seekback-chat-fix.md).

## Observed source availability

The local `studio.log` records capture-only Twitch chat at 14:02:05.035 and
14:03:10.379 (UTC-04:00). Each is followed by a first backward seek. Opening the
replay video took 3.195 and 3.084 seconds respectively.

An anonymous, read-only request to Twitch at **18:07:49 UTC** confirmed:

- Current stream: `321695232859`, started September 25 at **15:01:08 UTC**.
- The current stream's `archiveVideo` field was **null**.
- The newest published archive was `2882785760`, started **September 24** at
  15:01:53 UTC. It is a different broadcast and cannot supply today's chat.

The metadata-only response and query are retained in
`artifacts/logs/imperialhal-seek-chat/twitch-archive-availability.json`. No account
token or chat message content was needed. The app can play the current DVR video
without a published archive; that does not provide historical chat. Messages
received while this tab is connected can still be recorded and replayed at their
proper broadcast offsets. Existing polling promotes the session if Twitch later
publishes a matching archive.

## Reproduced application bug

`SeekReplaySerializedAsync` clears the visible chat and anchors its timeline to
the requested position before opening the replay video. `IsReplayMode` and
`IsBehindLive` are committed only after the video opens successfully. During that
interval, `ChatClientOnMessageReceived` still treated incoming IRC messages as
live display messages. They filled the newly cleared chat panel with a few
messages from the live edge. After the seek finished, replay timing took over.
At a middle-of-stream position without captured or published history, no more
messages were due. This produced the misleading burst followed by silence.

The regression holds the actual view model's first replay open in flight,
receives three messages through its chat client, then completes the seek. Before
the fix, all three timing cases failed with **Expected '0', got '3'**. The cases
cover DVR, an archive-backed live stream, and messages belonging at the seek
target. They do not depend on network delays to hit the race.

A separate native-overlay test also failed: the first seek did not send a blank
frame until `PlayFromAsync` (including its initial seek) had returned. Stopping the
live overlay controller alone left its last image visible during that wait.

## Correction

Live message receipt still captures every message, but display now checks the
seek-in-progress flag and the seek generation. The queue rechecks that generation
alongside its chat epoch, and delivery of already-due captured messages uses the
same generation check as the playback clock. Messages captured during the seek
remain available and appear once playback reaches their offsets.

Once the live overlay controller releases the shared frame pipe, an empty replay
window now queues its clear frame before opening the replay video. The existing
post-open clear is retained for the newly opened output. Both native-overlay
regressions pass, including the check that the frame is blank while the initial
seek is still blocked.

The user's retry at 14:16 used the same capture-only DVR path. The first build's
tooltip did not sufficiently explain the empty chat surface. The user specified
that chat must stay synchronized and visibly explain unavailable history, rather
than switch to current live chat. A fresh Twitch request at 18:18 UTC still
returned `archiveVideo: null` for the same broadcast.

The chat panel and native VLC overlay now display:

> Twitch hasn't published chat history for this broadcast. Captured messages will appear when playback reaches them.

This status appears when the playback position precedes retained captured chat.
Future live messages do not dismiss it. It clears when playback reaches captured
chat, when a matching archive is published, or when returning to live. Seeking
back into unavailable history shows it again. Queued status updates carry the
seek generation and chat epoch so a previous position cannot overwrite a newer
one. The docked header now says **REPLAY CHAT** during replay.

The status is separate from chat messages and does not fabricate historical
messages. Video seeking and background live capture continue normally.

This correction prevents current live messages from appearing at an unrelated
historical position. It cannot reconstruct chat from before the tab connected
when Twitch has not made that broadcast's chat archive available.

## Verification

All four new regression tests pass, including continued capture and delivery
without duplicate messages. Their failing and passing logs are retained in
`artifacts/logs/imperialhal-seek-chat/`. The tests exercise the real view model,
chat controller, timeline, and delivery queue with a controlled playback engine.
The two separately enabled native-overlay tests verify the rendered/transparent
frames through the real named-pipe protocol. They do not claim a live Twitch/VLC
desktop reproduction.

Initial validation: **1,013 passed, 240 desktop-only skips, zero failures or
timeouts** in the full headless suite, plus **2 passed, zero skips** in the
separately enabled native-overlay checks. The skip ceiling remains 240; this
change adds no skipped tests. Release build and self-contained publish completed
with warnings treated as errors. Formatting/analyzer verification and
`git diff --check` also passed.

The follow-up status change passes **1,017 tests with 240 desktop-only skips**,
with no failures or timeouts. All **7 current-live DVR checks** also pass when
separately enabled, including native frames and archive promotion. Four status
regressions cover captured-message timing, queued callbacks, the actual native
frame pipe, and the actual docked-header XAML bindings and wrapping. The two new
rendering checks run offscreen and add no skips. Reviewed PNGs and logs are in
`artifacts/imperialhal-chat-status/screenshots/` and
`artifacts/logs/imperialhal-chat-status/`.

The runnable build is `artifacts/imperialhal-seek-chat/app/StreamStudio.exe`.
It includes the earlier growing-archive fix as well as these DVR transition
corrections. The existing published executable and installed applications were
not replaced.
