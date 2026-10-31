'use strict';
const assert = require('node:assert/strict');

module.exports = async ({ cdp, evaluate, wait, getSavedSettings }) => {
  const bar = '.player:not([hidden]) .video-volume';
  const video = '.player:not([hidden]) video';
  const pointAt = async selector => evaluate(`(()=>{const el=document.querySelector(${JSON.stringify(selector)});el.scrollIntoView({block:'center'});const r=el.getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2}})()`);
  const wheel = async (selector, deltaY) => {
    const point = await pointAt(selector);
    await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', ...point });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseWheel', ...point, deltaX: 0, deltaY });
  };
  const waitSavedVolume = async expected => {
    for (let n = 0; n < 120; n++) {
      if (getSavedSettings().volumes['Twitch:northstar'] === expected) return;
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    throw new Error(`Account volume did not save ${expected}.`);
  };

  await evaluate("document.dispatchEvent(new KeyboardEvent('keydown',{key:'k',ctrlKey:true,bubbles:true}));document.querySelector('#open-query').value='Northstar';document.querySelector('#open-form').requestSubmit()");
  await wait("!!document.querySelector('.player:not([hidden]) video') && document.querySelector('.player:not([hidden]) video').currentTime>0.1");
  await evaluate(`document.querySelector(${JSON.stringify(bar)}).value='0.4';document.querySelector(${JSON.stringify(bar)}).dispatchEvent(new Event('input',{bubbles:true}))`);
  await wait(`document.querySelector(${JSON.stringify(video)}).volume===0.4`);
  await evaluate(`window.volumeWheelEvents=[];document.querySelector(${JSON.stringify(bar)}).addEventListener('wheel',event=>volumeWheelEvents.push({deltaY:event.deltaY,canceled:event.defaultPrevented}))`);

  await pointAt(bar);
  const scrollY = await evaluate('window.scrollY');
  await wheel(bar, 100);
  await wait(`document.querySelector(${JSON.stringify(video)}).volume===0.35`);
  assert.equal(await evaluate(`document.querySelector(${JSON.stringify(bar)}).value`), '0.35');
  await wheel(bar, -100);
  await wait(`document.querySelector(${JSON.stringify(video)}).volume===0.4`);
  assert.deepEqual(await evaluate('window.volumeWheelEvents'), [
    { deltaY: 100, canceled: true }, { deltaY: -100, canceled: true }
  ]);
  assert.equal(await evaluate('window.scrollY'), scrollY, 'wheel on the bar does not scroll the page');
  await waitSavedVolume(0.4);

  await evaluate("document.querySelector('.player:not([hidden]) [data-action=mute]').click()");
  await wait(`document.querySelector(${JSON.stringify(video)}).muted`);
  await wheel(bar, -100);
  await wait(`document.querySelector(${JSON.stringify(video)}).volume===0.45 && !document.querySelector(${JSON.stringify(video)}).muted`);
  await waitSavedVolume(0.45);

  await wheel('.player:not([hidden]) .player-title', 100);
  assert.equal(await evaluate(`document.querySelector(${JSON.stringify(video)}).volume`), 0.45, 'wheel outside the bar leaves volume alone');

  await cdp('Page.reload');
  await wait("document.readyState==='complete' && !document.querySelector('#studio').hidden");
  await evaluate("document.dispatchEvent(new KeyboardEvent('keydown',{key:'k',ctrlKey:true,bubbles:true}));document.querySelector('#open-query').value='Northstar';document.querySelector('#open-form').requestSubmit()");
  await wait(`document.querySelector(${JSON.stringify(video)})?.currentTime>0.1`);
  assert.equal(await evaluate(`document.querySelector(${JSON.stringify(video)}).volume`), 0.45, 'wheel volume is restored after reload');
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  console.log('PASS trusted mouse-wheel volume changes, page-scroll cancellation, mute recovery, scoped hover target and reload persistence.');
};
