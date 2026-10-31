'use strict';

const assert = require('node:assert/strict');

module.exports = async ({ cdp, evaluate, wait, capture, viewport, address }) => {
  const layout = () => evaluate(`(() => {
    const sidebar = document.querySelector('#studio-sidebar').getBoundingClientRect();
    const workspace = document.querySelector('.workspace').getBoundingClientRect();
    const toggle = document.querySelector('#sidebar-toggle');
    return {
      sidebarWidth: sidebar.width,
      workspaceLeft: workspace.left,
      toggleLabel: toggle.getAttribute('aria-label'),
      expanded: toggle.getAttribute('aria-expanded'),
      channelsVisible: !!document.querySelector('#channels-toggle').getClientRects().length,
      overflow: document.documentElement.scrollWidth > innerWidth
    };
  })()`);
  const clickToggle = async () => {
    const point = await evaluate(`(() => {
      const r = document.querySelector('#sidebar-toggle').getBoundingClientRect();
      return { x: r.x + r.width / 2, y: r.y + r.height / 2 };
    })()`);
    await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', ...point });
    await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...point });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...point });
  };

  assert.deepEqual(await layout(), {
    sidebarWidth: 240, workspaceLeft: 240, toggleLabel: 'Collapse sidebar',
    expanded: 'true', channelsVisible: false, overflow: false
  });
  assert.equal(await evaluate("document.querySelector('#sidebar-toggle').getAttribute('aria-controls')"), 'studio-sidebar');
  await capture('sidebar-expanded');
  await clickToggle();
  await wait("document.querySelector('#studio').classList.contains('sidebar-collapsed')");
  assert.deepEqual(await layout(), {
    sidebarWidth: 68, workspaceLeft: 68, toggleLabel: 'Expand sidebar',
    expanded: 'false', channelsVisible: true, overflow: false
  });
  assert.ok(await evaluate("[...document.querySelectorAll('.sidebar nav button')].every(b => b.getClientRects().length && b.title && getComputedStyle(b.querySelector('span:nth-child(2)')).display === 'none')"), 'compact navigation keeps visible, named icon buttons');
  assert.equal(await evaluate("getComputedStyle(document.querySelector('#studio-sidebar .followed-section')).display"), 'none');
  assert.equal(await evaluate("getComputedStyle(document.querySelector('#open-player-count')).display"), 'none', 'no empty stream badge is shown');
  assert.equal(await evaluate("localStorage.getItem('streamstudio.sidebarCollapsed')"), 'true');
  await capture('sidebar-collapsed');

  await evaluate("document.querySelector('.sidebar [data-page=discover]').click()");
  await wait("document.querySelector('#studio').dataset.page==='discover' && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
  assert.equal((await layout()).workspaceLeft, 68, 'collapse survives page navigation');
  await evaluate("document.querySelector('#channels-toggle').click()");
  await wait("document.querySelector('#channels-dialog').open && document.querySelectorAll('#channels-dialog .followed-channel').length===6");
  assert.equal(await evaluate("getComputedStyle(document.querySelector('#channels-dialog .followed-section')).display"), 'block', 'the full channel list remains available in the stream dialog');
  await evaluate("document.querySelector('#close-channels').click()");
  await wait("!document.querySelector('#channels-dialog').open");

  await viewport(901);
  assert.deepEqual((await layout()).sidebarWidth, 68);
  assert.equal((await layout()).overflow, false, 'the compact layout fits the smallest desktop width');
  await viewport(1440);
  await evaluate("window.__sidebarReloadMarker=true");
  await cdp('Page.navigate', { url: address });
  await wait("document.readyState==='complete' && !window.__sidebarReloadMarker && !document.querySelector('#studio').hidden && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
  assert.deepEqual(await layout(), {
    sidebarWidth: 68, workspaceLeft: 68, toggleLabel: 'Expand sidebar',
    expanded: 'false', channelsVisible: true, overflow: false
  });

  await evaluate("document.querySelector('#channels-toggle').click()");
  await wait("document.querySelector('#channels-dialog').open");
  await evaluate("document.querySelector('#channels-dialog button.followed-channel').click()");
  await wait("document.querySelector('#studio').classList.contains('watching') && !document.querySelector('#channels-dialog').open && !!document.querySelector('.player:not([hidden]) video')");
  assert.ok(await evaluate("document.querySelector('.player:not([hidden]) video').getBoundingClientRect().width > 1000"), 'watch page uses the wider workspace');
  assert.ok((await layout()).channelsVisible, 'the stream switcher remains available while watching');
  await capture('sidebar-collapsed-watch');
  await evaluate("document.querySelector('.player:not([hidden]) [data-action=theater]').click()");
  await wait("document.querySelector('#studio').classList.contains('theater')");
  assert.equal(await evaluate("getComputedStyle(document.querySelector('#studio-sidebar')).display"), 'none');
  await evaluate("document.querySelector('.player:not([hidden]) [data-action=theater]').click()");
  await wait("!document.querySelector('#studio').classList.contains('theater')");
  assert.equal((await layout()).sidebarWidth, 68, 'leaving theater restores the chosen rail width');

  await viewport(800);
  assert.equal((await layout()).sidebarWidth, 68, 'tablet keeps its existing icon rail');
  assert.equal(await evaluate("document.querySelector('#sidebar-toggle').getClientRects().length"), 0);
  await viewport(375);
  const phone = await evaluate("(() => { const sidebar=document.querySelector('#studio-sidebar').getBoundingClientRect(); return { sidebarWidth: sidebar.width, sidebarBottom: sidebar.bottom, clientWidth: document.documentElement.clientWidth, viewportHeight: innerHeight, workspaceLeft: document.querySelector('.workspace').getBoundingClientRect().left, scrollWidth: document.documentElement.scrollWidth }; })()");
  assert.ok(phone.sidebarWidth === phone.clientWidth && phone.sidebarBottom === phone.viewportHeight && phone.workspaceLeft === 0 && phone.scrollWidth <= phone.clientWidth, 'phone keeps its bottom navigation and full-width workspace: ' + JSON.stringify(phone));
  await viewport(1440);
  await clickToggle();
  await wait("!document.querySelector('#studio').classList.contains('sidebar-collapsed')");
  assert.equal((await layout()).sidebarWidth, 240);
  assert.equal(await evaluate("localStorage.getItem('streamstudio.sidebarCollapsed')"), 'false');
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  console.log('PASS sidebar collapse: real button input; compact navigation; stream access; page changes; reload persistence; watch/theater; tablet/phone layouts; expansion; zero JavaScript errors.');
};
