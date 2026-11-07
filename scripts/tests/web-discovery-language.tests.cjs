'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');
const response = value => ({ ok: true, text: async () => JSON.stringify(value) });

for (const platform of ['twitch', 'kick']) {
  test(`${platform} discovery keeps language on pagination and resets it on a filter change`, async () => {
    const requests = [];
    let stale;
    const f = fixture({ onRequest: (url, input) => {
      if (url !== '/api/streams') return;
      requests.push(input);
      if (input.language === 'en' && input.cursor) return new Promise(resolve => { stale = resolve; });
      return response({ items: [{ key: input.language || 'all', channel: 'streamer', platform: 'Twitch' }], nextCursor: input.language === 'en' ? 'english-next' : '' });
    } });
    try {
      f.state.page = 'discover'; f.state.category = { id: '1', name: 'Category' };
      f.document.querySelector('#platform').value = platform;
      const language = f.document.querySelector('#discover-language'); language.value = 'en';
      await f.load();
      const more = f.load(true);
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(requests[1].language, 'en'); assert.equal(requests[1].cursor, 'english-next');
      language.value = 'fr'; language.dispatchEvent(new Event('change'));
      assert.equal(f.state.items.length, 0, 'changing language immediately removes old cards');
      stale(response({ items: [{ key: 'stale' }], nextCursor: 'stale-next' })); await more;
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(requests[2].platform, platform); assert.equal(requests[2].language, 'fr'); assert.equal(requests[2].cursor, '');
      assert.deepEqual(Array.from(f.state.items, item => item.key), ['fr']);
      assert.equal(f.state.cursor, '');
      language.value = ''; language.dispatchEvent(new Event('change'));
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(requests[3].language, ''); assert.equal(requests[3].cursor, '');
      assert.deepEqual(Array.from(f.state.items, item => item.key), ['all']);
    } finally { f.disconnect(); }
  });
}
