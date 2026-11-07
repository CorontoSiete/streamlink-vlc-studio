'use strict';
// Real Chromium regression: production HTML/CSS/JS with deterministic API/media fixtures;
// it does not exercise the production HTTPS/authentication boundary or live providers.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const loginMode = process.argv.includes('--login-only');
const searchMode = process.argv.includes('--search-only');
const discoveryLanguageMode = process.argv.includes('--discovery-language-only');
const deleteMode = process.argv.includes('--downloads-delete-only');
const vodLibraryMode = process.argv.includes('--vod-library-only');
const hotkeyMode = process.argv.includes('--hotkeys-only');
const settingsMode = process.argv.includes('--settings-only');
const multistreamMode = process.argv.includes('--multistream-only');
const volumeWheelMode = process.argv.includes('--volume-wheel-only');
const sidebarMode = process.argv.includes('--sidebar-only');
const mobileControlsMode = process.argv.includes('--mobile-controls-only');
const output = path.join(root, discoveryLanguageMode ? '.artifacts/web-discovery-language' : vodLibraryMode ? '.artifacts/web-vod-library' : mobileControlsMode ? '.artifacts/web-mobile-controls' : loginMode ? '.artifacts/web-login' : sidebarMode ? '.artifacts/web-sidebar' : multistreamMode ? '.artifacts/web-multistream' : hotkeyMode ? '.artifacts/web-hotkeys' : settingsMode ? '.artifacts/web-settings' : volumeWheelMode ? '.artifacts/web-volume-wheel' : deleteMode ? '.artifacts/web-download-delete' : searchMode ? '.artifacts/web-search' : '.artifacts/player-navigation');
const assets = path.join(root, 'src/StreamlinkVlcStudio.Infrastructure/LocalWeb/Assets');
const mediaFixture = path.join(root, 'tests/StreamlinkVlcStudio.Tests/Fixtures', multistreamMode ? 'hover-preview-fmp4' : 'replay-position-event');
const browserPath = [process.env.SVS_WEB_TEST_BROWSER, 'C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/BraveSoftware/Brave-Browser/Application/brave.exe'].find(file => file && fs.existsSync(file));
if (!browserPath) throw new Error('Chromium is required. Set SVS_WEB_TEST_BROWSER to its executable path.');
const profile = path.join(output, 'chrome-fixture-' + Date.now());
fs.mkdirSync(profile, { recursive: true });
const live = [
  ['mountain', 'Northstar', 'Taking the scenic route. Come along for the adventure.', 'Twitch', 24580, 'Outdoors'],
  ['game', 'Pixelquest', 'A new world, a fresh save. Let’s explore.', 'Twitch', 8312, 'Adventure games'],
  ['music', 'Afterhours', 'Late-night sessions · good music, good company', 'Kick', 4260, 'Music'],
  ['code', 'Buildspace', 'Making something from nothing. Live creative session.', 'Twitch', 1580, '']
].map(([key, channel, title, platform, viewers, categoryName]) => ({ key, channel, displayName: channel, title, platform, viewers, categoryName, state: 'Live', thumbnailUrl: 'https://artwork.fixture/' + key,
  profileImageUrl: 'https://profiles.fixture/' + key + '.png' }));
const offline = ['Slowmornings', 'Sundayclub'].map((channel, i) => ({ key: 'offline' + i, channel, displayName: channel, title: '', platform: i ? 'Kick' : 'Twitch', state: 'Offline', thumbnailUrl: 'https://artwork.fixture/offline', profileImageUrl: 'https://profiles.fixture/offline.png' }));
// Deliberately retain provider relevance order, including offline and unknown counts.
const rankedSearch = [
  ['Offline first', 'Offline', 2147483647],
  ['Unknown count', 'Live', null],
  ['Small audience', 'Live', 12],
  ['Unavailable channel', 'Unavailable', 2147483647],
  ['Largest audience', 'Live', 2147483647],
  ['Equal audience first', 'Live', 450],
  ['Zero viewers', 'Live', 0],
  ['Equal audience second', 'Live', 450],
  ['Missing count', 'Live', undefined],
  ['Offline last', 'Offline', 0]
].flatMap(([displayName, state, viewers], index) => ['Twitch', 'Kick'].map(platform => ({
  ...live[0], key: 'rank-' + platform.toLowerCase() + '-' + index, channel: 'rank' + index, displayName, state, viewers, platform
})));
const categories = ['Adventure games', 'Music', 'Outdoors', 'Creative', 'Just chatting'].map((name, i) => ({ id: String(i), name, viewerCount: 125000, thumbnailUrl: 'https://artwork.fixture/' + live[i % 4].key }));
const categoryViewerMode = process.argv.includes('--category-viewers-only');
const viewerRequests = [], heldViewers = [];
let holdViewerIds = new Set(['0', '1', '2', '3']);
let failViewer = true;
let activeViewers = 0, maximumViewers = 0;
function artwork(name) {
  const [base, light, dark] = ({ mountain: ['#224f50','#7badaa','#173b40'], game: ['#59417b','#a88bc5','#302749'], music: ['#805049','#dfab84','#3b303d'], code: ['#2a4b70','#72a2b5','#1d304a'] })[name] || ['#424852','#899999','#242c32'];
  return `<svg xmlns="http://www.w3.org/2000/svg" width="960" height="540" viewBox="0 0 960 540"><rect width="960" height="540" fill="${base}"/><circle cx="730" cy="150" r="90" fill="${light}"/><path d="M0 420 230 120 470 400 650 190 960 470V540H0Z" fill="${dark}"/><path d="m0 500 300-200 270 230 250-220 140 130v100H0Z" fill="#111d29" opacity=".8"/></svg>`;
}
let signedIn = false;
const loginFixture = { requests: 0, holdNext: false, held: [] };
const savedSettings = { quality: 'best', lowLatency: true, layout: 'docked', size: 16,
  opacity: 90, keepInactive: false, pauseInactiveVod: true, hotkeys: {}, volumes: {}, chatLayouts: {}, categoryLanguages: {} };
const settingsFixture = { failSave: false, saves: 0, holdNext: false, held: [] };
let lastOpen;
let openCount = 0;
let profileOverride = null;
const closed = [];
const progressWrites = [];
const playerMetadata = new Map();
const followStates = new Map(live.map(item => [item.platform + ':' + item.channel.toLowerCase(), true]));
const followRequests = [], followWrites = [];
const searchFixture = { requests: [], held: [], hold: new Set(), fail: false, canceled: 0 };
const discoveryRequests = [];
let followPending = false, followUnknown = false, followFailure = false, followConfirmed = true;
const deleteFixture = { requests: [], items: [{ id: 'ec263e99-7ad7-47f9-b2e6-50c2f3c744e1', title: 'Saved broadcast', channel: 'Northstar', platform: 'Twitch', state: 'Completed', format: 'HLS' }] };
const vodLibraryItems = [
  ['old', 'Older broadcast', '2026-10-30T15:00:00Z', 'music'],
  ['unknown', 'Broadcast with no date', null, ''],
  ['new', 'New broadcast', '2026-11-02T15:00:00Z', 'mountain'],
  ['broken', 'Broadcast with unavailable artwork', '2026-11-01T15:00:00Z', 'broken'],
  ['latest', 'Latest broadcast', '2026-11-03T15:00:00Z', 'game']
].map(([key, title, published, art]) => ({ key, id: key, title, published, channel: 'Northstar', platform: 'Twitch',
  thumbnailUrl: art ? 'https://artwork.fixture/' + art : '', profileImageUrl: live[0].profileImageUrl, duration: 7200 }));
const server = http.createServer(async (request, response) => {
  const url = new URL(request.url, 'http://localhost');
  if (url.pathname.startsWith('/artwork/')) { response.setHeader('Content-Type', 'image/svg+xml'); response.end(artwork(url.pathname.split('/').pop())); return; }
  if (url.pathname === '/media/fixture/root' || url.pathname === '/media/fixture/vod') {
    response.setHeader('Content-Type', 'application/vnd.apple.mpegurl');
    const playlist = fs.readFileSync(path.join(mediaFixture, 'index.m3u8'), 'utf8').replace(/^(index\d+\.ts)\r?$/gm, '/media/fixture/$1');
    response.end(playlist + (url.pathname.endsWith('/vod') ? '\n#EXT-X-ENDLIST\n' : '')); return;
  }
  if (/^\/media\/fixture\/index[0-5]\.ts$/.test(url.pathname)) { response.setHeader('Content-Type', 'video/mp2t'); response.end(fs.readFileSync(path.join(mediaFixture, url.pathname.split('/').pop()))); return; }
  if (multistreamMode && /^\/media\/fixture\/(init\.mp4|segment[0-5]\.m4s)$/.test(url.pathname)) { response.setHeader('Content-Type', 'video/mp4'); response.end(fs.readFileSync(path.join(mediaFixture, url.pathname.split('/').pop()))); return; }
  if (url.pathname.startsWith('/api/')) {
    let raw = ''; for await (const chunk of request) raw += chunk;
    const input = JSON.parse(raw || '{}');
    const action = url.pathname.slice(5);
    response.setHeader('Content-Type', 'application/json');
    let result = {};
    if (action === 'session') { response.statusCode = signedIn ? 200 : 401; result = { csrf: 'visual-fixture', username: 'webtester' }; }
    else if (action === 'login') {
      loginFixture.requests++;
      const send = () => {
        signedIn = input.username === 'webtester' && input.password === 'Test web password 123!';
        response.statusCode = signedIn ? 200 : 401;
        response.end(JSON.stringify(signedIn ? { csrf: 'visual-fixture', username: 'webtester' } : { error: 'Username or password did not match.' }));
      };
      if (loginFixture.holdNext) { loginFixture.holdNext = false; loginFixture.held.push(send); } else send();
      return;
    }
    else if (action === 'logout') signedIn = false;
    else if (action === 'settings') result = { defaultQuality: savedSettings.quality, lowLatency: savedSettings.lowLatency,
      preferences: savedSettings, qualities: ['best', '720p'], browseLanguages: [{ code: '', name: 'All languages' }, { code: 'en', name: 'English' }, { code: 'fr', name: 'French' }], accounts: { twitch: true, kick: true } };
    else if (action === 'settings/save') {
      settingsFixture.saves++;
      if (settingsFixture.holdNext) {
        settingsFixture.holdNext = false;
        settingsFixture.held.push({ input, send: () => { Object.assign(savedSettings, input); response.end(JSON.stringify({ saved: true })); } });
        return;
      }
      if (settingsFixture.failSave) { response.statusCode = 503; result = { error: 'Web preferences could not be saved.' }; }
      else Object.assign(savedSettings, input);
    }
    else if (action === 'open') {
      lastOpen = input;
      const isVod = !!input.input?.includes('/videos/');
      const item = live.find(item => item.key === input.key || item.channel.toLowerCase() === input.input?.toLowerCase()) ||
        (isVod ? live[0] : { key: 'guest', channel: input.input || 'Guest', platform: input.platform === 'kick' ? 'Kick' : 'Twitch', title: 'Guest stream' });
      result = { id: 'visual-player-' + ++openCount, key: isVod ? input.input : item.key, title: isVod ? 'Mountain adventure - full broadcast' : item.title,
        channel: item.channel, platform: item.platform, profileImageUrl: item.profileImageUrl, url: '/media/fixture/' + (isVod ? 'vod' : 'root'), isVod, isMp4: false, resume: 0, duration: 0,
        quality: input.quality ?? savedSettings.quality, chatLayout: savedSettings.chatLayouts[item.platform + ':' + item.channel.toLowerCase()] ?? null };
      playerMetadata.set(result.id, result);
    }
    else if (action === 'volume/save') {
      const p = playerMetadata.get(input.player);
      if (p) savedSettings.volumes[p.platform + ':' + p.channel.toLowerCase()] = input.volume;
      result = { saved: true };
    }
    else if (action === 'close') closed.push(input.player);
    else if (action === 'progress') progressWrites.push(input);
    else if (action === 'follow/status') {
      followRequests.push(input.player);
      const p = playerMetadata.get(input.player);
      if (followFailure) { response.statusCode = 502; result = { error: 'Status lookup failed.' }; }
      else result = { pending: followPending && p.platform === 'Twitch', following: followUnknown ? null : followStates.get(p.platform + ':' + p.channel.toLowerCase()) ?? false,
        message: followUnknown ? 'Follow status unavailable.' : '' };
    }
    else if (action === 'follow' || action === 'unfollow') {
      const p = playerMetadata.get(input.player);
      followWrites.push({ action, player: input.player });
      if (followConfirmed) followStates.set(p.platform + ':' + p.channel.toLowerCase(), action === 'follow');
      result = { following: followConfirmed ? action === 'follow' : null, message: followConfirmed ? 'Confirmed.' : 'Not confirmed.' };
    }
    else if (action === 'channel/profile') {
      const p = playerMetadata.get(input.player);
      result = profileOverride || { displayName: p.channel, title: p.title, description: 'Community, adventures and good company.', category: 'Adventure games', viewers: live.find(item => item.channel === p.channel)?.viewers ?? 24580, followers: 124500,
        profileImageUrl: 'https://profiles.fixture/refreshed.png', isPartner: true, tags: ['English', 'Community'], links: [], panels: [] };
    }
    else if (action === 'chat') result = { messages: [], status: 'Connected' };
    else if (action === 'chat/presentation') result = { version: 0, messages: [] };
    else if (action === 'following') result = { items: live, offline };
    else if (action === 'categories' && categoryViewerMode) {
      const items = input.platform === 'kick' ? [{ id: '0', name: 'Kick category', viewerCount: 77 }]
        : input.query ? [{ id: '7', name: 'Search category', viewerCount: null }]
        : input.cursor ? [{ id: '5', name: 'Next page', viewerCount: null }]
        : categories.map((item, index) => ({ ...item, viewerCount: index === 4 ? 321 : null }));
      result = { items, nextCursor: !input.cursor && !input.query && input.platform === 'twitch' ? 'next' : '' };
    }
    else if (action === 'categories/viewers') {
      viewerRequests.push(input);
      activeViewers++; maximumViewers = Math.max(maximumViewers, activeViewers);
      response.once('close', () => activeViewers--);
      const id = input.categoryIds[0];
      const send = () => {
        if (response.destroyed) return;
        if (id === '2' && failViewer) { response.statusCode = 502; response.end(JSON.stringify({ error: 'Provider unavailable.' })); return; }
        response.end(JSON.stringify({ available: true, items: [{ categoryId: id, viewerCount: id === '3' ? 0 : id === '1' ? 329518 : Number(id) * 1000 + 500 }] }));
      };
      if (holdViewerIds.has(id)) heldViewers.push({ id, response, send }); else send();
      return;
    }
    else if (action === 'categories') result = { items: categories };
    else if (action === 'streams' && discoveryLanguageMode) {
      discoveryRequests.push(input);
      savedSettings.categoryLanguages[(input.platform === 'kick' ? 'Kick' : 'Twitch') + ':' + input.categoryId] = input.language;
      result = { items: input.language === 'fr' ? [] : [{ ...live[0], key: input.cursor ? 'second-page' : 'first-page', platform: input.platform === 'kick' ? 'Kick' : 'Twitch' }], nextCursor: input.cursor || input.language === 'fr' ? '' : 'page-two' };
    }
    else if (action === 'streams') result = { items: live };
    else if (action === 'recent') result = { items: live.slice(0, 2) };
    else if (action === 'search' && searchMode) {
      searchFixture.requests.push(input);
      response.once('close', () => { if (!response.writableEnded) searchFixture.canceled++; });
      const items = input.query === 'ranked' ? rankedSearch
        : input.query === 'many' ? Array.from({ length: 30 }, (_, i) => ({ ...live[0], key: 'many-' + i, displayName: 'Channel ' + (i + 1) }))
        : input.query === 'hostile' ? [{ ...live[0], displayName: '<img src=x onerror=window.injected=true>', title: '<script>window.injected=true</script>', profileImageUrl: 'javascript:alert(1)' }]
        : ['all', 'slow', 'late'].includes(input.query) ? [...live, ...offline]
        : [...live, ...offline].filter(item => item.channel.toLowerCase().includes(input.query.toLowerCase()));
      const send = () => {
        if (response.destroyed) return;
        response.statusCode = searchFixture.fail ? 502 : 200;
        response.end(JSON.stringify(searchFixture.fail ? { error: 'Provider temporarily unavailable.' } : { items: items.filter(item => item.platform.toLowerCase() === input.platform) }));
      };
      if (searchFixture.hold.has(input.query)) searchFixture.held.push({ query: input.query, send }); else send();
      return;
    }
    else if (action === 'search') result = { items: live.filter(item => item.channel.toLowerCase().includes(input.query.toLowerCase())) };
    else if (action === 'vods' && vodLibraryMode) result = { items: (input.cursor ? [vodLibraryItems[4], vodLibraryItems[2]] : vodLibraryItems.slice(0, 4)).map(item => ({ ...item, state: 'VOD', platform: input.platform === 'kick' ? 'Kick' : 'Twitch' })), nextCursor: input.cursor ? '' : 'page2' };
    else if (action === 'downloads' && vodLibraryMode) result = { items: vodLibraryItems.map(item => ({ ...item, state: 'Completed', format: 'MP4', totalSegments: 10, completedSegments: 10 })) };
    else if (action === 'vods') result = { items: [{ key: 'vod1', title: 'The long way home · a mountain adventure', channel: 'Northstar', platform: 'Twitch', state: 'VOD', duration: 7200, resume: 1820, thumbnailUrl: 'https://artwork.fixture/mountain', profileImageUrl: live[0].profileImageUrl }] };
    else if (action === 'downloads' && deleteMode) result = { items: deleteFixture.items };
    else if (action === 'downloads/remove' && deleteMode) {
      deleteFixture.requests.push(input);
      deleteFixture.items = deleteFixture.items.filter(item => item.id !== input.id);
      result = { removed: true };
    }
    else if (action === 'downloads') result = { items: [{ id: 'download1', title: 'Ready to watch', channel: 'Northstar', platform: 'Twitch', state: 'Completed', format: 'MP4', totalSegments: 100, completedSegments: 100, bytesDownloaded: 590000000, thumbnailUrl: 'https://artwork.fixture/mountain', profileImageUrl: live[0].profileImageUrl }] };
    response.end(JSON.stringify(result)); return;
  }
  const name = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
  if (!['index.html', 'app.css', 'app.js', 'twitch-logo.svg', 'kick-logo.svg', 'hls.min.js'].includes(name)) { response.statusCode = 404; response.end(); return; }
  response.setHeader('Content-Type', name.endsWith('.css') ? 'text/css' : name.endsWith('.js') ? 'text/javascript' : name.endsWith('.svg') ? 'image/svg+xml' : 'text/html');
  response.end(fs.readFileSync(path.join(assets, name)));
});
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function main() {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const address = 'http://127.0.0.1:' + server.address().port;
  console.log('Starting isolated render fixture at ' + address);
  console.log('Browser: ' + browserPath);
  const chrome = spawn(browserPath, ['--headless=new', '--disable-gpu', '--autoplay-policy=no-user-gesture-required', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  const deadline = setTimeout(() => { console.error('Renderer fixture timed out.'); chrome.kill(); server.close(); process.exit(1); }, 90000);
  process.stdin.on('data', () => { chrome.kill(); server.close(); process.exit(1); });
  let errors = ''; chrome.stderr.on('data', chunk => errors += chunk);
  let ws;
  try {
    let port;
    for (let n = 0; n < 150; n++) {
      try { port = fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]; if (port) break; } catch {}
      await sleep(100);
    }
    if (!port) throw new Error('Headless renderer did not start: ' + errors.slice(-2000));
    console.log('Renderer debugging port ' + port);
    const targets = await (await fetch('http://127.0.0.1:' + port + '/json/list', { signal: AbortSignal.timeout(8000) })).json();
    ws = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
    await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', reject, { once: true }); });
    let id = 0; const pending = new Map();
    ws.addEventListener('message', event => { const message = JSON.parse(event.data); if (message.id) { const item = pending.get(message.id); pending.delete(message.id); message.error ? item.reject(new Error(JSON.stringify(message.error))) : item.resolve(message.result); } });
    const cdp = (method, params = {}) => new Promise((resolve, reject) => { const key = ++id; const timer = setTimeout(() => { pending.delete(key); reject(new Error('Renderer command timed out: ' + method + '; ' + errors.slice(-1200))); }, 10000); pending.set(key, { resolve: value => { clearTimeout(timer); resolve(value); }, reject: error => { clearTimeout(timer); reject(error); } }); ws.send(JSON.stringify({ id: key, method, params })); });
    const avatarBytes = fs.readFileSync(path.join(root, 'tests/StreamlinkVlcStudio.Tests/Fixtures/profile-images/nicewigg-transparent-150.png'));
    const kickAvatarBytes = fs.readFileSync(path.join(root, 'tests/StreamlinkVlcStudio.Tests/Fixtures/profile-images/xqc-2026-10-22.webp'));
    const pendingAvatars = [], avatarRequests = [];
    ws.addEventListener('message', event => {
      const message = JSON.parse(event.data); if (message.method !== 'Fetch.requestPaused') return;
      const url = new URL(message.params.request.url), avatar = url.hostname === 'profiles.fixture';
      if (avatar) {
        avatarRequests.push(message.params.request);
        if (url.pathname === '/pending.png') { pendingAvatars.push(message.params.requestId); return; }
        if (url.pathname === '/broken.png') {
          cdp('Fetch.fulfillRequest', { requestId: message.params.requestId, responseCode: 404, body: '' }).catch(console.error); return;
        }
      }
      if (vodLibraryMode && !avatar && url.pathname === '/broken') {
        cdp('Fetch.fulfillRequest', { requestId: message.params.requestId, responseCode: 404, body: '' }).catch(console.error); return;
      }
      const kickAvatar = avatar && url.pathname === '/music.png';
      cdp('Fetch.fulfillRequest', { requestId: message.params.requestId, responseCode: 200,
        responseHeaders: [{ name: 'Content-Type', value: avatar ? kickAvatar ? 'image/webp' : 'image/png' : 'image/svg+xml' }],
        body: (avatar ? kickAvatar ? kickAvatarBytes : avatarBytes : Buffer.from(artwork(url.pathname.slice(1)))).toString('base64') }).catch(console.error);
    });
    await cdp('Fetch.enable', { patterns: [{ urlPattern: 'https://artwork.fixture/*', requestStage: 'Request' }, { urlPattern: 'https://profiles.fixture/*', requestStage: 'Request' }] });
    const evaluate = async expression => { const result = await cdp('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }); if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails)); return result.result.value; };
    const wait = async expression => { for (let n = 0; n < 120; n++) { if (await evaluate(expression)) return; await sleep(50); } throw new Error('UI condition timed out: ' + expression + '; ' + JSON.stringify(await evaluate("({errors:window.visualErrors,notice:document.querySelector('#notice')?.textContent,status:document.querySelector('.player-status')?.textContent,video:(()=>{const v=document.querySelector('video');return v&&{src:v.currentSrc,error:v.error?.message,paused:v.paused,time:v.currentTime,ready:v.readyState}})()})"))); };
    const capture = async name => { await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))'); await sleep(180); const shot = await cdp('Page.captureScreenshot', { format: 'png' }); fs.writeFileSync(path.join(output, name + '.png'), Buffer.from(shot.data, 'base64')); };
    const viewport = async width => cdp('Emulation.setDeviceMetricsOverride', { width, height: width < 651 ? 844 : 1000, deviceScaleFactor: 1, mobile: false });
    await cdp('Page.enable');
    await cdp('Page.addScriptToEvaluateOnNewDocument', { source: "window.visualErrors=[];window.addEventListener('error',e=>visualErrors.push(e.message));" });
    await viewport(1440);
    await cdp('Page.navigate', { url: address });
    await wait("document.readyState==='complete' && !!document.querySelector('#password')");
    await capture('login-desktop');
    if (loginMode) {
      await require('./helpers/web-login-browser.cjs')({ cdp, evaluate, wait, capture, viewport, address, fixture: loginFixture });
      return;
    }
    await evaluate("document.querySelector('#username').value='webtester';document.querySelector('#password').value='Test web password 123!';document.querySelector('#login-form').requestSubmit()");
    const settled = "document.querySelector('#cards').getAttribute('aria-busy')==='false'";
    await wait("!document.querySelector('#studio').hidden && document.querySelectorAll('.card').length===6");
    if (discoveryLanguageMode) {
      await require('./helpers/web-discovery-language-browser.cjs')({ cdp, evaluate, wait, capture, viewport, requests: discoveryRequests });
      return;
    }
    if (mobileControlsMode) {
      await require('./helpers/web-mobile-controls-browser.cjs')({ cdp, evaluate, wait, capture, output });
      return;
    }
    if (sidebarMode) {
      await require('./helpers/web-sidebar-browser.cjs')({ cdp, evaluate, wait, capture, viewport, address });
      return;
    }
    if (multistreamMode) {
      await require('./helpers/web-multistream-browser.cjs')({ cdp, evaluate, wait, capture, viewport, output });
      return;
    }
    if (hotkeyMode) {
      await require('./helpers/web-hotkeys-browser.cjs')({ cdp, evaluate, wait, capture, viewport, settingsFixture, getSavedSettings: () => savedSettings });
      return;
    }
    if (settingsMode) {
      await require('./helpers/web-settings-browser.cjs')({ cdp, evaluate, wait, settingsFixture, getSavedSettings: () => savedSettings });
      return;
    }
    if (volumeWheelMode) {
      await require('./helpers/web-volume-wheel-browser.cjs')({ cdp, evaluate, wait, getSavedSettings: () => savedSettings });
      return;
    }
    if (deleteMode) {
      await require('./helpers/web-download-delete-browser.cjs')({ cdp, ws, evaluate, wait, capture, viewport, fixture: deleteFixture });
      return;
    }
    if (vodLibraryMode) {
      await require('./helpers/web-vod-library-browser.cjs')({ evaluate, wait, capture, viewport });
      return;
    }
    if (searchMode) {
      await require('./helpers/web-search-browser.cjs')({ cdp, evaluate, wait, capture, viewport, searchFixture, live, offline,
        getOpen: () => ({ lastOpen, openCount }), output });
      return;
    }
    if (categoryViewerMode) {
      const meta = id => `document.querySelector('.card[data-key="${id}"] .media-meta')?.textContent`;
      const release = id => heldViewers.filter(item => item.id === id).splice(0).forEach(item => item.send());
      await evaluate("document.querySelector('[data-page=discover]').click()");
      await wait(settled + " && document.querySelectorAll('.card').length===5");
      for (let n = 0; n < 100 && viewerRequests.length < 4; n++) await sleep(20);
      assert.equal(viewerRequests.length, 4, 'at most four independent category requests start');
      assert.equal(maximumViewers, 4);
      assert.ok(viewerRequests.every(input => input.platform === 'twitch' && input.categoryIds.length === 1));
      assert.equal(await evaluate(meta('0')), 'Loading viewers…');
      assert.equal(await evaluate(meta('4')), '321 viewers', 'existing category totals need no lookup');
      await evaluate("window.categoryCard=document.querySelector('.card[data-key=\"1\"] .card-main');window.categoryCard.focus()");
      release('1'); release('2'); release('3');
      await wait(meta('1') + "===Number(329518).toLocaleString()+' viewers' && " + meta('2') + "==='Viewers unavailable' && " + meta('3') + "==='0 viewers'");
      assert.ok(await evaluate('document.activeElement===window.categoryCard'), 'updating totals preserves the focused category button');
      assert.equal(await evaluate(meta('0')), 'Loading viewers…', 'slow categories do not hold up other totals');
      await capture('category-viewers-desktop');
      await viewport(375);
      await capture('category-viewers-mobile');
      assert.ok(await evaluate("document.documentElement.scrollWidth<=innerWidth && [...document.querySelectorAll('.media-meta')].every(e=>e.getBoundingClientRect().right<=innerWidth)"), 'viewer badges fit the phone layout');
      await viewport(1440);

      holdViewerIds = new Set(['0']);
      await evaluate("document.querySelector('#more').click()");
      await wait(settled + " && document.querySelectorAll('.card').length===6 && " + meta('5') + "==='5,500 viewers'");
      assert.equal(await evaluate(meta('1')), '329,518 viewers', 'pagination preserves totals on earlier cards');
      assert.equal(viewerRequests.filter(input => input.categoryIds[0] === '1').length, 1, 'completed totals are not fetched again during pagination');
      assert.equal(await evaluate(meta('3')), '0 viewers');

      await evaluate("document.querySelector('#query').value='music';document.querySelector('#search-form').requestSubmit()");
      await wait(settled + " && document.querySelectorAll('.card').length===1 && " + meta('7') + "==='7,500 viewers'");
      release('0');
      assert.equal(await evaluate("document.querySelector('.card').dataset.key"), '7', 'late totals cannot change search results');
      const beforeKick = viewerRequests.length;
      await evaluate("document.querySelector('#platform').value='kick';document.querySelector('#platform').dispatchEvent(new Event('change'))");
      await wait(settled + ' && ' + meta('0') + "==='77 viewers'");
      assert.equal(viewerRequests.length, beforeKick, 'Kick totals continue to use their category response');

      failViewer = false; holdViewerIds.clear();
      await evaluate("document.querySelector('#query').value='';document.querySelector('#platform').value='twitch';document.querySelector('#platform').dispatchEvent(new Event('change'))");
      await wait(settled + ' && ' + meta('2') + "==='2,500 viewers'");
      await evaluate("document.querySelector('.card[data-key=\"1\"] .card-main').click()");
      await wait(settled + " && !document.querySelector('#cards').classList.contains('categories') && document.querySelectorAll('.card').length===4");
      assert.ok(await evaluate("[...document.querySelectorAll('.card .media-meta')].every(e=>e.textContent.endsWith(' viewers'))"), 'category navigation still renders stream viewer counts');
      assert.deepEqual(await evaluate('window.visualErrors'), []);
      console.log('PASS category totals: progressive rendering; four-request limit; zero and failed totals; stable keyboard focus; pagination; search; platform changes; cancellation; retry; stream navigation; desktop/phone layout.');
      return;
    }
    await wait("document.querySelectorAll('#followed-channels .avatar img').length===6 && [...document.querySelectorAll('#followed-channels .avatar img')].every(i=>i.complete&&i.naturalWidth>0)");
    await wait("document.querySelectorAll('.card:not(.is-offline) .avatar img').length===4 && [...document.querySelectorAll('.card:not(.is-offline) .avatar img')].every(i=>i.complete&&i.naturalWidth>0)");
    assert.equal(await evaluate("document.querySelector('#featured')"), null, 'Following displays channels without a live spotlight');
    assert.ok(await evaluate("[...document.querySelectorAll('.avatar img')].every(i=>i.src.startsWith('https://profiles.fixture/'))"), 'avatars use profile URLs instead of stream thumbnails');
    assert.ok(await evaluate("[...document.querySelectorAll('.avatar:has(img)')].every(a=>a.classList.contains('has-image')&&getComputedStyle(a.querySelector('.avatar-initials')).visibility==='hidden')"), 'transparent decoded avatars hide fallback initials');
    await capture('profile-pictures-desktop');
    const originalPictures = live.map(item => item.profileImageUrl);
    live[1].profileImageUrl = 'https://user:secret@profiles.fixture/credentials.png';
    live[2].profileImageUrl = 'https://profiles.fixture/broken.png';
    live[3].profileImageUrl = 'https://profiles.fixture/pending.png';
    await evaluate("document.querySelector('#refresh').click()");
    await wait("document.querySelector('#cards').getAttribute('aria-busy')==='false' && document.querySelector('.card[data-key=code] .avatar img')?.src.endsWith('/pending.png')");
    await wait("!document.querySelector('.card[data-key=music] .avatar img')");
    assert.equal(await evaluate("document.querySelector('.card[data-key=game] .avatar img')"), null, 'credential-bearing picture URLs are rejected');
    assert.ok(await evaluate("['game','music','code'].every(key=>{const a=document.querySelector('.card[data-key='+key+'] .avatar');return !a.classList.contains('has-image')&&getComputedStyle(a.querySelector('.avatar-initials')).visibility==='visible'})"), 'rejected, failed and pending pictures retain readable initials');
    await capture('profile-pictures-fallbacks');
    for (const requestId of pendingAvatars.splice(0)) await cdp('Fetch.fulfillRequest', { requestId, responseCode: 200, responseHeaders: [{ name: 'Content-Type', value: 'image/png' }], body: avatarBytes.toString('base64') });
    await wait("document.querySelector('.card[data-key=code] .avatar').classList.contains('has-image')");
    assert.ok(avatarRequests.every(request => !Object.keys(request.headers).some(name => name.toLowerCase() === 'referer')), 'profile requests omit the private server referrer');
    assert.ok(avatarRequests.every(request => !request.url.includes('credentials.png')), 'unsafe URLs never issue an image request');
    live.forEach((item, index) => item.profileImageUrl = originalPictures[index]);
    await evaluate("document.querySelector('#refresh').click()");
    await wait("document.querySelector('#cards').getAttribute('aria-busy')==='false' && document.querySelectorAll('#followed-channels .avatar.has-image').length===6");
    for (const [page, count] of [['recent', 2], ['downloads', 1]]) {
      await evaluate(`document.querySelector('[data-page=${page}]').click()`);
      await wait(`document.querySelector('#studio').dataset.page==='${page}' && document.querySelector('#cards').getAttribute('aria-busy')==='false' && document.querySelectorAll('.card .avatar.has-image').length===${count}`);
    }
    await evaluate("document.querySelector('[data-page=vods]').click()");
    await wait("document.querySelector('#studio').dataset.page==='vods' && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
    await evaluate("document.querySelector('#query').value='Northstar';document.querySelector('#search-form').requestSubmit()");
    await wait("document.querySelectorAll('.card .avatar.has-image').length===1");
    await evaluate("document.querySelector('[data-page=following]').click()");
    await wait("document.querySelector('#studio').dataset.page==='following' && document.querySelector('#cards').getAttribute('aria-busy')==='false' && document.querySelectorAll('.card').length===6");
    await wait("[...document.querySelectorAll('#cards .provider-logo')].every(logo => logo.complete && logo.naturalWidth > 0)");
    const followingLabels = await evaluate("Object.fromEntries([...document.querySelectorAll('#followed-channels > button.followed-channel')].map(row => [row.querySelector('strong').textContent, row.querySelector('.followed-copy > span')?.textContent ?? '']))");
    assert.equal(followingLabels.Northstar, 'Outdoors');
    assert.equal(followingLabels.Afterhours, 'Music');
    assert.equal(followingLabels.Buildspace, '', 'missing category does not leave an empty label');
    assert.equal(await evaluate("document.querySelector('#followed-channels .provider-logo')"), null, 'left Following rail has no platform logos');
    assert.equal(await evaluate("document.querySelector('.card[data-key=mountain] .provider-logo').alt"), 'Twitch');
    assert.equal(await evaluate("document.querySelector('.card[data-key=music] .provider-logo').alt"), 'Kick');
    assert.equal(await evaluate("[...document.querySelectorAll('#cards .provider-label')].every(label => label.textContent === '')"), true, 'channel cards show logos instead of platform text');
    assert.equal(await evaluate("[...document.querySelectorAll('#followed-channels > button.followed-channel')].find(row => row.querySelector('strong').textContent === 'Pixelquest').title"), 'Pixelquest · Twitch · Adventure games');
    assert.equal(await evaluate("[...document.querySelectorAll('#followed-channels > button.followed-channel')].find(row => row.querySelector('strong').textContent === 'Afterhours').getAttribute('aria-label')"), 'Watch Afterhours on Kick, streaming Music');
    const cardCategories = await evaluate("Object.fromEntries([...document.querySelectorAll('#cards .card')].map(card => [card.dataset.key, card.querySelector('.card-category')?.textContent ?? null]))");
    assert.deepEqual(cardCategories, { mountain: 'Category: Outdoors', game: 'Category: Adventure games', music: 'Category: Music', code: 'Category unavailable', offline0: null, offline1: null });
    assert.equal(await evaluate("document.querySelector('#cards .card[data-key=music] .card-main').getAttribute('aria-label')"), 'Watch Afterhours on Kick, streaming Music');
    assert.equal(await evaluate("document.querySelector('#cards .card[data-key=code] .card-main').getAttribute('aria-label')"), 'Watch Buildspace on Twitch, category unavailable');

    // Dispatch real pointer input so hit areas and event bubbling are exercised.
    const click = async selector => {
      const point = await evaluate(`(()=>{const e=document.querySelector(${JSON.stringify(selector)});if(!e)throw Error('Missing '+${JSON.stringify(selector)});e.scrollIntoView({block:'center'});const r=e.getBoundingClientRect();if(!r.width||!r.height)throw Error('Hidden '+${JSON.stringify(selector)});return {x:r.x+r.width/2,y:r.y+r.height/2}})()`);
      await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', ...point });
      await evaluate('new Promise(resolve=>requestAnimationFrame(resolve))');
      const hit = await evaluate(`(()=>{const e=document.querySelector(${JSON.stringify(selector)}),h=document.elementFromPoint(${point.x},${point.y});return {valid:e===h||e.contains(h),hit:h?.outerHTML?.slice(0,500),point:${JSON.stringify(point)},width:innerWidth}})()`);
      if (!hit.valid) { await capture('blocked-control'); throw Error('Blocked '+selector+': '+JSON.stringify(hit)); }
      await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', clickCount: 1 });
      await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', ...point, button: 'left', clickCount: 1 });
    };
    const count = async () => evaluate("document.querySelectorAll('#players video').length");
    const selected = async () => evaluate("document.querySelector('.is-selected').dataset.playerId");
    const open = async (input, platform = 'twitch') => {
      await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key: 'k', code: 'KeyK', windowsVirtualKeyCode: 75, modifiers: 2 });
      await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key: 'k', code: 'KeyK', windowsVirtualKeyCode: 75, modifiers: 2 });
      await wait("document.querySelector('#open-dialog').open");
      await evaluate(`document.querySelector('#open-query').value=${JSON.stringify(input)};document.querySelector('#open-platform').value=${JSON.stringify(platform)};document.querySelector('#open-form').requestSubmit()`);
      await wait("!document.querySelector('#open-dialog').open && document.querySelector('#studio').classList.contains('watching')");
    };
    if (process.argv.includes('--follow-only')) {
      const follow = '.player:not([hidden]) [data-action=follow]';
      const label = selector => `document.querySelector(${JSON.stringify(selector + ' .follow-label')})?.textContent`;
      const ready = (selector, text) => wait(`${label(selector)}===${JSON.stringify(text)} && !document.querySelector(${JSON.stringify(selector)}).disabled`);
      followPending = true;
      await click('button.followed-channel');
      await wait("document.querySelectorAll('.is-open').length===1");
      const first = await selected(), firstFollow = `#player-${first} [data-action=follow]`;
      await wait(`${label(firstFollow)}==='Checking…' && document.querySelector(${JSON.stringify(firstFollow)}).disabled`);
      assert.equal(followWrites.length, 0, 'opening a followed channel does not mutate the account');
      await click('#brand');
      await wait("getComputedStyle(document.querySelector('#library')).display!=='none' && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
      await open('Afterhours', 'kick');
      await ready(follow, 'Following');
      assert.equal(await evaluate(`document.querySelector(${JSON.stringify(follow)}).getAttribute('aria-pressed')`), 'true');
      assert.equal(await evaluate(label(firstFollow)), 'Checking…', 'switching channels does not borrow another player’s status');
      followPending = false;
      await ready(firstFollow, 'Following');
      assert.equal(followWrites.length, 0, 'both platform status checks are read-only');
      await capture('following-button');
      const kick = await selected();
      await open('Northstar');
      await ready(follow, 'Following');
      await evaluate('window.confirm=()=>true');
      await click(follow);
      await ready(follow, 'Follow');
      await ready(firstFollow, 'Follow');
      assert.equal(await evaluate(label(`#player-${kick} [data-action=follow]`)), 'Following', 'another platform/channel keeps its own state');
      await click(follow);
      await ready(firstFollow, 'Following');
      await click(`[data-focus-key=select-${kick}]`);
      const writesBeforeCancel = followWrites.length;
      await evaluate('window.confirm=()=>false');
      await click(follow);
      assert.equal(followWrites.length, writesBeforeCancel, 'canceling unfollow performs no write');
      await evaluate('window.confirm=()=>true');
      await click(follow);
      await ready(follow, 'Follow');
      assert.equal(followWrites.at(-1).action, 'unfollow', 'Following invokes unfollow');
      assert.equal(await evaluate(`document.querySelector(${JSON.stringify(follow)}).getAttribute('aria-pressed')`), 'false');
      await click(follow);
      await ready(follow, 'Following');
      assert.equal(followWrites.at(-1).action, 'follow');
      followConfirmed = false;
      await click(follow);
      await ready(follow, 'Following');
      followConfirmed = true;
      // A listed channel can have stale library data: the website status wins.
      followStates.set('Twitch:pixelquest', false);
      await open('Pixelquest');
      await ready(follow, 'Follow');
      await click('.player:not([hidden]) .player-options summary');
      await click('.player:not([hidden]) [data-action=reload]');
      await ready(follow, 'Follow');
      await open('Guest');
      await ready(follow, 'Follow');
      followUnknown = true;
      await open('Unknown');
      await ready(follow, 'Follow');
      assert.equal(await evaluate(`document.querySelector(${JSON.stringify(follow)}).getAttribute('aria-pressed')`), null, 'unverified status is not reported as false');
      assert.match(await evaluate(`document.querySelector(${JSON.stringify(follow)}).title`), /unavailable/i);
      followUnknown = false; followFailure = true;
      await open('Failure');
      await ready(follow, 'Follow');
      assert.equal(await evaluate(`document.querySelector(${JSON.stringify(follow)}).getAttribute('aria-pressed')`), null);
      assert.match(await evaluate(`document.querySelector(${JSON.stringify(follow)}).title`), /failed/i);
      followFailure = false;
      followPending = true;
      await open('Closing');
      await wait(`${label(follow)}==='Checking…'`);
      const closing = await selected();
      await click('.player:not([hidden]) .player-options summary');
      await click('.player:not([hidden]) [data-action=close]');
      const readsAfterClose = followRequests.filter(id => id === closing).length;
      await sleep(650);
      assert.equal(followRequests.filter(id => id === closing).length, readsAfterClose, 'closed players stop status polling');
      followPending = false;
      const readsBeforeVod = followRequests.length;
      await open('https://www.twitch.tv/videos/123');
      await sleep(650);
      assert.equal(followRequests.length, readsBeforeVod, 'VODs do not query hidden follow controls');
      assert.ok(await evaluate(`document.querySelector(${JSON.stringify(follow)}).hidden`));
      assert.equal(await evaluate('window.visualErrors.length'), 0);
      console.log('PASS website-confirmed Twitch/Kick initial state; background checks; channel isolation; follow/unfollow confirmation; unverified/failed reads; reload; closed-player cleanup; VOD exclusion; zero JavaScript errors.');
      return;
    }
    assert.equal(await evaluate("!!document.querySelector('#tabs')"), false, 'the old tab strip is removed');
    await click('button.followed-channel');
    await wait("document.querySelector('#players video')?.currentTime>0.1 && document.querySelectorAll('.is-open').length===1");
    const first = await selected();
    const video = `#player-${first} video`;
    const pauseButton = `#player-${first} [data-action=play]`;
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(video)}).paused`), false, 'stream starts playing');
    await click(video);
    await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(video)}).paused`), false, 'clicking the video leaves playback running');
    await click(pauseButton);
    await wait(`document.querySelector(${JSON.stringify(video)}).paused && document.querySelector(${JSON.stringify(pauseButton)}).getAttribute('aria-label')==='Play'`);
    await click(video);
    await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
    assert.equal(await evaluate(`document.querySelector(${JSON.stringify(video)}).paused`), true, 'clicking a paused video does not resume it');
    await click(pauseButton);
    await wait(`!document.querySelector(${JSON.stringify(video)}).paused && document.querySelector(${JSON.stringify(pauseButton)}).getAttribute('aria-label')==='Pause'`);
    assert.equal(await evaluate("document.querySelector('.player:not([hidden]) .channel-name').textContent"), 'Northstar');
    await wait("document.querySelector('.channel-avatar img')?.src==='https://profiles.fixture/refreshed.png' && document.querySelector('.channel-avatar img').naturalWidth===150 && document.querySelector('.is-open .avatar img')?.src==='https://profiles.fixture/refreshed.png'");
    assert.equal(await evaluate("document.querySelector('.is-open .followed-copy > span').textContent"), 'Watching');
    assert.equal(await evaluate("document.querySelector('#followed-channels .provider-logo')"), null, 'open streams also have no left-rail logos');
    assert.equal(await evaluate("document.querySelector('.is-open .watching-indicator span').textContent"), '24.6K', 'open channel shows its viewer count');
    assert.equal(await evaluate("document.querySelectorAll('.followed-channel').length"), 6, 'an open channel replaces its existing followed row');
    assert.ok(await evaluate("(()=>{const c=getComputedStyle(document.querySelector('.is-open strong')).color.match(/[0-9.]+/g).map(Number);return c[1]>c[0] && c[1]>c[2]})()"), 'open channel is green');
    await capture('watch-desktop');

    // Clicking a channel, including its library card, must reuse the existing player.
    await click('.followed-link');
    await click('#brand');
    await wait("document.querySelector('#cards').getAttribute('aria-busy')==='false' && !!document.querySelector('.card-main') && getComputedStyle(document.querySelector('#library')).display!=='none'");
    await click('.card-main');
    await wait("document.querySelector('#studio').classList.contains('watching')");
    assert.equal(openCount, 1, 'reopening the same library key does not allocate another player');
    assert.equal(await count(), 1);
    await click('button.followed-channel');
    await wait("document.querySelectorAll('.is-open').length===2");
    const second = await selected();
    assert.notEqual(first, second);
    assert.equal(await evaluate("document.querySelector('.player:not([hidden]) .channel-name').textContent"), 'Pixelquest');
    await wait(`document.querySelector('.is-open[data-player-id="${second}"] .watching-indicator span')?.textContent==='8.3K'`);
    assert.equal(await evaluate(`document.querySelector('.is-open[data-player-id="${first}"] .watching-indicator span').textContent`), '24.6K', 'each open stream keeps its own count');
    assert.equal(await evaluate("document.querySelector('#open-player-count').textContent"), '2');
    await click('.player:not([hidden]) .playback-settings summary');
    await click('.player:not([hidden]) .multistream-toggle');
    await wait("document.querySelector('#players').classList.contains('grid') && document.querySelectorAll('.player:not([hidden])').length===2");
    assert.ok(await evaluate("[...document.querySelectorAll('.multistream-toggle')].every(e=>e.checked)"), 'all player menus reflect multi-stream');
    await click(`#player-${second} .playback-settings summary`);
    for (const width of [320,390,650,651,768,900,901,1024,1180,1440,1920]) {
      await viewport(width);
      for (const player of [first, second]) {
        const menu = `#player-${player} .playback-settings`;
        await click(menu+' summary');
        assert.ok(await evaluate(`(()=>{const p=document.querySelector('#player-${player}'),s=p.querySelector('.video-surface').getBoundingClientRect(),b=p.querySelector('.playback-settings summary').getBoundingClientRect();return b.x>=s.x && b.right<=s.right})()`), 'gear stays inside each grid video at '+width);
        await click(menu+' .multistream-toggle');
        await wait("!document.querySelector('#players').classList.contains('grid') && document.querySelectorAll('.player:not([hidden])').length===1");
        assert.equal(await selected(), player, 'disabling keeps the player whose settings were used');
        // Space exercises the native switch's keyboard behavior as well as pointer input.
        await cdp('Input.dispatchKeyEvent', {type:'keyDown',key:' ',code:'Space',windowsVirtualKeyCode:32});
        await cdp('Input.dispatchKeyEvent', {type:'keyUp',key:' ',code:'Space',windowsVirtualKeyCode:32});
        await wait("document.querySelector('#players').classList.contains('grid') && [...document.querySelectorAll('.multistream-toggle')].every(e=>e.checked)");
        await click(menu+' summary');
      }
    }
    await viewport(1440);
    await click(`.is-open[data-player-id="${first}"] .followed-link`);
    await wait(`document.querySelector('.is-selected').dataset.playerId==='${first}'`);
    assert.equal(await evaluate("[...document.querySelectorAll('video')].filter(v=>!v.muted).length"), 1, 'grid retains one selected audio source');
    await capture('watch-multi-desktop');
    await click(`#player-${first} [data-action=fullscreen]`);
    await wait("document.fullscreenElement?.id==='players'");
    assert.ok(await evaluate("[...document.querySelectorAll('.player:not([hidden]) video')].every(v=>v.getBoundingClientRect().width>100 && v.getBoundingClientRect().height>100)"), 'fullscreen grid videos retain their rendered size');
    await capture('multi-fullscreen');
    await click(`#player-${first} .playback-settings summary`);
    await click(`#player-${first} .multistream-toggle`);
    await wait(`document.fullscreenElement?.id==='player-${first}' && !document.querySelector('#player-${first} .multistream-toggle').disabled`);
    assert.equal(await evaluate("document.querySelectorAll('.player:not([hidden])').length"), 1, 'turning multi-stream off keeps the selected player fullscreen');
    await click(`#player-${first} .multistream-toggle`);
    await wait("document.fullscreenElement?.id==='players' && !document.querySelector('.multistream-toggle').disabled");
    assert.equal(await evaluate("document.querySelectorAll('.player:not([hidden])').length"), 2, 'turning multi-stream on shows the grid fullscreen');
    await click(`#player-${first} .playback-settings summary`);
    await click(`#player-${first} [data-action=fullscreen]`);
    await wait("!document.fullscreenElement");
    await click(`#player-${first} .playback-settings summary`);
    await click(`#player-${first} .multistream-toggle`);
    await wait("!document.querySelector('#players').classList.contains('grid') && document.querySelectorAll('.player:not([hidden])').length===1");
    assert.ok(await evaluate("[...document.querySelectorAll('.multistream-toggle')].every(e=>!e.checked)"), 'disabling from another player synchronizes all menus');
    await capture('settings-desktop');
    await click(`#player-${first} .playback-settings summary`);
    await click(`.is-open[data-player-id="${second}"] .followed-exit`);
    await wait("document.querySelectorAll('.is-open').length===1");
    assert.equal(await selected(), first, 'closing a background row does not select it');
    assert.deepEqual(closed, [second], 'the sibling exit closes only its player');
    assert.equal(openCount, 2, 'the exit does not trigger the stream-open handler');
    assert.equal(await evaluate("document.querySelectorAll('button.followed-channel').length"), 5, 'closed followed channel returns to the regular list');

    // Non-followed channels and videos from the same streamer remain reachable.
    await click('.player:not([hidden]) .playback-settings summary');
    await click('.player:not([hidden]) .multistream-toggle');
    await click('.player:not([hidden]) .playback-settings summary');
    await open('GuestWithAnExceptionallyLongChannelName', 'kick');
    const guest = await selected();
    await open('https://www.twitch.tv/videos/12345');
    let vod = await selected();
    assert.ok(await evaluate("[...document.querySelectorAll('.multistream-toggle')].every(e=>e.checked)"), 'new players inherit multi-stream');
    await evaluate(`document.querySelector('#player-${vod} [data-action=reload]').click()`);
    await wait(`document.querySelectorAll('video').length===3 && !document.querySelector('#player-${vod}')`);
    vod = await selected();
    assert.ok(await evaluate("[...document.querySelectorAll('.multistream-toggle')].every(e=>e.checked)"), 'replacement players inherit multi-stream');
    await click(`#player-${vod} .playback-settings summary`);
    await click(`#player-${vod} .multistream-toggle`);
    await wait("document.querySelectorAll('.player:not([hidden])').length===1");
    await click(`#player-${vod} .playback-settings summary`);
    assert.equal(await count(), 3);
    assert.equal(await evaluate("document.querySelectorAll('.is-open').length"), 3);
    assert.ok(await evaluate("document.querySelector('.player:not([hidden]) .video-live-badge').hidden"), 'video has no live badge');
    assert.ok(await evaluate("document.querySelector('.is-selected .followed-copy span').textContent.includes('Mountain adventure')"), 'same-channel VOD has its own identifiable row');
    assert.equal(await evaluate("document.querySelector('.is-selected .watching-indicator span')"), null, 'video rows do not show live viewer counts');
    await click('.is-selected .followed-link');
    assert.equal(await count(), 3, 'selecting a VOD never opens the channel live');
    await evaluate("document.querySelector('.player:not([hidden]) .rename').value='Evening watch';document.querySelector('.player:not([hidden]) .rename').dispatchEvent(new Event('change'))");
    assert.equal(await evaluate("document.querySelector('.player:not([hidden]) .rename').value"), 'Evening watch');
    await click(`.is-open[data-player-id="${guest}"] .followed-link`);
    const metrics = [];
    for (const width of [320, 390, 650, 651, 768, 900, 901, 1024, 1180, 1440, 1920]) {
      await viewport(width);
      await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
      await evaluate("window.scrollTo(0,0)");
      await click('.player:not([hidden]) .playback-settings summary');
      await evaluate('window.scrollTo(0,0)');
      const metric = await evaluate(`(()=>{const rect=s=>{const r=document.querySelector(s).getBoundingClientRect();return {x:r.x,y:r.y,right:r.right,bottom:r.bottom,w:r.width,h:r.height}};return {width:innerWidth,content:document.documentElement.scrollWidth,header:rect('.topbar'),video:rect('.player:not([hidden]) .video-surface'),menu:rect('.player:not([hidden]) .playback-settings-menu'),toggle:rect('.player:not([hidden]) .multistream-toggle'),channels:rect('#channels-toggle')}})()`);
      metrics.push(metric);
      if (metric.content > width) {
        await capture('overflow-' + width);
        console.error(await evaluate("[...document.querySelectorAll('body *')].filter(e=>e.getClientRects().length && e.getBoundingClientRect().right>innerWidth).map(e=>({tag:e.tagName,cls:e.className,id:e.id,right:e.getBoundingClientRect().right})).slice(0,25)"));
      }
      assert.ok(metric.content<=width, 'watch overflow: '+JSON.stringify(metric));
      assert.equal(await evaluate("document.querySelector('.watch-toolbar')"), null, 'watch toolbar is removed from the DOM');
      assert.ok(Math.abs(metric.video.y-metric.header.bottom)<=1, 'video starts directly below the main header: '+JSON.stringify(metric));
      for (const name of ['menu','toggle']) {
        const r=metric[name]; assert.ok(r.w>0 && r.x>=0 && r.right<=width && r.y>=metric.video.y && r.bottom<=(width<651?844:1000), 'playback setting clipped: '+name+' '+JSON.stringify(metric));
      }
      if ([320,390,768,1440].includes(width)) await capture('settings-'+width);
      await click('.player:not([hidden]) .playback-settings summary');
      if (width <= 900) {
        assert.ok(metric.channels.w>0 && metric.channels.x>=0, 'Following remains accessible without the desktop sidebar');
        await click('#channels-toggle');
        await wait("document.querySelector('#channels-dialog').open && document.querySelector('#channels-dialog .is-selected')");
        if (width === 390) {
          await evaluate("document.querySelector('#close-channels').click();document.querySelector('#channels-toggle').click()");
          await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
          assert.ok(await evaluate("document.querySelector('#channels-dialog').open && !!document.querySelector('#channels-dialog #channel-navigation')"), 'a delayed close event cannot empty the reopened drawer');
        }
        assert.equal(await evaluate("document.querySelectorAll('#channels-dialog .is-open').length"), 3);
        assert.ok(await evaluate("[...document.querySelectorAll('#channels-dialog .followed-exit')].every(b=>{const r=b.getBoundingClientRect();return r.width>=44&&r.height>=44&&r.right<=innerWidth})"), 'touch exit targets');
        await capture('following-drawer-'+width);
        await cdp('Input.dispatchKeyEvent', {type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
        await cdp('Input.dispatchKeyEvent', {type:'keyUp',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
        await wait("!document.querySelector('#channels-dialog').open && !!document.querySelector('#sidebar-channels-host .followed-section')");
        assert.ok(await evaluate("document.querySelector('#studio').classList.contains('watching')"), 'Escape dismisses the drawer without leaving the player');
        await click('#channels-toggle');
        await click('.is-selected .followed-link');
        await wait("!document.querySelector('#channels-dialog').open");
      }
      if ([320,390,768,1440].includes(width)) await capture('watch-'+width);
    }
    fs.writeFileSync(path.join(output, 'viewport-metrics.json'), JSON.stringify(metrics, null, 2));

    // Theatre mode hides the sidebar, so it also needs Following access.
    await viewport(1440);
    await click('.player:not([hidden]) [data-action=theater]');
    await wait("document.querySelector('#studio').classList.contains('theater') && document.querySelector('#channels-toggle').getBoundingClientRect().width>0");
    await click('#channels-toggle');
    await click(`.is-open[data-player-id="${first}"] .followed-link`);
    await wait(`document.querySelector('.is-selected').dataset.playerId==='${first}'`);
    await click('.player:not([hidden]) [data-action=theater]');
    await wait("!document.querySelector('#studio').classList.contains('theater')");

    // Reload keeps the user's pause, speed and name while replacing its navigation row.
    await click(`.is-open[data-player-id="${vod}"] .followed-link`);
    await evaluate("(()=>{const p=document.querySelector('.player:not([hidden])');p.querySelector('video').pause();const speed=p.querySelector('.speed');speed.value='1.5';speed.dispatchEvent(new Event('change'))})()");
    await evaluate("document.querySelector('.player:not([hidden]) [data-action=reload]').click()");
    await wait(`document.querySelectorAll('.is-open').length===3 && !document.querySelector('.is-open[data-player-id="${vod}"]')`);
    await wait("document.querySelector('.player:not([hidden]) video').readyState>=1");
    assert.ok(await evaluate("(()=>{const p=document.querySelector('.player:not([hidden])'),v=p.querySelector('video');return v.paused && v.playbackRate===1.5 && p.querySelector('.speed').value==='1.5'})()"), 'reload preserves pause and speed after real media initialization');
    assert.equal(await evaluate("document.querySelector('.player:not([hidden]) .rename').value"), 'Evening watch');
    assert.equal(await evaluate("document.querySelectorAll('.followed-exit').length"), 3);

    // Let real decoded media reach its end, then verify that closing it keeps the
    // completed bookmark instead of replacing it with an end-position resume.
    const completedPlayer = await selected();
    await evaluate("(()=>{const v=document.querySelector('.player:not([hidden]) video');v.currentTime=v.duration-.1;return v.play()})()");
    await wait("document.querySelector('.player:not([hidden]) video').ended");
    for (let n = 0; n < 100 && !progressWrites.some(p => p.player === completedPlayer && p.ended); n++) await sleep(50);
    assert.ok(progressWrites.some(p => p.player === completedPlayer && p.ended), 'the actual ended event saves completion');

    // Exit current advances to a remaining player, and the final exit restores the library.
    await evaluate("document.querySelector('.player:not([hidden]) [data-action=close]').click()");
    await wait("document.querySelectorAll('.is-open').length===2");
    assert.equal(progressWrites.findLast(p => p.player === completedPlayer).ended, true, 'close preserves actual media completion');
    await viewport(390);
    await click('#channels-toggle');
    await click(`.is-open[data-player-id="${first}"] .followed-exit`);
    await wait("document.querySelectorAll('.is-open').length===1");
    assert.equal(await selected(), guest);
    await click('.followed-exit');
    await wait("document.querySelectorAll('#players video').length===0 && !document.querySelector('#channels-dialog').open");
    assert.ok(await evaluate("!document.querySelector('#studio').classList.contains('watching') && document.querySelector('#watch').hidden && document.activeElement.id==='library'"));
    assert.equal(await evaluate("document.querySelectorAll('.followed-exit').length"), 0);
    assert.equal(await evaluate("document.querySelectorAll('.followed-channel').length"), 6);
    assert.equal(await evaluate("document.querySelectorAll('#channel-navigation').length"), 1, 'drawer uses one synchronized list');
    assert.equal(await evaluate('window.visualErrors.length'), 0);
    await capture('library-after-exit-mobile');

    // The real channel has no short bio. Its introduction is in an untitled panel,
    // after sponsor terms: never guess that the first panel description is a bio.
    const panelUser = JSON.parse(fs.readFileSync(path.join(root, 'tests/StreamlinkVlcStudio.Tests/Fixtures/channel-profiles/imperialhal-2026-10-22.json'), 'utf8')).data.user;
    profileOverride = { displayName: panelUser.displayName, description: panelUser.description, followers: panelUser.followers.totalCount,
      panels: panelUser.panels.filter(panel => panel.type === 'DEFAULT').map((panel, index) => ({ ...panel,
        imageUrl: panel.imageURL ? 'https://artwork.fixture/panel-' + index : '', linkUrl: panel.linkURL })) };
    await viewport(1440);
    await open('imperialhal__');
    await wait("document.querySelector('.channel-name').textContent==='ImperialHal__' && document.querySelectorAll('.channel-panel').length===7");
    assert.ok(await evaluate("!document.querySelector('.channel-description').textContent.includes('hasn’t added a bio')"), 'panel-only channels must not claim the broadcaster has no bio');
    assert.ok(await evaluate("document.querySelector('.channel-description').textContent.includes('panels below')"));
    assert.ok(await evaluate("!document.querySelector('.channel-description').textContent.includes('Coupon')"), 'sponsor terms are not a biography fallback');
    assert.ok(await evaluate("[...document.querySelectorAll('.channel-panel')].some(panel=>panel.textContent.includes(\"Hi! My name is Phillip\"))"), 'the actual introduction is preserved');
    for (const width of [1440, 390]) {
      await viewport(width);
      await evaluate("document.querySelector('.channel-about').scrollIntoView({block:'start'})");
      await capture('panel-only-about-' + width);
      await click('.channel-panels-link');
      assert.ok(await evaluate("document.activeElement===document.querySelector('.channel-panels')"), 'panel shortcut moves keyboard focus into the selected player panels');
      assert.ok(await evaluate("document.querySelector('.channel-panels').getBoundingClientRect().top>=0 && document.querySelector('.channel-panels').getBoundingClientRect().top<innerHeight"));
      assert.ok(await evaluate("document.documentElement.scrollWidth<=document.documentElement.clientWidth"));
      await capture('panel-only-bio-' + width);
    }
    await evaluate("document.querySelector('[data-action=close]').click()");
    await wait("document.querySelectorAll('#players video').length===0");
    // Empty/unsafe panels do not create a dead shortcut. A real short bio stays intact.
    for (const description of ['', 'A real short bio. <script>window.injected=true</script>']) {
      profileOverride = { displayName: 'Empty channel', description, panels: [{ title: '', description: '', imageUrl: 'javascript:alert(1)', linkUrl: '' }] };
      await open('emptychannel');
      await wait("document.querySelector('.channel-name').textContent==='Empty channel'");
      assert.equal(await evaluate("document.querySelectorAll('.channel-panels-link').length"), 0);
      assert.equal(await evaluate("document.querySelector('.channel-description').textContent"), description || 'This streamer hasn’t added a bio yet.');
      assert.ok(await evaluate("!window.injected && !document.querySelector('.channel-description script')"));
      await evaluate("document.querySelector('[data-action=close]').click()");
      await wait("document.querySelectorAll('#players video').length===0");
    }
    assert.equal(await evaluate('window.visualErrors.length'), 0);
    console.log('PASS panel-only channel information; actual ImperialHal introduction after sponsors; scoped panel focus at desktop/phone widths; empty/unsafe panels; safe short-bio rendering.');
    console.log('PASS real decoded video; green Following states; duplicate prevention; independent background exits; live/VOD/unfollowed switching; selected grid audio; rename/reload; gear-menu Multi-stream synchronization; grid fullscreen; no toolbar or reserved gap; playback-menu bounds; theatre access; keyboard drawer dismissal; 44px touch exits; final-close recovery; 11 watch widths from 320-1920px; zero JavaScript errors.');
  } finally { clearTimeout(deadline); ws?.close(); chrome.kill(); server.close(); process.stdin.pause(); }
}
main().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
