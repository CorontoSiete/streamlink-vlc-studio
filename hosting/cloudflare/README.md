# Stream Studio account hosting

This Worker implements the same `/v1/kick/token` contract as the optional ASP.NET Core service. It runs without a database, container, custom domain, or paid upgrade. The desktop continues to use Twitch directly and calls this service only for Kick code exchange and refresh.

The Free Workers plan currently includes 100,000 requests/day and 10 ms CPU/request. Waiting for Kick's HTTP response does not count as CPU time. Stay on Free during setup; no billing plan change is needed for this service. Limits and account terms remain Cloudflare's: [pricing](https://developers.cloudflare.com/workers/platform/pricing/).

## One-time publisher setup

From `hosting/cloudflare`, with Node 22 or newer:

```powershell
npm ci
npm test
npm run check
npm run login -- --use-keyring --scopes account:read user:read workers:write workers_scripts:write
```

Complete the Cloudflare sign-in/registration and Wrangler consent in your own browser. Wrangler requests only account/user read access and Worker deployment access, plus its automatic refresh permission. On Windows, `--use-keyring` stores its credentials with the OS credential store; Wrangler may offer to install its official keyring backend. No Cloudflare API token needs to be copied into this repository or the desktop app.

Then, from the repository root, in PowerShell 7:

```powershell
pwsh -NoProfile -File scripts/deploy-account-worker.ps1
```

The provisioning script deploys the Worker to your account, reads the Kick app secret from your existing DPAPI-protected Stream Studio settings, and sends **only that app secret** to Cloudflare's encrypted `KICK_CLIENT_SECRET` binding using a private stdin pipe. No user access token, refresh token, or browser cookie is uploaded during provisioning. It verifies the actual deployed HTTPS endpoint and only then updates `shared/oauth-clients.json`. Rebuild the desktop app afterward. It does not select a paid plan or generate a temporary hosting account.

For a newly registered Workers subdomain, DNS and certificate activation may take time. If HTTPS verification fails after deployment, retain the printed real origin and resume later with `pwsh -NoProfile -File scripts/deploy-account-worker.ps1 -VerifyServiceUrl 'https://<worker>.<account-subdomain>.workers.dev'`. That verifies and configures the existing deployment without uploading code or secrets again. Certificate validation is always required.

The public client ID in `shared/oauth-clients.json` must match your saved Kick registration. Its redirect URL must remain exactly `http://localhost:39177`, with no trailing slash; both the desktop and this service pin that exact value. The desktop receives the code and holds the PKCE verifier. The hosted service never receives a platform password.

If your Cloudflare login belongs to multiple accounts, set `CLOUDFLARE_ACCOUNT_ID` for the provisioning command to the intended account's ID shown by `wrangler whoami`. Do not choose an account by list position.

## Validation and operation

- `GET /health` returns 200 only when the secret and rate limiter are present.
- `POST /v1/kick/token` accepts authorization-code and refresh grants for the pinned registration only.
- The Worker enforces a 16 KiB request bound, 64 KiB response bound, 20-second deadline, no upstream redirects, no-store responses, strict form validation, and complete renewable scopes.
- Cloudflare's rate-limit binding allows 30 token requests/minute per connecting IP within each Cloudflare location. Its counters are intentionally not a global exact quota. Worker logs and preview URLs are disabled; the code never logs requests, credentials, or tokens.
- `npm test` covers the wire contract, secret filtering, invalid requests, body bounds, timeouts, disconnects, and rate-limit behavior. `npm run check` validates and bundles with the pinned Wrangler version. A local `wrangler dev --local` smoke check verifies the actual Workers runtime.
- After deployment, complete Connect Kick in a clean desktop profile, restart the app, and verify chat and refresh. Provider consent must be completed by the account owner.

Never put `KICK_CLIENT_SECRET` under `vars` in `wrangler.jsonc`. Rotate it using Cloudflare's secret binding and the matching Kick developer registration if necessary. Keep the public client ID and service URL in version control so desktop builds are reproducible.

References: [Worker secrets](https://developers.cloudflare.com/workers/configuration/secrets/), [rate-limit bindings](https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/), [Kick OAuth requirements](https://docs.kick.com/getting-started/generating-tokens-oauth2-flow).
