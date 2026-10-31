'use strict';
const assert = require('node:assert/strict');

module.exports = async ({ cdp, evaluate, wait, capture, viewport, settingsFixture, getSavedSettings }) => {
  const key = async (key, modifiers = 0, code = '', repeat = false) => {
    const windowsVirtualKeyCode = ({ Escape: 27, Tab: 9, Enter: 13, Shift: 16, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40, '[': 219, ']': 221, '/': 191 })[key] || key.toUpperCase().charCodeAt(0);
    code ||= key.length === 1 && /[a-z]/i.test(key) ? 'Key' + key.toUpperCase() : key;
    await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key, code, windowsVirtualKeyCode, modifiers, autoRepeat: repeat });
    await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key, code, windowsVirtualKeyCode, modifiers });
  };
  const focus = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).focus()`);
  const click = async selector => {
    await evaluate(`document.querySelector(${JSON.stringify(selector)}).scrollIntoView({ block: 'center' })`);
    const point = await evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2}})()`);
    await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', clickCount: 1 });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', ...point, button: 'left', clickCount: 1 });
  };
  const mouse = async (button, modifiers = 0, selector = '#studio') => {
    const point = await evaluate(`(()=>{const r=document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();return {x:r.left+Math.min(10,r.width/2),y:r.top+Math.min(10,r.height/2)}})()`);
    await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button, buttons: button === 'back' ? 8 : 16, clickCount: 1, modifiers });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', ...point, button, buttons: 0, clickCount: 1, modifiers });
  };
  const page = async name => {
    await evaluate(`document.querySelector('button[data-page=${name}]').click()`);
    try { await ready(name); } catch (error) {
      console.error(await evaluate("({page:document.querySelector('#studio').dataset.page,classes:document.querySelector('#studio').className,busy:document.querySelector('#cards').getAttribute('aria-busy'),focus:document.activeElement.outerHTML.slice(0,200)})"));
      throw error;
    }
    await focus('#library');
  };
  const ready = name => wait(`document.querySelector('#studio')?.dataset.page==='${name}' && document.querySelector('#cards').getAttribute('aria-busy')==='false' && !document.querySelector('#studio').classList.contains('watching')`);
  const selected = () => evaluate("document.querySelector('.is-selected')?.dataset.playerId");
  const selectedVideo = "document.querySelector('#'+document.querySelector('.is-selected').querySelector('[aria-controls]').getAttribute('aria-controls')+' video')";
  const grid = () => evaluate("document.querySelector('#players').classList.contains('grid')");
  const open = async channel => {
    const count = await evaluate("document.querySelectorAll('#players video').length");
    await key('k', 2);
    await evaluate(`document.querySelector('#open-query').value=${JSON.stringify(channel)};document.querySelector('#open-form').requestSubmit()`);
    await wait(`!document.querySelector('#open-dialog').open && document.querySelectorAll('#players video').length===${count + 1} && document.querySelectorAll('#players video')[${count}].currentTime>0.1`);
    return selected();
  };
  const record = async (id, value, modifiers = 0) => { await focus('#hotkey-' + id); await key(value, modifiers); };
  const savedValue = id => evaluate(`document.querySelector('#hotkey-${id}').value`);
  const save = async () => {
    await wait("document.querySelector('#save-status').textContent==='All changes saved to your account.'");
    await focus('#library');
  };
  const reload = async () => {
    await save();
    await cdp('Page.reload');
    await wait("document.readyState==='complete' && !document.querySelector('#studio').hidden && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
    await focus('#library');
  };

  // Reproduce the reported Brave workflow using actual mouse/key events and
  // account-backed shortcut persistence between navigation and reload.
  await page('settings');
  await focus('#hotkey-back'); await mouse('back');
  await record('nextTab', 'ArrowRight'); await record('previousTab', 'ArrowLeft');
  await page('following'); await page('downloads');
  await mouse('back'); await ready('following');
  const configuredFirst = await open('Northstar'), configuredSecond = await open('Pixelquest');
  await key('ArrowRight'); assert.equal(await selected(), configuredFirst);
  await key('ArrowLeft'); assert.equal(await selected(), configuredSecond);
  await reload(); await page('settings');
  assert.equal(await savedValue('back'), 'Mouse4');
  assert.equal(await savedValue('nextTab'), 'ArrowRight'); assert.equal(await savedValue('previousTab'), 'ArrowLeft');
  await click('#reset-hotkeys'); await reload(); await ready('following');
  console.log('PASS reported workflow: record Mouse4 and arrow keys, navigate immediately, switch both ways, and reload with automatic saving.');

  // Back is application navigation history, including same-page clicks and category context.
  await page('downloads'); await key('Escape'); await ready('following');
  await page('downloads'); await page('recent');
  await key('Escape'); await ready('downloads'); await key('Escape'); await ready('following');
  await key('Escape'); await ready('following');
  await page('downloads'); await page('downloads'); await key('Escape'); await ready('following');
  await evaluate("document.querySelector('[data-filter=twitch]').click();document.querySelector('#live-filter').checked=true;document.querySelector('#channel-sort').value='name'");
  await page('discover');
  await evaluate("document.querySelector('.card-main').click()");
  await wait("document.querySelector('#breadcrumbs').textContent.includes('Adventure games') && document.querySelector('.card-main')?.getAttribute('aria-label').startsWith('Watch')");
  await page('downloads'); await key('Escape'); await ready('discover');
  assert.ok(await evaluate("document.querySelector('#breadcrumbs').textContent.includes('Adventure games')"), 'Back restores a category, not its parent page');
  await key('Escape'); await ready('discover');
  assert.equal(await evaluate("document.querySelector('#breadcrumbs').textContent"), '');
  await key('Escape'); await ready('following');
  assert.ok(await evaluate("document.querySelector('[data-filter=twitch]').getAttribute('aria-pressed')==='true' && document.querySelector('#live-filter').checked && document.querySelector('#channel-sort').value==='name'"), 'Back restores library filters');
  await page('vods');
  await evaluate("document.querySelector('#query').value='Northstar';document.querySelector('#search-form').requestSubmit()");
  await wait("document.querySelector('#cards').getAttribute('aria-busy')==='false' && document.querySelectorAll('.card').length===1");
  await page('downloads'); await key('Escape'); await ready('vods');
  assert.equal(await evaluate("document.querySelector('#query').value"), 'Northstar', 'Back restores the previous video channel');
  await key('Escape'); await ready('following');
  console.log('PASS previous-page history, repeated Back, same-page clicks, category context and library filters.');

  const first = await open('Northstar'), second = await open('Pixelquest'), third = await open('Afterhours');
  await key(']'); assert.equal(await selected(), first, 'next wraps');
  assert.ok(await evaluate(`document.activeElement===${selectedVideo}`), 'tab switching transfers keyboard focus');
  await key('['); assert.equal(await selected(), third, 'previous wraps');
  await evaluate(`document.querySelector('#player-${third} [data-action=left]').click()`);
  await key(']'); assert.equal(await selected(), second, 'switching follows reordered stream tabs');
  await evaluate(`document.querySelector('#player-${first} [data-action=close]').click()`);
  await wait("document.querySelectorAll('#players video').length===2");
  await key(']'); assert.equal(await selected(), third, 'closed streams are skipped');
  await key('g'); assert.equal(await grid(), true);
  assert.equal(await evaluate("document.querySelectorAll('.player:not([hidden])').length"), 2);
  await key('g', 0, 'KeyG', true); assert.equal(await grid(), true, 'held toggles do not repeat');
  await key('g'); assert.equal(await grid(), false);

  await evaluate(`${selectedVideo}.volume=0.43`);
  await key('a');
  assert.ok(await evaluate(`document.querySelector('#player-${third} .never-mute').checked`));
  await key(']'); assert.equal(await selected(), second);
  assert.ok(await evaluate("[...document.querySelectorAll('#players video')].every(v=>!v.muted)"), 'keep-audio survives tab switching');
  await key('a'); await key('m');
  assert.ok(await evaluate(`${selectedVideo}.muted`), 'manual mute overrides always-keep-audio');
  await key(']'); await key(']');
  assert.ok(await evaluate(`${selectedVideo}.muted`), 'manual mute survives selection changes');
  await key('m'); assert.equal(await evaluate(`${selectedVideo}.muted`), false, 'one key produces one toggle on a focused video');
  await key('a'); assert.equal(await evaluate(`document.querySelector('#player-${second} .never-mute').checked`), false);
  assert.equal(await evaluate(`document.querySelector('#player-${third} video').volume`), 0.43, 'audio hotkeys preserve remembered volume');

  // Native fullscreen migration uses trusted browser key events.
  await key('f'); await wait(`document.fullscreenElement?.id==='player-${second}'`);
  await key('g'); await wait("document.fullscreenElement?.id==='players' && document.querySelector('#players').classList.contains('grid')");
  await key('g'); await wait(`document.fullscreenElement?.id==='player-${second}' && !document.querySelector('#players').classList.contains('grid')`);
  await key(']'); await wait(`document.fullscreenElement?.id==='player-${third}'`);
  await key('f'); await wait('!document.fullscreenElement');
  console.log('PASS wraparound, reorder/close, focus, grid toggles, selected audio, manual mute precedence and fullscreen migration.');

  // Typing, controls, composition and dialogs retain their own keys.
  const selectedBefore = await selected(), gridBefore = await grid();
  for (const selector of [`#player-${third} .chat-form input`, `#player-${third} .rename`, `#player-${third} .quality`, `#player-${third} .video-volume`]) {
    await focus(selector); await key('g'); await key(']');
    assert.equal(await selected(), selectedBefore); assert.equal(await grid(), gridBefore);
  }
  await evaluate("const editable=document.createElement('div');editable.id='hotkey-editable';editable.contentEditable='true';editable.innerHTML='<span>Editing</span>';document.body.append(editable);editable.focus();editable.querySelector('span').dispatchEvent(new KeyboardEvent('keydown',{key:'g',bubbles:true}))");
  assert.equal(await grid(), gridBefore);
  await evaluate("document.querySelector('#hotkey-editable').remove()");
  await focus('#library');
  await evaluate("document.querySelector('#library').dispatchEvent(new KeyboardEvent('keydown',{key:'g',isComposing:true,bubbles:true}));document.querySelector('#library').dispatchEvent(new KeyboardEvent('keydown',{key:'g',modifierAltGraph:true,bubbles:true}))");
  assert.equal(await grid(), gridBefore);
  await focus(`#player-${third} video`); await key('k', 2); await wait("document.querySelector('#open-dialog').open");
  await key('Escape'); await wait("!document.querySelector('#open-dialog').open");
  await focus(`#player-${third} video`); await key('/'); assert.equal(await evaluate('document.activeElement.id'), 'query');
  await key('k', 2);
  await key('g'); await key(']');
  assert.equal(await grid(), gridBefore); assert.equal(await selected(), selectedBefore);
  await key('Escape'); await wait("!document.querySelector('#open-dialog').open");
  await evaluate("document.querySelector('#channels-toggle').click()");
  await key('g'); await key(']'); assert.equal(await grid(), gridBefore); assert.equal(await selected(), selectedBefore);
  await key('Escape'); await wait("!document.querySelector('#channels-dialog').open");

  // Navigating to Downloads and back restores the stream view without closing media.
  await page('downloads'); await key('Escape');
  await wait("document.querySelector('#studio').classList.contains('watching')");
  assert.equal(await selected(), third); assert.equal(await evaluate("document.querySelectorAll('#players video').length"), 2);
  await key('Escape'); await ready('following');
  console.log('PASS typing, editable descendants, selects/sliders, composition, dialogs and returning to the player.');

  await page('settings');
  const defaults = { back: 'Escape', nextTab: ']', previousTab: '[', grid: 'G', keepAudio: 'A', mute: 'M' };
  for (const [id, label] of Object.entries(defaults)) assert.equal(await savedValue(id), label);
  await record('nextTab', '[');
  assert.equal(await savedValue('nextTab'), ']');
  assert.ok(await evaluate("document.querySelector('#hotkey-status').textContent.includes('Already assigned')"));
  for (const [value, modifiers] of [['/', 0], ['k', 2], ['Enter', 0]]) {
    await record('nextTab', value, modifiers); assert.equal(await savedValue('nextTab'), ']');
    assert.ok(await evaluate("document.querySelector('#hotkey-status').textContent.includes('reserved')"));
  }
  await record('nextTab', 'Shift', 8); assert.equal(await savedValue('nextTab'), ']');
  await key('Tab'); assert.ok(await evaluate("document.activeElement.id!=='hotkey-nextTab'"), 'Tab navigates instead of becoming a binding');
  await record('back', 'b', 1);
  await record('back', 'Escape'); await ready('settings'); assert.equal(await savedValue('back'), 'Escape');
  await record('back', 'b', 1);
  await record('nextTab', 'x'); await record('previousTab', 'X', 8);
  await record('grid', 'G', 8); await record('keepAudio', 'a', 1); await record('mute', 'u');
  assert.equal(await savedValue('previousTab'), 'Shift + X');
  await focus('#library'); await key('g'); assert.equal(await grid(), false, 'replaced shortcuts stop working immediately');
  await key('G', 8); assert.equal(await grid(), true, 'recorded shortcuts apply immediately');
  await key('G', 8); assert.equal(await grid(), false);
  await save();
  assert.equal(getSavedSettings().hotkeys.mute.key, 'u');
  for (const width of [320, 390, 768, 1440]) {
    await viewport(width); await focus('#hotkey-grid');
    await evaluate("document.querySelector('#notice button')?.click();document.querySelector('.hotkeys-panel').scrollIntoView({block:'start'})");
    await capture('preferences-' + width);
    assert.ok(await evaluate("document.documentElement.scrollWidth<=innerWidth && [...document.querySelectorAll('.hotkey-controls input,.hotkey-controls button')].every(e=>{const r=e.getBoundingClientRect();return r.width>0&&r.left>=0&&r.right<=innerWidth})"), 'shortcut controls fit viewport ' + width);
  }
  await viewport(1440); await focus('#library');
  await key('x'); assert.equal(await selected(), second);
  await key('X', 8); assert.equal(await selected(), third);
  await key('m'); assert.equal(await evaluate(`${selectedVideo}.muted`), false, 'old mute key is inactive even on video');
  await key('u'); assert.equal(await evaluate(`${selectedVideo}.muted`), true);
  await key('u'); assert.equal(await evaluate(`${selectedVideo}.muted`), false);
  await key('g'); assert.equal(await grid(), false, 'modifier matching is exact');
  await key('G', 8); assert.equal(await grid(), true);
  await key('G', 8); assert.equal(await grid(), false);
  const keepBefore = await evaluate(`document.querySelector('#player-${third} .never-mute').checked`);
  await key('a', 1); assert.equal(await evaluate(`document.querySelector('#player-${third} .never-mute').checked`), !keepBefore);
  await page('downloads'); await key('Escape'); await ready('downloads');
  await key('b', 1); await wait("document.querySelector('#studio').classList.contains('watching')");
  await page('following'); await page('downloads'); await key('b', 1); await ready('following');
  console.log('PASS capture, conflict/reserved-key rejection, immediate apply/save, exact modifiers, remapped video mute, Back remap and 320–1440px layouts.');

  await reload(); await page('settings');
  assert.equal(await savedValue('back'), 'Alt + B'); assert.equal(await savedValue('mute'), 'U');
  const reopenedFirst = await open('Northstar'); await open('Pixelquest'); await key('x'); assert.equal(await selected(), reopenedFirst);
  await key('u'); assert.ok(await evaluate(`${selectedVideo}.muted`));
  await page('settings');
  await click('#hotkey-mute + button');
  await focus('#library'); await key('x');
  await evaluate(`${selectedVideo}.muted=false`); await key('u'); assert.equal(await evaluate(`${selectedVideo}.muted`), false);
  await reload(); await page('settings'); assert.equal(await savedValue('mute'), 'Not assigned');
  await click('#reset-hotkeys'); await reload(); await page('settings');
  for (const [id, label] of Object.entries(defaults)) assert.equal(await savedValue(id), label);

  // Side buttons record through trusted mouse events without leaving Preferences.
  await focus('#hotkey-back'); await mouse('back');
  assert.equal(await savedValue('back'), 'Mouse4'); await ready('settings');
  await focus('#hotkey-nextTab'); await mouse('back');
  assert.equal(await savedValue('nextTab'), ']');
  assert.ok(await evaluate("document.querySelector('#hotkey-status').textContent.includes('Already assigned')"));
  await focus('#hotkey-back'); await mouse('forward', 1);
  assert.equal(await savedValue('back'), 'Alt + Mouse5');
  await focus('#hotkey-back'); await mouse('back');
  for (const value of ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight']) {
    await record('nextTab', value); assert.equal(await savedValue('nextTab'), value);
  }
  await record('previousTab', 'ArrowLeft');
  await save();
  assert.equal(getSavedSettings().hotkeys.back.key, 'Mouse4');
  assert.equal(getSavedSettings().hotkeys.nextTab.key, 'ArrowRight');
  assert.equal(getSavedSettings().hotkeys.previousTab.key, 'ArrowLeft');
  await evaluate("history.pushState(null,'','?mouse-hotkey-history=1')");
  await page('following'); await page('downloads'); await page('recent');
  await mouse('back'); await ready('downloads');
  assert.equal(await evaluate('location.search'), '?mouse-hotkey-history=1', 'bound side button never invokes browser Back');
  await mouse('back'); await ready('following');
  await page('downloads'); await focus('#query'); await mouse('back'); await ready('downloads');
  assert.equal(await evaluate('location.search'), '?mouse-hotkey-history=1', 'paused shortcuts also suppress native browser history');
  await key('k', 2);
  await mouse('back'); assert.ok(await evaluate("document.querySelector('#open-dialog').open"));
  await key('Escape'); await wait("!document.querySelector('#open-dialog').open");
  await page('settings'); await reload(); await page('settings');
  assert.equal(await savedValue('back'), 'Mouse4');
  assert.equal(await savedValue('nextTab'), 'ArrowRight'); assert.equal(await savedValue('previousTab'), 'ArrowLeft');
  // Real clicks retain checkbox focus. Do not move focus to #library before the shortcut.
  await page('downloads'); await page('following');
  await click('#live-filter');
  assert.equal(await evaluate('document.activeElement.id'), 'live-filter');
  await mouse('back'); await ready('downloads');
  await page('following');
  const arrowFirst = await open('Northstar'), arrowSecond = await open('Pixelquest');
  await key('ArrowRight'); assert.equal(await selected(), arrowFirst);
  await key('ArrowRight', 0, '', true); assert.equal(await selected(), arrowFirst, 'held arrows switch once');
  await key('ArrowLeft'); assert.equal(await selected(), arrowSecond);
  assert.ok(await evaluate(`document.activeElement===${selectedVideo}`));
  for (const selector of [`#player-${arrowSecond} .chat-form input`, `#player-${arrowSecond} .rename`, `#player-${arrowSecond} .quality`, `#player-${arrowSecond} .video-volume`]) {
    await focus(selector); await key('ArrowRight'); assert.equal(await selected(), arrowSecond, 'arrow bindings leave controls usable');
  }
  await click(`#player-${arrowSecond} .playback-settings summary`);
  await click(`#player-${arrowSecond} .multistream-toggle`);
  assert.ok(await grid());
  assert.ok(await evaluate('document.activeElement.matches(".multistream-toggle")'));
  await key('ArrowRight'); assert.equal(await selected(), arrowFirst, 'next works after clicking Multi-stream');
  await key('ArrowLeft'); assert.equal(await selected(), arrowSecond, 'previous works after clicking Multi-stream');
  await click(`#player-${arrowSecond} .multistream-toggle`);
  assert.equal(await grid(), false);
  await key(' '); assert.ok(await grid(), 'Space still operates the focused switch');
  await key(' '); assert.equal(await grid(), false);
  await mouse('back'); await ready('following');
  await click('#live-filter'); await key('ArrowRight');
  assert.equal(await selected(), arrowFirst, 'next works from a library checkbox');
  await mouse('back'); await ready('following');
  await click('#live-filter'); await key('ArrowLeft');
  assert.equal(await selected(), arrowSecond, 'previous works from a library checkbox');
  await focus(`#player-${arrowSecond} video`); await key('f');
  await wait(`document.fullscreenElement?.id==='player-${arrowSecond}'`);
  await key('ArrowRight'); await wait(`document.fullscreenElement?.id==='player-${arrowFirst}'`);
  await mouse('back', 0, `#player-${arrowFirst} video`); await wait('!document.fullscreenElement');
  assert.ok(await evaluate("document.querySelector('#studio').classList.contains('watching')"), 'mouse Back exits fullscreen before navigating');
  await key('ArrowLeft'); assert.equal(await selected(), arrowSecond); await key('g');
  await mouse('back', 0, `#player-${arrowFirst} video`); await ready('following');
  await key('ArrowRight');
  assert.equal(await selected(), arrowFirst, 'side button preserves the selected stream when pointing at another stream');
  await page('settings'); await focus('#hotkey-back'); await mouse('forward', 1); await save();
  await page('following'); await page('downloads'); await mouse('forward'); await ready('downloads');
  await mouse('forward', 1); await ready('following');
  await page('settings'); await focus('#hotkey-back'); await mouse('forward'); await save();
  await page('following'); await page('downloads'); await mouse('forward'); await ready('following');
  assert.equal(await evaluate('location.search'), '?mouse-hotkey-history=1', 'Mouse5 also suppresses browser history');
  await reload(); await page('settings'); assert.equal(await savedValue('back'), 'Mouse5');
  await evaluate("document.querySelector('#reset-hotkeys').click()"); await save();
  console.log('PASS Mouse4/Mouse5 capture, conflicts, persistence, single Back, browser-history protection, arrow bindings after clicking switches, native Space, input/dialog guards and fullscreen.');

  // Shortcut saves share the account preference path and retain other fields.
  await page('settings');
  const oldSize = await evaluate("Number(document.querySelector('#chat-size').value)");
  await evaluate("document.querySelector('#chat-size').value='28';document.querySelector('#chat-size').dispatchEvent(new Event('input',{bubbles:true}))");
  assert.notEqual(oldSize, 28);
  await save();
  assert.equal(getSavedSettings().size, 28);
  const savesBefore = settingsFixture.saves;
  settingsFixture.failSave = true;
  await focus('#hotkey-back'); await mouse('back');
  await record('nextTab', 'ArrowRight'); await record('previousTab', 'ArrowLeft');
  await wait("document.querySelector('#save-status').textContent.includes('Couldn’t save your preferences')");
  assert.equal(settingsFixture.saves, savesBefore + 1, 'failed shortcut save reaches the account API');
  const independentFirst = await open('Northstar'), independentSecond = await open('Pixelquest');
  await key('ArrowRight'); assert.equal(await selected(), independentFirst);
  await key('ArrowLeft'); assert.equal(await selected(), independentSecond);
  await mouse('back'); await ready('settings');
  settingsFixture.failSave = false;
  await evaluate("document.querySelector('#retry-settings').click()");
  await save();
  await reload(); await page('settings');
  assert.equal(await savedValue('back'), 'Mouse4');
  assert.equal(await savedValue('nextTab'), 'ArrowRight'); assert.equal(await savedValue('previousTab'), 'ArrowLeft');
  assert.equal(await evaluate("Number(document.querySelector('#chat-size').value)"), 28);
  assert.equal(getSavedSettings().size, 28);
  await click('#reset-hotkeys'); await save();
  console.log('PASS shortcut save failures show retry and recover without losing other account preferences.');

  await record('mute', 'z'); await save();
  assert.equal(getSavedSettings().hotkeys.mute.key, 'z');
  await open('Northstar'); await key('z'); assert.ok(await evaluate(`${selectedVideo}.muted`));
  await evaluate("document.querySelector('#logout').click()");
  await wait("document.querySelector('#studio').hidden");
  await key('z'); await key('Escape'); assert.ok(await evaluate("document.querySelector('#studio').hidden"), 'shortcuts are inactive after disconnect');
  await evaluate("document.querySelector('#username').value='webtester';document.querySelector('#password').value='Test web password 123!';document.querySelector('#login-form').requestSubmit()");
  await wait("!document.querySelector('#studio').hidden && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
  await page('settings'); assert.equal(await savedValue('mute'), 'Z');
  await focus('#library'); await key('Escape'); await ready('following');
  assert.equal(await evaluate("document.querySelectorAll('#players video').length"), 0, 'reconnect starts a fresh navigation history');
  assert.equal(await evaluate('window.visualErrors.length'), 0);
  console.log('PASS account shortcut persistence, disconnect and zero JavaScript errors.');
};