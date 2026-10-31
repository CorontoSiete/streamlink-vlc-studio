'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

module.exports = async ({ cdp, evaluate, wait, capture, viewport, searchFixture: fixture, getOpen, output }) => {
  const key = async (key, code, value) => {
    await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key, code, windowsVirtualKeyCode: value, ...(key === 'Enter' ? { text: '\r', unmodifiedText: '\r' } : {}) });
    await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key, code, windowsVirtualKeyCode: value });
  };
  const click = async selector => {
    const point = await evaluate(`(()=>{const e=document.querySelector(${JSON.stringify(selector)});const r=e.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()`);
    await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...point });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...point });
  };
  const enter = () => key('Enter', 'Enter', 13);
  const escape = () => key('Escape', 'Escape', 27);
  const down = () => key('ArrowDown', 'ArrowDown', 40);
  const type = async value => {
    await click('#query');
    await cdp('Input.dispatchKeyEvent', { type: 'keyDown', key: 'a', code: 'KeyA', windowsVirtualKeyCode: 65, modifiers: 2 });
    await cdp('Input.dispatchKeyEvent', { type: 'keyUp', key: 'a', code: 'KeyA', windowsVirtualKeyCode: 65, modifiers: 2 });
    if (value) await cdp('Input.insertText', { text: value }); else await key('Backspace', 'Backspace', 8);
  };
  const settled = "document.querySelector('#search-options').getAttribute('aria-busy')==='false'";
  const options = "document.querySelectorAll('#search-options [role=option]')";
  const ready = count => wait(`!document.querySelector('#search-dropdown').hidden && ${settled} && ${options}.length===${count}`);
  const pageReady = page => wait(`document.querySelector('#studio').dataset.page==='${page}' && document.querySelector('#cards').getAttribute('aria-busy')==='false'`);
  const release = query => fixture.held.filter(item => item.query === query).forEach(item => item.send());
  const unchanged = async () => assert.ok(await evaluate("window.originalCards.every((e,i)=>document.querySelectorAll('#cards .card')[i]===e) && document.querySelector('#studio').dataset.page==='following'"), 'suggestions preserve the current library and its DOM');
  await evaluate("window.originalCards=[...document.querySelectorAll('#cards .card')]");
  const initialRequests = fixture.requests.length;
  await type('N'); await sleep(35); await cdp('Input.insertText', { text: 'or' }); await sleep(35); await cdp('Input.insertText', { text: 'th' });
  assert.equal(fixture.requests.length, initialRequests, 'typing is debounced');
  await ready(1);
  assert.equal(fixture.requests.length, initialRequests + 1, 'one request for the typing burst');
  assert.equal(fixture.requests.at(-1).query, 'North');
  assert.ok(await evaluate("document.querySelector('#query').getAttribute('aria-expanded')==='true' && document.querySelector('#search-options mark').textContent==='North'"));
  await cdp('Accessibility.enable');
  const accessibility = await cdp('Accessibility.getFullAXTree');
  const combobox = accessibility.nodes.find(item => item.role?.value === 'combobox' && item.name?.value === 'Channel, category, or video URL');
  assert.ok(combobox?.properties.some(item => item.name === 'expanded' && item.value.value === true), 'the browser accessibility tree exposes the named, expanded combobox');
  assert.ok(accessibility.nodes.some(item => item.role?.value === 'option' && item.name?.value.includes('Watch Northstar on Twitch')), 'results have actionable accessible names');
  await enter();
  assert.equal(getOpen().openCount, 0, 'Enter without an explicitly selected channel keeps the suggestions open');
  await unchanged();
  await capture('channel-dropdown-desktop');
  await down();
  assert.ok(await evaluate("document.activeElement.id==='query' && document.getElementById(document.querySelector('#query').getAttribute('aria-activedescendant')).getAttribute('aria-selected')==='true'"));
  await escape();
  assert.ok(await evaluate("document.querySelector('#search-dropdown').hidden && document.querySelector('#query').value==='North' && document.activeElement.id==='query'"));
  await unchanged();
  await down(); await ready(1); await down(); await enter();
  await wait("document.querySelector('#players video')?.currentTime>0.1 && document.querySelector('#studio').classList.contains('watching')");
  assert.equal(getOpen().lastOpen.key, 'mountain', 'keyboard selection opens the returned server key');
  assert.ok(await evaluate("document.querySelector('#search-dropdown').hidden && document.activeElement.tagName==='VIDEO'"));
  const player = await evaluate("document.querySelector('.player').id");
  await type('all'); await ready(4);
  await escape();
  assert.equal(await evaluate("document.querySelector('.player').id"), player, 'Escape leaves playback intact');
  assert.ok(await evaluate("document.querySelector('#studio').classList.contains('watching')"));
  await type('all'); await ready(4); await capture('channel-dropdown-during-playback');
  await click('#page-title');
  assert.ok(await evaluate("document.querySelector('#search-dropdown').hidden"), 'outside click closes suggestions');
  await evaluate("document.querySelector('[data-action=close]').click();document.querySelector('[data-page=following]').click()"); await pageReady('following');
  await evaluate("window.originalCards=[...document.querySelectorAll('#cards .card')]");

  const rankedNames = ['Largest audience', 'Equal audience first', 'Equal audience second', 'Small audience', 'Zero viewers', 'Unknown count', 'Missing count', 'Offline first', 'Unavailable channel', 'Offline last'];
  const assertRanking = async () => {
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.search-option-copy strong')].map(e=>e.textContent)"), rankedNames,
      'live channels lead by descending viewer count; ties, unknown counts and non-live results retain provider order');
    assert.ok(await evaluate("document.querySelectorAll('.search-option')[4].querySelector('.search-viewers').textContent==='0 viewers' && [5,6].every(i=>!document.querySelectorAll('.search-option')[i].querySelector('.search-viewers'))"),
      'zero viewers is a known count and missing counts are not displayed as zero');
  };
  for (const provider of ['twitch', 'kick']) {
    await evaluate(`document.querySelector('#platform').value=${JSON.stringify(provider)};document.querySelector('#platform').dispatchEvent(new Event('change'))`);
    await type('ranked'); await ready(10); await assertRanking(); await unchanged();
    if (provider === 'twitch') await capture('channel-dropdown-ranked');
    await down(); await enter();
    await wait("document.querySelector('#players video')?.currentTime>0.1");
    assert.equal(getOpen().lastOpen.key, 'rank-' + provider + '-4', 'keyboard selection opens the first sorted channel on ' + provider);
    await evaluate("document.querySelector('[data-action=close]').click()");
    await wait("!document.querySelector('#players video')");
    await type('ranked'); await ready(10); await assertRanking();
    await click('.search-option:nth-child(3)');
    await wait("document.querySelector('#players video')?.currentTime>0.1");
    assert.equal(getOpen().lastOpen.key, 'rank-' + provider + '-7', 'pointer selection opens the sorted row on ' + provider);
    await evaluate("document.querySelector('[data-action=close]').click()");
    await wait("!document.querySelector('#players video')");
  }
  await evaluate("document.querySelector('#platform').value='twitch';document.querySelector('#platform').dispatchEvent(new Event('change'))");
  await type('');

  fixture.hold.add('slow');
  await type('slow');
  await wait("document.querySelectorAll('.search-skeleton').length===3");
  await sleep(300); await capture('channel-dropdown-loading');
  const canceled = fixture.canceled;
  await type('Pixel'); await ready(1); release('slow');
  assert.ok(fixture.canceled > canceled, 'the superseded provider fetch was aborted');
  assert.equal(await evaluate("document.querySelector('.search-option-copy strong').textContent"), 'Pixelquest');
  await unchanged();
  // A provider may finish despite cancellation. Exercise generation checks separately.
  await evaluate("window.originalFetch=window.fetch;window.fetch=(url,options)=>url==='/api/search' && JSON.parse(options.body).query==='late' ? window.originalFetch(url,{...options,signal:undefined}) : window.originalFetch(url,options)");
  fixture.hold.add('late');
  await type('late'); await sleep(310); await type('North'); release('late'); await ready(1);
  assert.equal(await evaluate("document.querySelector('.search-option-copy strong').textContent"), 'Northstar', 'late successful responses cannot replace current suggestions');
  await evaluate('window.fetch=window.originalFetch');
  await type('all'); await ready(4);
  await evaluate("document.querySelector('#platform').value='kick';document.querySelector('#platform').dispatchEvent(new Event('change'))"); await ready(2);
  assert.ok(await evaluate("[...document.querySelectorAll('.search-option')].every(e=>e.dataset.provider==='kick') && document.querySelector('#search-context').textContent==='Kick'"));
  assert.equal(fixture.requests.at(-1).platform, 'kick');
  await evaluate("document.querySelector('#platform').value='twitch';document.querySelector('#platform').dispatchEvent(new Event('change'))"); await ready(4);
  await type('empty'); await ready(0);
  assert.ok(await evaluate("document.querySelector('#search-feedback').textContent.includes('No channels found')"));
  await capture('channel-dropdown-empty'); await unchanged();
  fixture.fail = true; await type('North'); await ready(0);
  assert.ok(await evaluate("document.querySelector('#search-feedback').textContent.includes('Provider temporarily unavailable.')"));
  await capture('channel-dropdown-error');
  fixture.fail = false; await click('.search-retry'); await ready(1); await unchanged();

  await type('hostile'); await ready(1);
  assert.equal(await evaluate("document.querySelector('.search-option-copy strong').textContent"), '<img src=x onerror=window.injected=true>');
  assert.ok(await evaluate("!window.injected && !document.querySelector('#search-options script,#search-options img[onerror],#search-options img[src^=javascript]')"));
  await type(''); await sleep(300);
  assert.ok(await evaluate("document.querySelector('#search-dropdown').hidden && !document.querySelector('#query').hasAttribute('aria-activedescendant')"));
  const beforeIme = fixture.requests.length;
  await evaluate("const q=document.querySelector('#query');q.dispatchEvent(new CompositionEvent('compositionstart'));q.value='North';q.dispatchEvent(new InputEvent('input',{isComposing:true}))");
  await sleep(300); assert.equal(fixture.requests.length, beforeIme, 'IME composition does not search partial text');
  await evaluate("document.querySelector('#query').dispatchEvent(new CompositionEvent('compositionend'))"); await ready(1);
  await key('Tab', 'Tab', 9); await key('Tab', 'Tab', 9);
  assert.ok(await evaluate("document.querySelector('#search-dropdown').hidden"), 'Tab out of the form dismisses suggestions');

  const metrics = [];
  for (const width of [320, 375, 390, 650, 768, 900, 1024, 1440, 1920]) {
    await viewport(width); await type('all'); await ready(4);
    await wait("[...document.querySelectorAll('.search-option .avatar img')].every(i=>i.complete&&i.naturalWidth>0)");
    const measure = await evaluate("(()=>{const d=document.querySelector('#search-dropdown'),r=d.getBoundingClientRect();return {width:innerWidth,left:r.left,right:r.right,bottom:r.bottom,height:innerHeight,overflow:document.documentElement.scrollWidth>innerWidth,rows:[...document.querySelectorAll('.search-option')].map(e=>({height:e.getBoundingClientRect().height,right:e.getBoundingClientRect().right}))}})()");
    assert.ok(measure.left >= 0 && measure.right <= width && measure.bottom <= measure.height && !measure.overflow, JSON.stringify(measure));
    assert.ok(measure.rows.every(row => row.height >= 44 && row.right <= width), 'touch targets and row bounds');
    metrics.push(measure); await capture('channel-dropdown-' + width);
    await type('many'); await ready(30);
    await key('ArrowUp', 'ArrowUp', 38);
    assert.ok(await evaluate("(()=>{const e=document.querySelector('[role=option][aria-selected=true]'),r=e.getBoundingClientRect(),l=document.querySelector('#search-options').getBoundingClientRect();return e.id.endsWith('-29')&&r.top>=l.top&&r.bottom<=l.bottom+1})()"), 'keyboard wraps and scrolls the final result into view');
    if ([320, 1440].includes(width)) await capture('channel-dropdown-scroll-' + width);
  }
  fs.writeFileSync(path.join(output, 'viewport-metrics.json'), JSON.stringify(metrics, null, 2));
  // Trusted touch input selects an offline channel without attempting a live open.
  await viewport(390); await type('Slow'); await ready(1);
  const beforeOffline = getOpen().openCount;
  const point = await evaluate("(()=>{const r=document.querySelector('.search-option').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()");
  await cdp('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [point] });
  await cdp('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await pageReady('vods');
  assert.equal(getOpen().openCount, beforeOffline, 'offline channel selection browses videos');
  assert.equal(await evaluate("document.querySelector('#query').value"), 'Slowmornings');
  await type('North'); await ready(1); await down(); await enter(); await pageReady('vods');
  assert.equal(await evaluate("document.querySelector('#query').value"), 'Northstar');
  await type('ranked'); await ready(10); await assertRanking();
  const beforeRankedVideos = getOpen().openCount;
  await down(); await enter(); await pageReady('vods');
  assert.equal(await evaluate("document.querySelector('#query').value"), 'rank4', 'Videos uses the selected sorted channel');
  assert.equal(getOpen().openCount, beforeRankedVideos, 'a live search result on Videos still browses its videos');

  await viewport(1440); await click('[data-page=discover]'); await pageReady('discover');
  await type('Music'); await ready(5); await down(); await enter();
  await wait("document.querySelector('#breadcrumbs').textContent.includes('Adventure games') && !document.querySelector('#cards').classList.contains('categories')");
  await click('[data-page=following]'); await pageReady('following');
  const beforeUrl = fixture.requests.length;
  await type('https://www.twitch.tv/videos/123456789'); await ready(1);
  assert.equal(fixture.requests.length, beforeUrl, 'pasted URLs are explicit actions, never channel searches');
  assert.ok(await evaluate("!document.querySelector('#players video')"), 'pasting does not start playback');
  await enter();
  try { await wait("document.querySelector('#players video')?.currentTime>0.1"); }
  catch (error) { throw new Error(error.message + '; URL action state: ' + JSON.stringify({ open: getOpen(), dom: await evaluate("({page:document.querySelector('#studio').dataset.page,query:document.querySelector('#query').value,focus:document.activeElement.outerHTML,dropdown:document.querySelector('#search-dropdown').outerHTML})") })); }
  assert.equal(getOpen().lastOpen.input, 'https://www.twitch.tv/videos/123456789');
  await click('[data-page=downloads]'); await pageReady('downloads');
  await type('https://www.twitch.tv/videos/123456789'); await ready(1);
  assert.equal(await evaluate("document.querySelector('.search-option-copy strong').textContent"), 'Download this video');
  await enter(); await wait("document.querySelector('#notice').textContent.includes('Download queued')");

  // Dismissal and navigation invalidate an in-flight response even if it ignores abort.
  await click('[data-page=following]'); await pageReady('following');
  await evaluate("window.fetch=(url,options)=>url==='/api/search' && JSON.parse(options.body).query==='late' ? window.originalFetch(url,{...options,signal:undefined}) : window.originalFetch(url,options)");
  for (const action of ['escape', 'navigation', 'disconnect']) {
    await type('late'); await sleep(310);
    if (action === 'escape') await escape();
    else if (action === 'navigation') await click('[data-page=recent]');
    else await click('#logout');
    release('late'); await sleep(180);
    assert.ok(await evaluate("document.querySelector('#search-dropdown').hidden && document.querySelector('#query').getAttribute('aria-expanded')==='false'"), action + ' suppresses late results');
    if (action !== 'disconnect') { await click('[data-page=following]'); await pageReady('following'); }
  }
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  console.log('PASS automatic dropdown: live-first viewer ranking on Twitch/Kick and Videos; stable ties/offline order; zero/unknown counts; sorted keyboard/pointer selection; trusted typing/debounce; stable library; live playback; keyboard/touch selection; offline/videos/categories/URLs/downloads; cancellation and stale success; provider switch; empty/error/retry; hostile metadata; IME; focus/Escape/outside dismissal; disconnect; 30-result scrolling; 9 widths from 320–1920px; zero JavaScript errors.');
};
