'use strict';
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/web-player-fixture.cjs');

async function chatFixture(onSend) {
  const f = fixture({ onRequest: (url, input) => url === '/api/chat/send' ? onSend(input) : undefined });
  const p = await f.open();
  const form = p.element.querySelector('.chat-form');
  const input = form.querySelector('input');
  return { f, p, form, input, submit: () => form.dispatchEvent(new Event('submit', { cancelable: true })) };
}

test('sending chat preserves the next draft, even when it matches the sent text', async () => {
  for (const next of ['Next message', 'First message']) {
    const started = Promise.withResolvers(), pending = Promise.withResolvers();
    const { f, input, submit } = await chatFixture(async () => { started.resolve(); await pending.promise; });
    try {
      input.value = 'First message'; submit();
      await started.promise;
      input.value = next; input.dispatchEvent(new Event('input'));
      pending.resolve(); await f.flush();
      assert.equal(input.value, next);
      assert.equal(f.requests.findLast(r => r.url === '/api/chat/send').input.message, 'First message');
    } finally { pending.resolve(); f.disconnect(); }
  }
});

test('repeated chat submits share one pending send and clear an unchanged draft on success', async () => {
  const started = Promise.withResolvers(), pending = Promise.withResolvers();
  const { f, form, input, submit } = await chatFixture(async () => { started.resolve(); await pending.promise; });
  try {
    input.value = 'Only once'; submit();
    await started.promise;
    assert.equal(form.querySelector('button[type=submit]').disabled, true);
    submit(); submit();
    pending.resolve(); await f.flush();
    assert.equal(f.requests.filter(r => r.url === '/api/chat/send').length, 1);
    assert.equal(input.value, '');
    assert.equal(form.querySelector('button[type=submit]').disabled, false);
  } finally { pending.resolve(); f.disconnect(); }
});

test('a failed chat send keeps its draft and permits an explicit retry', async () => {
  let fail = true;
  const { f, form, input, submit } = await chatFixture(() => fail ?
    { ok: false, status: 503, json: async () => ({ error: 'Chat unavailable.' }) } : undefined);
  try {
    input.value = 'Try again'; submit(); await f.flush();
    assert.equal(input.value, 'Try again');
    assert.equal(form.querySelector('button[type=submit]').disabled, false);
    fail = false; submit(); await f.flush();
    assert.equal(f.requests.filter(r => r.url === '/api/chat/send').length, 2);
    assert.equal(input.value, '');
  } finally { f.disconnect(); }
});

test('stopped players do not submit chat to a released server player', async () => {
  const { f, p, input, submit } = await chatFixture(() => undefined);
  try {
    await f.playerAction(p, 'stop');
    input.value = 'Keep this draft'; submit(); await f.flush();
    assert.equal(f.requests.filter(r => r.url === '/api/chat/send').length, 0);
    assert.equal(input.value, 'Keep this draft');
  } finally { f.disconnect(); }
});
