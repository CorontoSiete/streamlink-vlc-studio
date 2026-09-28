# Repository review and restructuring — 2026-09-27

The existing working tree was the baseline for this review. Its source files were
saved under `.tmp/repository-review-baseline` before implementation. Existing UI,
installer, playback, native-plugin, and preview changes were preserved. Framework
versions, package versions, settings and ownership formats, and the native overlay
wire protocol remain unchanged.

## Behavior fixes

- Ordinary replay seeks and skips retain pause, including replacement playback,
  prepared replay adoption, and decoder fallback. The frozen clock, resume hold,
  and bookmark seek expectation are committed after a successful seek. Explicit
  Resume and Return to Live still play. Replacement audio stays muted until the
  requested position and native paused state are confirmed. The original
  `IPlaybackEngine.PlayFromAsync` overload retains its playing semantics; the new
  overload accepts `startPaused`.
- Ordinary and forced Kick user-token rotation share `KickUserTokenCoordinator`.
  Concurrent callers share the HTTP work and cancel only their own waits.
  Publication checks the captured account before applying credentials, including
  deferred UI writes. Completed rotations are retained briefly for a surviving or
  later waiter. App-token selection remains separate.
- Native emote entries, queued work, decoded completions, static fallbacks, and
  rendered-image cache entries carry a source generation. Obsolete work releases
  its own resources without changing a replacement entry or its loading flag.
- Desktop verification exposed an existing resize race: repeated overlay frame
  acknowledgments invalidated a pending resize even though the source had not
  changed. Resuming resize persistence is now idempotent within the same session.
  A deterministic regression holds publication across a second acknowledgment;
  existing session-replacement checks still reject obsolete callbacks.
- Ownership metadata readers validate JSON object and number kinds before typed
  access. Malformed ownership data reaches the existing invalid-state result,
  retaining installation contents and retry metadata.
- Both packaging entry points use `Resolve-PackageVersion` from
  `scripts/lib/common.ps1`. Defaults come from `Directory.Build.props`; explicit
  versions take precedence. Missing, ambiguous, malformed, or invalid defaults
  fail before publish or staging starts.

## Ownership and reuse

`StreamSearchViewModel`, `VodLibraryViewModel`, `BrowseViewModel`,
`FollowedChannelsViewModel`, and `RecentStreamsViewModel` own their feature state,
commands, cancellation, and pending operations. `HomeFeatureViewModel` provides
shared lifetime handling. Shutdown cancels new work and waits for active providers
before releasing their resources. Main retains its binding facades, navigation,
tab ownership, and typed stream-opening callbacks. Recent collection reconciliation
uses `PagedResultTracker.ApplyItems`.

`NativeChatOverlayController` owns native overlay startup, shutdown, rendering,
scrolling, and image-cache subscriptions. It receives immutable playback, chat,
and message snapshots. `ReplayClockState` owns accepted clock samples, anchors,
frozen positions, and resume holds. `StreamTabViewModel` retains the playback
engine and playback-transition lock ordering.

MainWindow delegates docked-chat scrolling, PiP hosting, and fullscreen/tray work
to three disposable controllers. Main-window and PiP policies remain distinct.
Twitch and Kick Browse and Replay implementations are internal providers behind
the existing service facades, reusing bounded HTTP and payload helpers.
`LibVlcPreviewPlayer` now owns both preview implementations' aligned frame memory,
native player, and rooted callbacks, stopping native playback before freeing them.

Removed fields and branches were limited to verified dead data: native catalog
`id`/`image_type`, the unreachable normalized `subgifter` case, and unused replay
candidate titles and their GraphQL selection. A Roslyn comparison found unchanged
public member names and method/property signatures on MainViewModel,
StreamTabViewModel, MainWindow, BrowseService, and ReplayResolver. Literal
reflection references in the tests also resolve.

Desktop fixtures were updated for the existing rounded card/avatar design and
visible scrollbar width. The replay scroll fixture now consumes the new native
scrollbar range before issuing an absolute drag after adding messages.

## Native artifact

The changed controller was rebuilt with the existing GCC/VLC toolchain and pinned
in `dependencies/native-overlay.json`. Third-party source and license provenance
were retained.

- File: `src/StreamlinkVlcStudio.Infrastructure/Vlc/BundledOverlay/build/vlc_chat_overlay.exe`
- Size: 412,952 bytes
- SHA-256: `26dee8d3ab1a1f1a36b69f61b6a7c8d092002ed8522cd744c964f93da3dc677d`

## Verification

The final `scripts/dev.ps1 Check` passed: **1,279 tests passed and 252 expected
interactive tests were skipped**, with zero failures. The Release build reported
zero warnings and zero errors. Locked restore, PowerShell syntax, formatting,
mocked tooling, and both pinned native overlay inputs also passed. This adds 13
passing default-suite cases to the original 1,266-test baseline. The complete log
is `.tmp/review-check-complete.log`.

Targeted checks passed after each extraction: search, Browse, Followed, Recent,
navigation, VOD paging/resume, replay clock, overlay lifecycle, pause, PiP,
fullscreen, and docked-chat behavior. Added regressions cover paused seek/reload,
failed seek restoration, near-live positioning, prepared adoption, native fallback,
Kick refresh sharing and cancellation, account clearing/replacement, invalid
ownership types, and packaging version defaults and overrides.

All six native suites compiled with warnings treated as errors and passed:
TLS handshake, networking, readability, render resources, compositor, and
subpictures. Generation tests cover obsolete success, failure, fallback, queue
rejection, and rendered-cache reuse. Actual VLC paused-seek, prepared-adoption,
and decoder-fallback checks passed, including muted first output and a stationary
paused clock. Mocked tooling and publication tests passed.

The broad interactive run exercised 1,603 cases: 1,595 passed, six assertions
failed, and two cases could not acquire foreground activation. The assertion
failures were investigated and their affected checks subsequently passed in
targeted runs. This included the resize-persistence fix, the scroll-range and
rounded-card fixture corrections, restoring Main's internal Browse-selection
facade, and isolated retries of timing-sensitive playback/input checks. The final
overlay group passed 156 checks; its two foreground-dependent cases also passed
when run individually. There were no timeouts or unrun selected cases.

With installed VLC and a local bright video/TS fixture, 24 of 26 selected media
and window cases passed across isolated runs. These exercised preview decoding,
actual Direct3D11/GDI seek input, window capture, PiP edges and menus, responsive
video, tab switching, volume input, and back-navigation hotkeys.

Two optional GDI continuous-resize checks remain failing: video alone and video
with native chat briefly produce blank captured frames. Both failures were
reproduced by building the saved original working tree and running the same
fixtures, before the review changes. The final comparison observed one blank
frame in each current case; the original tree produced five and one respectively.
The PiP GDI frame test initially observed four blank frames and passed on an
isolated repeat; both baseline PiP runs passed. These results are retained as a
rendering limitation, not counted as unavailable coverage or a clean desktop
suite. Direct3D11 continuous resizing passed.

Windows sometimes denied foreground activation during grouped physical-input
tests. Those cases passed individually; a single clean grouped desktop run was
not obtained. Optional live-provider fixtures requiring channel/video selections
or authenticated accounts were not configured. No live publication was performed.

Local evidence is in `.tmp/review-interactive-full.log`,
`.tmp/review-overlay-final.log`, `.tmp/review-rerun-*.log`,
`.tmp/review-fixture-*.log`, `.tmp/review-current-gdi-comparison-*.log`, and
`.tmp/review-baseline-gdi-*.log`. Native and mocked tooling evidence is in
`.tmp/review-native-final/`, `.tmp/review-tooling-final.log`, and
`.tmp/review-publication-final.log`.
