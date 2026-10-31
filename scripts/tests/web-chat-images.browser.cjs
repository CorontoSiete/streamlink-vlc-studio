'use strict';
// Production chat renderer, real browser decoding, captured Twitch PNGs and controlled CDN failures.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const root = path.resolve(__dirname, '../..');
const assets = path.join(root, 'src/StreamlinkVlcStudio.Infrastructure/LocalWeb/Assets');
const fixtures = path.join(root, 'tests/StreamlinkVlcStudio.Tests/Fixtures/twitch-badges');
const output = path.join(root, '.artifacts/twitch-badges', process.env.SVS_BADGE_TEST_LABEL || 'browser');
fs.mkdirSync(output, { recursive: true });
const browser = process.env.SVS_WEB_TEST_BROWSER || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const source = fs.readFileSync(process.env.SVS_BADGE_TEST_SOURCE || path.join(assets, 'app.js'), 'utf8');
assert.ok(source.includes('  restoreSession();'));
// Replace authentication startup only; exercise the unchanged production renderer in the real DOM.
const testSource = source.replace('  restoreSession();', '  window.badgeTest = { renderChatRow, chatImage };');
const server = http.createServer((req, res) => {
  const name = req.url === '/' ? 'index.html' : req.url.slice(1);
  if (!['index.html', 'app.js', 'app.css', 'hls.min.js'].includes(name)) { res.writeHead(404).end(); return; }
  res.setHeader('Content-Type', name.endsWith('.js') ? 'text/javascript' : name.endsWith('.css') ? 'text/css' : 'text/html');
  res.end(name === 'app.js' ? testSource : fs.readFileSync(path.join(assets, name)));
});
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function main() {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const profile = path.join(output, 'profile-' + Date.now());
  const child = spawn(browser, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  let errors = '', ws;
  child.stderr.on('data', data => errors += data);
  const deadline = setTimeout(() => { child.kill(); server.close(); console.error('Badge browser test timed out.'); process.exit(1); }, 55000);
  try {
    let port;
    for (let n = 0; n < 100; n++) {
      try { port = fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]; if (port) break; } catch {}
      await sleep(100);
    }
    assert.ok(port, errors);
    const pages = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json();
    ws = new WebSocket(pages.find(page => page.type === 'page').webSocketDebuggerUrl);
    await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', reject, { once: true }); });
    let id = 0; const pending = new Map(), requests = new Map();
    ws.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (message.id) {
        const p = pending.get(message.id); pending.delete(message.id);
        if (p) message.error ? p.reject(new Error(JSON.stringify(message.error))) : p.resolve(message.result);
      }
      if (message.method === 'Fetch.requestPaused') {
        const { requestId, request } = message.params, key = new URL(request.url).pathname.replace('/stream-studio-badge-fixture', '');
        const count = (requests.get(key) || 0) + 1; requests.set(key, count);
        const fail = key === '/permanent' || key.startsWith('/removed') || key.startsWith('/transient') && count === 1;
        cdp('Fetch.fulfillRequest', { requestId, responseCode: fail ? 503 : 200,
          responseHeaders: [{ name: 'Content-Type', value: 'image/png' }, { name: 'Cache-Control', value: 'no-store' }],
          body: fail ? '' : fs.readFileSync(path.join(fixtures, 'alinity-' + (key.endsWith('84') ? '84' : '0') + '.png')).toString('base64') }).catch(console.error);
      }
    });
    const cdp = (method, params = {}) => new Promise((resolve, reject) => {
      const key = ++id, timer = setTimeout(() => reject(new Error(method + ' timed out')), 10000);
      pending.set(key, { resolve: value => { clearTimeout(timer); resolve(value); }, reject: error => { clearTimeout(timer); reject(error); } });
      ws.send(JSON.stringify({ id: key, method, params }));
    });
    const evaluate = async expression => {
      const r = await cdp('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
      if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails));
      return r.result.value;
    };
    const wait = async (expression, timeout = 5000) => {
      const until = Date.now() + timeout;
      do { if (await evaluate(expression)) return; await sleep(40); } while (Date.now() < until);
      throw new Error('Badge condition timed out: ' + expression + '; requests=' + JSON.stringify([...requests]) + '; DOM=' + await evaluate('window.rows?.outerHTML'));
    };
    const capture = async name => {
      const shot = await cdp('Page.captureScreenshot', { format: 'png' });
      fs.writeFileSync(path.join(output, name + '.png'), Buffer.from(shot.data, 'base64'));
    };
    await cdp('Fetch.enable', { patterns: [{ urlPattern: 'https://static-cdn.jtvnw.net/stream-studio-badge-fixture/*', requestStage: 'Request' }] });
    await cdp('Network.enable'); await cdp('Network.setCacheDisabled', { cacheDisabled: true });
    await cdp('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1200, deviceScaleFactor: 1, mobile: false });
    await cdp('Page.enable'); await cdp('Page.navigate', { url: 'http://127.0.0.1:' + server.address().port });
    await wait('!!window.badgeTest');
    await evaluate(`window.rows=document.createElement('section');rows.style.cssText='background:#18181b;color:white;padding:20px;font-size:18px;display:grid;grid-template-columns:repeat(3,1fr)';document.body.prepend(rows);
      window.addBadge=(id,url)=>{const row=document.createElement('p');row.id=id;row.className='chat-message';rows.append(row);
        const message={username:id,message:' subscriber badge',badges:[{id:'subscriber',title:'Subscriber',imageUrl:url}]};
        badgeTest.renderChatRow(row,message);return row;};
      window.transient=addBadge('transient','https://static-cdn.jtvnw.net/stream-studio-badge-fixture/transient');window.transientMessage=transient.chatMessage;`);
    await wait("!!transient.querySelector('.badge-fallback')");
    await capture('transient-failure');
    await evaluate('badgeTest.renderChatRow(transient,transientMessage)');
    await wait("!!transient.querySelector('img') && transient.querySelector('img').naturalWidth===36");
    assert.equal(requests.get('/transient'), 2, 'unchanged rows recover from a transient CDN failure');
    assert.ok(await evaluate('transient.chatMessage===transientMessage'), 'recovery needs no new message or catalog update');
    console.log('PASS transient badge failure recovers without a new message or changed presentation');
    await evaluate(`window.emoteRow=document.createElement('p');emoteRow.className='chat-message';rows.append(emoteRow);
      badgeTest.renderChatRow(emoteRow,{username:'viewer',message:'Dance',emotes:[{code:'Dance',startIndex:0,endIndex:5,imageUrl:'https://static-cdn.jtvnw.net/stream-studio-badge-fixture/transient-emote'}]})`);
    await wait("emoteRow.querySelector('img')?.naturalWidth===36");
    assert.equal(requests.get('/transient-emote'), 2, 'text-node emote fallbacks also recover');
    const badges = JSON.parse(fs.readFileSync(path.join(fixtures, 'alinity-helix-2026-10-22.json'), 'utf8')).data[0].versions;
    const liveCdn = process.argv.includes('--live-cdn');
    await evaluate(`${JSON.stringify(badges)}.forEach(b=>addBadge('alinity-'+b.id,${liveCdn ? 'b.image_url_2x' : "'https://static-cdn.jtvnw.net/stream-studio-badge-fixture/version-'+b.id"}))`);
    await wait("[...rows.querySelectorAll('[id^=alinity-]')].every(row=>row.querySelector('img')?.naturalWidth===36)");
    assert.equal(await evaluate("rows.querySelectorAll('[id^=alinity-] img').length"), 30);
    console.log('PASS 30 Alinity version rows decode ' + (liveCdn ? 'their actual Twitch CDN images' : 'the two captured PNG fixtures'));
    await evaluate("window.removed=addBadge('removed','https://static-cdn.jtvnw.net/stream-studio-badge-fixture/removed')");
    await wait("!!removed.querySelector('.badge-fallback')");
    await evaluate("removed.remove();window.reused=addBadge('reused','https://static-cdn.jtvnw.net/stream-studio-badge-fixture/removed-reused')");
    await wait("!!reused.querySelector('.badge-fallback')");
    await evaluate("badgeTest.renderChatRow(reused,{username:'replacement',message:'new message'})");
    await sleep(1300);
    assert.equal(requests.get('/removed'), 1, 'removed messages must not retry');
    assert.equal(requests.get('/removed-reused'), 1, 'replaced messages must not retry or restore stale images');
    assert.ok(await evaluate("!reused.querySelector('img') && reused.textContent==='replacementnew message'"));
    const stableRequests = requests.get('/transient');
    await evaluate('for(let n=0;n<10;n++)badgeTest.renderChatRow(transient,transientMessage)');
    await sleep(100); assert.equal(requests.get('/transient'), stableRequests, 'healthy unchanged images are retained');
    assert.ok(await evaluate("['javascript:alert(1)','http://badges.fixture/a','/api/settings'].every(url=>badgeTest.chatImage(url,'bad',true)===null)"));
    console.log('PASS removed rows stop retries, healthy rows stay attached, unsafe URLs are rejected');
    await evaluate("window.permanent=addBadge('permanent','https://static-cdn.jtvnw.net/stream-studio-badge-fixture/permanent')");
    await wait("!!permanent.querySelector('.badge-fallback')");
    const until = Date.now() + 24000;
    while ((requests.get('/permanent') || 0) < 4 && Date.now() < until) await sleep(100);
    await sleep(100);
    const budgetExpired = new Promise(resolve => {
      const handler = event => {
        if (JSON.parse(event.data).method === 'Emulation.virtualTimeBudgetExpired') { ws.removeEventListener('message', handler); resolve(); }
      };
      ws.addEventListener('message', handler);
    });
    await cdp('Emulation.setVirtualTimePolicy', { policy: 'pauseIfNetworkFetchesPending', budget: 120000 });
    await Promise.race([budgetExpired, sleep(3000).then(() => { throw new Error('Virtual-time retry budget did not finish'); })]);
    assert.equal(requests.get('/permanent'), 4, 'permanent failures have one initial request and three retries');
    assert.ok(await evaluate("!!permanent.querySelector('.badge-fallback') && permanent.textContent.includes('Subscriber')"));
    await capture('recovered-badges');
    fs.writeFileSync(path.join(output, 'requests.json'), JSON.stringify([...requests], null, 2));
    console.log('PASS permanent CDN failures retain readable text with a bounded retry budget');
  } finally { clearTimeout(deadline); if (ws) ws.close(); child.kill(); server.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
