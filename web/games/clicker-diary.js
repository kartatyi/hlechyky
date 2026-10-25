/*
  «📈 Літопис кола» — аналітика гончаря (етап 3, план D:/or-wt/_tools/clicker-analytics/PLAN.md). Частина ядра clicker.js.
  Дані — GET /api/games/clicker/diary (ClickerDiaryService.Wire): me, now (останній знімок щоденника: множники зі стелею,
  модель години й своя глина, що обпалено), days [{day, restored, total, formed, grabbed, slava, minutes}],
  hours [[day, hour, seconds, formed, grabbed, clicks]], round [{nick, me, days: [[day, total, formed, slava, minutes, restored]]}].
  Вантажимо, лише коли вкладку видно, і не частіше разу на хвилину. Графіки — HChart.line (web/stchart.js).
*/
(() => {
  if (!window.HClicker || !window.HClicker.part) return;

  const TAB = 'diary';
  const LOAD_MS = 60000;
  /// Вкладка — коли вже є що показати: від першої гривні за весь час (раніше криві — пара точок).
  const SHOW_FROM = 1e15;

  const WARES = {
    pot: 'Горщик', bowl: 'Миска', jug: 'Глечик', makitra: 'Макітра', dish: 'Полумисок', candle: 'Свічник',
    whistle: 'Свищик-півник', barrel: 'Барило', tile: 'Кахля', kumanets: 'Куманець', ram: 'Баранець',
    lion: 'Лев-посудина', kukhol: 'Кухоль', tykva: 'Тиква', pleskanets: 'Плесканець',
  };
  /// Складники множника «до всього» — у тому порядку, у якому про них питають: що найлегше підняти — вище.
  const MULT = [
    ['album', '📒 Альбом', 'клітинки, зірки, кахляна піч'],
    ['fair', '🎪 Села', 'шана сіл до 10 рівня'],
    ['guests', '🏛 Заморські гості', 'шана гостей до 10 рівня'],
    ['passivePerk', '🏺 Віхи «пасив +25 %»', 'віхи щаблів'],
    ['styles', '🎨 Розписи', '+5 % за кожен відкритий'],
    ['toloka', '🤝 Толока', '+5 % за будову'],
    ['house', '🏠 Хата', 'оздоби й дива'],
    ['stamps', '🔥 Клейма', 'обпал'],
  ];
  const DOW = ['пн', 'вт', 'ср', 'чт', 'пт', 'сб', 'нд'];

  /// Куди веде кнопка поради (where з ClickerAdvisor): глину ставимо одразу, решта — вкладка чи розділ.
  const WHERE_TAB = { album: 'album', village: 'guild', guests: 'guild', pier: 'guild', toloka: 'guild', shop: 'shop', mark: 'shop', fire: 'fire' };

  function go(st, api, where) {
    const [head, key] = String(where || '').split(':');
    if (head === 'clay') {
      return api.act(st, 'knead', { kind: key || '' }).then((r) => {
        if (r && r.message) api.toast(st, r.message, r.ok === false ? 'bad' : 'ok');
        load(st, true);
      });
    }
    const tab = WHERE_TAB[head];
    if (tab) api.showTab(st, tab);
    return Promise.resolve();
  }

  const goLabel = (where) => (String(where).startsWith('clay:') ? 'Постав' : 'Показати');

  function later(st, api, kind) {
    return api.act(st, 'glek', { later: kind }).then((r) => {
      if (r && r.message) api.toast(st, r.message, 'ok');
      load(st, true);
    });
  }

  /// Картка «🏺 Дядько Глек радить» угорі Літопису: поради зі знімка щоденника (вже словами).
  function glekCard(st, api, n) {
    const tips = Array.isArray(n.glek) ? n.glek : [];
    if (!tips.length) return '';
    const esc = (x) => api.esc(st, x);
    return '<section class="clkdy-card clkdy-glek"><div class="clkdy-h">🏺 Дядько Глек радить</div>'
      + tips.map((t) => '<div class="clkdy-tip"><div class="clkdy-tipt">' + esc(t.text) + '</div><div class="clkdy-tipb">'
        + '<button type="button" class="primary small" data-go="' + esc(t.where) + '">' + goLabel(t.where) + '</button>'
        + '<button type="button" class="ghost small" data-later="' + esc(t.kind) + '">Потім</button></div></div>').join('')
      + '</section>';
  }

  /// Підказка в моменті (вид glek): плашка над колом з тими самими двома кнопками; сама зникає за 40 с.
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
    const close = () => { el.remove(); if (st.glekPop === el) st.glekPop = null; window.removeEventListener('resize', fit); };
    bGo.onclick = () => { go(st, api, g.where); close(); };
    bLater.onclick = () => { later(st, api, g.kind); close(); };
    bX.onclick = close;
    st.el.appendChild(el);
    st.glekPop = el;
    // Над самою карткою кола, а не по центру вікна: праворуч бувають балачки, і кнопки ховались би під ними.
    function fit() {
      if (!el.isConnected || !st.el) return;
      const r = st.el.getBoundingClientRect();
      el.style.left = Math.max(8, r.left + 10) + 'px';
      el.style.width = Math.max(240, Math.min(r.width - 20, window.innerWidth - 16)) + 'px';
    }
    fit();
    window.addEventListener('resize', fit);
    setTimeout(close, 40000);
  }

  const myNick = (st) => (st.ctx && st.ctx.me && st.ctx.me.nick) || '';
  const dayMs = (d) => Date.parse(d + 'T12:00:00Z');
  const hueOf = (nick, i) => (window.HPeople && window.HPeople.hue ? window.HPeople.hue(nick) : (i * 67) % 360);
  const x2 = (n, d) => '×' + Number(n).toLocaleString('uk-UA', { maximumFractionDigits: d == null ? 2 : d });
  const pct = (n) => (n >= 0 ? '+' : '') + Math.round(n * 100).toLocaleString('uk-UA') + ' %';

  function load(st, force) {
    if (st.diaryBusy || (!force && Date.now() - (st.diaryAt || 0) < LOAD_MS)) return;
    st.diaryBusy = true;
    st.diaryAt = Date.now();
    fetch('/api/games/clicker/diary', { headers: { 'X-Nick': encodeURIComponent(myNick(st)) } })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => { if (d && d.ok) { st.diary = d; paint(st); } })
      .catch(() => { /* мережа — спробуємо за хвилину */ })
      .finally(() => { st.diaryBusy = false; });
  }

  // ---------- малювання ----------

  function card(title, sub, body, cls) {
    return '<section class="clkdy-card' + (cls ? ' ' + cls : '') + '"><div class="clkdy-h">' + title + '</div>'
      + (sub ? '<div class="clkdy-sub muted small">' + sub + '</div>' : '') + body + '</section>';
  }

  function tiles(st, api, d) {
    const n = d.now || {};
    const last7 = d.days.slice(-7);
    const mins = last7.reduce((a, x) => a + (x.minutes || 0), 0);
    const cut = new Date(Date.now() - 7 * 864e5).toISOString().slice(0, 10);
    let sec = 0, formed = 0, grabbed = 0;
    for (const h of d.hours) if (h[0] >= cut) { sec += h[2]; formed += h[3]; grabbed += h[4]; }
    const today = n.total && n.dayStart ? n.total - n.dayStart : 0;
    const t = (k, v, s) => '<div class="clkdy-tile"><div class="muted small">' + k + '</div><b>' + v + '</b>'
      + (s ? '<div class="muted small">' + s + '</div>' : '') + '</div>';
    return '<div class="clkdy-tiles">'
      + t('За весь час', api.short(n.total || 0))
      + t('Сьогодні', today > 0 ? '+' + api.short(today) : '—')
      + t('У колі за 7 днів', mins >= 60 ? (mins / 60).toLocaleString('uk-UA', { maximumFractionDigits: 1 }) + ' год' : mins + ' хв',
        last7.filter((x) => x.minutes > 0).length + ' днів гри')
      + t('За хвилину гри', sec >= 60 ? (formed / (sec / 60)).toLocaleString('uk-UA', { maximumFractionDigits: 1 }) + ' виробів' : '—',
        sec >= 60 ? (grabbed / (sec / 60)).toLocaleString('uk-UA', { maximumFractionDigits: 2 }) + ' глека з полиці' : 'рахується з 25.10')
      + '</div>';
  }

  /// Криві округи: col — стовпчик у days (1 заробіток, 2 вироби, 3 Слава, 4 хвилини). Нулі на початку відкидаємо.
  function series(d, col, f) {
    return d.round.map((r, i) => ({
      name: r.nick, me: r.me, h: hueOf(r.nick, i),
      pts: r.days.filter((x) => x[col] > 0).map((x) => [dayMs(x[0]), f ? f(x[col]) : x[col]]),
    })).filter((s) => s.pts.length);
  }

  function multHtml(api, n) {
    const m = n.multMax || {};
    const rows = MULT.filter(([k]) => m[k]).map(([k, name, how]) => {
      const p = m[k];
      const room = p.max > p.now * 1.0005 ? p.max / p.now : 0;
      const fill = room ? Math.max(4, Math.min(100, (Math.log(p.now) / Math.log(p.max)) * 100)) : 100;
      return '<div class="clkdy-mrow"><div class="clkdy-mname">' + name + '<span class="muted small"> · ' + how + '</span></div>'
        + '<div class="clkdy-mbar"><i style="width:' + fill.toFixed(1) + '%"></i></div>'
        + '<div class="clkdy-mval"><b>' + x2(p.now, k === 'stamps' ? 0 : 2) + '</b>'
        + (room ? '<span class="clkdy-room">' + (k === 'stamps' ? 'обпал дасть ' : 'до стелі ') + pct(room - 1) + '</span>'
          : '<span class="muted small">' + (k === 'stamps' ? 'обпал поки нічого' : 'на стелі') + '</span>') + '</div></div>';
    });
    const all = n.mult && n.mult.all;
    return rows.join('') + (all ? '<div class="muted small clkdy-foot">Разом «до всього» зараз: ' + api.short(all).replace(/ глек.*$/, '') + ' · смуга — скільки вже взято до стелі</div>' : '');
  }

  function clayHtml(n) {
    const c = n.clays || {};
    const keys = Object.keys(c);
    if (keys.length < 2) return '<div class="muted small">Порівняти нема з чим — у хаті одна глина.</div>';
    const names = { plain: 'Звичайна', red: 'Червона', white: 'Біла', black: 'Чорна', blue: 'Голуба', kaolin: 'Каолін' };
    const on = n.clay || 'plain';
    const best = (f) => Math.max(...keys.map((k) => f(c[k])));
    const bPlay = best((x) => x.play), bIdle = best((x) => x.idle), cur = c[on] || c[keys[0]];
    const minWork = Math.min(...keys.map((k) => c[k].work));
    const row = (k) => {
      const x = c[k];
      const cell = (v, b, base) => '<td><div class="clkdy-cbar"><i style="width:' + (v / b * 100).toFixed(1) + '%"></i></div>'
        + '<span>' + x2(v / base, 2) + '</span></td>';
      return '<tr' + (k === on ? ' class="on"' : '') + '><th>' + (names[k] || k) + (k === on ? ' <span class="muted small">на колі</span>' : '')
        + (k === 'plain' ? '' : '<div class="muted small">' + (x.level ? 'рівень ' + x.level : 'свіжа') + '</div>') + '</th>'
        + cell(x.play, bPlay, cur.play) + cell(x.idle, bIdle, cur.idle)
        + '<td>' + (x.work <= minWork + 1e-9 ? '<b>' : '') + x2(cur.work / x.work, 2) + (x.work <= minWork + 1e-9 ? '</b>' : '') + '</td></tr>';
    };
    return '<table class="clkdy-clay"><thead><tr><th></th><th>біля кола</th><th>без тебе</th><th>ліплення</th></tr></thead><tbody>'
      + keys.map(row).join('') + '</tbody></table>'
      + '<div class="muted small clkdy-foot">×1 — глина, що зараз на колі. Біля кола — година уважної гри (полиця, клік, Око, купці).</div>';
  }

  function heatHtml(d) {
    const grid = Array.from({ length: 7 }, () => new Array(24).fill(0));
    for (const h of d.hours) {
      const dow = (new Date(h[0] + 'T12:00:00Z').getUTCDay() + 6) % 7;
      grid[dow][h[1]] += h[2];
    }
    const max = Math.max(1, ...grid.flat());
    if (max <= 1) return '<div class="muted small">Години почали рахуватись із цього оновлення — зіграй трохи, і тут з’явиться, коли ти за колом.</div>';
    let html = '<div class="clkdy-heat"><span></span>' + Array.from({ length: 24 }, (_, i) => '<span class="hh">' + (i % 3 === 0 ? i : '') + '</span>').join('');
    grid.forEach((r, i) => {
      html += '<span class="dd">' + DOW[i] + '</span>' + r.map((s, hr) => '<i style="--a:' + (s / max).toFixed(3) + '" title="'
        + DOW[i] + ', ' + hr + ':00 — ' + Math.round(s / 60) + ' хв"></i>').join('');
    });
    return html + '</div><div class="muted small clkdy-foot">Хвилини біля кола за останні 5 тижнів, за київським часом.</div>';
  }

  function firedHtml(n) {
    const f = n.fired || {};
    const rows = Object.entries(f).sort((a, b) => b[1] - a[1]);
    const sum = rows.reduce((a, x) => a + x[1], 0) || 1, top = rows.length ? rows[0][1] : 1;
    return rows.map(([k, v]) => '<div class="clkdy-frow"><span>' + (WARES[k] || k) + '</span><div class="clkdy-cbar"><i style="width:'
      + (v / top * 100).toFixed(1) + '%"></i></div><b>' + v.toLocaleString('uk-UA') + '</b><span class="muted small">'
      + Math.round(v / sum * 100) + ' %</span></div>').join('');
  }

  function paint(st) {
    const pane = st.diaryPane, d = st.diary, api = window.HClicker.api;
    if (!pane || pane.hidden || !d) return;
    for (const c of st.diaryCharts || []) c.destroy();
    st.diaryCharts = [];
    if (!d.days.length) {
      pane.innerHTML = '<div class="muted clkdy-empty">Літопис ще порожній: перший запис з’явиться, щойно ти посидиш за колом.</div>';
      return;
    }
    const n = d.now || {};
    const restored = d.days.some((x) => x.restored);
    const roundNote = d.round.length > 1 ? 'ти й гончарі округи' : 'ти';
    pane.innerHTML = glekCard(st, api, n) + tiles(st, api, d)
      + card('Заробіток за весь час', 'шкала в степенях: кожна поділка — у рази більше · ' + roundNote, '<div class="clkdy-ch" data-ch="total"></div>')
      + card('Хвилин у колі щодня', roundNote, '<div class="clkdy-ch" data-ch="minutes"></div>')
      + '<div class="clkdy-two">'
      + card('Наліплено виробів', 'усього, руками й підмайстрами', '<div class="clkdy-ch" data-ch="formed"></div>')
      + card('Слава у світі', 'кораблі з Пристані', '<div class="clkdy-ch" data-ch="slava"></div>')
      + '</div>'
      + card('Звідки мої множники', 'множать усе — клік, полицю, пасив; «до стелі» — скільки ще можна взяти', multHtml(api, n))
      + card('Година на моїй глині', 'у скільки разів більше, ніж на глині, що зараз на колі', clayHtml(n))
      + card('Коли я граю', '', heatHtml(d))
      + card('Що я обпалюю', 'вироби з горна за весь час', firedHtml(n))
      + (restored ? '<div class="muted small clkdy-foot">Дні до 25.10 відновлено з копій бази — там точки рідші, а хвилини рахуються з 26.09.</div>' : '');
    for (const b of pane.querySelectorAll('[data-go]')) b.onclick = () => go(st, api, b.dataset.go);
    for (const b of pane.querySelectorAll('[data-later]')) b.onclick = () => later(st, api, b.dataset.later);
    if (!window.HChart) return;
    const chart = (key, s, o) => {
      const el = pane.querySelector('[data-ch="' + key + '"]');
      if (el) st.diaryCharts.push(window.HChart.line(el, Object.assign({ height: 200, max: 9 }, o, { series: s })));
    };
    chart('total', series(d, 1, (v) => Math.log10(v)), { yFmt: (v) => api.short(Math.pow(10, v)) });
    chart('minutes', series(d, 4), { zero: true, yFmt: (v) => Math.round(v) + ' хв' });
    chart('formed', series(d, 2), { zero: true, yFmt: (v) => api.short(v) });
    chart('slava', series(d, 3), { zero: true });
  }

  window.HClicker.part({
    id: 'diary',
    order: 70,

    mount(st, api) {
      st.diary = null;
      st.diaryAt = 0;
      st.diaryCharts = [];
      st.diaryPane = api.tab(st, TAB, '📈 Літопис', 55);
      st.diaryPane.classList.add('clkdy');
      st.diaryShown = false;
      api.showWhen(st, TAB, (st2, v) => (v && v.total >= SHOW_FROM) || !!st2.diary);
    },

    update(st, v, api) {
      if (v && v.glek !== undefined) moment(st, api, v.glek);
    },

    slow(st) {
      const pane = st.diaryPane;
      if (!pane) return;
      const shown = !pane.hidden;
      if (shown && !st.diaryShown) paint(st);       // відкрили вкладку — намалювати те, що вже є
      st.diaryShown = shown;
      if (shown) load(st, false);
    },

    unmount(st) {
      if (st.glekPop) { st.glekPop.remove(); st.glekPop = null; }
      for (const c of st.diaryCharts || []) c.destroy();
      st.diaryCharts = [];
      st.diary = null;
      st.diaryPane = null;
    },
  });
})();
