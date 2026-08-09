# Sub-only Twitch VOD playback (TwitchNoSub technique) — Design

Date: 2026-08-05
Status: Approved (auto mode)

## Goal

When a user tries to watch a Twitch VOD that is marked "subscribers only", the app
plays it instead of failing. This reimplements the technique of the TwitchNoSub
browser extension (https://github.com/besuper/TwitchNoSub, v0.9.3) natively in the
desktop app. No code is copied; the technique is reimplemented in C# (clean-room,
credit given in README).

Non-goals: sub-only *live* streams (TwitchNoSub does not handle them either), Kick,
browser-extension behavior changes, removing restriction badges from any website.

## The technique (verified from TwitchNoSub source)

Twitch serves VOD playlists through `usher.ttvnw.net`, which denies sub-only VODs
without a subscriber token. However, the actual VOD segments live on CloudFront
under predictable URLs that do not require authorization. TwitchNoSub derives those
URLs from the VOD's public storyboard (seek preview) metadata:

1. GraphQL `POST https://gql.twitch.tv/gql` with header
   `Client-Id: kimne78kx3ncx6brgo4mv6wki5h1ko` (public Twitch web client ID, no
   OAuth) and body:
   `query { video(id: "<vodId>") { broadcastType, createdAt, seekPreviewsURL, owner { login } } }`
2. From `seekPreviewsURL` (e.g. `https://<host>/<specialId>/storyboards/0.jpg`):
   take `host` and `specialId` = the path segment immediately before the segment
   containing `storyboards`.
3. For each quality key in `[chunked, 1080p60, 720p60, 480p30, 360p30, 160p30]`
   (best first), build a candidate playlist URL:
   - `broadcastType == "highlight"`:
     `https://<host>/<specialId>/<key>/highlight-<vodId>.m3u8`
   - `broadcastType == "upload"` and older than TwitchNoSub's frozen cutoff
     (`createdAt` is compared against a frozen "now" of 2023-02-10, minus 7
     days — not the current time):
     `https://<host>/<ownerLogin>/<vodId>/<specialId>/<key>/index-dvr.m3u8`
   - otherwise (archive, and uploads newer than the cutoff):
     `https://<host>/<specialId>/<key>/index-dvr.m3u8`
4. Probe each candidate; keep only ones that return a valid playlist.
5. In the fetched media playlist, replace `-unmuted` with `-muted` (sub-only VODs
   404 on the unmuted segment names for muted ranges).

Known upstream limitation, kept deliberately: uploads newer than the frozen
2023-02-10 cutoff use the archive URL shape, which usually 404s → the VOD is
reported unplayable, same as TwitchNoSub.

## Chosen approach: fallback resolver (Approach A)

Normal VODs keep the existing streamlink path unchanged. Only when streamlink
fails to resolve a Twitch VOD do we try the bypass. Rejected alternatives:

- **Always bypass streamlink for Twitch VODs**: loses streamlink quality naming,
  `audio_only`, and breaks recent uploads. Regression for normal VODs.
- **Pipe the resolved URL through streamlink**: extra process hop, no benefit —
  libVLC plays HLS natively.

## Components

### 1. Core: pure helpers (unit-testable, no I/O)

New file `src/StreamlinkVlcStudio.Core/Twitch/TwitchSubOnlyVodPlaylist.cs`:

- `TryParseStoryboardLocation(string seekPreviewsUrl, out string host, out string specialId)`
  — parse host + specialId as described above. False on missing/invalid input.
- `BuildVariantPlaylistUrl(broadcastType, createdAtUtc, nowUtc, host, specialId, ownerLogin, vodId, qualityKey)`
  — the three URL shapes above.
- `QualityKeys` — ordered best-first list: `chunked, 1080p60, 720p60, 480p30, 360p30, 160p30`.
- `SelectQualityKey(IReadOnlyList<string> availableKeys, string requestedQuality)`
  — mapping from app quality ids (`QualityOption.Defaults`). First map the
  request to a preferred key: `best`/`source` → `chunked`; `1080p60`/`1080p` →
  `1080p60`; `720p60`/`720p` → `720p60`; `480p` → `480p30`; anything else →
  `chunked`. Special cases: `worst` and `audio_only` → last available key
  (audio_only has no rendition in this scheme; the lowest video variant carries
  audio). Then pick from `availableKeys` by nearest index distance to the
  preferred key inside `QualityKeys`; ties prefer the lower quality (higher
  index). This is deterministic and covers missing variants (e.g. only
  `chunked` + `720p60` existing).
- `RewriteMediaPlaylist(string playlistContent, Uri playlistUri)`
  — replace `-unmuted` → `-muted`; rewrite relative segment/variant lines and
  `#EXT-X-KEY`/`#EXT-X-MEDIA`-style `URI="..."` attributes to absolute URLs
  against `playlistUri` (required because the playlist is saved to a local file).
  Lines already absolute stay untouched.

New file `src/StreamlinkVlcStudio.Core/Services/ITwitchSubOnlyVodResolver.cs`:

```csharp
public interface ITwitchSubOnlyVodResolver
{
    Task<TwitchSubOnlyVodResolution> ResolveAsync(
        TwitchSubOnlyVodRequest request, CancellationToken cancellationToken = default);
}

public sealed record TwitchSubOnlyVodRequest(string VodId, string Quality);
public sealed record TwitchSubOnlyVodResolution(Uri PlaybackUri, string QualityKey, string Message);
```

### 2. Infrastructure: `TwitchSubOnlyVodResolver`

New file `src/StreamlinkVlcStudio.Infrastructure/Twitch/TwitchSubOnlyVodResolver.cs`.
Follows the existing `ReplayResolver.GetTwitchGraphQlArchiveVodsAsync` pattern
(`ReplayResolver.cs:252-286`): static shared `HttpClient` with overridable ctor
param for tests, same GQL endpoint, same Client-Id const, `X-Device-Id` header,
JSON via `System.Text.Json`.

`ResolveAsync(vodId, quality)`:

1. POST the GQL video query. `data.video == null` → `InvalidOperationException`
   ("VOD was not found or is not public").
2. `TryParseStoryboardLocation` → false → throw with clear message.
3. Build + probe all 6 candidates with GET `Range: bytes=0-65535`; valid =
   2xx and body starts with `#EXTM3U`. (Codec detection from TwitchNoSub is not
   needed — libVLC, not MSE, is the player.) No valid variants → throw.
4. `SelectQualityKey` → fetch that variant playlist fully (no Range).
5. `RewriteMediaPlaylist` → write to
   `%TEMP%\StreamlinkVlcStudio\sub-only-vods\<vodId>-<qualityKey>.m3u8`
   (deterministic name, overwrite OK). Return `file:///` URI.
6. Constructor sweeps the temp dir, deleting files older than 24 h (per-run
   cleanup; resolver is a singleton from the composition root).

All failures throw `InvalidOperationException` with actionable messages;
`OperationCanceledException` propagates.

### 3. Playback integration

`src/StreamlinkVlcStudio.App.Wpf/ViewModels/StreamTabViewModel.cs`:

- New optional ctor param `ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver = null`
  (matches the existing optional-service pattern, `:280-285`).
- In `StartAsync`, split the shared `TwitchVod`/`KickVod` branch (`:1152-1162`):
  Kick keeps today's behavior. Twitch: try `ResolveStreamUrlAsync`; on any
  exception other than `OperationCanceledException`, and when the resolver is
  present, call `ResolveAsync` with `Target.MediaId` (fall back to parsing
  trailing digits from `Target.Url` when MediaId is empty) and `Quality`.
  Success → `directPlaybackUri` = bypass URI, log + `AddSystemMessage` noting the
  sub-only fallback and the quality used. Failure → throw an error containing
  both the streamlink message and the bypass message.
- VOD ID parsing fallback helper: last numeric path segment of the URL.

### 4. Paste support: `twitch.tv/videos/{id}`

`src/StreamlinkVlcStudio.Core/Parsing/StreamInputParser.cs`:

- `ParseInput` gains a VOD branch: on a Twitch host with exactly
  `videos/{numericId}` segments (ordinal-ignore-case `videos`; `@videos` keeps
  being rejected as today), return the VOD id.
- `ParsedInput` record gains `string? VodId`.
- `ParseCandidates`, `Parse`, `TryParsePlatformUrl` return
  `new StreamTarget(PlatformKind.Twitch, vodId, "https://www.twitch.tv/videos/<id>",
  StreamTargetKind.TwitchVod, MediaId: vodId, DisplayTitle: $"VOD {vodId}")`.
  `Channel = vodId` keeps `StateKey` unique per VOD (volume memory) while
  `DisplayTitle` drives display; `TabIdentityKey` already keys on MediaId.
  VODs never start chat or viewer polling (`StartAsync` gates on `IsExplicitVod`).

`src/StreamlinkVlcStudio.App.Wpf/ViewModels/MainViewModel.cs`:

- `SearchStreamCandidatesAsync` (`:3445`): before anything else, if the query
  parses to a VOD target, short-circuit and return a single probe
  `(target, StreamlinkProbeResult(true, "Twitch VOD"))`. This keeps pasted VOD
  URLs working when `streamSearchService` is configured and avoids a streamlink
  probe that would wrongly mark sub-only VODs unplayable.
- No other flow changes: clicking the result goes through `OpenSearchResultAsync`
  (CanPlay=true) → `OpenCandidatesAsync` → single candidate passes
  `ResolvePlayableCandidatesAsync` unprobed → tab opens → `StartAsync`.

### 5. DI wiring

`MainWindow.xaml.cs` composition root (`:757-812`): construct one
`TwitchSubOnlyVodResolver(logger)` and pass it into `MainViewModel`; `CreateTab`/
`CreateAndSelectTab` pass it into `StreamTabViewModel`. Constructor signature
changes ripple through `MainViewModel` ctor and its call sites in tests.

### 6. Docs

- `README.md`: replace/adjust the line 150 statement "The app does not bypass
  ads, DRM, geo restrictions, age gates, account checks, or platform
  permissions." — it is no longer accurate. New wording: the app does not bypass
  ads, DRM, geo restrictions, or age gates; describe the sub-only VOD feature
  and credit TwitchNoSub (technique reimplemented, no code copied).

## Error handling

| Case | Behavior |
|---|---|
| GQL non-200 / network error | throw with status + API message (existing `ExtractApiMessage` style) |
| `video` null | "VOD was not found or is not public" |
| storyboard URL unparseable | "could not derive direct playlist location" |
| no valid quality variants | "no playable qualities were found" (covers recent-upload limitation) |
| temp write failure | throw with path context |
| cancellation | `OperationCanceledException` propagates, never swallowed |
| streamlink fails AND bypass fails | one error containing both messages |

## Testing

Follow the dependency-free harness in `tests/StreamlinkVlcStudio.Tests/Program.cs`
(registry array, `Assert` class, fakes at file bottom).

New tests:

- Storyboard parsing: archive/highlight forms, invalid/missing inputs.
- Variant URL building: archive, highlight, upload >7d, upload ≤7d.
- Quality selection: every `QualityOption.Defaults` id, fallback orders.
- Playlist rewrite: `-unmuted`→`-muted`, relative→absolute segments, `URI="..."`
  attributes, already-absolute untouched.
- Resolver end-to-end with fake `HttpMessageHandler`: GQL + probes + full
  playlist fetch; assert chosen variant, temp file path exists, rewritten content;
  temp dir injectable.
- `StreamTabViewModel.StartAsync` Twitch VOD: fake `IStreamlinkService` that
  throws + fake resolver → playback starts with bypass URI; Kick VOD does not
  invoke the resolver; cancellation is not swallowed.
- Parser: `twitch.tv/videos/123456` → TwitchVod target with MediaId;
  `@videos` still rejected; existing search-bar flows unaffected.

Existing tests to update (intentional behavior change):

- `Program.cs:126` — `TryParsePlatformUrl("https://www.twitch.tv/videos/123456")`
  now returns true with a TwitchVod target.
- Any other assertions that `/videos/` URLs are rejected (keep `@videos`
  rejection assertions unchanged — `Program.cs:134`, `:602`).

## Validation commands

- `dotnet build StreamlinkVlcStudio.sln`
- `dotnet test` (runs the exe-based harness via the csproj `VSTest` hook)
- `SVS_TEST_FILTER` env var to run focused tests during development.
