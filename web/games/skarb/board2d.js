// «Скарб Полуботка» — пласка мапа розрізу (SVG). Той самий API, що й 3D-сцена (docs/games/specs/skarb.md, §10):
// запасний вигляд для слабких пристроїв / без WebGL і вибір «🗺 Пласка». Правил не знає, дій не шле — лише малює
// вид і каже картці, куди клацнули (лише по підсвіченому). Клітинка — прямокутник кольору шару, хід — темні смуги
// від середини до відкритих країв (глуха — огризки й сірий валун), ліхтар — жовта цятка (тьмяна поза linked).

import { GRID, BIT, DIRS, STEP, STRATA, PAL, SEAT_COLOR } from './consts.js';

const NS = 'http://www.w3.org/2000/svg';
const U = 60;            // пікселів viewBox на клітинку
const SKY = 1.2;         // смужка поверхні над полем, клітинок
const TW = 0.36 * U;     // ширина тунелю
const INK = '#20140b';

const CSS = `
.s2 { width: 100%; height: 100%; display: block; user-select: none; -webkit-user-select: none; touch-action: pan-y; }
.s2.zoomed { touch-action: none; }
.s2 text { pointer-events: none; font-family: var(--display, system-ui), system-ui, sans-serif; }
.s2-leg { pointer-events: none; }
.s2-leg rect { fill: rgba(255, 211, 90, .16); stroke: ${PAL.legal}; stroke-width: 4; transform-box: fill-box; transform-origin: center;
  animation: s2pulse 1s ease-in-out infinite alternate; }
.s2-leg.alt rect { fill: rgba(255, 233, 168, .07); stroke: ${PAL.alt}; stroke-width: 2.5; stroke-dasharray: 9 6; opacity: .8; animation: none; }
.s2-leg.rock rect { fill: rgba(214, 69, 65, .14); stroke: ${PAL.bad}; stroke-dasharray: 10 6; }
.s2-leg.goal rect { stroke-width: 5; }
.s2-leg.hot rect { stroke: #fff; stroke-width: 6; fill: rgba(255, 255, 255, .16); animation: none; transform: scale(1.06); opacity: 1; }
.s2-alt { fill: ${PAL.alt}; font-size: 20px; font-weight: 800; opacity: .9; }
.s2-ghost { opacity: .55; pointer-events: none; }
.s2-cur rect { fill: none; stroke: #fff; stroke-width: 3; stroke-dasharray: 6 5; animation: s2march 1s linear infinite; }
@keyframes s2pulse { from { transform: scale(.94); } to { transform: scale(1.02); } }
@keyframes s2march { to { stroke-dashoffset: -22; } }
.s2-pop { transform-box: fill-box; transform-origin: center; animation: s2pop .38s cubic-bezier(.3,1.5,.5,1); }
@keyframes s2pop { 0% { transform: scale(.4); opacity: .2; } 100% { transform: scale(1); opacity: 1; } }
.s2-flip { transform-box: fill-box; transform-origin: center; animation: s2flip .4s ease-out; }
@keyframes s2flip { 0% { transform: scaleX(0); } 100% { transform: scaleX(1); } }
.s2-shake { animation: s2shake .3s linear; }
@keyframes s2shake { 25% { transform: translate(3px, -2px); } 50% { transform: translate(-3px, 2px); } 75% { transform: translate(2px, 1px); } }
.s2-fall { animation: s2fall .38s cubic-bezier(.5,0,1,1) forwards; }
@keyframes s2fall { from { transform: translateY(-70px); opacity: 0; } 30% { opacity: 1; } to { transform: translateY(0); opacity: 1; } }
.s2-fly { animation: s2fly .7s ease-out forwards; }
@keyframes s2fly { 0% { transform: translate(0, 0) scale(.5); opacity: 0; } 20% { opacity: 1; } 100% { transform: translate(var(--dx), var(--dy)) scale(1); opacity: 0; } }
.s2-peek { animation: s2peek 1s ease-out forwards; }
@keyframes s2peek { 0% { transform: translateY(10px); opacity: 0; } 25%, 75% { transform: translateY(-6px); opacity: 1; } 100% { transform: translateY(-14px); opacity: 0; } }
.s2-mole { animation: s2mole .9s ease-in-out forwards; }
@keyframes s2mole { 0%, 100% { transform: translateY(14px); opacity: 0; } 30%, 70% { transform: translateY(0); opacity: 1; } }
.s2-dim { opacity: .35; }
@media (prefers-reduced-motion: reduce) { .s2-leg rect, .s2-cur rect { animation: none; } }
`;

function el(tag, attrs, parent) {
  const e = document.createElementNS(NS, tag);
  if (attrs) for (const k in attrs) e.setAttribute(k, attrs[k]);
  if (parent) parent.appendChild(e);
  return e;
}
const txt = (parent, x, y, s, attrs) => { const t = el('text', { x, y, 'text-anchor': 'middle', ...attrs }, parent); t.textContent = s; return t; };
const strata = (y) => (STRATA.find((s) => y >= s.y0 && y <= s.y1) || STRATA[STRATA.length - 1]).color;
function shade(hex, k) {
  const n = parseInt(hex.slice(1), 16);
  const c = (s) => Math.max(0, Math.min(255, Math.round(((n >> s) & 255) * k)));
  return 'rgb(' + c(16) + ',' + c(8) + ',' + c(0) + ')';
}
/** Детермінований «шум» для відтінку грудок. */
const noise = (x, y) => { const s = Math.sin(x * 12.9898 + y * 78.233) * 43758.5453; return s - Math.floor(s); };

export async function createBoard2d(host, opts = {}) {
  const o = { onPick() {}, onHover() {}, reducedMotion: false, ...opts };
  const st = { grid: null, gridSig: '', view: null, sig: '', legal: null, hot: null, cur: null, timers: new Set(), dead: false,
    vb: null, fit: null, zoom: 1, ptrs: new Map(), drag: null, touchedAt: 0 };

  const svg = el('svg', { class: 's2', preserveAspectRatio: 'xMidYMid meet', role: 'img', 'aria-label': 'Розріз землі під селом' });
  el('style', null, svg).textContent = CSS;
  const defs = el('defs', null, svg);
  const sky = el('linearGradient', { id: 's2sky', x1: 0, y1: 0, x2: 0, y2: 1 }, defs);
  el('stop', { offset: '0', 'stop-color': PAL.skyTop }, sky);
  el('stop', { offset: '1', 'stop-color': PAL.skyLow }, sky);
  const world = el('g', null, svg);
  const gBg = el('g', null, world), gCards = el('g', null, world), gGoals = el('g', null, world), gLegal = el('g', null, world);
  const gGhost = el('g', { class: 's2-ghost' }, world), gFig = el('g', null, world), gCur = el('g', { class: 's2-cur' }, world), gFx = el('g', null, world);
  host.appendChild(svg);
  if (getComputedStyle(host).position === 'static') host.style.position = 'relative';

  const later = (fn, ms) => { const t = setTimeout(() => { st.timers.delete(t); if (!st.dead) fn(); }, ms); st.timers.add(t); return t; };
  const sleep = (ms) => new Promise((res) => later(res, o.reducedMotion ? Math.min(ms, 120) : ms));
  const C = (x, y) => [x * U, y * U];

  // ─── Поле й поверхня (статичне) ─────────────────────────────────────────
  function setGrid(grid) {
    const g = grid || GRID;
    const sig = JSON.stringify(g);
    if (sig === st.gridSig) return;
    st.grid = g; st.gridSig = sig;
    gBg.textContent = '';
    const L = (g.x0 - 0.5) * U, R = (g.x1 + 0.5) * U, T = (g.y0 - 0.5) * U, B = (g.y1 + 0.5) * U;
    // Небо й трава.
    el('rect', { x: L, y: T - SKY * U, width: R - L, height: SKY * U, fill: 'url(#s2sky)' }, gBg);
    el('circle', { cx: R - 1.3 * U, cy: T - 0.85 * U, r: 0.16 * U, fill: '#f6efcf', opacity: 0.9 }, gBg);
    for (let i = 0; i < 9; i++) el('circle', { cx: L + (i * 1.9 + 0.6) * U, cy: T - (0.9 + noise(i, 3) * 0.25) * U, r: 1.4, fill: '#fff', opacity: 0.7 }, gBg);
    el('rect', { x: L, y: T - 0.24 * U, width: R - L, height: 0.24 * U, fill: PAL.grass }, gBg);
    el('rect', { x: L, y: T - 0.06 * U, width: R - L, height: 0.06 * U, fill: shade(PAL.grass, 0.7) }, gBg);
    // Хата: білені стіни, солом'яна стріха, віконце.
    const hx = -2.6 * U, gy = T - 0.24 * U;
    el('rect', { x: hx - 0.55 * U, y: gy - 0.5 * U, width: 1.1 * U, height: 0.5 * U, fill: PAL.wall, stroke: INK, 'stroke-width': 1.5 }, gBg);
    el('path', { d: `M${hx - 0.72 * U},${gy - 0.46 * U} L${hx},${gy - 0.92 * U} L${hx + 0.72 * U},${gy - 0.46 * U} Z`, fill: PAL.thatch, stroke: INK, 'stroke-width': 1.5, 'stroke-linejoin': 'round' }, gBg);
    el('rect', { x: hx - 0.3 * U, y: gy - 0.38 * U, width: 0.2 * U, height: 0.18 * U, fill: PAL.lamp, stroke: INK, 'stroke-width': 1 }, gBg);
    el('rect', { x: hx + 0.12 * U, y: gy - 0.36 * U, width: 0.18 * U, height: 0.36 * U, fill: PAL.wood, stroke: INK, 'stroke-width': 1 }, gBg);
    // Тин і соняшник.
    for (let i = 0; i < 6; i++) el('line', { x1: -1.6 * U + i * 9, y1: gy, x2: -1.6 * U + i * 9, y2: gy - 0.26 * U, stroke: PAL.wood, 'stroke-width': 3, 'stroke-linecap': 'round' }, gBg);
    el('line', { x1: -1.65 * U, y1: gy - 0.16 * U, x2: -1.6 * U + 46, y2: gy - 0.16 * U, stroke: shade(PAL.wood, 0.8), 'stroke-width': 2 }, gBg);
    for (const sx of [2.6, 3.1]) {
      el('line', { x1: sx * U, y1: gy, x2: sx * U, y2: gy - 0.55 * U, stroke: '#4c7a2c', 'stroke-width': 2.5 }, gBg);
      el('circle', { cx: sx * U, cy: gy - 0.6 * U, r: 7, fill: '#f2c14e', stroke: '#7d5a1c', 'stroke-width': 3 }, gBg);
    }
    // Журавель над шахтою: стовп, жердина, відро на мотузці.
    const wx = g.start[0] * U;
    el('rect', { x: wx - 0.3 * U, y: gy - 0.2 * U, width: 0.6 * U, height: 0.2 * U, fill: PAL.wood, stroke: INK, 'stroke-width': 1.5 }, gBg);
    el('line', { x1: wx + 0.75 * U, y1: gy, x2: wx + 0.75 * U, y2: gy - 0.75 * U, stroke: shade(PAL.wood, 0.8), 'stroke-width': 5, 'stroke-linecap': 'round' }, gBg);
    el('line', { x1: wx + 1.45 * U, y1: gy - 0.35 * U, x2: wx - 0.05 * U, y2: gy - 1.0 * U, stroke: PAL.wood, 'stroke-width': 3.5, 'stroke-linecap': 'round' }, gBg);
    el('line', { x1: wx - 0.03 * U, y1: gy - 0.99 * U, x2: wx, y2: gy - 0.35 * U, stroke: '#cdb68a', 'stroke-width': 1.2 }, gBg);
    el('rect', { x: wx - 6, y: gy - 0.38 * U, width: 12, height: 10, fill: '#6d6a66', stroke: INK, 'stroke-width': 1 }, gBg);
    // Шари ґрунту: клітинка — 3×3 грудки з легким шумом відтінку.
    for (let y = g.y0; y <= g.y1; y++) for (let x = g.x0; x <= g.x1; x++) {
      const base = strata(y);
      el('rect', { x: (x - 0.5) * U, y: (y - 0.5) * U, width: U, height: U, fill: base }, gBg);
      for (let i = 0; i < 3; i++) for (let j = 0; j < 3; j++) {
        const k = 0.9 + noise(x * 3 + i, y * 3 + j) * 0.18;
        el('rect', { x: (x - 0.5) * U + i * U / 3 + 1, y: (y - 0.5) * U + j * U / 3 + 1, width: U / 3 - 2, height: U / 3 - 2, rx: 3, fill: shade(base, k) }, gBg);
      }
    }
    // Шахта над колодязем: зруб, драбина.
    for (const [sx, sy] of g.shaft || []) {
      const [cx, cy] = C(sx, sy);
      el('rect', { x: cx - TW / 2 - 4, y: cy - U / 2, width: TW + 8, height: U, fill: shade(PAL.wood, 0.55) }, gBg);
      el('rect', { x: cx - TW / 2, y: cy - U / 2, width: TW, height: U, fill: PAL.tunnel }, gBg);
      for (let k = 0; k < 3; k++) el('line', { x1: cx - TW / 2 + 3, x2: cx + TW / 2 - 3, y1: cy - U / 2 + 10 + k * 20, y2: cy - U / 2 + 10 + k * 20, stroke: PAL.wood, 'stroke-width': 3 }, gBg);
    }
    el('rect', { x: L, y: T, width: R - L, height: B - T, fill: 'none', stroke: 'rgba(0,0,0,.35)', 'stroke-width': 2 }, gBg);
    st.fit = null;
    fitView();
  }

  // ─── Вид: сітка viewBox ─────────────────────────────────────────────────
  function fitBox() {
    const g = st.grid || GRID;
    const W = host.clientWidth || 800, H = host.clientHeight || 500;
    const top = (g.y0 - 0.5 - SKY) * U, bot = (g.y1 + 0.5) * U;
    // Портрет (телефон) — по ширині x ∈ [−1,5; 9,5], як у 3D; ширше — усе поле.
    const portrait = H > W * 0.85;
    const x0 = portrait ? -1.5 * U : (g.x0 - 0.5) * U, x1 = portrait ? 9.5 * U : (g.x1 + 0.5) * U;
    return { x: x0, y: top, w: x1 - x0, h: bot - top };
  }
  function applyVb() {
    const b = st.vb;
    svg.setAttribute('viewBox', [b.x, b.y, b.w, b.h].map((n) => n.toFixed(1)).join(' '));
    svg.classList.toggle('zoomed', st.zoom > 1.02);
  }
  function fitView() { st.fit = fitBox(); st.vb = { ...st.fit }; st.zoom = 1; applyVb(); }
  function clampVb() {
    const g = st.grid || GRID, b = st.vb;
    const L = (g.x0 - 1) * U, R = (g.x1 + 1) * U, T = (g.y0 - 0.5 - SKY) * U, B = (g.y1 + 1) * U;
    if (b.w < R - L) b.x = Math.max(L, Math.min(R - b.w, b.x)); else b.x = (L + R) / 2 - b.w / 2;
    if (b.h < B - T) b.y = Math.max(T, Math.min(B - b.h, b.y)); else b.y = (T + B) / 2 - b.h / 2;
  }
  function zoomAt(k, px, py) {
    const f = st.fit || fitBox();
    const z = Math.max(0.6, Math.min(2.5, st.zoom * k));
    const real = st.zoom / z;
    const b = st.vb;
    const [wx, wy] = toWorld(px, py);
    b.w *= real; b.h *= real;
    b.x = wx - (wx - b.x) * real; b.y = wy - (wy - b.y) * real;
    st.zoom = z;
    if (Math.abs(z - 1) < 0.01) { st.vb = { ...f }; st.zoom = 1; }
    clampVb(); applyVb();
  }
  /** Пікселі host → координати viewBox. */
  function toWorld(px, py) {
    const m = svg.getScreenCTM();
    if (!m) return [0, 0];
    const r = host.getBoundingClientRect();
    const p = svg.createSVGPoint();
    p.x = px + r.left; p.y = py + r.top;
    const q = p.matrixTransform(m.inverse());
    return [q.x, q.y];
  }

  // ─── Карти поля ─────────────────────────────────────────────────────────
  function tunnel(g, x, y, m, c, opts2 = {}) {
    const [cx, cy] = C(x, y);
    const color = opts2.color || PAL.tunnel;
    for (const d of DIRS) {
      if (!(m & BIT[d])) continue;
      const [dx, dy] = STEP[d];
      const ex = cx + dx * U / 2, ey = cy + dy * U / 2;
      const sx = c ? cx : cx + dx * U * 0.2, sy = c ? cy : cy + dy * U * 0.2;
      el('line', { x1: sx, y1: sy, x2: ex, y2: ey, stroke: color, 'stroke-width': TW, 'stroke-linecap': 'butt' }, g);
      if (!opts2.ghost) {
        // Кріплення: два стовпчики біля краю.
        const px = dy !== 0 ? TW / 2 : 0, py = dx !== 0 ? TW / 2 : 0;
        const bx = cx + dx * U * 0.38, by = cy + dy * U * 0.38;
        el('line', { x1: bx - px, y1: by - py, x2: bx + px, y2: by + py, stroke: PAL.wood, 'stroke-width': 2, opacity: 0.55 }, g);
      }
    }
    if (c && m) el('rect', { x: cx - TW / 2, y: cy - TW / 2, width: TW, height: TW, fill: color }, g);
    if (!c) {
      el('circle', { cx, cy, r: 0.19 * U, fill: '#8d8f94', stroke: '#4a4b4f', 'stroke-width': 2 }, g);
      el('circle', { cx: cx - 4, cy: cy - 4, r: 0.06 * U, fill: '#b5b7bb' }, g);
    }
  }

  function drawCards() {
    gCards.textContent = '';
    const v = st.view;
    if (!v) return;
    const linked = new Set((v.linked || []).map(([x, y]) => x + ',' + y));
    for (const b of v.board || []) {
      const g = el('g', { 'data-c': b.x + ',' + b.y }, gCards);
      if (b.s === 'start') {
        tunnel(g, b.x, b.y, b.m, true);
        const [cx, cy] = C(b.x, b.y);
        el('rect', { x: cx - TW / 2, y: cy - U / 2, width: TW, height: U / 2, fill: PAL.tunnel }, g);
        el('circle', { cx, cy, r: 0.25 * U, fill: 'none', stroke: PAL.wood, 'stroke-width': 5 }, g);
        el('circle', { cx, cy, r: 0.1 * U, fill: '#2d4a6b' }, g);
        continue;
      }
      tunnel(g, b.x, b.y, b.m, b.c);
      if (b.c) {
        const [cx, cy] = C(b.x, b.y);
        const on = linked.has(b.x + ',' + b.y);
        if (on) el('circle', { cx, cy: cy - 3, r: 0.22 * U, fill: PAL.lamp, opacity: 0.18 }, g);
        el('circle', { cx, cy: cy - 3, r: 5, fill: on ? PAL.lamp : '#6f5a33', stroke: INK, 'stroke-width': 1 }, g);
      }
    }
  }

  function chest(g, cx, cy, s = 1) {
    el('rect', { x: cx - 13 * s, y: cy - 4 * s, width: 26 * s, height: 16 * s, rx: 2, fill: '#7a4a22', stroke: INK, 'stroke-width': 1.5 }, g);
    el('path', { d: `M${cx - 13 * s},${cy - 4 * s} Q${cx},${cy - 16 * s} ${cx + 13 * s},${cy - 4 * s} Z`, fill: '#8f5a2a', stroke: INK, 'stroke-width': 1.5 }, g);
    el('rect', { x: cx - 13 * s, y: cy + 1 * s, width: 26 * s, height: 3 * s, fill: '#c9a24a' }, g);
    el('circle', { cx, cy: cy + 4 * s, r: 3 * s, fill: PAL.gold, stroke: INK, 'stroke-width': 1 }, g);
    for (const [dx, dy] of [[-8, -7], [0, -10], [8, -7], [-4, -12], [4, -12]]) el('circle', { cx: cx + dx * s, cy: cy + dy * s, r: 3.4 * s, fill: PAL.gold, stroke: '#8a6a1a', 'stroke-width': 1 }, g);
  }
  function stones(g, cx, cy, s = 1) {
    for (const [dx, dy, r] of [[-9, 6, 7], [7, 7, 8], [0, -3, 8], [-12, -4, 5], [11, -3, 5]]) el('circle', { cx: cx + dx * s, cy: cy + dy * s, r: r * s, fill: PAL.stone, stroke: '#55575b', 'stroke-width': 1.5 }, g);
  }

  function drawGoals() {
    gGoals.textContent = '';
    const v = st.view;
    if (!v) return;
    const peeks = (v.me && v.me.peeks) || {};
    for (const gl of v.goals || []) {
      const g = el('g', { 'data-g': gl.i }, gGoals);
      const [cx, cy] = C(gl.x, gl.y);
      if (gl.open) {
        tunnel(g, gl.x, gl.y, gl.m, true);
        if (gl.kind === 'gold') { el('circle', { cx, cy, r: 0.48 * U, fill: PAL.gold, opacity: 0.22 }, g); chest(g, cx, cy + 4, 1.05); }
        else stones(g, cx, cy, 1);
        continue;
      }
      el('rect', { x: cx - 0.4 * U, y: cy - 0.42 * U, width: 0.8 * U, height: 0.84 * U, rx: 6, fill: PAL.wood, stroke: shade(PAL.wood, 0.5), 'stroke-width': 2.5 }, g);
      for (let k = 1; k < 3; k++) el('line', { x1: cx - 0.4 * U + 3, x2: cx + 0.4 * U - 3, y1: cy - 0.42 * U + k * 0.28 * U, y2: cy - 0.42 * U + k * 0.28 * U, stroke: shade(PAL.wood, 0.7), 'stroke-width': 1.5 }, g);
      if (gl.kind) {
        // Кінець раунду: видно, що там було, хоч мітку й не відкопали.
        el('rect', { x: cx - 0.4 * U, y: cy - 0.42 * U, width: 0.8 * U, height: 0.84 * U, rx: 6, fill: 'rgba(0,0,0,.35)' }, g);
        if (gl.kind === 'gold') chest(g, cx, cy + 4, 0.9); else stones(g, cx, cy, 0.85);
      } else {
        el('path', { d: `M${cx - 11},${cy - 11} L${cx + 11},${cy + 11} M${cx + 11},${cy - 11} L${cx - 11},${cy + 11}`, stroke: '#3a2412', 'stroke-width': 5, 'stroke-linecap': 'round' }, g);
        for (const [nx, ny] of [[-0.3, -0.32], [0.3, -0.32], [0, 0.33]]) el('circle', { cx: cx + nx * U, cy: cy + ny * U, r: 2.2, fill: '#c8c2b6', stroke: INK, 'stroke-width': .8 }, g);
      }
      const pk = peeks[gl.i];
      if (pk && !gl.open) {
        const bx = cx + 0.33 * U, by = cy - 0.35 * U;
        el('circle', { cx: bx, cy: by, r: 10, fill: pk === 'gold' ? PAL.gold : PAL.stone, stroke: '#fff', 'stroke-width': 2 }, g);
        txt(g, bx, by + 4, pk === 'gold' ? '★' : '·', { 'font-size': 13, 'font-weight': 800, fill: INK });
        const t = el('title', null, g); t.textContent = pk === 'gold' ? 'Ти бачив: тут скарб' : 'Ти бачив: тут каміння';
      }
    }
  }

  // ─── Фігурки ────────────────────────────────────────────────────────────
  const OFF = [[-0.27, -0.27], [0.27, -0.27], [-0.27, 0.27], [0.27, 0.27]];
  function figPos() {
    const v = st.view, out = new Map();
    if (!v) return out;
    const at = new Map();
    for (const b of v.board || []) if (b.by != null) at.set(b.by, [b.x, b.y]);
    const per = new Map(), top = [];
    for (const p of v.players || []) {
      const c = at.get(p.seat);
      if (!c) { top.push(p.seat); continue; }
      const k = c.join();
      const n = per.get(k) || 0;
      per.set(k, n + 1);
      const off = OFF[n] || [0, 0.05 * (n - 3)];
      out.set(p.seat, [(c[0] + off[0]) * U, (c[1] + off[1]) * U, n >= 4]);
    }
    const g = st.grid || GRID, gy = (g.y0 - 0.5) * U - 0.24 * U;
    top.forEach((s, i) => out.set(s, [(g.start[0] - 0.9 - i * 0.32) * U, gy - 8, false]));
    return out;
  }
  function drawFigs() {
    gFig.textContent = '';
    const v = st.view;
    if (!v) return;
    const pos = figPos();
    for (const p of v.players || []) {
      const q = pos.get(p.seat);
      if (!q) continue;
      const [x, y, behind] = q;
      const g = el('g', { 'data-s': p.seat, opacity: behind ? 0.6 : 1 }, gFig);
      const col = SEAT_COLOR[p.color] || '#999';
      el('circle', { cx: x, cy: y + 2, r: 8.5, fill: 'rgba(0,0,0,.35)' }, g);
      el('circle', { cx: x, cy: y, r: 8.5, fill: col, stroke: '#fff', 'stroke-width': 2 }, g);
      el('path', { d: `M${x - 7},${y - 5} L${x + 7},${y - 5} L${x + 4},${y - 9} L${x - 4},${y - 9} Z`, fill: '#e8d39a', stroke: INK, 'stroke-width': .8 }, g);
      if ((p.broken || []).length) el('path', { d: `M${x - 6},${y + 6} L${x + 6},${y - 6}`, stroke: PAL.bad, 'stroke-width': 3, 'stroke-linecap': 'round' }, g);
      const t = el('title', null, g); t.textContent = p.nick + ((p.broken || []).length ? ' — щось зламано' : '');
    }
  }

  function setState(view) {
    st.view = view ? { ...view, board: (view.board || []).slice(), goals: (view.goals || []).map((g) => ({ ...g })), me: view.me ? { ...view.me, peeks: { ...(view.me.peeks || {}) } } : null } : null;
    if (view && view.grid) setGrid(view.grid); else if (!st.grid) setGrid(GRID);
    const sig = view ? JSON.stringify([view.board, view.linked, view.goals, (view.players || []).map((p) => [p.seat, p.color, p.broken, p.nick]), view.me && view.me.peeks]) : '';
    if (sig === st.sig) return;
    st.sig = sig;
    redraw();
  }
  function redraw() { drawCards(); drawGoals(); drawFigs(); }

  // ─── Підсвітка, привид, курсор ──────────────────────────────────────────
  function setLegal(l) {
    st.legal = l && l.kind ? { kind: l.kind, cells: l.cells || [], alt: l.alt || [], goals: l.goals || [], ghost: l.ghost || null } : null;
    gLegal.textContent = ''; gGhost.textContent = '';
    st.hot = null;
    const L = st.legal;
    if (!L) return;
    const box = (cls, x, y, tag) => {
      const [cx, cy] = C(x, y);
      const g = el('g', { class: 's2-leg ' + cls, 'data-t': tag }, gLegal);
      el('rect', { x: cx - U / 2 + 3, y: cy - U / 2 + 3, width: U - 6, height: U - 6, rx: 7 }, g);
      if (cls === 'alt') txt(g, cx + U / 2 - 12, cy - U / 2 + 21, '⟲', { class: 's2-alt' });
      return g;
    };
    if (L.kind === 'place') { for (const [x, y] of L.cells) box('', x, y, 'c' + x + ',' + y); for (const [x, y] of L.alt) box('alt', x, y, 'a' + x + ',' + y); }
    else if (L.kind === 'rockfall') for (const [x, y] of L.cells) box('rock', x, y, 'c' + x + ',' + y);
    else if (L.kind === 'goal') {
      for (const i of L.goals) { const gl = (st.view && st.view.goals || []).find((g) => g.i === i) || { x: GRID.goals[i][0], y: GRID.goals[i][1] }; box('goal', gl.x, gl.y, 'g' + i); }
    }
  }

  /** Найближча підсвічена ціль у межах 0,7 клітинки від точки (world). */
  function targetAt(wx, wy) {
    const L = st.legal;
    if (!L) return null;
    let best = null, bd = 0.7 * U;
    const test = (x, y, t) => { const d = Math.hypot(wx - x * U, wy - y * U); if (d < bd) { bd = d; best = t; } };
    if (L.kind === 'goal') for (const i of L.goals) { const gl = (st.view && st.view.goals || []).find((g) => g.i === i); if (gl) test(gl.x, gl.y, { kind: 'goal', i, tag: 'g' + i }); }
    else {
      for (const [x, y] of L.cells) test(x, y, { kind: 'cell', x, y, tag: 'c' + x + ',' + y, alt: false });
      for (const [x, y] of L.alt) test(x, y, { kind: 'cell', x, y, tag: 'a' + x + ',' + y, alt: true });
    }
    return best;
  }

  function setHot(t) {
    const tag = t ? t.tag : null;
    if ((st.hot && st.hot.tag) === tag) return;
    if (st.hot) { const g = gLegal.querySelector('[data-t="' + st.hot.tag + '"]'); if (g) g.classList.remove('hot'); }
    st.hot = t;
    gGhost.textContent = '';
    if (t) {
      const g = gLegal.querySelector('[data-t="' + tag + '"]');
      if (g) g.classList.add('hot');
      const L = st.legal;
      if (L && L.kind === 'place' && L.ghost && t.kind === 'cell') tunnel(gGhost, t.x, t.y, t.alt ? L.ghost.mAlt : L.ghost.m, L.ghost.c, { ghost: true, color: '#120b06' });
      if (t.kind === 'goal') o.onHover('goal', t.i); else o.onHover('cell', t.x, t.y);
    } else o.onHover(null);
  }

  function setCursor(x, y) {
    gCur.textContent = '';
    if (x == null) { st.cur = null; return; }
    st.cur = [x, y];
    const [cx, cy] = C(x, y);
    el('rect', { x: cx - U / 2 - 2, y: cy - U / 2 - 2, width: U + 4, height: U + 4, rx: 9 }, gCur);
    const t = targetAt(cx, cy);
    if (t) setHot(t);
  }

  // ─── Указівник: тап, тягнути, щипати, колесо ────────────────────────────
  const local = (e) => { const r = host.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  function onDown(e) {
    if (e.button != null && e.button > 0) return;
    const p = local(e);
    st.ptrs.set(e.pointerId, p);
    if (st.ptrs.size === 1) st.drag = { x: p[0], y: p[1], vb: { ...st.vb }, moved: 0, id: e.pointerId };
    else if (st.ptrs.size === 2) {
      const [a, b] = [...st.ptrs.values()];
      st.drag = { pinch: Math.hypot(a[0] - b[0], a[1] - b[1]), zoom: st.zoom, moved: 99 };
    }
    try { svg.setPointerCapture(e.pointerId); } catch (err) { /* старий браузер */ }
  }
  function onMove(e) {
    const p = local(e);
    if (st.ptrs.has(e.pointerId)) st.ptrs.set(e.pointerId, p);
    const d = st.drag;
    if (d && d.pinch && st.ptrs.size === 2) {
      const [a, b] = [...st.ptrs.values()];
      const dist = Math.hypot(a[0] - b[0], a[1] - b[1]);
      zoomAt((d.zoom * dist / d.pinch) / st.zoom, (a[0] + b[0]) / 2, (a[1] + b[1]) / 2);
      return;
    }
    if (d && st.ptrs.size === 1 && d.id === e.pointerId) {
      const dx = p[0] - d.x, dy = p[1] - d.y;
      d.moved = Math.max(d.moved, Math.hypot(dx, dy));
      if (d.moved >= 8) {
        const k = st.vb.w / (host.clientWidth || 1);
        st.vb.x = d.vb.x - dx * k; st.vb.y = d.vb.y - dy * k;
        clampVb(); applyVb();
        return;
      }
    }
    if (e.pointerType === 'mouse' || !e.pointerType) { const [wx, wy] = toWorld(p[0], p[1]); setHot(targetAt(wx, wy)); }
  }
  function onUp(e) {
    const d = st.drag, p = local(e);
    st.ptrs.delete(e.pointerId);
    if (st.ptrs.size === 0) st.drag = null;
    if (!d || d.pinch || d.id !== e.pointerId || d.moved >= 8 || e.type === 'pointercancel') return;
    const [wx, wy] = toWorld(p[0], p[1]);
    const t = targetAt(wx, wy);
    if (!t) return;
    if (t.kind === 'goal') o.onPick('goal', t.i); else o.onPick('cell', t.x, t.y);
  }
  function onLeave() { if (!st.ptrs.size) setHot(null); }
  function onWheel(e) {
    e.preventDefault();
    const [px, py] = local(e);
    zoomAt(Math.exp(-e.deltaY * 0.0015), px, py);
  }
  function onDbl() { fitView(); }
  svg.addEventListener('pointerdown', onDown);
  svg.addEventListener('pointermove', onMove);
  svg.addEventListener('pointerup', onUp);
  svg.addEventListener('pointercancel', onUp);
  svg.addEventListener('pointerleave', onLeave);
  svg.addEventListener('wheel', onWheel, { passive: false });
  svg.addEventListener('dblclick', onDbl);

  // ─── Анімації (короткі, ≤ 400 мс; сцена лишається в стані «після події») ─
  const restart = (node, cls) => { if (!node || o.reducedMotion) return; node.classList.remove(cls); void node.getBBox(); node.classList.add(cls); later(() => node.classList.remove(cls), 700); };

  async function dig(ev) {
    if (!st.view) return;
    st.view.board = st.view.board.filter((b) => b.x !== ev.x || b.y !== ev.y);
    st.view.board.push({ x: ev.x, y: ev.y, s: ev.s, r: ev.r, m: ev.m, c: ev.c, by: ev.seat, id: ev.card });
    if (!(st.view.linked || []).some(([x, y]) => x === ev.x && y === ev.y)) st.view.linked = [...(st.view.linked || []), [ev.x, ev.y]];
    st.sig = '';
    redraw();
    restart(gCards.querySelector('[data-c="' + ev.x + ',' + ev.y + '"]'), 's2-pop');
    dust(ev.x, ev.y, '#a77b4a', 6);
    await sleep(380);
  }

  async function flip(ev) {
    if (!st.view) return;
    const g = st.view.goals.find((x) => x.i === ev.i);
    if (g) Object.assign(g, { open: true, kind: ev.kind, s: ev.s, r: ev.r, m: ev.m, c: ev.c });
    st.sig = '';
    drawGoals();
    const node = gGoals.querySelector('[data-g="' + ev.i + '"]');
    restart(node, 's2-flip');
    if (ev.kind === 'gold') { coins(ev.x, ev.y, 10); await sleep(600); } else { dust(ev.x, ev.y, '#8d8f94', 5); await sleep(400); }
  }

  async function rockfall(ev) {
    if (!st.view) return;
    st.view.board = st.view.board.filter((b) => b.x !== ev.x || b.y !== ev.y);
    st.sig = '';
    redraw();
    if (!o.reducedMotion) {
      const [cx, cy] = C(ev.x, ev.y);
      for (let i = 0; i < 6; i++) {
        const g = el('g', { class: 's2-fall', style: 'animation-delay:' + i * 30 + 'ms; opacity:0' }, gFx);
        el('circle', { cx: cx + (noise(i, ev.x) - 0.5) * U * 0.7, cy: cy + (noise(ev.y, i) - 0.5) * U * 0.5, r: 6 + noise(i, i) * 6, fill: PAL.stone, stroke: '#4a4b4f', 'stroke-width': 1.5 }, g);
        later(() => g.remove(), 700);
      }
      restart(world, 's2-shake');
    }
    await sleep(400);
  }

  async function peek(ev, kind) {
    if (!st.view) return;
    const gl = st.view.goals.find((x) => x.i === ev.goal);
    if (!gl) return;
    const [cx, cy] = C(gl.x, gl.y);
    if (!o.reducedMotion) {
      const g = el('g', { class: 's2-peek' }, gFx);
      el('rect', { x: cx - 18, y: cy - U * 0.85, width: 36, height: 24, rx: 3, fill: PAL.map, stroke: '#8a6a3a', 'stroke-width': 1.5 }, g);
      el('path', { d: `M${cx - 12},${cy - U * 0.85 + 16} q8,-12 16,-2 t8,-6`, fill: 'none', stroke: PAL.bad, 'stroke-width': 1.6, 'stroke-dasharray': '3 2' }, g);
      later(() => g.remove(), 1050);
    }
    if (kind && st.view.me) { st.view.me.peeks = { ...(st.view.me.peeks || {}), [ev.goal]: kind }; st.sig = ''; drawGoals(); }
    await sleep(700);
  }

  async function finale(side) {
    if (o.reducedMotion) return;
    const v = st.view;
    if (side === 'diggers') {
      const gl = v && (v.goals || []).find((g) => g.kind === 'gold');
      const [x, y] = gl ? [gl.x, gl.y] : [8, 0];
      coins(x, y, 16);
    } else if (side === 'moles') {
      for (let i = 0; i < 6; i++) {
        const x = Math.round(1 + noise(i, 7) * 9), y = Math.round(-3 + noise(7, i) * 7);
        const [cx, cy] = C(x, y);
        const g = el('g', { class: 's2-mole', style: 'animation-delay:' + i * 80 + 'ms; opacity:0' }, gFx);
        el('ellipse', { cx, cy, rx: 13, ry: 10, fill: '#5a4a3e', stroke: INK, 'stroke-width': 1.5 }, g);
        el('circle', { cx, cy: cy + 3, r: 3, fill: '#e98fa0' }, g);
        el('circle', { cx: cx - 5, cy: cy - 3, r: 1.6, fill: '#111' }, g);
        el('circle', { cx: cx + 5, cy: cy - 3, r: 1.6, fill: '#111' }, g);
        later(() => g.remove(), 1500);
      }
    } else for (let i = 0; i < 4; i++) dust(2 + i * 2, 0, '#a77b4a', 5);
    await sleep(900);
  }

  function dust(x, y, color, n) {
    if (o.reducedMotion) return;
    const [cx, cy] = C(x, y);
    for (let i = 0; i < n; i++) {
      const a = (i / n) * Math.PI * 2 + noise(x, i);
      const g = el('g', { class: 's2-fly', style: '--dx:' + Math.cos(a) * 30 + 'px; --dy:' + Math.sin(a) * 30 + 'px; opacity:0' }, gFx);
      el('circle', { cx, cy, r: 4, fill: color, opacity: 0.8 }, g);
      later(() => g.remove(), 760);
    }
  }
  function coins(x, y, n) {
    if (o.reducedMotion) return;
    const [cx, cy] = C(x, y);
    for (let i = 0; i < n; i++) {
      const a = -Math.PI / 2 + (i / (n - 1) - 0.5) * 2.4;
      const r = 40 + noise(i, n) * 40;
      const g = el('g', { class: 's2-fly', style: '--dx:' + Math.cos(a) * r + 'px; --dy:' + Math.sin(a) * r + 'px; animation-delay:' + i * 25 + 'ms; opacity:0' }, gFx);
      el('circle', { cx, cy, r: 6, fill: PAL.gold, stroke: '#8a6a1a', 'stroke-width': 1.5 }, g);
      later(() => g.remove(), 1000);
    }
  }

  function screenPos(kind, a, b) {
    let p = null;
    if (kind === 'cell') p = C(a, b);
    else if (kind === 'goal') { const gl = (st.view && st.view.goals || []).find((g) => g.i === a) || (GRID.goals[a] && { x: GRID.goals[a][0], y: GRID.goals[a][1] }); if (gl) p = C(gl.x, gl.y); }
    else if (kind === 'seat') { const q = figPos().get(a); if (q) p = [q[0], q[1]]; }
    const m = svg.getScreenCTM();
    if (!p || !m) return null;
    const pt = svg.createSVGPoint();
    pt.x = p[0]; pt.y = p[1];
    const s = pt.matrixTransform(m);
    const r = host.getBoundingClientRect();
    return { x: s.x - r.left, y: s.y - r.top };
  }

  function focus() { /* пласка мапа камерою не їздить: людина бачить усе поле */ }
  function resize() { if (st.zoom === 1) fitView(); }

  function dispose() {
    st.dead = true;
    for (const t of st.timers) clearTimeout(t);
    st.timers.clear();
    svg.removeEventListener('pointerdown', onDown);
    svg.removeEventListener('pointermove', onMove);
    svg.removeEventListener('pointerup', onUp);
    svg.removeEventListener('pointercancel', onUp);
    svg.removeEventListener('pointerleave', onLeave);
    svg.removeEventListener('wheel', onWheel);
    svg.removeEventListener('dblclick', onDbl);
    svg.remove();
  }

  setGrid(GRID);
  return {
    kind: '2d',
    setGrid, setState, setLegal, setCursor, dig, flip, rockfall, peek, finale, focus, screenPos,
    resetView: fitView, resize, dispose,
  };
}
