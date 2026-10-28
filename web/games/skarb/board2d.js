// «Скарб Полуботка» — пласка мапа розрізу (SVG). Той самий API, що й 3D-сцена (docs/games/specs/skarb.md, §10):
// запасний вигляд для слабких пристроїв / без WebGL і вибір «🗺 Пласка». Правил не знає, дій не шле — лише малює
// вид і каже картці, куди клацнули (лише по підсвіченому). Малюнок — з концептів художника, три теми
// (opts.theme / setTheme: 'fairy' «Казкова ніч», 'wood' «Дерев'яна настолка» — типова, 'night' «Ніч копачів»);
// самі SVG-рядки — у board2d-art.js. Клітинки квадратні; на широкому хості видно все поле 17×9 зі смугою поверхні,
// на вузькому (портрет) — ядро «колодязь → мітки» на всю висоту, решта — тягнути/щипати (камера сама підпливає до
// нового ходу й до законних клітинок). Тло (небо, боки, підошва) домальовується під будь-яке співвідношення сторін. Поки нічого не діється — жодних
// перемальовувань: шари перебудовуються лише на зміну стану, підсвітки чи розміру.

import { GRID, PAL, STRATA } from './consts.js';
import * as A from './board2d-art.js';

export { THEME_LIST, DEFAULT_THEME } from './board2d-art.js';

const NS = 'http://www.w3.org/2000/svg';
const U = A.U;           // одиниць viewBox на клітинку
const PAD = 0.14;        // поля навколо поля й поверхні, клітинок
const TAP_PX = 26;       // мінімальний радіус влучання пальцем, px (≈ 52 px ціль)

const CSS = `
.s2 { width: 100%; height: 100%; display: block; user-select: none; -webkit-user-select: none; touch-action: pan-y; }
.s2.zoomed { touch-action: none; }
.s2-leg, .s2-ghost, .s2-cur { pointer-events: none; }
.s2-leg { transform-box: fill-box; transform-origin: center; transition: transform .12s ease-out; }
.s2-leg .pl { transform-box: fill-box; transform-origin: center; animation: s2pulse 1s ease-in-out infinite alternate; }
.s2-leg.alt .pl { animation: none; opacity: .9; }
.s2-leg.hot { transform: translateY(-6px) scale(1.04); }
.s2-leg.hot > rect:first-child { stroke: #fff; }
.s2-leg.hot .pl { animation: none; }
.s2-ghost { opacity: .9; }
.s2-cur rect { fill: none; stroke: #fff; stroke-width: 4; stroke-dasharray: 10 8; animation: s2march 1s linear infinite; }
@keyframes s2pulse { from { transform: scale(.92); opacity: .65; } to { transform: scale(1.03); opacity: 1; } }
@keyframes s2march { to { stroke-dashoffset: -36; } }
.s2-pop { transform-box: fill-box; transform-origin: center; animation: s2pop .38s cubic-bezier(.3,1.5,.5,1); }
@keyframes s2pop { 0% { transform: scale(.4); opacity: .2; } 100% { transform: scale(1); opacity: 1; } }
.s2-crumble { transform-box: fill-box; transform-origin: center; animation: s2crumble .4s ease-in forwards; }
@keyframes s2crumble { 0% { transform: scale(1); opacity: 1; } 100% { transform: scale(.25); opacity: 0; } }
.s2-flip { transform-box: fill-box; transform-origin: center; animation: s2flip .4s ease-out; }
@keyframes s2flip { 0% { transform: scaleX(0); } 100% { transform: scaleX(1); } }
.s2-shake { animation: s2shake .3s linear; }
@keyframes s2shake { 25% { transform: translate(5px, -3px); } 50% { transform: translate(-5px, 3px); } 75% { transform: translate(3px, 2px); } }
.s2-fall { animation: s2fall .38s cubic-bezier(.5,0,1,1) forwards; }
@keyframes s2fall { from { transform: translateY(-120px); opacity: 0; } 30% { opacity: 1; } to { transform: translateY(0); opacity: 1; } }
.s2-fly { animation: s2fly .7s ease-out forwards; }
@keyframes s2fly { 0% { transform: translate(0, 0) scale(.5); opacity: 0; } 20% { opacity: 1; } 100% { transform: translate(var(--dx), var(--dy)) scale(1); opacity: 0; } }
.s2-peek { animation: s2peek 1s ease-out forwards; }
@keyframes s2peek { 0% { transform: translateY(16px); opacity: 0; } 25%, 75% { transform: translateY(-10px); opacity: 1; } 100% { transform: translateY(-24px); opacity: 0; } }
.s2-mole { animation: s2mole .9s ease-in-out forwards; }
@keyframes s2mole { 0%, 100% { transform: translateY(24px); opacity: 0; } 30%, 70% { transform: translateY(0); opacity: 1; } }
.s2.rm .s2-leg .pl, .s2.rm .s2-cur rect { animation: none; }
.s2.rm .s2-leg { transition: none; }
@media (prefers-reduced-motion: reduce) { .s2-leg .pl, .s2-cur rect { animation: none; } .s2-leg { transition: none; } }
`;

let uid = 0;
function el(tag, attrs, parent) {
  const e = document.createElementNS(NS, tag);
  if (attrs) for (const k in attrs) e.setAttribute(k, attrs[k]);
  if (parent) parent.appendChild(e);
  return e;
}
/** Детермінований «шум». */
const noise = (x, y) => { const s = Math.sin(x * 12.9898 + y * 78.233) * 43758.5453; return s - Math.floor(s); };

export async function createBoard2d(host, opts = {}) {
  const o = { onPick() {}, onHover() {}, reducedMotion: false, quality: 'high', theme: A.DEFAULT_THEME, ...opts };
  const p = 's2' + (++uid) + '_';
  const lite = o.quality === 'low';
  const st = { grid: GRID, gridSig: '', view: null, sig: '', legal: null, hot: null, cur: null, timers: new Set(), dead: false,
    vb: null, fit: null, art: null, zoom: 1, ptrs: new Map(), drag: null, rubble: new Set(), t: A.themeOf(o.theme), artT: 0, all: null, glide: 0, userAt: 0 };

  const svg = el('svg', { class: 's2' + (o.reducedMotion ? ' rm' : ''), preserveAspectRatio: 'xMidYMid meet', role: 'img', 'aria-label': 'Розріз землі під селом' });
  el('style', null, svg).textContent = CSS;
  const defs = el('defs', null, svg);
  const world = el('g', null, svg);
  const gBg = el('g', null, world), gCards = el('g', null, world), gGoals = el('g', null, world), gFig = el('g', null, world);
  const gDeco = el('g', null, world), gLegal = el('g', null, world), gGhost = el('g', { class: 's2-ghost' }, world);
  const gCur = el('g', { class: 's2-cur' }, world), gFx = el('g', null, world);
  host.appendChild(svg);
  if (getComputedStyle(host).position === 'static') host.style.position = 'relative';

  const later = (fn, ms) => { const t = setTimeout(() => { st.timers.delete(t); if (!st.dead) fn(); }, ms); st.timers.add(t); return t; };
  const sleep = (ms) => new Promise((res) => later(res, o.reducedMotion ? Math.min(ms, 120) : ms));
  const C = (x, y) => [x * U, y * U];
  const html = (g, s) => { g.innerHTML = s; };

  // ─── Вид: viewBox рівно під пропорції хоста ─────────────────────────────
  // st.all — «показати все» (усе поле; найдальше, куди можна віддалитись, і межі панорамування; тло покриває саме
  // його). st.fit — типовий вид: на широкому хості = st.all, на вузькому (портрет) — ядро x ∈ [CORE] (колодязь →
  // мітки) на всю висоту, як у 3D і в концептах для телефона; решта поля — тягнути пальцем або щипати.
  const CORE = [-1.5, 9.5];
  function boxFor(cx, cw) {
    const g = st.grid, t = st.t;
    const W = host.clientWidth || 800, H = host.clientHeight || 500;
    const cy = (g.y0 - 0.5 - t.surf) * U, ch = (g.y1 - g.y0 + 1 + t.surf + PAD) * U;
    const ar = W / H;
    if (cw / ch < ar) { const w = ch * ar; return { x: cx - (w - cw) / 2, y: cy, w, h: ch }; }
    // Вище, ніж треба: більша частина зайвого — небу, решта — кам'яній підошві.
    const h = cw / ar;
    return { x: cx, y: cy - (h - ch) * 0.55, w: cw, h };
  }
  function allBox() { const g = st.grid; return boxFor((g.x0 - 0.5 - PAD) * U, (g.x1 - g.x0 + 1 + 2 * PAD) * U); }
  function fitBox() {
    const W = host.clientWidth || 800, H = host.clientHeight || 500;
    const all = allBox();
    if (H <= W * 0.85) return all;
    const f = boxFor(CORE[0] * U, (CORE[1] - CORE[0]) * U);
    if (f.w >= all.w) return all;
    f.x = Math.max(all.x, Math.min(all.x + all.w - f.w, f.x));
    return f;
  }
  const zoomOf = () => st.fit.w / st.vb.w;
  function applyVb() {
    const b = st.vb;
    svg.setAttribute('viewBox', [b.x, b.y, b.w, b.h].map((n) => n.toFixed(1)).join(' '));
    st.zoom = zoomOf();
    svg.classList.toggle('zoomed', st.zoom > 1.02);
  }
  const covers = (a, b) => a && b.x >= a.x - 1 && b.y >= a.y - 1 && b.x + b.w <= a.x + a.w + 1 && b.y + b.h <= a.y + a.h + 1;
  /** Тло під «показати все» (лише якщо старе не покриває або завелике). */
  function ensureArt(force) {
    const f = st.all;
    if (!force && covers(st.art, f) && st.art.w * st.art.h < f.w * f.h * 1.6) return;
    st.art = { ...f };
    html(gBg, A.backdrop(st.t, p, st.grid, f, lite));
    html(gDeco, A.deco(st.t, p, st.grid, f, lite));
  }
  function fitView(soon) {
    stopGlide();
    st.all = allBox(); st.fit = fitBox(); st.vb = { ...st.fit }; applyVb();
    if (!soon) { ensureArt(); return; }
    // Під час перетягування розміру — перебудувати тло, коли вгамується.
    clearTimeout(st.artT); st.timers.delete(st.artT);
    st.artT = later(() => ensureArt(), 140);
  }
  function clampVb(b = st.vb) {
    const f = st.all;
    b.x = b.w >= f.w ? f.x + (f.w - b.w) / 2 : Math.max(f.x, Math.min(f.x + f.w - b.w, b.x));
    b.y = b.h >= f.h ? f.y + (f.h - b.h) / 2 : Math.max(f.y, Math.min(f.y + f.h - b.h, b.y));
    return b;
  }
  /** Масштаб у k разів навколо точки хоста; віддалитись — не далі за «показати все», наблизитись — до ×3 від типового. */
  function zoomAt(k, px, py) {
    const b = st.vb;
    const w = Math.max(st.fit.w / 3, Math.min(st.all.w, b.w / k));
    const real = w / b.w;
    const [wx, wy] = toWorld(px, py);
    b.w = w; b.h *= real;
    b.x = wx - (wx - b.x) * real; b.y = wy - (wy - b.y) * real;
    clampVb(); applyVb();
  }
  const canPan = () => st.vb.w < st.all.w - 1 || st.vb.h < st.all.h - 1;

  // Камера «підпливає» до клітинки, якщо та поза видом (≈ 0,35 с; без руху — нічого не перемальовується).
  function visible(x, y, m = 0.6) { const b = st.vb; return x * U >= b.x + m * U && x * U <= b.x + b.w - m * U && y * U >= b.y + m * U && y * U <= b.y + b.h - m * U; }
  function stopGlide() { if (st.glide) { cancelAnimationFrame(st.glide); st.glide = 0; } }
  function follow(x, y, auto) {
    if (st.dead || !st.vb || !canPan() || visible(x, y)) return;
    if (auto && Date.now() - st.userAt < 4000) return; // людина щойно сама рухала мапу — не смикати
    // Рухати лише ту вісь, по якій клітинка випала з виду.
    const b = st.vb, inX = x * U >= b.x + 0.6 * U && x * U <= b.x + b.w - 0.6 * U, inY = y * U >= b.y + 0.6 * U && y * U <= b.y + b.h - 0.6 * U;
    const to = clampVb({ ...b, x: inX ? b.x : x * U - b.w / 2, y: inY ? b.y : y * U - b.h / 2 });
    stopGlide();
    if (o.reducedMotion) { st.vb.x = to.x; st.vb.y = to.y; applyVb(); return; }
    const from = { x: st.vb.x, y: st.vb.y }, t0 = performance.now(), T = 350;
    const step = (now) => {
      if (st.dead) return;
      const k = Math.min(1, (now - t0) / T), e = 1 - Math.pow(1 - k, 3);
      st.vb.x = from.x + (to.x - from.x) * e; st.vb.y = from.y + (to.y - from.y) * e; applyVb();
      st.glide = k < 1 ? requestAnimationFrame(step) : 0;
    };
    st.glide = requestAnimationFrame(step);
  }
  /** Пікселі host → координати viewBox. */
  function toWorld(px, py) {
    const m = svg.getScreenCTM();
    if (!m) return [0, 0];
    const r = host.getBoundingClientRect();
    const pt = svg.createSVGPoint();
    pt.x = px + r.left; pt.y = py + r.top;
    const q = pt.matrixTransform(m.inverse());
    return [q.x, q.y];
  }
  const unitsPerPx = () => (st.vb ? st.vb.w / (host.clientWidth || 1) : 1);

  // ─── Тема й поле ────────────────────────────────────────────────────────
  function applyTheme() {
    defs.innerHTML = A.defs(st.t, p);
    svg.style.background = st.t.bg;
    st.fit = null; st.art = null;
    fitView();
  }
  function setGrid(grid) {
    const g = grid || GRID;
    const sig = JSON.stringify(g);
    if (sig === st.gridSig) return;
    st.grid = g; st.gridSig = sig;
    st.fit = null; st.art = null;
    fitView();
  }
  function setTheme(id) {
    const t = A.themeOf(id);
    if (t === st.t) return;
    st.t = t;
    applyTheme();
    redraw();
    setLegal(st.legal, true);
    if (st.cur) setCursor(st.cur[0], st.cur[1]);
  }

  // ─── Ходи, мітки, фігурки ───────────────────────────────────────────────
  function drawCards() {
    const v = st.view;
    if (!v) { gCards.textContent = ''; return; }
    const linked = new Set((v.linked || []).map(([x, y]) => x + ',' + y));
    const board = v.board || [];
    const cards = board.some((b) => b.s === 'start') ? board : [{ x: st.grid.start[0], y: st.grid.start[1], s: 'start', m: 15, c: true }, ...board];
    // Осип після завалу лишається, доки на клітинку не ляже нова карта; новий раунд (лише колодязь) — чисто.
    if (board.length <= 1) st.rubble.clear();
    for (const b of board) st.rubble.delete(b.x + ',' + b.y);
    const opened = (v.goals || []).filter((g) => g.open);
    html(gCards, A.tunnels(st.t, p, st.grid, cards, opened, linked, [...st.rubble]));
  }

  function drawGoals() {
    const v = st.view;
    if (!v) { gGoals.textContent = ''; return; }
    const peeks = (v.me && v.me.peeks) || {};
    let s = '';
    for (const gl of v.goals || []) s += `<g data-g="${gl.i}">${A.goal(st.t, p, gl, !gl.open && peeks[gl.i])}</g>`;
    html(gGoals, s);
  }

  /** seat → [x, yНіг, позаду?, масштаб]. Фігурка — на клітинці свого останнього ходу; немає — біля колодязя. */
  function figPos() {
    const v = st.view, out = new Map();
    if (!v) return out;
    const at = new Map();
    for (const b of v.board || []) if (b.by != null) at.set(b.by, [b.x, b.y]);
    const per = new Map(), top = [];
    for (const pl of v.players || []) {
      const c = at.get(pl.seat);
      if (!c) { top.push(pl.seat); continue; }
      const k = c.join();
      if (!per.has(k)) per.set(k, { c, seats: [] });
      per.get(k).seats.push(pl.seat);
    }
    const floor = st.t.tw / 2 - 4;
    for (const { c, seats } of per.values()) {
      const n = Math.min(4, seats.length), sc = n > 2 ? 0.8 : 1, gap = n > 2 ? 20 : 26;
      seats.forEach((s, i) => {
        if (i < 4) out.set(s, [c[0] * U + (i - (n - 1) / 2) * gap, c[1] * U + floor, false, sc]);
        else out.set(s, [c[0] * U + (i - 5.5) * 12, c[1] * U + floor - 8, true, 0.7]);
      });
    }
    const g = st.grid, gy = (g.y0 - 0.5) * U + 6;
    top.forEach((s, i) => out.set(s, [g.start[0] * U + 92 + i * 26, gy, false, 0.85]));
    return out;
  }
  function drawFigs() {
    const v = st.view;
    if (!v) { gFig.textContent = ''; return; }
    const pos = figPos();
    let back = '', front = '';
    for (const pl of v.players || []) {
      const q = pos.get(pl.seat);
      if (!q) continue;
      const [x, y, behind, sc] = q;
      const broken = (pl.broken || []).length > 0;
      const s = `<g data-s="${pl.seat}"${behind ? ' opacity=".6"' : ''}>${A.figure(st.t, x, y, sc, pl.color, broken)}<title>${esc(pl.nick || '')}${broken ? ' — щось зламано' : ''}</title></g>`;
      if (behind) back += s; else front += s;
    }
    html(gFig, back + front);
  }
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

  function setState(view) {
    st.view = view ? { ...view, board: (view.board || []).slice(), goals: (view.goals || []).map((g) => ({ ...g })), me: view.me ? { ...view.me, peeks: { ...(view.me.peeks || {}) } } : null } : null;
    if (view && view.grid) setGrid(view.grid);
    const sig = view ? JSON.stringify([view.board, view.linked, view.goals, (view.players || []).map((q) => [q.seat, q.color, q.broken, q.nick]), view.me && view.me.peeks]) : '';
    if (sig === st.sig) return;
    st.sig = sig;
    redraw();
  }
  function redraw() { drawCards(); drawGoals(); drawFigs(); }

  // ─── Підсвітка, привид, курсор ──────────────────────────────────────────
  function setLegal(l, again) {
    const prevHot = again && st.hot ? st.hot.tag : null;
    st.legal = l && l.kind ? { kind: l.kind, cells: l.cells || [], alt: l.alt || [], goals: l.goals || [], ghost: l.ghost || null } : null;
    gLegal.textContent = ''; gGhost.textContent = '';
    st.hot = null;
    const L = st.legal;
    if (!L) return;
    let s = '';
    const box = (cls, x, y, tag) => { s += `<g class="s2-leg ${cls}" data-t="${tag}">${A.legalCell(st.t, p, x, y, cls, lite)}</g>`; };
    if (L.kind === 'place') { for (const [x, y] of L.alt) box('alt', x, y, 'a' + x + ',' + y); for (const [x, y] of L.cells) box('', x, y, 'c' + x + ',' + y); }
    else if (L.kind === 'rockfall') for (const [x, y] of L.cells) box('rock', x, y, 'c' + x + ',' + y);
    else if (L.kind === 'goal') for (const i of L.goals) { const gl = goalXY(i); if (gl) box('goal', gl.x, gl.y, 'g' + i); }
    html(gLegal, s);
    if (!again) {
      const ts = allTargets();
      if (ts.length && !ts.some((q) => visible(q.x, q.y))) {
        const cx = st.vb.x / U + st.vb.w / U / 2, cy = st.vb.y / U + st.vb.h / U / 2;
        const n = ts.reduce((a, q) => (Math.hypot(q.x - cx, q.y - cy) < Math.hypot(a.x - cx, a.y - cy) ? q : a));
        follow(n.x, n.y, false);
      }
    }
    if (prevHot) { const t = allTargets().find((q) => q.tag === prevHot); if (t) setHot(t, true); }
  }
  function goalXY(i) {
    const gl = (st.view && st.view.goals || []).find((g) => g.i === i);
    if (gl) return gl;
    const q = (st.grid.goals || GRID.goals)[i];
    return q ? { x: q[0], y: q[1] } : null;
  }
  function allTargets() {
    const L = st.legal, out = [];
    if (!L) return out;
    if (L.kind === 'goal') for (const i of L.goals) { const gl = goalXY(i); if (gl) out.push({ kind: 'goal', i, x: gl.x, y: gl.y, tag: 'g' + i }); }
    else {
      for (const [x, y] of L.cells) out.push({ kind: 'cell', x, y, tag: 'c' + x + ',' + y, alt: false });
      for (const [x, y] of L.alt) out.push({ kind: 'cell', x, y, tag: 'a' + x + ',' + y, alt: true });
    }
    return out;
  }
  /** Найближча підсвічена ціль: мишею — у межах 0,7 клітинки, пальцем — щонайменше TAP_PX екранних пікселів. */
  function targetAt(wx, wy, touch) {
    let best = null, bd = Math.max(0.7 * U, touch ? TAP_PX * unitsPerPx() : 0);
    for (const t of allTargets()) { const d = Math.hypot(wx - t.x * U, wy - t.y * U); if (d < bd) { bd = d; best = t; } }
    return best;
  }

  function setHot(t, quiet) {
    const tag = t ? t.tag : null;
    if (!quiet && (st.hot && st.hot.tag) === tag) return;
    if (st.hot) { const g = gLegal.querySelector('[data-t="' + st.hot.tag + '"]'); if (g) g.classList.remove('hot'); }
    st.hot = t;
    gGhost.textContent = '';
    if (t) {
      const g = gLegal.querySelector('[data-t="' + tag + '"]');
      if (g) { g.classList.add('hot'); gLegal.appendChild(g); }
      const L = st.legal;
      if (L && L.kind === 'place' && L.ghost && t.kind === 'cell') {
        const m = t.alt ? L.ghost.mAlt : L.ghost.m;
        if (m != null) html(gGhost, `<g transform="translate(0 -6)">${A.ghost(st.t, t.x, t.y, m, L.ghost.c)}</g>`);
      }
      if (!quiet) { if (t.kind === 'goal') o.onHover('goal', t.i); else o.onHover('cell', t.x, t.y); }
    } else if (!quiet) o.onHover(null);
  }

  function setCursor(x, y) {
    gCur.textContent = '';
    if (x == null) { st.cur = null; return; }
    st.cur = [x, y];
    const [cx, cy] = C(x, y);
    el('rect', { x: cx - U / 2 + 1, y: cy - U / 2 + 1, width: U - 2, height: U - 2, rx: 12 }, gCur);
    follow(x, y, false);
    const t = targetAt(cx, cy, false);
    if (t) setHot(t);
  }

  // ─── Указівник: тап, тягнути, щипати, колесо ────────────────────────────
  const local = (e) => { const r = host.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  const isTouch = (e) => e.pointerType === 'touch' || e.pointerType === 'pen';
  function onDown(e) {
    if (e.button != null && e.button > 0) return;
    stopGlide();
    const pt = local(e);
    st.ptrs.set(e.pointerId, pt);
    if (st.ptrs.size === 1) st.drag = { x: pt[0], y: pt[1], vb: { ...st.vb }, moved: 0, id: e.pointerId };
    else if (st.ptrs.size === 2) {
      const [a, b] = [...st.ptrs.values()];
      st.drag = { pinch: Math.hypot(a[0] - b[0], a[1] - b[1]), w: st.vb.w, moved: 99 };
    }
    try { svg.setPointerCapture(e.pointerId); } catch (err) { /* старий браузер */ }
  }
  function onMove(e) {
    const pt = local(e);
    if (st.ptrs.has(e.pointerId)) st.ptrs.set(e.pointerId, pt);
    const d = st.drag;
    if (d && d.pinch && st.ptrs.size === 2) {
      const [a, b] = [...st.ptrs.values()];
      const dist = Math.hypot(a[0] - b[0], a[1] - b[1]);
      zoomAt(st.vb.w / (d.w * d.pinch / Math.max(1, dist)), (a[0] + b[0]) / 2, (a[1] + b[1]) / 2);
      st.userAt = Date.now();
      return;
    }
    if (d && st.ptrs.size === 1 && d.id === e.pointerId) {
      const dx = pt[0] - d.x, dy = pt[1] - d.y;
      d.moved = Math.max(d.moved, Math.hypot(dx, dy));
      if (d.moved >= 8) {
        if (canPan()) {
          const k = st.vb.w / (host.clientWidth || 1);
          st.vb.x = d.vb.x - dx * k; st.vb.y = d.vb.y - dy * k;
          clampVb(); applyVb();
          st.userAt = Date.now();
        }
        return;
      }
    }
    if (e.pointerType === 'mouse' || !e.pointerType) { const [wx, wy] = toWorld(pt[0], pt[1]); setHot(targetAt(wx, wy, false)); }
  }
  function onUp(e) {
    const d = st.drag, pt = local(e);
    st.ptrs.delete(e.pointerId);
    if (st.ptrs.size === 0) st.drag = null;
    if (!d || d.pinch || d.id !== e.pointerId || d.moved >= 8 || e.type === 'pointercancel') return;
    const [wx, wy] = toWorld(pt[0], pt[1]);
    const t = targetAt(wx, wy, isTouch(e));
    if (!t) return;
    if (t.kind === 'goal') o.onPick('goal', t.i); else o.onPick('cell', t.x, t.y);
  }
  function onLeave() { if (!st.ptrs.size) setHot(null); }
  function onWheel(e) {
    if (st.vb.w >= st.all.w - 0.5 && e.deltaY > 0) return; // уже все поле: хай сторінка гортається
    e.preventDefault();
    const [px, py] = local(e);
    zoomAt(Math.exp(-e.deltaY * 0.0015), px, py);
    st.userAt = Date.now();
  }
  function onDbl() { fitView(); }
  svg.addEventListener('pointerdown', onDown);
  svg.addEventListener('pointermove', onMove);
  svg.addEventListener('pointerup', onUp);
  svg.addEventListener('pointercancel', onUp);
  svg.addEventListener('pointerleave', onLeave);
  svg.addEventListener('wheel', onWheel, { passive: false });
  svg.addEventListener('dblclick', onDbl);

  // ─── Анімації (короткі; сцена лишається в стані «після події») ──────────
  const restart = (node, cls) => { if (!node || o.reducedMotion) return; node.classList.remove(cls); void node.getBBox(); node.classList.add(cls); later(() => node.classList.remove(cls), 700); };
  const soilAt = (y) => { const i = STRATA.findIndex((q) => y >= q.y0 && y <= q.y1); return st.t.strata[i < 0 ? STRATA.length - 1 : i][0]; };

  async function dig(ev) {
    if (!st.view) return;
    st.view.board = st.view.board.filter((b) => b.x !== ev.x || b.y !== ev.y);
    st.view.board.push({ x: ev.x, y: ev.y, s: ev.s, r: ev.r, m: ev.m, c: ev.c, by: ev.seat, id: ev.card });
    if (!(st.view.linked || []).some(([x, y]) => x === ev.x && y === ev.y)) st.view.linked = [...(st.view.linked || []), [ev.x, ev.y]];
    st.sig = '';
    redraw();
    if (!o.reducedMotion) {
      // Земля «вигризається»: грудка кольору шару стискається й тане над свіжим ходом.
      const [cx, cy] = C(ev.x, ev.y);
      const g = el('g', { class: 's2-crumble' }, gFx);
      el('rect', { x: cx - U / 2 + 4, y: cy - U / 2 + 4, width: U - 8, height: U - 8, rx: 10, fill: soilAt(ev.y), opacity: 0.9 }, g);
      later(() => g.remove(), 450);
    }
    follow(ev.x, ev.y, true);
    restart(gCards.querySelector('[data-c="' + ev.x + ',' + ev.y + '"]'), 's2-pop');
    dust(ev.x, ev.y, '#a77b4a', 7);
    await sleep(380);
  }

  async function flip(ev) {
    if (!st.view) return;
    const g = st.view.goals.find((x) => x.i === ev.i);
    if (g) Object.assign(g, { open: true, kind: ev.kind, s: ev.s, r: ev.r, m: ev.m, c: ev.c });
    st.sig = '';
    drawCards(); drawGoals();
    restart(gGoals.querySelector('[data-g="' + ev.i + '"]'), 's2-flip');
    if (ev.kind === 'gold') { coins(ev.x, ev.y, 12); await sleep(600); } else { dust(ev.x, ev.y, '#8d8f94', 6); await sleep(400); }
  }

  async function rockfall(ev) {
    if (!st.view) return;
    st.view.board = st.view.board.filter((b) => b.x !== ev.x || b.y !== ev.y);
    st.rubble.add(ev.x + ',' + ev.y);
    st.sig = '';
    redraw();
    if (!o.reducedMotion) {
      const [cx, cy] = C(ev.x, ev.y);
      for (let i = 0; i < 6; i++) {
        const g = el('g', { class: 's2-fall', style: 'animation-delay:' + i * 30 + 'ms; opacity:0' }, gFx);
        el('circle', { cx: cx + (noise(i, ev.x) - 0.5) * U * 0.7, cy: cy + (noise(ev.y, i) - 0.5) * U * 0.5, r: 10 + noise(i, i) * 10, fill: st.t.boulder, stroke: A.INK, 'stroke-width': 2.5 }, g);
        later(() => g.remove(), 700);
      }
      restart(world, 's2-shake');
      dust(ev.x, ev.y, '#8d8f94', 6);
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
      el('rect', { x: cx - 30, y: cy - U * 0.9, width: 60, height: 40, rx: 5, fill: PAL.map, stroke: '#8a6a3a', 'stroke-width': 2.5 }, g);
      el('path', { d: `M${cx - 20},${cy - U * 0.9 + 27} q13,-20 26,-3 t14,-10`, fill: 'none', stroke: PAL.bad, 'stroke-width': 2.6, 'stroke-dasharray': '5 3' }, g);
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
      coins(x, y, 18);
    } else if (side === 'moles') {
      for (let i = 0; i < 6; i++) {
        const x = Math.round(1 + noise(i, 7) * 9), y = Math.round(-3 + noise(7, i) * 7);
        const [cx, cy] = C(x, y);
        const g = el('g', { class: 's2-mole', style: 'animation-delay:' + i * 80 + 'ms; opacity:0' }, gFx);
        el('ellipse', { cx, cy, rx: 22, ry: 17, fill: '#5a4a3e', stroke: A.INK, 'stroke-width': 2.5 }, g);
        el('circle', { cx, cy: cy + 5, r: 5, fill: '#e98fa0' }, g);
        el('circle', { cx: cx - 8, cy: cy - 5, r: 2.6, fill: '#111' }, g);
        el('circle', { cx: cx + 8, cy: cy - 5, r: 2.6, fill: '#111' }, g);
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
      const g = el('g', { class: 's2-fly', style: '--dx:' + (Math.cos(a) * 50).toFixed(1) + 'px; --dy:' + (Math.sin(a) * 50).toFixed(1) + 'px; opacity:0' }, gFx);
      el('circle', { cx, cy, r: 7, fill: color, opacity: 0.8 }, g);
      later(() => g.remove(), 760);
    }
  }
  function coins(x, y, n) {
    if (o.reducedMotion) return;
    const [cx, cy] = C(x, y);
    for (let i = 0; i < n; i++) {
      const a = -Math.PI / 2 + (i / (n - 1) - 0.5) * 2.4;
      const r = 70 + noise(i, n) * 70;
      const g = el('g', { class: 's2-fly', style: '--dx:' + (Math.cos(a) * r).toFixed(1) + 'px; --dy:' + (Math.sin(a) * r).toFixed(1) + 'px; animation-delay:' + i * 25 + 'ms; opacity:0' }, gFx);
      el('circle', { cx, cy, r: 10, fill: `url(#${p}coin)`, stroke: '#6b4208', 'stroke-width': 2.5 }, g);
      later(() => g.remove(), 1000);
    }
  }

  function screenPos(kind, a, b) {
    let pt = null;
    if (kind === 'cell') pt = C(a, b);
    else if (kind === 'goal') { const gl = goalXY(a); if (gl) pt = C(gl.x, gl.y); }
    else if (kind === 'seat') { const q = figPos().get(a); if (q) pt = [q[0], q[1] - 25 * q[3]]; }
    const m = svg.getScreenCTM();
    if (!pt || !m) return null;
    const sp = svg.createSVGPoint();
    sp.x = pt[0]; sp.y = pt[1];
    const s = sp.matrixTransform(m);
    const r = host.getBoundingClientRect();
    return { x: s.x - r.left, y: s.y - r.top };
  }

  /** Підвести вид до клітинки, якщо вона поза ним (не чіпати, якщо людина рухала мапу < 4 с тому). */
  function focus(x, y) { if (x != null && y != null) follow(x, y, true); }
  function resize() {
    if (st.dead) return;
    const f = fitBox(), o2 = st.fit;
    if (o2 && Math.abs(f.w - o2.w) < 0.5 && Math.abs(f.h - o2.h) < 0.5 && Math.abs(f.x - o2.x) < 0.5 && Math.abs(f.y - o2.y) < 0.5) return;
    fitView(true);
  }

  function dispose() {
    st.dead = true;
    stopGlide();
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

  st.gridSig = JSON.stringify(GRID);
  applyTheme();
  return {
    kind: '2d',
    setGrid, setState, setLegal: (l) => setLegal(l), setCursor, dig, flip, rockfall, peek, finale, focus, screenPos,
    setTheme, get theme() { return st.t.id; },
    resetView: () => fitView(), resize, dispose,
  };
}
