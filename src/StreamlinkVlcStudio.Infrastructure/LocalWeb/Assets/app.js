'use strict';
(() => {
  // Old one-time desktop links are no longer authentication credentials.
  if (new URLSearchParams(location.hash.slice(1)).has('desktop')) history.replaceState(null, '', location.pathname + location.search);
  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const state = { csrf: '', page: 'following', players: [], selected: null, grid: false, settings: null, cursor: '', category: null, channel: '', generation: 0, polling: false, authenticated: false, items: [], followed: [], cardType: 'following', filter: 'all', busy: false };
  const navigationHistory = [];
  const search = { timer: null, abort: null, version: 0, items: [], active: -1, signature: '', status: '', message: '', mode: '', composing: false };
  const pages = {
    following: ['Following', 'YOUR DAILY LINEUP', 'Your favorite channels, together in one place.', 'Your channels'],
    discover: ['Discover', 'FIND YOUR NEXT FAVORITE', 'Explore categories. Find your people.', 'Browse categories'],
    vods: ['Videos', 'WORTH ANOTHER WATCH', 'Catch up on broadcasts, highlights and everything in between.', 'Channel videos'],
    recent: ['Recently watched', 'PICK UP WHERE YOU LEFT OFF', 'A familiar face is always one click away.', 'Your watch history'],
    downloads: ['Downloads', 'YOUR OFFLINE COLLECTION', 'Manage videos saved on your desktop. Watch them here.', 'Download library'],
    settings: ['Preferences', 'MAKE IT YOURS', 'A few small adjustments. A better way to watch.', '']
  };
  const iconPaths = {
    studio: 'M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2ZM10 8l6 4-6 4Z',
    home: 'M3 10 12 3l9 7v10a1 1 0 0 1-1 1h-5v-7H9v7H4a1 1 0 0 1-1-1Z',
    compass: 'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20ZM16 8l-3 5-5 3 3-5Z',
    video: 'M3 5h18v14H3ZM10 9l5 3-5 3Z',
    history: 'M3 4v5h5M3 9a9 9 0 1 1 0 6M12 7v5l3 2',
    download: 'M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5',
    settings: 'M9 3h6l1 3 3 1 2 5-2 5-3 1-1 3H9l-1-3-3-1-2-5 2-5 3-1ZM12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8Z',
    monitor: 'M3 3h18v14H3ZM8 21h8m-4-4v4',
    shield: 'M12 2 3 6v6c0 5 9 10 9 10s9-5 9-10V6ZM8 12l3 3 5-6',
    logout: 'M9 3H3v18h6m4-14 5 5-5 5M8 12h13',
    plus: 'M12 5v14M5 12h14',
    refresh: 'M20 7a9 9 0 1 0 1 9M20 3v5h-5',
    search: 'M10 3a7 7 0 1 0 0 14 7 7 0 0 0 0-14Zm5 12 6 6',
    play: 'M8 4v16l12-8Z',
    grid: 'M3 3h7v7H3ZM14 3h7v7h-7ZM3 14h7v7H3ZM14 14h7v7h-7Z',
    'panel-close': 'M3 4h18v16H3ZM9 4v16m6-11-3 3 3 3',
    'panel-open': 'M3 4h18v16H3ZM9 4v16m4-11 3 3-3 3',
    fullscreen: 'M3 9V3h6m6 0h6v6m0 6v6h-6m-6 0H3v-6',
    close: 'M6 6l12 12M6 18 18 6',
    check: 'M5 12l4 4L19 6',
    chat: 'M3 3h18v14H9l-6 5ZM7 8h10M7 12h6',
    move: 'M12 3v18M3 12h18M8 7l4-4 4 4M8 17l4 4 4-4M7 8l-4 4 4 4M17 8l4 4-4 4',
    resize: 'M5 19 19 5M5 12v7h7M12 5h7v7',
    grip: 'M9 5h.01M15 5h.01M9 12h.01M15 12h.01M9 19h.01M15 19h.01',
    send: 'M22 2 9 15M22 2l-7 20-6-7-7-6Z',
    pip: 'M3 4h18v16H3ZM12 11h7v7h-7Z',
    sliders: 'M4 7h16M4 17h16M8 4v6M16 14v6',
    'arrow-left': 'M20 12H4m6-6-6 6 6 6',
    'arrow-right': 'M4 12h16m-6-6 6 6-6 6',
    'arrow-down': 'M12 4v16m-6-6 6 6 6-6',
    'chevron-down': 'M6 9l6 6 6-6',
    eye: 'M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12Zm10-3a3 3 0 1 0 0 6 3 3 0 0 0 0-6Z'
  };
  Object.assign(iconPaths, {
    pause: 'M7 4h3v16H7ZM14 4h3v16h-3Z',
    volume: 'M3 9h4l5-5v16l-5-5H3ZM16 8a6 6 0 0 1 0 8m3-11a10 10 0 0 1 0 14',
    muted: 'M3 9h4l5-5v16l-5-5H3ZM16 9l6 6m0-6-6 6',
    heart: 'M20 5a5 5 0 0 0-8 1 5 5 0 0 0-8-1c-5 6 8 15 8 15S25 11 20 5Z',
    verified: 'm12 2 3 2 4 1 1 4 2 3-2 3-1 4-4 1-3 2-3-2-4-1-1-4-2-3 2-3 1-4 4-1ZM7 12l3 3 7-7',
    bits: 'm12 2 9 13-9 7-9-7ZM3 15l9-4 9 4',
    gift: 'M3 8h18v4H3ZM5 12v9h14v-9M12 8v13M12 8C2 8 5-1 9 3l3 5Zm0 0c10 0 7-9 3-5Z',
    star: 'm12 2 3 6 7 1-5 5 1 8-6-4-6 4 1-8-5-5 7-1Z',
    users: 'M16 21v-3a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v3M9 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8ZM17 4a4 4 0 0 1 0 7m2 3a4 4 0 0 1 3 4v3',
    share: 'M12 15V2m-5 5 5-5 5 5M5 10H3v12h18V10h-2',
    more: 'M12 4h.01M12 12h.01M12 20h.01',
    external: 'M14 3h7v7m0-7L10 14M10 3H3v18h18v-7',
    clip: 'M3 5h18v15H3ZM3 10h18M7 5v5m5-5v5m5-5v5',
    theater: 'M2 5h20v14H2Z'
  });
  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('class', 'icon'); svg.setAttribute('aria-hidden', 'true');
    const path = document.createElementNS(svg.namespaceURI, 'path'); path.setAttribute('d', iconPaths[name] || iconPaths.play); svg.append(path); return svg;
  }
  function hydrateIcons(root = document) { $$('[data-icon]', root).forEach(el => el.replaceChildren(icon(el.dataset.icon))); }
  hydrateIcons();
  const sidebarToggle = $('#sidebar-toggle');
  function setSidebarCollapsed(collapsed) {
    $('#studio').classList.toggle('sidebar-collapsed', collapsed);
    const label = collapsed ? 'Expand sidebar' : 'Collapse sidebar';
    sidebarToggle.setAttribute('aria-label', label);
    sidebarToggle.setAttribute('aria-expanded', String(!collapsed));
    sidebarToggle.title = label;
    sidebarToggle.replaceChildren(icon(collapsed ? 'panel-open' : 'panel-close'));
  }
  try { setSidebarCollapsed(localStorage.getItem('streamstudio.sidebarCollapsed') === 'true'); }
  catch { setSidebarCollapsed(false); }
  sidebarToggle.addEventListener('click', () => {
    const collapsed = !$('#studio').classList.contains('sidebar-collapsed');
    setSidebarCollapsed(collapsed);
    try { localStorage.setItem('streamstudio.sidebarCollapsed', String(collapsed)); } catch { /* The layout still works when browser storage is unavailable. */ }
  });
  let queue = Promise.resolve();
  let preferences = { layout: 'docked', size: 16, opacity: 90, keepInactive: false, pauseInactiveVod: true };
  const bounded = (value, min, max, fallback) => Number.isFinite(Number(value)) && value !== null ? Math.max(min, Math.min(max, Number(value))) : fallback;
  const hotkeyActions = [
    { id: 'back', label: 'Go back', key: 'Escape' },
    { id: 'nextTab', label: 'Next stream tab', key: ']' },
    { id: 'previousTab', label: 'Previous stream tab', key: '[' },
    { id: 'grid', label: 'Toggle Multi-stream grid', key: 'g' },
    { id: 'keepAudio', label: 'Toggle always keep audio on', key: 'a' },
    { id: 'mute', label: 'Mute / unmute selected tab', key: 'm' }
  ];
  function normalizeHotkey(value) {
    if (!value || typeof value !== 'object' || typeof value.key !== 'string') return null;
    const key = Array.from(value.key).length === 1 ? value.key.toLowerCase() : value.key;
    if (!/^[^\s\p{C}]$/u.test(key) && !/^(Escape|Backspace|Delete|Insert|Home|End|PageUp|PageDown|ArrowLeft|ArrowRight|ArrowUp|ArrowDown|Mouse[45]|F(?:[1-9]|1[0-2]))$/.test(key)) return null;
    if (['ctrl', 'alt', 'shift', 'meta'].some(name => value[name] !== undefined && typeof value[name] !== 'boolean')) return null;
    return { key, ctrl: !!value.ctrl, alt: !!value.alt, shift: !!value.shift, meta: !!value.meta };
  }
  function sameHotkey(a, b) { return !!a && !!b && ['key', 'ctrl', 'alt', 'shift', 'meta'].every(name => a[name] === b[name]); }
  function reservedHotkey(binding) {
    if ((binding.ctrl || binding.meta) && !binding.alt && binding.key === 'k') return true;
    return !binding.ctrl && !binding.alt && !binding.meta &&
      (binding.key === '/' || !binding.shift && ['k', 'f'].includes(binding.key));
  }
  function loadHotkeys(saved) {
    const result = {};
    for (const action of hotkeyActions) {
      const value = saved && typeof saved === 'object' && Object.hasOwn(saved, action.id) ? saved[action.id] : undefined;
      let binding = value === null ? null : normalizeHotkey(value) || normalizeHotkey({ key: action.key });
      if (binding && (reservedHotkey(binding) || Object.values(result).some(other => sameHotkey(other, binding)))) binding = null;
      result[action.id] = binding;
    }
    return result;
  }
  let hotkeys = loadHotkeys(preferences.hotkeys);
  function storeHotkeys(message) {
    preferences = { ...preferences, hotkeys: { ...hotkeys } };
    $('#hotkey-status').textContent = message + ' Saving to your account…';
    queuePreferencesSave('hotkeys');
  }
  function hotkeyLabel(binding) {
    if (!binding) return 'Not assigned';
    return [binding.ctrl && 'Ctrl', binding.alt && 'Alt', binding.shift && 'Shift', binding.meta && 'Meta', binding.key.length === 1 ? binding.key.toUpperCase() : binding.key].filter(Boolean).join(' + ');
  }
  function renderHotkeyPreferences() {
    $('#hotkey-bindings').replaceChildren(...hotkeyActions.map(action => {
      const row = node('div', undefined, 'setting-row hotkey-row');
      const label = node('label', action.label); label.htmlFor = 'hotkey-' + action.id;
      const controls = node('div', undefined, 'hotkey-controls');
      const input = node('input'); input.id = label.htmlFor; input.type = 'text'; input.readOnly = true;
      input.dataset.hotkey = action.id; input.value = hotkeyLabel(hotkeys[action.id]);
      input.setAttribute('aria-describedby', 'hotkey-help hotkey-status');
      const clear = button('Clear', () => {
        hotkeys = { ...hotkeys, [action.id]: null }; input.value = hotkeyLabel(null);
        storeHotkeys(action.label + ' cleared.');
      });
      clear.setAttribute('aria-label', 'Clear shortcut for ' + action.label.toLowerCase());
      controls.append(input, clear); row.append(label, controls); return row;
    }));
  }
  renderHotkeyPreferences();
  $('#reset-hotkeys').addEventListener('click', () => {
    hotkeys = loadHotkeys(); renderHotkeyPreferences();
    storeHotkeys('Default shortcuts restored.');
  });
  function hotkeyFromEvent(event, key = event.key) {
    return normalizeHotkey({ key, ctrl: event.ctrlKey, alt: event.altKey, shift: event.shiftKey, meta: event.metaKey });
  }
  function recordHotkey(event, input, key = event.key) {
    if (key === 'Tab') return;
    event.preventDefault(); event.stopPropagation();
    if (event.repeat || ['Control', 'Alt', 'Shift', 'Meta'].includes(key)) return;
    const binding = hotkeyFromEvent(event, key);
    if (!binding || reservedHotkey(binding)) {
      $('#hotkey-status').textContent = 'That key is reserved for typing, playback, search, or keyboard navigation. Choose another shortcut.';
      return;
    }
    const conflict = hotkeyActions.find(action => action.id !== input.dataset.hotkey && sameHotkey(hotkeys[action.id], binding));
    if (conflict) { $('#hotkey-status').textContent = 'Already assigned to ' + conflict.label.toLowerCase() + '. Clear that shortcut first.'; return; }
    hotkeys = { ...hotkeys, [input.dataset.hotkey]: binding }; input.value = hotkeyLabel(binding);
    storeHotkeys('Shortcut set to ' + input.value + '.');
  }
  const streamVolumes = new Map();
  const volumeSaveTimers = new Map();
  function restoreVolume(p, previous) {
    // Use the resolved channel, so live, replay and downloaded videos share a streamer's level.
    p.volumeKey = p.platform.trim() + ':' + p.channel.trim().toLowerCase();
    let volume = streamVolumes.get(p.volumeKey);
    if (previous?.volumeKey === p.volumeKey) volume = previous.video.volume;
    p.volume = volume ?? p.video.volume;
    p.video.volume = p.volume;
    streamVolumes.set(p.volumeKey, p.volume);
  }
  function rememberVolume(p) {
    if (p.closed || p.video.volume === p.volume) return;
    p.volume = p.video.volume;
    streamVolumes.set(p.volumeKey, p.volume);
    clearTimeout(volumeSaveTimers.get(p.id));
    volumeSaveTimers.set(p.id, setTimeout(() => { volumeSaveTimers.delete(p.id); run(() => saveVolume(p)); }, 250));
  }
  async function saveVolume(p) {
    if (state.authenticated) await api('volume/save', { player: p.id, volume: p.volume });
  }
  async function flushVolume(p) {
    if (!volumeSaveTimers.has(p.id)) return;
    clearTimeout(volumeSaveTimers.get(p.id)); volumeSaveTimers.delete(p.id);
    await saveVolume(p);
  }
  async function flushVolumes() { for (const p of state.players) await flushVolume(p); }
  function cancelVolumeSaves() {
    for (const timer of volumeSaveTimers.values()) clearTimeout(timer);
    volumeSaveTimers.clear(); streamVolumes.clear();
  }

  function node(tag, text, className) {
    const element = document.createElement(tag);
    if (text !== undefined) element.textContent = text;
    if (className) element.className = className;
    return element;
  }
  function providerLogo(platform) {
    const provider = (platform || '').trim().toLowerCase();
    if (provider !== 'twitch' && provider !== 'kick') return node('span', platform || '', 'provider-name');
    const logo = node('img', undefined, 'provider-logo');
    logo.classList.add('provider-logo-' + provider);
    logo.src = '/' + provider + '-logo.svg';
    logo.alt = provider === 'twitch' ? 'Twitch' : 'Kick';
    return logo;
  }
  function button(text, action) {
    const element = node('button', text);
    element.type = 'button';
    element.addEventListener('click', () => run(async () => { element.disabled = true; try { return await action(); } finally { element.disabled = false; } }));
    return element;
  }
  function notice(message, error = false) {
    const box = $('#notice'); box.replaceChildren(); box.hidden = !message;
    box.classList.toggle('error', error);
    if (!message) return;
    box.append(node('span', message, 'notice-message'));
    const close = button('', () => { box.hidden = true; }); close.className = 'icon-button'; close.setAttribute('aria-label', 'Dismiss notification'); close.append(icon('close')); box.append(close);
  }
  async function run(action) {
    try { return await action(); } catch (error) { notice(error.message, true); }
  }
  async function request(action, input = {}, method = 'POST', signal, busyRetries = 0) {
    if (signal?.aborted) throw new DOMException('Chat request canceled.', 'AbortError');
    const response = await fetch('/api/' + action, {
      method, credentials: 'same-origin', cache: 'no-store', signal,
      keepalive: action === 'chat/layout' || action === 'settings/save' || action === 'volume/save',
      headers: method === 'POST' ? { 'Content-Type': 'application/json', 'X-StreamStudio': '1', 'X-CSRF-Token': state.csrf } : {},
      body: method === 'POST' ? JSON.stringify(input) : undefined
    });
    // A canceled chat fetch can finish in the browser just before the server releases its
    // command gate. A 429 command has not run, so allow that short cancellation race to drain.
    if (response.status === 429 && action !== 'login' && busyRetries < 4) {
      await response.text();
      await new Promise(resolve => setTimeout(resolve, 100 * (busyRetries + 1)));
      return request(action, input, method, signal, busyRetries + 1);
    }
    if (!response.ok) {
      if (response.status === 401 && action !== 'login' && action !== 'session') { disconnect(); $('#login-error').textContent = 'Your session ended. Sign in again to reconnect.'; }
      let message = response.status === 429 ? (action === 'login' ? 'Too many attempts. Wait five minutes before trying again.' : 'The desktop is busy. Wait a moment and try again.') : response.status === 401 ? 'Username or password did not match.' : 'Couldn’t complete the request. Check your desktop connection and try again.';
      try { message = (await response.json()).error || message; } catch { /* Empty security responses. */ }
      throw new Error(message);
    }
    const text = await response.text();
    return text ? JSON.parse(text) : {};
  }
  function api(action, input = {}, signal) {
    const result = queue.then(() => request(action, input, 'POST', signal));
    queue = result.catch(() => {});
    return result;
  }
  function platform() { return $('#platform').value; }
  function image(url, alt) {
    const img = node('img');
    try {
      if (typeof url === 'string' && /^\/category-artwork\/[a-f0-9]{64}$/.test(url)) img.src = url;
      else { const parsed = new URL(url); if (parsed.protocol !== 'https:') return null; img.src = parsed.href; }
    } catch { return null; }
    img.alt = alt; img.loading = 'lazy'; img.referrerPolicy = 'no-referrer';
    img.addEventListener('error', () => { img.remove(); }, { once: true });
    return img;
  }
  function renderAvatar(avatar, name, url) {
    avatar.classList.remove('has-image');
    avatar.replaceChildren(node('span', initials(name), 'avatar-initials'));
    let source = safeChannelUrl(url, true);
    // Cached download pictures are served by the authenticated player, never file URLs.
    if (typeof url === 'string' && /^\/media\/[A-Za-z0-9_-]+\/profile$/.test(url)) source = url;
    if (!source) return avatar;
    const img = node('img'); img.alt = ''; img.referrerPolicy = 'no-referrer'; img.decoding = 'async';
    img.addEventListener('load', () => { if (img.parentNode === avatar) avatar.classList.add('has-image'); }, { once: true });
    img.addEventListener('error', () => { if (img.parentNode === avatar) { avatar.classList.remove('has-image'); img.remove(); } }, { once: true });
    avatar.append(img); img.src = source;
    return avatar;
  }
  function avatar(item, name = item.displayName || item.channel) {
    return renderAvatar(node('span', undefined, 'avatar'), name, item.profileImageUrl);
  }
  function duration(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) return '';
    const value = Math.floor(seconds);
    return [Math.floor(value / 3600), Math.floor(value / 60) % 60, value % 60].map((n, i) => i ? String(n).padStart(2, '0') : String(n)).join(':');
  }
  function disconnect() {
    clearTimeout(profileSaveTimer); profileSaveTimer = null; profileSaveTask = null; ++profileSaveEpoch;
    cancelVolumeSaves();
    navigationHistory.length = 0;
    closeSearch(); $('#query').value = '';
    ++state.generation;
    cancelCategoryViewerCounts();
    cancelFollowingHistory();
    state.authenticated = false;
    state.csrf = '';
    for (const p of state.players) { p.closed = true; p.followStatusAbort?.abort(); p.chatAbort?.abort(); p.chatResizeObserver?.disconnect(); p.panelsObserver?.disconnect(); releasePlayerMedia(p); p.element.remove(); }
    state.players = []; state.selected = null;
    $('#studio').hidden = true; $('#login').hidden = false; $('#watch').hidden = true;
    state.followed = []; $('#followed-channels').replaceChildren(); $('#sidebar-live-count').textContent = '';
    state.items = []; $('#cards').replaceChildren(); notice(''); $('#open-dialog').close(); closeChannels();
    $('#password').value = ''; $('#username').focus();
  }
  async function connected(session) {
    navigationHistory.length = 0;
    state.csrf = session.csrf;
    state.authenticated = true;
    $('#password').value = '';
    try { state.settings = await api('settings'); }
    catch (error) { disconnect(); throw error; }
    preferences = { ...preferences, ...state.settings.preferences };
    hotkeys = loadHotkeys(preferences.hotkeys);
    renderHotkeyPreferences();
    cancelVolumeSaves();
    for (const [key, volume] of Object.entries(preferences.volumes || {}))
      if (typeof volume === 'number' && Number.isFinite(volume) && volume >= 0 && volume <= 1) streamVolumes.set(key, volume);
    $('#login').hidden = true; $('#studio').hidden = false;
    $('#signed-in-user').textContent = session.username;
    $('#default-quality').replaceChildren(...state.settings.qualities.map(q => new Option(q, q)));
    $('#default-quality').value = state.settings.defaultQuality;
    $('#low-latency').checked = state.settings.lowLatency;
    $('#chat-layout').value = ['docked', 'overlay', 'hidden'].includes(preferences.layout) ? preferences.layout : 'docked';
    $('#chat-size').value = Math.max(12, Math.min(32, Number(preferences.size) || 16));
    $('#chat-opacity').value = bounded(preferences.opacity, 0, 100, 90);
    $('#keep-inactive').checked = !!preferences.keepInactive;
    $('#pause-inactive-vod').checked = preferences.pauseInactiveVod !== false;
    resetPreferencesSaveState();
    $('#account-status').replaceChildren(...['twitch', 'kick'].map(provider => {
      const connected = state.settings.accounts[provider];
      const item = node('span', undefined, 'account-status-item'); item.append(node('span', provider === 'twitch' ? 'Twitch' : 'Kick', provider), icon(connected ? 'check' : 'monitor'), document.createTextNode(connected ? 'Connected' : 'Connect on desktop')); return item;
    }));
    updateRangeValues();
    await showPage('following', null, false);
  }
  $('#login-form').addEventListener('submit', async event => {
    event.preventDefault();
    const submit = $('button', event.currentTarget); submit.disabled = true;
    try { await connected(await request('login', { username: $('#username').value.trim(), password: $('#password').value })); $('#login-error').textContent = ''; }
    catch (error) { $('#login-error').textContent = error.message; }
    finally { submit.disabled = false; }
  });
  const logout = () => run(async () => { await flushProfilePreferences(); await flushVolumes(); await saveAll(); await api('logout'); disconnect(); });
  $('#logout').addEventListener('click', logout);
  $('#mobile-logout').addEventListener('click', logout);
  $('#brand').addEventListener('click', event => { event.preventDefault(); run(() => showPage('following')); });
  $$('[data-page]').forEach(b => b.addEventListener('click', () => run(() => showPage(b.dataset.page))));
  $$('[data-nav-page]').forEach(b => b.addEventListener('click', () => run(() => showPage(b.dataset.navPage))));

  function navigationSnapshot() {
    return { page: state.page, category: state.category, query: $('#query').value, platform: platform(),
      vodType: $('#vod-type').value, filter: state.filter, liveOnly: $('#live-filter').checked, sort: $('#channel-sort').value,
      watching: $('#studio').classList.contains('watching'), selected: state.selected,
      scrollX: window.scrollX, scrollY: window.scrollY };
  }
  function sameNavigation(a, b) {
    return a.page === b.page && a.category?.id === b.category?.id && a.query === b.query && a.platform === b.platform &&
      a.vodType === b.vodType && a.watching === b.watching;
  }
  function rememberNavigation() {
    if (!state.authenticated || !$('#studio').dataset.page) return;
    const current = navigationSnapshot();
    if (!navigationHistory.length || !sameNavigation(navigationHistory.at(-1), current)) navigationHistory.push(current);
    if (navigationHistory.length > 100) navigationHistory.shift();
  }
  async function goBack() {
    if (document.fullscreenElement) { await document.exitFullscreen(); return; }
    const current = navigationSnapshot();
    let previous;
    do { previous = navigationHistory.pop(); } while (previous && sameNavigation(previous, current));
    if (!previous) return;
    const loading = showPage(previous.page, previous, false), generation = state.generation;
    await loading;
    if (!state.authenticated || generation !== state.generation) return;
    if (previous.watching && state.players.some(p => p.id === previous.selected && !p.closed && !p.closing)) {
      selectPlayer(previous.selected, false); state.players.find(p => p.id === previous.selected).video.focus({ preventScroll: true });
    } else $('#library').focus({ preventScroll: true });
    window.scrollTo({ left: previous.scrollX, top: previous.scrollY });
  }
  function renderBreadcrumbs() {
    $('#breadcrumbs').replaceChildren();
    if (state.category) $('#breadcrumbs').append(button('All categories', () => browseCategory(null, '')), node('span', '/ ' + state.category.name));
  }
  async function browseCategory(category, query = $('#query').value) {
    if (state.category?.id !== category?.id) rememberNavigation();
    state.category = category; state.items = []; $('#query').value = query; renderBreadcrumbs(); await load();
  }
  async function showPage(page, view = null, record = true) {
    if (!pages[page]) return;
    if (record && (page !== state.page || state.category || $('#studio').classList.contains('watching') ||
      view?.query !== undefined && view.query !== $('#query').value || view?.platform !== undefined && view.platform !== platform())) rememberNavigation();
    closeSearch();
    $('#studio').classList.remove('watching', 'theater');
    renderFollowedChannels();
    state.page = page; state.category = view?.category || null; state.cursor = ''; state.channel = ''; state.items = []; state.filter = view?.filter || 'all';
    if (view?.platform) $('#platform').value = view.platform;
    if (view?.vodType) $('#vod-type').value = view.vodType;
    if (view?.sort) $('#channel-sort').value = view.sort;
    $('#live-filter').checked = !!view?.liveOnly; $('#query').value = view?.query ?? (page === 'vods' ? $('#query').value : '');
    const [title, eyebrow, description, results] = pages[page];
    $('#page-title').textContent = title;
    $('#page-eyebrow').textContent = eyebrow; $('#page-description').textContent = description;
    $('#results-title').textContent = results; $('#location-label').textContent = title;
    document.title = title + ' · Stream Studio';
    $$('[data-page]').forEach(b => b.setAttribute('aria-current', b.dataset.page === page ? 'page' : 'false'));
    $$('[data-nav-page]').forEach(b => b.setAttribute('aria-current', b.dataset.navPage === page ? 'page' : 'false'));
    $('#preferences').hidden = page !== 'settings';
    $('.library-tools').hidden = page === 'settings'; $('#results-heading').hidden = page === 'settings';
    $('#search-form').hidden = page === 'settings';
    $('#studio').dataset.page = page;
    $('#refresh').hidden = page === 'settings'; $('#cards').hidden = page === 'settings';
    $('#vod-type').hidden = page !== 'vods' || platform() === 'kick';
    $('#download-input').hidden = page !== 'downloads';
    $('#platform-filters').hidden = page !== 'following';
    $('#sort-label').hidden = page !== 'following';
    $('#live-filter-label').hidden = page !== 'following';
    $('#empty-state').hidden = true;
    $('#query').placeholder = page === 'discover' ? 'Search categories…' : page === 'vods' ? 'Enter a channel name…' : page === 'downloads' ? 'Paste a video URL to download…' : 'Search channels or paste a URL…';
    renderBreadcrumbs();
    updateFilterButtons();
    const loading = load();
    if (state.players.length) $('#library').scrollIntoView({ block: 'start' });
    else window.scrollTo({ top: 0 });
    await loading;
  }
  function setBusy(busy) {
    state.busy = busy;
    $('#cards').setAttribute('aria-busy', String(busy));
    $('#refresh').disabled = busy; $('#more').disabled = busy;
  }
  function skeletons() {
    $('#cards').classList.remove('categories');
    $('#cards').replaceChildren(...Array.from({ length: 6 }, () => {
      const card = node('div', undefined, 'skeleton'); card.setAttribute('aria-hidden', 'true');
      card.append(node('div', undefined, 'skeleton-media'), node('div', undefined, 'skeleton-line'), node('div', undefined, 'skeleton-line')); return card;
    }));
  }
  function empty(title, message, symbol, actionText, action) {
    const box = $('#empty-state'); box.replaceChildren();
    const marker = node('span'); marker.append(icon(symbol));
    box.append(marker, node('h3', title), node('p', message));
    if (action) { const b = button(actionText, action); b.className = 'primary'; box.append(b); }
    box.hidden = false;
  }
  async function load(append = false) {
    const generation = ++state.generation;
    cancelCategoryViewerCounts();
    cancelFollowingHistory();
    const page = state.page;
    if (!append) state.cursor = '';
    $('#more').hidden = true; $('#empty-state').hidden = true; notice('');
    if (page === 'settings') { setBusy(false); $('#cards').replaceChildren(); return; }
    if (page === 'vods' && !$('#query').value.trim()) {
      $('#cards').replaceChildren(); $('#result-count').textContent = '';
      empty('Every channel has a back catalog.', 'Choose Twitch or Kick and start typing a channel name above to explore its videos.', 'video', 'Enter a channel', () => $('#query').focus());
      setBusy(false); return;
    }
    setBusy(true); $('#result-count').textContent = 'Loading…';
    if (!state.items.length) skeletons();
    try {
      let result;
      if (page === 'following') result = await api('following');
      else if (page === 'recent') result = await api('recent');
      else if (page === 'downloads') result = await api('downloads');
      else if (page === 'discover') {
        result = state.category
          ? await api('streams', { platform: platform(), categoryId: state.category.id, categoryName: state.category.name, cursor: state.cursor })
          : await api('categories', { platform: platform(), query: $('#query').value.trim(), cursor: state.cursor });
      } else if (page === 'vods') {
        state.channel = $('#query').value.trim();
        result = await api('vods', { platform: platform(), channel: state.channel, type: $('#vod-type').value, cursor: state.cursor });
      }
      if (generation !== state.generation || !state.authenticated) return;
      const items = [...(result.items || []), ...(result.offline || [])];
      if (page === 'following') { state.followed = items; renderFollowedChannels(); }
      const type = page === 'discover' && !state.category ? 'categories' : page;
      state.items = append ? [...state.items, ...items.filter(item => !state.items.some(old => (old.key || old.id) === (item.key || item.id)))] : items;
      state.cardType = type;
      state.cursor = result.nextCursor || '';
      renderResults();
      $('#more').hidden = !state.cursor;
      if (result.message) notice(result.message);
      if (type === 'categories' && platform() === 'twitch') loadCategoryViewerCounts(generation);
      if (page === 'following' && result.offline?.length) loadFollowingHistory(generation);
    } catch (error) {
      if (generation !== state.generation || !state.authenticated) return;
      if (!state.items.length) {
        $('#cards').replaceChildren();
        empty('Couldn’t load this page.', error.message, 'refresh', 'Try again', () => load());
      }
      notice(error.message, true);
    } finally { if (generation === state.generation) setBusy(false); }
  }
  let followingHistoryAbort = null;
  function cancelFollowingHistory() {
    followingHistoryAbort?.abort(); followingHistoryAbort = null;
  }
  function channelKey(item) { return (item.platform || '').trim().toLowerCase() + ':' + (item.channel || '').trim().toLowerCase(); }
  async function loadFollowingHistory(generation) {
    const controller = new AbortController(); followingHistoryAbort = controller;
    try {
      const result = await request('following/history', {}, 'POST', controller.signal);
      if (controller.signal.aborted || !state.authenticated || generation !== state.generation || state.page !== 'following') return;
      const updates = new Map((result.items || []).map(item => [channelKey(item), item]));
      for (const item of state.followed) {
        const update = updates.get(channelKey(item));
        if (item.state === 'Offline' && update) item.lastLiveAtUtc = update.lastLiveAtUtc;
      }
      // Update the existing text nodes so focused cards, filters and artwork stay in place.
      for (const element of $$('.last-live')) {
        const item = updates.get(element.dataset.channelKey);
        if (item) renderLastLive(element, item);
      }
    } catch { /* Cached times remain visible when optional history is unavailable. */ }
    finally { if (followingHistoryAbort === controller) followingHistoryAbort = null; }
  }
  function renderLastLive(element, item) {
    element.dataset.channelKey = channelKey(item);
    element.dataset.lastLiveAtUtc = typeof item.lastLiveAtUtc === 'string' ? item.lastLiveAtUtc : '';
    refreshLastLiveLabel(element);
  }
  function refreshLastLiveLabel(element) {
    const timestamp = Date.parse(element.dataset.lastLiveAtUtc), now = Date.now();
    if (!Number.isFinite(timestamp) || timestamp <= 0 || timestamp > now) {
      element.textContent = 'Last live unavailable';
      element.title = 'Broadcast history is unavailable for this channel.';
      return;
    }
    const minutes = (now - timestamp) / 60000;
    const [count, unit] = minutes < 60 ? [Math.floor(minutes), 'minute']
      : minutes < 1440 ? [Math.floor(minutes / 60), 'hour']
      : minutes < 43200 ? [Math.floor(minutes / 1440), 'day']
      : minutes < 525600 ? [Math.floor(minutes / 43200), 'month']
      : [Math.floor(minutes / 525600), 'year'];
    element.textContent = minutes < 1 ? 'Last live just now' : `Last live ${count} ${unit}${count === 1 ? '' : 's'} ago`;
    element.title = 'Latest available broadcast started ' + new Date(timestamp).toLocaleString(undefined, { dateStyle: 'full', timeStyle: 'short' });
  }
  function refreshLastLiveLabels() { $$('.last-live').forEach(refreshLastLiveLabel); }
  let categoryViewerCountsAbort = null;
  function cancelCategoryViewerCounts() {
    categoryViewerCountsAbort?.abort(); categoryViewerCountsAbort = null;
  }
  async function loadCategoryViewerCounts(generation) {
    const controller = new AbortController(); categoryViewerCountsAbort = controller;
    const pending = state.items.filter(item => item.viewerCount == null);
    const current = () => !controller.signal.aborted && state.authenticated && generation === state.generation && state.page === 'discover' && !state.category && platform() === 'twitch';
    // Each category can finish independently if another requires full stream pagination.
    // These read-only requests bypass the command queue and are also bounded on the server.
    const worker = async () => {
      while (pending.length && current()) {
        const item = pending.shift();
        let count = null;
        try {
          const result = await request('categories/viewers', { platform: 'twitch', categoryIds: [item.id] }, 'POST', controller.signal);
          const reported = result.available && result.items?.find(value => value.categoryId === item.id);
          if (reported && Number.isInteger(reported.viewerCount) && reported.viewerCount >= 0 && reported.viewerCount <= 2147483647) count = reported.viewerCount;
        } catch (error) { if (!current()) return; }
        if (!current()) return;
        if (count != null) item.viewerCount = count;
        const card = $$('#cards .card').find(card => card.dataset.key === item.id);
        const media = card && $('.card-media', card);
        if (media) renderMediaMeta(media, count == null ? 'Viewers unavailable' : count.toLocaleString() + ' viewers', count != null);
      }
    };
    try { await Promise.all(Array.from({ length: Math.min(4, pending.length) }, worker)); }
    finally { if (categoryViewerCountsAbort === controller) categoryViewerCountsAbort = null; }
  }
  function renderMediaMeta(media, text, viewers) {
    let meta = $('.media-meta', media);
    if (!text) { meta?.remove(); return; }
    if (!meta) { meta = node('span', undefined, 'media-meta'); media.append(meta); }
    meta.replaceChildren();
    if (viewers) meta.append(icon('eye'));
    meta.append(document.createTextNode(text));
  }
  function updateFilterButtons() {
    $$('[data-filter]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.filter === state.filter)));
  }
  function renderFollowedChannels() {
    const list = $('#followed-channels');
    const focusKey = document.activeElement?.dataset.focusKey;
    list.replaceChildren();
    const items = [...state.followed].sort((a, b) => Number(b.state === 'Live') - Number(a.state === 'Live') || (b.viewers || 0) - (a.viewers || 0));
    const live = items.filter(item => item.state === 'Live').length;
    $('#sidebar-live-count').textContent = live ? live + ' LIVE' : '';
    $('#following-count').textContent = live; $('#following-count').hidden = live === 0;
    const openChannels = new Set(state.players.map(channelKey));
    if (state.players.length) {
      const heading = node('div', undefined, 'following-group-label');
      heading.append(node('span', 'OPEN STREAMS'), node('span', String(state.players.length))); list.append(heading);
    }
    for (const p of state.players) {
      const item = items.find(item => channelKey(item) === channelKey(p));
      const name = item?.displayName || p.channel;
      const selected = state.selected === p.id && $('#studio').classList.contains('watching');
      const status = p.stopped ? 'Stopped' : selected ? 'Watching' : 'Open';
      const viewers = !p.isVod && !p.stopped && Number.isInteger(p.profile?.viewers) && p.profile.viewers >= 0 ? p.profile.viewers : null;
      const row = node('div', undefined, 'followed-channel is-open');
      row.dataset.provider = p.platform.toLowerCase(); row.dataset.playerId = p.id;
      row.classList.toggle('is-selected', selected);
      row.classList.toggle('has-viewers', viewers != null);
      const link = button('', () => { selectPlayer(p.id); closeChannels(); $('#watch').scrollIntoView({ block: 'start' }); });
      link.className = 'followed-link'; link.dataset.focusKey = 'select-' + p.id;
      link.setAttribute('aria-label', `${status}: ${name} on ${p.platform}${p.isVod ? ', video: ' + p.name : ''}${viewers == null ? '' : ', ' + viewers.toLocaleString() + ' viewers'}`);
      if (state.selected === p.id) link.setAttribute('aria-current', 'true');
      link.setAttribute('aria-controls', 'player-' + p.id);
      link.title = name + ' · ' + p.platform + ' · ' + p.name;
      const copy = node('span', undefined, 'followed-copy');
      copy.append(node('strong', name), node('span', p.isVod ? status + ' · Video · ' + p.name : status));
      const indicator = node('span', undefined, 'watching-indicator');
      indicator.append(icon(viewers != null || selected ? 'eye' : 'play'));
      if (viewers != null) {
        indicator.append(node('span', compactNumber(viewers)));
        indicator.title = viewers.toLocaleString() + ' viewers';
      }
      link.append(avatar({ profileImageUrl: p.profileImageUrl || item?.profileImageUrl }, name), copy, indicator);
      const exit = button('', async () => {
        exit.disabled = true;
        try {
          await closePlayer(p);
          if (!state.players.length) { closeChannels(); $('#library').focus(); }
          else ($$('[data-focus-key]', list).find(b => b.dataset.focusKey === 'select-' + state.selected) || $('#channels-toggle')).focus();
        } finally { exit.disabled = false; }
      });
      exit.className = 'followed-exit'; exit.dataset.focusKey = 'exit-' + p.id;
      exit.title = 'Exit ' + name + (p.isVod ? ' video: ' + p.name : ' stream'); exit.setAttribute('aria-label', exit.title); exit.append(icon('close'));
      row.append(link, exit); list.append(row);
    }
    const remaining = items.filter(item => !openChannels.has(channelKey(item)));
    if (state.players.length && remaining.length) list.append(node('div', 'YOUR CHANNELS', 'following-group-label'));
    if (!state.players.length && !items.length) { list.append(node('p', 'Follow channels on your desktop to add them here.', 'sidebar-empty')); return; }
    for (const item of remaining) {
      const name = item.displayName || item.channel;
      const live = item.state === 'Live';
      const category = item.categoryName?.trim();
      const row = button('', async () => { if (live) await openPlayer({ key: item.key }); else await browseVideos(item); closeChannels(); });
      row.className = 'followed-channel'; row.dataset.provider = item.platform?.toLowerCase() || '';
      row.title = name + ' · ' + item.platform + ' · ' + (live ? category || 'Live now' : 'Offline');
      row.setAttribute('aria-label', (live ? 'Watch ' : 'Browse videos from ') + name + ' on ' + item.platform + (live && category ? ', streaming ' + category : ''));
      const copy = node('span', undefined, 'followed-copy');
      copy.append(node('strong', name));
      if (live && category) copy.append(node('span', category));
      else if (!live) { const history = node('span', undefined, 'last-live'); renderLastLive(history, item); copy.append(history); }
      const status = node('span', live ? (item.viewers == null ? 'LIVE' : compactNumber(item.viewers)) : '', 'followed-status');
      if (live) status.prepend(node('i', '', 'live-dot'));
      row.append(avatar(item, name), copy, status); list.append(row);
    }
    if (focusKey) $$('[data-focus-key]', list).find(b => b.dataset.focusKey === focusKey)?.focus();
  }
  function compactNumber(value) { return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(Number(value)); }
  function initials(text) { return (text || 'SS').replace(/[^\p{L}\p{N} ]/gu, '').trim().slice(0, 2).toUpperCase() || 'SS'; }
  function renderResults() {
    let items = state.items;
    const following = state.page === 'following';
    if (following) {
      const live = state.items.filter(i => i.state === 'Live').length;
      $('#following-count').textContent = live; $('#following-count').hidden = live === 0;
      if (state.filter !== 'all') items = items.filter(i => i.platform?.toLowerCase() === state.filter);
      if ($('#live-filter').checked) items = items.filter(i => i.state === 'Live');
    }
    $('#platform-filters').hidden = !following; $('#live-filter-label').hidden = !following; $('#sort-label').hidden = !following;
    $('#results-title').textContent = state.category ? state.category.name : pages[state.page][3];
    if (following) {
      const sort = $('#channel-sort').value;
      items = [...items].sort((a, b) => {
        const liveFirst = Number(b.state === 'Live') - Number(a.state === 'Live');
        if (liveFirst) return liveFirst;
        if (sort === 'name') return (a.displayName || a.channel || '').localeCompare(b.displayName || b.channel || '');
        if (sort === 'viewers') return (b.viewers || 0) - (a.viewers || 0);
        return 0;
      });
    }
    const live = items.filter(i => i.state === 'Live').length;
    $('#result-count').textContent = following ? live + ' live · ' + items.length + ' channels' : items.length + (items.length === 1 ? ' result' : ' results');
    renderCards(items, state.cardType); $('#empty-state').hidden = items.length > 0;
    if (items.length) return;
    if (following && state.items.length) empty('No channels match these filters.', 'Try another platform or turn off Live only to see your offline channels.', 'sliders', 'Reset filters', () => { state.filter = 'all'; $('#live-filter').checked = false; updateFilterButtons(); renderResults(); });
    else if (state.page === 'following') empty('Your next favorite is out there.', 'Follow channels from the desktop app to see them here, or explore what’s streaming right now.', 'compass', 'Discover channels', () => showPage('discover'));
    else if (state.page === 'recent') empty('Start your watch history.', 'The live channels you watch will appear here, ready for your next visit.', 'history', 'Find something to watch', () => showPage('discover'));
    else if (state.page === 'downloads') empty('A little space for your favorites.', 'Browse a channel’s videos and choose Download, or paste a video URL above. Your desktop stores the files.', 'download', 'Browse videos', () => showPage('vods'));
    else if (state.page === 'vods') empty('No videos found for this channel.', 'Check the channel name, try another video type, or come back after the next broadcast.', 'video', 'Try another channel', () => $('#query').focus());
    else empty('Nothing here just yet.', 'Try another category or a different search.', 'compass', 'Browse all categories', () => browseCategory(null, ''));
  }
  function renderCards(items, type) {
    const cards = $('#cards'); cards.replaceChildren(); cards.classList.toggle('categories', type === 'categories');
    for (const item of items) {
      const card = node('article', undefined, 'card');
      card.dataset.key = item.id || item.key || ''; card.dataset.provider = item.platform?.toLowerCase() || platform();
      const offline = ['Offline', 'Unavailable'].includes(item.state); card.classList.toggle('is-offline', offline);
      const display = item.displayName || item.name || item.title || item.channel || 'Untitled';
      const liveCategory = type === 'following' && item.state === 'Live' ? item.categoryName?.trim() || '' : null;
      const main = button('', async () => {
        if (type === 'categories') {
          await browseCategory(item);
        } else if (type === 'downloads') await openPlayer({ downloadId: item.id });
        else if (offline) await browseVideos(item);
        else await openPlayer({ key: item.key });
      });
      main.className = 'card-main';
      main.setAttribute('aria-label', (type === 'categories' ? 'Browse ' : offline ? 'Browse videos from ' : type === 'downloads' ? 'Play downloaded video: ' : 'Watch ') + display
        + (item.platform ? ' on ' + item.platform : '')
        + (liveCategory === null ? '' : liveCategory ? ', streaming ' + liveCategory : ', category unavailable'));
      const media = node('div', undefined, 'card-media');
      const placeholder = unavailable => {
        const element = node('div', undefined, 'card-placeholder'); element.append(node('span', initials(display)));
        if (unavailable && type === 'categories') { element.classList.add('artwork-unavailable'); element.append(node('span', 'Artwork unavailable', 'artwork-unavailable-label')); }
        return element;
      };
      media.append(placeholder(false));
      const img = image(item.thumbnailUrl, '');
      if (img) { media.replaceChildren(img); img.addEventListener('error', () => media.prepend(placeholder(true)), { once: true }); }
      else media.replaceChildren(placeholder(true));
      if (item.state === 'Live') media.append(node('span', 'LIVE', 'on-air media-badge'));
      else if (item.watched) media.append(node('span', 'Watched', 'badge media-badge'));
      else if (offline) media.append(node('span', item.state === 'Unavailable' ? 'Unavailable' : 'Offline', 'badge media-badge'));
      const viewers = item.viewers ?? item.viewerCount;
      const mediaText = viewers != null ? Number(viewers).toLocaleString() + ' viewers' : item.duration ? duration(item.duration) : type === 'categories' && platform() === 'twitch' ? 'Loading viewers…' : '';
      renderMediaMeta(media, mediaText, viewers != null);
      const play = node('span', undefined, 'play-affordance'); play.append(icon(offline || type === 'categories' ? 'arrow-right' : 'play')); media.append(play);
      if (item.resume > 0 && item.duration > 0) { const track = node('div', undefined, 'resume-bar'); const progress = node('progress'); progress.max = item.duration; progress.value = item.resume; progress.setAttribute('aria-label', 'Watched progress'); track.append(progress); media.append(track); }
      const body = node('div', undefined, 'card-body'); const copy = node('div', undefined, 'card-copy');
      const heading = node('h3', display); heading.title = display; copy.append(heading);
      if (item.state === 'Offline') { const history = node('p', undefined, 'card-description last-live'); renderLastLive(history, item); copy.append(history); }
      const description = item.state === 'Offline' ? '' : offline ? 'Unavailable now · Browse past videos' : item.title && item.title !== display ? item.title : type === 'vods' ? item.channel : '';
      if (description) copy.append(node('p', description, 'card-description'));
      if (liveCategory !== null) copy.append(node('p', liveCategory ? 'Category: ' + liveCategory : 'Category unavailable', 'card-category'));
      const meta = node('div', undefined, 'card-meta');
      if (item.platform) { const provider = node('span', undefined, 'provider-label'); provider.append(providerLogo(item.platform)); meta.append(provider); }
      if (item.resume > 0) meta.append(node('span', 'Resume ' + duration(item.resume)));
      if (item.format) meta.append(node('span', item.format));
      if (type === 'downloads') meta.append(node('span', item.state.replace(/([a-z])([A-Z])/g, '$1 $2')));
      if (item.published) { const date = new Date(item.published); if (!Number.isNaN(date.getTime())) meta.append(node('span', date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }))); }
      copy.append(meta);
      if (type !== 'categories') body.append(avatar(item, item.displayName || item.channel || display));
      body.append(copy); main.append(media, body); card.append(main);
      if (type === 'downloads') {
        const progressBox = node('div', undefined, 'download-progress'); const progress = node('progress');
        progress.max = Math.max(1, item.totalSegments || 0); progress.value = item.completedSegments || 0; progress.setAttribute('aria-label', 'Download progress');
        progressBox.append(progress, node('p', (item.totalSegments > 0 ? Math.round(item.completedSegments / item.totalSegments * 100) + '% · ' : '') + ((item.bytesDownloaded || 0) / 1000000).toFixed(1) + ' MB'));
        if (item.error) progressBox.append(node('p', item.error));
        card.append(progressBox); main.disabled = !['Completed', 'ConversionQueued', 'Converting'].includes(item.state);
      }
      const actions = node('div', undefined, 'card-actions');
      if (type === 'vods') actions.append(button('Download', async () => { await api('downloads/add', { key: item.key, quality: state.settings.defaultQuality }); notice('Download queued. Follow its progress in Downloads.'); }));
      if (type === 'recent') actions.append(button('Remove', async () => { await api('recent/remove', { key: item.key }); await load(); }));
      if (item.channel && type !== 'vods' && type !== 'downloads') actions.append(button('Videos', () => browseVideos(item)));
      if (type === 'downloads') {
        if (['Failed', 'Canceled', 'Interrupted'].includes(item.state)) actions.append(button('Retry', async () => { await api('downloads/retry', { id: item.id }); await load(); }));
        if (['Queued', 'Resolving', 'Downloading', 'ConversionQueued', 'Converting'].includes(item.state)) actions.append(button('Cancel', async () => { await api('downloads/cancel', { id: item.id }); await load(); }));
        const remove = button('Delete', async () => { if (confirm('Permanently delete “' + item.title + '” and its downloaded media on the desktop?')) { await api('downloads/remove', { id: item.id }); await load(); } }); remove.className = 'danger'; actions.append(remove);
      }
      if (actions.childElementCount) card.append(actions); cards.append(card);
    }
  }
  async function browseVideos(item) { await showPage('vods', { platform: item.platform.toLowerCase(), query: item.channel }); }
  function closeSearch() {
    clearTimeout(search.timer); search.timer = null; search.abort?.abort(); search.abort = null; ++search.version;
    search.items = []; search.active = -1; search.signature = ''; search.status = ''; search.message = '';
    $('#search-dropdown').hidden = true; $('#search-options').replaceChildren(); $('#search-options').setAttribute('aria-busy', 'false');
    $('#query').setAttribute('aria-expanded', 'false'); $('#query').removeAttribute('aria-activedescendant');
    $('#search-announcement').textContent = '';
  }
  function searchMode(query) {
    if (/^(?:https?:\/\/|\/\/|(?:(?:www|m)\.)?(?:twitch\.tv|kick\.com)\/)/i.test(query)) return state.page === 'downloads' ? 'download' : 'url';
    return state.page === 'discover' ? 'categories' : state.page === 'downloads' ? '' : 'channels';
  }
  function matchedText(parent, text, query) {
    const value = String(text || ''), index = value.toLocaleLowerCase().indexOf(query.toLocaleLowerCase());
    if (index < 0 || !query) { parent.textContent = value; return; }
    parent.append(document.createTextNode(value.slice(0, index)), node('mark', value.slice(index, index + query.length)), document.createTextNode(value.slice(index + query.length)));
  }
  function setSearchActive(index) {
    const options = $$('#search-options [role=option]');
    search.active = index >= 0 && index < options.length ? index : -1;
    options.forEach((option, i) => option.setAttribute('aria-selected', String(i === search.active)));
    if (search.active < 0) $('#query').removeAttribute('aria-activedescendant');
    else { $('#query').setAttribute('aria-activedescendant', options[search.active].id); options[search.active].scrollIntoView({ block: 'nearest' }); }
  }
  function renderSearch(query) {
    const list = $('#search-options'), feedback = $('#search-feedback'); list.replaceChildren(); feedback.replaceChildren(); feedback.hidden = true;
    search.active = -1; $('#query').removeAttribute('aria-activedescendant');
    const provider = platform() === 'kick' ? 'Kick' : 'Twitch';
    $('#search-kind').textContent = search.mode === 'categories' ? 'CATEGORIES' : ['url', 'download'].includes(search.mode) ? 'READY WHEN YOU ARE' : state.page === 'vods' ? 'CHANNEL VIDEOS' : 'CHANNELS';
    $('#search-context').textContent = ['url', 'download'].includes(search.mode) ? 'Direct link' : provider; $('#search-context').className = ['url', 'download'].includes(search.mode) ? '' : platform();
    list.setAttribute('aria-label', search.mode === 'categories' ? 'Category suggestions' : 'Channel suggestions');
    list.setAttribute('aria-busy', String(search.status === 'loading'));
    if (search.status === 'loading') {
      for (let i = 0; i < 3; i++) { const row = node('div', undefined, 'search-skeleton'); row.setAttribute('aria-hidden', 'true'); row.append(node('span'), node('span')); list.append(row); }
      $('#search-announcement').textContent = 'Searching ' + provider + '…'; return;
    }
    for (const [index, item] of search.items.entries()) {
      const direct = search.mode === 'url' || search.mode === 'download', category = search.mode === 'categories';
      const name = direct ? search.mode === 'download' ? 'Download this video' : 'Open this stream or video' : item.displayName || item.channel || item.name;
      const offline = ['Offline', 'Unavailable'].includes(item.state), videos = state.page === 'vods' || offline;
      const row = node('div', undefined, 'search-option'); row.id = 'search-option-' + search.version + '-' + index;
      row.setAttribute('role', 'option'); row.setAttribute('aria-selected', 'false'); row.dataset.provider = (item.platform || platform()).toLowerCase();
      const visual = direct ? node('span', undefined, 'search-source-icon') : category ? node('span', undefined, 'search-category-art') : avatar(item, name);
      if (direct) visual.append(icon(search.mode === 'download' ? 'download' : 'play'));
      if (category) { const artwork = image(item.thumbnailUrl, ''); visual.append(artwork || icon('compass')); }
      const copy = node('span', undefined, 'search-option-copy'), heading = node('strong'); matchedText(heading, name, query);
      const detail = direct ? query : category ? item.viewerCount == null ? 'Explore category' : Number(item.viewerCount).toLocaleString() + ' viewers' : item.title || (videos ? 'Browse past broadcasts and highlights' : 'Open channel');
      copy.append(heading, node('span', detail, 'search-option-description'));
      const meta = node('span', undefined, 'search-option-meta');
      if (!direct && !category) {
        const live = item.state === 'Live';
        const status = node('span', live ? 'LIVE' : offline ? item.state === 'Unavailable' ? 'Unavailable' : 'Offline' : '', live ? 'search-live' : 'search-offline');
        if (live) status.prepend(node('i', undefined, 'live-dot'));
        meta.append(status);
        if (live && item.viewers != null) meta.append(node('span', compactNumber(item.viewers) + ' viewers', 'search-viewers'));
      }
      const action = direct ? search.mode === 'download' ? 'Download' : 'Open' : category ? 'Explore' : videos ? 'Videos' : 'Watch';
      const arrow = node('span', undefined, 'search-option-arrow'); arrow.append(icon('arrow-right')); meta.append(arrow);
      row.setAttribute('aria-label', action + ' ' + name + (item.platform ? ' on ' + item.platform : '') + (item.state === 'Live' ? ', live' + (item.viewers != null ? ', ' + Number(item.viewers).toLocaleString() + ' viewers' : '') : offline ? ', ' + item.state.toLowerCase() : ''));
      row.append(visual, copy, meta);
      row.addEventListener('pointermove', event => { if (event.pointerType === 'mouse' && search.active !== index) setSearchActive(index); });
      row.addEventListener('mousedown', event => event.preventDefault());
      row.addEventListener('click', () => run(() => chooseSearch(index)));
      list.append(row);
    }
    if (search.message || !search.items.length) {
      feedback.hidden = false;
      if (!search.items.length) {
        const symbol = node('span', undefined, 'search-feedback-icon'); symbol.append(icon(search.status === 'error' ? 'refresh' : 'search'));
        feedback.append(symbol, node('strong', search.status === 'error' ? 'Search couldn’t finish' : search.mode === 'categories' ? 'No categories found' : 'No channels found'));
        feedback.append(node('p', search.message || 'Try another name or switch platforms.'));
        if (search.status === 'error') { const retry = button('Try again', () => { $('#query').focus(); updateSearch(true); }); retry.className = 'search-retry'; feedback.append(retry); }
      } else feedback.append(node('p', search.message));
    }
    $('#search-announcement').textContent = ['url', 'download'].includes(search.mode) ? 'Press Enter to ' + (search.mode === 'download' ? 'download this video.' : 'open this stream or video.') : search.status === 'error' ? 'Search couldn’t finish. ' + search.message : search.items.length + (search.mode === 'categories' ? ' categories' : ' suggestions') + ' found on ' + provider + '.' + (search.items.length ? ' Use arrow keys to choose a result.' : ' Try another name or switch platforms.');
  }
  function updateSearch(immediate = false) {
    if (search.composing) return;
    const query = $('#query').value.trim(), mode = searchMode(query), signature = JSON.stringify([query, platform(), state.page]);
    if (!state.authenticated || !query || !mode) { closeSearch(); return; }
    if (signature === search.signature && search.status !== 'error' && !$('#search-dropdown').hidden) return;
    closeSearch(); search.signature = signature; search.mode = mode;
    $('#search-dropdown').hidden = false; $('#query').setAttribute('aria-expanded', 'true');
    if (mode === 'url' || mode === 'download') { search.status = 'ready'; search.items = [{}]; renderSearch(query); return; }
    const version = search.version, controller = new AbortController(); search.abort = controller; search.status = 'loading'; renderSearch(query);
    search.timer = setTimeout(async () => {
      search.timer = null;
      try {
        const result = await api(mode === 'categories' ? 'categories' : 'search', { query, platform: platform() }, controller.signal);
        if (version !== search.version || !state.authenticated) return;
        search.status = 'ready'; search.items = [...(result.items || [])];
        if (mode === 'channels') search.items.sort((a, b) => {
          const liveFirst = Number(b.state === 'Live') - Number(a.state === 'Live');
          if (liveFirst) return liveFirst;
          // Keep provider order for ties and offline results; unknown counts follow zero.
          return a.state === 'Live' ? (b.viewers ?? -1) - (a.viewers ?? -1) : 0;
        });
        search.message = result.message || ''; renderSearch(query);
      } catch (error) {
        if (version !== search.version || controller.signal.aborted || !state.authenticated) return;
        search.status = 'error'; search.message = error.message; renderSearch(query);
      } finally { if (search.abort === controller) search.abort = null; }
    }, immediate ? 0 : 250);
  }
  async function chooseSearch(index) {
    const item = search.items[index]; if (!item) return;
    const mode = search.mode, query = $('#query').value.trim(), provider = platform(); closeSearch();
    if (mode === 'categories') {
      await browseCategory(item, item.name); $('#library').focus();
    } else if (mode === 'download') {
      await api('downloads/add', { input: query, platform: provider, quality: state.settings.defaultQuality }); await load(); notice('Download queued.');
    } else if (mode === 'url') { await openPlayer({ input: query, platform: provider }); state.players.find(p => p.id === state.selected)?.video.focus(); }
    else if (state.page === 'vods' || ['Offline', 'Unavailable'].includes(item.state)) { await browseVideos(item); $('#library').focus(); }
    else { await openPlayer({ key: item.key }); state.players.find(p => p.id === state.selected)?.video.focus(); }
  }
  $('#query').addEventListener('input', () => updateSearch());
  $('#query').addEventListener('focus', () => updateSearch());
  $('#query').addEventListener('compositionstart', () => { search.composing = true; closeSearch(); });
  $('#query').addEventListener('compositionend', () => { search.composing = false; updateSearch(); });
  $('#query').addEventListener('keydown', event => {
    if (event.isComposing || search.composing) return;
    if (event.key === 'Escape' && (!$('#search-dropdown').hidden || $('#query').value)) { event.preventDefault(); event.stopPropagation(); closeSearch(); return; }
    if (['ArrowDown', 'ArrowUp'].includes(event.key)) {
      event.preventDefault(); event.stopPropagation();
      if ($('#search-dropdown').hidden) { updateSearch(true); return; }
      if (search.items.length) setSearchActive(search.active < 0 ? event.key === 'ArrowDown' ? 0 : search.items.length - 1 : (search.active + (event.key === 'ArrowDown' ? 1 : -1) + search.items.length) % search.items.length);
    } else if (event.key === 'Enter' && !$('#search-dropdown').hidden && search.active >= 0) { event.preventDefault(); event.stopPropagation(); run(() => chooseSearch(search.active)); }
  });
  document.addEventListener('pointerdown', event => { if (!$('#search-form').contains(event.target)) closeSearch(); });
  $('#search-form').addEventListener('focusout', event => { if (!$('#search-form').contains(event.relatedTarget)) closeSearch(); });
  $('#search-form').addEventListener('submit', event => {
    if (search.composing) { event.preventDefault(); return; }
    event.preventDefault(); run(async () => {
      const query = $('#query').value.trim();
      if (search.active >= 0 && !$('#search-dropdown').hidden) { await chooseSearch(search.active); return; }
      if (['url', 'download'].includes(searchMode(query))) { updateSearch(true); await chooseSearch(0); return; }
      if (state.page !== 'discover' && state.page !== 'vods') { updateSearch(true); return; }
      closeSearch();
      if (state.category) rememberNavigation();
      state.channel = ''; state.category = null; $('#breadcrumbs').replaceChildren();
      state.items = []; await load();
    });
  });
  $('#download-input').addEventListener('click', () => run(async () => {
    closeSearch();
    if (!$('#query').value.trim()) { $('#query').focus(); notice('Paste a video URL before choosing Download URL.', true); return; }
    await api('downloads/add', { input: $('#query').value.trim(), platform: platform(), quality: state.settings.defaultQuality }); await load();
  }));
  $('#refresh').addEventListener('click', () => run(() => load()));
  $('#more').addEventListener('click', () => run(() => load(true)));
  $('#platform').addEventListener('change', () => run(() => {
    const hasQuery = !!$('#query').value.trim(); closeSearch();
    if (hasQuery && state.page !== 'discover' && state.page !== 'vods') { updateSearch(true); return; }
    state.category = null; state.channel = ''; state.items = []; $('#breadcrumbs').replaceChildren();
    $('#vod-type').hidden = state.page !== 'vods' || platform() === 'kick'; return load();
  }));
  $('#query').addEventListener('search', () => {
    updateSearch();
  });
  $('#vod-type').addEventListener('change', () => run(() => { state.items = []; return load(); }));
  $$('[data-filter]').forEach(b => b.addEventListener('click', () => { state.filter = b.dataset.filter; updateFilterButtons(); renderResults(); }));
  $('#live-filter').addEventListener('change', renderResults);
  $('#channel-sort').addEventListener('change', renderResults);
  function showOpenDialog() {
    closeSearch();
    $('#open-error').textContent = ''; $('#open-platform').value = platform();
    if (!$('#open-dialog').open) $('#open-dialog').showModal(); $('#open-query').focus();
  }
  $('#close-open').addEventListener('click', () => $('#open-dialog').close());
  $('#open-dialog').addEventListener('click', event => {
    const rect = event.currentTarget.getBoundingClientRect();
    if (event.target === event.currentTarget && (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom)) event.currentTarget.close();
  });
  $('#open-form').addEventListener('submit', async event => {
    event.preventDefault(); const submit = $('button[type=submit]', event.currentTarget); submit.disabled = true;
    $('#open-error').textContent = '';
    try {
      const p = await openPlayer({ input: $('#open-query').value.trim(), platform: $('#open-platform').value });
      $('#open-dialog').close(); $('#open-query').value = '';
      // Closing a modal restores its old focus, which may select the previous video.
      selectPlayer(p.id); p.video.focus();
    }
    catch (error) { $('#open-error').textContent = error.message; notice(''); }
    finally { submit.disabled = false; }
  });
  function safeChannelUrl(value, image = false) {
    try {
      const url = new URL(value);
      return (url.protocol === 'https:' || !image && url.protocol === 'http:') && !url.username && !url.password ? url.href : '';
    } catch { return ''; }
  }
  function channelLink(label, url, className = '') {
    const link = node('a', label, className); link.href = url; link.target = '_blank'; link.rel = 'noopener noreferrer'; return link;
  }
  // Render the subset of Twitch panel Markdown used for descriptions without interpreting HTML.
  function panelInline(parent, text) {
    const pattern = /(!?\[([^\]]*)\]\((https?:\/\/[^\s)]+)\)|\*\*([^*]+)\*\*|__([^_]+)__|\*([^*]+)\*|`([^`]+)`)/g;
    let position = 0;
    for (const match of text.matchAll(pattern)) {
      parent.append(document.createTextNode(text.slice(position, match.index)));
      if (match[2] !== undefined) {
        const url = safeChannelUrl(match[3], match[0].startsWith('!'));
        if (!url) parent.append(document.createTextNode(match[2]));
        else if (match[0].startsWith('!')) {
          const img = node('img'); img.src = url; img.alt = match[2]; img.loading = 'lazy'; img.referrerPolicy = 'no-referrer'; parent.append(img);
        } else parent.append(channelLink(match[2], url));
      } else parent.append(node(match[4] || match[5] ? 'strong' : match[6] ? 'em' : 'code', match[4] || match[5] || match[6] || match[7]));
      position = match.index + match[0].length;
    }
    parent.append(document.createTextNode(text.slice(position)));
  }
  function panelMarkdown(text) {
    const content = node('div', undefined, 'panel-description'); let list = null;
    for (const line of String(text || '').replace(/\r/g, '').split('\n')) {
      if (!line.trim()) { list = null; continue; }
      const bullet = line.match(/^\s*(?:[-*+]\s+|\d+\.\s+)(.*)/);
      if (bullet) { if (!list) { list = node('ul'); content.append(list); } const item = node('li'); panelInline(item, bullet[1]); list.append(item); continue; }
      list = null;
      const heading = line.match(/^(#{1,3})\s+(.*)/);
      const block = node(heading ? 'h4' : 'p'); panelInline(block, heading ? heading[2] : line); content.append(block);
    }
    return content;
  }
  function renderChannelProfile(p, profile, loading = false) {
    p.profile = profile;
    const root = p.element, name = profile.displayName || p.channel;
    p.channelUrl = 'https://' + (p.platform === 'Twitch' ? 'www.twitch.tv/' : 'kick.com/') + encodeURIComponent(p.channel);
    for (const selector of ['.channel-name', '.channel-avatar-link']) $(selector, root).href = p.channelUrl;
    $('.channel-name', root).textContent = name; $('.about-name', root).textContent = name;
    for (const badge of $$('.channel-verified', root)) badge.hidden = !profile.isPartner;
    $('.broadcast-title', root).textContent = p.isVod ? p.title : profile.title || p.title;
    p.profileImageUrl = profile.profileImageUrl || p.profileImageUrl;
    renderAvatar($('.channel-avatar', root), name, p.profileImageUrl);
    if (state.players.includes(p)) renderFollowedChannels();
    $('.channel-live-badge', root).textContent = p.isVod ? 'VIDEO' : 'LIVE';
    $('.video-live-badge', root).hidden = p.isVod;
    const category = $('.channel-category', root); category.textContent = profile.category || ''; category.hidden = !profile.category;
    category.onclick = () => run(async () => {
      await showPage('discover', { platform: p.platform.toLowerCase() });
      $('#query').value = profile.category; $('#search-form').requestSubmit();
    });
    $('.channel-tags', root).replaceChildren(...(profile.tags || []).map(tag => node('span', tag, 'channel-tag')));
    const viewers = $('.channel-viewers', root); viewers.hidden = p.isVod || profile.viewers == null;
    $('span:last-child', viewers).textContent = profile.viewers == null ? '' : Number(profile.viewers).toLocaleString();
    const followers = $('.channel-followers', root); followers.hidden = profile.followers == null;
    followers.replaceChildren(node('strong', profile.followers == null ? '' : new Intl.NumberFormat('en', { notation: 'compact', maximumFractionDigits: 1 }).format(profile.followers)), document.createTextNode(' followers'));
    $('.channel-profile-status', root).replaceChildren();
    $('.channel-socials', root).replaceChildren(...(profile.links || []).flatMap(link => {
      const url = safeChannelUrl(link.url); if (!url) return [];
      const anchor = channelLink(link.title || new URL(url).hostname, url); anchor.prepend(icon('external')); return [anchor];
    }));
    const panels = $('.channel-panels', root); panels.replaceChildren(); p.panelCards = [];
    for (const panel of profile.panels || []) {
      const card = node('section', undefined, 'channel-panel');
      if (panel.title) card.append(node('h3', panel.title));
      const imageUrl = safeChannelUrl(panel.imageUrl, true), url = safeChannelUrl(panel.linkUrl);
      if (imageUrl) {
        const img = node('img'); img.src = imageUrl; img.alt = panel.title || (url ? new URL(url).hostname.replace(/^www\./, '') : name + ' channel panel');
        img.loading = 'lazy'; img.decoding = 'async'; img.referrerPolicy = 'no-referrer';
        img.addEventListener('error', () => img.replaceWith(node('span', 'Panel image unavailable', 'panel-image-error')), { once: true });
        if (url) { const anchor = channelLink('', url, 'panel-image-link'); anchor.append(img); card.append(anchor); } else card.append(img);
      }
      if (panel.description) card.append(panelMarkdown(panel.description));
      if (card.childNodes.length) p.panelCards.push(card);
    }
    layoutChannelPanels(p);
    const description = $('.channel-description', root);
    description.replaceChildren();
    if (profile.description) description.textContent = profile.description;
    else if (loading) description.textContent = 'Loading channel bio…';
    else if (p.panelCards.length) {
      // A missing short bio does not mean the channel has no introduction.
      // Untitled panels can contain sponsors as well as bios; preserve the source
      // content and let the viewer read the panels instead of selecting one by guesswork.
      description.append(node('p', 'This channel shares its information in the panels below.'));
      const shortcut = button('View channel panels', () => {
        panels.scrollIntoView({ block: 'start' });
        panels.focus({ preventScroll: true });
      });
      shortcut.className = 'channel-panels-link';
      description.append(shortcut);
    } else description.textContent = 'This streamer hasn’t added a bio yet.';
    const extensions = $('.channel-extensions', root); extensions.hidden = !profile.hasExtensions;
    extensions.href = p.channelUrl + '/about';
    for (const [selector, path] of [['.channel-bits', 'https://www.twitch.tv/bits'], ['.channel-gift', 'https://www.twitch.tv/subs/' + encodeURIComponent(p.channel)], ['.channel-subscribe', 'https://www.twitch.tv/subs/' + encodeURIComponent(p.channel)]]) {
      const anchor = $(selector, root); anchor.hidden = p.platform !== 'Twitch'; anchor.href = path;
    }
    updateChannelClock(p);
  }
  function layoutChannelPanels(p) {
    if (p.closed) return;
    const panels = $('.channel-panels', p.element);
    const count = Math.max(1, Math.floor(((panels.clientWidth || 0) + 20) / 340));
    if (panels.children.length === count && p.panelCards?.every(card => panels.contains(card))) return;
    const columns = Array.from({ length: count }, () => node('div', undefined, 'channel-panel-column'));
    panels.replaceChildren(...columns);
    (p.panelCards || []).forEach((card, index) => columns[index % count].append(card));
  }
  async function loadChannelProfile(p) {
    if (p.closed || p.loadingProfile) return;
    p.loadingProfile = true; p.lastProfileLoad = Date.now();
    try {
      const profile = await api('channel/profile', { player: p.id });
      if (p.closed) return;
      // Periodic viewer updates must not rebuild panel images or disturb selected text.
      const signature = JSON.stringify({ ...profile, viewers: null });
      if (p.profileSignature !== signature) { renderChannelProfile(p, profile); p.profileSignature = signature; }
      else {
        p.profile = profile;
        $('.channel-viewers', p.element).hidden = p.isVod || profile.viewers == null;
        $('.channel-viewers span:last-child', p.element).textContent = profile.viewers == null ? '' : Number(profile.viewers).toLocaleString();
        $('.channel-profile-status', p.element).replaceChildren();
        renderFollowedChannels();
      }
    } catch {
      if (p.closed) return;
      if (!p.profileSignature) $('.channel-description', p.element).textContent = 'Channel details are temporarily unavailable.';
      const status = $('.channel-profile-status', p.element); status.replaceChildren(node('span', 'Couldn’t refresh channel details. '), button('Try again', () => loadChannelProfile(p)));
    } finally { p.loadingProfile = false; }
  }
  function updateChannelClock(p) {
    const start = Date.parse(p.profile?.startedAt || '');
    $('.channel-uptime', p.element).textContent = !p.isVod && Number.isFinite(start) ? duration(Math.max(0, (Date.now() - start) / 1000)) : '';
  }
  function updateVideoControls(p) {
    const root = p.element, video = p.video;
    const play = $('[data-action=play]', root); play.replaceChildren(icon(video.paused ? 'play' : 'pause'));
    play.title = video.paused ? 'Play' : 'Pause'; play.setAttribute('aria-label', play.title);
    const mute = $('[data-action=mute]', root); mute.replaceChildren(icon(video.muted || video.volume === 0 ? 'muted' : 'volume'));
    mute.title = video.muted ? 'Unmute' : 'Mute'; mute.setAttribute('aria-label', mute.title);
    $('.video-volume', root).value = video.muted ? 0 : video.volume;
    const seek = $('.video-seek', root); seek.hidden = !p.isVod;
    seek.max = Number.isFinite(video.duration) && video.duration > 0 ? video.duration : 1;
    seek.value = video.currentTime;
    $('.video-time', root).textContent = p.isVod ? duration(video.currentTime) + ' / ' + duration(video.duration) : '';
    $('[data-action=live]', root).hidden = p.isVod && !p.liveSource;
    updateChannelClock(p);
  }
  function setupVideoControls(p) {
    const root = p.element, video = p.video, surface = $('.video-surface', root);
    video.controls = false;
    for (const name of ['play', 'pause', 'timeupdate', 'volumechange', 'loadedmetadata', 'durationchange']) video.addEventListener(name, () => updateVideoControls(p));
    const volume = $('.video-volume', root);
    const setVolume = value => { video.volume = value; p.manualMuted = false; applyAudio(); updateVideoControls(p); };
    volume.addEventListener('input', event => setVolume(Number(event.target.value)));
    volume.addEventListener('wheel', event => {
      if (!event.deltaY) return;
      event.preventDefault();
      const direction = event.deltaY < 0 ? 1 : -1;
      setVolume(Math.max(0, Math.min(100, Math.round(video.volume * 100) + direction * 5)) / 100);
    }, { passive: false });
    $('.video-seek', root).addEventListener('input', event => { if (Number.isFinite(video.duration)) video.currentTime = Number(event.target.value); });
    video.addEventListener('dblclick', () => run(() => playerAction(p, 'fullscreen')));
    video.addEventListener('keydown', event => {
      if (event.defaultPrevented || event.isComposing || event.repeat || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
      const action = { ' ': 'play', k: 'play', f: 'fullscreen' }[event.key.toLowerCase()];
      if (action) { event.preventDefault(); run(() => playerAction(p, action)); }
    });
    let hideTimer;
    const reveal = () => { surface.classList.remove('controls-idle'); clearTimeout(hideTimer); hideTimer = setTimeout(() => { if (!p.closed && !video.paused) surface.classList.add('controls-idle'); }, 2500); };
    surface.addEventListener('pointermove', reveal); surface.addEventListener('pointerdown', reveal);
    video.addEventListener('play', reveal); video.addEventListener('pause', reveal);
    updateVideoControls(p);
  }
  async function openPlayer(source, replace = null) {
    closeSearch();
    const existing = !replace && source.key && state.players.find(p => !p.stopped && (p.key === source.key || p.source.key === source.key));
    if (existing) { selectPlayer(existing.id); $('#watch').scrollIntoView({ block: 'start' }); return existing; }
    if (replace?.reloading) return replace;
    if (replace) { rememberVolume(replace); await flushVolume(replace); await saveProgress(replace); }
    if (replace) replace.reloading = true;
    notice('Resolving stream…');
    let result;
    try { result = await api('open', { ...source, quality: source.quality || state.settings.defaultQuality, replacePlayer: replace?.id }); }
    catch (error) {
      if (replace) {
        replace.reloading = false; replace.stopped = true; releasePlayerMedia(replace);
        $('.player-status', replace.element).textContent = 'Reload failed. Select Reload to retry.';
      }
      throw error;
    }
    const element = $('#player-template').content.firstElementChild.cloneNode(true);
    const menuFullscreen = node('button', 'Fullscreen'); menuFullscreen.dataset.action = 'fullscreen-menu';
    $('.stream-actions', element).append(menuFullscreen);
    hydrateIcons(element);
    const video = $('video', element);
    const p = { ...result, element, video, source, hls: null, after: 0, frontier: Math.max(0, result.resume - 30), vodMessages: [], vodMessageIds: new Set(), chatVersion: 0, chatDone: false, chatRetryAt: 0, lastSaved: 0, name: replace?.name || result.title, neverMute: replace?.neverMute || false, manualMuted: replace?.manualMuted || false, desiredMute: false, liveSource: replace?.liveSource || (!result.isVod ? source : null), closed: false };
    $('.video-surface', element).append($('.player-controls', element));
    $('.channel-stats', element).append($('.player-options', element));
    const item = [...state.items, ...state.followed].find(item => (source.key && item.key === source.key) ||
      item.channel?.toLowerCase() === p.channel.toLowerCase() && item.platform?.toLowerCase() === p.platform.toLowerCase());
    renderChannelProfile(p, { displayName: item?.displayName || p.channel, title: p.isVod ? p.title : item?.title || p.title,
      profileImageUrl: p.profileImageUrl || item?.profileImageUrl, viewers: item?.viewers, tags: [], links: [], panels: [] }, true);
    setupVideoControls(p);
    restoreVolume(p, replace);
    restoreChatGeometry(p);
    p.chatRows = new Map(); p.presentationVersion = -1; p.lastPresentationRefresh = 0;
    setupChatControls(p);
    p.panelsObserver = new ResizeObserver(() => {
      // Column changes can resize this same region; apply them after this delivery.
      if (p.panelsLayoutFrame) return;
      p.panelsLayoutFrame = requestAnimationFrame(() => { p.panelsLayoutFrame = 0; layoutChannelPanels(p); });
    });
    p.panelsObserver.observe($('.channel-panels', element));
    $('.rename', element).value = p.name;
    $('.player-title', element).textContent = p.name;
    $('.player-provider', element).textContent = p.platform;
    $('.player-provider', element).classList.add(p.platform.toLowerCase());
    $('.chat-live-indicator', element).textContent = p.isVod ? 'REPLAY' : 'LIVE';
    const quality = $('.quality', element);
    quality.replaceChildren(...state.settings.qualities.map(q => new Option(q, q))); quality.value = p.quality;
    $('.speed', element).addEventListener('change', event => { video.playbackRate = Number(event.target.value); });
    $('.multistream-toggle', element).addEventListener('change', event => run(() => setGrid(event.target.checked)));
    quality.addEventListener('change', () => run(() => openPlayer({ ...p.source, quality: quality.value }, p)));
    $('.rename', element).addEventListener('change', event => { p.name = event.target.value || p.title; renderPlayers(); });
    $('.never-mute', element).addEventListener('change', event => setKeepAudio(p, event.target.checked));
    $('.never-mute', element).checked = p.neverMute;
    video.addEventListener('volumechange', () => { if (p.closed) return; rememberVolume(p); if (video.muted !== p.desiredMute) p.manualMuted = video.muted; });
    element.addEventListener('pointerdown', () => { if (state.selected !== p.id) selectPlayer(p.id); });
    element.addEventListener('focusin', () => { if (state.selected !== p.id) selectPlayer(p.id); });
    $$('button[data-action]', element).forEach(b => b.addEventListener('click', () => run(async () => { b.disabled = true; try { await playerAction(p, b.dataset.action); } finally { if (['follow', 'unfollow'].includes(b.dataset.action)) renderFollowState(p); else b.disabled = false; } })));
    $('.chat-form', element).hidden = p.isVod;
    $('.chat-form', element).addEventListener('submit', event => {
      event.preventDefault(); run(async () => { const input = $('input', event.currentTarget); await api('chat/send', { player: p.id, message: input.value }); input.value = ''; });
    });
    $('[data-action=clip]', element).hidden = p.isVod;
    $('[data-action=follow]', element).hidden = p.isVod;
    $('[data-action=unfollow]', element).hidden = p.isVod;
    $('[data-action=download]', element).hidden = !p.isVod;
    $('[data-action=replay]', element).hidden = p.isVod;
    $('[data-action=live]', element).disabled = p.isVod && !p.liveSource;
    $('[data-action=pip]', element).hidden = !document.pictureInPictureEnabled || !video.requestPictureInPicture;
    const pageFullscreen = !!document.fullscreenEnabled && !!element.requestFullscreen;
    $('[data-action=fullscreen]', element).hidden = !pageFullscreen;
    menuFullscreen.hidden = !pageFullscreen;
    if (pageFullscreen) video.setAttribute('controlslist', 'nofullscreen');
    $('[data-action=predictions]', element).hidden = p.platform !== 'Twitch' || p.isVod;
    video.addEventListener('loadedmetadata', () => { if (p.resume > 0 && p.resume < video.duration) video.currentTime = p.resume; });
    video.addEventListener('playing', () => {
      $('.player-status', element).textContent = '';
      if (!p.startedSent && !p.closed) { p.startedSent = true; run(() => api('started', { player: p.id })); }
    });
    video.addEventListener('pause', () => { if (!p.closed) run(() => saveProgress(p)); });
    video.addEventListener('ended', () => run(() => saveProgress(p, true)));
    video.addEventListener('seeking', () => { if (p.isVod) resetVodChat(p); });
    video.addEventListener('seeked', () => { scheduleChatPoll(p); run(() => saveProgress(p)); });
    video.addEventListener('timeupdate', () => pumpVodChat(p));
    video.addEventListener('error', () => {
      if (p.closed || p.stopped) return;
      const reason = { 2: 'The video request failed.', 3: 'The browser could not decode the video.', 4: 'The browser could not play this video source.' }[video.error?.code] || 'The video could not be loaded.';
      $('.player-status', element).textContent = `${reason} Reload the stream to retry.`;
    });
    $('.player-status', element).textContent = 'Loading video…';
    const nativeHls = video.canPlayType('application/vnd.apple.mpegurl');
    const appleMobile = /iPhone|iPad|iPod/.test(navigator.userAgent) || navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1;
    if (p.isMp4) video.src = result.url;
    // iOS can report both native HLS and hls.js ManagedMediaSource support.
    // Keep its playback on the native HLS media stack.
    else if (appleMobile && nativeHls) video.src = result.url;
    else if (window.Hls && Hls.isSupported()) {
      p.hls = new Hls({ enableWorker: true, lowLatencyMode: state.settings.lowLatency, backBufferLength: 90, maxBufferLength: 30, xhrSetup: xhr => { xhr.withCredentials = true; } });
      p.hls.loadSource(result.url); p.hls.attachMedia(video);
      p.hls.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) $('.player-status', element).textContent = 'Playback stopped. Reload the quality or reopen this stream.'; });
    } else if (nativeHls) video.src = result.url;
    else $('.player-status', element).textContent = 'This browser does not support HLS or Media Source Extensions. Use a supported browser.';
    state.players.push(p); $('#players').append(element); selectPlayer(p.id); applyChatLayout(p);
    if (replace) await closePlayer(replace, true);
    renderPlayers(); $('#watch').hidden = false; $('#watch').scrollIntoView({ block: 'start' });
    notice('');
    video.play().catch(error => {
      if (p.closed || p.stopped) return;
      if (error?.name === 'NotAllowedError') $('.player-status', element).textContent = 'Tap Play to start on this device.';
      else if (error?.name !== 'AbortError') $('.player-status', element).textContent = 'The video could not start. Reload the stream to retry.';
    });
    loadChannelProfile(p);
    loadFollowStatus(p);
    scheduleChatPoll(p);
    return p;
  }
  function releasePlayerMedia(p) {
    p.hls?.destroy(); p.hls = null;
    p.video.pause(); p.video.removeAttribute('src'); p.video.load();
  }
  async function closePlayer(p, remoteClosed = false) {
    if (p.closed || p.closing) return;
    p.closing = true; p.followStatusAbort?.abort(); p.chatAbort?.abort();
    let failure;
    if (!remoteClosed) {
      rememberVolume(p);
      try { await flushVolume(p); } catch (error) { failure = error; }
      try { await saveProgress(p); } catch (error) { failure ??= error; }
    }
    if (p.closed) { if (failure) throw failure; return; }
    p.closed = true; p.chatResizeObserver?.disconnect(); p.panelsObserver?.disconnect(); releasePlayerMedia(p);
    p.element.remove(); state.players = state.players.filter(other => other !== p);
    if (state.selected === p.id) state.selected = state.players.at(-1)?.id || null;
    renderPlayers();
    if (!remoteClosed && state.authenticated) {
      try { await api('close', { player: p.id }); } catch (error) { failure ??= error; }
    }
    if (failure) throw failure;
  }
  function selectPlayer(id, record = true) {
    if (record && !$('#studio').classList.contains('watching')) rememberNavigation();
    state.selected = id; $('#studio').classList.add('watching'); renderPlayers();
  }
  function setKeepAudio(p, enabled) {
    p.neverMute = enabled; $('.never-mute', p.element).checked = enabled; applyAudio();
  }
  let switchingPlayer = false;
  async function cyclePlayer(direction) {
    if (switchingPlayer) return;
    const players = state.players.filter(p => !p.closed && !p.closing);
    if (!players.length) return;
    const index = players.findIndex(p => p.id === state.selected);
    const next = players[(index + direction + players.length) % players.length];
    const moveFullscreen = state.players.some(p => p.element === document.fullscreenElement && p !== next);
    switchingPlayer = true;
    try {
      if (moveFullscreen) await document.exitFullscreen();
      selectPlayer(next.id); next.video.focus({ preventScroll: true }); $('#watch').scrollIntoView({ block: 'start' });
      if (moveFullscreen) await next.element.requestFullscreen();
    } finally { switchingPlayer = false; }
  }
  function applyAudio() { for (const p of state.players) { p.desiredMute = p.manualMuted || (!p.neverMute && p.id !== state.selected); p.video.muted = p.desiredMute; } }
  function renderPlayers() {
    $('#watch').hidden = !state.players.length;
    if (!state.players.length) $('#studio').classList.remove('watching', 'theater');
    const grid = state.grid;
    $('#open-player-count').textContent = state.players.length ? String(state.players.length) : '';
    $('#players').classList.toggle('grid', grid);
    $('#players').style.setProperty('--grid-rows', String(Math.max(1, Math.ceil(state.players.length / 2))));
    $('#players').style.setProperty('--grid-count', String(Math.max(1, state.players.length)));
    for (const p of state.players) {
      // Keep every media row ahead of the channel information in multi-stream.
      // Move the existing section so its content and handlers survive toggling.
      const information = $('.channel-information', p.element);
      const informationParent = grid ? p.element : $('.screen', p.element);
      if (information.parentElement !== informationParent) informationParent.append(information);
      information.hidden = grid && p.id !== state.selected;
      p.element.id = 'player-' + p.id;
      $('.player-title', p.element).textContent = p.name;
      p.element.hidden = !grid && p.id !== state.selected;
      $('.multistream-toggle', p.element).checked = grid;
      const pause = !grid && p.id !== state.selected && (!preferences.keepInactive || p.isVod && preferences.pauseInactiveVod);
      if (pause && !p.video.paused) { p.pausedForSelection = true; p.video.pause(); }
      else if (!pause && p.pausedForSelection && !p.stopped && !p.closed) { p.pausedForSelection = false; p.video.play().catch(() => {}); }
      sizeChat(p);
    }
    applyAudio();
    renderFollowedChannels();
  }
  let changingGrid = false;
  async function setGrid(grid) {
    if (changingGrid) return;
    const fullscreen = document.fullscreenElement;
    const moveFullscreen = fullscreen === $('#players') || state.players.some(p => p.element === fullscreen);
    state.grid = grid;
    renderPlayers();
    if (!moveFullscreen) return;
    // The menu is available in fullscreen too; keep its host consistent with the layout.
    changingGrid = true;
    for (const p of state.players) $('.multistream-toggle', p.element).disabled = true;
    try {
      await document.exitFullscreen();
      const target = grid ? $('#players') : state.players.find(p => p.id === state.selected)?.element;
      if (target) await target.requestFullscreen();
    } finally {
      changingGrid = false;
      for (const p of state.players) $('.multistream-toggle', p.element).disabled = false;
    }
  }
  $('#channels-toggle').addEventListener('click', () => {
    $('#channels-dialog-list').append($('#channel-navigation'));
    $('#channels-dialog').showModal(); $('#channels-toggle').setAttribute('aria-expanded', 'true');
    ($$('[data-focus-key]').find(b => b.dataset.focusKey === 'select-' + state.selected) || $('#close-channels')).focus();
  });
  function closeChannels() {
    const dialog = $('#channels-dialog');
    dialog.close();
    $('#sidebar-channels-host').append($('#channel-navigation'));
    $('#channels-toggle').setAttribute('aria-expanded', 'false');
  }
  $('#close-channels').addEventListener('click', closeChannels);
  $('#channels-dialog').addEventListener('close', () => {
    // A previous close event can arrive after the user has reopened the list.
    if (!$('#channels-dialog').open) closeChannels();
  });
  $('#channels-dialog').addEventListener('click', event => {
    const rect = event.currentTarget.getBoundingClientRect();
    if (event.target === event.currentTarget && (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom)) closeChannels();
  });
  function renderFollowState(p) {
    const follow = $('[data-action=follow]', p.element), unfollow = $('[data-action=unfollow]', p.element);
    follow.disabled = unfollow.disabled = !!p.followBusy;
    if (typeof p.following === 'boolean') follow.setAttribute('aria-pressed', String(p.following));
    else follow.removeAttribute('aria-pressed');
    let label = p.following ? 'Following' : 'Follow';
    if (p.followStatusAbort) label = 'Checking…';
    else if (p.followBusy) label = p.followOperation === 'unfollow' ? 'Unfollowing…' : 'Following…';
    $('.follow-label', follow).textContent = label;
    follow.title = p.followStatusAbort ? 'Checking follow status' : p.followError || (p.following ? 'Unfollow channel' : 'Follow channel');
  }
  async function loadFollowStatus(p) {
    if (p.isVod) return;
    const controller = new AbortController();
    p.followStatusAbort = controller; p.followBusy = true; p.following = null;
    renderFollowState(p);
    try {
      while (!controller.signal.aborted) {
        const result = await api('follow/status', { player: p.id }, controller.signal);
        if (p.closed || p.closing || p.followStatusAbort !== controller) return;
        if (!result.pending) {
          p.following = typeof result.following === 'boolean' ? result.following : null;
          p.followError = result.message || (p.following === null ? 'Follow status unavailable. Reopen the channel to retry.' : '');
          return;
        }
        await new Promise(resolve => setTimeout(resolve, 500));
      }
    } catch (error) {
      if (!controller.signal.aborted) p.followError = error.message;
    } finally {
      if (p.followStatusAbort === controller) {
        p.followStatusAbort = null; p.followBusy = false;
        if (!p.closed) renderFollowState(p);
      }
    }
  }
  async function changeFollowState(p, action) {
    if (p.closed || p.closing || p.isVod || p.followBusy) return;
    if (action === 'follow' && p.following) action = 'unfollow';
    if (action === 'unfollow' && !confirm(`Unfollow ${p.channel}?`)) return;
    const peers = state.players.filter(other => !other.isVod && other.platform === p.platform && other.channel.toLowerCase() === p.channel.toLowerCase());
    for (const other of peers) {
      other.followStatusAbort?.abort(); other.followStatusAbort = null;
      other.followBusy = true; other.followOperation = action;
      renderFollowState(other);
    }
    try {
      const result = await api(action, { player: p.id }); notice(result.message);
      if (typeof result.following === 'boolean') for (const other of peers) { other.following = result.following; other.followError = ''; }
    } finally {
      for (const other of peers) { other.followBusy = false; other.followOperation = null; if (!other.closed) renderFollowState(other); }
    }
  }
  async function playerAction(p, action) {
    const video = p.video;
    switch (action) {
      case 'play': if (video.paused) await video.play(); else video.pause(); break;
      case 'mute': p.manualMuted = !video.muted; applyAudio(); updateVideoControls(p); break;
      case 'theater': {
        const enabled = $('#studio').classList.toggle('theater');
        $('[data-action=theater]', p.element).setAttribute('aria-pressed', String(enabled));
        sizeChat(p); break;
      }
      case 'share':
        if (navigator.clipboard?.writeText) { await navigator.clipboard.writeText(p.channelUrl); notice('Channel link copied.'); }
        else notice(p.channelUrl);
        break;
      case 'back': video.currentTime = Math.max(video.seekable.length ? video.seekable.start(0) : 0, video.currentTime - 30); break;
      case 'reload': await openPlayer({ ...p.source, quality: p.quality }, p); break;
      case 'stop': {
        p.followStatusAbort?.abort(); p.chatAbort?.abort();
        let failure;
        try { await saveProgress(p); } catch (error) { failure = error; }
        p.stopped = true; releasePlayerMedia(p);
        if (state.authenticated) {
          try { await api('close', { player: p.id }); } catch (error) { failure ??= error; }
        }
        $('.player-status', p.element).textContent = 'Stopped. Select Reload to play again.'; renderPlayers();
        if (failure) throw failure;
        break;
      }
      case 'forward': video.currentTime = Math.min(video.duration || Infinity, video.currentTime + 30); break;
      case 'live':
        if (p.isVod && p.liveSource) await openPlayer(p.liveSource, p);
        else if (p.hls?.liveSyncPosition) video.currentTime = p.hls.liveSyncPosition;
        else if (video.seekable.length) video.currentTime = Math.max(0, video.seekable.end(video.seekable.length - 1) - 2);
        break;
      case 'replay': {
        const replay = await api('replay', { player: p.id });
        if (!replay.available) { notice(replay.message); break; }
        await openPlayer({ key: replay.key, quality: p.quality }, p); break;
      }
      case 'fullscreen-menu': case 'fullscreen': if (document.fullscreenElement) await document.exitFullscreen(); else await (state.grid ? $('#players') : p.element).requestFullscreen(); break;
      case 'pip': await video.requestPictureInPicture(); break;
      case 'chat': $('.chat', p.element).hidden = !$('.chat', p.element).hidden; $('[data-action=chat]', p.element).setAttribute('aria-pressed', String(!$('.chat', p.element).hidden)); break;
      case 'clip': {
        const clip = await api('clip', { player: p.id }); notice(clip.message);
        if (clip.url) {
          const url = new URL(clip.url);
          if (url.protocol === 'https:') { const a = node('a', 'Open published clip'); a.href = url.href; a.target = '_blank'; a.rel = 'noopener noreferrer'; $('.notice-message').append(' ', a); }
        } break;
      }
      case 'follow': case 'unfollow': {
        await changeFollowState(p, action);
        break;
      }
      case 'download': await api('downloads/add', { key: p.key, quality: p.quality }); notice('Download queued on the desktop.'); break;
      case 'close': await closePlayer(p); break;
      case 'left': case 'right': {
        const from = state.players.indexOf(p), to = Math.max(0, Math.min(state.players.length - 1, from + (action === 'left' ? -1 : 1)));
        state.players.splice(from, 1); state.players.splice(to, 0, p); state.players.forEach(other => $('#players').append(other.element)); renderPlayers(); break;
      }
      case 'predictions': await showPredictions(p); break;
    }
  }
  async function showPredictions(p) {
    const panel = $('.prediction-panel', p.element);
    panel.hidden = false; panel.replaceChildren(node('p', 'Loading prediction…'));
    const result = await api('prediction', { player: p.id }); panel.replaceChildren();
    const prediction = result.prediction;
    if (prediction) {
      panel.append(node('h2', prediction.title));
      for (const outcome of prediction.outcomes) panel.append(node('p', `${outcome.title}: ${outcome.channelPoints.toLocaleString()} points · ${outcome.users} participants`));
      if (result.access.canManage) {
        const act = async (operation, outcomeId) => { await api('prediction/action', { player: p.id, predictionId: prediction.id, operation, outcomeId }); await showPredictions(p); };
        panel.append(button('Lock', () => act('lock')), button('Cancel prediction', () => act('cancel')));
        for (const outcome of prediction.outcomes) panel.append(button('Resolve: ' + outcome.title, async () => { if (confirm(`Resolve prediction with “${outcome.title}” as winner?`)) await act('resolve', outcome.id); }));
      }
    } else panel.append(node('p', 'No current prediction.'));
    if (result.access.canManage) {
      const form = node('form');
      for (const [name, placeholder, max] of [['title', 'Prediction title', 45], ['first', 'First outcome', 25], ['second', 'Second outcome', 25]]) {
        const input = node('input'); input.name = name; input.placeholder = placeholder; input.setAttribute('aria-label', placeholder); input.maxLength = max; input.required = true; form.append(input);
      }
      const windowInput = node('input'); windowInput.name = 'window'; windowInput.type = 'number'; windowInput.min = '30'; windowInput.max = '1800'; windowInput.value = '120'; windowInput.setAttribute('aria-label', 'Prediction window in seconds'); form.append(windowInput);
      const submit = node('button', 'Create prediction'); submit.type = 'submit'; form.append(submit);
      form.addEventListener('submit', event => { event.preventDefault(); run(async () => { const data = new FormData(form); await api('prediction/create', { player: p.id, title: data.get('title'), outcomes: [data.get('first'), data.get('second')], window: Number(data.get('window')) }); await showPredictions(p); }); }); panel.append(form);
    } else panel.append(node('p', result.access.message));
    panel.append(button('Hide predictions', () => { panel.hidden = true; }));
  }

  function appendChat(p, message) {
    const box = $('.chat-messages', p.element);
    const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 80;
    const key = message.messageId;
    if (key && p.chatRows.has(key)) { renderChatRow(p.chatRows.get(key), message); return; }
    const row = node('p', undefined, 'chat-message');
    renderChatRow(row, message);
    box.append(row); if (key) p.chatRows.set(key, row);
    while (box.childElementCount > 100) { const first = box.firstElementChild; if (first.chatMessage?.messageId) p.chatRows.delete(first.chatMessage.messageId); first.remove(); }
    if (atBottom) box.scrollTop = box.scrollHeight;
  }
  function chatImage(url, alt, badge = false) {
    let parsed;
    try { parsed = new URL(url, location.href); } catch { return null; }
    if (parsed.protocol !== 'https:' || parsed.origin === location.origin && !parsed.pathname.startsWith('/chat-assets/')) return null;
    const retryDelays = [1000, 5000, 15000];
    let retries = 0, fallback;
    function load() {
      const img = node('img'); img.alt = alt || ''; img.title = alt || '';
      img.className = badge ? 'badge-image' : 'chat-emote'; img.referrerPolicy = 'no-referrer'; img.decoding = 'async';
      img.addEventListener('load', () => { if (fallback?.isConnected) fallback.replaceWith(img); }, { once: true });
      img.addEventListener('error', () => {
        if (!img.isConnected && !fallback?.isConnected) return;
        fallback ??= badge ? node('span', alt, 'badge-fallback') : document.createTextNode(alt || '');
        if (img.isConnected) img.replaceWith(fallback);
        // An unchanged chat message will not be rendered again after a CDN failure.
        // Retry independently, with a finite budget, while its fallback is still visible.
        if (retries < retryDelays.length) {
          const delay = retryDelays[retries++];
          setTimeout(() => { if (fallback.isConnected) load(); }, delay);
        }
      }, { once: true });
      img.src = parsed.href;
      return img;
    }
    return load();
  }
  function renderChatRow(row, message) {
    if (row.chatSignature === JSON.stringify(message)) return;
    row.chatSignature = JSON.stringify(message); row.chatMessage = message; row.replaceChildren();
    for (const badge of message.badges || []) {
      const title = badge.title || badge.id;
      const img = chatImage(badge.imageUrl, title, true);
      row.append(img || node('span', title, 'badge-fallback'));
    }
    const username = node('strong', message.username);
    if (/^#[0-9a-f]{6}$/i.test(message.color || '')) username.style.color = message.color;
    row.append(username);
    const text = message.message || ''; let position = 0;
    for (const emote of [...(message.emotes || [])].sort((a, b) => a.startIndex - b.startIndex)) {
      if (!Number.isInteger(emote.startIndex) || !Number.isInteger(emote.endIndex) || emote.startIndex < position || emote.endIndex > text.length || emote.endIndex <= emote.startIndex) continue;
      row.append(document.createTextNode(text.slice(position, emote.startIndex)));
      const code = text.slice(emote.startIndex, emote.endIndex);
      row.append(chatImage(emote.imageUrl, emote.code || code) || document.createTextNode(code));
      position = emote.endIndex;
    }
    row.append(document.createTextNode(text.slice(position)));
  }
  function canPollChat(p) {
    return state.authenticated && !p.closed && !p.closing && !p.stopped && !$('.chat', p.element).hidden && (p.id === state.selected || state.grid);
  }
  function resetVodChat(p) {
    p.chatVersion++; p.chatAbort?.abort();
    p.frontier = Math.max(0, p.video.currentTime - 30); p.chatFrom = p.frontier;
    p.vodMessages = []; p.vodMessageIds.clear(); p.chatDone = false; p.chatRetryAt = 0; p.nextChatPoll = 0;
    p.chatRows.clear(); $('.chat-messages', p.element).replaceChildren();
    updateChatStatus(p, 'Loading replay chat…');
  }
  function pumpVodChat(p) {
    if (!p.isVod || !canPollChat(p) || p.video.seeking) return;
    // The media clock owns delivery, including pause and playback rate. Network latency must
    // never prevent already buffered messages from appearing when their time is reached.
    const position = p.video.currentTime;
    let due = 0;
    while (due < p.vodMessages.length && p.vodMessages[due].offset <= position) due++;
    if (!due) return;
    const messages = p.vodMessages.splice(0, due);
    for (const entry of messages.slice(-100)) appendChat(p, entry.message);
  }
  function scheduleChatPoll(p) {
    if (!canPollChat(p) || p.chatPolling || p.isVod && p.video.seeking || Date.now() < (p.nextChatPoll || 0) || Date.now() < p.chatRetryAt) return;
    if (p.isVod && !p.chatDone && p.frontier < p.video.currentTime - 30) resetVodChat(p);
    const version = p.chatVersion;
    p.chatAbort = new AbortController(); p.chatPolling = true; p.nextChatPoll = Date.now() + (p.isVod ? 1000 : 2000);
    pollChat(p, p.chatAbort.signal).catch(error => {
      if (p.closed || version !== p.chatVersion || error.name === 'AbortError') return;
      p.chatRetryAt = Date.now() + 3000;
      updateChatStatus(p, 'Chat will retry: ' + error.message);
    }).finally(() => {
      p.chatPolling = false;
      if (version !== p.chatVersion) scheduleChatPoll(p);
    });
  }
  async function pollChat(p, signal) {
    if (!canPollChat(p)) return;
    const version = p.chatVersion;
    if (p.isVod) {
      pumpVodChat(p);
      // Keep paging immediately until the look-ahead is covered. Busy Twitch pages may span
      // only a second; waiting for the maintenance timer between pages falls behind the video.
      for (let page = 0; page < 100 && canPollChat(p) && !p.chatDone &&
          !p.video.seeking && p.frontier < p.video.currentTime + 45 && p.vodMessages.length < 2000; page++) {
        const from = p.frontier;
        const result = await api('chat', { player: p.id, from }, signal);
        if (p.closed || version !== p.chatVersion || signal.aborted) return;
        for (const entry of result.messages || []) {
          if (!Number.isFinite(entry.offset) || entry.offset < (p.chatFrom ?? Math.max(0, p.resume - 30)) || !entry.message) continue;
          const key = entry.message.messageId || JSON.stringify([entry.offset, entry.message.username, entry.message.message, entry.message.timestamp]);
          if (p.vodMessageIds.has(key)) continue;
          p.vodMessageIds.add(key); p.vodMessages.push(entry);
        }
        while (p.vodMessageIds.size > 40000) p.vodMessageIds.delete(p.vodMessageIds.values().next().value);
        p.vodMessages.sort((a, b) => a.offset - b.offset);
        p.chatDone = ['Completed', 'Unsupported'].includes(result.outcome);
        updateChatStatus(p, result.status);
        pumpVodChat(p);
        if (result.outcome === 'Failed') { p.chatRetryAt = Date.now() + 3000; break; }
        if (!p.chatDone && (!Number.isFinite(result.through) || result.through <= from)) {
          p.chatRetryAt = Date.now() + 3000; updateChatStatus(p, 'Replay chat stalled. Retrying…'); break;
        }
        if (Number.isFinite(result.through) && result.through > from) p.frontier = result.through;
      }
    } else {
      const result = await api('chat', { player: p.id, after: p.after }, signal);
      if (p.closed || signal.aborted) return;
      for (const entry of result.messages || []) { appendChat(p, entry.message); p.after = Math.max(p.after, entry.sequence); }
      updateChatStatus(p, result.status);
    }
    if (Date.now() - p.lastPresentationRefresh >= 3000) {
      p.lastPresentationRefresh = Date.now();
      const generation = p.chatVersion;
      const result = await api('chat/presentation', { player: p.id, version: p.presentationVersion }, signal);
      if (p.closed || generation !== p.chatVersion || signal.aborted) return;
      p.presentationVersion = result.version ?? -1;
      const box = $('.chat-messages', p.element), atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 80;
      const updates = new Map((result.messages || []).map(message => [message.messageId, message]));
      for (const [id, row] of p.chatRows) { const updated = updates.get(id); if (updated) renderChatRow(row, updated); }
      for (const entry of p.vodMessages) entry.message = updates.get(entry.message.messageId) || entry.message;
      if (atBottom) box.scrollTop = box.scrollHeight;
    }
  }
  async function saveProgress(p, ended = false) {
    if (!state.authenticated || !p.isVod || p.closed || p.stopped || !Number.isFinite(p.video.duration) || p.video.duration <= 0) return;
    p.lastSaved = Date.now();
    await api('progress', { player: p.id, position: p.video.currentTime, duration: p.video.duration, ended });
  }
  async function saveAll() { for (const p of state.players) await saveProgress(p); }
  function updateChatStatus(p, status) {
    const label = $('.chat-status', p.element);
    label.textContent = status;
    label.dataset.connected = String(status === 'Connected');
  }
  function applyChatLayout(p) {
    const chat = $('.chat', p.element);
    const overlay = preferences.layout === 'overlay';
    if (!overlay) p.cancelChatGesture?.();
    const messages = $('.chat-messages', p.element);
    const atBottom = messages.scrollHeight - messages.scrollTop - messages.clientHeight < 80;
    p.element.classList.toggle('chat-overlay', overlay);
    p.element.classList.toggle('chat-transparent', overlay && bounded(preferences.opacity, 0, 100, 90) === 0);
    const parent = $(overlay ? '.video-surface' : '.video-chat', p.element);
    if (chat.parentElement !== parent) parent.append(chat);
    chat.hidden = preferences.layout === 'hidden';
    $('[data-action=chat]', p.element).setAttribute('aria-pressed', String(preferences.layout !== 'hidden'));
    p.element.style.setProperty('--chat-size', `${Math.max(12, Math.min(32, Number(preferences.size) || 16))}px`);
    p.element.style.setProperty('--chat-opacity', String(bounded(preferences.opacity, 0, 100, 90) / 100));
    sizeChat(p);
    if (atBottom) messages.scrollTop = messages.scrollHeight;
  }
  function restoreChatGeometry(p) {
    // The server resolves the channel and restores its durable app settings, including
    // after browser storage is cleared, the server address changes or the app updates.
    const layout = p.chatLayout;
    p.chatWidth = layout?.width ?? null; p.chatHeight = layout?.height ?? null;
    p.chatX = bounded(layout?.x, 0, 1, 1); p.chatY = bounded(layout?.y, 0, 1, 0);
  }
  function chatGeometry(p) {
    const surface = $('.video-surface', p.element);
    const maxWidth = Math.max(0, surface.clientWidth - 16), maxHeight = Math.max(0, surface.clientHeight - 8);
    const width = bounded(p.chatWidth, Math.min(160, maxWidth), maxWidth, Math.min(340, surface.clientWidth * .6, maxWidth));
    // Leave media controls clear by default, but allow moving and resizing to the video's bottom edge.
    const defaultHeight = Math.max(Math.min(140, maxHeight), surface.clientHeight - 60);
    const height = bounded(p.chatHeight, Math.min(140, maxHeight), maxHeight, defaultHeight);
    // Store position as a fraction of the available travel so it survives viewport and layout changes.
    const left = 8 + (maxWidth - width) * p.chatX;
    const top = 8 + (maxHeight - height) * p.chatY;
    return { width, height, left, top, maxWidth, maxHeight };
  }
  function sizeChat(p) {
    if (!p.element.classList.contains('chat-overlay') || p.element.hidden) return;
    const box = chatGeometry(p), chat = $('.chat', p.element);
    for (const key of ['width', 'height', 'left', 'top']) chat.style.setProperty(`--overlay-${key}`, `${box[key]}px`);
    $('.chat-resize-feedback', chat).textContent = `${Math.round(box.width)} × ${Math.round(box.height)}`;
  }
  function positionChat(p, left, top) {
    const box = chatGeometry(p), horizontal = box.maxWidth - box.width, vertical = box.maxHeight - box.height;
    if (horizontal > 0) p.chatX = bounded((left - 8) / horizontal, 0, 1, 1);
    if (vertical > 0) p.chatY = bounded((top - 8) / vertical, 0, 1, 0);
    sizeChat(p);
  }
  function resizeChat(p, width, height, anchor = chatGeometry(p)) {
    const box = chatGeometry(p), right = Math.min(anchor.left + anchor.width, box.maxWidth + 8);
    const top = Math.min(anchor.top, box.maxHeight + 8 - Math.min(140, box.maxHeight));
    // The lower-left resize handle keeps the opposite corner fixed, including after moving chat.
    p.chatWidth = bounded(width, Math.min(160, right - 8), right - 8, box.width);
    p.chatHeight = bounded(height, Math.min(140, box.maxHeight + 8 - top), box.maxHeight + 8 - top, box.height);
    positionChat(p, right - p.chatWidth, top);
  }
  function storeChatGeometry(p) {
    const layout = { width: p.chatWidth ?? null, height: p.chatHeight ?? null, x: p.chatX, y: p.chatY };
    // Commit after a completed gesture, never while moving or canceling it. The API
    // queue preserves commit order and completes these writes before a reopen/close.
    return run(async () => {
      try { await api('chat/layout', { player: p.id, layout }); }
      catch { throw new Error('Could not save this stream’s chat position and size. Reconnect and adjust the chat again to retry.'); }
    });
  }
  function setupChatControls(p) {
    const chat = $('.chat', p.element);
    let drag = null;
    const finish = save => {
      if (!drag) return;
      const previous = drag; drag = null;
      if (save) storeChatGeometry(p);
      else { Object.assign(p, previous.original); sizeChat(p); }
      chat.classList.remove('is-moving', 'is-resizing');
      if (previous.handle.hasPointerCapture(previous.id)) previous.handle.releasePointerCapture(previous.id);
    };
    p.cancelChatGesture = () => finish(false);
    for (const mode of ['move', 'resize']) {
      const handle = $('.chat-' + mode, p.element);
      handle.addEventListener('pointerdown', event => {
        if (event.button !== 0 || !event.isPrimary || drag || preferences.layout !== 'overlay') return;
        event.preventDefault(); event.stopPropagation(); handle.focus({ preventScroll: true });
        drag = { id: event.pointerId, handle, x: event.clientX, y: event.clientY, box: chatGeometry(p), original: { chatWidth: p.chatWidth, chatHeight: p.chatHeight, chatX: p.chatX, chatY: p.chatY } };
        handle.setPointerCapture(event.pointerId); chat.classList.add(mode === 'move' ? 'is-moving' : 'is-resizing');
      });
      handle.addEventListener('pointermove', event => {
        if (!drag || event.pointerId !== drag.id) return;
        const dx = event.clientX - drag.x, dy = event.clientY - drag.y, box = drag.box;
        if (mode === 'move') positionChat(p, box.left + dx, box.top + dy);
        else resizeChat(p, box.width - dx, box.height + dy, box);
      });
      for (const type of ['pointerup', 'pointercancel', 'lostpointercapture']) handle.addEventListener(type, event => {
        if (drag && event.pointerId === drag.id) finish(type === 'pointerup');
      });
      handle.addEventListener('keydown', event => {
        if (event.key === 'Escape' && drag) { event.preventDefault(); event.stopPropagation(); finish(false); return; }
        if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home'].includes(event.key) || preferences.layout !== 'overlay') return;
        event.preventDefault(); event.stopPropagation(); finish(false);
        const box = chatGeometry(p), step = event.shiftKey ? 40 : 10;
        const dx = event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0, dy = event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0;
        if (event.key === 'Home') {
          if (mode === 'move') { p.chatX = 1; p.chatY = 0; }
          else { p.chatWidth = p.chatHeight = null; }
          sizeChat(p);
        } else if (mode === 'move') positionChat(p, box.left + dx, box.top + dy);
        else resizeChat(p, box.width - dx, box.height + dy, box);
        storeChatGeometry(p);
      });
    }
    if (window.ResizeObserver) { p.chatResizeObserver = new ResizeObserver(() => sizeChat(p)); p.chatResizeObserver.observe($('.video-surface', p.element)); }
  }
  window.addEventListener('resize', () => state.players.forEach(sizeChat));
  document.addEventListener('fullscreenchange', () => state.players.forEach(sizeChat));
  function updateRangeValues() { $('#chat-size-value').textContent = $('#chat-size').value + 'px'; $('#chat-opacity-value').textContent = $('#chat-opacity').value + '%'; }
  $('#chat-size').addEventListener('input', updateRangeValues); $('#chat-opacity').addEventListener('input', updateRangeValues);
  const preferenceIds = new Map([
    ['default-quality', 'quality'], ['low-latency', 'lowLatency'], ['chat-layout', 'layout'],
    ['chat-size', 'size'], ['chat-opacity', 'opacity'], ['keep-inactive', 'keepInactive'],
    ['pause-inactive-vod', 'pauseInactiveVod']
  ]);
  let preferenceDraft = '';
  let profileSaveTimer = null, profileSaveTask = null, profileSaveEpoch = 0;
  let profileSaveRevision = 0, profileSavedRevision = 0, profileSaveError = '', profileFailureNotice = false;
  const profileDirtyKeys = new Set();
  function preferenceValues() {
    return { quality: $('#default-quality').value, lowLatency: $('#low-latency').checked,
      layout: $('#chat-layout').value, size: Number($('#chat-size').value), opacity: Number($('#chat-opacity').value),
      keepInactive: $('#keep-inactive').checked, pauseInactiveVod: $('#pause-inactive-vod').checked,
      hotkeys: { ...hotkeys } };
  }
  function updatePreferencesStatus() {
    $('#retry-settings').hidden = !profileSaveError;
    $('#save-status').textContent = profileSaveError ? 'Couldn’t save your preferences. Check the desktop connection and retry.'
      : profileSaveRevision !== profileSavedRevision ? 'Saving your preferences…' : 'All changes saved to your account.';
  }
  function resetPreferencesSaveState() {
    clearTimeout(profileSaveTimer); profileSaveTimer = null; profileSaveTask = null; ++profileSaveEpoch;
    profileSaveRevision = profileSavedRevision = 0; profileSaveError = ''; profileFailureNotice = false;
    profileDirtyKeys.clear();
    preferenceDraft = JSON.stringify(preferenceValues());
    updatePreferencesStatus();
  }
  function queuePreferencesSave(field, delay = 250) {
    if (!state.authenticated) return;
    if (field) profileDirtyKeys.add(field);
    const draft = JSON.stringify(preferenceValues());
    if (draft === preferenceDraft && !profileSaveError) return;
    preferenceDraft = draft; profileSaveError = ''; ++profileSaveRevision;
    clearTimeout(profileSaveTimer);
    profileSaveTimer = setTimeout(() => { profileSaveTimer = null; run(saveProfilePreferences); }, delay);
    updatePreferencesStatus();
  }
  async function saveProfilePreferences() {
    if (!state.authenticated || profileSaveRevision === profileSavedRevision) return;
    if (profileSaveTask) return profileSaveTask;
    const epoch = profileSaveEpoch;
    const task = (async () => {
      while (state.authenticated && epoch === profileSaveEpoch && profileSaveRevision !== profileSavedRevision) {
        const revision = profileSaveRevision;
        const values = JSON.parse(preferenceDraft);
        const patch = Object.fromEntries([...profileDirtyKeys].map(key => [key, values[key]]));
        try { await api('settings/save', patch); }
        catch (error) {
          if (!state.authenticated || epoch !== profileSaveEpoch) return;
          if (revision !== profileSaveRevision) continue;
          profileSaveError = error.message; profileFailureNotice = true;
          updatePreferencesStatus(); notice(error.message, true); return;
        }
        if (epoch !== profileSaveEpoch) return;
        const current = preferenceValues();
        for (const [key, value] of Object.entries(patch))
          if (JSON.stringify(current[key]) === JSON.stringify(value)) profileDirtyKeys.delete(key);
        profileSavedRevision = revision; profileSaveError = '';
        updatePreferencesStatus();
        $('#hotkey-status').textContent = 'Shortcuts saved to your account.';
        if (profileFailureNotice) { notice('Preferences saved.'); profileFailureNotice = false; }
      }
    })();
    profileSaveTask = task;
    try { await task; } finally { if (profileSaveTask === task) profileSaveTask = null; }
  }
  async function flushProfilePreferences() {
    clearTimeout(profileSaveTimer); profileSaveTimer = null;
    await saveProfilePreferences();
  }
  function preferenceChanged(event) {
    const id = event.target.id;
    if (!preferenceIds.has(id)) return;
    const values = preferenceValues();
    preferences = { ...preferences, ...values };
    state.settings.defaultQuality = values.quality; state.settings.lowLatency = values.lowLatency;
    if (id === 'keep-inactive' || id === 'pause-inactive-vod') renderPlayers();
    else if (id.startsWith('chat-')) state.players.forEach(applyChatLayout);
    queuePreferencesSave(preferenceIds.get(id));
  }
  $('#preferences').addEventListener('input', preferenceChanged);
  $('#preferences').addEventListener('change', preferenceChanged);
  $('#preferences').addEventListener('submit', event => event.preventDefault());
  $('#retry-settings').addEventListener('click', () => {
    if (profileSaveError) queuePreferencesSave(null, 0);
  });
  let lastDownloadRefresh = 0;
  setInterval(() => {
    for (const p of state.players) { pumpVodChat(p); scheduleChatPoll(p); }
  }, 250);
  setInterval(async () => {
    if (!state.authenticated || state.polling) return;
    state.polling = true;
    try {
      for (const p of [...state.players]) {
        updateChannelClock(p);
        if (!document.hidden && !p.closed && !p.stopped && (p.id === state.selected || state.grid) && Date.now() - p.lastProfileLoad >= 60000) loadChannelProfile(p);
        if (Date.now() - p.lastSaved >= 5000) await saveProgress(p);
      }
      if (state.page === 'downloads' && !state.busy && !document.hidden && !document.activeElement?.closest('#cards') && Date.now() - lastDownloadRefresh >= 8000) {
        lastDownloadRefresh = Date.now();
        const generation = state.generation; const result = await api('downloads');
        if (state.authenticated && state.page === 'downloads' && generation === state.generation && !document.activeElement?.closest('#cards') && JSON.stringify(state.items) !== JSON.stringify(result.items || [])) {
          state.items = result.items || []; renderResults();
        }
      }
    } catch (error) { if (state.authenticated) notice(error.message, true); }
    finally { state.polling = false; }
  }, 2000);
  // Save before normal tab backgrounding. Session expiry handles abrupt disconnect cleanup.
  setInterval(refreshLastLiveLabels, 60000);
  document.addEventListener('visibilitychange', () => { if (document.hidden) run(async () => { await flushProfilePreferences(); await flushVolumes(); await saveAll(); }); else refreshLastLiveLabels(); });
  window.addEventListener('pagehide', () => { run(async () => { await flushProfilePreferences(); await flushVolumes(); }); });

  function hotkeyActionForEvent(event, binding) {
    if ($('#open-dialog').open || $('#channels-dialog').open) return null;
    if (event.target?.closest?.('.chat-move,.chat-resize') && ['Home', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(binding?.key)) return null;
    if (binding?.key === 'Escape' && state.players.some(p => $('.chat', p.element).classList.contains('is-moving') || $('.chat', p.element).classList.contains('is-resizing'))) return null;
    const editing = element => {
      // Switches keep focus after a click, but do not use navigation shortcut keys.
      // Keep text fields, sliders, radio groups and selects in control of their keys.
      if (element?.matches?.('input')) return !['checkbox', 'button', 'submit', 'reset'].includes(element.type);
      return element?.matches?.('select,textarea,[role="textbox"]') || element?.isContentEditable;
    };
    if (editing(event.target) || editing(document.activeElement)) return null;
    const action = hotkeyActions.find(action => sameHotkey(hotkeys[action.id], binding));
    return action && (action.id === 'back' || state.players.some(p => p.id === state.selected && !p.closed && !p.closing)) ? action : null;
  }
  function runHotkeyAction(action) {
    const p = state.players.find(p => p.id === state.selected && !p.closed && !p.closing);
    run(async () => {
      switch (action.id) {
        case 'back': await goBack(); break;
        case 'nextTab': await cyclePlayer(1); break;
        case 'previousTab': await cyclePlayer(-1); break;
        case 'grid': await setGrid(!state.grid); break;
        case 'keepAudio': setKeepAudio(p, !p.neverMute); break;
        case 'mute': await playerAction(p, 'mute'); break;
      }
    });
  }
  document.addEventListener('keydown', event => {
    if (event.defaultPrevented || event.isComposing || event.keyCode === 229 || event.getModifierState?.('AltGraph') || !state.authenticated) return;
    const input = event.target?.closest?.('[data-hotkey]');
    if (input && !input.disabled) { recordHotkey(event, input); return; }
    const action = hotkeyActionForEvent(event, hotkeyFromEvent(event));
    if (!action) return;
    event.preventDefault(); event.stopPropagation();
    if (!event.repeat) runHotkeyAction(action);
  }, true);
  const handledMouseHotkeys = new Set();
  for (const type of ['pointerdown', 'mousedown', 'mouseup', 'auxclick']) document.addEventListener(type, event => {
    const key = event.button === 3 ? 'Mouse4' : event.button === 4 ? 'Mouse5' : null;
    if (!key) return;
    if (event.type === 'mouseup' || event.type === 'auxclick') {
      if (!handledMouseHotkeys.has(event.button)) return;
      event.preventDefault(); event.stopPropagation();
      if (event.type === 'auxclick') handledMouseHotkeys.delete(event.button);
      return;
    }
    if (event.defaultPrevented || !state.authenticated || event.getModifierState?.('AltGraph')) return;
    const input = document.activeElement?.closest?.('[data-hotkey]') || event.target?.closest?.('[data-hotkey]');
    const recording = input && !input.disabled;
    const binding = hotkeyFromEvent(event, key);
    const assigned = hotkeyActions.some(action => sameHotkey(hotkeys[action.id], binding));
    if (!recording && !assigned) return;
    // Keep side buttons from selecting the hovered stream or moving recorder focus.
    // Cancel mouse defaults through release/auxclick so browser history stays put.
    event.stopPropagation();
    if (event.type === 'pointerdown') return;
    event.preventDefault(); handledMouseHotkeys.add(event.button);
    if (recording) recordHotkey(event, input, key);
    else {
      const action = hotkeyActionForEvent(event, binding);
      if (action) runHotkeyAction(action);
    }
  }, true);
  window.addEventListener('blur', () => handledMouseHotkeys.clear());
  document.addEventListener('keydown', event => {
    if (event.defaultPrevented || event.isComposing || event.keyCode === 229) return;
    if ($('#open-dialog').open || $('#channels-dialog').open || !state.authenticated) return;
    const editing = document.activeElement?.matches('input,select,textarea') || document.activeElement?.isContentEditable;
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') { event.preventDefault(); showOpenDialog(); return; }
    if (event.key === '/' && !editing && !event.ctrlKey && !event.metaKey && !event.altKey && !$('#search-form').closest('[hidden]')) { event.preventDefault(); $('#query').focus(); return; }
    if (!['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
    const active = document.activeElement;
    if (active?.matches('input,select,textarea,video') || active?.isContentEditable) return;
    const from = active?.getBoundingClientRect();
    if (!from) return;
    const horizontal = event.key === 'ArrowLeft' || event.key === 'ArrowRight';
    const sign = event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 1;
    let best = null, bestScore = Infinity;
    for (const candidate of $$('button:not(:disabled),a[href],input,select,video,summary,[tabindex="0"]')) {
      if (candidate === active || !candidate.getClientRects().length) continue;
      const rect = candidate.getBoundingClientRect();
      const dx = rect.left + rect.width / 2 - from.left - from.width / 2;
      const dy = rect.top + rect.height / 2 - from.top - from.height / 2;
      const forward = (horizontal ? dx : dy) * sign;
      if (forward < 2) continue;
      const score = forward + Math.abs(horizontal ? dy : dx) * 3;
      if (score < bestScore) { best = candidate; bestScore = score; }
    }
    if (best) { event.preventDefault(); best.focus(); best.scrollIntoView({ block: 'nearest', inline: 'nearest' }); }
  });
  async function restoreSession() {
    try { await connected(await request('session', {}, 'GET')); $('#login-error').textContent = ''; }
    catch { /* A new or expired session can sign in. */ }
  }
  restoreSession();
})();
