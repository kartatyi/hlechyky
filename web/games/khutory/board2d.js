// «Хутори» — пласка карта згори (SVG). Той самий API, що й 3D-сцена (docs/games/specs/khutory.md, §10):
// запасний вигляд для слабких пристроїв / без WebGL і вибір «🗺 Пласка». Правил не знає, дій не шле —
// лише малює вид і каже картці, куди клацнули (лише по підсвічених місцях).

// Сусідні модулі — з тим самим ?v=, що й у цього файла: сайт оновлює гру без F5, а карта модулів сторінки
// тримає старі './x.js' назавжди; із відбитком у адресі після деплою підтягнеться нове (khutory.js, modQ).
const Q = new URL(import.meta.url).search;
const { TERRAIN_COLOR, RES_COLOR, SEA_COLOR, SEAT_COLOR, PIPS, TERRAIN_NAME, RES_NAME } = await import('./consts.js' + Q);

// Мова підсвітки — та сама, що в 3D (khutory-look.md §3): золото #ffd23f з темним кантом #2a1a00, «мурашки» по шляхах.

const NS = 'http://www.w3.org/2000/svg';
const U = 60;   // пікселів viewBox на одиницю геометрії (радіус гекса = 1)
const TERRAIN_ICON = { forest: '🌲', pit: '🏺', pasture: '🐑', field: '🌾', mountain: '⛰️', steppe: '🗿' };
const RES_ICON = { wood: '🪵', clay: '🧱', wool: '🧶', grain: '🌾', ore: '⛏️' };
const INK = '#2a1e10';

const CSS = `
.k2 { width: 100%; height: 100%; display: block; touch-action: manipulation; user-select: none; -webkit-user-select: none; }
.k2 text { pointer-events: none; font-family: var(--display, system-ui), system-ui, sans-serif; }
.k2-hex { stroke: #ead9ac; stroke-width: 3; stroke-linejoin: round; }
.k2-tok { fill: #f6ead0; stroke: #8a6a3a; stroke-width: 1.5; }
.k2-robbed { opacity: .38; }
.k2-fig { transform-box: fill-box; transform-origin: center; }
.k2-fig.pop { animation: k2pop .55s cubic-bezier(.3,1.6,.5,1); }
@keyframes k2pop { 0% { transform: scale(0); } 60% { transform: scale(1.35); } 100% { transform: scale(1); } }
.k2-rob { transition: transform .45s cubic-bezier(.3,1.4,.5,1); }
.k2-leg { cursor: pointer; }
.k2-leg .halo { fill: none; stroke: rgba(255,210,63,.38); stroke-width: 10; }
.k2-leg .kant { fill: none; stroke: #2a1a00; stroke-width: 9; }
.k2-leg .ring { fill: #fff4c2; stroke: #ffd23f; stroke-width: 4.5; transform-box: fill-box; transform-origin: center;
  animation: k2pulse 1.1s ease-in-out infinite alternate; }
.k2-leg.city .ring { fill: rgba(255,244,194,.3); }
.k2-leg line.halo { stroke: rgba(255,210,63,.45); stroke-width: 26; stroke-linecap: round; }
.k2-leg line.kant { stroke: #2a1a00; stroke-width: 15; stroke-linecap: round; }
.k2-leg .bar { stroke: #ffd23f; stroke-width: 9; stroke-linecap: round; }
.k2-leg .ants { stroke: #fff; stroke-width: 3; stroke-linecap: round; stroke-dasharray: 7 9; animation: k2march 1s linear infinite; }
.k2-leg polygon.kant { fill: none; stroke: #2a1a00; stroke-width: 10; stroke-linejoin: round; }
.k2-leg .area { fill: rgba(255,210,63,.16); stroke: #ffd23f; stroke-width: 5; stroke-linejoin: round; stroke-dasharray: 14 8; animation: k2march 1.2s linear infinite; }
.k2-leg .hit { fill: transparent; stroke: transparent; }
.k2-leg:hover .ring, .k2-leg.hot .ring { fill: #fff; stroke-width: 6; animation: none; transform: scale(1.35); }
.k2-leg:hover .bar, .k2-leg.hot .bar { stroke-width: 12; }
.k2-leg:hover line.kant, .k2-leg.hot line.kant { stroke-width: 19; }
.k2-leg:hover .area, .k2-leg.hot .area { fill: rgba(255,210,63,.36); stroke-dasharray: none; }
.k2-show { fill: rgba(255,210,63,.3); stroke: #ffd23f; stroke-width: 5; stroke-linejoin: round; pointer-events: none; animation: k2breathe 1.6s ease-in-out infinite alternate; }
@keyframes k2breathe { from { fill-opacity: .55; } to { fill-opacity: 1; } }
@keyframes k2pulse { from { transform: scale(.85); } to { transform: scale(1.15); } }
@keyframes k2march { to { stroke-dashoffset: -32; } }
.k2-fly { animation: k2fly .95s ease-out forwards; }
@keyframes k2fly { 0% { transform: translate(0,0) scale(.6); opacity: 0; } 20% { opacity: 1; transform: translate(0,-12px) scale(1.1); }
  100% { transform: translate(0,-70px) scale(1); opacity: 0; } }
.k2-hexflash { animation: k2flash .9s ease-out; }
@keyframes k2flash { 0%,100% { stroke: #ead9ac; } 30% { stroke: #fff6c8; stroke-width: 7; } }
.k2-dice { position: absolute; right: 10px; top: 10px; display: flex; gap: 6px; pointer-events: none; z-index: 3; }
.k2-die { width: 38px; height: 38px; border-radius: 8px; background: #fbf6ea; box-shadow: 0 3px 8px rgba(0,0,0,.35), inset 0 -3px 0 rgba(0,0,0,.12);
  display: grid; grid-template: repeat(3, 1fr) / repeat(3, 1fr); padding: 5px; box-sizing: border-box; }
.k2-die i { width: 7px; height: 7px; border-radius: 50%; background: #2a1e10; place-self: center; visibility: hidden; }
.k2-die i.on { visibility: visible; }
.k2-die.red i { background: #b8322c; }
.k2-dice.spin .k2-die { animation: k2spin .18s linear infinite; }
.k2-dice.land .k2-die { animation: k2land .35s ease-out; }
@keyframes k2spin { from { transform: rotate(0); } to { transform: rotate(90deg); } }
@keyframes k2land { 0% { transform: scale(1.3); } 100% { transform: scale(1); } }
@media (prefers-reduced-motion: reduce) { .k2-leg .ring, .k2-leg .ants, .k2-leg .area, .k2-show { animation: none; } }
`;

const PIP_CELLS = { 1: [4], 2: [0, 8], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };

function el(tag, attrs, parent) {
  const e = document.createElementNS(NS, tag);
  if (attrs) for (const k in attrs) e.setAttribute(k, attrs[k]);
  if (parent) parent.appendChild(e);
  return e;
}

function darker(hex, k = 0.55) {
  const n = parseInt(hex.slice(1), 16);
  const c = (s) => Math.round(((n >> s) & 255) * k);
  return 'rgb(' + c(16) + ',' + c(8) + ',' + c(0) + ')';
}

// Тло пласкої карти — у тон 3D-тла (scene.js BG_VARIANTS / BG_DEFAULT): темна стільниця під лампою — світло посередині,
// краї в тінь; «море» — синя глибина. opts.bg — назва варіанта; без неї — темне дерево (типове «lamp»).
const DARK_WOOD_BG = 'radial-gradient(ellipse 72% 68% at 50% 50%, #4a3020 0%, #2f1e12 55%, #0f0905 100%)';
const BG2D = {
  lamp: DARK_WOOD_BG, rushnyk: DARK_WOOD_BG, khata: DARK_WOOD_BG,
  sea: 'radial-gradient(ellipse 72% 68% at 50% 50%, #3b6d90 0%, #27495f 55%, #1a2f3f 100%)',
  planks: '',   // старе: тло рамки поля з khutory.css
};

export async function createBoard2d(host, opts = {}) {
  const o = { onPick() {}, onHover() {}, reducedMotion: false, ...opts };
  const st = { board: null, boardId: null, view: null, figSig: '', legal: null, timers: new Set(), dead: false, centers: [], hot: null };

  const svg = el('svg', { class: 'k2', preserveAspectRatio: 'xMidYMid meet', role: 'img', 'aria-label': 'Карта хуторів' });
  el('style', null, svg).textContent = CSS;
  svg.style.background = BG2D[o.bg] ?? DARK_WOOD_BG;
  const gSea = el('g', null, svg), gPorts = el('g', null, svg), gHex = el('g', null, svg), gTok = el('g', null, svg);
  const gRoads = el('g', null, svg), gTowns = el('g', null, svg), gRob = el('g', { class: 'k2-rob' }, svg);
  const gLegal = el('g', null, svg), gFx = el('g', null, svg);
  const gShow = el('g', null);   // glowHexes — над землями, під жетонами
  svg.insertBefore(gShow, gTok);
  host.appendChild(svg);
  if (getComputedStyle(host).position === 'static') host.style.position = 'relative';

  const dice = document.createElement('div');
  dice.className = 'k2-dice';
  dice.hidden = true;
  dice.innerHTML = '<div class="k2-die"></div><div class="k2-die red"></div>';
  host.appendChild(dice);

  const later = (fn, ms) => { const t = setTimeout(() => { st.timers.delete(t); if (!st.dead) fn(); }, ms); st.timers.add(t); return t; };
  const sleep = (ms) => new Promise((res) => later(res, ms));

  const P = (xy) => [xy[0] * U, xy[1] * U];
  const pts = (list) => list.map((p) => p[0].toFixed(1) + ',' + p[1].toFixed(1)).join(' ');
  const vxy = (v) => P(st.board.vx[v]);
  const exy = (e) => { const [a, b] = st.board.edges[e]; const A = vxy(a), B = vxy(b); return [(A[0] + B[0]) / 2, (A[1] + B[1]) / 2]; };
  const hxy = (h) => st.centers[h];
  const posOf = (kind, id) => (kind === 'v' ? vxy(id) : kind === 'e' ? exy(id) : hxy(id));

  // ─── Поле ────────────────────────────────────────────────────────────────
  function setBoard(board) {
    if (!board || board.id === st.boardId) return;
    st.board = board; st.boardId = board.id; st.figSig = '';
    for (const g of [gSea, gPorts, gHex, gTok, gRoads, gTowns, gRob, gLegal, gFx, gShow]) g.textContent = '';
    st.centers = board.hexes.map((h) => {
      let x = 0, y = 0;
      for (const v of h.v) { x += board.vx[v][0]; y += board.vx[v][1]; }
      return [x / 6 * U, y / 6 * U];
    });
    let R = 0;
    for (const p of board.vx) R = Math.max(R, Math.hypot(p[0], p[1]));
    const sea = (R + 1.25) * U;
    const vb = sea + 4;
    svg.setAttribute('viewBox', [-vb, -vb * 0.94, vb * 2, vb * 1.88].map((n) => n.toFixed(0)).join(' '));
    // Море — пласкодонний шестикутник довкола острова.
    const seaPts = [];
    for (let i = 0; i < 6; i++) { const a = Math.PI / 3 * i; seaPts.push([Math.cos(a) * sea, Math.sin(a) * sea]); }
    el('polygon', { points: pts(seaPts), fill: SEA_COLOR, stroke: '#3f6f9f', 'stroke-width': 6, 'stroke-linejoin': 'round' }, gSea);
    el('polygon', { points: pts(seaPts.map(([x, y]) => [x * 0.93, y * 0.93])), fill: 'none', stroke: 'rgba(255,255,255,.18)',
      'stroke-width': 3, 'stroke-dasharray': '18 14' }, gSea);

    // Пристані: місток від двох вершин ребра до таблички курсу в морі.
    for (const p of board.ports || []) {
      const [a, b] = board.edges[p.e];
      const A = vxy(a), B = vxy(b), M = exy(p.e);
      const d = Math.hypot(M[0], M[1]) || 1;
      const L = [M[0] + M[0] / d * 0.62 * U, M[1] + M[1] / d * 0.62 * U];
      for (const X of [A, B]) el('line', { x1: X[0], y1: X[1], x2: L[0], y2: L[1], stroke: '#8a6a3a', 'stroke-width': 7, 'stroke-linecap': 'round' }, gPorts);
      const g = el('g', null, gPorts);
      const title = el('title', null, g);
      title.textContent = p.kind === 'any' ? 'Пристань 3:1 — будь-які 3 однакові за 1' : 'Пристань 2:1 — ' + RES_NAME[p.kind] + ' 2 за 1';
      el('circle', { cx: L[0], cy: L[1], r: 0.33 * U, fill: p.kind === 'any' ? '#f6ead0' : RES_COLOR[p.kind], stroke: '#5a3e1c', 'stroke-width': 3 }, g);
      if (p.kind === 'any') {
        const t = el('text', { x: L[0], y: L[1] + 6, 'text-anchor': 'middle', 'font-size': 18, 'font-weight': 800, fill: INK }, g);
        t.textContent = (p.rate || 3) + ':1';
      } else {
        const t = el('text', { x: L[0], y: L[1] - 1, 'text-anchor': 'middle', 'font-size': 17 }, g);
        t.textContent = RES_ICON[p.kind];
        const t2 = el('text', { x: L[0], y: L[1] + 15, 'text-anchor': 'middle', 'font-size': 12, 'font-weight': 800, fill: INK }, g);
        t2.textContent = (p.rate || 2) + ':1';
      }
    }

    board.hexes.forEach((h, i) => {
      const poly = el('polygon', { class: 'k2-hex', points: pts(h.v.map(vxy)), fill: TERRAIN_COLOR[h.t] || '#999', 'data-h': i }, gHex);
      const tt = el('title', null, poly);
      tt.textContent = TERRAIN_NAME[h.t] + (h.num ? ' · ' + h.num : '');
      const [cx, cy] = st.centers[i];
      const g = el('g', { 'data-tok': i }, gTok);
      const ic = el('text', { x: cx, y: cy - 0.42 * U, 'text-anchor': 'middle', 'font-size': 0.4 * U }, g);
      ic.textContent = TERRAIN_ICON[h.t] || '';
      if (h.num) {
        const red = h.num === 6 || h.num === 8;
        el('circle', { class: 'k2-tok', cx, cy: cy + 0.16 * U, r: 0.34 * U }, g);
        const t = el('text', { x: cx, y: cy + 0.22 * U, 'text-anchor': 'middle', 'font-size': (red ? 0.33 : 0.3) * U, 'font-weight': 800,
          fill: red ? '#c0312b' : INK }, g);
        t.textContent = h.num;
        const n = PIPS[h.num] || 0;
        for (let k = 0; k < n; k++) {
          el('circle', { cx: cx + (k - (n - 1) / 2) * 6, cy: cy + 0.36 * U, r: 2.3, fill: red ? '#c0312b' : INK }, g);
        }
      }
    });
    st.legal && setLegal(st.legal);
    if (st.shown) glowHexes(st.shown);
  }

  // ─── Фігури ──────────────────────────────────────────────────────────────
  const colorOf = (seat) => {
    const p = (st.view && st.view.players || []).find((x) => x.seat === seat);
    return SEAT_COLOR[p ? p.color : seat] || '#ccc';
  };
  const shapeOf = (seat) => {
    const p = (st.view && st.view.players || []).find((x) => x.seat === seat);
    return ((p ? p.color : seat) % 4 + 4) % 4;
  };
  /** Фігура гравця ●▲■◆ (як SEAT_SHAPE) з центром (x, y) і «радіусом» r. */
  function shape(g, k, x, y, r, attrs) {
    if (k === 0) return el('circle', { cx: x, cy: y, r: r * 0.92, ...attrs }, g);
    const d = k === 1 ? `M${x},${y - r * 1.05} L${x + r * 1.05},${y + r * 0.8} L${x - r * 1.05},${y + r * 0.8} Z`
      : k === 2 ? `M${x - r * 0.82},${y - r * 0.82} h${r * 1.64} v${r * 1.64} h${-r * 1.64} Z`
      : `M${x},${y - r * 1.15} L${x + r},${y} L${x},${y + r * 1.15} L${x - r},${y} Z`;
    return el('path', { d, 'stroke-linejoin': 'round', ...attrs }, g);
  }
  /** Прапорець на даху: держак від гребеня й табличка-фігура в кольорі гравця з темним кантом. */
  function flag(g, x, y, c, k) {
    const s = U / 60;
    el('line', { x1: x, y1: y, x2: x, y2: y - 13 * s, stroke: INK, 'stroke-width': 1.8, 'stroke-linecap': 'round' }, g);
    shape(g, k, x + 6.5 * s, y - 13 * s, 5.6 * s, { fill: c, stroke: INK, 'stroke-width': 2 });
  }

  function house(g, x, y, c, city, k) {
    const s = U / 60;
    if (city) {
      el('path', { d: `M${x - 17 * s},${y + 12 * s} V${y - 4 * s} L${x - 8 * s},${y - 12 * s} L${x + 1 * s},${y - 4 * s} V${y - 9 * s}`
        + ` L${x + 9 * s},${y - 19 * s} L${x + 17 * s},${y - 9 * s} V${y + 12 * s} Z`, fill: c, stroke: INK, 'stroke-width': 2.4, 'stroke-linejoin': 'round' }, g);
      el('rect', { x: x - 12 * s, y: y + 1 * s, width: 6 * s, height: 7 * s, fill: '#fbf2d5', stroke: INK, 'stroke-width': 1.2 }, g);
      el('rect', { x: x + 6 * s, y: y - 4 * s, width: 6 * s, height: 7 * s, fill: '#fbf2d5', stroke: INK, 'stroke-width': 1.2 }, g);
      el('circle', { cx: x + 9 * s, cy: y - 21 * s, r: 2.5 * s, fill: '#f4c542', stroke: INK, 'stroke-width': 1 }, g);
      flag(g, x - 8 * s, y - 12 * s, c, k);
    } else {
      el('rect', { x: x - 11 * s, y: y - 3 * s, width: 22 * s, height: 14 * s, fill: '#fbf2d5', stroke: INK, 'stroke-width': 2.2 }, g);
      el('path', { d: `M${x - 15 * s},${y - 1 * s} L${x},${y - 15 * s} L${x + 15 * s},${y - 1 * s} Z`, fill: c, stroke: INK, 'stroke-width': 2.2,
        'stroke-linejoin': 'round' }, g);
      el('rect', { x: x - 3 * s, y: y + 3 * s, width: 6 * s, height: 8 * s, fill: c, stroke: INK, 'stroke-width': 1 }, g);
      flag(g, x, y - 15 * s, c, k);
    }
  }

  function setState(view) {
    st.view = view;
    if (view && view.board && view.board.id !== st.boardId) setBoard(view.board);
    if (!st.board || !view) return;
    const sig = JSON.stringify([view.towns, view.roads, (view.players || []).map((p) => p.color)]);
    if (sig !== st.figSig) {
      st.figSig = sig;
      gRoads.textContent = ''; gTowns.textContent = '';
      for (const r of view.roads || []) {
        const [a, b] = st.board.edges[r.e];
        const A = vxy(a), B = vxy(b);
        const k = 0.17;
        const x1 = A[0] + (B[0] - A[0]) * k, y1 = A[1] + (B[1] - A[1]) * k, x2 = B[0] - (B[0] - A[0]) * k, y2 = B[1] - (B[1] - A[1]) * k;
        const g = el('g', { class: 'k2-fig', 'data-e': r.e }, gRoads);
        el('line', { x1, y1, x2, y2, stroke: INK, 'stroke-width': 13, 'stroke-linecap': 'round' }, g);
        el('line', { x1, y1, x2, y2, stroke: colorOf(r.seat), 'stroke-width': 8.5, 'stroke-linecap': 'round' }, g);
      }
      for (const t of view.towns || []) {
        const [x, y] = vxy(t.v);
        const g = el('g', { class: 'k2-fig', 'data-v': t.v }, gTowns);
        house(g, x, y, colorOf(t.seat), t.city, shapeOf(t.seat));
      }
    }
    // Кармелюк: числа на його гексі тьмяніють, сам — темна постать у шапці, стрибає переходом.
    for (const g of gTok.children) g.classList.toggle('k2-robbed', +g.dataset.tok === view.robber);
    if (view.robber != null && st.centers[view.robber]) {
      if (!gRob.firstChild) {
        const g = el('g', null, gRob);
        const t = el('title', null, g);
        t.textContent = 'Кармелюк: тут числа не родять';
        el('ellipse', { cx: 0, cy: 16, rx: 13, ry: 4, fill: 'rgba(0,0,0,.35)' }, g);
        el('path', { d: 'M-10,15 Q-12,-2 0,-6 Q12,-2 10,15 Z', fill: '#2b2a33', stroke: '#111', 'stroke-width': 1.5 }, g);
        el('circle', { cx: 0, cy: -11, r: 6.5, fill: '#d9b48a', stroke: '#111', 'stroke-width': 1.2 }, g);
        el('path', { d: 'M-8,-14 Q0,-28 8,-14 Z', fill: '#1b1a20', stroke: '#111', 'stroke-width': 1 }, g);
        el('path', { d: 'M-4,-9 h8', stroke: '#111', 'stroke-width': 1.5 }, g);
      }
      const [cx, cy] = st.centers[view.robber];
      gRob.style.transform = `translate(${cx - 0.5 * U}px, ${cy + 0.1 * U}px)`;
    }
  }

  // ─── Підсвітка законних місць ───────────────────────────────────────────
  function setLegal(l) {
    st.legal = l && l.kind ? l : null;
    gLegal.textContent = '';
    st.hot = null;
    if (!st.legal || !st.board) return;
    const k = st.legal.kind;
    if (k === 'town' || k === 'city') {
      for (const v of st.legal.v || []) {
        const [x, y] = vxy(v);
        const g = el('g', { class: 'k2-leg ' + k, 'data-k': 'v', 'data-id': v }, gLegal);
        const r = k === 'city' ? 0.36 * U : 0.17 * U;
        el('circle', { class: 'hit', cx: x, cy: y, r: 0.4 * U }, g);
        el('circle', { class: 'halo', cx: x, cy: y, r: r + 9 }, g);
        el('circle', { class: 'kant', cx: x, cy: y, r }, g);
        el('circle', { class: 'ring', cx: x, cy: y, r }, g);
      }
    } else if (k === 'road') {
      for (const e of st.legal.e || []) {
        const [a, b] = st.board.edges[e];
        const A = vxy(a), B = vxy(b), q = 0.22;
        const x1 = A[0] + (B[0] - A[0]) * q, y1 = A[1] + (B[1] - A[1]) * q, x2 = B[0] - (B[0] - A[0]) * q, y2 = B[1] - (B[1] - A[1]) * q;
        const g = el('g', { class: 'k2-leg', 'data-k': 'e', 'data-id': e }, gLegal);
        el('line', { class: 'hit', x1, y1, x2, y2, 'stroke-width': 0.42 * U, stroke: 'transparent' }, g);
        for (const c of ['halo', 'kant', 'bar', 'ants']) el('line', { class: c, x1, y1, x2, y2 }, g);
      }
    } else if (k === 'robber') {
      for (const h of st.legal.h || []) {
        const g = el('g', { class: 'k2-leg', 'data-k': 'h', 'data-id': h }, gLegal);
        const c = st.centers[h];
        const ring = pts(st.board.hexes[h].v.map((v) => { const [x, y] = vxy(v); return [c[0] + (x - c[0]) * 0.88, c[1] + (y - c[1]) * 0.88]; }));
        el('polygon', { class: 'kant', points: ring }, g);
        el('polygon', { class: 'area', points: ring }, g);
      }
    }
  }

  // ─── «Показ» гексів без клікабельності (glowHexes) ───────────────────────
  function glowHexes(ids) {
    st.shown = Array.isArray(ids) && ids.length ? ids.slice() : null;
    gShow.textContent = '';
    if (!st.shown || !st.board) return;
    for (const h of st.shown) {
      const hx = st.board.hexes[h], c = st.centers[h];
      if (!hx || !c) continue;
      el('polygon', { class: 'k2-show', points: pts(hx.v.map((v) => { const [x, y] = vxy(v); return [c[0] + (x - c[0]) * 0.93, c[1] + (y - c[1]) * 0.93]; })) }, gShow);
    }
  }

  function legalAt(target) {
    const g = target && target.closest && target.closest('.k2-leg');
    return g && gLegal.contains(g) ? g : null;
  }
  function onClick(e) {
    const g = legalAt(e.target);
    if (g) o.onPick(g.dataset.k, +g.dataset.id);
  }
  function onOver(e) {
    const g = legalAt(e.target);
    if (g === st.hot) return;
    if (st.hot) st.hot.classList.remove('hot');
    st.hot = g;
    if (g) { g.classList.add('hot'); o.onHover(g.dataset.k, +g.dataset.id); } else o.onHover(null, null);
  }
  function onLeave() { if (st.hot) { st.hot.classList.remove('hot'); st.hot = null; o.onHover(null, null); } }
  svg.addEventListener('click', onClick);
  svg.addEventListener('pointerover', onOver);
  svg.addEventListener('pointerleave', onLeave);

  // ─── Анімації ────────────────────────────────────────────────────────────
  function paintDie(d, n) {
    const on = PIP_CELLS[n] || [];
    let h = '';
    for (let i = 0; i < 9; i++) h += '<i' + (on.includes(i) ? ' class="on"' : '') + '></i>';
    d.innerHTML = h;
  }
  async function roll(a, b) {
    const [d1, d2] = dice.children;
    dice.hidden = false;
    dice.classList.remove('land');
    if (!o.reducedMotion) {
      dice.classList.add('spin');
      for (let i = 0; i < 8; i++) {
        paintDie(d1, 1 + Math.floor(Math.random() * 6)); paintDie(d2, 1 + Math.floor(Math.random() * 6));
        await sleep(80);
        if (st.dead) return;
      }
      dice.classList.remove('spin');
    }
    paintDie(d1, a); paintDie(d2, b);
    void dice.offsetWidth;
    dice.classList.add('land');
    if (!o.reducedMotion) await sleep(250);
  }

  async function produce(gains) {
    if (!st.board || !gains || !gains.length) return;
    const hexes = new Set(gains.map((g) => g.hex));
    for (const h of hexes) {
      const poly = gHex.querySelector('[data-h="' + h + '"]');
      if (poly) { poly.classList.remove('k2-hexflash'); void poly.getBBox(); poly.classList.add('k2-hexflash'); }
    }
    if (o.reducedMotion) return;
    gains.slice(0, 16).forEach((gn, i) => {
      const c = st.centers[gn.hex];
      if (!c) return;
      const g = el('g', { transform: `translate(${c[0] + ((i % 3) - 1) * 16},${c[1]})` }, gFx);
      const f = el('g', { class: 'k2-fly', style: 'animation-delay:' + (i * 50) + 'ms; opacity:0' }, g);
      el('circle', { r: 15, fill: RES_COLOR[gn.res] || '#ccc', stroke: colorOf(gn.seat), 'stroke-width': 4 }, f);
      const t = el('text', { y: 6, 'text-anchor': 'middle', 'font-size': 16 }, f);
      t.textContent = RES_ICON[gn.res] || '';
      later(() => g.remove(), 1100 + i * 50);
    });
    await sleep(900);
  }

  function pop(kind, id) {
    const sel = kind === 'e' ? '[data-e="' + id + '"]' : '[data-v="' + id + '"]';
    const f = (kind === 'e' ? gRoads : gTowns).querySelector(sel);
    if (!f || o.reducedMotion) return;
    f.classList.remove('pop'); void f.getBBox(); f.classList.add('pop');
    later(() => f.classList.remove('pop'), 700);
  }

  function screenPos(kind, id) {
    if (!st.board) return null;
    const p = posOf(kind, id);
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
    svg.removeEventListener('pointerover', onOver);
    svg.removeEventListener('pointerleave', onLeave);
    svg.remove(); dice.remove();
  }

  return {
    kind: '2d',
    setBoard, setState, setLegal, glowHexes, roll, produce, pop, screenPos,
    resetView() {}, resize() {}, dispose,
  };
}
