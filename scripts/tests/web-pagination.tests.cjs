'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

function pages(responses) {
  const f = fixture({ onRequest: (url, input) => {
    if (url !== '/api/vods') return;
    const response = responses.shift();
    assert.ok(response, 'unexpected page request');
    assert.equal(input.cursor, response.cursor);
    if (response.error) throw new Error(response.error);
    return { ok: true, text: async () => JSON.stringify({ items: response.keys.map(key => ({ key, title: key })), nextCursor: response.next }) };
  } });
  f.state.page = 'vods';
  f.document.querySelector('#query').value = 'streamer';
  f.document.querySelector('#platform').value = 'kick';
  return f;
}

test('browser pagination deduplicates each page and stops repeated cursor cycles', async () => {
  const f = pages([
    { cursor: '', keys: ['one', 'one'], next: 'a' },
    { cursor: 'a', keys: ['one', 'two', 'two'], next: 'b' },
    { cursor: 'b', keys: ['three'], next: 'a' },
    { cursor: '', keys: ['fresh'], next: 'a' }
  ]);
  try {
    await f.load();
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one']);
    await f.load(true);
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one', 'two']);
    await f.load(true);
    assert.equal(f.state.cursor, '');
    assert.equal(f.document.querySelector('#more').hidden, true);
    await f.load();
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['fresh']);
    assert.equal(f.state.cursor, 'a', 'refresh starts a new pagination history');
  } finally { f.disconnect(); }
});

test('a failed browser page keeps Load more available for retry', async () => {
  const f = pages([
    { cursor: '', keys: ['one'], next: 'a' },
    { cursor: 'a', error: 'Provider temporarily unavailable.' },
    { cursor: 'a', keys: ['two'], next: 'a' }
  ]);
  try {
    await f.load();
    await f.load(true);
    assert.equal(f.state.cursor, 'a');
    assert.equal(f.document.querySelector('#more').hidden, false);
    await f.load(true);
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one', 'two']);
    assert.equal(f.state.cursor, '', 'a page pointing to itself ends pagination');
    assert.equal(f.document.querySelector('#more').hidden, true);
  } finally { f.disconnect(); }
});

test('provider cursor cycles end pagination even when every page has new items', async () => {
  const f = pages([
    { cursor: '', keys: ['one'], next: 'a' },
    { cursor: 'a', keys: ['two'], next: 'b' },
    { cursor: 'b', keys: ['three'], next: 'a' }
  ]);
  try {
    await f.load(); await f.load(true); await f.load(true);
    assert.equal(f.state.cursor, '');
    assert.equal(f.document.querySelector('#more').hidden, true);
    assert.deepEqual(Array.from(f.state.items, item => item.key), ['one', 'two', 'three']);
  } finally { f.disconnect(); }
});

test('different downloads of the same VOD retain their individual library entries', async () => {
  const f = fixture({ onRequest: url => {
    if (url === '/api/downloads') return { ok: true, text: async () => JSON.stringify({ items: [
      { id: 'high', key: 'same-vod', title: '1080p copy', state: 'Completed' },
      { id: 'low', key: 'same-vod', title: '720p copy', state: 'Completed' }
    ] }) };
  } });
  try {
    f.state.page = 'downloads';
    await f.load();
    assert.deepEqual(Array.from(f.state.items, item => item.id), ['high', 'low']);
  } finally { f.disconnect(); }
});
