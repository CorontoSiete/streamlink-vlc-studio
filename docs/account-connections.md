# Browser account connections

First-run setup and Settings > Accounts offer **Connect Twitch** and **Connect Kick**. Users sign in and approve access in their system browser. Account tokens are saved with the existing Windows DPAPI protection. Manual developer-app credentials remain in collapsed Advanced sections for existing installations and developers.

## Current deployment status

`shared/oauth-clients.json` contains the registered public client IDs recovered from this Windows account's existing Stream Studio settings. It contains no secret or account token. The Twitch ID was checked against Twitch's token validation endpoint. The Kick registration was checked with its client-credentials endpoint. The Kick service is deployed at `https://stream-studio-accounts.stream-studio-account-service.workers.dev/v1/kick/token`, and the verified URL is configured in the desktop build. Cloudflare holds the app secret in an encrypted Worker secret binding. HTTPS health and rejected-grant checks passed with normal certificate validation. During deployment, the selected Mullvad server's DNS did not resolve the newly created hostname while public DNS did. Switching VPN servers resolved that failure; normal system DNS and HTTPS checks then passed. A fresh setup with empty account credentials connected both Twitch and Kick successfully. The saved Kick connection contains a refresh token and no client secret; live refresh, saving the rotated token, and profile lookup all passed.

## Publisher configuration (once per app, not per user)

The registered redirect strings below must have **no trailing slash**. A registration
ending in `/` does not match the callback sent by v1.8.9. Removing that slash from
both provider registrations resolved the reported sign-in failures on another
user's v1.8.9 installation on 2026-11-02. The browser can display `/` on the final
localhost page; that is separate from the exact OAuth parameter being registered.

For the hosted setup without a container or paid server, use the prepared [Cloudflare Worker and provisioning command](../hosting/cloudflare/README.md). The ASP.NET Core deployment below is available for publishers who already operate a .NET host. Both implementations use the same desktop token-service contract.

1. Keep the Twitch registration's redirect URI exactly `http://localhost:39178`. The existing implicit browser flow requests `chat:read chat:edit user:read:follows channel:manage:predictions clips:edit`, validates the resulting token and its issuing client ID, and saves the login and client ID automatically. This registration must support implicit authorization; Twitch Public applications support only Device Code Flow. This implementation preserves the app's existing implicit flow and its reconnect-on-expiry behavior. It does not claim automatic Twitch token renewal.
2. Keep the Kick registration's redirect URI exactly `http://localhost:39177`. Authorization uses a fresh random state and PKCE S256 on every attempt. The requested scopes are `user:read channel:read chat:write`.
3. Deploy `src/StreamlinkVlcStudio.AccountService` using the .NET 10 ASP.NET Core runtime. For example, publish with `dotnet publish src/StreamlinkVlcStudio.AccountService -c Release -o artifacts/account-service`. This is a separate server application; never include its secret configuration in the desktop installer.
4. In the hosting provider's secret store, configure `Kick__ClientId` to match `shared/oauth-clients.json` and `Kick__ClientSecret` to the registered Kick application secret. Do not commit these values to an appsettings file. Locally, .NET user-secrets is supported by the service project's `UserSecretsId`.
5. Put the service behind a stable HTTPS endpoint, forwarding to its private Kestrel listener. Expose `POST /v1/kick/token` and `GET /health`. Set `KickTokenServiceUrl` in `shared/oauth-clients.json` to the full HTTPS token endpoint, then rebuild the desktop app. The URL is embedded in the desktop assembly and is never taken from a browser callback or personal settings. Loopback URLs, HTTP, URL credentials, query strings, and fragments are rejected.
6. Verify the external health endpoint, then test both Connect buttons from a fresh Windows profile. Complete platform consent, restart the app, and verify Twitch follows/chat and Kick chat plus automatic refresh. The account owner completed the live browser consent during verification.

The service's IP rate limit is 30 token requests per minute. If a reverse proxy sends all connections from one IP, enforce the per-client limit at the edge and configure trusted proxy forwarding before increasing capacity. Do not trust arbitrary forwarded-IP headers. Configure edge body limits to 16 KiB and disable request/response body logging and token caching. The application already bounds form fields, upstream responses (64 KiB), request duration (20 seconds), and upstream redirects.

## Token-service contract

The desktop sends `application/x-www-form-urlencoded` POST bodies:

| Grant | Fields |
| --- | --- |
| `authorization_code` | `client_id`, `grant_type`, `code`, `code_verifier`, `redirect_uri` |
| `refresh_token` | `client_id`, `grant_type`, `refresh_token` |

The service pins the registered client ID, localhost callback, and Kick token endpoint. It adds the client secret only to the server-to-server request. It accepts no client-credentials grant, arbitrary redirect, client secret from the caller, duplicate fields, or extra fields. It returns only `access_token`, `refresh_token`, `expires_in`, `scope`, and `token_type`, with `Cache-Control: no-store`. Errors are generic and never echo upstream bodies. The service does not store users' tokens or maintain login sessions.

Kick connections record whether they use this service. Refresh uses the same route; shared refresh coordination and protection against stale results after disconnect remain active. Existing personal client IDs/secrets keep their original direct flow. Managed tokens require all three scopes, including `channel:read`, because no app token or shared secret is available on the desktop for broadcaster lookup.

## Provider documentation checked

- [Twitch OAuth flows](https://dev.twitch.tv/docs/authentication/getting-tokens-oauth): client-side implicit authorization; public-client Device Code Flow requirements.
- [Kick OAuth 2.1](https://docs.kick.com/getting-started/generating-tokens-oauth2-flow): PKCE, required client secret on code exchange and refresh, localhost callback guidance.

Kick's documented secret requirement is why a hosted component is necessary. Shipping the secret in the executable or borrowing another application's client registration would not implement this correctly.

## Verification

- Desktop/test build: zero warnings and errors.
- 17 account-connection tests and 12 focused existing authentication/setup regressions passed.
- 16 Cloudflare Worker tests, Wrangler deployment bundling, and package vulnerability audit passed.
- Live HTTPS health, unsupported-grant rejection, fresh browser connection on both platforms, protected settings reload, and Kick token refresh/profile lookup passed.
- The private test profile and logs remain under ignored `.artifacts/account-setup-live`; no user tokens or client secrets are included in changed source/configuration or Wrangler deployment logs.
