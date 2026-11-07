'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

test('shortcut recording and restored bindings preserve letters whose lowercase expands', () => {
  const f = fixture();
  try {
    const binding = f.normalizeHotkey({ key: 'İ' });
    assert.ok(binding, 'the Turkish dotted capital I is one valid shortcut character');
    assert.equal(binding.key, 'İ');
    const restored = f.loadHotkeys({ mute: binding }).mute;
    const pressed = f.hotkeyFromEvent({ key: 'İ' });
    assert.deepEqual(restored, pressed, 'a saved shortcut must match the same keyboard input');
  } finally { f.disconnect(); }
});

test('shortcut normalization retains case folding, named keys and invalid-character checks', () => {
  const f = fixture();
  try {
    for (const [input, expected] of [['A', 'a'], ['É', 'é'], ['𐐀', '𐐨'], ['🎮', '🎮'], ['F1', 'F1'], ['F12', 'F12']])
      assert.equal(f.normalizeHotkey({ key: input }).key, expected);
    for (const key of ['', 'ab', 'F01', 'F13', ' ', '\u200d', '\ue000', '\ud800'])
      assert.equal(f.normalizeHotkey({ key }), null, JSON.stringify(key));
    assert.equal(f.loadHotkeys({ mute: { key: 'K' } }).mute, null, 'reserved keys still apply after case folding');
  } finally { f.disconnect(); }
});
