# Repository review — 2026-09-26

Reviewed the current working tree, preserving existing uncommitted work. The
review combined repository-wide builds, analyzers, formatting and reference
searches with manual inspection of playback, chat, HTTP, persistence, native
thread lifetimes, and installation/update code.

## Corrections

- **VOD bookmark ordering:** a newer sample at the same playback position was
  discarded without updating its timestamp. A delayed older sample could then
  overwrite that position. History now compares the complete bookmark, retaining
  the latest timestamp in memory and on disk. The regression covers both an
  immediate stale update and one received after reloading the history.
- **Stalled local replay playlists:** the long-VOD timeline adapter requested
  headers with `ResponseHeadersRead` but did not keep the HTTP timeout active
  while reading the body. It now uses the existing `BoundedHttpResponseSender`,
  with coverage for a server that supplies headers and stalls its body.
- **Native overlay shutdown:** individual workers were forcibly terminated, and
  some worker waits were ignored, before shared locks and catalogs were freed.
  Shutdown now waits for all workers together. If they cannot finish within the
  shared deadline, it terminates the standalone controller process before any
  shared-state cleanup. Child-process tests cover both normal completion and a
  worker blocked while holding a lock. The Windows rationale is documented in
  [the native README](../native/chat-overlay/README.md#verification).

## Reuse and cleanup

- Consolidated the identical Kick JSON object readers into
  `JsonElementReader.GetObjectProperty` and removed their local copies.
- Replaced repeated native worker-wait/close blocks with one shutdown helper.
- Removed a permanently disabled native cursor-debugging block.
- Corrected existing C# whitespace and line-ending violations.
- Rebuilt both pinned overlay binaries and updated their manifest hashes. A
  second independent build produced identical binaries.

## Validation

- The starting tree passed 1,179 tests with 246 interactive tests skipped.
- Native overlay shutdown/rendering tests and GDI compositor tests passed.
- Development command and mocked release-publication suites passed.
- Final `scripts/dev.ps1 Check` passed: locked restore, PowerShell
  syntax, formatting, tooling contracts, pinned native inputs, a Release build
  with zero warnings/errors, and the complete headless-safe test suite:
  **1,181 passed; 246 interactive tests skipped**.

Desktop interaction cases require the explicit interactive test mode and were
not run during this review. No release was published or application installed.
