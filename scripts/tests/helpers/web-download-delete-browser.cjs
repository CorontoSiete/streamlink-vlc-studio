'use strict';
const assert = require('node:assert/strict');

module.exports = async ({ cdp, ws, evaluate, wait, capture, fixture }) => {
  const click = async selector => {
    const point = await evaluate(`(()=>{const e=document.querySelector(${JSON.stringify(selector)});e.scrollIntoView({block:'center'});const r=e.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()`);
    await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...point });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...point });
  };
  let accept = false;
  const dialogs = [];
  const dialogHandler = event => {
    const message = JSON.parse(event.data);
    if (message.method !== 'Page.javascriptDialogOpening') return;
    dialogs.push(message.params);
    cdp('Page.handleJavaScriptDialog', { accept }).catch(error => { throw error; });
  };
  ws.addEventListener('message', dialogHandler);
  try {
    await click('[data-page=downloads]');
    await wait("document.querySelector('#cards').getAttribute('aria-busy')==='false' && document.querySelectorAll('.card').length===1");
    await click('.card-actions .danger');
    await wait("!document.querySelector('.card-actions .danger').disabled");
    assert.equal(dialogs.length, 1, 'a trusted Delete click opens confirmation');
    assert.equal(dialogs[0].type, 'confirm');
    assert.ok(dialogs[0].message.includes('Saved broadcast'));
    assert.deepEqual(fixture.requests, [], 'canceling never requests deletion');
    accept = true;
    await click('.card-actions .danger');
    await wait("document.querySelectorAll('.card').length===0 && !document.querySelector('#empty-state').hidden");
    assert.deepEqual(fixture.requests, [{ id: 'ec263e99-7ad7-47f9-b2e6-50c2f3c744e1' }]);
    await capture('deleted');
    assert.deepEqual(await evaluate('window.visualErrors'), []);
    console.log('PASS VOD Delete: trusted Chromium click; actual native confirmation; cancel sends no request; confirm sends the selected ID once and refreshes the library.');
  } finally { ws.removeEventListener('message', dialogHandler); }
};
