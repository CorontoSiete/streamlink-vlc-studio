'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

test('native HLS wins when both native playback and hls.js are available', async () => {
  const f = fixture({ media: 'both', userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 26_0 like Mac OS X) AppleWebKit/605.1.15 Brave/1.0 Mobile/15E148' });
  const player = await f.open();
  assert.equal(player.video.url, '/media/fixture');
  assert.equal(player.hls, null);
  assert.deepEqual(player.video.attachedVolumes, [1]);
});

test('desktop playback keeps hls.js when both playback paths are available', async () => {
  const player = await fixture({ media: 'both' }).open();
  assert.ok(player.hls);
  assert.equal(player.video.url, undefined);
});

test('hls.js remains the fallback where native HLS is unavailable', async () => {
  const player = await fixture({ media: 'hls' }).open();
  assert.ok(player.hls);
  assert.equal(player.video.url, undefined);
});

test('a play event does not hide loading status before frames can play', async () => {
  const f = fixture({ media: 'native' });
  const player = await f.open();
  const status = player.element.querySelector('.player-status');
  assert.equal(status.textContent, 'Loading video…');
  player.video.dispatchEvent(new Event('play'));
  assert.equal(status.textContent, 'Loading video…');
  player.video.dispatchEvent(new Event('playing'));
  assert.equal(status.textContent, '');
});
