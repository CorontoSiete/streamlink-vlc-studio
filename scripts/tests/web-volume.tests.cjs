'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture, source } = require('./helpers/web-player-fixture.cjs');
const volumeKey = (platform = 'Twitch', channel = 'streamer') => `${platform}:${channel}`;

function wheel(element, deltaY) {
  const event = new Event('wheel', { cancelable: true });
  Object.defineProperty(event, 'deltaY', { value: deltaY });
  element.dispatchEvent(event);
  return event;
}

async function expectVolume(f, expected) {
  const p = await f.open();
  assert.equal(p.video.volume, expected);
  assert.equal(p.video.firstPlayVolume, expected, 'restore before the first play call');
  assert.deepEqual(p.video.attachedVolumes, [expected], 'restore before attaching HLS/native/MP4 media');
  await f.events();
  return p;
}

test('a changed volume survives close/reopen and a fresh browser session for the same account', async () => {
  const f = fixture(), p = await expectVolume(f, 1);
  p.video.volume = 0.37; await f.events();
  assert.equal(f.volumes.get(volumeKey()), 0.37);
  await f.closePlayer(p);
  await expectVolume(f, 0.37);
  await expectVolume(fixture({ volumes: f.volumes }), 0.37);
});

test('wheel over the volume bar adjusts by 5%, prevents page scrolling and saves the level', async () => {
  const f = fixture(), p = await f.open();
  const volume = p.element.querySelector('.video-volume');
  volume.value = '0.4'; volume.dispatchEvent(new Event('input')); await f.events();
  assert.equal(p.video.volume, 0.4);

  assert.equal(wheel(volume, -100).defaultPrevented, true);
  assert.equal(p.video.volume, 0.45);
  assert.equal(volume.value, 0.45);
  assert.equal(wheel(volume, 100).defaultPrevented, true);
  assert.equal(p.video.volume, 0.4);
  await f.events();
  assert.equal(f.volumes.get(volumeKey()), 0.4);

  assert.equal(wheel(p.element.querySelector('.video-seek'), -100).defaultPrevented, false);
  assert.equal(p.video.volume, 0.4, 'wheel outside the volume bar does not change volume');
  assert.equal(wheel(volume, 0).defaultPrevented, false);
  assert.equal(p.video.volume, 0.4, 'horizontal wheel input does not change volume');

  for (let i = 0; i < 20; i++) wheel(volume, 100);
  assert.equal(p.video.volume, 0);
  for (let i = 0; i < 25; i++) wheel(volume, -100);
  assert.equal(p.video.volume, 1);
  await f.events();
  assert.equal(f.volumes.get(volumeKey()), 1);
});

test('wheel un-mutes the selected player and keeps an inactive player muted', async () => {
  const f = fixture(), first = await f.open();
  const firstVolume = first.element.querySelector('.video-volume');
  first.video.volume = 0.3; await f.events();
  await f.playerAction(first, 'mute'); await f.events();
  assert.equal(first.video.muted, true);
  wheel(firstVolume, -100); await f.events();
  assert.equal(first.video.volume, 0.35);
  assert.equal(first.video.muted, false);

  f.target({ channel: 'other' });
  const selected = await f.open(); await f.events();
  assert.equal(first.video.muted, true);
  wheel(firstVolume, 100); await f.events();
  assert.equal(first.video.volume, 0.3);
  assert.equal(first.video.muted, true);
  assert.equal(selected.video.muted, false);
  assert.equal(f.volumes.get(volumeKey()), 0.3);
});

test('0%, 100%, native HLS and MP4 restore before media attachment and playback', async () => {
  for (const media of ['hls', 'native', 'mp4']) for (const volume of [0, 0.42, 1]) {
    const f = fixture({ media, volumes: new Map([[volumeKey(), volume]]) });
    if (media === 'mp4') f.target({ isVod: true, isMp4: true });
    await expectVolume(f, volume);
  }
});

test('quality reload, stop/reload and replay replacement preserve the current level', async () => {
  const f = fixture(); let p = await f.open();
  p.video.volume = 0.28; // A replacement must also capture a still-pending volumechange.
  p = await f.openPlayer({ ...p.source, quality: '720p' }, p);
  assert.equal(p.video.firstPlayVolume, 0.28);
  assert.equal(f.state.players.length, 1);
  await f.playerAction(p, 'stop');
  await f.playerAction(p, 'reload');
  p = f.state.players[0];
  assert.equal(p.video.firstPlayVolume, 0.28);
  f.target({ isVod: true });
  p = await f.openPlayer({ key: 'resolved-replay' }, p);
  assert.equal(p.video.firstPlayVolume, 0.28);
  await f.events();
});

test('resolved channel identity is case-insensitive, per platform, and shared with VODs', async () => {
  const f = fixture(), first = await f.open();
  first.video.volume = 0.31; await f.events();
  f.target({ channel: 'other' });
  const other = await expectVolume(f, 1);
  other.video.volume = 0.62; await f.events();
  f.target({ platform: 'Kick', channel: 'streamer' });
  const kick = await expectVolume(f, 1);
  kick.video.volume = 0.19; await f.events();
  f.target({ platform: 'Twitch', channel: ' StReAmEr ', isVod: true });
  await expectVolume(f, 0.31);
  assert.equal(f.volumes.get(volumeKey()), 0.31);
  assert.equal(f.volumes.get(volumeKey('Twitch', 'other')), 0.62);
  assert.equal(f.volumes.get(volumeKey('Kick')), 0.19);
});

test('inactive-player muting, Never mute and manual mute do not overwrite levels', async () => {
  const f = fixture(), first = await f.open();
  first.video.volume = 0.33; await f.events();
  f.target({ channel: 'other' });
  const other = await f.open(); await f.events();
  assert.equal(first.video.muted, true);
  assert.equal(first.video.volume, 0.33);
  assert.equal(f.volumes.get(volumeKey()), 0.33);
  f.selectPlayer(first.id); await f.events();
  assert.equal(first.video.muted, false);
  assert.equal(other.video.muted, true);
  other.neverMute = true;
  f.selectPlayer(first.id); await f.events();
  assert.equal(other.video.muted, false);
  first.video.muted = true; await f.events();
  f.target({ channel: 'streamer' });
  const replacement = await f.openPlayer(first.source, first); await f.events();
  assert.equal(replacement.video.muted, true);
  assert.equal(replacement.video.volume, 0.33);
  assert.equal(f.volumes.get(volumeKey()), 0.33);
});

test('close flushes pending changes, while disposed players cannot overwrite account volume', async () => {
  const f = fixture(), closed = await f.open();
  closed.video.volume = 0.23;
  await f.closePlayer(closed);
  let p = await expectVolume(f, 0.23);
  closed.video.volume = 0.91; await f.events();
  assert.equal(f.volumes.get(volumeKey()), 0.23);
  p.video.volume = 0.47;
  await f.events();
  f.disconnect(); await f.events();
  assert.equal(f.volumes.get(volumeKey()), 0.47);
  await expectVolume(fixture({ volumes: f.volumes }), 0.47);
});

test('failed saves still release the player and close its server session', async () => {
  const volume = fixture(), live = await volume.open();
  live.video.volume = 0.23;
  volume.failVolumeSave(true);
  await assert.rejects(volume.closePlayer(live), /Volume save failed/);
  assert.equal(live.closed, true);
  assert.equal(volume.state.players.length, 0);
  assert.deepEqual(volume.closeCalls, [live.id]);

  const progress = fixture();
  progress.target({ isVod: true });
  const vod = await progress.open();
  progress.failProgressSave(true);
  await assert.rejects(progress.closePlayer(vod), /Progress save failed/);
  assert.equal(vod.closed, true);
  assert.equal(progress.state.players.length, 0);
  assert.deepEqual(progress.closeCalls, [vod.id]);
});

test('failed VOD progress save still stops playback and closes its server session', async () => {
  const f = fixture();
  f.target({ isVod: true });
  const p = await f.open();
  f.failProgressSave(true);
  await assert.rejects(f.playerAction(p, 'stop'), /Progress save failed/);
  assert.equal(p.stopped, true);
  assert.equal(p.video.paused, true);
  assert.deepEqual(f.closeCalls, [p.id]);
});

test('volumes follow the web account across browsers without leaking to another account', async () => {
  const alice = fixture(), p = await alice.open();
  p.video.volume = 0.56; await alice.events();
  await expectVolume(fixture({ volumes: alice.volumes }), 0.56);
  const bob = fixture();
  await expectVolume(bob, 1);
  assert.equal(bob.volumes.has(volumeKey()), false);
});

test('browser storage being blocked does not affect account volume persistence', async () => {
  const f = fixture();
  f.localStorage.getItem = () => { throw new Error('Storage blocked'); };
  f.localStorage.setItem = () => { throw new Error('Storage full'); };
  const p = await expectVolume(f, 1);
  p.video.volume = 0.48; await f.events();
  await f.closePlayer(p);
  await expectVolume(f, 0.48);
  assert.equal(f.volumes.get(volumeKey()), 0.48);
  await expectVolume(fixture({ volumes: f.volumes }), 0.48);
});

test('a restored level remains remembered if storage becomes unavailable later', async () => {
  const f = fixture({ volumes: new Map([[volumeKey(), 0.39]]) });
  const p = await expectVolume(f, 0.39);
  f.localStorage.getItem = () => { throw new Error('Storage blocked'); };
  await f.closePlayer(p);
  await expectVolume(f, 0.39);
});

test('replacing a player with a different streamer uses that streamer\'s volume', async () => {
  const f = fixture(), p = await f.open();
  p.video.volume = 0.17; await f.events();
  f.target({ channel: 'other' });
  const replacement = await f.openPlayer({ input: 'other' }, p);
  assert.equal(replacement.video.volume, 1);
  assert.equal(f.volumes.get(volumeKey()), 0.17);
});

test('regression check: the original missing save/restore behavior fails reopen', async () => {
  const originalBehavior = source.replace('    restoreVolume(p, replace);', '')
    .replace(/rememberVolume\((?:p|replace)\);/g, '');
  assert.notEqual(originalBehavior, source);
  const f = fixture({ code: originalBehavior }), p = await f.open();
  p.video.volume = 0.37; await f.events();
  await f.closePlayer(p);
  const reopened = await f.open();
  assert.equal(reopened.video.volume, 1, 'the reported full-volume reset is reproduced');
  assert.notEqual(reopened.video.volume, 0.37);
});
