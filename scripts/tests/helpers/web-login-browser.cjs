'use strict';
const assert = require('node:assert/strict');

module.exports = async ({ cdp, evaluate, wait, capture, viewport, address, fixture }) => {
  const submit = () => evaluate("document.querySelector('#login-form').requestSubmit()");
  const fill = password => evaluate(`document.querySelector('#username').value='webtester';document.querySelector('#password').value=${JSON.stringify(password)}`);

  // Real layout at phone, tablet, laptop and wide desktop sizes, including both breakpoints.
  for (const width of [320, 360, 390, 650, 768, 900, 901, 1024, 1280, 1440, 1920]) {
    await viewport(width);
    assert.ok(await evaluate('document.documentElement.scrollWidth<=innerWidth'), 'no horizontal overflow at ' + width);
    assert.ok(await evaluate("[...document.querySelectorAll('#username,#password,#login-submit,#password-toggle,.connection-help summary')].every(el=>{const r=el.getBoundingClientRect();return r.width>0&&r.height>=44&&r.left>=0&&r.right<=innerWidth})"), 'visible touch targets at ' + width);
    if ([390, 768, 1024, 1440, 1920].includes(width)) await capture('login-' + width);
  }
  for (const [width, height] of [[1440, 900], [1366, 768], [1280, 720], [1024, 768], [320, 568], [844, 390]]) {
    await cdp('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
    assert.ok(await evaluate('document.documentElement.scrollWidth<=innerWidth'), 'no overflow at ' + width + '×' + height);
    if (width > 900) assert.ok(await evaluate('document.documentElement.scrollHeight<=innerHeight'), 'desktop login fits at ' + width + '×' + height);
    if (width > 900) await capture('login-' + width + 'x' + height);
  }
  await viewport(390);
  await evaluate("document.querySelector('.connection-help summary').click()");
  assert.ok(await evaluate("document.querySelector('.connection-help').open && document.documentElement.scrollWidth<=innerWidth"));
  await capture('login-mobile-help');
  await evaluate("document.querySelector('.connection-help summary').click()");

  await submit();
  assert.equal(fixture.requests, 0, 'empty form uses browser validation');
  await fill('wrong password');
  await evaluate("document.querySelector('#password-toggle').click()");
  assert.ok(await evaluate("document.querySelector('#password').type==='text' && document.querySelector('#password-toggle').getAttribute('aria-pressed')==='true' && document.querySelector('#password-toggle').getAttribute('aria-label')==='Hide password'"));
  await evaluate("document.querySelector('#password-toggle').click()");
  assert.ok(await evaluate("document.querySelector('#password').type==='password' && document.querySelector('#password').value==='wrong password'"));
  await submit();
  await wait("document.querySelector('#login-error').textContent.length>0 && !document.querySelector('#login-submit').disabled");
  assert.equal(await evaluate("document.querySelector('#login-error').textContent"), 'Username or password did not match.');
  await capture('login-mobile-error');
  await evaluate("document.querySelector('#password').dispatchEvent(new Event('input',{bubbles:true}))");
  assert.equal(await evaluate("document.querySelector('#login-error').textContent"), '', 'editing clears the stale error');

  await viewport(1440);
  await fill('Test web password 123!');
  await evaluate("document.querySelector('#password-toggle').click();document.querySelector('#password').focus()");
  fixture.holdNext = true;
  // Enter in the password field must submit the form, not activate the reveal control.
  await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', text: '\r', windowsVirtualKeyCode: 13 });
  await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
  await wait("document.querySelector('#login-submit').disabled");
  assert.equal(fixture.held.length, 1);
  assert.ok(await evaluate("document.querySelector('#login-form').getAttribute('aria-busy')==='true' && document.querySelector('#login-submit').textContent.includes('Connecting') && !document.querySelector('#password-toggle').disabled"));
  const count = fixture.requests;
  await submit();
  assert.equal(fixture.requests, count, 'pending sign-in cannot submit twice');
  await capture('login-desktop-connecting');
  fixture.held.shift()();
  await wait("!document.querySelector('#studio').hidden && document.querySelectorAll('.card').length===6 && !document.querySelector('#login-submit').disabled");
  assert.ok(await evaluate("document.querySelector('#password').value==='' && document.querySelector('#password').type==='password' && document.querySelector('#password-toggle').getAttribute('aria-pressed')==='false'"), 'successful login clears and hides the password');

  await cdp('Page.navigate', { url: address });
  await wait("document.readyState==='complete' && !document.querySelector('#studio').hidden && document.querySelectorAll('.card').length===6");
  await evaluate("document.querySelector('#logout').click()");
  await wait("!document.querySelector('#login').hidden && document.querySelector('#studio').hidden");
  assert.ok(await evaluate("document.querySelector('#password').value==='' && document.querySelector('#password').type==='password' && document.activeElement.id==='username'"));
  assert.equal(await evaluate('window.visualErrors.length'), 0);
  console.log('PASS login: 11 responsive widths; 44px touch targets; connection help; required fields; password visibility; credential errors; Enter submission; pending state and duplicate prevention; successful sign-in; session restoration; logout; zero JavaScript errors.');
};
