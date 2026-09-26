# Repository review follow-up — 2026-09-26

Reviewed the existing working tree without discarding the pending application,
playback, overlay, or release-workflow changes. Repository-wide validation and
reference/duplication searches covered the managed projects, XAML, native sources,
tests, and PowerShell scripts. Manual inspection focused on playback lifetimes,
VOD history, HTTP limits and cancellation, chat protocol readers, and
installation/publication boundaries.

## Bugs corrected

- **Native TLS handshake stalls:** the handshake loop called `recv` even when
  Schannel had returned another complete buffered handshake record. It now
  processes `SECBUFFER_EXTRA` immediately and reads more only after consuming the
  buffer or receiving `SEC_E_INCOMPLETE_MESSAGE`. Completed handshakes preserve
  any following encrypted application bytes. The original code failed the
  coalesced-record reproduction after one handshake stage and an unnecessary
  second socket read.
- **TLS resource cleanup:** allocated output tokens were leaked on initial
  handshake failure and when their payload length was zero. Token send/free
  handling is shared, and context ownership is recorded only when Schannel
  supplies a valid handle.
- **Dropped fragmented Kick messages:** the native client parsed each WebSocket
  frame independently. It now assembles one bounded UTF-8 text message before
  parsing JSON, including UTF-8 characters split across fragments, and processes
  interleaved ping/pong without losing the pending message. The original reader
  delivered `he` from a fragmented `hello`. Invalid frame sequences and truncated
  messages reconnect without delivering partial text.
- **Invalid WebSocket upgrades:** searching the response for `101` accepted an
  HTTP 200 response with that text in an unrelated header. A full response buffer
  could also be accepted without the terminating headers, and cancellation could
  inspect an uninitialized buffer. Upgrade validation now requires a complete
  HTTP 101 response, the upgrade headers, and the matching accept key; unsolicited
  extensions and protocols are rejected. Keys and outgoing frame masks share an
  OS random-byte helper.
- **Oversized Twitch IRC lines:** resetting a full line buffer let the remainder
  of the same oversized line become a new IRC command. The connection now closes
  at the size limit. The regression drives the production IRC session with an
  oversized prefix followed by a plausible chat command.

The protocol decisions follow [Schannel's extra-buffer contract](https://learn.microsoft.com/en-us/windows/win32/secauthn/extra-buffers-returned-by-schannel)
and [RFC 6455](https://www.rfc-editor.org/rfc/rfc6455).

## Reuse and removal

- Replaced duplicated TLS extra-buffer and token handling with shared helpers.
- Consolidated WebSocket fragmentation and control-frame handling in one text
  reader; removed the old per-frame dispatch and unsupported server-mask path.
- Removed redundant TLS flag set/clear code and an impossible zero-size
  allocation branch.
- Reused the native test build/run loop for the protocol and renderer suites.
- Reference searches found no additional confidently unused C# members. WPF
  attached-property accessors and converter methods were retained because the
  framework invokes them without ordinary C# call sites.
- Rebuilt the bundled controller and updated its pinned length and SHA-256.
  The overlay DLL remains byte-for-byte identical. A second independent build
  produced identical binaries.

## Validation

- Starting baseline: **1,181 passed; 246 interactive tests skipped**.
- Deterministic native TLS, WebSocket, IRC, renderer, and worker-shutdown tests
  pass with compiler warnings treated as errors. The native command also passes
  under Windows PowerShell 5.1 with redirected output.
- Development-command and mocked release-publication suites pass.
- Final `scripts/dev.ps1 Check` passed: locked restore, PowerShell syntax,
  formatting, tooling contracts, pinned native inputs, and a Release build with
  **zero warnings and errors**. The complete headless-safe suite passed:
  **1,181 passed; 246 interactive tests skipped**.
- `git diff --check` passed. Independent native rebuilds produced identical
  plugin and controller binaries.

No live-provider chat messages were sent. Interactive desktop tests were not run.
