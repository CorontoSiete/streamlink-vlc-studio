# OAuth callbacks and replay endpoint validation

Twitch's browser page now relays the returned access token in a form-encoded POST
to `/twitch-oauth-token`. It clears the browser URL before sending the request,
and never places the token in the relay's query string. The receiver requires
POST on that route, rejects query parameters, limits the body to 16 KiB even
without Content-Length, decodes UTF-8 strictly, and bounds body reads to three
seconds. Existing duplicate-field and OAuth state checks still apply. Kick's
provider callback continues to use GET.

Callback responses disable caching and referrers, disallow framing, and send a
Content Security Policy that blocks external content. Only the Twitch capture
page permits a script, using a fresh cryptographic nonce, and same-origin fetch.
It displays the acknowledgement as text without `document.write`. These changes
reduce local token exposure; Twitch still returns its initial token in a URL
fragment through its existing implicit authorization flow.

Replay DNS validation now restricts native IPv6 destinations to global unicast
addresses outside IETF protocol assignments, 6to4, and documentation ranges.
It also rejects scoped addresses. Standard DNS64/NAT64 addresses remain usable
when their embedded IPv4 destination passes the IPv4 public-address checks.
Private IPv4 addresses embedded in mapped or translated IPv6 addresses are
rejected. The existing provider hostname allowlist and rejection of DNS answers
containing any nonpublic address remain in effect. This change does not bind
the HTTP transport's connection to the previously validated DNS answer.

Regression coverage includes callback methods and headers, declared and streamed
body limits, invalid encoding and state, upload cancellation, IPv6 range
boundaries, and a WebView2 browser fixture with an isolated profile and synthetic
credentials. The browser fixture intercepts all requests locally and checks that
the nonce-protected relay works, removes the fragment, sends POST without a
Referer, and blocks an injected script. It does not contact Twitch or use a real
account.

The Windows HTTP.sys integration cases require a working `HttpListener`. In the
restricted development environment, `HttpListener.Start()` fails with Windows
error 6 (invalid handle), including in the pre-existing OAuth receiver test.
WebView2 browser checks are also blocked in this environment: the new browser
fixture reports `GpuProcessExited`, including with `--disable-gpu`, and the
pre-existing Kick browser fixture times out. The independent parser, IPv6, and Node.js relay checks can run here;
the real browser policy and HTTP.sys integration remain unverified here.
Those integration cases must also be run in a Windows environment where HTTP.sys
is available:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 Test -Filter security
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 Test -Filter 'loopback OAuth receiver' -NoBuild
node --test scripts/tests/oauth-relay.tests.mjs
```

Validation on this change: the Release solution build passed with zero warnings
and zero errors; seven selected .NET security checks and all five Node.js relay
checks passed. Four .NET integration cases failed at the environment boundaries
described above. Whitespace checks passed. The complete application suite was
not run.

References: [OAuth security guidance on token leakage](https://www.rfc-editor.org/rfc/rfc9700.html#section-4.2)
and [IANA's IPv6 special-purpose registry](https://www.iana.org/assignments/iana-ipv6-special-registry).
