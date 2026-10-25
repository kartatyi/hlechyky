/*
  Поради Дядька Глека в самому Гончарному колі (аналітика кола, етап 4). Частина ядра clicker.js.
  Усі графіки й літопис — на окремій сторінці #gonchar (web/gonchar.js): у колі для них замало місця (25.10 користувач).
  Тут лишається те, що треба просто біля кола:
  - згортна картка «🏺 Глек радить» угорі «Майстерні»: поради з GET /api/games/clicker/diary?glek=1 (вже словами),
    кнопки «Постав» (глину — одразу) / «Показати» (вкладка) і «Потім» (дія glek — сховати на добу),
    а внизу — «📈 Увесь літопис →»;
  - плашка в моменті (вид glek: глина біля кола чи перед виходом, Толока) з тими самими кнопками; сама зникає за 40 с.
*/
(() => {
  if (!window.HClicker || !window.HClicker.part) return;

  const LOAD_MS = 120000;
  /// Куди веде кнопка поради (where з ClickerAdvisor): глину ставимо одразу, решта — вкладка.
  const WHERE_TAB = { album: 'album', village: 'guild', guests: 'guild', pier: 'guild', toloka: 'guild', shop: 'shop', mark: 'shop', fire: 'fire' };

  const myNick = (st) => (st.ctx && st.ctx.me && st.ctx.me.nick) || '';
  const goLabel = (where) => (String(where).startsWith('clay:') ? 'Постав' : 'Показати');

  function load(st, force) {
    if (st.glekBusy || !st.mine || (!force && Date.now() - (st.glekAt || 0) < LOAD_MS)) return;
    st.glekBusy = true;
    st.glekAt = Date.now();
    fetch('/api/games/clicker/diary?glek=1', { headers: { 'X-Nick': encodeURIComponent(myNick(st)) } })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => { if (d && d.ok) { st.glekTips = Array.isArray(d.glek) ? d.glek : []; paint(st, window.HClicker.api); } })
      .catch(() => { /* мережа — спробуємо пізніше */ })
      .finally(() => { st.glekBusy = false; });
  }

  function go(st, api, where) {
    const [head, key] = String(where || '').split(':');
    if (head === 'clay') {
      return api.act(st, 'knead', { kind: key || '' }).then((r) => {
        if (r && r.message) api.toast(st, r.message, r.ok === false ? 'bad' : 'ok');
        setTimeout(() => load(st, true), 600);
      });
    }
    const tab = WHERE_TAB[head];
    if (tab) api.showTab(st, tab);
    return Promise.resolve();
  }

  function later(st, api, kind) {
    return api.act(st, 'glek', { later: kind }).then((r) => {
      if (r && r.message) api.toast(st, r.message, 'ok');
      setTimeout(() => load(st, true), 600);
    });
  }

  /// Місце картки — угорі «Майстерні», над перемикачами ×1/×10: там вирішують, що робити далі.
  function place(st) {
    const pane = st.el && st.el.querySelector('.clk-pane[data-pane="shop"]');
    if (!pane || !st.glekEl) return;
    if (pane.firstElementChild !== st.glekEl) pane.prepend(st.glekEl);
  }

  function paint(st, api) {
    const tips = st.glekTips || [];
    if (!tips.length || !st.mine) { if (st.glekEl) st.glekEl.hidden = true; return; }
    if (!st.glekEl) {
      st.glekEl = document.createElement('section');
      st.glekEl.className = 'clkdy-card';
      st.glekEl.dataset.fold = 'glek.tips';
    }
    st.glekEl.hidden = false;
    place(st);
    const esc = (x) => api.esc(st, x);
    const html = api.foldHead('🏺 Дядько Глек радить <span class="muted small">· ' + tips.length + '</span>')
      + '<div class="clkdy-body">' + tips.map((t) => '<div class="clkdy-tip"><div class="clkdy-tipt">' + esc(t.text) + '</div><div class="clkdy-tipb">'
        + '<button type="button" class="primary small" data-go="' + esc(t.where) + '">' + goLabel(t.where) + '</button>'
        + '<button type="button" class="ghost small" data-later="' + esc(t.kind) + '">Потім</button></div></div>').join('')
      + '<a class="clkdy-all" href="#gonchar">📈 Увесь літопис кола →</a></div>';
    if (api.swap(st.glekEl, html)) {
      for (const b of st.glekEl.querySelectorAll('[data-go]')) b.onclick = () => go(st, api, b.dataset.go);
      for (const b of st.glekEl.querySelectorAll('[data-later]')) b.onclick = () => later(st, api, b.dataset.later);
    }
  }

  /// Підказка в моменті (вид glek): плашка над карткою кола з тими самими кнопками; сама зникає за 40 с.
  function moment(st, api, g) {
    if (!g || !g.id || st.glekSeen === g.id || !st.el) return;
    st.glekSeen = g.id;
    if (st.glekPop) st.glekPop.remove();
    const el = document.createElement('div');
    el.className = 'clkdy-pop';
    el.setAttribute('role', 'status');
    el.innerHTML = '<span class="clkdy-popi">🏺</span><div class="clkdy-popt">' + api.esc(st, g.text) + '</div><div class="clkdy-tipb">'
      + '<button type="button" class="primary small">' + goLabel(g.where) + '</button>'
      + '<button type="button" class="ghost small">Потім</button><button type="button" class="ghost small" aria-label="Закрити">✕</button></div>';
    const [bGo, bLater, bX] = el.querySelectorAll('button');
    // Над самою карткою кола, а не по центру вікна: праворуч бувають балачки, і кнопки ховались би під ними.
    function fit() {
      if (!el.isConnected || !st.el) return;
      const r = st.el.getBoundingClientRect();
      el.style.left = Math.max(8, r.left + 10) + 'px';
      el.style.width = Math.max(240, Math.min(r.width - 20, window.innerWidth - 16)) + 'px';
    }
    const close = () => { el.remove(); if (st.glekPop === el) st.glekPop = null; window.removeEventListener('resize', fit); };
    bGo.onclick = () => { go(st, api, g.where); close(); };
    bLater.onclick = () => { later(st, api, g.kind); close(); };
    bX.onclick = close;
    st.el.appendChild(el);
    st.glekPop = el;
    fit();
    window.addEventListener('resize', fit);
    setTimeout(close, 40000);
  }

  window.HClicker.part({
    id: 'diary',
    order: 70,

    mount(st) {
      st.glekTips = [];
      st.glekAt = 0;
      st.glekEl = null;
    },

    update(st, v, api) {
      if (v && v.glek !== undefined) moment(st, api, v.glek);
      place(st);
    },

    slow(st) {
      load(st, false);
    },

    unmount(st) {
      if (st.glekPop) { st.glekPop.remove(); st.glekPop = null; }
      if (st.glekEl) { st.glekEl.remove(); st.glekEl = null; }
      st.glekTips = [];
    },
  });
})();
