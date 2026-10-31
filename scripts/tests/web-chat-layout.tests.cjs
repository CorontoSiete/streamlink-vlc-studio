'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

const first = { width: 420, height: 260, x: .23, y: .87 };
const other = { width: 280, height: 400, x: .9, y: .1 };
const fields = p => ({ width: p.chatWidth, height: p.chatHeight, x: p.chatX, y: p.chatY });
async function save(f, p, layout) {
  Object.assign(p, { chatWidth: layout.width, chatHeight: layout.height, chatX: layout.x, chatY: layout.y });
  await f.storeChatGeometry(p);
}
function key(p, handle, key) {
  const event = new Event('keydown', { cancelable: true }); Object.assign(event, { key, shiftKey: false });
  p.element.querySelector('.chat-' + handle).dispatchEvent(event);
}

test('layouts restore per resolved channel and platform across close, reopen and fresh browser state', async () => {
  const f = fixture(); const p = await f.open(); await save(f, p, first);
  f.target({ channel: 'other' }); const second = await f.open();
  assert.deepEqual(fields(second), { width: null, height: null, x: 1, y: 0 });
  await save(f, second, other);
  f.target({ platform: 'Kick', channel: 'streamer' }); const kick = await f.open();
  assert.deepEqual(fields(kick), { width: null, height: null, x: 1, y: 0 });
  await save(f, kick, { ...other, x: 0 });
  await f.closePlayer(p); f.target({ platform: 'Twitch', channel: ' STREAMER ' });
  assert.deepEqual(fields(await f.open()), first);
  const restarted = fixture({ layouts: f.layouts });
  assert.equal(restarted.storage.size, 0, 'no browser data is required to restore the server layout');
  assert.deepEqual(fields(await restarted.open()), first);
  restarted.target({ channel: 'other' }); assert.deepEqual(fields(await restarted.open()), other);
});

test('quality changes, replay, MP4 and changed player IDs use the durable channel layout', async () => {
  const f = fixture(); let p = await f.open(); await save(f, p, first);
  const originalId = p.id;
  p = await f.openPlayer({ ...p.source, quality: '720p' }, p);
  assert.notEqual(p.id, originalId); assert.deepEqual(fields(p), first);
  f.target({ isVod: true }); p = await f.openPlayer({ key: 'replay' }, p);
  assert.deepEqual(fields(p), first);
  f.target({ isMp4: true }); p = await f.openPlayer({ downloadId: 'download' }, p);
  assert.deepEqual(fields(p), first);
  f.target({ channel: 'other' }); p = await f.openPlayer({ input: 'other' }, p);
  assert.deepEqual(fields(p), { width: null, height: null, x: 1, y: 0 });
});

test('legacy shared geometry cannot leak into another channel or override saved server data', async () => {
  const storage = new Map([['stream-studio-preferences', JSON.stringify({ layout: 'overlay', overlayWidth: 900, overlayHeight: 600, overlayX: 0, overlayY: 1 })]]);
  const f = fixture({ storage, layouts: new Map([['twitch:streamer', first]]) });
  assert.deepEqual(fields(await f.open()), first);
  f.target({ channel: 'new' });
  assert.deepEqual(fields(await f.open()), { width: null, height: null, x: 1, y: 0 });
});

test('Home persists responsive size and position resets for only the current stream', async () => {
  const f = fixture({ layouts: new Map([['twitch:streamer', first], ['twitch:other', other]]) });
  f.setPreferences({ layout: 'overlay' }); const p = await f.open();
  key(p, 'resize', 'Home'); await f.flush();
  assert.deepEqual(f.layouts.get('twitch:streamer'), { width: null, height: null, x: .23, y: .87 });
  key(p, 'move', 'Home'); await f.flush();
  assert.deepEqual(f.layouts.get('twitch:streamer'), { width: null, height: null, x: 1, y: 0 });
  assert.deepEqual(f.layouts.get('twitch:other'), other);
  assert.deepEqual(fields(await fixture({ layouts: f.layouts }).open()), { width: null, height: null, x: 1, y: 0 });
});

test('viewport clamping does not overwrite the preferred size or relative position', async () => {
  const f = fixture({ layouts: new Map([['twitch:streamer', first]]) });
  f.setPreferences({ layout: 'overlay' }); const p = await f.open();
  const surface = p.element.querySelector('.video-surface'); surface.clientWidth = 304; surface.clientHeight = 180;
  const small = f.chatGeometry(p); assert.ok(small.width <= 288 && small.height <= 172);
  assert.deepEqual(fields(p), first); assert.equal(f.layoutWrites.length, 0);
  surface.clientWidth = 1440; surface.clientHeight = 810;
  assert.equal(f.chatGeometry(p).width, first.width); assert.equal(f.chatGeometry(p).height, first.height);
});

test('queued commits finish in order before close/reopen, including when browser storage is blocked', async () => {
  const f = fixture(); f.localStorage.getItem = f.localStorage.setItem = () => { throw new Error('Blocked'); };
  const p = await f.open();
  const a = save(f, p, first), b = save(f, p, other);
  await f.closePlayer(p); await Promise.all([a, b]);
  assert.deepEqual(fields(await f.open()), other);
  assert.equal(f.layoutWrites.length, 2);
});

test('disk/network save failures are visible and a subsequent adjustment retries successfully', async () => {
  const f = fixture(); const p = await f.open(); f.failSave(true);
  await save(f, p, first); assert.equal(f.layouts.size, 0);
  assert.match(f.document.querySelector('#notice').children[0].textContent, /Could not save this stream/);
  f.failSave(false); await save(f, p, other);
  assert.deepEqual(fields(await fixture({ layouts: f.layouts }).open()), other);
});
