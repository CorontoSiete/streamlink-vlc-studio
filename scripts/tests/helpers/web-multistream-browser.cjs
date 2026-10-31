'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

module.exports = async ({ cdp, evaluate, wait, capture, viewport, output }) => {
  const measurements = {};
  const open = async channel => {
    const count = await evaluate("document.querySelectorAll('#players video').length");
    await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key: 'k', code: 'KeyK', windowsVirtualKeyCode: 75, modifiers: 2 });
    await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key: 'k', code: 'KeyK', windowsVirtualKeyCode: 75, modifiers: 2 });
    await wait("document.querySelector('#open-dialog').open");
    await evaluate(`document.querySelector('#open-query').value=${JSON.stringify(channel)};document.querySelector('#open-form').requestSubmit()`);
    await wait(`document.querySelectorAll('#players video').length===${count + 1} && document.querySelectorAll('#players video')[${count}].currentTime>0.1`);
  };
  const layout = async name => {
    await evaluate("document.querySelector('button[data-page=settings]').click()");
    await wait("document.querySelector('#studio').dataset.page==='settings' && !document.querySelector('#studio').classList.contains('watching')");
    await evaluate(`document.querySelector('#chat-layout').value=${JSON.stringify(name)};document.querySelector('#chat-layout').dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('#chat-opacity').value='0';document.querySelector('#chat-opacity').dispatchEvent(new Event('input',{bubbles:true}))`);
    await wait("document.querySelector('#save-status').textContent==='All changes saved automatically.'");
    await evaluate("(document.querySelector('.followed-link[aria-current=true]') || document.querySelector('[data-page=following]')).click()");
    await wait("!document.querySelector('#players video') || document.querySelector('#studio').classList.contains('watching')");
  };
  const toggle = async value => {
    await evaluate(`(()=>{const t=document.querySelector('.player:not([hidden]) .multistream-toggle');t.checked=${value};t.dispatchEvent(new Event('change'))})()`);
    await wait(`document.querySelector('#players').classList.contains('grid')===${value}`);
  };
  const metrics = async () => {
    await evaluate('scrollTo(0,0);new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
    return evaluate(`(()=>{const rect=e=>{const r=e.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height,right:r.right,bottom:r.bottom}};
      return {width:innerWidth,height:innerHeight,scrollWidth:document.documentElement.scrollWidth,header:rect(document.querySelector('.topbar')),
        media:[...document.querySelectorAll('.video-chat')].map(rect),videos:[...document.querySelectorAll('video')].map(v=>({...rect(v),frameWidth:v.videoWidth,frameHeight:v.videoHeight,fit:getComputedStyle(v).objectFit})),
        info:[...document.querySelectorAll('.channel-information:not([hidden])')].map(rect),descriptions:[...document.querySelectorAll('.channel-information:not([hidden]) .channel-description')].map(rect)}})()`);
  };
  const assertGrid = async (count, label) => {
    const m = await metrics();
    measurements[label] = m;
    assert.equal(m.media.length, count);
    assert.equal(m.info.length, 1, label + ': only the selected channel information is visible');
    assert.ok(await evaluate("document.querySelector('.channel-information:not([hidden])').closest('.player').id==='player-'+document.querySelector('.is-selected').dataset.playerId"), label + ': channel information follows the selected tab');
    assert.ok(m.scrollWidth <= m.width, label + ': no horizontal overflow');
    assert.ok(Math.abs(m.info[0].w - (Math.max(...m.media.map(r => r.right)) - Math.min(...m.media.map(r => r.x)))) <= 1, label + ': selected channel information spans the video grid');
    const lastMedia = Math.max(...m.media.map(r => r.bottom));
    assert.ok(m.info.every(r => r.y >= lastMedia - 1), label + ': all channel information follows all video rows');
    assert.ok(m.descriptions.every(r => r.y >= m.height - 1), label + ': descriptions require scrolling');
    assert.ok(m.videos.every(r => r.h >= 100 && r.w >= 100), label + ': videos retain usable dimensions');
    if (count <= 4 && m.width > 650) {
      assert.ok(Math.abs(lastMedia - m.height) <= 1, label + ': videos use the available viewport');
    }
    return m;
  };

  await layout('overlay');
  await cdp('Emulation.setDeviceMetricsOverride', { width: 1910, height: 965, deviceScaleFactor: 1, mobile: false });
  await open('Northstar');
  await open('Pixelquest');
  await toggle(true);
  const two = await assertGrid(2, 'two streams at screenshot size');
  assert.ok(two.videos.every(r => r.fit === 'contain' && r.frameWidth / r.frameHeight === 16 / 9 && Math.min(r.h, r.w * r.frameHeight / r.frameWidth) > 965 * .36), 'actual 16:9 pictures exceed the old 36vh limit without cropping');
  await capture('two-streams');

  // Real wheel input must reach the existing channel descriptions and social/panel content.
  await cdp('Input.dispatchMouseEvent', { type: 'mouseWheel', x: 800, y: 700, deltaX: 0, deltaY: 800 });
  await wait("[...document.querySelectorAll('.channel-information:not([hidden]) .channel-description')].some(e=>{const r=e.getBoundingClientRect();return r.y<innerHeight && r.bottom>64})");
  await capture('channel-information-after-scroll');
  await evaluate("window.originalVideo=document.querySelector('video');window.originalInfo=document.querySelector('.channel-information');document.querySelector('.channel-information:not([hidden]) .channel-description').textContent+=' Long biography.'.repeat(500)");
  assert.deepEqual((await metrics()).videos, two.videos, 'long channel descriptions do not change media dimensions');
  await evaluate("document.querySelectorAll('.player')[0].querySelector('.video-surface').dispatchEvent(new PointerEvent('pointerdown',{bubbles:true}))");
  assert.ok(await evaluate("document.querySelector('video')===window.originalVideo && !window.originalVideo.paused && !window.originalVideo.muted"), 'selection retains playback and audio');
  await assertGrid(2, 'selecting the first stream');

  await layout('hidden');
  await assertGrid(2, 'hidden chat');
  await layout('docked');
  await assertGrid(2, 'docked chat');
  assert.ok(await evaluate("[...document.querySelectorAll('.player')].every(p=>{const c=p.querySelector('.chat').getBoundingClientRect(),v=p.querySelector('video').getBoundingClientRect(),m=p.querySelector('.video-chat').getBoundingClientRect();return c.height>0 && c.y>=v.bottom-1 && c.bottom<=m.bottom+1})"), 'docked chat stays inside its media tile');
  await layout('overlay');
  assert.ok(await evaluate("[...document.querySelectorAll('.player')].every(p=>{const c=p.querySelector('.chat').getBoundingClientRect(),s=p.querySelector('.video-surface').getBoundingClientRect();return c.x>=s.x && c.right<=s.right && c.y>=s.y && c.bottom<=s.bottom})"), 'overlay chat stays inside each video');

  // Use a trusted click to exercise the actual fullscreen control.
  await metrics();
  const point = await evaluate("(()=>{const r=document.querySelector('[data-action=fullscreen]').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()");
  await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', ...point, button: 'left', clickCount: 1 });
  await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', ...point, button: 'left', clickCount: 1 });
  await wait("document.fullscreenElement?.id==='players'");
  assert.ok(await evaluate("[...document.querySelectorAll('.channel-information')].every(e=>getComputedStyle(e).display==='none') && [...document.querySelectorAll('.video-chat')].every(e=>{const r=e.getBoundingClientRect();return r.top>=0 && r.bottom<=innerHeight+1})"), 'fullscreen fits videos and omits channel details');
  await capture('fullscreen');
  await evaluate('document.exitFullscreen()');
  await wait('!document.fullscreenElement');

  for (const count of [3, 4, 5]) {
    await open('stream' + count);
    await assertGrid(count, count + ' streams');
  }
  await capture('five-streams');
  for (const width of [320, 390, 650, 651, 768, 1440]) {
    await viewport(width);
    await assertGrid(5, 'five streams at ' + width);
  }
  await capture('responsive-grid');
  for (let count = 6; count <= 16; count++) await open('stream' + count);
  await assertGrid(16, 'sixteen streams');

  await toggle(false);
  assert.ok(await evaluate("document.querySelectorAll('.player:not([hidden])').length===1 && [...document.querySelectorAll('.channel-information')].every(e=>e.parentElement.classList.contains('screen')) && document.querySelector('video')===window.originalVideo && document.querySelector('.channel-information')===window.originalInfo"), 'single view restores the same video and channel DOM');
  await toggle(true);
  for (let count = 16; count > 1; count--) {
    await evaluate("document.querySelector('.player:last-child [data-action=close]').click()");
    await wait(`document.querySelectorAll('#players video').length===${count - 1}`);
  }
  await assertGrid(1, 'closing down to one stream');
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  fs.writeFileSync(path.join(output, 'measurements.json'), JSON.stringify(measurements, null, 2));
  console.log('PASS viewport-sized multi-stream videos; descriptions below every video row; wheel scrolling; long bios; 1-16 streams; responsive layouts; docked/overlay/hidden chat; fullscreen; selection/audio; DOM preservation; close and single-view recovery.');
};
