# Security improvements — 2026-10-31

The source review found no confirmed vulnerability in the inspected boundaries. It covered LAN authentication, browser origins, session ownership, media proxy destinations, credentials, local files, process arguments and representative update/native paths. Coverage was partial; deployment settings and the complete application were not audited.

## Changes

- Diagnostic redaction now covers passwords, session cookies, API/CSRF/identity tokens and the app's account-secret field names. Complete Cookie, Set-Cookie and authorization headers are removed, including Basic authentication. Redaction runs before line-break escaping so separate diagnostic lines remain usable. Quoted values retain the existing handling of spaces, escaped quotes and incomplete values. Log files and emitted source/message fields are covered.
- Managed overlay connections and the replay event listener require the current Windows user through `PipeOptions.CurrentUserOnly`. Windows also checks elevation. Native overlay packets and rendering remain unchanged.
- Kick's curl fallback ignores user curl configuration, permits only HTTPS for initial requests and redirects, limits redirects to five, disables URL glob expansion and passes its destination with `--url`. Environment-based proxy settings and explicit executable selection retain their existing behavior.

## Verification

- The two new regressions failed against the previous implementation: password text remained visible and curl accepted a synthetic local file URL. Both pass after the changes. The curl check uses the real Windows curl with synthetic files and curl configuration; it makes no Internet request.
- Release solution build: zero warnings and errors.
- Final targeted runs: **26 passed, zero skipped** — 11 credential/sign-in checks, six native input checks, three replay event checks, three logger checks, public-address validation, Kick request construction and actual overlay frame transport/rendering.
- Existing OAuth listener/browser checks required normal Windows access. A replay lifecycle check required the test runner's interactive mode; the final runs above include those successful checks.
- Formatting and whitespace validation apply to the changed files. Live provider playback, a second Windows identity and the full application suite were not exercised.

The security review preserves the existing workspace changes. These additions are preventive safeguards; they do not establish that a remote exploit existed before the change.
