'use strict';

// Exercise the application's overlay drag handler and CSS with trusted Chrome input.
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

const assets = path.resolve(__dirname, '../../src/StreamlinkVlcStudio.Infrastructure/LocalWeb/Assets');
const source = fs.readFileSync(path.join(assets, 'app.js'), 'utf8');
const css = fs.readFileSync(path.join(assets, 'app.css'), 'utf8');
const bounded = source.match(/^  const bounded = .*;$/m)?.[0];
const start = source.indexOf('  function restoreChatGeometry(p) {');
const end = source.indexOf("  window.addEventListener('resize'", start);
assert.ok(bounded && start >= 0 && end > start, 'Application drag handlers must be present');

const chrome = process.env.CHROME_PATH || (process.platform === 'win32'
  ? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'
  : '/usr/bin/google-chrome');
assert.ok(fs.existsSync(chrome), `Chrome not found at ${chrome}; set CHROME_PATH`);

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function main() {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'streamstudio-scrollbar-'));
  assert.equal(path.dirname(fs.realpathSync(profile)), fs.realpathSync(os.tmpdir()));
  const page = path.join(profile, 'fixture.html');
  fs.writeFileSync(page, `<!doctype html><meta charset="utf-8"><style>${css}
    html, body { margin: 0; }
    .player { width: 800px; }
    .video-surface { position: relative; width: 800px; height: 500px; }
    </style><div class="player chat-overlay chat-transparent"><div class="video-surface">
    <aside class="chat"><button class="chat-move">Move</button>
    <div class="chat-messages" tabindex="0"><div style="height:2000px">Chat messages</div></div>
    <form class="chat-form"><input><button>Send</button></form>
    <button class="chat-resize"><span class="chat-resize-feedback"></span></button></aside>
    </div></div><script>
    const $ = (selector, root = document) => root.querySelector(selector);
    const preferences = { layout: 'overlay' }, state = { players: [] };
    const run = action => action(), api = async () => ({});
    ${bounded}
    ${source.slice(start, end)}
    const p = { element: document.querySelector('.player'), chatX: 1, chatY: 0 };
    setupChatControls(p); sizeChat(p); window.fixtureReady = true;
    </script>`);

  const child = spawn(chrome, [
    '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    '--remote-debugging-port=0', `--user-data-dir=${profile}`, pathToFileURL(page).href
  ], { stdio: 'ignore' });
  let socket;
  try {
    const portFile = path.join(profile, 'DevToolsActivePort');
    for (let i = 0; i < 100 && !fs.existsSync(portFile); i++) await delay(100);
    assert.ok(fs.existsSync(portFile), 'Chrome did not open its debugging port');
    const port = fs.readFileSync(portFile, 'utf8').split(/\r?\n/)[0];
    const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    const target = targets.find(item => item.type === 'page');
    assert.ok(target, 'Chrome did not create a page');
    socket = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => {
      socket.addEventListener('open', resolve, { once: true });
      socket.addEventListener('error', reject, { once: true });
    });
    let id = 0;
    const pending = new Map();
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      const wait = pending.get(message.id);
      if (wait) { pending.delete(message.id); wait(message); }
    });
    const call = (method, params = {}) => new Promise((resolve, reject) => {
      const request = ++id;
      pending.set(request, message => message.error ? reject(new Error(message.error.message)) : resolve(message.result));
      socket.send(JSON.stringify({ id: request, method, params }));
    });
    const evaluate = async expression => {
      const response = await call('Runtime.evaluate', { expression, returnByValue: true });
      if (response.exceptionDetails) throw new Error(JSON.stringify(response.exceptionDetails));
      return response.result.value;
    };
    const mouse = (type, x, y, buttons = 0) => call('Input.dispatchMouseEvent', {
      type, x, y, button: buttons ? 'left' : 'none', buttons, clickCount: type === 'mouseMoved' ? 0 : 1
    });
    for (let i = 0; i < 100 && !await evaluate('window.fixtureReady === true'); i++) await delay(100);
    assert.equal(await evaluate('window.fixtureReady === true'), true, 'Fixture did not initialize');

    for (const transparent of [true, false]) {
      await evaluate(`document.querySelector('.player').classList.toggle('chat-transparent', ${transparent})`);
      const point = await evaluate(`(()=>{const r=document.querySelector('.chat-move').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()`);
      await mouse('mouseMoved', point.x, point.y);
      await mouse('mousePressed', point.x, point.y, 1);
      await mouse('mouseMoved', point.x - 80, point.y + 30, 1);
      await mouse('mouseReleased', point.x - 80, point.y + 30);
      await mouse('mouseMoved', 0, 490);
      const away = await evaluate(`(()=>{const c=document.querySelector('.chat'),m=c.querySelector('.chat-messages');return {overflow:m.scrollHeight>m.clientHeight,focused:c.contains(document.activeElement),hover:c.matches(':hover'),width:getComputedStyle(m).scrollbarWidth}})()`);
      assert.equal(away.overflow, true);
      assert.equal(away.focused, true);
      assert.equal(away.hover, false);
      assert.equal(away.width, 'none', `Scrollbar stayed visible after drag (${transparent ? 'transparent' : 'opaque'}): ${JSON.stringify(away)}`);
      const over = await evaluate(`(()=>{const r=document.querySelector('.chat-messages').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()`);
      await mouse('mouseMoved', over.x, over.y);
      assert.equal(await evaluate("getComputedStyle(document.querySelector('.chat-messages')).scrollbarWidth"), 'thin');
      console.log(`PASS ${transparent ? 'transparent' : 'opaque'} overlay: drag, leave, hover`);
    }
    await evaluate("document.querySelector('.player').classList.remove('chat-overlay', 'chat-transparent')");
    await mouse('mouseMoved', 0, 490);
    assert.equal(await evaluate("getComputedStyle(document.querySelector('.chat-messages')).scrollbarWidth"), 'thin');
    console.log('PASS docked chat retains its scrollbar');
  } finally {
    socket?.close();
    child.kill();
    await delay(500);
    fs.rmSync(profile, { recursive: true, force: true });
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
