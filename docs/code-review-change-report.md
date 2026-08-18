# Streamlink VLC Studio code review change report

Review date: 2026-08-16

Review scope: application and Core source, Infrastructure integrations, WPF code, the dependency-free test runner and test suite, browser extension, PowerShell install/build/package tooling, WiX authoring, and supporting documentation. Generated build output and bundled third-party binaries were not treated as source code.

## Correctness and security fixes

- TwitchDownloader chat caches are scanned to their bounded end rather than stopping at 5,000 comments. Cache coverage is reported honestly, and uncovered GraphQL ranges are merged and deduplicated while retaining the 60-second backfill and four-minute prefetch windows.
- Twitch and Kick live chat use one reconnect supervisor. Initial connection behavior is unchanged; EOF, server reconnect requests, remote close, and transient failures retry indefinitely with jittered 1/2/4/8/16/30-second backoff, reset after 60 stable seconds. Explicit shutdown cancels and drains pending retries.
- Kick channel/chatroom identity maps are copy-on-write snapshots behind one store, and subscription shutdown now stops admission, cancels and drains work, then disposes HTTP before the final settings save.
- Kick replay JSONL retention is enforced at 30 days and 512 MiB without following reparse points or deleting the active/current-day file.
- All bounded HTTP/JSON callers now request headers-only completion before applying byte limits. Large or delayed remote bodies are no longer buffered in full first.
- Kick website JSON loading and GraphQL error parsing are shared helpers. The Kick helper consistently falls back from blocked, timed-out, invalid, blank, or oversized managed responses while preserving caller cancellation.
- Kick webhook replay IDs use reserve/commit/release semantics, so failed parsing or persistence can be retried safely. Released-entry bookkeeping is compacted, and public-key refresh retains the last known good key with single-flight/backoff behavior.
- Twitch client-ID and Kick token acquisition no longer let one canceled waiter cancel shared work. Near-expiry Kick tokens are not cached, and abandoned in-flight entries clean themselves up.
- Twitch and Kick loopback OAuth callbacks share strict method/path/state validation and safely continue past unrelated or malformed requests.
- Process output draining, logger flush failure, Kick history disposal, Twitch EventSub start/dispose, Streamlink output lines, JSONL replay lines, and replay cache loading now have bounded memory/time/lifecycle behavior.
- Replay HTTP production clients prevent automatic redirects from bypassing provider-host validation. Replay chat now uses the configured normalized Twitch OAuth token and rejects overflowing offsets.
- Live-channel cache expiry starts when a request succeeds, and failed/canceled entries are evicted immediately. Retry headers and date arithmetic are clamped and saturating.
- DPAPI allocations are cleanup-safe, and unmanaged plaintext output is zeroed before release.
- Windows command-line tokenization now follows `CommandLineToArgvW` quote/backslash behavior, including adjacent quote runs.
- Duration conversion rejects the rounded `Int64` boundary without mutating a failed out value.
- Catalog loads distinguish success from mutation, retain a one-hour TTL only after success, retry total failure from five seconds, and reject stale generation completions. Scope payloads follow the 256-entry LRU and message-supplied emotes use a 4,096-entry LRU.
- Native overlay messages have one codec and a 32 MiB complete-message limit. Rendering downscales to budget with a bounded transparent fallback; the queue reserves control capacity, coalesces stale frames, isolates callback/logger faults, restarts after loop failure, and drains on disposal.
- Native overlay resize persistence uses same-directory temporary files plus session/sequence validation and atomic replacement. Capability probes are identity-keyed, single-flight, and cache transient failures for five seconds.
- Image fallbacks are candidate-local, multicast subscribers are isolated, command gates release after throwing callbacks, delayed overlay-dispatcher startup is drained, and clipboard contention receives three bounded retries with nonfatal UI reporting.

## Reuse, cleanup, and maintainability

- Twitch and Kick reserved routes have one canonical JSON policy. Core embeds it, the browser extension consumes a generated artifact, CI checks that artifact, and package/installer payloads include it. Missing routes such as Kick `/browse` and Twitch `/creatorcamp` are rejected as channels.
- Kick badge aliases have one Core normalizer used by parsing, the image catalog, and glyph rendering.
- Browse and followed-stream cards are one `LiveStreamCardViewModel` and one open workflow/template instead of parallel models and handlers.
- GraphQL error selection, Kick website retrieval, bounded HTTP sending, installer dependency validation, and bounded atomic downloads are reusable shared components.
- Twitch GraphQL transport, Kick website JSON/fallback transport, browse payload mapping, Twitch rate-limit coordination, Kick webhook authentication/replay protection, libVLC runtime/audio request state, tab-start throttling, deferred playback cleanup, background UI work, and home auto-scroll calculations now have focused components behind the existing façades.
- `MainViewModel` and `StreamTabViewModel` retain all XAML-facing names and collection identities while delegating search/VOD cancellation, recent streams, grouping, playback policy, background work, tab starts, playback resources, replay-window selection, and chat event coordination.
- Settings types were split into individual files without changing namespaces, defaults, or serialized property names. `StreamInputParser.TryFromChannel` provides the new nonthrowing channel parser.
- Playback engine creation is async-only. The unused synchronous LibVLC factory/constructor and dead overlay preparation path were removed.
- Legacy positional `MainViewModel` and `StreamTabViewModel` constructors were removed. Production uses dependency records and tests use centralized builders.
- Duplicate/private compatibility-only methods and obsolete test cancellation infrastructure were removed where reference/build checks proved them unnecessary.
- LibVLC audio updates use one coalescing serialized worker and atomic immutable request snapshots instead of independently mutable volume/state/version fields.
- The dependency-free runner reports passed, failed, timed-out, skipped, and not-run tests accurately, bounds timeout configuration, and does not leave needless timeout delays pending.
- The original 705 characterized tests remain in explicit ordered catalogs exactly once; `Program.cs` is one line and additional fixes live in a separate regression catalog.

## Installer and release fixes

- Windows dependency manifests have one parser and one canonical `length` field. Installation selects a discovered executable only when its parsed version meets or exceeds the pin and reports every rejected candidate.
- Installer/build downloads stream into bounded same-directory temporary files, require HTTPS, validate declared/exact size and integrity before promotion, and atomically replace existing files.
- Streamlink dependency detection is version-aware rather than treating any `streamlink.exe` as sufficient.
- Pinned installer length/hash/signature/publisher/product checks are implemented once in the shared script library.
- Alternate native overlay sources are verified as a closed set, including hidden entries, before exactly those verified files are staged. Alternate paths cannot bypass lengths, hashes, signatures, or provenance.
- `shared/release-contract.json` is the canonical payload/output/asset contract. It requires one payload root, the complete browser runtime, and exactly six release assets; route policy check mode runs before packaging.
- SBOM generation and verification independently reconstruct package, publish-deps, runtime-pack, Windows-installer, and native-manifest dependencies and require exact canonical equality.
- WiX versions are compared semantically. MSI/bootstrapper outputs and final release sets are built in temporary staging and promoted together with rollback rather than overwritten piecemeal.
- The uninstaller validates ownership schema, install ID, manifest hash, managed paths, file hashes, and reparse-point safety before removing shortcuts or registration. Metadata remains intact if validation or cleanup handoff fails.
- Release publishing consumes only the verified closed release set; duplicated workflow asset lists and MSI version calculations were removed.
- SBOM enumeration includes hidden files and rejects reparse-point ambiguity.
- Browser route generation is strict, deterministic, and atomic.
- Third-party notices include resolved SkiaSharp, HarfBuzzSharp, Toolkit notification, Windows SDK, and self-contained .NET runtime/runtime-pack metadata.

## Compatibility and removal decisions

- XAML bindings, command/property names, settings JSON names/defaults, collection identity, and application-facing service interfaces remain compatible. `ReplayResolver`, `ReplayChatProvider`, `BrowseService`, `KickWebhookChatServer`, `LibVlcPlaybackEngine`, `MainViewModel`, and `StreamTabViewModel` remain the application façades.
- Kick event subscription lifetime intentionally changed to `IAsyncDisposable`; no-op replay/backfill disposable surfaces and the unused process-tree wrapper were removed.
- The unused installer-verification script, actionlint configuration, redundant artifact-pattern code, ineffective app `System.Drawing.Common` update, generated `.dependency-audit` cache, and obsolete mega-catalog were removed. `.dependency-audit/` and `.nuget/` are ignored. No Git repository was initialized.

## Verification

- Release solution build with warnings as errors: 0 warnings, 0 errors.
- Dependency-free suite: all 719 tests passed with 0 skipped and 0 timed out. This includes all original 705 characterized tests plus 14 focused regressions.
- Twitch behind-live native replay overlay scroll/seek stress: 10/10 fresh runs passed.
- Browser extension Node suite: 16/16 passed.
- `dotnet format --verify-no-changes --no-restore`: passed.
- All 14 PowerShell source files parsed successfully; tooling regressions passed for canonical lengths, compatible-version selection, closed native provenance, ambiguous payloads, stale routes, exact release sets, runtime-pack SBOM reconstruction, and semantic MSI versions.
- Workflow YAML passed actionlint 1.7.12, and the browser route artifact passed `-Check` mode.
- Native provenance verification accepted exactly the two pinned overlay binaries.
- A clean self-contained publish/package pass validated 23 payload files and produced an 84,881,581-byte release zip before its isolated staging directory was removed.
- The SBOM tooling fixture generated and independently verified 14 packages, including runtime-pack reconstruction.
- NuGet direct/transitive vulnerability audit: no known vulnerable packages in any solution project.
