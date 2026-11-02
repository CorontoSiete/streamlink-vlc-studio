'use strict';
const assert = require('node:assert/strict');

module.exports = async ({ evaluate, wait, capture, viewport }) => {
  const order = () => evaluate("[...document.querySelectorAll('#cards .card')].map(card=>card.dataset.key)");
  const verify = async (page, provider = 'twitch') => {
    await viewport(1440);
    await evaluate(`document.querySelector('[data-page=${page}]').click()`);
    await wait(`document.querySelector('#studio').dataset.page==='${page}' && document.querySelector('#cards').getAttribute('aria-busy')==='false'`);
    if (page === 'vods') {
      await evaluate(`document.querySelector('#platform').value='${provider}';document.querySelector('#query').value='Northstar';document.querySelector('#search-form').requestSubmit()`);
      await wait("document.querySelectorAll('#cards .card').length===4 && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
      assert.deepEqual(await order(), ['new', 'broken', 'old', 'unknown']);
      await evaluate("document.querySelector('#more').click()");
    }
    await wait("document.querySelectorAll('#cards .card').length===5 && document.querySelector('#cards').getAttribute('aria-busy')==='false'");
    assert.deepEqual(await order(), ['latest', 'new', 'broken', 'old', 'unknown'], 'publication order survives pagination and missing dates');
    await wait("[...document.querySelectorAll('.card[data-key=latest] .card-media img,.card[data-key=new] .card-media img,.card[data-key=old] .card-media img')].every(img=>img.complete && img.naturalWidth>0)");
    await wait("!!document.querySelector('.card[data-key=broken] .card-placeholder')");
    assert.equal(await evaluate("document.querySelectorAll('.card[data-key=broken] .card-media img').length"), 0);
    assert.equal(await evaluate("document.querySelectorAll('.card[data-key=unknown] .card-placeholder').length"), 1);
    assert.equal(await evaluate("document.querySelector('.card[data-key=unknown] .card-published').textContent"), 'Publication date unavailable');
    assert.equal(await evaluate("document.querySelector('.card[data-key=new] time').dateTime"), '2026-11-02T15:00:00.000Z');
    assert.match(await evaluate("document.querySelector('.card[data-key=new] time').textContent"), /Published .*2026/);
    assert.ok(await evaluate("document.querySelector('.card[data-key=new] time').title.startsWith('Published ')"));
    await capture(page + '-' + provider + '-desktop');
    await viewport(390);
    assert.ok(await evaluate('document.documentElement.scrollWidth<=document.documentElement.clientWidth'), 'publication dates fit phone layouts');
    await capture(page + '-' + provider + '-phone');
  };
  await verify('downloads');
  await verify('vods');
  await verify('vods', 'kick');
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  console.log('PASS VOD libraries: newest publication first across pagination; full publication dates; loaded thumbnails; missing/broken artwork fallback; Twitch/Kick and Downloads at desktop/phone widths; no JavaScript errors.');
};
