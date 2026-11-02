# Account connection investigation, 2026-11-02

## Status

The root cause is confirmed: both provider registrations had a trailing `/`,
while the shipped desktop app and Kick token service use callback strings without
one. The user removed the trailing slashes from the registrations and reported
that the friend's existing v1.8.9 installation connected successfully. A new
installer and token-service deployment are unnecessary for this fix.

The working registrations are exactly `http://localhost:39178` for Twitch and
`http://localhost:39177` for Kick. Keep the desktop and token service on those
values. A separate browser-launch encoding correction is retained; it is not
claimed as the cause of the reported errors.

## Evidence

- The Twitch screenshot reports `The provided redirect_uri does not match`.
  Kick reports `invalid authorization request`, which does not identify an
  individual invalid parameter. The user subsequently confirmed Brave and
  app v1.8.9. The final visible URLs were `http://localhost:39178/` for Twitch
  and `https://api.kick.com/oauth/authorize` for Kick. These are the final
  callback/error locations, not the initial authorization requests.
- Twitch's callback script intentionally clears its query/fragment before
  displaying the result. A final `/` in its address bar therefore neither
  recovers the original `redirect_uri` parameter nor establishes a mismatch
  involving a trailing slash.
- GitHub's latest release when checked was v1.8.9, published 2026-10-31 UTC,
  built from commit `f81d52552a94b96c70c55f111b9433149d93deb6`.
- The actual downloaded release ZIP has SHA-256
  `01818a0e2ad049dace801484ded96576d02352a232ba3a318f16b57572d65954`,
  matching both the GitHub asset digest and release metadata. The executable's
  compressed .NET bundle was inspected without launching it. Its embedded public
  configuration and callback constants match the tagged source:

  | Provider | Client ID | Exact callback string |
  | --- | --- | --- |
  | Twitch | `gl9psxrunyx92fxbwh3sedjfe4wgr1` | `http://localhost:39178` |
  | Kick | `01KSRPMC6B2H8FSGJZTHND0KDN` | `http://localhost:39177` |

- The embedded Kick token service is
  `https://stream-studio-accounts.stream-studio-account-service.workers.dev/v1/kick/token`.
  Its live `/health` endpoint returned HTTP 200 with `{"status":"ok"}`.
  This verifies service availability, not user consent or token exchange.
- Unauthenticated authorization requests with the shipped public IDs and fresh
  state/PKCE values reached provider login responses. Both callback spellings
  (with and without a trailing slash) reached login. This does **not** establish
  which spelling is registered or whether authorization succeeds after login.
- Existing local evidence records successful publisher-account consent on
  2026-10-21. That historical result does not prove that another account can
  connect now.
- Browser inspection was unavailable: the computer-use tool stopped because it
  could not verify whether the current browser URL was allowed. No registration
  settings were read or changed through that browser.
- The user supplied the saved developer registration values, ending in `/`, and
  then confirmed that removing those slashes fixed the failures. That live result
  establishes the root cause; it is stronger evidence than the earlier
  unauthenticated login-page probes.

## Verified code change

Both OAuth services previously passed `authorizationUri.ToString()` to Windows.
That display representation changes encoded scope separators (`%20`) into
literal spaces. Both services now pass `AbsoluteUri`, preserving the encoded URL
at the browser-launch boundary. The redirect strings, client selection, scopes,
state validation, PKCE, and token-service routing are unchanged. The setup wizard
now reads its displayed/copied callback URLs from those production constants.

The two regression tests inspect the same `ProcessStartInfo` builders used by the
production sign-in paths. They check encoding and the exact decoded parameters
for both publisher and personal registrations. Each test failed with the old
`ToString()` behavior and passed after the correction.

## Validation

- Release build with SDK 10.0.302: zero warnings and errors.
- Account connection suite: 19 passed, none skipped.
- Credential protection and OAuth callback suite: 11 passed, none skipped.
- Browser callback JavaScript and Cloudflare Worker suites: 21 passed.
- The changed files pass `git diff --check`. A separate `dotnet format`
  verification was stopped while still processing; no formatting result is claimed.
- The initial restricted account-suite run could not use Windows DPAPI for its
  temporary persistence fixture. Repeating with Windows-user access passed all
  19 tests, including protected persistence.

The affected user's successful connection after the registration correction was
reported by the user. No new independent claim about that user's subsequent
token refresh or chat activity is made.

## Regression coverage

The browser-launch tests pin each exact callback string. Both token-service
implementations now have explicit checks that the slashless callback is forwarded
unchanged and that the slash-suffixed variant is rejected. Existing tests cover
managed Kick refresh and protected token persistence. No production service code,
client IDs, or secrets need to change.

## Provider references

- [Twitch OAuth flows](https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/)
- [Kick OAuth 2.1](https://docs.kick.com/getting-started/generating-tokens-oauth2-flow)
