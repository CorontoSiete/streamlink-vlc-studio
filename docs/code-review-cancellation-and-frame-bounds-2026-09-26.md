# Code review — cancellation, frame bounds, and cleanup

This pass fixes cancellation and native frame-size validation errors, consolidates
category mapping, and removes obsolete theme resources. Existing uncommitted work
was preserved; a separate starting snapshot was used to inspect this pass's changes.

Repository-wide builds, analyzers, reference searches, and tests supplemented
manual inspection of operation lifetimes, HTTP and JSON handling, persistence,
playback and chat, native rendering and protocols, and installation/update tooling.

- **Canceled operations could acquire a released slot.** A semaphore release can
  complete a queued wait before its cancellation callback runs. The shared
  `AsyncOperationGate` now checks cancellation after acquiring the slot and returns
  it when rejecting the operation. Tab startup also treats admission closed during
  shutdown as cancellation. The starting suite exposed an `ObjectDisposedException`
  for a queued tab; a new deterministic regression separately reproduced admission
  of canceled work and verifies that the next caller can still acquire the slot.
- **Native frame-size multiplication could overflow.** Dimensions of
  `2470483914 × 3733427279` wrapped their RGBA byte count to `14056792`, passing the
  32 MiB payload check. Validation now bounds the 64-bit pixel count before
  multiplying by four. The native regression failed before the fix and passes
  afterward, alongside zero-dimension, mismatched-size, and boundary cases. The
  bundled plugin was rebuilt and its length, SHA-256, and provenance updated.
- **Category mapping is shared.** Twitch and Kick category lists use one iterator
  and one category reader. Kick categories discovered in live-stream responses use
  the same reader, retaining platform-specific thumbnails, tags, and viewer counts.
- **Unused UI resources were removed.** Reference checks identified six obsolete
  category color/brush declarations in each of eight palettes, two unused main-window
  brushes, and an unused button style: 51 resource declarations in total. WPF
  accessors and overrides invoked by the framework were retained.

Validation:

- The starting managed suite reported 1,259 passes, one failure, and 252 interactive
  skips. After the cancellation fix, all 15 repository-review tests passed.
- Both newly reproduced failures were observed before their corresponding fixes.
- Native subpicture, compositor, TLS, WebSocket, IRC, rendering, and worker-shutdown
  tests pass with compiler warnings treated as errors.
- Two independent native builds produce identical binaries. The unchanged
  controller also matches its previous checksum. The plugin was built with
  WinLibs GCC 16.1.0 MSVCRT and VLC 3.0.23 headers; the downloaded compiler archive
  was verified against the official release SHA-256.
- Development-command and mocked release-publication suites pass. Source Python,
  XML, JSON, and workflow YAML parsing checks pass.
- Final `scripts/dev.ps1 Check` passed with the updated embedded plugin and shared
  mapper: locked restore, PowerShell syntax, formatting, tooling contracts, pinned
  native inputs, and a Release build with zero warnings and errors. The complete
  headless suite passed: **1,261 passed; 252 interactive tests skipped**.
- `git diff --check` passed.

Interactive desktop cases and live-provider end-to-end sessions were not exercised.
