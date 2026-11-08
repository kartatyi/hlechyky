// «Сорочинський ярмарок» — пласке поле (SVG). Той самий API, що й 3D-сцена (docs/games/specs/sorochyntsi.md, §10):
// запасний вигляд для слабких пристроїв / без WebGL і вибір «🗺 Пласка». Правил не знає, дій не шле — лише малює вид
// і каже картці, куди клацнули (лише по підсвіченому).
//
// Розкладка (§11, «2D»): широке поле — прилавки суперників смугами згори, гості рядком (старшина й булава праворуч),
// три ряди крамниць (III згори) з колодою ліворуч, банк стовпчиком праворуч, мій прилавок смугою внизу. Вузьке
// (висота > ширини) — банк рядком над гостями. На 375 px картка ряду ≥ 72 px завширшки.
// setParts (лише тут, 3D його не має): картка ховає те, що й так видно в HUD — на ПК лишаються ринок і банк, на
// телефоні — без мого прилавка (він у стрічці знизу).

import {
  GOODS, BONUSES, TOKENS, GOOD_COLOR, GOOD_INK, GOOD_NAME, TIER_ROMAN, TIER_NAME, GUESTS, SEAT_COLOR, SEAT_SHAPE,
  SEAT_LIGHT, PAL, card as cardOf,
} from './consts.js';
import { svgIcon, tokenIconColors, ICON_FILL, ICON_DETAIL } from './icons.js';

const NS = 'http://www.w3.org/2000/svg';
const CW = 100, CH = 128, GAP = 8;   // крамниця в одиницях viewBox
const INK = '#2a1d17';

// ─── Вигляд (опитування 09.11, «Тепле дерево»): стільниця з дошками, ряди на доріжці з вишитою каймою, власні значки
// (icons.js) замість емодзі, тіні й об'єм у жетонах. Палітра — тут, кольори товарів — consts.js.
const TH = {
  table: ['#86562f', '#6a4124'], planks: 'rgba(0,0,0,.14)', runner: 'rgba(40,18,6,.28)', band: '#efe3c4',
  paper: ['#fffaf0', '#f1e3c3'], edge: '#6e4523', shadow: 'rgba(28,12,2,.38)',
  tile: '#4e2f19', tileEdge: '#c8955a', tileInk: '#f6efdc', ok: '#8ee09a', muted: 'rgba(246,239,220,.55)', near: '#ffd23f',
  deck: '#5e3b21', deckEdge: '#d9a933', deckOrn: '#c23a32', deckInk: '#f6efdc', empty: 'rgba(255,255,255,.28)', count: 'rgba(0,0,0,.35)',
};
/** Смуга крамниці — колір товару; полотно — темніше за лице, щоб смуга не зникала. */
const BAND = { ...GOOD_COLOR, linen: '#dccba0' };
const bandIcon = (gd) => (gd === 'linen' ? { fill: '#fffaf0', detail: '#b3262e' } : tokenIconColors(gd, GOOD_COLOR[gd]));
function mixHex(a, b, k) {
  const p = (h) => [1, 3, 5].map((i) => parseInt(String(h).slice(i, i + 2), 16));
  const x = p(a), y = p(b);
  return 'rgb(' + x.map((v, i) => Math.round(v + (y[i] - v) * k)).join(',') + ')';
}

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
.sor-m-leg > .lh { fill: none; stroke: transparent; stroke-width: 3.5; }
.sor-m-leg:hover > .lh, .sor-m-leg:focus-visible > .lh { stroke: #fff3c4; }
.sor-m .lgB { fill: none; stroke: #3fbf5f; stroke-width: 3.5; }
.sor-m .lgG { fill: none; stroke: #e9b838; stroke-width: 3.5; }
.sor-m .pz { animation: sor-m-pulse 1.1s ease-in-out infinite alternate; }
.sor-m text.serif { font-family: Georgia, 'Times New Roman', serif; }
@media (prefers-reduced-motion: reduce) { .sor-m-leg > .lg, .sor-m .fin, .sor-m .pz { animation: none; } .sor-m .lift { transition: none; } }
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
  const o = { onPick() {}, onHover() {}, onLayout() {}, reducedMotion: false, ...opts };
  // Що малювати, крім ринку й банку: картка ховає те, що й так видно поруч у HUD (ПК — плашки й колонка, телефон — стрічка
  // мого прилавка), щоб ринок був більший. Типово — усе (стенди, лежачий телефон).
  const ALL = { opp: true, guests: true, awards: true, mine: true };
  const st = { view: null, legal: null, marks: {}, pick: null, sel: null, mode: null, sig: '', pos: {}, timers: new Set(), dead: false, hot: null,
    parts: { ...ALL, ...(opts.parts || {}) } };

  const U = 'sm' + Math.random().toString(36).slice(2, 7);   // id градієнтів цього поля
  // Арт крамниць (опитування 09.11): сценки — art.js; не завантажився — крамниці з емблемою, як до арту.
  const ART = await import('./art.js').catch(() => null);
  const svg = el('svg', { class: 'sor-m', preserveAspectRatio: 'xMidYMid meet', role: 'group', 'aria-label': 'Ярмарковий стіл' });
  el('style', null, svg).textContent = CSS;
  {
    const defs = el('defs', null, svg);
    const grad = (tag, id, attrs, stops) => {
      const g = el(tag, { id: U + id, ...attrs }, defs);
      stops.forEach(([o, c, a]) => el('stop', { offset: o, 'stop-color': c, 'stop-opacity': a == null ? 1 : a }, g));
    };
    grad('linearGradient', 'p', { x1: 0, y1: 0, x2: 0, y2: 1 }, [[0, TH.paper[0]], [1, TH.paper[1]]]);
    // тіло крамниці (під смугою) із заокругленим низом — рамка для сцени
    el('path', { d: 'M0 26h' + CW + 'v' + (CH - 34) + 'a8 8 0 0 1-8 8H8a8 8 0 0 1-8-8z' }, el('clipPath', { id: U + 'cb' }, defs));
    st.defs = defs;
    grad('linearGradient', 't', { x1: 0, y1: 0, x2: 0, y2: 1 }, [[0, TH.table[0]], [1, TH.table[1]]]);
    [...TOKENS, 'fern'].forEach((gd) => {
      const c = GOOD_COLOR[gd];
      grad('radialGradient', 'k' + gd, { cx: 0.38, cy: 0.32, r: 0.78 }, [[0, mixHex(c, '#ffffff', gd === 'gold' ? 0.5 : 0.24)], [0.62, c], [1, darker(c, 0.78)]]);
    });
  }
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
    const P = st.parts;
    const L = { tall, parts: P };
    const nOpp = Math.max(1, seats().others.length);
    if (!tall) {
      const deckW = 74, mx = 8, mw = deckW + 10 + 4 * CW + 3 * GAP;   // 508
      const bankX = mx + mw + 16;
      L.W = bankX + 56 + 8;
      L.opp = { y: 6, h: 48, x: mx, w: L.W - 2 * mx - 96, n: nOpp };   // праворуч угорі — місце для «🧊 3D / 🔇» картки
      // Без смуг суперників (ПК: вони в плашках) гості й нагороди — першим рядком, вищі (під ними мій поступ), а ліве
      // верхнє місце над колодами вільне — там стоять «🧊 3D / 🔇» картки.
      const gy = P.opp ? 62 : 6, gh = P.opp ? 80 : 92;
      L.prog = !P.opp;
      L.guests = { y: gy, h: gh, x: P.opp ? mx : mx + deckW + 10, tw: 82, gap: 6 };
      L.awards = { y: gy, h: gh, w: 62, x: L.W - mx - 2 * 62 - 6 };
      // Лише ринок: ряди від самого верху; «🧊 3D / 🔇» — стовпчиком у правому верхньому куті над банком, тож банк
      // починається нижче (кружальця щільніше, але не впритул).
      const top = P.opp || P.guests || P.awards;
      const r0 = !top ? 8 : P.opp ? 152 : gy + gh + 10;
      L.rowY = (i) => r0 + i * (CH + GAP);
      L.deck = { x: mx, w: deckW };
      L.cardX = (slot) => mx + deckW + 10 + slot * (CW + GAP);
      const free = top ? 0 : 92, rr = top ? 24 : 22;
      const bankTop = L.rowY(0) + 26 + free, bankStep = (3 * (CH + GAP) - GAP - 52 - free) / 5;
      L.pile = (i) => ({ x: bankX + 28, y: bankTop + i * bankStep, r: rr });
      L.mine = { y: L.rowY(3) + 4, h: 96, x: mx, w: L.W - 2 * mx };
      L.H = P.mine ? L.mine.y + L.mine.h + 8 : L.rowY(2) + CH + 12;
    } else {
      const deckW = 50, mx = 8;
      L.W = mx + deckW + 8 + 4 * CW + 3 * GAP + mx;   // 498
      L.opp = { y: 4, h: 40, x: mx, w: L.W - 2 * mx - 112, n: nOpp };
      // Без смуги суперників (телефон: вони в плашках над полем) купи й гості — від самого верху, а над колодами лишається
      // стовпчик під «🧊 3D / 🔇» (.sor.slim): купи щільніші, гості трохи вужчі.
      const by = P.opp ? 52 : 4, gx = P.opp ? mx : mx + deckW + 8, step = P.opp ? 56 : 50;
      L.pile = (i) => ({ x: gx + 26 + i * step, y: by + 24, r: 22 });
      L.awards = { y: by, h: 50, w: 56, x: L.W - mx - 2 * 56 - 6 };
      L.guests = { y: by + 56, h: 70, x: gx, tw: (L.W - mx - gx - 4 * 6) / 5, gap: 6 };
      L.rowY = (i) => by + 56 + 76 + i * (CH + GAP);
      L.deck = { x: mx, w: deckW };
      L.cardX = (slot) => mx + deckW + 8 + slot * (CW + GAP);
      L.mine = { y: L.rowY(3) + 2, h: 84, x: mx, w: L.W - 2 * mx };
      L.H = P.mine ? L.mine.y + L.mine.h + 6 : L.rowY(2) + CH + 12;
    }
    return L;
  }

  /// Прилавки, яких на полі нема (сховані частини), — точки за краєм: суперники вгорі (там плашки), я — внизу. Туди
  /// летять жетони й крамниці, там з'являються бульбашки чужих ходів.
  function offSeats(L, S) {
    if (!L.parts.opp) S.others.forEach((seat, i) => { st.pos.seat[seat] = [L.W * (i + 1) / (S.others.length + 1), 4]; });
    if (!L.parts.mine && S.me != null) st.pos.seat[S.me] = [L.W / 2, L.H - 4];
  }

  // ─── Малювання ───────────────────────────────────────────────────────────
  function legalSet(kind) { const l = st.legal; return new Set((l && l[kind]) || []); }

  function wrapLegal(parent, kind, id, label, x, y, w, h, rx, marked) {
    const leg = legalSet(kind === 'card' ? 'cards' : kind === 'deck' ? 'decks' : kind === 'pile' ? 'piles' : 'guests').has(id);
    const sel = kind === 'card' && st.sel === id;
    const g = el('g', { class: (leg ? 'sor-m-leg' : '') + (sel ? ' sor-m-sel' : ''), 'data-k': kind, 'data-id': id }, parent);
    // Підсвітка (опитування 09.11): рамка лише на тому, що можна купити (зелена; з дукатом — золота; у свій хід
    // пульсує), решта законного — рамка під курсором і фокусом. Гостя на вибір — яскраво: це обов'язковий вибір.
    const loud = kind === 'guest';
    if (leg) {
      g.setAttribute('tabindex', '0');
      g.setAttribute('role', 'button');
      g.setAttribute('data-pad-focus', '');
      g.setAttribute('aria-label', label);
      if (loud) {
        el('rect', { class: 'lk', x: x - 4, y: y - 4, width: w + 8, height: h + 8, rx: rx + 3 }, g);
        el('rect', { class: 'lg', x: x - 4, y: y - 4, width: w + 8, height: h + 8, rx: rx + 3 }, g);
      } else if (!sel) {
        el('rect', { class: 'lh', x: x - 4, y: y - 4, width: w + 8, height: h + 8, rx: rx + 3 }, g);
      }
    }
    if (sel && !(leg && loud)) el('rect', { class: 'sg', x: x - 4, y: y - 4, width: w + 8, height: h + 8, rx: rx + 3 }, g);
    if (!loud && !sel && (marked === 'buy' || marked === 'gold')) {
      el('rect', { class: (marked === 'buy' ? 'lgB' : 'lgG') + (leg ? ' pz' : ''), x: x - 3.5, y: y - 3.5, width: w + 7, height: h + 7, rx: rx + 2.5 }, g);
    }
    if (marked === 'near') el('rect', { class: 'near', x: x - 2, y: y - 2, width: w + 4, height: h + 4, rx: rx + 2, style: 'stroke:' + TH.near + ';stroke-width:2.6;stroke-dasharray:0.1 5.6;stroke-linecap:round' }, g);
    const lift = el('g', { class: 'lift' }, g);
    if (leg) el('rect', { class: 'hit', x: x - 8, y: y - 8, width: w + 16, height: h + 16 }, g);
    return lift;
  }

  /** Вишита смуга: ромбики червоний/чорний уздовж відрізка (горизонтально чи вертикально) на світлій стрічці. */
  function embroid(parent, x, y, len, h, vert, bg) {
    const g = el('g', null, parent);
    if (bg) el('rect', vert ? { x, y, width: h, height: len, fill: bg } : { x, y, width: len, height: h, fill: bg }, g);
    const r = h * 0.3, step = h * 1.25;
    let pr = '', pk = '';
    for (let t = step / 2, i = 0; t < len - r; t += step, i++) {
      const cx = vert ? x + h / 2 : x + t, cy = vert ? y + t : y + h / 2;
      const d = 'M' + cx + ' ' + (cy - r) + 'l' + r + ' ' + r + '-' + r + ' ' + r + '-' + r + '-' + r + 'z';
      if (i % 2) pk += d; else pr += d;
    }
    el('path', { d: pr, fill: PAL.embroideryRed }, g);
    el('path', { d: pk, fill: PAL.embroideryBlack }, g);
    const lw = Math.max(0.8, h * 0.09);
    if (vert) { el('rect', { x, y, width: lw, height: len, fill: PAL.embroideryRed }, g); el('rect', { x: x + h - lw, y, width: lw, height: len, fill: PAL.embroideryRed }, g); }
    else { el('rect', { x, y, width: len, height: lw, fill: PAL.embroideryRed }, g); el('rect', { x, y: y + h - lw, width: len, height: lw, fill: PAL.embroideryRed }, g); }
    return g;
  }

  /** Стільниця: дерево з дошками, під рядами — темніша доріжка з вишитою каймою згори й знизу. */
  function drawTable(L) {
    const T = TH, W = L.W, H = L.H;
    el('rect', { x: 0, y: 0, width: W, height: H, rx: 14, fill: 'url(#' + U + 't)' }, gBase);
    const ry0 = L.rowY(0) - 8, ry1 = L.rowY(2) + CH + 8;
    {
      let off = 0;
      for (let y = 30; y < H - 6; y += 38) {
        el('rect', { x: 4, y, width: W - 8, height: 1.3, fill: T.planks }, gBase);
        el('rect', { x: 4, y: y + 1.3, width: W - 8, height: 1, fill: 'rgba(255,225,180,.07)' }, gBase);
        off = (off + 137) % W;
        el('rect', { x: off, y: y - 38 + 2, width: 1.3, height: 36, fill: T.planks }, gBase);
      }
    }
    el('rect', { x: 0, y: ry0, width: W, height: ry1 - ry0, fill: T.runner }, gBase);
    embroid(gBase, 0, ry0 - 5, W, 10, false, T.band);
    embroid(gBase, 0, ry1 - 5, W, 10, false, T.band);
  }

  /** Сцена крамниці як <symbol> у defs (раз на поле) — у крамницях лише <use>. null — сцени нема. */
  function artSym(id) {
    if (!ART || !st.defs) return null;
    const key = U + 'a' + id;
    if (st.arts && st.arts.has(id)) return st.arts.get(id);
    st.arts = st.arts || new Map();
    const inner = ART.artInner(id);
    if (!inner) { st.arts.set(id, null); return null; }
    const sym = el('symbol', { id: key, viewBox: '0 0 100 102', preserveAspectRatio: 'xMidYMid slice' }, st.defs);
    sym.innerHTML = '<rect width="100" height="102" fill="#f2e7d0"/>' + inner;
    st.arts.set(id, key);
    return key;
  }

  /** Крамниця з артом (розкладка B, опитування 09.11): сцена на все тіло, ціна стовпчиком поверх ліворуч, назва стрічкою внизу. */
  function drawCardArt(g, c, x, y, s, mark, sym) {
    const T = TH;
    const k = el('g', { transform: 'translate(' + x + ',' + y + ') scale(' + s + ')' }, g);
    el('rect', { x: 1.2, y: 2.6, width: CW, height: CH, rx: 8, fill: T.shadow }, k);
    el('rect', { x: 0, y: 0, width: CW, height: CH, rx: 8, fill: '#f2e7d0' }, k);
    el('use', { href: '#' + sym, x: 0, y: 26, width: CW, height: CH - 26, 'clip-path': 'url(#' + U + 'cb)' }, k);
    el('rect', { x: 0, y: 0, width: CW, height: CH, rx: 8, fill: 'none', stroke: T.edge, 'stroke-width': 1.6 }, k);
    const col = BAND[c.bonus], ink = GOOD_INK[c.bonus];
    el('path', { d: 'M0,8 a8,8 0 0 1 8,-8 h' + (CW - 16) + ' a8,8 0 0 1 8,8 v18 h-' + CW + ' z', fill: col, stroke: T.edge, 'stroke-width': 1.6 }, k);
    let pr = '', pk = '';
    for (let i = 0; i < 11; i++) {
      const cx = 7 + i * 8.6, cy = 30;
      const d = 'M' + cx + ' ' + (cy - 2.3) + 'l2.3 2.3-2.3 2.3-2.3-2.3z';
      if (i % 2) pk += d; else pr += d;
    }
    el('path', { d: pr, fill: PAL.embroideryRed, opacity: 0.85 }, k);
    el('path', { d: pk, fill: PAL.embroideryBlack, opacity: 0.75 }, k);
    if (c.pts) {
      txt(k, 7, 21, String(c.pts), { 'font-size': 20, 'font-weight': 900, fill: ink, stroke: c.bonus === 'linen' ? '#fff6e0' : darker(col, 0.55), 'stroke-width': 2.4, 'paint-order': 'stroke' });
      svgIcon(k, 'star', c.pts >= 10 ? 33 : 25, 13, 13, { fill: '#ffd23f', stroke: '#7a5400', sw: 2.4 });
    }
    if (c.k) svgIcon(k, 'sabres', CW - 33, 13, 16, { fill: ink, lw: 3 });
    svgIcon(k, c.bonus, CW - 13, 13, 19, bandIcon(c.bonus));
    // назва — стрічкою внизу праворуч від ціни, до 2 рядків
    const lines = wrap2(c.name, 13), nh = lines.length > 1 ? 24 : 14;
    el('rect', { x: 28, y: CH - 4 - nh, width: CW - 31, height: nh, rx: 4, fill: 'rgba(255,250,240,.93)', stroke: T.edge, 'stroke-width': 0.8 }, k);
    lines.forEach((l, i) => txt(k, 28 + (CW - 31) / 2, CH - 4 - nh + 10.5 + i * 10.5, l, { 'font-size': 9.4, 'font-weight': 800, fill: INK, 'text-anchor': 'middle' }));
    // ціна — кружечки зі світлим обідком поверх сцени
    let cy = CH - 13.5;
    for (let i = GOODS.length - 1; i >= 0; i--) {
      const gd = GOODS[i], n = c.cost[gd];
      if (!n) continue;
      el('circle', { cx: 15, cy: cy + 1.2, r: 10.4, fill: 'rgba(40,20,5,.32)' }, k);
      el('circle', { cx: 15, cy, r: 10.4, fill: 'url(#' + U + 'k' + gd + ')', stroke: '#fffaf0', 'stroke-width': 1.6 }, k);
      txt(k, 15, cy + 4.8, String(n), { 'font-size': 13.5, 'font-weight': 900, fill: GOOD_INK[gd], 'text-anchor': 'middle' });
      cy -= 22.5;
    }
    // «купиш» — значок угорі праворуч на сцені (низ праворуч — назва)
    if (mark === 'buy') {
      el('circle', { cx: CW - 13, cy: 44, r: 11, fill: PAL.buy, stroke: '#fffaf0', 'stroke-width': 1.8 }, k);
      el('path', { d: 'M' + (CW - 18.2) + ' 44l3.8 4 7-7.8', fill: 'none', stroke: '#fff', 'stroke-width': 3, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, k);
    } else if (mark === 'gold') {
      el('circle', { cx: CW - 13, cy: 44, r: 11, fill: 'url(#' + U + 'kgold)', stroke: '#fffaf0', 'stroke-width': 1.6 }, k);
      svgIcon(k, 'gold', CW - 13, 44, 15, { fill: '#fff0b0', detail: '#8a5e00' });
    }
  }

  /** Крамниця: тінь, папір, смуга кольору товару з вишитим пояском, ⭐ і ⚔ значками, повнобарвна емблема. */
  function drawCardN(g, c, x, y, s, mark) {
    const T = TH;
    const k = el('g', { transform: 'translate(' + x + ',' + y + ') scale(' + s + ')' }, g);
    el('rect', { x: 1.2, y: 2.6, width: CW, height: CH, rx: 8, fill: T.shadow }, k);
    el('rect', { x: 0, y: 0, width: CW, height: CH, rx: 8, fill: 'url(#' + U + 'p)', stroke: T.edge, 'stroke-width': 1.6 }, k);
    const col = BAND[c.bonus], ink = GOOD_INK[c.bonus];
    el('path', { d: 'M0,8 a8,8 0 0 1 8,-8 h' + (CW - 16) + ' a8,8 0 0 1 8,8 v18 h-' + CW + ' z', fill: col, stroke: T.edge, 'stroke-width': 1.6 }, k);
    // вишитий поясок під смугою
    let pr = '', pk = '';
    for (let i = 0; i < 11; i++) {
      const cx = 7 + i * 8.6, cy = 30;
      const d = 'M' + cx + ' ' + (cy - 2.3) + 'l2.3 2.3-2.3 2.3-2.3-2.3z';
      if (i % 2) pk += d; else pr += d;
    }
    el('path', { d: pr, fill: PAL.embroideryRed, opacity: 0.85 }, k);
    el('path', { d: pk, fill: PAL.embroideryBlack, opacity: 0.75 }, k);
    if (c.pts) {
      txt(k, 7, 21, String(c.pts), { 'font-size': 20, 'font-weight': 900, fill: ink, stroke: c.bonus === 'linen' ? '#fff6e0' : darker(col, 0.55), 'stroke-width': 2.4, 'paint-order': 'stroke' });
      svgIcon(k, 'star', c.pts >= 10 ? 33 : 25, 13, 13, { fill: '#ffd23f', stroke: '#7a5400', sw: 2.4 });
    }
    if (c.k) svgIcon(k, 'sabres', CW - 33, 13, 16, { fill: ink, lw: 3 });
    svgIcon(k, c.bonus, CW - 13, 13, 19, bandIcon(c.bonus));
    wrap2(c.name, 12).forEach((l, i) => txt(k, 63, 46 + i * 12, l, { 'font-size': 10.5, 'font-weight': 800, fill: INK, 'text-anchor': 'middle' }));
    const big = c.bonus === 'linen' ? { stroke: '#9c8355', sw: 1.4 } : { stroke: darker(ICON_FILL[c.bonus], 0.55), sw: 0.9 };
    el('ellipse', { cx: 63, cy: 110, rx: 16, ry: 3, fill: 'rgba(60,30,10,.12)' }, k);
    svgIcon(k, c.bonus, 63, 89, 40, { fill: ICON_FILL[c.bonus], detail: ICON_DETAIL[c.bonus], ...big });
    let cy = CH - 13.5;
    for (let i = GOODS.length - 1; i >= 0; i--) {
      const gd = GOODS[i], n = c.cost[gd];
      if (!n) continue;
      el('circle', { cx: 15, cy: cy + 1, r: 10.4, fill: 'rgba(40,20,5,.22)' }, k);
      el('circle', { cx: 15, cy, r: 10.4, fill: 'url(#' + U + 'k' + gd + ')', stroke: darker(GOOD_COLOR[gd], 0.5), 'stroke-width': 1.3 }, k);
      txt(k, 15, cy + 4.8, String(n), { 'font-size': 13.5, 'font-weight': 900, fill: GOOD_INK[gd], 'text-anchor': 'middle' });
      cy -= 22.5;
    }
    if (!mark) txt(k, CW - 7, CH - 6, TIER_ROMAN[c.tier], { class: 'serif', 'font-size': 10, 'font-weight': 700, fill: T.edge, 'text-anchor': 'end', opacity: 0.7 });
    if (mark === 'buy') {
      el('circle', { cx: CW - 14, cy: CH - 14, r: 11.5, fill: PAL.buy, stroke: '#0d3a17', 'stroke-width': 1.8 }, k);
      el('path', { d: 'M' + (CW - 19.5) + ' ' + (CH - 14) + 'l4 4.2 7.4-8.2', fill: 'none', stroke: '#fff', 'stroke-width': 3, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, k);
    } else if (mark === 'gold') {
      el('circle', { cx: CW - 14, cy: CH - 14, r: 11.5, fill: 'url(#' + U + 'kgold)', stroke: '#6b4a00', 'stroke-width': 1.6 }, k);
      svgIcon(k, 'gold', CW - 14, CH - 14, 15, { fill: '#fff0b0', detail: '#8a5e00' });
    }
  }

  /** Крамниця 100×128 у (x, y), масштаб s; mark — 'buy' | 'gold' (значок у куті). */
  function drawCard(g, id, x, y, s = 1, mark) {
    const c = cardOf(id);
    if (!c) return;
    const sym = artSym(id);
    if (sym) drawCardArt(g, c, x, y, s, mark, sym); else drawCardN(g, c, x, y, s, mark);
  }

  /** Колода сорочкою: темне дерево, золотий кант, ромби, римська цифра й кількість. n == null — без числа (бронь наосліп). */
  function drawBack(g, tier, x, y, w, h, n) {
    const T = TH, k = el('g', null, g);
    if (n == null || n > 0) {
      if (n > 1) el('rect', { x: x + 3, y: y + 4, width: w, height: h, rx: 8, fill: darker(T.deck, 0.55) }, k);
      el('rect', { x: x + 1.2, y: y + 2.6, width: w, height: h, rx: 8, fill: T.shadow }, k);
      el('rect', { x, y, width: w, height: h, rx: 8, fill: T.deck, stroke: T.deckEdge, 'stroke-width': 2 }, k);
      el('rect', { x: x + 5, y: y + 5, width: w - 10, height: h - 10, rx: 5, fill: 'none', stroke: T.deckEdge, 'stroke-width': 1, opacity: 0.55 }, k);
      const nR = Math.max(3, Math.floor((w - 14) / 9));
      let pr = '', pk = '';
      for (let i = 0; i < nR; i++) {
        const cx = x + w / 2 + (i - (nR - 1) / 2) * 9;
        for (const cy of [y + 14, y + h - 14]) {
          const d = 'M' + cx + ' ' + (cy - 3.2) + 'l3.2 3.2-3.2 3.2-3.2-3.2z';
          if (i % 2) pk += d; else pr += d;
        }
      }
      el('path', { d: pr, fill: T.deckOrn }, k);
      el('path', { d: pk, fill: T.deckEdge }, k);
      txt(k, x + w / 2, y + h / 2 + (n != null ? 0 : 9), TIER_ROMAN[tier], { class: 'serif', 'font-size': w > 60 ? 30 : 22, 'font-weight': 900, fill: T.deckEdge, 'text-anchor': 'middle' });
    } else {
      el('rect', { x, y, width: w, height: h, rx: 8, fill: 'none', stroke: T.empty, 'stroke-width': 2, 'stroke-dasharray': '6 5' }, k);
    }
    if (n != null) {
      el('rect', { x: x + w / 2 - 15, y: y + h / 2 + 9, width: 30, height: 17, rx: 8.5, fill: n > 0 ? T.count : 'none' }, k);
      txt(k, x + w / 2, y + h / 2 + 22, String(n), { 'font-size': 13, 'font-weight': 800, fill: n > 0 ? T.deckInk : T.empty, 'text-anchor': 'middle' });
    }
  }

  function token(g, gd, x, y, r, n, ring) {
    el('circle', { cx: x + r * 0.05, cy: y + r * 0.13, r, fill: 'rgba(0,0,0,.3)' }, g);
    el('circle', { cx: x, cy: y, r, fill: 'url(#' + U + 'k' + gd + ')', stroke: darker(GOOD_COLOR[gd], 0.5), 'stroke-width': Math.max(1.2, r * 0.08) }, g);
    el('circle', { cx: x, cy: y, r: r * 0.8, fill: 'none', stroke: gd === 'gold' ? 'rgba(122,84,0,.5)' : gd === 'linen' ? 'rgba(120,90,50,.35)' : 'rgba(255,255,255,.32)', 'stroke-width': Math.max(0.8, r * 0.05), 'stroke-dasharray': (r * 0.14) + ' ' + (r * 0.1) }, g);
    svgIcon(g, gd, x, y, r * 1.12, tokenIconColors(gd, GOOD_COLOR[gd]));
    if (n != null) {
      el('circle', { cx: x + r * 0.78, cy: y + r * 0.78, r: r * 0.5, fill: '#1c120b', stroke: ring || '#f6efdc', 'stroke-width': 1.5 }, g);
      txt(g, x + r * 0.78, y + r * 0.78 + r * 0.2, String(n), { 'font-size': r * 0.6, 'font-weight': 900, fill: '#fff', 'text-anchor': 'middle' });
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
    // Стояча мапа (телефон) впирається в ширину: картка підганяє висоту поля під її пропорцію (onLayout), а що лишиться —
    // унизу, не порожньою смугою над купами.
    svg.setAttribute('preserveAspectRatio', L.tall ? 'xMidYMin meet' : 'xMidYMid meet');
    const ratio = L.tall ? Math.round(L.H / L.W * 1e4) / 1e4 : null;
    if (st.ratio !== ratio) { st.ratio = ratio; o.onLayout(ratio); }
    // Стільниця й скатертина під рядами.
    drawTable(L);
    const S = seats();
    if (L.parts.opp) drawOpps(L, S);
    if (L.parts.guests) drawGuests(L);
    if (L.parts.awards) drawAwards(L);
    drawMarket(L);
    drawBank(L);
    if (L.parts.mine) drawMine(L, S);
    offSeats(L, S);
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
      txt(g, x + w - 6, y + 18, String(p.points || 0), { 'font-size': 12.5, 'font-weight': 800, fill: PAL.lantern, 'text-anchor': 'end' });
      svgIcon(g, 'star', x + w - 22 - String(p.points || 0).length * 4, y + 13.5, 12, { fill: '#ffd23f', stroke: '#7a5400', sw: 2.4 });
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

  /// Мої знижки й позначки — для поступу на плитках гостей і старшини (лише гравцю, і лише в режимі L.prog).
  function mineForProg(L) {
    const v = st.view;
    if (!L.prog || !v || !v.me) return null;
    return pOf(v.me.seat);
  }

  function drawGuests(L) {
    const v = st.view, G = L.guests;
    const list = v.guests || [];
    const mp = mineForProg(L), myB = mp ? mp.bonus || {} : null;
    // Нагороди в тому ж рядку (широка мапа) — гості до них; стояча мапа — нагороди рядком вище, гості на всю ширину.
    const right = L.awards.y === G.y ? L.awards.x - 8 : L.W - 8;
    const tw = Math.min(G.tw, list.length ? (right - G.x - (list.length - 1) * G.gap) / list.length : G.tw);
    list.forEach((gs, i) => {
      const x = G.x + i * (tw + G.gap), y = G.y, h = G.h;
      const gd = GUESTS[gs.id];
      if (!gd) return;
      const lift = wrapLegal(gBase, 'guest', gs.id, 'Гість ' + gd[1], x, y, tw, h, 8);
      const g = el('g', gs.by != null ? { class: 'dim' } : null, lift);
      el('rect', { x: x + 1.2, y: y + 2.4, width: tw, height: h, rx: 8, fill: TH.shadow }, g);
      el('rect', { x, y, width: tw, height: h, rx: 8, fill: TH.tile, stroke: TH.tileEdge, 'stroke-width': 1.5 }, g);
      const tall = L.tall;
      wrap2(gd[1], tall ? 11 : 12).forEach((l, j) => txt(g, x + tw / 2, y + 13 + j * 11, l, { 'font-size': tall ? 9.5 : 10, 'font-weight': 700, fill: TH.tileInk, 'text-anchor': 'middle' }));
      txt(g, x + tw - 15, y + h - 6, '+3', { 'font-size': 10, 'font-weight': 900, fill: TH.tileInk, 'text-anchor': 'end' });
      svgIcon(g, 'star', x + tw - 9, y + h - 9.5, 10, { fill: '#ffd23f', stroke: '#7a5400', sw: 2.4 });
      const req = GOODS.map((k, j) => [k, gd[2][j]]).filter((r) => r[1]);
      const rr = tall ? 8 : 9, step = rr * 2 + 3, x0 = x + tw / 2 - (req.length - 1) * step / 2;
      // Мій поступ (ПК, гості на полі замість колонки): під кожним кружечком «є/треба»; усе зібрано — зелена рамка.
      const prog = myB && gs.by == null;
      if (prog && req.every(([k, n]) => (myB[k] || 0) >= n)) el('rect', { x: x + 1.5, y: y + 1.5, width: tw - 3, height: h - 3, rx: 7, fill: 'none', stroke: PAL.buy, 'stroke-width': 3 }, g);
      req.forEach(([k, n], j) => {
        const cx = x0 + j * step, cy = prog ? y + 47 : y + h - (tall ? 24 : 28);
        el('circle', { cx, cy, r: rr, fill: 'url(#' + U + 'k' + k + ')', stroke: darker(GOOD_COLOR[k], 0.5), 'stroke-width': 1.3 }, g);
        txt(g, cx, cy + rr * 0.45, String(n), { 'font-size': rr * 1.25, 'font-weight': 800, fill: GOOD_INK[k], 'text-anchor': 'middle' });
        if (prog) {
          const b = Math.min(myB[k] || 0, n);
          txt(g, cx, cy + rr + 12, b + '/' + n, { 'font-size': 10, 'font-weight': 800, fill: b >= n ? TH.ok : b ? TH.tileInk : TH.muted, 'text-anchor': 'middle' });
        }
      });
      if (gs.by != null) seatBadge(lift, gs.by, x + 10, y + h - 10, 7);
      st.pos.guest[gs.id] = [x + tw / 2, y + h / 2];
    });
  }

  function drawAwards(L) {
    const v = st.view, A = L.awards;
    const tiles = [['starshyna', v.starshyna ? v.starshyna.seat : null], ['bulava', v.bulava]];
    tiles.forEach(([k, owner], i) => {
      const x = A.x + i * (A.w + 6), y = A.y, g = el('g', null, gBase);
      el('rect', { x: x + 1.2, y: y + 2.4, width: A.w, height: A.h, rx: 8, fill: TH.shadow }, g);
      el('rect', { x, y, width: A.w, height: A.h, rx: 8, fill: k === 'bulava' ? '#7a1f22' : TH.tile, stroke: PAL.gild, 'stroke-width': owner != null ? 2.5 : 1.2 }, g);
      // старшина — схрещені шаблі й «3 ⭐»; булава — пернач на червоній подушці
      svgIcon(g, k === 'bulava' ? 'bulava' : 'sabres', x + A.w / 2, y + A.h * (k === 'bulava' ? 0.5 : 0.52), A.h * (k === 'bulava' ? 0.56 : 0.4),
        k === 'bulava' ? { fill: '#e2b23a', detail: '#7a4a00', stroke: '#4a1010', sw: 1.2 } : { fill: PAL.gild, lw: 2.6 });
      if (k === 'starshyna') {
        txt(g, x + 5, y + A.h - 5, '3', { 'font-size': 10, 'font-weight': 900, fill: TH.tileInk });
        svgIcon(g, 'star', x + 16, y + A.h - 8.5, 9.5, { fill: '#ffd23f', stroke: '#7a5400', sw: 2.4 });
      }
      if (owner != null) seatBadge(g, owner, x + A.w - 10, y + A.h - 10, 7);
      // Мій поступ до старшини: «мої ⚔ / скільки треба, щоб узяти чи перебрати» (своя — ✓).
      const mp = k === 'starshyna' ? mineForProg(L) : null;
      if (mp) {
        const mine = owner != null && owner === mp.seat, need = owner == null ? 3 : (v.starshyna.icons || 0) + 1;
        txt(g, x + A.w / 2, y + 13, mine ? '✓ твоя' : (mp.icons || 0) + '/' + need, { 'font-size': 10, 'font-weight': 800, fill: mine ? TH.ok : TH.tileInk, 'text-anchor': 'middle' });
      }
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
          el('rect', { x, y, width: CW, height: CH, rx: 8, fill: 'none', stroke: TH.empty, 'stroke-width': 2, 'stroke-dasharray': '6 5' }, gBase);
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
    svgIcon(g, 'star', M.x + 14, M.y + 39.5, L.tall ? 11 : 12.5, { fill: '#ffd23f', stroke: '#7a5400', sw: 2.4 });
    txt(g, M.x + 21, M.y + 44, String(p.points || 0), { 'font-size': L.tall ? 11 : 13, 'font-weight': 800, fill: PAL.lantern });
    txt(g, M.x + 21, M.y + 60, (p.ntok || 0) + '/10', { 'font-size': 10.5, 'font-weight': 700, fill: (p.ntok || 0) >= 10 ? '#ff8a80' : '#f3ead7', 'text-anchor': 'middle' });
    if (p.starshyna) svgIcon(g, 'sabres', M.x + (p.bulava ? 14 : 21), M.y + 72, 13, { fill: PAL.gild, lw: 2.8 });
    if (p.bulava) svgIcon(g, 'bulava', M.x + (p.starshyna ? 29 : 21), M.y + 72, 14, { fill: '#e2b23a', detail: '#7a4a00' });
    // 6 стовпчиків: знижка (стос) зверху, жетони — під нею; під цвітом — дукати.
    const x0 = M.x + 42, colW = L.tall ? 43 : 48;
    BONUSES.forEach((gd, j) => {
      const cx = x0 + j * colW + colW / 2, nb = (p.bonus || {})[gd] || 0;
      const by = M.y + 8, bw = colW - 8, bh = L.tall ? 32 : 38;
      el('rect', { x: cx - bw / 2, y: by, width: bw, height: bh, rx: 5, fill: nb ? GOOD_COLOR[gd] : 'none', stroke: nb ? darker(GOOD_COLOR[gd], 0.5) : 'rgba(255,255,255,.45)', 'stroke-width': 1.5, 'stroke-dasharray': nb ? null : '3 3' }, g);
      svgIcon(g, gd, cx - bw / 2 + 9, by + 9, 12, nb ? bandIcon(gd) : { fill: 'rgba(255,255,255,.55)', detail: 'rgba(0,0,0,.3)' });
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
      st.legal, st.marks, st.pick, st.sel, st.parts]);
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
  /// Лише пласке поле: { opp, guests, awards, mine } — що малювати поруч із ринком (див. ALL).
  function setParts(p) { st.parts = { ...ALL, ...(p || {}) }; paint(); }

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
    const any = e.target && e.target.closest && e.target.closest('[data-k="card"]');
    const g = legalAt(e.target) || (any && svg.contains(any) ? any : null);
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
      svgIcon(g, 'guest', 0, -4, 20, { fill: PAL.gild, detail: PAL.tableDark });
      txt(g, 0, 18, gd ? gd[1].split(' ')[0] : '', { 'font-size': 9, fill: '#fff', 'text-anchor': 'middle' });
    }, 380);
  }
  const badge = (icon) => (g) => {
    el('circle', { r: 20, fill: '#7a1f22', stroke: PAL.gild, 'stroke-width': 3 }, g);
    svgIcon(g, icon, 0, 0, 26, icon === 'bulava' ? { fill: '#e2b23a', detail: '#7a4a00' } : { fill: PAL.gild, lw: 2.6 });
  };
  async function starshyna(ev) { await fly(ev.from != null ? at('seat', ev.from) : at('starshyna'), at('seat', ev.seat), badge('sabres'), 380); }
  async function bulava(ev) { await fly(at('bulava'), at('seat', ev.seat), badge('bulava'), 400); }
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
    if (st.ratio != null) o.onLayout(null);
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
    setState, setLegal, setMarks, setPick, setSelected, setParts,
    take, reserve, buy, refill, discard, guest, starshyna, bulava, finale, screenPos,
    resetView() {}, resize() { paint(); }, dispose,
  };
}
