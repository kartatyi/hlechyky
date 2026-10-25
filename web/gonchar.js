/*
  «📈 Літопис кола» — window.HGonchar, сторінка #gonchar (аналітика Гончарного кола; план D:/or-wt/_tools/clicker-analytics/PLAN.md).
  У самому колі для графіків замало місця (25.10 користувач: «в самій грі виходить замало місця») — тож тут уся ширина:
  як я ріс поруч із гончарями округи, хвилини біля кола, вироби, Слава, «Майстер дня», звідки множники й скільки до
  стелі, глина, коли я граю, що обпалюю, ачівки. Угорі — поради Дядька Глека.

  Дані — GET /api/games/clicker/diary (ClickerDiaryService.Wire): me, now (останній знімок щоденника), days, hours,
  round [{nick, me, days: [[day, total, formed, slava, minutes, restored]]}], master {нік: [[day, points]]},
  ach {нік: {ключ: коли}}, achNames {ключ: [назва, опис, значок]}.

  Графіки — власні SVG без бібліотек: лінія 2 px, точки з кільцем кольору тла, підписи в кінці ліній без накладань,
  хрестик і підказка з усіма значеннями; стовпчики до 24 px із заокругленим верхом. Кольори — перевірена категоріальна
  палітра (темні кроки), «я» — акцентний жовтий сайту; колір іде за гончарем, а не за місцем у списку.
*/
(() => {
  'use strict';

  const NS = 'http://www.w3.org/2000/svg';
  /// Категоріальна палітра (темні кроки; зелений вилучено — зливається з тлом сайту). «Я» — var(--accent).
  const PALETTE = ['#3987e5', '#d95926', '#199e70', '#d55181', '#9085e9', '#e66767'];
  const ME_COLOR = 'var(--accent)';
  /// Глина — не гончарі: свої два кольори, щоб не плутати з лініями округи (акцент і приглушений).
  const CLAY_PLAY = '#f4c542', CLAY_IDLE = '#7f9c8c';
  const PICK_LS = 'gonchar.with';
  const WARES = {
    pot: 'Горщик', bowl: 'Миска', jug: 'Глечик', makitra: 'Макітра', dish: 'Полумисок', candle: 'Свічник',
    whistle: 'Свищик-півник', barrel: 'Барило', tile: 'Кахля', kumanets: 'Куманець', ram: 'Баранець',
    lion: 'Лев-посудина', kukhol: 'Кухоль', tykva: 'Тиква', pleskanets: 'Плесканець',
  };
  const CLAYS = { plain: 'Звичайна', red: 'Червона', white: 'Біла', black: 'Чорна', blue: 'Голуба', kaolin: 'Каолін' };
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
  const MONTHS = ['січ.', 'лют.', 'бер.', 'квіт.', 'трав.', 'черв.', 'лип.', 'серп.', 'вер.', 'жовт.', 'лист.', 'груд.'];

  let o = null;            // { esc, go, api, me } з app.js
  let data = null;
  let busy = false;
  let loadedAt = 0;
  let tip = null;
  let resizeT = 0;

  // ---------- числа ----------

  const nf = (v, d) => Number(v).toLocaleString('uk-UA', { maximumFractionDigits: d == null ? 0 : d });
  function plural(n, one, few, many) {
    if (n % 1 !== 0) return few;
    const a = Math.abs(n) % 100, b = a % 10;
    if (a >= 11 && a <= 14) return many;
    return b === 1 ? one : b >= 2 && b <= 4 ? few : many;
  }
  /// Глеки так, як каже гра: глеки → гривні → золоті → дукачі, далі тис./млн/млрд/трлн.
  function pots(v) {
    if (!(v > 0)) return '0';
    const units = [[1e39, 'дукач', 'дукачі', 'дукачів'], [1e27, 'золотий', 'золоті', 'золотих'], [1e15, 'гривня', 'гривні', 'гривень'], [1, 'глек', 'глеки', 'глеків']];
    for (const [u, one, few, many] of units) {
      if (v < u) continue;
      const x = v / u;
      for (const [s, w] of [[1e12, 'трлн'], [1e9, 'млрд'], [1e6, 'млн'], [1e3, 'тис.']]) if (x >= s) return nf(x / s, 1) + ' ' + w + ' ' + many;
      const r = Math.round(x * 10) / 10;
      return nf(r, x < 10 ? 1 : 0) + ' ' + plural(r, one, few, many);
    }
    return nf(v);
  }
  /// Коротко для осі: «10 млрд зол.», «10 тис. дук.» — щоб підписи влазили й на телефоні.
  function potsAxis(v) {
    if (!(v > 0)) return '0';
    for (const [u, w] of [[1e39, 'дук.'], [1e27, 'зол.'], [1e15, 'грн'], [1, 'гл.']]) {
      if (v < u) continue;
      const x = v / u;
      for (const [s, k] of [[1e12, 'трлн'], [1e9, 'млрд'], [1e6, 'млн'], [1e3, 'тис.']]) if (x >= s) return nf(x / s) + ' ' + k + ' ' + w;
      return nf(x) + ' ' + w;
    }
    return nf(v);
  }
  const dayMs = (d) => Date.parse(d + 'T12:00:00Z');
  const dayLabel = (ms) => { const d = new Date(ms); return d.getUTCDate() + ' ' + MONTHS[d.getUTCMonth()]; };
  const dayLong = (d) => new Date(dayMs(d)).toLocaleDateString('uk-UA', { weekday: 'short', day: 'numeric', month: 'long', timeZone: 'UTC' });

  // ---------- підказка ----------

  function showTip(ev, title, rows) {
    if (!tip) { tip = document.createElement('div'); tip.className = 'gnr-tip'; document.body.appendChild(tip); }
    tip.innerHTML = '<div class="t">' + o.esc(title) + '</div>' + rows.map((r) => '<div class="r"><span><i style="background:' + r.color + '"></i>'
      + o.esc(r.name) + (r.sub ? ' <small>' + o.esc(r.sub) + '</small>' : '') + '</span><b>' + o.esc(r.value) + '</b></div>').join('');
    tip.style.display = 'block';
    const x = ev.clientX + 14, y = ev.clientY + 14, w = tip.offsetWidth, h = tip.offsetHeight;
    tip.style.left = Math.max(8, Math.min(x, innerWidth - w - 8)) + 'px';
    tip.style.top = (y + h > innerHeight - 8 ? ev.clientY - h - 14 : y) + 'px';
  }
  const hideTip = () => { if (tip) tip.style.display = 'none'; };

  const el = (tag, attrs, parent) => {
    const e = document.createElementNS(NS, tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(e);
    return e;
  };
  const cssColor = (c) => (c.startsWith('var(') ? getComputedStyle(document.documentElement).getPropertyValue(c.slice(4, -1)).trim() || '#f4c542' : c);

  // ---------- лінії ----------

  /// series: [{name, color, me, pts: [[ms, y]], dim?: [bool]}]; opt: {log, h, yfmt, tfmt, zero}
  function lineChart(host, series, opt) {
    host.innerHTML = '';
    series = series.filter((s) => s.pts.length);
    if (!series.length) { host.innerHTML = '<div class="gnr-empty">Поки нема з чого малювати.</div>'; return; }
    const W = Math.max(280, host.clientWidth || 600), H = opt.h || 300, narrow = W < 520;
    const m = { l: narrow ? 78 : 96, r: narrow ? 66 : 96, t: 14, b: 30 };
    const svg = el('svg', { viewBox: `0 0 ${W} ${H}`, width: W, height: H, role: 'img', 'aria-label': opt.label || '' }, host);
    const all = series.flatMap((s) => s.pts);
    const x0 = Math.min(...all.map((p) => p[0])), x1 = Math.max(...all.map((p) => p[0]));
    let ticks, Y;
    if (opt.log) {
      const lo = Math.floor(Math.log10(Math.min(...all.map((p) => p[1])))), hi = Math.ceil(Math.log10(Math.max(...all.map((p) => p[1]))));
      const step = Math.max(1, Math.ceil((hi - lo) / 6));
      ticks = []; for (let e = lo; e <= hi; e += step) ticks.push(Math.pow(10, e));
      const top = Math.log10(ticks[ticks.length - 1]) >= hi ? hi : hi + step;
      Y = (v) => m.t + (H - m.t - m.b) * (1 - (Math.log10(v) - lo) / Math.max(1, top - lo));
    } else {
      const max = Math.max(1, ...all.map((p) => p[1])) * 1.08;
      const raw = max / 4, mag = Math.pow(10, Math.floor(Math.log10(raw))), st = [1, 2, 2.5, 5, 10].map((k) => k * mag).find((k) => k >= raw);
      ticks = []; for (let v = 0; v <= max; v += st) ticks.push(v);
      Y = (v) => m.t + (H - m.t - m.b) * (1 - v / max);
    }
    const X = (t) => m.l + (W - m.l - m.r) * (t - x0) / Math.max(1, x1 - x0);
    for (const v of ticks) {
      const y = Y(v);
      if (y < m.t - 1) continue;
      el('line', { x1: m.l, x2: W - m.r, y1: y, y2: y, class: 'g' }, svg);
      el('text', { x: m.l - 8, y: y + 4, 'text-anchor': 'end', class: 'ax' }, svg).textContent = opt.yfmt(v);
    }
    const days = Math.max(1, (x1 - x0) / 864e5), pxDay = (W - m.l - m.r) / days;
    const every = [1, 2, 7, 14, 28].find((k) => k * pxDay >= 64) || 28;
    for (let t = dayMs(new Date(x0).toISOString().slice(0, 10)), i = 0; t <= x1; t += 864e5, i++) {
      if (i % every) continue;
      el('text', { x: X(t), y: H - 9, 'text-anchor': 'middle', class: 'ax' }, svg).textContent = dayLabel(t);
    }
    const surface = cssColor('var(--panel)');
    // Лінії: спершу чужі, «я» — зверху й товщою.
    for (const s of [...series].sort((a, b) => (a.me ? 1 : 0) - (b.me ? 1 : 0))) {
      const d = s.pts.map((p, i) => (i ? 'L' : 'M') + X(p[0]).toFixed(1) + ' ' + Y(p[1]).toFixed(1)).join(' ');
      el('path', { d, fill: 'none', stroke: s.color, 'stroke-width': s.me ? 3 : 2, 'stroke-linejoin': 'round', 'stroke-linecap': 'round', opacity: s.me ? 1 : .9 }, svg);
      if (s.pts.length < 40) for (const p of s.pts) el('circle', { cx: X(p[0]), cy: Y(p[1]), r: s.me ? 3.5 : 2.5, fill: s.color, stroke: surface, 'stroke-width': 1.5 }, svg);
      const last = s.pts[s.pts.length - 1];
      s._x = X(last[0]); s._y = Y(last[1]); s._ly = s._y;
    }
    const ends = [...series].sort((a, b) => a._y - b._y);
    for (let i = 1; i < ends.length; i++) if (ends[i]._ly - ends[i - 1]._ly < 15) ends[i]._ly = ends[i - 1]._ly + 15;
    for (const s of ends) {
      el('circle', { cx: s._x, cy: s._y, r: 4.5, fill: s.color, stroke: surface, 'stroke-width': 2 }, svg);
      const t = el('text', { x: W - m.r + 8, y: s._ly + 4, class: 'lb' + (s.me ? ' me' : '') }, svg);
      t.textContent = s.name.length > 12 ? s.name.slice(0, 11) + '…' : s.name;
      t.setAttribute('fill', s.color);
    }
    const xh = el('line', { y1: m.t, y2: H - m.b, class: 'xh', visibility: 'hidden' }, svg);
    const dots = series.map((s) => el('circle', { r: 5, fill: s.color, stroke: surface, 'stroke-width': 2, visibility: 'hidden' }, svg));
    const hit = el('rect', { x: m.l, y: 0, width: Math.max(1, W - m.l - m.r), height: H, fill: 'transparent' }, svg);
    const move = (ev) => {
      const r = svg.getBoundingClientRect(), sx = (ev.clientX - r.left) * W / r.width;
      const t = x0 + (sx - m.l) / Math.max(1, W - m.l - m.r) * (x1 - x0);
      const rows = [];
      series.forEach((s, i) => {
        let p = null; for (const q of s.pts) { if (q[0] <= t + 432e5) p = q; else break; }
        if (!p) { dots[i].setAttribute('visibility', 'hidden'); return; }
        dots[i].setAttribute('cx', X(p[0])); dots[i].setAttribute('cy', Y(p[1])); dots[i].setAttribute('visibility', 'visible');
        rows.push({ name: s.name, color: s.color, value: opt.tfmt(p[1]), sub: dayLabel(p[0]), v: p[1] });
      });
      rows.sort((a, b) => b.v - a.v);
      xh.setAttribute('x1', sx); xh.setAttribute('x2', sx); xh.setAttribute('visibility', 'visible');
      showTip(ev, dayLabel(t), rows);
    };
    hit.addEventListener('mousemove', move);
    hit.addEventListener('touchstart', (e) => move(e.touches[0]), { passive: true });
    hit.addEventListener('mouseleave', () => { hideTip(); xh.setAttribute('visibility', 'hidden'); dots.forEach((d) => d.setAttribute('visibility', 'hidden')); });
  }

  // ---------- стовпчики ----------

  /// cats: підписи; series: [{name, color, vals}]; opt: {h, cfmt(c, full), vfmt}
  function barChart(host, cats, series, opt) {
    host.innerHTML = '';
    const W = Math.max(280, host.clientWidth || 600), H = opt.h || 240;
    const m = { l: 44, r: 10, t: 12, b: 30 };
    const svg = el('svg', { viewBox: `0 0 ${W} ${H}`, width: W, height: H, role: 'img', 'aria-label': opt.label || '' }, host);
    const max = Math.max(1, ...series.flatMap((s) => s.vals)) * 1.08;
    const raw = max / 4, mag = Math.pow(10, Math.floor(Math.log10(raw))), st = [1, 2, 2.5, 5, 10].map((k) => k * mag).find((k) => k >= raw);
    const Y = (v) => m.t + (H - m.t - m.b) * (1 - v / max);
    for (let v = 0; v <= max; v += st) {
      el('line', { x1: m.l, x2: W - m.r, y1: Y(v), y2: Y(v), class: 'g' }, svg);
      el('text', { x: m.l - 6, y: Y(v) + 4, 'text-anchor': 'end', class: 'ax' }, svg).textContent = opt.yfmt ? opt.yfmt(v) : nf(v, v < 10 ? 1 : 0);
    }
    const band = (W - m.l - m.r) / cats.length, gap = 2, n = series.length;
    const bw = Math.max(2, Math.min(24, (band * 0.78 - gap * (n - 1)) / n));
    const every = Math.ceil(cats.length / Math.max(1, Math.floor((W - m.l) / 56)));
    cats.forEach((c, i) => {
      const gx = m.l + band * i + (band - (bw * n + gap * (n - 1))) / 2;
      series.forEach((s, j) => {
        const v = s.vals[i]; if (!(v > 0)) return;
        const x = gx + j * (bw + gap), y = Y(v), h = Y(0) - y, r = Math.min(4, bw / 2, h);
        el('path', { d: `M${x} ${Y(0)} V${y + r} Q${x} ${y} ${x + r} ${y} H${x + bw - r} Q${x + bw} ${y} ${x + bw} ${y + r} V${Y(0)} Z`, fill: s.color }, svg);
      });
      if (i % every === 0) el('text', { x: m.l + band * i + band / 2, y: H - 9, 'text-anchor': 'middle', class: 'ax' }, svg).textContent = opt.cfmt(c, false);
      const hit = el('rect', { x: m.l + band * i, y: m.t, width: band, height: H - m.t - m.b, fill: 'transparent' }, svg);
      const move = (ev) => showTip(ev, opt.cfmt(c, true), series.map((s) => ({ name: s.name, color: s.color, value: opt.vfmt(s.vals[i]) })));
      hit.addEventListener('mousemove', move);
      hit.addEventListener('touchstart', (e) => move(e.touches[0]), { passive: true });
      hit.addEventListener('mouseleave', hideTip);
    });
  }

  // ---------- хто на графіках ----------

  /// Колір за гончарем: «я» — акцент, решта — палітра за порядком у round (ранг за заробітком), не за вибором.
  function people() {
    let k = 0;
    return data.round.map((r) => ({ nick: r.nick, me: r.me, color: r.me ? ME_COLOR : PALETTE[k++ % PALETTE.length], days: r.days }));
  }

  /// З ким порівнювати: збережений вибір; нема — двоє найближчих за заробітком.
  function picked(ps) {
    let saved = null;
    try { saved = JSON.parse(localStorage.getItem(PICK_LS) || 'null'); } catch { /* приватне вікно */ }
    const others = ps.filter((p) => !p.me);
    if (Array.isArray(saved)) return new Set(saved.filter((n) => others.some((p) => p.nick === n)));
    const me = ps.find((p) => p.me);
    const myTotal = me && me.days.length ? me.days[me.days.length - 1][1] : 0;
    const near = [...others].sort((a, b) => Math.abs(Math.log10((a.days.at(-1) || [0, 1])[1] || 1) - Math.log10(myTotal || 1))
      - Math.abs(Math.log10((b.days.at(-1) || [0, 1])[1] || 1) - Math.log10(myTotal || 1))).slice(0, 2);
    return new Set(near.map((p) => p.nick));
  }

  function savePick(set) { try { localStorage.setItem(PICK_LS, JSON.stringify([...set])); } catch { /* не біда */ } }

  // ---------- сторінка ----------

  function panel(title, sub, body, cls) {
    return '<section class="panel gnr-card' + (cls ? ' ' + cls : '') + '"><div class="gnr-h"><h2>' + title + '</h2>'
      + (sub ? '<span class="muted">' + sub + '</span>' : '') + '</div>' + body + '</section>';
  }

  function headHtml(n, d) {
    const esc = o.esc;
    const today = n.total && n.dayStart ? n.total - n.dayStart : 0;
    const days = d.days.filter((x) => x.minutes > 0 || !x.restored).length;
    const mins7 = d.days.slice(-7).reduce((a, x) => a + (x.minutes || 0), 0);
    let sec = 0, formed = 0, grabbed = 0;
    const cut = new Date(Date.now() - 7 * 864e5).toISOString().slice(0, 10);
    for (const h of d.hours) if (h[0] >= cut) { sec += h[2]; formed += h[3]; grabbed += h[4]; }
    const chip = (v, k) => '<span><b>' + v + '</b> ' + k + '</span>';
    return '<section class="panel gnr-head"><div class="gnr-kick">📈 Літопис кола</div>'
      + '<p>Гончарний шлях <b>' + esc(d.me) + '</b>: як ти ріс поруч з округою, де беруться гроші й що ще можна взяти. '
      + 'Дані щоденника кола; дні до 25.10 я, Дядько Глек, відновив зі старих записів.</p>'
      + '<div class="gnr-stats">'
      + chip(esc(pots(n.total || 0)), 'за весь час')
      + (today > 0 ? chip('+' + esc(pots(today)), 'сьогодні') : '')
      + chip(nf(mins7 / 60, 1) + ' год', 'біля кола за 7 днів')
      + (sec >= 60 ? chip(nf(formed / (sec / 60), 1), 'виробів за хвилину гри') : '')
      + (sec >= 60 && grabbed > 0 ? chip(nf(grabbed / (sec / 60), 2), 'глека з полиці за хвилину') : '')
      + chip(days, plural(days, 'день', 'дні', 'днів') + ' у літописі')
      + '</div><a class="gnr-go" href="#games">🏺 До кола →</a></section>';
  }

  function glekHtml(n) {
    const tips = Array.isArray(n.glek) ? n.glek : [];
    if (!tips.length) return '';
    return '<section class="panel gnr-glek"><div class="gnr-glek-head"><span class="gnr-ava">🏺</span><div><h2>Дядько Глек радить</h2>'
      + '<span class="muted">натиснути «Постав» чи «Потім» можна біля кола — там ці поради теж є</span></div></div>'
      + '<ol>' + tips.map((t) => '<li>' + o.esc(t.text) + '</li>').join('') + '</ol></section>';
  }

  function pickHtml(ps, sel) {
    return '<div class="gnr-pick"><span class="muted">Поруч зі мною:</span>'
      + ps.filter((p) => !p.me).map((p) => '<button type="button" class="gnr-chip' + (sel.has(p.nick) ? ' on' : '') + '" data-nick="'
        + o.esc(p.nick) + '" style="--c:' + p.color + '"><i></i>' + o.esc(p.nick) + '</button>').join('')
      + '</div>';
  }

  function multHtml(n) {
    const m = n.multMax || {};
    const rows = MULT.filter(([k]) => m[k]).map(([k, name, how]) => {
      const p = m[k];
      const room = p.max > p.now * 1.0005 ? p.max / p.now : 0;
      const fill = room ? Math.max(4, Math.min(100, Math.log(p.now) / Math.log(p.max) * 100)) : 100;
      const val = k === 'stamps' ? '×' + nf(p.now) : '×' + nf(p.now, 2);
      const tag = room ? '<em>' + (k === 'stamps' ? 'обпал дасть' : 'до стелі') + ' +' + nf((room - 1) * 100) + ' %</em>'
        : '<span class="muted">' + (k === 'stamps' ? 'обпал поки нічого' : 'на стелі') + '</span>';
      return '<div class="gnr-mrow"><div class="nm">' + name + '<small>' + how + '</small></div>'
        + '<div class="gnr-bar"><i style="width:' + fill.toFixed(1) + '%"></i></div><div class="v"><b>' + val + '</b>' + tag + '</div></div>';
    });
    return rows.join('') + (n.mult && n.mult.all ? '<p class="gnr-note">Разом «до всього» зараз — ×' + o.esc(pots(n.mult.all).replace(/ глек.*$/, ''))
      + '. Смуга — скільки з можливого вже взято; «до стелі» — на скільки виросте все, якщо дотягнути.</p>' : '');
  }

  function heatHtml(d) {
    const grid = Array.from({ length: 7 }, () => new Array(24).fill(0));
    for (const h of d.hours) grid[(new Date(h[0] + 'T12:00:00Z').getUTCDay() + 6) % 7][h[1]] += h[2];
    const max = Math.max(...grid.flat());
    if (!(max > 0)) return '<div class="gnr-empty">Години біля кола гра почала рахувати з цього оновлення — зазирни за кілька днів.</div>';
    let html = '<div class="gnr-heat"><span></span>' + Array.from({ length: 24 }, (_, i) => '<span class="hh">' + (i % 3 === 0 ? i : '') + '</span>').join('');
    grid.forEach((r, i) => {
      html += '<span class="dd">' + DOW[i] + '</span>' + r.map((s, hr) => '<i style="--a:' + (s / max).toFixed(3) + '" title="' + DOW[i] + ', '
        + hr + ':00 — ' + Math.round(s / 60) + ' хв"></i>').join('');
    });
    return html + '</div><p class="gnr-note">Що яскравіше клітинка — то довше ти сидів біля кола в цю годину.</p>';
  }

  function firedHtml(n) {
    const rows = Object.entries(n.fired || {}).sort((a, b) => b[1] - a[1]);
    if (!rows.length) return '<div class="gnr-empty">Ще нічого не обпалено.</div>';
    const sum = rows.reduce((a, x) => a + x[1], 0), top = rows[0][1];
    return '<div class="gnr-bars">' + rows.map(([k, v]) => '<div class="gnr-brow"><span>' + (WARES[k] || k) + '</span><div class="gnr-bar"><i style="width:'
      + (v / top * 100).toFixed(1) + '%"></i></div><b>' + nf(v) + '</b><small>' + Math.round(v / sum * 100) + ' %</small></div>').join('') + '</div>';
  }

  function achHtml(ps, sel) {
    const show = ps.filter((p) => p.me || sel.has(p.nick));
    const names = data.achNames || {};
    const keys = Object.keys(names).filter((k) => show.some((p) => data.ach[p.nick] && data.ach[p.nick][k]));
    if (!keys.length) return '<div class="gnr-empty">Ачівок кола ще нема.</div>';
    const first = (k) => Math.min(...show.map((p) => (data.ach[p.nick] && data.ach[p.nick][k] ? Date.parse(data.ach[p.nick][k]) : Infinity)));
    keys.sort((a, b) => first(a) - first(b));
    const fmt = (s) => { const d = new Date(Date.parse(s)); return d.getDate() + ' ' + MONTHS[d.getMonth()]; };
    return '<div class="gnr-tablewrap"><table class="gnr-ach"><thead><tr><th>Ачівка</th>' + show.map((p) => '<th><i style="background:' + p.color + '"></i>'
      + o.esc(p.nick) + '</th>').join('') + '</tr></thead><tbody>' + keys.map((k) => {
      const n = names[k], f = first(k);
      return '<tr><td><span class="ic">' + n[2] + '</span><b>' + o.esc(n[0]) + '</b><small>' + o.esc(n[1]) + '</small></td>' + show.map((p) => {
        const at = data.ach[p.nick] && data.ach[p.nick][k];
        return at ? '<td>' + fmt(at) + (Date.parse(at) === f && show.length > 1 ? ' <span class="star" title="першим">★</span>' : '') + '</td>' : '<td class="muted">—</td>';
      }).join('') + '</tr>';
    }).join('') + '</tbody></table></div>';
  }

  function render() {
    const root = document.getElementById('gonchar');
    if (!root) return;
    hideTip();
    if (!data) { root.innerHTML = '<section class="panel gnr-head"><div class="gnr-kick">📈 Літопис кола</div><p class="muted">Гортаю літопис…</p></section>'; return; }
    if (!data.days.length) {
      root.innerHTML = '<section class="panel gnr-head"><div class="gnr-kick">📈 Літопис кола</div><p>Літопис ще порожній: перший запис з’явиться, '
        + 'щойно ти посидиш за Гончарним колом.</p><a class="gnr-go" href="#games">🏺 До кола →</a></section>';
      return;
    }
    const n = data.now || {};
    const ps = people();
    const sel = picked(ps);
    const shown = ps.filter((p) => p.me || sel.has(p.nick));
    root.innerHTML = headHtml(n, data) + glekHtml(n)
      + '<section class="panel gnr-card"><div class="gnr-h"><h2>Як я ріс</h2><span class="muted">заробіток за весь час, шкала в степенях</span></div>'
      + pickHtml(ps, sel) + '<div class="gnr-ch" data-ch="total"></div></section>'
      + panel('Хвилин біля кола', 'останні 30 днів', '<div class="gnr-ch" data-ch="minutes"></div>')
      + '<div class="gnr-two">'
      + panel('Наліплено виробів', 'усього, руками й підмайстрами', '<div class="gnr-ch" data-ch="formed"></div>')
      + panel('Слава у світі', 'кораблі з Пристані', '<div class="gnr-ch" data-ch="slava"></div>')
      + '</div>'
      + panel('Майстер дня', 'очки за дзвінкі й розкішні з ручного горна', '<div class="gnr-ch" data-ch="master"></div>')
      + '<div class="gnr-two">'
      + panel('Звідки мої множники', 'множать усе — клік, полицю, пасив', multHtml(n))
      + panel('Година на моїй глині', 'у скільки разів більше, ніж на глині, що зараз на колі', '<div class="gnr-ch" data-ch="clay"></div>')
      + '</div>'
      + panel('Коли я граю', 'хвилини біля кола за останні 5 тижнів, за київським часом', heatHtml(data))
      + panel('Що я обпалюю', 'вироби з горна за весь час', firedHtml(n))
      + panel('Ачівки кола', 'хто першим — ★', achHtml(ps, sel));
    for (const b of root.querySelectorAll('.gnr-chip')) {
      b.onclick = () => {
        const s = picked(ps);
        if (s.has(b.dataset.nick)) s.delete(b.dataset.nick); else s.add(b.dataset.nick);
        savePick(s);
        render();
      };
    }
    charts(root, shown, n);
  }

  function charts(root, shown, n) {
    const at = (k) => root.querySelector('[data-ch="' + k + '"]');
    const line = (col, f) => shown.map((p) => ({ name: p.nick, color: p.color, me: p.me,
      pts: p.days.filter((x) => x[col] > 0).map((x) => [dayMs(x[0]), f ? f(x[col]) : x[col]]) }));
    lineChart(at('total'), line(1), { log: true, h: 340, yfmt: potsAxis, tfmt: pots, label: 'Заробіток за весь час' });
    // Хвилини — стовпчиками за останні 30 днів.
    const days = [];
    for (let i = 29; i >= 0; i--) days.push(new Date(Date.now() - i * 864e5 + 3 * 36e5).toISOString().slice(0, 10));
    const mins = data.minutes || {};
    barChart(at('minutes'), days, shown.map((p) => {
      const by = Object.fromEntries((mins[p.nick] || []).map((x) => [x[0], x[1]]));
      return { name: p.nick, color: p.color, vals: days.map((d) => by[d] || 0) };
    }), { h: 240, cfmt: (d, full) => (full ? dayLong(d) : dayLabel(dayMs(d))), vfmt: (v) => (v ? nf(v) + ' хв' : '—'), yfmt: (v) => nf(v), label: 'Хвилин біля кола' });
    lineChart(at('formed'), line(2), { h: 240, yfmt: (v) => (v >= 1000 ? nf(v / 1000) + ' тис.' : nf(v)), tfmt: (v) => nf(v), label: 'Наліплено виробів' });
    lineChart(at('slava'), line(3), { h: 240, yfmt: (v) => nf(v), tfmt: (v) => nf(v), label: 'Слава у світі' });
    const mdays = [];
    for (let i = 20; i >= 0; i--) mdays.push(new Date(Date.now() - i * 864e5 + 3 * 36e5).toISOString().slice(0, 10));
    const master = data.master || {};
    const ms = shown.map((p) => {
      const by = Object.fromEntries((master[p.nick] || []).map((x) => [x[0], x[1]]));
      return { name: p.nick, color: p.color, vals: mdays.map((d) => by[d] || 0) };
    });
    const first = mdays.findIndex((d, i) => ms.some((s) => s.vals[i] > 0));
    if (first < 0) at('master').innerHTML = '<div class="gnr-empty">Ще ніхто з вибраних не грав у «Майстра дня».</div>';
    else barChart(at('master'), mdays.slice(first), ms.map((s) => ({ ...s, vals: s.vals.slice(first) })),
      { h: 220, cfmt: (d, full) => (full ? dayLong(d) : dayLabel(dayMs(d))), vfmt: (v) => (v ? nf(v) + ' ' + plural(v, 'очко', 'очки', 'очок') : '—'), label: 'Майстер дня' });
    // Глина: дві ролі (біля кола, без тебе) — проти тієї, що зараз на колі.
    const c = n.clays || {}, keys = Object.keys(c), on = n.clay || 'plain', cur = c[on] || c[keys[0]];
    if (keys.length < 2 || !cur) at('clay').innerHTML = '<div class="gnr-empty">Порівняти нема з чим — у хаті одна глина.</div>';
    else {
      barChart(at('clay'), keys, [
        { name: 'біля кола', color: CLAY_PLAY, vals: keys.map((k) => c[k].play / cur.play) },
        { name: 'без тебе', color: CLAY_IDLE, vals: keys.map((k) => c[k].idle / cur.idle) },
      ], { h: 240, cfmt: (k, full) => (CLAYS[k] || k) + (full && k === on ? ' — на колі' : '') + (full && c[k].level ? ' · рівень ' + c[k].level : ''),
        vfmt: (v) => '×' + nf(v, 2), yfmt: (v) => '×' + nf(v, 1), label: 'Година на глині' });
      at('clay').insertAdjacentHTML('beforeend', '<div class="gnr-legend"><span><i style="background:' + CLAY_PLAY + '"></i>біля кола (година уважної гри)</span>'
        + '<span><i style="background:' + CLAY_IDLE + '"></i>без тебе</span><span class="muted">×1 — ' + (CLAYS[on] || on).toLowerCase() + ', що на колі</span></div>');
    }
  }

  function load(force) {
    if (busy || (!force && Date.now() - loadedAt < 60000)) return;
    busy = true;
    o.api('GET', '/api/games/clicker/diary')
      .then((d) => { if (d && d.ok) { data = d; loadedAt = Date.now(); render(); } })
      .catch(() => { /* мережа — наступний показ спробує ще */ })
      .finally(() => { busy = false; });
  }

  window.HGonchar = {
    init(opts) {
      o = opts;
      addEventListener('resize', () => {
        clearTimeout(resizeT);
        resizeT = setTimeout(() => { if (document.body.classList.contains('route-gonchar')) render(); }, 150);
      });
    },
    show() {
      render();
      load(false);
    },
    hide() { hideTip(); },
  };
})();
