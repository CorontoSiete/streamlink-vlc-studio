'use strict';
// Runs the application's actual geometry and pointer/keyboard handlers without a
// browser. Browser rendering and trusted input remain covered by the WebView2 test.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assets = path.resolve(__dirname, '../../src/StreamlinkVlcStudio.Infrastructure/LocalWeb/Assets');
const source = fs.readFileSync(path.join(assets, 'app.js'), 'utf8');
const css = fs.readFileSync(path.join(assets, 'app.css'), 'utf8');
const bounded = source.match(/^  const bounded = .*;$/m)?.[0];
const start = source.indexOf('  function restoreChatGeometry(p) {');
const end = source.indexOf("  window.addEventListener('resize'", start);
assert.ok(bounded && start >= 0 && end > start, 'Application geometry/handlers must be present');
const application = source.slice(start, end);

function classes(initial = []) {
  const values = new Set(initial);
  return { contains: value => values.has(value), add: (...items) => items.forEach(item => values.add(item)), remove: (...items) => items.forEach(item => values.delete(item)) };
}
function button() {
  const listeners = new Map(), captured = new Set();
  return {
    addEventListener(type, listener) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(listener); },
    emit(type, event) { for (const listener of listeners.get(type) || []) listener(event); },
    focus() {}, setPointerCapture(id) { captured.add(id); }, hasPointerCapture(id) { return captured.has(id); },
    releasePointerCapture(id) { captured.delete(id); this.emit('lostpointercapture', { pointerId: id }); }
  };
}
function fixture(width = 1440, height = 810, code = application) {
  const surface = { clientWidth: width, clientHeight: height }, properties = new Map();
  const chat = { classList: classes(), style: { setProperty: (key, value) => properties.set(key, value) } };
  const move = button(), resize = button(), feedback = {};
  let stored;
  const context = vm.createContext({ window: {}, preferences: { layout: 'overlay' }, run: action => action(),
    api(action, input) { assert.equal(action, 'chat/layout'); stored = JSON.stringify(input.layout); return Promise.resolve({ saved: true }); },
    $: selector => ({ '.video-surface': surface, '.chat': chat, '.chat-move': move, '.chat-resize': resize, '.chat-resize-feedback': feedback })[selector] });
  vm.runInContext(bounded + '\n' + code, context);
  const p = { element: { classList: classes(['chat-overlay']), hidden: false }, chatX: 1, chatY: 0 };
  context.setupChatControls(p);
  const box = () => context.chatGeometry(p);
  const gap = () => surface.clientHeight - box().top - box().height;
  const bottom = () => assert.ok(Math.abs(gap()) < 1e-8, `Expected bottom gap 0, got ${gap()}`);
  const event = extra => ({ button: 0, isPrimary: true, pointerId: 1, clientX: 0, clientY: 0, preventDefault() {}, stopPropagation() {}, ...extra });
  const drag = (handle, dx, dy, finish = 'pointerup') => { handle.emit('pointerdown', event()); handle.emit('pointermove', event({ clientX: dx, clientY: dy })); handle.emit(finish, event({ clientX: dx, clientY: dy })); };
  const key = (handle, key, shiftKey = false) => handle.emit('keydown', event({ key, shiftKey }));
  return { surface, properties, context, p, box, gap, bottom, move, resize, drag, key, chat, event, stored: () => stored };
}

let passed = 0;
function test(name, run) { run(); ++passed; console.log('PASS ' + name); }

test('original geometry reproduces the exact 52px bottom gap', () => {
  const old = application.replace('maxHeight = Math.max(0, surface.clientHeight - 8)', 'maxHeight = Math.max(0, surface.clientHeight - 60)')
    .replace('maxHeight, defaultHeight);', 'maxHeight, maxHeight);');
  assert.notEqual(old, application);
  const f = fixture(1440, 810, old); f.drag(f.move, 0, 10000);
  assert.equal(f.gap(), 52); assert.throws(f.bottom, /Expected bottom gap 0, got 52/);
});
test('default-size pointer drag reaches the bottom without resizing', () => {
  const f = fixture(), before = f.box(); f.drag(f.move, 0, 10000); f.bottom();
  assert.equal(f.box().width, before.width); assert.equal(f.box().height, before.height);
  assert.equal(JSON.parse(f.stored()).y, 1); assert.equal(f.chat.classList.contains('is-moving'), false);
});
test('keyboard movement reaches the bottom and clamps, with Home resetting position', () => {
  const f = fixture(); for (let i = 0; i < 100; ++i) f.key(f.move, 'ArrowDown'); f.bottom();
  f.key(f.move, 'ArrowUp', true); assert.equal(f.gap(), 40);
  f.key(f.move, 'ArrowDown', true); f.bottom(); f.key(f.move, 'ArrowDown', true); f.bottom();
  f.key(f.move, 'Home'); assert.equal(f.box().top, 8); assert.equal(f.box().left + f.box().width, 1432);
});
test('CSS permits the maximum height requested by resize, and Home resets size', () => {
  const inset = Number(css.match(/\.chat-overlay \.chat \{[^\n]*max-height: calc\(100% - (\d+)px\)/)?.[1]);
  assert.equal(inset, 8);
  const f = fixture(); f.drag(f.resize, 0, 10000); f.bottom(); assert.equal(f.box().top, 8);
  assert.equal(Number.parseFloat(f.properties.get('--overlay-height')), f.surface.clientHeight - inset);
  f.key(f.resize, 'Home'); assert.equal(f.box().height, 750); assert.equal(f.gap(), 52);
});
test('moving and resizing clamp at every video edge across desktop, phone, grid and fullscreen dimensions', () => {
  for (const [width, height] of [[1440, 810], [304, 180], [288, 180], [640, 220], [768, 432], [1920, 1080]]) {
    for (const desiredHeight of [undefined, 140, 240, 10000]) {
      const f = fixture(width, height); f.p.chatHeight = desiredHeight;
      f.drag(f.move, -10000, -10000); assert.equal(f.box().left, 8); assert.equal(f.box().top, 8);
      f.drag(f.move, 10000, 10000); f.bottom(); assert.equal(f.box().left + f.box().width, width - 8);
      assert.ok(f.box().width <= width - 16); assert.ok(f.box().height <= height - 8);
    }
  }
});
test('lower-left resize keeps a moved overlay top-right corner fixed and can grow to the bottom', () => {
  const f = fixture(); f.p.chatHeight = 300; f.drag(f.move, -120, 10000);
  let anchor = f.box(); f.drag(f.resize, 20, -20);
  assert.equal(f.box().top, anchor.top); assert.equal(f.box().left + f.box().width, anchor.left + anchor.width);
  assert.equal(f.box().height, anchor.height - 20);
  anchor = f.box(); f.drag(f.resize, 0, 10000); f.bottom();
  assert.equal(f.box().top, anchor.top); assert.equal(f.box().left + f.box().width, anchor.left + anchor.width);
});
test('Escape, pointer cancellation and lost capture restore geometry without saving the attempted move', () => {
  for (const cancel of ['Escape', 'pointercancel', 'lostpointercapture']) {
    const f = fixture(); f.p.chatHeight = 300; f.drag(f.move, -120, 10000);
    const before = JSON.stringify(f.box()), stored = f.stored();
    f.move.emit('pointerdown', f.event()); f.move.emit('pointermove', f.event({ clientX: -50, clientY: -40 }));
    assert.notEqual(JSON.stringify(f.box()), before);
    if (cancel === 'Escape') f.key(f.move, 'Escape'); else f.move.emit(cancel, f.event());
    assert.equal(JSON.stringify(f.box()), before); assert.equal(f.stored(), stored);
    assert.equal(f.chat.classList.contains('is-moving'), false);
  }
});
test('bottom alignment survives viewport changes and restoring saved preferences', () => {
  const f = fixture(); f.p.chatHeight = 300; f.drag(f.move, -120, 10000);
  const saved = JSON.parse(f.stored());
  for (const [width, height] of [[304, 180], [640, 360], [1920, 1080], [1440, 810]]) {
    f.surface.clientWidth = width; f.surface.clientHeight = height; f.context.sizeChat(f.p); f.bottom();
    assert.equal(f.p.chatHeight, 300); assert.equal(f.p.chatY, 1);
  }
  const restored = fixture(); restored.p.chatLayout = saved; restored.context.restoreChatGeometry(restored.p);
  restored.context.sizeChat(restored.p); restored.bottom(); assert.equal(JSON.stringify(restored.box()), JSON.stringify(f.box()));
});
test('player geometry stays independent while both overlays remain at the bottom', () => {
  const a = fixture(640, 360), b = fixture(640, 360); a.p.chatHeight = b.p.chatHeight = 200;
  a.drag(a.move, -20, 10000); b.drag(b.move, -20, 10000); const before = JSON.stringify(b.box());
  a.key(a.move, 'ArrowLeft', true); assert.equal(JSON.stringify(b.box()), before); a.bottom(); b.bottom();
});
console.log(`${passed} passed, 0 failed. These are code-level regressions; they do not establish browser rendering or trusted-input results.`);
