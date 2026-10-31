# Kick chat reconnect diagnosis — 2026-10-21

The reported blueface overlay statuses were "Kick chat input ready" and "Kick chat reconnecting...". These strings identify the standalone native controller, not the managed docked-chat client. Both implementations compiled the same Pusher application key and region into their connections. The native controller also discarded Pusher error events and WebSocket close details, then retried the same endpoint.

This is a confirmed recovery defect: when that application is retired or reassigned, reconnecting cannot discover its replacement. A regression against the original native handler failed because a Pusher 4003 rejection never reached the visible status. The test uses a scripted rejection; it does not establish that blueface returned 4003 during this session.

Current anonymous negotiation is described by the [Kick integration implementation](https://github.com/75ohmantenna/chat-downloader-fork/blob/master/src/chat_downloader/sites/kick/realtime_connection.py) and its [live transport guide](https://github.com/75ohmantenna/chat-downloader-fork/blob/master/docs/kick-integration-guide.md). A [recent independent client release](https://github.com/JRBlaze/FriendlyChatExtension#new-in-1239) also reports the retired-application reconnect loop. [Pusher's protocol](https://pusher.com/docs/channels/library_auth_reference/pusher-websockets-protocol/) defines rejection codes, connection acknowledgement and ping/pong behavior.

## Changes

Both chat clients now POST an anonymous reader identity and Pusher-only capability to `https://web.kick.com/api/v1/realtime/channels/{channel_id}/chat/connection` on every connection. They validate the returned application key and cluster and construct the Pusher URL from those fields. The channel ID is retained separately from the chatroom and broadcaster IDs. Discovery does not receive account credentials and redirects are disabled.

Native chat waits for Pusher's connection acknowledgement before subscribing. Rejection codes/messages and server close details are recorded, and the visible overlay exposes them. Existing chat parsing, control-frame ping/pong and rendering remain covered by the native regressions. The bundled controller is rebuilt and its manifest length/hash updated, so application builds contain the change.

## Verification

- The original native rejection regression failed; the corrected handler passes.
- Native network tests pass, including a complete production session that receives a rejection, negotiates a changed application and region, subscribes after acknowledgement, answers a ping and delivers one live fixture message. HTTP failures, unsupported providers, malformed endpoints, cancellation and separate channel/chatroom identities are also covered.
- Native TLS handshake, rendering/readability and render-resource tests pass. Controller compilation uses warnings as errors, static compiler support and timestamp-free PE output. Its imports contain only Windows system DLLs.
- The application and test assembly build successfully with zero warnings/errors, including verification of the updated bundled controller hash.
- The existing embedded-bundle extraction test also passes, including controller extraction and current-hash validation in a writable temporary directory.
- All 20 focused managed checks pass: discovery, cancellation, channel identity, Pusher errors, handshake, supervisor cleanup/retries, remote disconnect recovery and recent history.
- The broad Kick run before the final cancellation fix reported 231 passed, 5 failed, 1 timed out and 46 not run. One failure exposed cancellation being checked too late when a test HTTP handler returned a response after cancellation; this is corrected and retested separately. The other failures concern badge extraction, DPAPI settings encryption and WebView2 follow/clip fixtures. See `.tmp/kick-chat-reconnect/managed-kick.log` for the complete unfiltered result. The full suite is not certified green.

## Live verification limit

The sandbox denied access to the installed app's log folder, and a direct Pusher probe failed with Windows socket error 10013 (access forbidden). Kick's channel endpoint was also unreachable from the command environment. Therefore the exact server-side cause of this user's blueface disconnects, the live discovery response and a sustained blueface connection have not been observed here. The code and deterministic transport fixes are verified; a live run of the rebuilt app remains necessary to confirm the user's symptom is resolved. No account reconnection or credential edits are required by this change.
