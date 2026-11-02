'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

module.exports = async ({ cdp, evaluate, wait, capture, output }) => {
  const measurements = [];
  const open = async input => {
    const count = await evaluate("document.querySelectorAll('#players video').length");
    await evaluate("document.querySelector('#open-dialog').showModal()");
    await evaluate(`document.querySelector('#open-query').value=${JSON.stringify(input)};document.querySelector('#open-form').requestSubmit()`);
    await wait(`document.querySelectorAll('#players video').length===${count + 1} && document.querySelectorAll('#players video')[${count}].currentTime>0.1`);
  };
  const device = async (width, height = 844) => {
    await cdp('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: true });
    await cdp('Emulation.setTouchEmulationEnabled', { enabled: true });
  };
  const measure = async label => {
    await evaluate("scrollTo(0,0);document.querySelectorAll('.video-surface').forEach(e=>e.classList.remove('controls-idle'));new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
    const m = await evaluate(`(()=>{
      const rect=e=>{const r=e.getBoundingClientRect();return {left:r.left,top:r.top,right:r.right,bottom:r.bottom,width:r.width,height:r.height}};
      return {width:innerWidth,height:innerHeight,scrollWidth:document.documentElement.scrollWidth,coarse:matchMedia('(pointer:coarse)').matches,
        players:[...document.querySelectorAll('.player:not([hidden])')].map(p=>{
          const bar=p.querySelector('.player-controls'),f=p.querySelector('[data-action=fullscreen]'),r=f.getBoundingClientRect();
          return {surface:rect(p.querySelector('.video-surface')),bar:rect(bar),decoded:p.querySelector('video').videoWidth>0,
            fullscreen:{...rect(f),visible:!f.hidden,hit:f.contains(document.elementFromPoint(r.x+r.width/2,r.y+r.height/2))},
            controls:[...bar.querySelectorAll('.control-group > *')].filter(e=>e.getBoundingClientRect().width>0).map(e=>({name:e.dataset.action||e.className,...rect(e)}))};
        })};
    })()`);
    m.label = label;
    measurements.push(m);
    fs.writeFileSync(path.join(output, 'measurements.json'), JSON.stringify(measurements, null, 2));
    assert.ok(m.coarse, label + ': real touch emulation is enabled');
    assert.ok(m.scrollWidth <= m.width, label + ': no page overflow');
    for (const p of m.players) {
      assert.ok(p.decoded, label + ': real video frames decoded');
      assert.ok(p.fullscreen.visible && p.fullscreen.hit, label + ': fullscreen is visible and receives input');
      for (const r of p.controls) {
        assert.ok(r.left >= p.surface.left - 1 && r.right <= p.surface.right + 1 && r.top >= p.surface.top - 1 && r.bottom <= p.surface.bottom + 1,
          label + ': ' + r.name + ' fits inside the video: ' + JSON.stringify({control:r,surface:p.surface}));
      }
      for (let i = 0; i < p.controls.length; i++) for (let j = i + 1; j < p.controls.length; j++) {
        const a = p.controls[i], b = p.controls[j];
        assert.ok(a.right <= b.left + 1 || b.right <= a.left + 1 || a.bottom <= b.top + 1 || b.bottom <= a.top + 1,
          label + ': ' + a.name + ' and ' + b.name + ' do not overlap');
      }
    }
    return m;
  };
  const tapFullscreen = async () => {
    const m = await measure('before fullscreen touch');
    const r = m.players[0].fullscreen;
    await cdp('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: r.left + r.width / 2, y: r.top + r.height / 2 }] });
    await cdp('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  };
  for (const source of ['live', 'vod', 'long-vod']) {
    await open(source === 'live' ? 'Northstar' : 'https://www.twitch.tv/videos/12345');
    if (source === 'long-vod') {
      // The six-second decoded fixture cannot supply an hours-long timeline. Feed
      // realistic metadata through the production duration/time event handlers.
      await evaluate(`(()=>{const v=document.querySelector('.player:not([hidden]) video');
        Object.defineProperty(v,'duration',{configurable:true,value:17*3600+59*60+59});
        Object.defineProperty(v,'currentTime',{configurable:true,value:12*3600+34*60+56});
        v.dispatchEvent(new Event('durationchange'));v.dispatchEvent(new Event('timeupdate'))})()`);
      assert.equal(await evaluate("document.querySelector('.player:not([hidden]) .video-time').textContent"), '12:34:56 / 17:59:59');
    }
    // 808/809 and 848/849 straddle video-container breakpoints with docked chat.
    for (const width of [320, 360, 375, 390, 414, 440, 650, 651, 760, 768, 800, 808, 809, 812, 820, 844, 848, 849, 900, 980, 981, 1020, 1021, 1440]) {
      await device(width);
      await measure(source + ' at ' + width);
      if ([320, 812].includes(width)) await capture(source + '-' + width);
    }
    // Use trusted touch input on the actual button, including the original failure
    // width, then rotate while fullscreen and exit using the same control.
    await device(812, 375);
    await tapFullscreen();
    await wait("!!document.fullscreenElement");
    await measure(source + ' landscape fullscreen');
    await device(320, 568);
    await measure(source + ' portrait fullscreen');
    await capture(source + '-portrait-fullscreen');
    await tapFullscreen();
    await wait("!document.fullscreenElement");
    await evaluate("document.querySelector('.player:not([hidden]) [data-action=close]').click()");
    await wait("document.querySelectorAll('#players video').length===0");
  }

  await device(1440);
  await open('Northstar');
  await open('Pixelquest');
  await evaluate("const t=document.querySelector('.player:not([hidden]) .multistream-toggle');t.checked=true;t.dispatchEvent(new Event('change'))");
  await wait("document.querySelector('#players').classList.contains('grid')");
  for (const width of [390, 812, 1180, 1202, 1440]) {
    await device(width);
    const m = await measure('two-stream grid at ' + width);
    assert.equal(m.players.length, 2);
  }
  await device(812, 375);
  await tapFullscreen();
  await wait("document.fullscreenElement?.id==='players'");
  await measure('two-stream grid fullscreen');
  await capture('grid-fullscreen');
  await tapFullscreen();
  await wait("!document.fullscreenElement");
  assert.deepEqual(await evaluate('window.visualErrors'), []);
  console.log('PASS mobile control bounds and input targets; no overlap; live, VOD and long timelines; 24 viewport widths; breakpoint edges; trusted fullscreen touches; fullscreen rotation; two-stream grids; zero JavaScript errors.');
};
