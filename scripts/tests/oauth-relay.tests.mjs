import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { runInNewContext } from 'node:vm';

// Exercise the JavaScript shipped in the C# page without a Windows HTTP.sys or
// WebView2 dependency. Browser CSP enforcement is covered by the .NET fixture.
const source = await readFile(new URL('../../src/StreamlinkVlcStudio.Infrastructure/Chat/TwitchOAuthService.cs', import.meta.url), 'utf8');
const script = source.match(/<script nonce="\{\{scriptNonce\}\}">([\s\S]*?)<\/script>/)?.[1];
assert.ok(script, 'The production OAuth capture script must be present');

async function relay(hash, search, failure = false) {
  const calls = [];
  const status = { textContent: 'Finishing authorization...' };
  const location = { hash, search };
  const sandbox = {
    window: { location, history: { replaceState(_state, _title, path) {
      assert.equal(path, '/');
      location.hash = '';
      location.search = '';
      calls.push({ kind: 'clear-url' });
    } } },
    document: { getElementById(id) { assert.equal(id, 'status'); return status; } },
    async fetch(url, options) {
      assert.equal(location.hash, '');
      assert.equal(location.search, '');
      calls.push({ kind: 'fetch', url, ...options });
      if (failure) throw new Error('Connection closed');
      return { async text() { return '<p>Authorization result</p>'; } };
    },
    DOMParser: class {
      parseFromString(html, type) {
        assert.equal(html, '<p>Authorization result</p>');
        assert.equal(type, 'text/html');
        return { querySelector(selector) { assert.equal(selector, 'p'); return { textContent: 'Authorization result' }; } };
      }
    }
  };
  await runInNewContext(script, sandbox, { timeout: 1000 });
  return { calls, status: status.textContent };
}

for (const [name, hash, search, body] of [
  ['successful fragment', '#state=expected&access_token=private-token', '', 'state=expected&access_token=private-token'],
  ['denied consent', '', '?state=expected&error=access_denied', 'state=expected&error=access_denied'],
  ['fragment precedence', '#state=expected&access_token=private-token', '?state=wrong', 'state=expected&access_token=private-token']
]) {
  test(`${name} clears the URL before posting form data`, async () => {
    const result = await relay(hash, search);
    assert.equal(result.status, 'Authorization result');
    assert.deepEqual(result.calls.map(call => call.kind), ['clear-url', 'fetch']);
    const request = result.calls[1];
    assert.equal(request.url, '/twitch-oauth-token');
    assert.equal(request.method, 'POST');
    assert.equal(request.body, body);
    assert.equal(request.cache, 'no-store');
    assert.equal(request.referrerPolicy, 'no-referrer');
    assert.equal(request.headers['Content-Type'], 'application/x-www-form-urlencoded');
  });
}

test('an empty callback clears the URL and sends no request', async () => {
  const result = await relay('', '');
  assert.deepEqual(result.calls.map(call => call.kind), ['clear-url']);
  assert.equal(result.status, 'Twitch did not return an OAuth token.');
});

test('a failed relay displays the error as text', async () => {
  const result = await relay('#state=expected&access_token=private-token', '', true);
  assert.equal(result.status, 'Twitch authorization failed: Error: Connection closed');
});
