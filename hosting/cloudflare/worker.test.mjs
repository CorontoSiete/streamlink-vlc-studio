import test from 'node:test';
import assert from 'node:assert/strict';
import worker, { handle, contract } from './worker.mjs';

const env = () => ({ KICK_CLIENT_SECRET: 'server-only-secret', TOKEN_RATE_LIMITER: { limit: async () => ({ success: true }) } });
const form = (grant = 'refresh_token') => new URLSearchParams(grant === 'refresh_token'
  ? { grant_type: grant, client_id: contract.clientId, refresh_token: 'private-refresh' }
  : { grant_type: grant, client_id: contract.clientId, code: 'code', code_verifier: 'x'.repeat(43), redirect_uri: contract.redirectUri });
const request = (body = form(), extras = {}) => new Request(`https://accounts.example.com${contract.tokenPath}`, {
  method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'CF-Connecting-IP': '192.0.2.1' }, body, ...extras,
});
const token = (overrides = {}) => Response.json({ access_token: 'access', refresh_token: 'rotated', expires_in: 3600,
  scope: contract.scopes.join(' '), token_type: 'Bearer', client_secret: 'server-only-secret', ...overrides });

test('Worker entry point accepts the runtime execution context', async () => {
  const response = await worker.fetch(new Request('https://accounts.example.com/health'), env(), { waitUntil() {} });
  assert.equal(response.status, 200);
});
test('health fails until the secret and limiter are provisioned', async () => {
  const response = await handle(new Request('https://accounts.example.com/health'), {});
  assert.equal(response.status, 503);
  assert.equal(response.headers.get('Cache-Control'), 'no-store');
});
for (const grant of ['authorization_code', 'refresh_token']) {
  test(`${grant} pins the provider and adds the secret only upstream`, async () => {
    const response = await handle(request(form(grant)), env(), async (url, options) => {
      assert.equal(url, 'https://id.kick.com/oauth/token');
      assert.equal(options.redirect, 'manual');
      const sent = new URLSearchParams(options.body);
      assert.equal(sent.get('client_secret'), 'server-only-secret');
      assert.equal(sent.get('grant_type'), grant);
      if (grant === 'authorization_code') assert.equal(sent.get('redirect_uri'), 'http://localhost:39177');
      return token();
    });
    assert.equal(response.status, 200);
    assert.equal(response.headers.get('Cache-Control'), 'no-store');
    const value = await response.json();
    assert.equal(value.refresh_token, 'rotated');
    assert.equal('client_secret' in value, false);
    assert.deepEqual(Object.keys(value).sort(), ['access_token', 'expires_in', 'refresh_token', 'scope', 'token_type']);
  });
}
test('invalid clients, injected secrets, grant types, duplicates, escapes and callbacks never reach Kick', async () => {
  const invalid = [
    new URLSearchParams({ grant_type: 'refresh_token', client_id: 'other', refresh_token: 'refresh' }),
    new URLSearchParams({ grant_type: 'client_credentials', client_id: contract.clientId }),
    new URLSearchParams({ ...Object.fromEntries(form()), client_secret: 'injected' }),
    new URLSearchParams({ ...Object.fromEntries(form('authorization_code')), code_verifier: 'short' }),
    new URLSearchParams({ ...Object.fromEntries(form('authorization_code')), redirect_uri: 'https://attacker.example' }),
    new URLSearchParams({ ...Object.fromEntries(form('authorization_code')), redirect_uri: 'http://localhost:39177/' }),
    `${form()}&refresh_token=duplicate`,
    `${form()}&invalid=%QZ`,
  ];
  for (const body of invalid) {
    const response = await handle(request(body), env(), () => { throw new Error('Unexpected upstream call'); });
    assert.equal(response.status, 400);
  }
});
test('request size is enforced on the stream without trusting Content-Length', async () => {
  const response = await handle(request('x'.repeat(16_385)), env(), () => { throw new Error('Unexpected upstream call'); });
  assert.equal(response.status, 400);
});
test('unsupported content and methods are rejected', async () => {
  assert.equal((await handle(request('{}', { headers: { 'Content-Type': 'application/json' } }), env())).status, 415);
  assert.equal((await handle(new Request(`https://accounts.example.com${contract.tokenPath}`), env())).status, 405);
});
test('rate limiting uses the Cloudflare connection IP and fails closed', async () => {
  const configuration = env();
  configuration.TOKEN_RATE_LIMITER.limit = async ({ key }) => {
    assert.equal(key, 'kick:192.0.2.1');
    return { success: false };
  };
  assert.equal((await handle(request(), configuration)).status, 429);
});
test('provider errors never echo secret material', async () => {
  const response = await handle(request(), env(), async () => new Response('server-only-secret private-refresh', { status: 400 }));
  assert.equal(response.status, 400);
  assert.equal((await response.text()).includes('private-refresh'), false);
});
test('incomplete scopes and missing refresh tokens fail validation', async () => {
  for (const overrides of [{ scope: 'chat:write' }, { refresh_token: '' }, { expires_in: 0 }, { expires_in: true }, { expires_in: null }]) {
    const response = await handle(request(), env(), async () => token(overrides));
    assert.equal(response.status, 502);
  }
});
test('provider response size is bounded', async () => {
  const response = await handle(request(), env(), async () => new Response('x'.repeat(65_537)));
  assert.equal(response.status, 502);
});
test('provider network and redirect failures are generic', async () => {
  const redirected = await handle(request(), env(), async () => new Response('', { status: 302, headers: { Location: 'https://attacker.example' } }));
  assert.equal(redirected.status, 502);
  const failed = await handle(request(), env(), async () => { throw new Error('contains server-only-secret'); });
  assert.equal(failed.status, 502);
  assert.equal((await failed.text()).includes('server-only-secret'), false);
});
test('the deadline aborts an upstream response that never arrives', async () => {
  let aborted = false;
  const response = await handle(request(), env(), (_, { signal }) => new Promise((_, reject) => {
    signal.addEventListener('abort', () => { aborted = true; reject(signal.reason); }, { once: true });
  }), 20);
  assert.equal(response.status, 502);
  assert.equal(aborted, true);
});
test('the deadline bounds a stalled upstream response body', async () => {
  const response = await handle(request(), env(), async () => new Response(new ReadableStream({ start() {} })), 20);
  assert.equal(response.status, 502);
});
test('the deadline bounds a stalled request body', async () => {
  const incoming = request(new ReadableStream({ start() {} }), { duplex: 'half' });
  const response = await handle(incoming, env(), undefined, 20);
  assert.equal(response.status, 408);
});
test('caller cancellation aborts provider work', async () => {
  const controller = new AbortController();
  const response = await handle(request(form(), { signal: controller.signal }), env(), async (_, { signal }) => {
    controller.abort();
    assert.equal(signal.aborted, true);
    signal.throwIfAborted();
  });
  assert.equal(response.status, 502);
});
