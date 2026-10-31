import clients from '../../shared/oauth-clients.json' with { type: 'json' };

export const contract = Object.freeze({
  clientId: clients.KickClientId,
  redirectUri: 'http://localhost:39177',
  tokenPath: '/v1/kick/token',
  scopes: ['user:read', 'channel:read', 'chat:write'],
});
const allowedFields = new Set(['client_id', 'grant_type', 'code', 'code_verifier', 'redirect_uri', 'refresh_token']);
const headers = { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'Pragma': 'no-cache', 'X-Content-Type-Options': 'nosniff' };
const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers });
const error = (name, status) => json({ error: name }, status);

async function boundedText(body, limit, signal) {
  if (!body) return '';
  const reader = body.getReader();
  const decoder = new TextDecoder('utf-8', { fatal: true });
  let size = 0;
  let text = '';
  const abort = () => { void reader.cancel().catch(() => {}); };
  signal.addEventListener('abort', abort, { once: true });
  try {
    while (true) {
      signal.throwIfAborted();
      const { done, value } = await reader.read();
      signal.throwIfAborted();
      if (done) break;
      size += value.byteLength;
      if (size > limit) throw new RangeError('Body too large');
      text += decoder.decode(value, { stream: true });
    }
    return text + decoder.decode();
  } catch (e) {
    await reader.cancel().catch(() => {});
    throw e;
  } finally {
    signal.removeEventListener('abort', abort);
    reader.releaseLock();
  }
}

function tokenForm(body) {
  // URLSearchParams tolerates malformed escapes, so authenticate only strict form data.
  if (/%(?![0-9a-f]{2})/i.test(body)) return null;
  const form = new URLSearchParams(body);
  const seen = new Set();
  for (const [name, value] of form) {
    if (!allowedFields.has(name) || seen.has(name) || !value || value.length > 4096) return null;
    seen.add(name);
  }
  if (form.get('client_id') !== contract.clientId) return null;
  if (form.get('grant_type') === 'authorization_code') {
    if (seen.size !== 5 || !form.get('code') ||
        form.get('redirect_uri') !== contract.redirectUri ||
        !/^[A-Za-z0-9._~-]{43,128}$/.test(form.get('code_verifier') ?? '')) return null;
  } else if (form.get('grant_type') === 'refresh_token') {
    if (seen.size !== 3 || !form.get('refresh_token')) return null;
  } else return null;
  return form;
}

function readToken(value) {
  if (!value || typeof value !== 'object' ||
      typeof value.access_token !== 'string' || !value.access_token.trim() ||
      typeof value.refresh_token !== 'string' || !value.refresh_token.trim()) return null;
  const seconds = typeof value.expires_in === 'number' ? value.expires_in :
    typeof value.expires_in === 'string' && /^\d+$/.test(value.expires_in) ? Number(value.expires_in) : NaN;
  const scopes = typeof value.scope === 'string' ? value.scope.split(/\s+/) :
    Array.isArray(value.scope) ? value.scope.filter(scope => typeof scope === 'string') : [];
  if (!Number.isSafeInteger(seconds) || seconds <= 0 || !contract.scopes.every(scope => scopes.includes(scope))) return null;
  return { access_token: value.access_token, refresh_token: value.refresh_token, expires_in: seconds,
    scope: scopes.join(' '), token_type: 'Bearer' };
}

export async function handle(request, env, upstream = fetch, timeoutMs = 20_000) {
  const url = new URL(request.url);
  if (url.pathname === '/health' && request.method === 'GET')
    return env.KICK_CLIENT_SECRET && contract.clientId && env.TOKEN_RATE_LIMITER
      ? json({ status: 'ok' }) : error('service_not_configured', 503);
  if (url.pathname !== contract.tokenPath || url.search) return error('not_found', 404);
  if (request.method !== 'POST') return error('method_not_allowed', 405);
  if (!env.KICK_CLIENT_SECRET || !contract.clientId || !env.TOKEN_RATE_LIMITER) return error('service_not_configured', 503);

  const controller = new AbortController();
  const disconnect = () => controller.abort();
  request.signal.addEventListener('abort', disconnect, { once: true });
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    if (request.signal.aborted) controller.abort();
    const limit = await env.TOKEN_RATE_LIMITER.limit({ key: `kick:${request.headers.get('CF-Connecting-IP') ?? 'unknown'}` });
    if (!limit.success) return error('rate_limited', 429);
    if (request.headers.get('Content-Type')?.split(';')[0].trim().toLowerCase() !== 'application/x-www-form-urlencoded')
      return error('invalid_request', 415);
    if (Number(request.headers.get('Content-Length') ?? '0') > 16_384) return error('invalid_request', 413);
    let form;
    try { form = tokenForm(await boundedText(request.body, 16_384, controller.signal)); }
    catch { return error('invalid_request', controller.signal.aborted ? 408 : 400); }
    if (!form) return error('invalid_request', 400);

    form.set('client_secret', env.KICK_CLIENT_SECRET);
    const response = await upstream('https://id.kick.com/oauth/token', {
      method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: form.toString(), redirect: 'manual', signal: controller.signal,
    });
    if (!response.ok) {
      await response.body?.cancel().catch(() => {});
      return error('authorization_failed', [400, 401].includes(response.status) ? 400 : 502);
    }
    const result = readToken(JSON.parse(await boundedText(response.body, 65_536, controller.signal)));
    return result ? json(result) : error('invalid_provider_response', 502);
  } catch {
    return error('connection_service_unavailable', 502);
  } finally {
    clearTimeout(timer);
    request.signal.removeEventListener('abort', disconnect);
  }
}

export default { fetch: (request, env) => handle(request, env) };
