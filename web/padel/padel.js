'use strict';
// Падельня — оболонка сторінки /padel/: хто я, запити до /api/padel, свій хаб /hub/padel, вкладки за #адресою,
// тост, лист (модальне вікно), вибір гравців. Вкладки — окремими файлами (board.js, tour.js…), кожна кличе
// Padel.tab({...}). Контракт сервера — D:/or-wt/_tools/padel-contract.md.
(function () {
  const $ = (s, r = document) => r.querySelector(s);
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const hue = (s) => { let h = 7; for (const c of String(s)) h = (h * 31 + c.codePointAt(0)) % 360; return h; };
  function plural(n, a, b, c) { n = Math.abs(n) % 100; const d = n % 10; if (n > 10 && n < 20) return c; if (d > 1 && d < 5) return b; if (d === 1) return a; return c; }

  const tabs = [];            // { id, icon, title, order, mount, show, hide, badge }
  const handlers = {};        // подія хаба → [fn]
  let current = null;         // { tab, arg }
  let conn = null;
  let front = null;           // відбиток файлів /padel/ на старті — після деплою сторінка перезавантажиться сама
  let playersCache = null, playersAt = 0;
  // Головна відкриває Падельню в рамці поверх себе (#padel), щоб не глушити радіо: тоді адресу шлемо їй, а посилання
  // на головну («←», «увійди на головній») не вантажимо в рамку — просимо головну закрити її.
  const framed = (() => { try { return window.parent !== window && parent.location.origin === location.origin; } catch { return false; } })();
  const toHost = (msg) => { if (framed) parent.postMessage(msg, location.origin); };

  const P = window.Padel = {
    me: { nick: '', account: false, admin: false, pid: null },
    esc, hue, plural,

    /// Аватарка-кружечок з першою літерою (як на сайті).
    av(name, cls) { const n = String(name || '?'); return '<span class="av' + (cls ? ' ' + cls : '') + '" style="--h:' + hue(n) + '">' + esc([...n][0].toUpperCase()) + '</span>'; },
    /// Гравець рядком: аватарка + ім'я (+ «гість»). p — {pid, name, guest} або pid.
    who(p, opts) {
      const o = typeof p === 'string' ? { pid: p, name: P.name(p), guest: p.startsWith('g:') } : (p || {});
      return '<span class="pp">' + P.av(o.name) + '<span>' + esc(o.name || '?') + '</span>'
        + (o.guest && !(opts && opts.noGuest) ? '<span class="muted small">гість</span>' : '') + '</span>';
    },
    money(n) { return (Math.round(n || 0)).toLocaleString('uk-UA').replace(/,/g, ' ') + ' грн'; },
    /// Київський час: «сб, 4 жовт · 18:00»; opts.time=false — без часу, opts.rel — «сьогодні/завтра».
    when(iso, opts) {
      if (!iso) return '';
      const d = new Date(iso), o = opts || {};
      const tz = { timeZone: 'Europe/Kyiv' };
      const day = new Intl.DateTimeFormat('uk-UA', { ...tz, weekday: 'short', day: 'numeric', month: 'short' }).format(d);
      const time = new Intl.DateTimeFormat('uk-UA', { ...tz, hour: '2-digit', minute: '2-digit' }).format(d);
      let head = day;
      if (o.rel !== false) {
        const key = (x) => new Intl.DateTimeFormat('en-CA', { ...tz, year: 'numeric', month: '2-digit', day: '2-digit' }).format(x);
        const today = key(new Date()), tomorrow = key(new Date(Date.now() + 864e5));
        if (key(d) === today) head = 'сьогодні'; else if (key(d) === tomorrow) head = 'завтра';
      }
      return o.time === false ? head : head + ' · ' + time;
    },
    /// «за 25 хв», «2 год 5 хв тому» — для годинника оренди й стрічок.
    span(ms) { const m = Math.max(0, Math.round(Math.abs(ms) / 60000)); const h = Math.floor(m / 60); return h ? h + ' год' + (m % 60 ? ' ' + (m % 60) + ' хв' : '') : m + ' хв'; },

    /// Запит до сервера. body — об'єкт (тоді POST, якщо method не дано). Відмова сервера ({ok:false, message}) —
    /// тост і throw, щоб виклик міг зупинитись: `try { await P.api(...) } catch { return; }`.
    async api(path, body, method) {
      const opt = { method: method || (body !== undefined ? 'POST' : 'GET'), headers: {}, credentials: 'same-origin' };
      if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
      let res, data = null;
      try { res = await fetch(path.startsWith('/') ? path : '/api/padel/' + path, opt); }
      catch (e) { P.toast('Нема зв\'язку з сервером', 'bad'); throw e; }
      try { data = await res.json(); } catch { /* порожня відповідь */ }
      if (!res.ok || (data && data.ok === false)) {
        const msg = (data && data.message) || (res.status === 403 ? 'Це може лише той, хто має право' : res.status === 404 ? 'Не знайшлось' : 'Щось пішло не так');
        P.toast(msg, 'bad');
        const err = new Error(msg); err.status = res.status; err.data = data; throw err;
      }
      return data;
    },

    /// Підписка на подію хаба /hub/padel (match, tournament, gathering, money, rating, lobby, toast).
    on(name, fn) { (handlers[name] = handlers[name] || []).push(fn); },

    toast(text, kind) {
      const el = $('#pdToast'); el.textContent = text; el.className = 'pd-toast show' + (kind === 'bad' ? ' bad' : '');
      clearTimeout(P.toast.t); P.toast.t = setTimeout(() => { el.className = 'pd-toast'; }, 3200);
    },

    /// Лист поверх сторінки. html — вміст; повертає { el, close }. onClose — коли закрили (хрестик, тло, Esc, close()).
    sheet(title, html, onClose) {
      const bg = document.createElement('div'); bg.className = 'pd-sheet-bg';
      bg.innerHTML = '<div class="pd-sheet" role="dialog" aria-modal="true"><div class="pd-sheet-h"><b>' + esc(title) + '</b>'
        + '<button class="pd-sheet-x" aria-label="Закрити">✕</button></div><div class="pd-sheet-b">' + html + '</div></div>';
      let done = false;
      const close = () => { if (done) return; done = true; bg.remove(); document.removeEventListener('keydown', onKey); if (onClose) onClose(); };
      const onKey = (e) => { if (e.key === 'Escape') close(); };
      bg.addEventListener('click', (e) => { if (e.target === bg || e.target.closest('.pd-sheet-x')) close(); });
      document.addEventListener('keydown', onKey);
      document.body.appendChild(bg);
      return { el: bg.querySelector('.pd-sheet-b'), close };
    },

    /// Свої налаштування цього пристрою (тема табло, голос…) — localStorage, без падінь у приватному вікні.
    pref(key, val) {
      const k = 'padel.' + key;
      try {
        if (val === undefined) { const v = localStorage.getItem(k); return v == null ? null : JSON.parse(v); }
        localStorage.setItem(k, JSON.stringify(val));
      } catch { /* приватне вікно — просто не пам'ятаємо */ }
      return val;
    },

    /// Вкладка: { id, icon, title, order, mount(host), show(host, arg), hide() }. mount — раз, show — на кожен перехід.
    tab(t) {
      if (!t || !t.id) return;
      const i = tabs.findIndex((x) => x.id === t.id);
      if (i >= 0) tabs[i] = t; else tabs.push(t);
      tabs.sort((a, b) => (a.order || 99) - (b.order || 99));
      if (started) { renderTabs(); route(); }
    },
    /// Перейти: P.go('tour', 't3') → #tour/t3.
    go(id, arg) { const h = '#' + id + (arg != null && arg !== '' ? '/' + arg : ''); if (location.hash === h) route(); else location.hash = h; },
    /// Позначка на вкладці (кількість боргів, живих матчів…); 0/'' — прибрати.
    badge(id, n) { const t = tabs.find((x) => x.id === id); if (t) { t.badge = n; renderTabs(); } },
    get current() { return current; },

    players: {
      /// Усі гравці й акаунти: { players:[{pid,name,guest,…}], accounts:[{pid,name}] } (кеш на 30 с).
      async load(force) {
        if (!force && playersCache && Date.now() - playersAt < 30000) return playersCache;
        try { playersCache = await P.api('players'); playersAt = Date.now(); } catch { playersCache = playersCache || { players: [], accounts: [] }; }
        return playersCache;
      },
      known() { return playersCache; },
      forget() { playersCache = null; },
    },
    /// Ім'я за pid із кешу гравців (до завантаження — нік з pid або «?»).
    name(pid) {
      if (!pid) return '?';
      const c = playersCache;
      const p = c && ((c.players || []).find((x) => x.pid === pid) || (c.accounts || []).find((x) => x.pid === pid));
      return p ? p.name : pid.startsWith('u:') ? pid.slice(2) : '?';
    },

    /// Вибір гравців у host: чипи всіх знайомих (спершу ті, хто грав нещодавно), пошук, «+ гість».
    /// opts: { value:[pid], max, onChange(pids), hint }. Повертає { get(), set(pids) }. Порядок вибору зберігається
    /// (номер на чипі) — табло бере перших двох у пару.
    async pick(host, opts) {
      const o = opts || {};
      let value = (o.value || []).slice();
      const data = await P.players.load();
      const seen = new Set();
      const list = [];
      for (const p of (data.players || [])) { if (p.linkedTo || seen.has(p.pid)) continue; seen.add(p.pid); list.push(p); }
      for (const a of (data.accounts || [])) { if (seen.has(a.pid)) continue; seen.add(a.pid); list.push({ ...a, guest: false }); }
      let find = '';
      const paint = () => {
        const f = find.trim().toLowerCase();
        const shown = list.filter((p) => !f || p.name.toLowerCase().includes(f) || value.includes(p.pid));
        host.querySelector('.chips').innerHTML = shown.map((p) => {
          const i = value.indexOf(p.pid);
          return '<button type="button" class="chip' + (i >= 0 ? ' on' : '') + '" data-pid="' + esc(p.pid) + '">' + P.av(p.name) + esc(p.name)
            + (p.guest ? '<span class="g">гість</span>' : '') + (i >= 0 && o.numbered ? '<span class="n">' + (i + 1) + '</span>' : '') + '</button>';
        }).join('') || '<span class="muted small">Нікого не знайшлось — додай гостя.</span>';
        const cnt = host.querySelector('.pd-pick-cnt'); if (cnt) cnt.textContent = value.length + (o.max ? ' з ' + o.max : '');
      };
      host.classList.add('pd-pick');
      host.innerHTML = (o.hint ? '<div class="muted small" style="margin-bottom:6px">' + esc(o.hint) + ' <b class="pd-pick-cnt"></b></div>' : '')
        + '<div class="pd-pick-find"><input class="inp" type="search" placeholder="знайти або вписати ім\'я гостя" maxlength="24">'
        + '<button type="button" class="btn sm" data-guest>+ гість</button></div><div class="chips"></div>';
      const inp = host.querySelector('input');
      inp.addEventListener('input', () => { find = inp.value; paint(); });
      const addGuest = async () => {
        const name = inp.value.trim();
        if (!name) { P.toast('Впиши ім\'я гостя в поле пошуку'); inp.focus(); return; }
        let r; try { r = await P.api('guests', { name }); } catch { return; }
        const g = r.player;
        if (!list.some((x) => x.pid === g.pid)) list.unshift(g);
        P.players.forget();
        if (!value.includes(g.pid) && (!o.max || value.length < o.max)) value.push(g.pid);
        inp.value = ''; find = ''; paint(); if (o.onChange) o.onChange(value.slice());
      };
      host.querySelector('[data-guest]').addEventListener('click', addGuest);
      inp.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); addGuest(); } });
      host.querySelector('.chips').addEventListener('click', (e) => {
        const b = e.target.closest('[data-pid]'); if (!b) return;
        const pid = b.dataset.pid, i = value.indexOf(pid);
        if (i >= 0) value.splice(i, 1);
        else { if (o.max && value.length >= o.max) { P.toast('Уже ' + o.max + ' — зніми когось'); return; } value.push(pid); }
        paint(); if (o.onChange) o.onChange(value.slice());
      });
      paint();
      return { get: () => value.slice(), set: (v) => { value = (v || []).slice(); paint(); } };
    },

    /// Екран не гасне (табло на планшеті). on=false — відпустити.
    async wake(on) {
      try {
        if (on && !P.wake.lock && navigator.wakeLock) { P.wake.lock = await navigator.wakeLock.request('screen'); P.wake.lock.addEventListener('release', () => { P.wake.lock = null; }); }
        if (!on && P.wake.lock) { await P.wake.lock.release(); P.wake.lock = null; }
      } catch { /* браузер не дав — не біда */ }
    },

    start,
  };

  let started = false;

  function renderTabs() {
    const nav = $('#pdTabs');
    nav.innerHTML = tabs.map((t) => '<a href="#' + t.id + '" data-tab="' + t.id + '" class="' + (current && current.tab === t ? 'on' : '') + '">'
      + '<span class="pd-ico">' + (t.icon || '•') + '</span><span>' + esc(t.title) + '</span>'
      + (t.badge ? '<span class="pd-badge">' + esc(t.badge) + '</span>' : '') + '</a>').join('');
  }

  function route() {
    if (!tabs.length) return;
    const h = decodeURIComponent(location.hash.replace(/^#/, ''));
    const [id, ...rest] = h.split('/');
    const tab = tabs.find((t) => t.id === id) || tabs[0];
    const arg = tab.id === id ? rest.join('/') : '';
    const main = $('#pdMain');
    if (current && current.tab !== tab && current.tab.hide) { try { current.tab.hide(); } catch (e) { console.error(e); } }
    let host = main.querySelector('.pd-view[data-tab="' + tab.id + '"]');
    for (const v of main.querySelectorAll('.pd-view')) v.hidden = v !== host;
    if (!host) {
      host = document.createElement('section'); host.className = 'pd-view'; host.dataset.tab = tab.id;
      for (const v of main.querySelectorAll('.pd-view')) v.hidden = true;
      main.appendChild(host);
      try { if (tab.mount) tab.mount(host); } catch (e) { console.error(e); host.innerHTML = '<div class="empty">Вкладка впала: ' + esc(e.message) + '</div>'; }
    }
    current = { tab, arg };
    renderTabs();
    try { if (tab.show) tab.show(host, arg); } catch (e) { console.error(e); }
    document.title = tab.title + ' · Падельня';
    toHost({ padel: 'at', hash: location.hash.replace(/^#/, '') });
  }

  function renderMe() {
    const el = $('#pdMe');
    if (P.me.account) { el.className = 'pd-me'; el.innerHTML = P.av(P.me.nick) + '<span>' + esc(P.me.nick) + '</span>'; el.href = '#me'; el.title = 'Твоя сторінка в Падельні'; }
    else { el.className = 'pd-me guest'; el.textContent = 'Увійти'; el.href = '/'; el.title = 'Вести рахунок і записуватись можуть акаунти — увійди на головній Глечиків'; }
  }

  async function checkFront() {
    let files;
    try { files = ((await (await fetch('/api/front', { credentials: 'same-origin' })).json()) || {}).files || {}; } catch { return; }
    const mine = {};
    for (const k in files) if (k.includes('padel')) mine[k] = files[k];
    const print = JSON.stringify(mine);
    if (front === null) { front = print; return; }
    // Після деплою — свіжі файли. Тут нема музики, а стан живе на сервері: перезавантаження нічого не губить.
    // Крім брелоків: їхнє Bluetooth-з'єднання обірветься — тоді чекаємо, поки їх від'єднають (tags.js).
    if (print === front) return;
    if (P.tags && P.tags.busy()) { P.stale = true; return; }
    location.reload();
  }

  function connect() {
    if (!window.signalR) return;
    conn = new signalR.HubConnectionBuilder().withUrl('/hub/padel')
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (c) => Math.min(30000, 1000 * Math.pow(2, Math.min(c.previousRetryCount, 5))) })
      .build();
    const fire = (name) => (x) => { for (const fn of handlers[name] || []) { try { fn(x); } catch (e) { console.error(e); } } };
    for (const name of ['match', 'tournament', 'gathering', 'money', 'rating', 'lobby']) conn.on(name, fire(name));
    conn.on('toast', (x) => { if (x && x.text) P.toast(x.text); fire('toast')(x); });
    conn.onreconnecting(() => { $('#pdNet').hidden = false; });
    conn.onreconnected(() => { $('#pdNet').hidden = true; checkFront(); fire('reconnected')({}); });
    // Автоперепідключення здається після кількох спроб — тоді самі, вічно (як головна сторінка)
    conn.onclose(() => { $('#pdNet').hidden = false; setTimeout(startConn, 5000); });
    startConn();
  }
  async function startConn() {
    try { await conn.start(); $('#pdNet').hidden = true; } catch { $('#pdNet').hidden = false; setTimeout(startConn, 5000); }
  }

  async function start() {
    if (started) return;
    started = true;
    try {
      const me = await (await fetch('/api/me', { credentials: 'same-origin' })).json();
      P.me = { nick: me.nick || '', account: !!me.account, admin: me.role === 'admin', pid: me.account ? 'u:' + String(me.nick || '').trim().toLowerCase() : null };
    } catch { /* без сервера — лише перегляд */ }
    renderMe();
    renderTabs();
    if (framed) {
      document.addEventListener('click', (e) => {
        const a = e.target.closest && e.target.closest('a[href]');
        if (!a || a.target || e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        const u = new URL(a.href, location.href);
        if (u.origin !== location.origin || u.pathname.startsWith('/padel/')) return;
        e.preventDefault();
        toHost({ padel: 'leave', hash: u.hash });
      });
    }
    window.addEventListener('hashchange', route);
    route();
    P.players.load();
    checkFront();
    connect();
  }
})();
