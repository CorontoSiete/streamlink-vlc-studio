# UI freeze while playback continues — 2026-09-20

## Confirmed cause

The WPF UI was starved by a chat catalog/repaint feedback loop. Native VLC playback
continued on its own threads.

Twitch's replay chat response supplies two different identifiers for an embedded
emote. For example, the public response for VOD `2879641133` contained:

```json
{"id":"74510;0;9","emoteID":"74510","from":0,"__typename":"EmbeddedEmote"}
```

The next occurrence of that same emote had `id: "74510;11;20"` and the same
`emoteID: "74510"`. `TwitchVodChatFetcher` ignored `emoteID` and used `id` in
the CDN image URL. This made repeated occurrences appear to have different images.

`DockedChatMessageTextBlock.RebuildInlines` registered the message's emotes in a
shared catalog on every repaint. Entries were keyed by platform, channel and
displayed emote code. Conflicting image URLs repeatedly replaced each other and
raised `CatalogChanged`, causing every subscribed row to repaint and repeat the
mutation. The callbacks used WPF Normal priority, above input and rendering, and
worker-thread notifications were not coalesced before entering the dispatcher.

## Evidence

- Six thread samples were captured from the affected process, PID `52092`.
  Five placed its UI thread inside `DockedChatMessageTextBlock.RebuildInlines`.
  Different inner frames and increasing thread CPU established active repeated
  rebuilding rather than a fixed lock wait.
- A local process snapshot showed repeated emote codes with position-bearing URLs,
  including `1035696%3B0%3B3` and `1035696%3B5%3B8` in one message. Input and
  background dispatcher operations had accumulated while catalog repaint work ran.
  Traversal of the actual dispatcher queue verified 26,300 pending operations,
  including 2,398 mouse callbacks. One subscribed visible row had 15 occurrences
  of `TheIlluminati` mapped to 15 different position-bearing URLs.
- An anonymous request to the same public Twitch replay query confirmed the
  separate `id` and `emoteID` fields. Only emote fragments were retained as the
  response fixture; no credentials were used.
- All seven new chat UI regression tests failed against the original production
  sources without timing out. A burst of 128 notifications posted 128 operations;
  pending input ran after catalog refreshes; unloaded rows were rebuilt; and
  unchanged messages kept producing catalog notifications or replacing newer data.
- The exact replay-payload regression also failed against the original parser:
  expected `/1035696/static/light/2.0`, received
  `/1035696%3B0%3B3/static/light/2.0`. The correct public CDN URL returned HTTP 200;
  the old malformed URL returned HTTP 404.

Diagnostic artifacts are local under `.tmp/ui-freeze-diagnosis/`, including the
thread samples, heap analysis, original-source snapshots, public emote samples and
before/after test logs. The full process dump is retained there for local analysis.

## Changes

- Parse Twitch's canonical `emoteID`, preserve supported legacy ID fields, and
  reject occurrence IDs and opaque node IDs as image identifiers.
- Register an immutable chat message's emotes once per catalog, using weak identity
  tracking so old messages cannot repeatedly republish their data or be retained
  by the registration cache. Existing catalog load retries and the 4,096-entry
  learned-emote limit remain in place.
- Keep repainting read-only with respect to catalog registration. Admission occurs
  when a control receives a message or loads.
- Coalesce catalog notifications before dispatcher submission, schedule decoration
  refreshes at Background priority, and ignore queued refreshes after unloading.

The regressions cover input priority, burst coalescing, unloading, conflicting
images within and across messages, recreated/concurrent views, and the actual
Twitch replay response shape. Per-message image URLs remain authoritative for
rendering that message.

## Validation

- Release solution build with warnings treated as errors: passed, zero warnings.
- New chat responsiveness regressions: 7/7 passed after failing against the
  original sources. The focused chat filter also passed its existing matched test.
- Twitch VOD suite: 31/31 passed, including three new identity/parser regressions.
- Full desktop suite, including real VLC video fixtures: 786 tests executed with
  zero skips. The initial run passed 777. Three failures were reproduced as
  environment issues and passed focused reruns after setting the project SDK on
  `PATH` and using a writable workspace `TEMP`/`TMP`. Physical-input checks are
  recorded separately in the diagnostic logs. Five of six remaining physical
  checks passed isolated reruns, including real Direct3D11/GDI VLC input. One
  pointer-drag precision check still failed; it also failed against the original
  renderer/catalog build at the same assertion. Its fixture has chat disabled,
  so it does not exercise the changed rendering paths. Final coverage: 785 of 786
  distinct checks passed across the full run and focused reruns; no skips.
- The normal Release output was tested separately with all seven new chat
  regressions and the exact replay-payload regression before launching it.
- The corrected app was reopened from its original executable path and xQc live
  playback resumed. A snapshot on the same .NET 10.0.7 runtime showed 19 subscribed
  chat controls and only three pending operations on the main dispatcher. The
  UI thread was waiting for messages rather than continuously rebuilding rows.
  Three subsequent snapshots showed queue counts of 6, 5 and 11, with the app
  responsive in each sample, visible chat controls and no position-bearing image
  URLs or same-code image conflicts in the sampled visible emotes.

The original behind-live session was at about `1:41:08` in VOD `2879641133`.
Playback was reopened live. Desktop screenshot capture failed with
`SetIsBorderRequired: No such interface supported (0x80004002)`; final app checks
used accessible control state, thread samples and read-only process snapshots.

The packaging scripts invoke `dotnet` themselves, so invoking the test DLL with the
correct SDK executable alone is insufficient: child processes also need
`.tools/dotnet` first on `PATH`. The default `C:/Windows/TEMP` in this session denied
access to a replaced test file; `.tmp/test-runtime` worked without product changes.
