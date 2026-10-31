'use strict';
// Run the full web application against deterministic DOM, media and server adapters.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.resolve(__dirname, '../../../src/StreamlinkVlcStudio.Infrastructure/LocalWeb/Assets/app.js'), 'utf8');

function fixture({ storage = new Map(), volumes = new Map(), layouts = new Map(), media = 'hls', userAgent = 'Desktop', code = source } = {}) {
  const pendingEvents = [];
  class Element extends EventTarget {
    constructor() {
      super();
      this.elements = new Map(); this.children = []; this.dataset = {}; this.value = ''; this.checked = false;
      const classes = new Set();
      this.classList = { add: (...items) => items.forEach(item => classes.add(item)), remove: (...items) => items.forEach(item => classes.delete(item)),
        contains: item => classes.has(item), toggle: (item, active) => active ? classes.add(item) : classes.delete(item) };
      this.style = { setProperty() {} }; this.clientWidth = 1440; this.clientHeight = 810;
    }
    querySelector(selector) {
      if (!this.elements.has(selector)) this.elements.set(selector, selector === 'video' ? new Video() : new Element());
      return this.elements.get(selector);
    }
    querySelectorAll() { return []; }
    append(...elements) { for (const element of elements) { if (element && typeof element === 'object') element.parentElement = this; this.children.push(element); } }
    replaceChildren(...elements) { this.children = []; this.append(...elements); }
    remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(element => element !== this); }
    cloneNode() { return new Element(); }
    setAttribute() {}
    removeAttribute() {}
    focus() {}
    close() {}
    scrollIntoView() {}
  }
  class Video extends Element {
    constructor() { super(); this.level = 1; this.silent = false; this.paused = true; this.duration = 100; this.currentTime = 0; this.attachedVolumes = []; }
    get volume() { return this.level; }
    set volume(value) {
      assert.ok(Number.isFinite(value) && value >= 0 && value <= 1);
      if (this.level !== value) { this.level = value; pendingEvents.push(() => this.dispatchEvent(new Event('volumechange'))); }
    }
    get muted() { return this.silent; }
    set muted(value) { if (this.silent !== value) { this.silent = value; pendingEvents.push(() => this.dispatchEvent(new Event('volumechange'))); } }
    set src(value) { this.url = value; this.attachedVolumes.push(this.volume); }
    canPlayType() { return media === 'native' || media === 'both' ? 'probably' : ''; }
    play() { this.firstPlayVolume ??= this.volume; this.paused = false; return Promise.resolve(); }
    pause() { this.paused = true; }
    load() {}
  }
  class Hls {
    static isSupported() { return media === 'hls' || media === 'both'; }
    static Events = { ERROR: 'error' };
    loadSource() {}
    attachMedia(video) { video.attachedVolumes.push(video.volume); }
    on() {}
    destroy() {}
  }
  const document = new Element();
  document.createElement = document.createElementNS = document.createTextNode = () => new Element();
  document.querySelector('#player-template').content = { firstElementChild: new Element() };
  const localStorage = { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) };
  const players = new Map(), layoutWrites = [], volumeWrites = [], closeCalls = [];
  let failLayoutSave = false, failVolumeSave = false, failProgressSave = false;
  const layoutKey = p => p.platform.trim().toLowerCase() + ':' + p.channel.trim().toLowerCase();
  const volumeKey = p => p.platform.trim() + ':' + p.channel.trim().toLowerCase();
  let opened = 0, target = { channel: 'streamer', platform: 'Twitch', isVod: false, isMp4: false };
  const context = vm.createContext({ document, localStorage, Event, URL, URLSearchParams, Option: Element, Hls, AbortController, DOMException, setTimeout, clearTimeout,
    navigator: { userAgent, platform: 'Win32', maxTouchPoints: 0 },
    ResizeObserver: class { observe() {} disconnect() {} },
    window: Object.assign(new EventTarget(), { Hls }), location: { hash: '', pathname: '/', search: '' }, setInterval() {},
    fetch: async (url, options) => {
      const input = JSON.parse(options.body || '{}');
      let result = {};
      if (url === '/api/open') {
        result = { ...target, id: `player-${++opened}`, key: `key-${opened}`, title: 'Display title',
          chatLayout: layouts.get(layoutKey(target)) ?? null, url: '/media/fixture', quality: input.quality, resume: 0 };
        players.set(result.id, { ...target });
      }
      if (url === '/api/chat/layout') {
        assert.equal(options.keepalive, true, 'a dispatched save can finish during page navigation');
        layoutWrites.push(input);
        if (failLayoutSave) return { ok: false, status: 500, json: async () => ({ error: 'Disk full' }) };
        const resolved = players.get(input.player); assert.ok(resolved, 'save is bound to an open server player');
        layouts.set(layoutKey(resolved), input.layout);
        result = { saved: true };
      }
      if (url === '/api/volume/save') {
        assert.equal(options.keepalive, true);
        const resolved = players.get(input.player); assert.ok(resolved, 'volume belongs to an open server player');
        volumeWrites.push(input);
        if (failVolumeSave) return { ok: false, status: 503, json: async () => ({ error: 'Volume save failed.' }) };
        volumes.set(volumeKey(resolved), input.volume);
        result = { saved: true };
      }
      if (url === '/api/progress' && failProgressSave)
        return { ok: false, status: 503, json: async () => ({ error: 'Progress save failed.' }) };
      if (url === '/api/close') { closeCalls.push(input.player); players.delete(input.player); }
      return { ok: true, text: async () => JSON.stringify(result) };
    }
  });
  // Only replace startup authentication. All opening, replacement, closing, audio
  // selection, event handlers and persistence run from the actual application.
  assert.ok(code.includes('  restoreSession();'));
  vm.runInContext(code.replace('  restoreSession();', '  globalThis.playerTest = { state, openPlayer, closePlayer, playerAction, selectPlayer, disconnect, flushVolumes, storeChatGeometry, resizeChat, positionChat, chatGeometry, setPreferences: value => Object.assign(preferences, value), seedVolumes: values => { for (const [key, volume] of values) streamVolumes.set(key, volume); }, flush: () => queue };'), context);
  const app = context.playerTest;
  app.state.authenticated = true;
  app.state.settings = { defaultQuality: 'best', qualities: ['best', '720p'], lowLatency: true };
  app.seedVolumes(volumes);
  return { ...app, storage, volumes, volumeWrites, layouts, layoutWrites, closeCalls, localStorage, document,
    failSave: value => { failLayoutSave = value; },
    failVolumeSave: value => { failVolumeSave = value; },
    failProgressSave: value => { failProgressSave = value; },
    target: value => { target = { ...target, ...value }; },
    open: (input = {}) => app.openPlayer({ input: 'pasted source', ...input }),
    events: async () => { while (pendingEvents.length) pendingEvents.shift()(); await app.flushVolumes(); }
  };
}

module.exports = { fixture, source };
