/*
  Дядько Глек у хаті (аналітика кола, етап 4; вибір користувача 25.10 — «Глек на сцені з бульбашкою»). Частина ядра clicker.js.
  Усі графіки й літопис — на окремій сторінці #gonchar (web/gonchar.js). Тут — лише Глек:
  - маленький глечик-Глек у лівому нижньому куті сцени (шар api.layer front). Є свіжі поради — на ньому крапка;
  - клік по Глеку — бульбашка з однією порадою за раз (‹ 1 / 3 ›), кнопки «Постав» (глину — одразу) / «Показати»
    (вкладка), «Потім» (дія glek — сховати на добу) і «📈 Літопис кола»; поради — GET /api/games/clicker/diary?glek=1;
  - підказка в моменті (вид glek: глина біля кола чи перед виходом, Толока) — Глек підстрибує й сам відкриває
    бульбашку з нею; за 30 с бульбашка ховається.
*/
(() => {
  if (!window.HClicker || !window.HClicker.part) return;

  const LOAD_MS = 120000;
  const MOMENT_MS = 30000;
  const SEEN_LS = 'clk.glek.seen';
  /// Куди веде кнопка поради (where з ClickerAdvisor): глину ставимо одразу, решта — вкладка.
  const WHERE_TAB = { album: 'album', village: 'guild', guests: 'guild', pier: 'guild', toloka: 'guild', shop: 'shop', mark: 'shop', fire: 'fire' };

  const myNick = (st) => (st.ctx && st.ctx.me && st.ctx.me.nick) || '';
  const goLabel = (where) => (String(where).startsWith('clay:') ? 'Постав' : 'Показати');
  const sig = (tips) => tips.map((t) => t.kind + ':' + t.text).join('|');
  const lsGet = (k) => { try { return localStorage.getItem(k) || ''; } catch { return ''; } };
  const lsSet = (k, v) => { try { localStorage.setItem(k, v); } catch { /* приватне вікно */ } };

  function load(st, force) {
    if (st.glekBusy || !st.mine || (!force && Date.now() - (st.glekAt || 0) < LOAD_MS)) return;
    st.glekBusy = true;
    st.glekAt = Date.now();
    fetch('/api/games/clicker/diary?glek=1', { headers: { 'X-Nick': encodeURIComponent(myNick(st)) } })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (!d || !d.ok) return;
        st.glekTips = Array.isArray(d.glek) ? d.glek : [];
        if (st.glekI >= st.glekTips.length) st.glekI = 0;
        paint(st, window.HClicker.api);
      })
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

  // ---------- Глек і бульбашка ----------

  function build(st, api) {
    if (st.glekBox && st.glekBox.isConnected) return;
    const host = api.layer(st, 'front', 'glek');
    host.innerHTML = '<button type="button" class="clkgl-glek" aria-label="Дядько Глек радить"><img src="/static/glek.svg" alt=""><i hidden></i></button>'
      + '<div class="clkgl-bubble" role="dialog" aria-label="Порада Дядька Глека" hidden></div>';
    st.glekBox = host;
    st.glekBtn = host.querySelector('.clkgl-glek');
    st.glekDot = st.glekBtn.querySelector('i');
    st.glekBub = host.querySelector('.clkgl-bubble');
    // Глек — не коло: натиск по ньому не крутить коло й не ловить нічого під ним.
    for (const ev of ['pointerdown', 'mousedown', 'touchstart']) {
      host.addEventListener(ev, (e) => { if (e.target.closest('.clkgl-glek, .clkgl-bubble')) e.stopPropagation(); }, { passive: true });
    }
    st.glekBtn.onclick = (e) => {
      e.stopPropagation();
      if (!st.glekBub.hidden && !st.glekMoment) { close(st); return; }
      st.glekMoment = null;
      open(st, api);
    };
  }

  function close(st) {
    if (st.glekBub) st.glekBub.hidden = true;
    st.glekMoment = null;
    clearTimeout(st.glekT);
  }

  /// Бульбашка: підказка в моменті (якщо є) або порада з карток — одна за раз.
  function open(st, api) {
    const tips = st.glekTips || [];
    const m = st.glekMoment;
    if (!m && !tips.length) {
      st.glekBub.innerHTML = '<div class="clkgl-t">Поки все добре — грай далі, я підкажу, коли буде що.</div>'
        + '<div class="clkgl-foot"><a href="#gonchar">📈 Літопис кола</a><button type="button" class="clkgl-x" aria-label="Закрити">✕</button></div>';
    } else {
      const t = m || tips[Math.min(st.glekI || 0, tips.length - 1)];
      const esc = (x) => api.esc(st, x);
      const nav = !m && tips.length > 1
        ? '<span class="clkgl-nav"><button type="button" data-step="-1" aria-label="Попередня">‹</button>' + ((st.glekI || 0) + 1) + ' / ' + tips.length
          + '<button type="button" data-step="1" aria-label="Наступна">›</button></span>' : '';
      st.glekBub.innerHTML = '<div class="clkgl-t">' + esc(t.text) + '</div>'
        + '<div class="clkgl-act"><button type="button" class="primary small" data-go>' + goLabel(t.where) + '</button>'
        + '<button type="button" class="ghost small" data-later>Потім</button>' + nav + '</div>'
        + '<div class="clkgl-foot"><a href="#gonchar">📈 Літопис кола</a><button type="button" class="clkgl-x" aria-label="Закрити">✕</button></div>';
      st.glekBub.querySelector('[data-go]').onclick = () => { go(st, api, t.where); close(st); };
      st.glekBub.querySelector('[data-later]').onclick = () => { later(st, api, t.kind); close(st); };
      for (const b of st.glekBub.querySelectorAll('[data-step]')) {
        b.onclick = () => { st.glekI = ((st.glekI || 0) + +b.dataset.step + tips.length) % tips.length; open(st, api); };
      }
    }
    st.glekBub.querySelector('.clkgl-x').onclick = () => close(st);
    st.glekBub.hidden = false;
    // Відкрив — значить бачив: крапка гасне, доки поради не зміняться.
    lsSet(SEEN_LS, sig(tips));
    if (st.glekDot) st.glekDot.hidden = true;
  }

  /// «📈 Літопис» у рядку під лічильником (поруч із «🏆 Майстер дня»): постійний вхід на сторінку #gonchar.
  function chip(st) {
    const rr = st.el && st.el.querySelector('.clk-rr');
    if (!rr) return;
    if (!st.glekChip) {
      st.glekChip = document.createElement('a');
      st.glekChip.className = 'clkgl-chip small';
      st.glekChip.href = '#gonchar';
      st.glekChip.title = 'Літопис кола: як ти граєш і що робити далі';
      st.glekChip.textContent = '📈 Літопис';
    }
    if (st.glekChip.parentElement !== rr) rr.appendChild(st.glekChip);
  }

  function paint(st, api) {
    if (!st.mine || !st.front) return;
    chip(st);
    build(st, api);
    const tips = st.glekTips || [];
    st.glekDot.hidden = !tips.length || lsGet(SEEN_LS) === sig(tips);
    if (!st.glekBub.hidden && !st.glekMoment) open(st, api);   // поради оновились, поки бульбашка відкрита
  }

  /// Підказка в моменті (вид glek): Глек підстрибує й сам каже її в бульбашці.
  function moment(st, api, g) {
    if (!g || !g.id || st.glekSeen === g.id || !st.mine || !st.front) return;
    st.glekSeen = g.id;
    build(st, api);
    st.glekMoment = g;
    open(st, api);
    st.glekBtn.classList.remove('hop');
    void st.glekBtn.offsetWidth;            // перезапуск анімації стрибка
    st.glekBtn.classList.add('hop');
    clearTimeout(st.glekT);
    st.glekT = setTimeout(() => { if (st.glekMoment === g) close(st); }, MOMENT_MS);
  }

  window.HClicker.part({
    id: 'diary',
    order: 70,

    mount(st) {
      st.glekTips = [];
      st.glekAt = 0;
      st.glekI = 0;
      st.glekBox = null;
    },

    update(st, v, api) {
      paint(st, api);
      if (v && v.glek !== undefined) moment(st, api, v.glek);
    },

    slow(st) {
      load(st, false);
    },

    unmount(st) {
      clearTimeout(st.glekT);
      if (st.glekBox) st.glekBox.innerHTML = '';
      if (st.glekChip) { st.glekChip.remove(); st.glekChip = null; }
      st.glekBox = st.glekBtn = st.glekBub = st.glekDot = null;
      st.glekTips = [];
    },
  });
})();
