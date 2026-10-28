// «Сорочинський ярмарок» — пласке поле (SVG). Той самий API, що й 3D-сцена (docs/games/specs/sorochyntsi.md, §10):
// запасний вигляд для слабких пристроїв / без WebGL і вибір «🗺 Пласка». Правил не знає, дій не шле — лише малює вид
// і каже картці, куди клацнули (лише по підсвіченому).
//
// Розкладка (§11, «2D»): широке поле — прилавки суперників смугами згори, гості рядком (старшина й булава праворуч),
// три ряди крамниць (III згори) з колодою ліворуч, банк стовпчиком праворуч, мій прилавок смугою внизу. Вузьке
// (висота > ширини) — банк рядком над гостями. На 375 px картка ряду ≥ 72 px завширшки.

import {
  GOODS, BONUSES, TOKENS, GOOD_ICON, GOOD_COLOR, GOOD_INK, GOOD_NAME, TIER_ROMAN, TIER_NAME, GUESTS, SEAT_COLOR, SEAT_SHAPE,
  SEAT_LIGHT, PAL, card as cardOf,
} from './consts.js';

const NS = 'http://www.w3.org/2000/svg';
const CW = 100, CH = 128, GAP = 8;   // крамниця в одиницях viewBox
const INK = '#2a1d17';

const CSS = `
.sor-m { width: 100%; height: 100%; display: block; touch-action: manipulation; user-select: none; -webkit-user-select: none; }
.sor-m text { pointer-events: none; font-family: system-ui, -apple-system, 'Segoe UI', sans-serif; }
.sor-m .em { font-family: 'Apple Color Emoji', 'Segoe UI Emoji', 'Noto Color Emoji', sans-serif; }
.sor-m-leg { cursor: pointer; outline: none; }
.sor-m-leg > .lg { fill: none; stroke: ${PAL.legal}; stroke-width: 3; animation: sor-m-pulse 1.1s ease-in-out infinite alternate; }
.sor-m-leg > .lk { fill: none; stroke: ${PAL.legalEdge}; stroke-width: 6; opacity: .8; }
.sor-m-leg > .hit { fill: transparent; }
.sor-m-leg:hover > .lift, .sor-m-leg:focus-visible > .lift { transform: translateY(-5px); }
.sor-m-leg:hover > .lg, .sor-m-leg:focus-visible > .lg { stroke: #fff6c8; stroke-width: 5; animation: none; }
.sor-m .lift { transition: transform .15s ease-out; }
.sor-m-sel > .lift { transform: translateY(-7px); }
.sor-m-sel > .lg, .sor-m-sel > .sg { stroke: #fff; stroke-width: 6; animation: none; }
.sor-m .sg { fill: none; stroke: #fff; stroke-width: 5; }
.sor-m .near { fill: none; stroke: ${PAL.near}; stroke-width: 3; stroke-dasharray: 7 5; }
.sor-m .dim { opacity: .45; }
.sor-m .turn { stroke: ${PAL.lantern}; stroke-width: 3.5; }
.sor-m .fin { stroke: ${PAL.gild}; stroke-width: 5; animation: sor-m-pulse .9s ease-in-out 3 alternate; }
@keyframes sor-m-pulse { from { stroke-opacity: .55; } to { stroke-opacity: 1; } }
@media (prefers-reduced-motion: reduce) { .sor-m-leg > .lg, .sor-m .fin { animation: none; } .sor-m .lift { transition: none; } }
`;

function el(tag, attrs, parent) {
  const e = document.createElementNS(NS, tag);
  if (attrs) for (const k in attrs) if (attrs[k] != null) e.setAttribute(k, attrs[k]);
  if (parent) parent.appendChild(e);
  return e;
}
function txt(parent, x, y, s, attrs) {
  const t = el('text', { x, y, ...attrs }, parent);
  t.textContent = s;
  return t;
}
function darker(hex, k = 0.6) {
  const n = parseInt(String(hex).slice(1), 16);
  const c = (s) => Math.round(((n >> s) & 255) * k);
  return 'rgb(' + c(16) + ',' + c(8) + ',' + c(0) + ')';
}
/** Назва в 2 рядки до ~n знаків. */
function wrap2(s, n) {
  const w = String(s).split(' ');
  const a = [];
  let cur = '';
  for (const x of w) {
    if (!cur) cur = x;
    else if ((cur + ' ' + x).length <= n) cur += ' ' + x;
    else { a.push(cur); cur = x; }
  }
  if (cur) a.push(cur);
  if (a.length > 2) a.splice(1, a.length - 1, a.slice(1).join(' '));
  return a.map((l) => (l.length > n + 2 ? l.slice(0, n + 1) + '…' : l));
}

export async function createBoard2d(host, opts = {}) {
  const o = { onPick() {}, onHover() {}, reducedMotion: false, ...opts };
  const st = { view: null, legal: null, marks: {}, pick: null, sel: null, mode: null, sig: '', pos: {}, timers: new Set(), dead: false, hot: null };

  const svg = el('svg', { class: 'sor-m', preserveAspectRatio: 'xMidYMid meet', role: 'group', 'aria-label': 'Ярмарковий стіл' });
  el('style', null, svg).textContent = CSS;
  const gBase = el('g', null, svg), gFx = el('g', { 'pointer-events': 'none' }, svg);
  host.appendChild(svg);
  if (getComputedStyle(host).position === 'static') host.style.position = 'relative';

  const later = (fn, ms) => { const t = setTimeout(() => { st.timers.delete(t); if (!st.dead) fn(); }, ms); st.timers.add(t); return t; };
  const sleep = (ms) => new Promise((res) => later(res, ms));

  // ─── Хто де ──────────────────────────────────────────────────────────────
  const players = () => (st.view && st.view.players) || [];
  const pOf = (seat) => players().find((p) => p.seat === seat) || null;
  const colorOf = (seat) => { const p = pOf(seat); return SEAT_COLOR[p ? p.color : seat] || '#ccc'; };
  const shapeOf = (seat) => { const p = pOf(seat); return SEAT_SHAPE[p ? p.color : seat] || '●'; };
  const lightOf = (seat) => { const p = pOf(seat); return !!SEAT_LIGHT[p ? p.color : seat]; };
  /** «Я» (глядач — order[0]) і решта за порядком ходу від мене. */
  function seats() {
    const v = st.view;
    const order = (v && v.order) || players().map((p) => p.seat);
    const me = v && v.me ? v.me.seat : order[0];
    const i = Math.max(0, order.indexOf(me));
    return { me, others: order.slice(i + 1).concat(order.slice(0, i)) };
  }

  // ─── Розкладка ───────────────────────────────────────────────────────────
  function layout() {
    const r = host.getBoundingClientRect();
    const tall = r.height > r.width * 1.02;
    const L = { tall };
    const nOpp = Math.max(1, seats().others.length);
    if (!tall) {
      const deckW = 74, mx = 8, mw = deckW + 10 + 4 * CW + 3 * GAP;   // 508
      const bankX = mx + mw + 16;
      L.W = bankX + 56 + 8;
      L.opp = { y: 6, h: 48, x: mx, w: L.W - 2 * mx - 96, n: nOpp };   // праворуч угорі — місце для «🧊 3D / 🔇» картки
      L.guests = { y: 62, h: 80, x: mx, tw: 82, gap: 6 };
      L.awards = { y: 62, h: 80, w: 62, x: L.W - mx - 2 * 62 - 6 };
      L.rowY = (i) => 152 + i * (CH + GAP);
      L.deck = { x: mx, w: deckW };
      L.cardX = (slot) => mx + deckW + 10 + slot * (CW + GAP);
      const bankTop = L.rowY(0) + 26, bankStep = (3 * (CH + GAP) - GAP - 52) / 5;
      L.pile = (i) => ({ x: bankX + 28, y: bankTop + i * bankStep, r: 24 });
      L.mine = { y: L.rowY(3) + 4, h: 96, x: mx, w: L.W - 2 * mx };
      L.H = L.mine.y + L.mine.h + 8;
    } else {
      const deckW = 50, mx = 8;
      L.W = mx + deckW + 8 + 4 * CW + 3 * GAP + mx;   // 498
      L.opp = { y: 4, h: 40, x: mx, w: L.W - 2 * mx - 112, n: nOpp };
      const by = 52;
      L.pile = (i) => ({ x: mx + 26 + i * 56, y: by + 24, r: 22 });
      L.awards = { y: by, h: 50, w: 56, x: L.W - mx - 2 * 56 - 6 };
      L.guests = { y: by + 56, h: 70, x: mx, tw: (L.W - 2 * mx - 4 * 6) / 5, gap: 6 };
      L.rowY = (i) => by + 56 + 76 + i * (CH + GAP);
      L.deck = { x: mx, w: deckW };
      L.cardX = (slot) => mx + deckW + 8 + slot * (CW + GAP);
      L.mine = { y: L.rowY(3) + 2, h: 84, x: mx, w: L.W - 2 * mx };
      L.H = L.mine.y + L.mine.h + 6;
    }
    return L;
  }

  // ─── Малювання ───────────────────────────────────────────────────────────
  function legalSet(kind) { const l = st.legal; return new Set((l && l[kind]) || []); }

  function wrapLegal(parent, kind, id, label, x, y, w, h, rx, marked) {
    const leg = legalSet(kind === 'card' ? 'cards' : kind === 'deck' ? 'decks' : kind === 'pile' ? 'piles' : 'guests').has(id);
    const sel = kind === 'card' && st.sel === id;
    const g = el('g', { class: (leg ? 'sor-m-leg' : '') + (sel ? ' sor-m-sel' : ''), 'data-k': kind, 'data-id': id }, parent);
    if (leg) {
      g.setAttribute('tabindex', '0');
      g.setAttribute('role', 'button');
      g.setAttribute('data-pad-focus', '');
      g.setAttribute('aria-label', label);
      el('rect', { class: 'lk', x: x - 4, y: y - 4, width: w + 8, height: h + 8, rx: rx + 3 }, g);
      el('rect', { class: 'lg', x: x - 4, y: y - 4, width: w + 8, height: h + 8, rx: rx + 3 }, g);
    } else if (sel) el('rect', { class: 'sg', x: x - 4, y: y - 4, width: w + 8, height: h + 8, rx: rx + 3 }, g);
    if (marked === 'near') el('rect', { class: 'near', x: x - 2, y: y - 2, width: w + 4, height: h + 4, rx: rx + 2 }, g);
    const lift = el('g', { class: 'lift' }, g);
    if (leg) el('rect', { class: 'hit', x: x - 8, y: y - 8, width: w + 16, height: h + 16 }, g);
    return lift;
  }

  /** Крамниця 100×128 у (x, y), масштаб s. */
  function drawCard(g, id, x, y, s = 1, mark) {
    const c = cardOf(id);
    if (!c) return;
    const k = el('g', { transform: 'translate(' + x + ',' + y + ') scale(' + s + ')' }, g);
    el('rect', { x: 0, y: 0, width: CW, height: CH, rx: 8, fill: '#f6efdc', stroke: PAL.plaqueEdge, 'stroke-width': 2 }, k);
    const col = GOOD_COLOR[c.bonus], ink = GOOD_INK[c.bonus];
    el('path', { d: 'M0,8 a8,8 0 0 1 8,-8 h' + (CW - 16) + ' a8,8 0 0 1 8,8 v20 h-' + CW + ' z', fill: col, stroke: PAL.plaqueEdge, 'stroke-width': 2 }, k);
    if (c.pts) txt(k, 8, 22, String(c.pts), { 'font-size': 21, 'font-weight': 800, fill: ink });
    if (c.pts) txt(k, c.pts >= 10 ? 30 : 21, 21, '⭐', { class: 'em', 'font-size': 11 });
    if (c.k) txt(k, c.pts ? 38 : 8, 21, '⚔', { 'font-size': 15, 'font-weight': 800, fill: ink });
    txt(k, CW - 7, 21, GOOD_ICON[c.bonus], { class: 'em', 'font-size': 17, 'text-anchor': 'end' });
    // Назва — дрібно під смугою, праворуч від ціни; емблема — велика бліда.
    txt(k, 66, 84, GOOD_ICON[c.bonus], { class: 'em', 'font-size': 34, 'text-anchor': 'middle', opacity: 0.32 });
    wrap2(c.name, 12).forEach((l, i) => txt(k, 66, 42 + i * 12, l, { 'font-size': 10.5, 'font-weight': 700, fill: INK, 'text-anchor': 'middle' }));
    let cy = CH - 13;
    for (let i = GOODS.length - 1; i >= 0; i--) {
      const gd = GOODS[i], n = c.cost[gd];
      if (!n) continue;
      el('circle', { cx: 15, cy, r: 10.5, fill: GOOD_COLOR[gd], stroke: darker(GOOD_COLOR[gd], 0.55), 'stroke-width': 1.6 }, k);
      txt(k, 15, cy + 5, String(n), { 'font-size': 14, 'font-weight': 800, fill: GOOD_INK[gd], 'text-anchor': 'middle' });
      cy -= 23;
    }
    // Рівень — римська цифра в куті.
    txt(k, CW - 7, CH - 7, TIER_ROMAN[c.tier], { 'font-size': 10, 'font-weight': 800, fill: PAL.plaqueEdge, 'text-anchor': 'end', opacity: 0.7 });
    if (mark === 'buy') {
      el('circle', { cx: CW - 14, cy: CH - 30, r: 12, fill: PAL.buy, stroke: '#0d3a17', 'stroke-width': 2 }, k);
      txt(k, CW - 14, CH - 24.5, '✓', { 'font-size': 16, 'font-weight': 900, fill: '#fff', 'text-anchor': 'middle' });
    } else if (mark === 'gold') {
      el('circle', { cx: CW - 14, cy: CH - 30, r: 12, fill: GOOD_COLOR.gold, stroke: '#5a3e00', 'stroke-width': 2 }, k);
      txt(k, CW - 14, CH - 25.5, '🪙', { class: 'em', 'font-size': 13, 'text-anchor': 'middle' });
    }
  }

  function drawBack(g, tier, x, y, w, h, n) {
    const k = el('g', null, g);
    if (n > 1) el('rect', { x: x + 3, y: y + 4, width: w, height: h, rx: 8, fill: PAL.tableDark, stroke: '#2a1a0c', 'stroke-width': 1.5 }, k);
    if (n > 0) {
      el('rect', { x, y, width: w, height: h, rx: 8, fill: '#6e4523', stroke: PAL.gild, 'stroke-width': 2 }, k);
      el('rect', { x: x + 5, y: y + 5, width: w - 10, height: h - 10, rx: 5, fill: 'none', stroke: PAL.embroideryRed, 'stroke-width': 2, 'stroke-dasharray': '6 4' }, k);
      txt(k, x + w / 2, y + h / 2 - 2, TIER_ROMAN[tier], { 'font-size': w > 60 ? 26 : 20, 'font-weight': 900, fill: PAL.gild, 'text-anchor': 'middle' });
    } else {
      el('rect', { x, y, width: w, height: h, rx: 8, fill: 'none', stroke: 'rgba(255,255,255,.3)', 'stroke-width': 2, 'stroke-dasharray': '6 5' }, k);
    }
    if (n != null) txt(k, x + w / 2, y + h / 2 + 22, String(n), { 'font-size': 15, 'font-weight': 800, fill: n > 0 ? '#f6efdc' : 'rgba(255,255,255,.45)', 'text-anchor': 'middle' });
  }

  function token(g, gd, x, y, r, n, ring) {
    el('circle', { cx: x, cy: y, r, fill: GOOD_COLOR[gd], stroke: darker(GOOD_COLOR[gd], 0.5), 'stroke-width': 2.5 }, g);
    el('circle', { cx: x, cy: y, r: r * 0.74, fill: 'none', stroke: 'rgba(255,255,255,.28)', 'stroke-width': 1.5 }, g);
    txt(g, x, y + r * 0.32, GOOD_ICON[gd], { class: 'em', 'font-size': r * 0.95, 'text-anchor': 'middle' });
    if (n != null) {
      el('circle', { cx: x + r * 0.78, cy: y + r * 0.78, r: r * 0.5, fill: '#1c120b', stroke: ring || '#f6efdc', 'stroke-width': 1.5 }, g);
      txt(g, x + r * 0.78, y + r * 0.78 + r * 0.2, String(n), { 'font-size': r * 0.58, 'font-weight': 800, fill: '#fff', 'text-anchor': 'middle' });
    }
  }

  function seatBadge(g, seat, x, y, r) {
    el('circle', { cx: x, cy: y, r, fill: colorOf(seat), stroke: lightOf(seat) ? INK : 'rgba(0,0,0,.45)', 'stroke-width': 1.5 }, g);
    txt(g, x, y + r * 0.42, shapeOf(seat), { 'font-size': r * 1.15, 'text-anchor': 'middle', fill: lightOf(seat) ? INK : '#fff' });
  }

  function render() {
    const v = st.view;
    gBase.textContent = '';
    st.pos = { card: {}, deck: {}, pile: {}, guest: {}, seat: {} };
    if (!v || !v.rows) { svg.setAttribute('viewBox', '0 0 100 60'); return; }
    const L = layout();
    st.mode = L.tall ? 'tall' : 'wide';
    svg.setAttribute('viewBox', '0 0 ' + L.W + ' ' + L.H);
    // Стільниця й скатертина під рядами.
    el('rect', { x: 0, y: 0, width: L.W, height: L.H, rx: 14, fill: PAL.table }, gBase);
    const ry0 = L.rowY(0) - 8, ry1 = L.rowY(2) + CH + 8;
    el('rect', { x: 2, y: ry0, width: L.W - 4, height: ry1 - ry0, rx: 10, fill: PAL.cloth, opacity: 0.16 }, gBase);
    el('rect', { x: 2, y: ry0, width: L.W - 4, height: ry1 - ry0, rx: 10, fill: 'none', stroke: PAL.embroideryRed, 'stroke-width': 3, 'stroke-dasharray': '10 4 3 4', opacity: 0.8 }, gBase);
    const S = seats();
    drawOpps(L, S);
    drawGuests(L);
    drawAwards(L);
    drawMarket(L);
    drawBank(L);
    drawMine(L, S);
  }

  function drawOpps(L, S) {
    const v = st.view, n = S.others.length;
    if (!n) return;
    const w = (L.opp.w - (n - 1) * 6) / n, h = L.opp.h;
    S.others.forEach((seat, i) => {
      const p = pOf(seat);
      if (!p) return;
      const x = L.opp.x + i * (w + 6), y = L.opp.y;
      const g = el('g', null, gBase);
      const win = v.over && (v.over.winners || []).includes(seat);
      const turn = v.turn === seat && v.phase !== 'over';
      el('rect', { x, y, width: w, height: h, rx: 8, fill: 'rgba(28,18,11,.78)', class: turn ? 'turn' : win ? 'fin' : '', stroke: turn || win ? null : 'rgba(255,255,255,.12)' }, g);
      if (v.over && !win) g.setAttribute('class', 'dim');
      el('rect', { x, y, width: 6, height: h, rx: 3, fill: colorOf(seat) }, g);
      seatBadge(g, seat, x + 18, y + 14, 9);
      const nick = String(p.nick || '').replace(' 🤖', '') + (p.bot || p.away ? ' 🤖' : '');
      const maxc = Math.max(4, Math.floor((w - 80) / 7));
      txt(g, x + 31, y + 18, nick.length > maxc ? nick.slice(0, maxc - 1) + '…' : nick, { 'font-size': 12.5, 'font-weight': 700, fill: '#fff' });
      txt(g, x + w - 6, y + 18, '⭐' + (p.points || 0), { class: 'em', 'font-size': 12.5, 'font-weight': 800, fill: PAL.lantern, 'text-anchor': 'end' });
      // Знижки — крихітні чипи; нульові — тьмяні.
      // Хвіст «🪙1 ·7 📌2» праворуч; знижки — ліворуч, лише коли влазять (вузьке поле на 4 — без них, вони є на плашках).
      const tailW = 84, cw = Math.min(16, (w - 18 - tailW) / 6 - 2);
      if (cw >= 9) BONUSES.forEach((gd, j) => {
        const bx = x + 12 + j * (cw + 2), by = y + h - 17, nb = (p.bonus || {})[gd] || 0;
        el('rect', { x: bx, y: by, width: cw, height: 13, rx: 3, fill: nb ? GOOD_COLOR[gd] : 'none', stroke: nb ? darker(GOOD_COLOR[gd], 0.5) : 'rgba(255,255,255,.35)', 'stroke-width': 1, 'stroke-dasharray': nb ? null : '2 2' }, g);
        if (nb) txt(g, bx + cw / 2, by + 10.5, String(nb), { 'font-size': 10, 'font-weight': 800, fill: GOOD_INK[gd], 'text-anchor': 'middle' });
      });
      const tail = '🪙' + ((p.tokens || {}).gold || 0) + '·' + (p.ntok || 0) + ((p.reserved || []).length ? ' 📌' + p.reserved.length : '');
      txt(g, x + w - 6, y + h - 6, tail, { class: 'em', 'font-size': 10, fill: '#f3ead7', 'text-anchor': 'end' });
      st.pos.seat[seat] = [x + w / 2, y + h / 2];
    });
  }

  function drawGuests(L) {
    const v = st.view, G = L.guests;
    const list = v.guests || [];
    const tw = Math.min(G.tw, list.length ? (L.awards.x - G.x - 8 - (list.length - 1) * G.gap) / list.length : G.tw);
    list.forEach((gs, i) => {
      const x = G.x + i * (tw + G.gap), y = G.y, h = G.h;
      const gd = GUESTS[gs.id];
      if (!gd) return;
      const lift = wrapLegal(gBase, 'guest', gs.id, 'Гість ' + gd[1], x, y, tw, h, 8);
      const g = el('g', gs.by != null ? { class: 'dim' } : null, lift);
      el('rect', { x, y, width: tw, height: h, rx: 8, fill: PAL.tableDark, stroke: PAL.plaque, 'stroke-width': 1.5 }, g);
      const tall = L.tall;
      wrap2(gd[1], tall ? 11 : 12).forEach((l, j) => txt(g, x + tw / 2, y + 13 + j * 11, l, { 'font-size': tall ? 9.5 : 10, 'font-weight': 700, fill: '#f6efdc', 'text-anchor': 'middle' }));
      txt(g, x + tw - 5, y + h - 6, '+3⭐', { class: 'em', 'font-size': 9.5, 'font-weight': 800, fill: PAL.lantern, 'text-anchor': 'end' });
      const req = GOODS.map((k, j) => [k, gd[2][j]]).filter((r) => r[1]);
      const rr = tall ? 8 : 9, step = rr * 2 + 3, x0 = x + tw / 2 - (req.length - 1) * step / 2;
      req.forEach(([k, n], j) => {
        const cx = x0 + j * step, cy = y + h - (tall ? 24 : 28);
        el('circle', { cx, cy, r: rr, fill: GOOD_COLOR[k], stroke: darker(GOOD_COLOR[k], 0.5), 'stroke-width': 1.3 }, g);
        txt(g, cx, cy + rr * 0.45, String(n), { 'font-size': rr * 1.25, 'font-weight': 800, fill: GOOD_INK[k], 'text-anchor': 'middle' });
      });
      if (gs.by != null) seatBadge(lift, gs.by, x + 10, y + h - 10, 7);
      st.pos.guest[gs.id] = [x + tw / 2, y + h / 2];
    });
  }

  function drawAwards(L) {
    const v = st.view, A = L.awards;
    const tiles = [['starshyna', '🎖', v.starshyna ? v.starshyna.seat : null, '3⭐'], ['bulava', '⚜', v.bulava, '']];
    tiles.forEach(([k, icon, owner, sub], i) => {
      const x = A.x + i * (A.w + 6), y = A.y, g = el('g', null, gBase);
      el('rect', { x, y, width: A.w, height: A.h, rx: 8, fill: k === 'bulava' ? '#7a1f22' : PAL.tableDark, stroke: PAL.gild, 'stroke-width': owner != null ? 2.5 : 1.2 }, g);
      txt(g, x + A.w / 2, y + A.h * 0.55, icon, { class: 'em', 'font-size': A.h * 0.42, 'text-anchor': 'middle', fill: PAL.gild, opacity: owner != null ? 1 : 0.85 });
      if (sub) txt(g, x + 4, y + A.h - 5, sub, { class: 'em', 'font-size': 9, 'font-weight': 800, fill: PAL.lantern });
      if (owner != null) seatBadge(g, owner, x + A.w - 10, y + A.h - 10, 7);
      st.pos[k] = [x + A.w / 2, y + A.h / 2];
    });
  }

  function drawMarket(L) {
    const v = st.view;
    for (let i = 0; i < 3; i++) {
      const tier = 3 - i, y = L.rowY(i);
      const n = (v.decks || [])[tier - 1] || 0;
      const lift = wrapLegal(gBase, 'deck', tier, 'Забронювати наосліп з колоди ' + TIER_ROMAN[tier], L.deck.x, y, L.deck.w, CH, 8);
      drawBack(lift, tier, L.deck.x, y, L.deck.w, CH, n);
      st.pos.deck[tier] = [L.deck.x + L.deck.w / 2, y + CH / 2];
      const row = (v.rows || [])[tier - 1] || [];
      for (let s = 0; s < 4; s++) {
        const x = L.cardX(s), id = row[s];
        st.pos['slot' + tier + '_' + s] = [x + CW / 2, y + CH / 2];
        if (id == null) {
          el('rect', { x, y, width: CW, height: CH, rx: 8, fill: 'none', stroke: 'rgba(255,255,255,.22)', 'stroke-width': 2, 'stroke-dasharray': '6 5' }, gBase);
          continue;
        }
        const c = cardOf(id);
        const lift2 = wrapLegal(gBase, 'card', id, c ? c.name + ', ' + TIER_NAME[c.tier] : 'крамниця', x, y, CW, CH, 8, st.marks[id]);
        drawCard(lift2, id, x, y, 1, st.marks[id]);
        st.pos.card[id] = [x + CW / 2, y + CH / 2];
      }
    }
  }

  function drawBank(L) {
    const v = st.view, bank = v.bank || {};
    TOKENS.forEach((gd, i) => {
      const P = L.pile(i);
      const n = bank[gd] || 0;
      const lift = gd === 'gold' ? el('g', null, gBase) : wrapLegal(gBase, 'pile', gd, GOOD_NAME[gd] + ': ' + n, P.x - P.r, P.y - P.r, P.r * 2, P.r * 2, P.r);
      const g = el('g', n ? null : { class: 'dim' }, lift);
      token(g, gd, P.x, P.y, P.r, n);
      const pk = st.pick && st.pick[gd];
      if (pk) {
        // Обрані кружальця «висять» над стосом: у вузькому — над ним, у широкому (банк стовпчиком) — підняті вліво-вгору.
        el('circle', { cx: P.x, cy: P.y, r: P.r + 5, fill: 'none', stroke: '#fff', 'stroke-width': 3 }, lift);
        for (let k = 0; k < pk; k++) {
          if (L.tall) token(lift, gd, P.x + (k - (pk - 1) / 2) * 10, P.y - P.r - 10 - k * 4, P.r * 0.62, null);
          else token(lift, gd, P.x - P.r * 0.9 - k * 7, P.y - P.r * 0.55 - k * 7, P.r * 0.6, null);
        }
        const tx = L.tall ? P.x : P.x - P.r * 0.9 - pk * 7, ty = L.tall ? P.y - P.r * 1.9 - pk * 4 : P.y - P.r * 1.2 - pk * 7;
        txt(lift, tx, ty, '+' + pk, { 'font-size': 14, 'font-weight': 900, fill: PAL.legal, 'text-anchor': 'middle', stroke: PAL.legalEdge, 'stroke-width': 3, 'paint-order': 'stroke' });
      }
      st.pos.pile[gd] = [P.x, P.y];
    });
  }

  function drawMine(L, S) {
    const v = st.view, M = L.mine, seat = S.me, p = pOf(seat);
    if (!p) return;
    const g = el('g', null, gBase);
    const win = v.over && (v.over.winners || []).includes(seat);
    const turn = v.turn === seat && v.phase !== 'over';
    el('rect', { x: M.x, y: M.y, width: M.w, height: M.h, rx: 10, fill: 'rgba(28,18,11,.8)', class: turn ? 'turn' : win ? 'fin' : '', stroke: turn || win ? null : 'rgba(255,255,255,.12)' }, g);
    if (v.over && !win) g.setAttribute('class', 'dim');
    el('rect', { x: M.x, y: M.y, width: 7, height: M.h, rx: 3, fill: colorOf(seat) }, g);
    seatBadge(g, seat, M.x + 21, M.y + 17, 10);
    txt(g, M.x + 22, M.y + 44, '⭐' + (p.points || 0), { class: 'em', 'font-size': L.tall ? 11 : 13, 'font-weight': 800, fill: PAL.lantern, 'text-anchor': 'middle' });
    txt(g, M.x + 21, M.y + 60, (p.ntok || 0) + '/10', { 'font-size': 10.5, 'font-weight': 700, fill: (p.ntok || 0) >= 10 ? '#ff8a80' : '#f3ead7', 'text-anchor': 'middle' });
    if (p.starshyna || p.bulava) txt(g, M.x + 21, M.y + 76, (p.starshyna ? '🎖' : '') + (p.bulava ? '⚜' : ''), { class: 'em', 'font-size': 12, 'text-anchor': 'middle' });
    // 6 стовпчиків: знижка (стос) зверху, жетони — під нею; під цвітом — дукати.
    const x0 = M.x + 42, colW = L.tall ? 43 : 48;
    BONUSES.forEach((gd, j) => {
      const cx = x0 + j * colW + colW / 2, nb = (p.bonus || {})[gd] || 0;
      const by = M.y + 8, bw = colW - 8, bh = L.tall ? 32 : 38;
      el('rect', { x: cx - bw / 2, y: by, width: bw, height: bh, rx: 5, fill: nb ? GOOD_COLOR[gd] : 'none', stroke: nb ? darker(GOOD_COLOR[gd], 0.5) : 'rgba(255,255,255,.45)', 'stroke-width': 1.5, 'stroke-dasharray': nb ? null : '3 3' }, g);
      txt(g, cx - bw / 2 + 4, by + 13, GOOD_ICON[gd], { class: 'em', 'font-size': 11 });
      txt(g, cx + bw / 2 - 4, by + bh - 6, String(nb), { 'font-size': L.tall ? 16 : 18, 'font-weight': 900, fill: nb ? GOOD_INK[gd] : 'rgba(255,255,255,.55)', 'text-anchor': 'end' });
      const tg = gd === 'fern' ? 'gold' : gd, nt = (p.tokens || {})[tg] || 0;
      const ty = by + bh + (L.tall ? 18 : 22), tr = L.tall ? 13 : 15;
      const tk = el('g', nt ? null : { opacity: 0.35 }, g);
      token(tk, tg, cx, ty, tr, nt);
    });
    st.pos.seat[seat] = [x0 + 3 * colW, M.y + M.h / 2];
    // Бронь: до 3 міні-крамниць (моя — лицем, наосліп — з 🂠; глядачу — як видно).
    const rs = (v.me && v.me.seat === seat ? v.me.reserved : p.reserved) || [];
    const sc = (M.h - 12) / CH, mw = CW * sc;
    const rx0 = M.x + M.w - 3 * (mw + 6);
    for (let i = 0; i < 3; i++) {
      const x = rx0 + i * (mw + 6), y = M.y + 6;
      const r = rs[i];
      if (!r) { el('rect', { x, y, width: mw, height: CH * sc, rx: 6, fill: 'none', stroke: 'rgba(255,255,255,.2)', 'stroke-width': 1.5, 'stroke-dasharray': '4 4' }, gBase); continue; }
      if (r.id == null) { drawBack(gBase, r.tier, x, y, mw, CH * sc, null); continue; }
      const c = cardOf(r.id);
      const lift = wrapLegal(gBase, 'card', r.id, (c ? c.name : '') + ' — твоя бронь', x, y, mw, CH * sc, 6, st.marks[r.id]);
      drawCard(lift, r.id, x, y, sc, st.marks[r.id]);
      if (r.blind) txt(lift, x + mw - 4, y + CH * sc - 4, '🂠', { class: 'em', 'font-size': 14, 'text-anchor': 'end', fill: PAL.embroideryRed });
      st.pos.card[r.id] = [x + mw / 2, y + CH * sc / 2];
    }
  }

  // ─── API ─────────────────────────────────────────────────────────────────
  function sigNow() {
    const v = st.view;
    const r = host.getBoundingClientRect();
    const tall = r.height > r.width * 1.02;
    return JSON.stringify([tall, v && [v.players, v.rows, v.decks, v.bank, v.guests, v.starshyna, v.bulava, v.turn, v.phase, v.over && v.over.winners, v.me && v.me.reserved, v.order],
      st.legal, st.marks, st.pick, st.sel]);
  }
  function paint() {
    const s = sigNow();
    if (s === st.sig) return;
    st.sig = s;
    const focusKey = document.activeElement && svg.contains(document.activeElement) ? document.activeElement.getAttribute('data-k') + ':' + document.activeElement.getAttribute('data-id') : null;
    render();
    if (focusKey) {
      const [k, id] = focusKey.split(':');
      const n = gBase.querySelector('[data-k="' + k + '"][data-id="' + id + '"][tabindex]');
      if (n) n.focus({ preventScroll: true });
    }
  }

  function setState(view) { st.view = view; paint(); }
  function setLegal(l) {
    st.legal = l ? { cards: (l.cards || []).slice(), decks: (l.decks || []).slice(), piles: (l.piles || []).slice(), guests: (l.guests || []).slice() } : null;
    paint();
  }
  function setMarks(m) { st.marks = m ? { ...m } : {}; paint(); }
  function setPick(p) { st.pick = p && Object.keys(p).length ? { ...p } : null; paint(); }
  function setSelected(id) { st.sel = id == null ? null : id; paint(); }

  function legalAt(target) {
    const g = target && target.closest && target.closest('.sor-m-leg');
    return g && svg.contains(g) ? g : null;
  }
  const idOf = (g) => (g.dataset.k === 'pile' ? g.dataset.id : +g.dataset.id);
  function onClick(e) { const g = legalAt(e.target); if (g) o.onPick(g.dataset.k, idOf(g)); }
  function onKeyDown(e) {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    const g = legalAt(e.target);
    if (!g) return;
    e.preventDefault(); e.stopPropagation();
    o.onPick(g.dataset.k, idOf(g));
  }
  function onOver(e) {
    const g = legalAt(e.target);
    if (g === st.hot) return;
    st.hot = g;
    if (g) o.onHover(g.dataset.k, idOf(g)); else o.onHover(null, null);
  }
  function onLeave() { if (st.hot) { st.hot = null; o.onHover(null, null); } }
  svg.addEventListener('click', onClick);
  svg.addEventListener('keydown', onKeyDown);
  svg.addEventListener('pointerover', onOver);
  svg.addEventListener('pointerleave', onLeave);

  // ─── Анімації (≤ 400 мс): переліт кружечка/картки, поява ─────────────────
  const at = (k, id) => {
    if (k === 'seat') return st.pos.seat && st.pos.seat[id];
    if (k === 'starshyna' || k === 'bulava') return st.pos[k];
    return st.pos[k] && st.pos[k][id];
  };
  const slotPos = (tier, slot) => st.pos['slot' + tier + '_' + slot];
  async function fly(from, to, draw, ms = 360, delay = 0) {
    if (o.reducedMotion || !from || !to || st.dead) return;
    const g = el('g', null, gFx);
    draw(g);
    g.style.transform = 'translate(' + from[0] + 'px,' + from[1] + 'px)';
    if (delay) { g.style.opacity = '0'; await sleep(delay); g.style.opacity = '1'; }
    try {
      const a = g.animate([{ transform: 'translate(' + from[0] + 'px,' + from[1] + 'px) scale(1)' },
        { transform: 'translate(' + ((from[0] + to[0]) / 2) + 'px,' + (Math.min(from[1], to[1]) - 30) + 'px) scale(1.12)', offset: 0.5 },
        { transform: 'translate(' + to[0] + 'px,' + to[1] + 'px) scale(.85)' }], { duration: ms, easing: 'ease-in-out', fill: 'forwards' });
      await Promise.race([a.finished, sleep(ms + 60)]);
    } catch (e) { /* без WAAPI — просто зникне */ }
    g.remove();
  }
  const tokenAt0 = (gd) => (g) => token(g, gd, 0, 0, 14, null);
  const cardAt0 = (id, s = 0.5) => (g) => { if (id != null) drawCard(g, id, -CW * s / 2, -CH * s / 2, s); else drawBack(g, 3, -CW * s / 2, -CH * s / 2, CW * s, CH * s, 1); };
  function tokensFly(o2, from, to) {
    const list = [];
    for (const gd of TOKENS) for (let i = 0; i < Math.min(4, (o2 || {})[gd] || 0); i++) list.push(gd);
    return Promise.all(list.slice(0, 6).map((gd, i) => fly(from(gd), to(gd), tokenAt0(gd), 320, i * 50)));
  }

  async function take(ev) { await tokensFly(ev.got, (gd) => at('pile', gd), () => at('seat', ev.seat)); }
  async function reserve(ev) {
    const from = ev.slot != null ? slotPos(ev.tier, ev.slot) : at('deck', ev.tier);
    const p = [fly(from, at('seat', ev.seat), ev.slot != null && ev.card != null ? cardAt0(ev.card) : (g) => drawBack(g, ev.tier, -25, -32, 50, 64, 1), 380)];
    if (ev.gold) p.push(fly(at('pile', 'gold'), at('seat', ev.seat), tokenAt0('gold'), 320, 80));
    await Promise.all(p);
  }
  async function buy(ev) {
    await tokensFly(ev.pay, () => at('seat', ev.seat), (gd) => at('pile', gd));
    const from = ev.from === 'row' ? slotPos(cardOf(ev.card) ? cardOf(ev.card).tier : 1, ev.slot) : at('card', ev.card) || at('seat', ev.seat);
    await fly(from, at('seat', ev.seat), cardAt0(ev.card), 340);
  }
  async function refill(ev) {
    if (ev.card == null) return;
    await fly(at('deck', ev.tier), slotPos(ev.tier, ev.slot), cardAt0(ev.card, 0.8), 300);
  }
  async function discard(ev) { await tokensFly(ev.gave, () => at('seat', ev.seat), (gd) => at('pile', gd)); }
  async function guest(ev) {
    const gd = GUESTS[ev.guest];
    await fly(at('guest', ev.guest), at('seat', ev.seat), (g) => {
      el('rect', { x: -30, y: -24, width: 60, height: 48, rx: 7, fill: PAL.tableDark, stroke: PAL.gild, 'stroke-width': 2 }, g);
      txt(g, 0, 4, '🏠', { class: 'em', 'font-size': 20, 'text-anchor': 'middle' });
      txt(g, 0, 18, gd ? gd[1].split(' ')[0] : '', { 'font-size': 9, fill: '#fff', 'text-anchor': 'middle' });
    }, 380);
  }
  const badge = (icon) => (g) => {
    el('circle', { r: 20, fill: '#7a1f22', stroke: PAL.gild, 'stroke-width': 3 }, g);
    txt(g, 0, 8, icon, { class: 'em', 'font-size': 22, 'text-anchor': 'middle' });
  };
  async function starshyna(ev) { await fly(ev.from != null ? at('seat', ev.from) : at('starshyna'), at('seat', ev.seat), badge('🎖'), 380); }
  async function bulava(ev) { await fly(at('bulava'), at('seat', ev.seat), badge('⚜'), 400); }
  async function finale() {
    if (o.reducedMotion) return;
    await sleep(380);
  }

  function screenPos(kind, id) {
    let p = null;
    if (kind === 'card') p = at('card', id);
    else if (kind === 'deck') p = at('deck', id);
    else if (kind === 'pile') p = at('pile', id);
    else if (kind === 'guest') p = at('guest', id);
    else if (kind === 'seat') p = at('seat', id);
    else if (kind === 'starshyna' || kind === 'bulava') p = at(kind);
    const m = svg.getScreenCTM();
    if (!p || !m) return null;
    const pt = svg.createSVGPoint();
    pt.x = p[0]; pt.y = p[1];
    const s = pt.matrixTransform(m);
    const r = host.getBoundingClientRect();
    return { x: s.x - r.left, y: s.y - r.top };
  }

  function dispose() {
    st.dead = true;
    for (const t of st.timers) clearTimeout(t);
    st.timers.clear();
    svg.removeEventListener('click', onClick);
    svg.removeEventListener('keydown', onKeyDown);
    svg.removeEventListener('pointerover', onOver);
    svg.removeEventListener('pointerleave', onLeave);
    svg.remove();
  }

  return {
    kind: '2d',
    setState, setLegal, setMarks, setPick, setSelected,
    take, reserve, buy, refill, discard, guest, starshyna, bulava, finale, screenPos,
    resetView() {}, resize() { paint(); }, dispose,
  };
}
