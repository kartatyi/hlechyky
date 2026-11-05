// «Соляний шлях» — пласке поле: той самий API, що й 3D-сцена (docs/games/specs/solyanyi.md §10), SVG у одиницях сітки мапи.
// Сцена не знає правил і не шле дій: малює, що їй сказали, і повідомляє про кліки. Геометрія — лише routeCurve / routeSlots.
//
// Перф: сам <svg> має overflow: visible і власний шар, тож зсув і щипок — це CSS transform (компонується, вузли не
// перемальовуються). Лише коли жест заспокоївся після зміни масштабу, новий масштаб записується у viewBox — і вже тоді
// (один раз) перемальовуються підписи й різкість. Без руху й анімацій нічого не працює: rAF лише на час польоту.

import { CITIES, ROUTES, MAP, city as cityOf, route as routeOf, routeCurve, routeSlots, ticket as ticketOf } from './consts.js';
import { themeOf, DEFAULT_THEME, FALLBACK } from './themes.js';
import { twoFinger, wheelZoomFactor } from '../khutory/gestures.js';
import { artOf, buildGeo, labelLines, f1 } from './board2d-art.js';
import { homeCrop } from './hud-calc.js';

const NS = 'http://www.w3.org/2000/svg';
const ZOOM_MAX = 6;                                  // §11.2: 1 — уся мапа, 6 — найближче
const BOX = { x0: 35, y0: 43, x1: 3960, y1: 2687 };  // крайні точки країни (geo.md §5.1)
const VOID = .12;                                    // ≤ 12 % порожнечі за межею мапи
const TOUCH_R = 40, MOUSE_R = 12;                    // допуск влучання, CSS px
const TAP_PX = 8, DOUBLE_MS = 320, USER_QUIET_MS = 4000;
const COMMIT_MS = 140, RESIZE_MS = 160, ANIM_MAX = 385;   // разом із чергою подій — не довше 400 мс
const PHONE_CROP_UNITS = 1150, CROP_MAX = 1.5;       // скільки одиниць мапи видно завширшки на телефоні стоячи і наскільки це можна розширити заради замовлень
const EDGE_PX = 6;                                   // поле кадру при наближенні 1

let geoPromise = null;
/** geo-map.json тягнемо один раз на сторінку; не вдалось — поле без контуру, але з шляхами. */
function loadGeo() {
  if (!geoPromise) geoPromise = fetch(MAP.url).then((r) => { if (!r.ok) throw new Error('geo-map ' + r.status); return r.json(); }).catch((e) => { console.warn('solyanyi board2d:', e); geoPromise = null; return null; });
  return geoPromise;
}

// Дані шляхів незмінні: рахуємо один раз на модуль.
let RD = null;
function routeData() {
  if (RD) return RD;
  RD = ROUTES.map((row) => {
    const id = row[0], r = routeOf(id), curve = routeCurve(id), slots = routeSlots(id), A = cityOf(r.a), B = cityOf(r.b);
    const span = curve.pts.filter((_, i) => curve.arc[i] >= A.r - 4 && curve.arc[i] <= curve.total - B.r + 4);
    const P = (pts) => 'M' + pts.map((q) => f1(q[0]) + ' ' + f1(q[1])).join('L');
    const xs = curve.pts.map((q) => q[0]), ys = curve.pts.map((q) => q[1]);
    return { ...r, curve, slots, A, B, curveD: P(curve.pts.filter((_, i) => i % 2 === 0)), spanD: P(span), mid: { x: curve.pts[32][0], y: curve.pts[32][1] }, bb: [Math.min(...xs), Math.min(...ys), Math.max(...xs), Math.max(...ys)] };
  });
  return RD;
}
const CITY = CITIES.map((_, i) => { const c = cityOf(i); return { ...c, name: c.name }; });

const baseCache = new Map();   // тема → { defs, body }: перемикання тем не перебудовує декор

function svgEl(tag, attrs) { const e = document.createElementNS(NS, tag); if (attrs) for (const k in attrs) e.setAttribute(k, attrs[k]); return e; }
const easeOut = (t) => 1 - (1 - t) ** 3;

const measureCtx = (() => { let c = null; return () => { if (c === null) { try { c = document.createElement('canvas').getContext('2d'); } catch { c = false; } } return c; }; })();
const widthMemo = new Map();
function textW(text, font, px) {
  const key = font + '|' + text;
  if (widthMemo.has(key)) return widthMemo.get(key) * px;
  const ctx = measureCtx(); let w = text.length * .58;
  if (ctx) { ctx.font = font.replace('%', '100px'); w = ctx.measureText(text).width / 100; }
  widthMemo.set(key, w);
  return w * px;
}

/**
 * Створює пласке поле в `host`. Опції: onPick, onHover, quality (не використовується), reducedMotion, theme.
 * Необов'язкове: followOthers — підводити камеру й до чужих зайнятих шляхів (за §11.3 типово ні, лише до своїх і вибору).
 */
export async function createBoard2d(host, opts = {}) {
  const raw = await loadGeo();
  const reduced = !!opts.reducedMotion;

  // ── DOM ──
  if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
  const wrap = document.createElement('div');
  wrap.className = 'sol2d';
  wrap.style.cssText = 'position:absolute;inset:0;overflow:hidden;touch-action:none;user-select:none;-webkit-user-select:none;-webkit-tap-highlight-color:transparent;contain:strict;cursor:grab';
  const svg = svgEl('svg', { xmlns: NS, role: 'img', 'aria-label': 'Мапа чумацьких шляхів України' });
  svg.style.cssText = 'position:absolute;left:0;top:0;width:100%;height:100%;overflow:visible;transform-origin:0 0;will-change:transform;pointer-events:none;display:block';
  const G = {};
  for (const name of ['defs', 'base', 'halos', 'under', 'free', 'taken', 'over', 'rims', 'cities', 'rings', 'labels', 'anim', 'top']) { G[name] = svgEl(name === 'defs' ? 'defs' : 'g'); svg.appendChild(G[name]); }
  wrap.appendChild(svg);
  host.appendChild(wrap);

  // ── стан ──
  const routes = routeData();
  let themeId = themeOf(opts.theme).id, art, ctx;
  let W = Math.max(1, wrap.clientWidth), H = Math.max(1, wrap.clientHeight);
  let disposed = false, kFit = 1, homeAll = { x: 0, y: 0 };
  let hud = (opts.hud || []).slice(), hudSig = '', crop = opts.crop ?? null, dense = opts.dense ?? null, home = (opts.home || []).slice();
  const v = { k: 1, x: 0, y: 0 };        // екран = мапа · k + (x, y)
  const c = { k: 1, x: 0, y: 0 };        // те, що записано у viewBox
  let touched = false, lastUser = -1e9, commitTimer = 0, resizeTimer = 0, flyRaf = 0, inertiaRaf = 0;
  let lastTouch = (typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches);
  const owner = new Map(); let closed = new Set(), mySeat = null, doubles = null, lastBySeat = new Map(), seatColor = new Map();
  let legal = null, legalCities = null, marks = null, tickets = [], path = null, selected = null, cursor = null, hover = null;
  let finaleWinners = null, sig = null;
  const routeEls = new Array(routes.length);
  let labelPrev = new Map(), labelsKey = '';

  const portrait = () => W < 700 || H > W * 1.1;
  // Кадр: на телефоні стоячи — ділянка замовлень; без підказки картки (стенд) — за пропорцією поля.
  const cropped = () => (crop == null ? portrait() : crop);
  const denseNow = () => (dense == null ? portrait() : dense);
  // Тісне поле на ПК: підписи менші, щоб усі 45 уміщались (на просторому — повний кегль, §11.3).
  const roomK = (k) => (denseNow() ? 1 : Math.max(0.8, Math.min(1, k / 0.21)));
  const colorOf = (seat) => { const col = seatColor.get(seat); return col == null ? seat : col; };

  // ── тема ──
  function buildTheme() {
    art = artOf(themeId);
    const pal0 = themeOf(themeId).pal, pal = { ...pal0, seat: pal0.seat || FALLBACK.seat, cargo: pal0.cargo || FALLBACK.cargo, cargoInk: pal0.cargoInk || FALLBACK.cargoInk };
    const geo = buildGeo(raw, art.geo);
    ctx = { theme: themeId, p: `s2${themeId[0]}-`, pal, geo, routes, cities: CITY };
    let b = baseCache.get(themeId);
    if (!b || b.raw !== raw) { b = { ...art.base(ctx), raw }; baseCache.set(themeId, b); }
    wrap.style.background = art.bg;
    G.defs.innerHTML = b.defs;
    G.base.innerHTML = b.body;
    G.cities.innerHTML = CITY.map((q) => art.city(q, ctx)).join('');
    // шляхи: один проклад рядків, а далі кожен <g> оновлюється сам
    let f = '', t = '';
    for (const R of routes) {
      const seat = owner.get(R.id), attr = `data-r="${R.id}"`;
      if (seat == null) f += `<g ${attr}${closed.has(R.id) ? ' opacity=".36"' : ''}>${art.route(R, ctx)}</g>`;
      else t += `<g ${attr}>${art.claimed(R, colorOf(seat), ctx)}</g>`;
    }
    G.free.innerHTML = f; G.taken.innerHTML = t;
    for (const g of [...G.free.children, ...G.taken.children]) routeEls[+g.getAttribute('data-r')] = g;
    renderHalos(); renderUnder(); renderOver(); renderRims(); renderRings(); renderTop();
    labelsKey = ''; labelPrev = new Map(); relabel();
    if (finaleWinners) applyFinale([...finaleWinners]);
  }
  const freeRoutes = () => routes.filter((R) => !owner.has(R.id) && !closed.has(R.id));
  function renderHalos() { G.halos.innerHTML = art.halos(freeRoutes(), ctx); }
  function renderRoute(id) {
    const R = routes[id], g = routeEls[id]; if (!R || !g) return;
    const seat = owner.get(id);
    g.innerHTML = seat == null ? art.route(R, ctx) : art.claimed(R, colorOf(seat), ctx);
    if (closed.has(id) && seat == null) g.setAttribute('opacity', '.36'); else g.removeAttribute('opacity');
    const parent = seat == null ? G.free : G.taken;
    if (g.parentNode !== parent) parent.appendChild(g);
  }

  // ── підсвітки ──
  const spanOf = (ids) => ids.map((id) => routes[id]?.spanD || '').join('');
  function renderUnder() {
    const pal = ctx.pal, w = art.slotW; let s = '';
    const stroke = (d, col, sw, extra = '') => d ? `<path d="${d}" fill="none" stroke="${col}" stroke-width="${sw}" stroke-linecap="round" stroke-linejoin="round"${extra}/>` : '';
    if (path && path.length) { const d = spanOf(path); s += stroke(d, pal.legalEdge, w + 26, ' opacity=".9"') + stroke(d, pal.plan, w + 18); }
    if (marks) {
      const by = { ok: [], near: [], plan: [] };
      for (const id in marks) if (by[marks[id]] && !owner.has(+id)) by[marks[id]].push(+id);
      const plan = spanOf(by.plan), near = spanOf(by.near), ok = spanOf(by.ok);
      s += stroke(plan, pal.plan, w + 10, ' stroke-opacity=".62"');
      s += stroke(near, pal.near, w + 10, ' stroke-dasharray="26 18"');
      s += stroke(ok, pal.legalEdge, w + 16, ' stroke-opacity=".75"') + stroke(ok, pal.legal, w + 10);
    }
    if (legal) {
      const d = spanOf([...legal].filter((id) => !owner.has(id)));
      s += stroke(d, pal.legalEdge, w + 18, ' stroke-opacity=".85"') + stroke(d, pal.legal, w + 11);
    }
    G.under.innerHTML = s;
  }
  function renderOver() {
    const R = selected == null ? null : routes[selected];
    G.over.innerHTML = R ? art.sel(R, ctx, owner.has(R.id) ? art.claimed(R, colorOf(owner.get(R.id)), ctx) : null) : '';
  }
  function hotCitySet() {
    const s = new Set();
    if (selected != null && routes[selected]) { s.add(routes[selected].a); s.add(routes[selected].b); }
    return s;
  }
  function orderCitySet() { const s = new Set(); for (const t of tickets) if (!t.done) { s.add(t.a); s.add(t.b); } return s; }
  function renderRims() {
    let s = ''; const hot = hotCitySet();
    for (const i of hot) s += art.rim(CITY[i]);
    if (legalCities) for (const i of legalCities) if (!hot.has(i) && CITY[i]) s += art.rim(CITY[i], ctx.pal.legal);
    G.rims.innerHTML = s;
  }
  function renderRings() {
    let s = '', n = 0;
    const seen = new Set();
    for (const t of tickets) {
      if (t.done) continue;
      for (const i of [t.a, t.b]) if (CITY[i] && !seen.has(i)) { seen.add(i); s += art.ring(CITY[i], n); }
      n++;
    }
    G.rings.innerHTML = s;
  }
  function renderTop() {
    const pal = ctx.pal; let s = '';
    const frame = (kind, id, col, dash) => {
      const ex = dash ? ' stroke-dasharray="14 10"' : '';
      if (kind === 'route' && routes[id]) s += `<path d="${art.slotsD(routes[id], art.slotW + 8)}" fill="none" stroke="${col}" stroke-width="${dash ? 8 : 6}" stroke-linejoin="round"${ex}/>`;
      else if (kind === 'city' && CITY[id]) s += `<circle cx="${CITY[id].x}" cy="${CITY[id].y}" r="${f1(art.nodeR(CITY[id]) + 12)}" fill="none" stroke="${col}" stroke-width="${dash ? 8 : 6}"${ex}/>`;
    };
    if (hover && !(hover.kind === 'route' && hover.id === selected)) frame(hover.kind, hover.id, pal.sel || pal.legal, false);
    if (cursor) { frame(cursor.kind, cursor.id, pal.legalEdge, true); frame(cursor.kind, cursor.id, pal.legal, false); }
    G.top.innerHTML = s;
  }

  // ── підписи: сталий кегль на екрані, місце шукаємо так, щоб не наїхати на шляхи, міста й чужі підписи ──
  function relabel() {
    const k = c.k, phone = denseNow(), ratio = k / kFit, hot = hotCitySet(), ord = orderCitySet();
    const heavy = new Set(path || []); if (selected != null) heavy.add(selected);
    // на телефоні підпис при краї вікна шукає місце всередину: вікно теж частина ключа
    const vx0 = -v.x / v.k, vy0 = -v.y / v.k, vx1 = vx0 + W / v.k, vy1 = vy0 + H / v.k;
    const key = [k.toFixed(4), phone, [...hot].join(), [...ord].join(), [...heavy].join(), themeId, phone ? [vx0, vy0].map(Math.round).join() : ''].join('|');
    if (key === labelsKey) return;
    labelsKey = key;
    const u = 1 / k, grow = Math.min(1.25, Math.max(1, (k / .34) ** .25));
    const showAll = !phone || ratio >= 1.6;
    // перешкоди: точки вздовж слотів і кола міст, в одиницях мапи
    const slotR = (art.slotW + 8) / 2, obst = [];
    for (const R of routes) {
      const w = heavy.has(R.id) ? 14 : 2.6, rad = heavy.has(R.id) ? (art.selW + 26) / 2 : slotR;
      for (const sl of R.slots) {
        const n = Math.max(1, Math.round(sl.len / 26)), dx = Math.cos(sl.a), dy = Math.sin(sl.a);
        for (let i = 0; i < n; i++) { const t = ((i + .5) / n - .5) * sl.len; obst.push([sl.x + dx * t, sl.y + dy * t, rad, w]); }
      }
    }
    for (const q of CITY) obst.push([q.x, q.y, art.nodeR(q) + 4 * u, 5]);
    const fontOf = (w) => `${w} % ${art.font.family}`;
    const metas = CITY.map((q) => {
      const fs = art.labelPx(q, phone) * grow * u * roomK(k), lines = labelLines(q.name), lh = fs * 1.1;
      const wTxt = Math.max(...lines.map((l) => textW(l.replace(/'/g, '’'), fontOf(art.font.weight(q)), fs)));
      const pad = art.plate ? 4 * u : 0, pady = art.plate ? 1.5 * u : 0;
      return { q, fs, lines, lh, w: wTxt + pad * 2, h: lines.length * lh + pady * 2, pady };
    });
    const rankOf = (q) => (hot.has(q.i) ? -1 : ord.has(q.i) ? 0 : q.lvl);
    const order = metas.slice().sort((a, b) => (rankOf(a.q) - rankOf(b.q)) || (b.lines.join('').length - a.lines.join('').length));
    const placed = new Map();
    const overlapCost = (x0, y0, x1, y1, self) => {
      let cost = 0;
      for (const o of self.near) {
        const dx = Math.max(x0 - o[0], 0, o[0] - x1), dy = Math.max(y0 - o[1], 0, o[1] - y1), d = dx * dx + dy * dy;
        if (d < o[2] * o[2]) { const dd = Math.sqrt(d); cost += (o[2] - dd) / o[2] * o[3]; }
      }
      for (const [m, r] of placed) if (m !== self && x0 < r.x + r.w && x1 > r.x && y0 < r.y + r.h && y1 > r.y) cost += 70;
      if (x0 < 6 || y0 < 6 || x1 > MAP.w - 6) cost += 25;
      // Типовий кадр: назва лишається в полі (§11.2).
      if (!phone && !touched && (x0 < vx0 + 2 * u || x1 > vx1 - 2 * u || y0 < vy0 + 2 * u || y1 > vy1 - 2 * u)) cost += 60;
      for (const r of hud) if (x1 * c.k + c.x > r.x0 && x0 * c.k + c.x < r.x1 && y1 * c.k + c.y > r.y0 && y0 * c.k + c.y < r.y1) cost += 60;
      if (phone && self.q.x > vx0 && self.q.x < vx1 && self.q.y > vy0 && self.q.y < vy1 && (x0 < vx0 + 4 * u || x1 > vx1 - 4 * u || y0 < vy0 + 4 * u || y1 > vy1 - 4 * u)) cost += 40;
      return cost;
    };
    for (const m of metas) {
      const R0 = 200 + m.w + m.h;
      m.near = obst.filter((o) => Math.abs(o[0] - m.q.x) < R0 && Math.abs(o[1] - m.q.y) < R0 && !(o[0] === m.q.x && o[1] === m.q.y));
    }
    for (let pass = 0; pass < 3; pass++) for (const m of order) {
      placed.delete(m);
      const q = m.q, R0 = art.nodeR(q) + 3 * u + (ord.has(q.i) ? art.ringExtra : 0) + (hot.has(q.i) ? 12 : 0), w = m.w, h = m.h;
      let best = null, bc = 1e12;
      const cands = [[R0, -h / 2, .0], [-R0 - w, -h / 2, .5], [-w / 2, R0, .8], [-w / 2, -R0 - h, 1],
        [R0 * .72, R0 * .55, 1.3], [R0 * .72, -R0 * .55 - h, 1.3], [-R0 * .72 - w, R0 * .55, 1.4], [-R0 * .72 - w, -R0 * .55 - h, 1.4],
        [R0 + 8 * u, -h / 2, 1.6], [-R0 - w - 8 * u, -h / 2, 1.8], [-w / 2, R0 + 8 * u, 2], [-w / 2, -R0 - h - 8 * u, 2.2],
        [R0 * .3, R0 + 2 * u, 2], [-w + (-R0 * .3), R0 + 2 * u, 2], [R0 * .3, -R0 - h - 2 * u, 2.2], [-w - R0 * .3, -R0 - h - 2 * u, 2.2],
        [R0 + 22 * u, -h / 2, 3.4], [-R0 - w - 22 * u, -h / 2, 3.4], [-w / 2, R0 + 20 * u, 3.4], [-w / 2, -R0 - h - 20 * u, 3.6]];
      const prev = labelPrev.get(q.i);
      cands.forEach((cd, n) => {
        const x = q.x + cd[0], y = q.y + cd[1];
        const cost = overlapCost(x, y, x + w, y + h, m) + cd[2] - (prev === n ? 1.2 : 0);
        if (cost < bc) { bc = cost; best = { x, y, w, h, n, cost }; }
      });
      placed.set(m, best);
    }
    // що ховаємо: на телефоні дрібні — до наближення 1,6; наїзд — менший рівень поступається більшому
    let s = '';
    const shown = [];
    labelPrev = new Map();
    for (const m of order) {
      const r = placed.get(m), q = m.q, must = hot.has(q.i) || ord.has(q.i);
      labelPrev.set(q.i, r.n);
      if (!must && !showAll && q.lvl > 1) continue;
      const hit = shown.some((o) => r.x < o.x + o.w && r.x + r.w > o.x && r.y < o.y + o.h && r.y + r.h > o.y);
      // Телефон: тільки те, що вміщається без наїзду; свої замовлення йдуть у черзі першими, тож ховаються останніми. ПК: поступається дрібний.
      if (hit && (phone || (!must && q.lvl > 1))) continue;
      // Обрізану при краї назву на телефоні не показуємо: місце знайдеться, щойно камера стане.
      if (phone && hud.some((q) => (r.x + r.w) * v.k + v.x > q.x0 && r.x * v.k + v.x < q.x1 && (r.y + r.h) * v.k + v.y > q.y0 && r.y * v.k + v.y < q.y1)) continue;
      if (phone && r.x + r.w > vx0 && r.x < vx1 && r.y + r.h > vy0 && r.y < vy1 && (r.x < vx0 || r.x + r.w > vx1 || r.y < vy0 || r.y + r.h > vy1)) continue;
      shown.push(r);
      s += art.label({ c: q, lines: m.lines, x: r.x, y: r.y, w: r.w, h: r.h, cx: r.x + r.w / 2, fs: m.fs, lh: m.lh, pady: m.pady, u, hot: hot.has(q.i), ord: ord.has(q.i) });
    }
    G.labels.innerHTML = s;
    labelRects = shown.map((r) => r);
    labelNames = order.filter((m) => shown.includes(placed.get(m))).map((m) => m.q.name);
  }
  let labelRects = [], labelNames = [];

  // ── камера ──
  // Точки кадру: міста з колом і половиною підпису, середини слотів. Крайні точки країни — BOX: їх ніколи не обрізаємо.
  const nameHalf = (q, k) => { const fs = (art ? art.labelPx(q, denseNow()) : 14) * roomK(k), l = labelLines(q.name); return art ? Math.max(...l.map((t) => textW(t.replace(/'/g, '’'), `${art.font.weight(q)} % ${art.font.family}`, fs))) / 2 + 4 : 30; };
  const framePts = CITY.map((q) => ({ x: q.x, y: q.y, r: null, q }));
  for (const R of routes) for (const sl of R.slots) framePts.push({ x: sl.x, y: sl.y, r: sl.len / 2, q: null });
  /** Наближення 1: найближчий кадр, де видно всю країну, усі підписи в полі, і нічого під HUD. */
  function measure() {
    W = Math.max(1, wrap.clientWidth); H = Math.max(1, wrap.clientHeight);
    let x0 = BOX.x0, x1 = BOX.x1;
    const y0 = BOX.y0, y1 = BOX.y1;
    let k0 = Math.min((W - 2 * EDGE_PX) / (x1 - x0), (H - 2 * EDGE_PX) / (y1 - y0));
    // Підпис крайнього міста теж мусить лишитись у полі: відступаємо, поки вміщається.
    for (let i = 0; i < 12; i++) {
      x0 = BOX.x0; x1 = BOX.x1;
      for (const p of framePts) if (p.q) { const half = (nameHalf(p.q, k0) + 2) / k0; x0 = Math.min(x0, p.x - half); x1 = Math.max(x1, p.x + half); }
      const k = Math.min((W - 2 * EDGE_PX) / (x1 - x0), (H - 2 * EDGE_PX) / (y1 - y0));
      if (Math.abs(k - k0) < 1e-5) break;
      k0 = k;
    }
    kFit = k0; homeAll = { x: (W - (x0 + x1) * k0) / 2, y: (H - (y0 + y1) * k0) / 2 };
    if (!hud.length || denseNow()) return;           // на телефоні HUD кадр не рухає, лише назви тікають із-під кнопок
    const clear = (k, ox, oy) => {
      for (const p of framePts) {
        const sx = p.x * k + ox, sy = p.y * k + oy, pad = (p.q ? art ? art.nodeR(p.q) : p.q.r : p.r) * k + 3;
        for (const r of hud) if (sx + pad > r.x0 && sx - pad < r.x1 && sy + pad > r.y0 && sy - pad < r.y1) return false;
      }
      return true;
    };
    const offs = (n) => Array.from({ length: 2 * n + 1 }, (_, i) => (i - n) / n).sort((a, b) => Math.abs(a) - Math.abs(b));
    const ox = offs(6), oy = offs(5);
    for (let step = 0; step < 34; step++) {
      const k = k0 / (1 + step * 0.015), cx = (W - (x0 + x1) * k) / 2, cy = (H - (y0 + y1) * k) / 2;
      const sx = Math.max(0, (W - 2 * EDGE_PX - (x1 - x0) * k) / 2), sy = Math.max(0, (H - 2 * EDGE_PX - (y1 - y0) * k) / 2);
      for (const dy of oy) for (const dx of ox) {
        const x = cx + dx * sx, y = cy + dy * sy;
        if (clear(k, x, y)) { kFit = k; homeAll = { x, y }; return; }
      }
    }
  }
  const kMax = () => kFit * ZOOM_MAX;
  function clampView(q, voidK = 1) {
    q.k = Math.max(kFit, Math.min(kMax(), q.k));
    const grow = Math.min(1, (q.k / kFit - 1) * 4) * voidK;
    const axis = (o, size, b0, b1, home0) => {
      const vw = size / q.k, voidU = VOID * vw * grow, lo = b0 - voidU, hi = b1 - vw + voidU;
      let u = -o / q.k;
      // Уся мапа влазить: центр — той, що обрав кадр (він обходить HUD), а не середина поля.
      u = lo <= hi ? Math.max(lo, Math.min(hi, u)) : (size / 2 - home0) / kFit - vw / 2;
      return -u * q.k;
    };
    q.x = axis(q.x, W, BOX.x0, BOX.x1, homeAll.x);
    q.y = axis(q.y, H, BOX.y0, BOX.y1, homeAll.y);
    return q;
  }
  function paintView() {
    const s = v.k / c.k, tx = v.x - c.x * s, ty = v.y - c.y * s;
    svg.style.transform = (tx === 0 && ty === 0 && s === 1) ? 'none' : `translate(${tx.toFixed(2)}px,${ty.toFixed(2)}px) scale(${s.toFixed(5)})`;
  }
  /** Записує масштаб у viewBox. Лише якщо він справді змінився: чистий зсув лишається транформом, вузли не чіпаємо. */
  function commit(force) {
    clearTimeout(commitTimer); commitTimer = 0;
    if (disposed) return;
    if (!force && v.k === c.k) return;
    c.k = v.k; c.x = v.x; c.y = v.y;
    svg.setAttribute('viewBox', `${(-c.x / c.k).toFixed(3)} ${(-c.y / c.k).toFixed(3)} ${(W / c.k).toFixed(3)} ${(H / c.k).toFixed(3)}`);
    paintView();
    relabel();
  }
  function setView(k, x, y) {
    v.k = k; v.x = x; v.y = y; clampView(v); paintView();
    settleSoon();
  }
  /** Заспокоїлось: новий масштаб — у viewBox; на телефоні — ще й підписи при краях. Без змін нічого не малює. */
  function settleSoon() {
    clearTimeout(commitTimer);
    commitTimer = setTimeout(() => { if (v.k !== c.k) commit(false); else if (!disposed && portrait()) relabel(); }, v.k !== c.k ? COMMIT_MS : COMMIT_MS + 60);
  }
  function zoomAt(px, py, f) {
    const k = Math.max(kFit, Math.min(kMax(), v.k * f)), g = k / v.k;
    setView(k, px - (px - v.x) * g, py - (py - v.y) * g);
  }
  function stopFly() { if (flyRaf) cancelAnimationFrame(flyRaf); flyRaf = 0; if (inertiaRaf) cancelAnimationFrame(inertiaRaf); inertiaRaf = 0; }
  function flyTo(k, x, y, ms = 360) {
    stopFly();
    const t = clampView({ k, x, y });
    if (reduced || ms <= 0) { setView(t.k, t.x, t.y); return; }
    if (Math.abs(t.k - v.k) < 1e-5 && Math.abs(t.x - v.x) < .5 && Math.abs(t.y - v.y) < .5) return;
    const f = { ...v }, t0 = performance.now();
    const step = (now) => {
      if (disposed) return;
      const p = Math.min(1, (now - t0) / Math.min(ms, ANIM_MAX)), e = easeOut(p);
      // масштаб — геометрично, центр — лінійно в одиницях мапи: виглядає рівно
      const kk = f.k * (t.k / f.k) ** e;
      const cx0 = (W / 2 - f.x) / f.k, cy0 = (H / 2 - f.y) / f.k, cx1 = (W / 2 - t.x) / t.k, cy1 = (H / 2 - t.y) / t.k;
      const cx = cx0 + (cx1 - cx0) * e, cy = cy0 + (cy1 - cy0) * e;
      v.k = kk; v.x = W / 2 - cx * kk; v.y = H / 2 - cy * kk; paintView();
      if (p < 1) flyRaf = requestAnimationFrame(step);
      else { flyRaf = 0; v.k = t.k; v.x = t.x; v.y = t.y; paintView(); settleSoon(); }
    };
    flyRaf = requestAnimationFrame(step);
  }
  const centerOn = (k, mx, my) => ({ k, x: W / 2 - mx * k, y: H / 2 - my * k });
  const allView = () => ({ k: kFit, x: homeAll.x, y: homeAll.y });
  /** Дім: на широкому полі — уся мапа; на телефоні — читабельна ділянка навколо замовлень (нема їх — уся мапа, §11.3). */
  function homeView() {
    if (!cropped()) return allView();
    const q = homeCrop(home.length ? home : tickets.filter((t) => !t.done), PHONE_CROP_UNITS, PHONE_CROP_UNITS * H / W, CROP_MAX);
    if (!q) return allView();
    const k = Math.max(kFit, Math.min(kMax(), W / q.w));
    return k <= kFit * 1.08 ? allView() : clampView(centerOn(k, q.x, q.y), 0);
  }
  const userRecently = () => performance.now() - lastUser < USER_QUIET_MS;
  /** М'яко показує рамку в одиницях мапи: уже видно — стоїмо; не влізає — трохи віддаляємось; порожньо на телефоні — наближаємось до робочого масштабу. */
  function showBox(x0, y0, x1, y1) {
    const pad = Math.min(W, H) * .1;
    const kNeed = Math.min((W - 2 * pad) / Math.max(1, x1 - x0), (H - 2 * pad) / Math.max(1, y1 - y0));
    const kWork = cropped() ? Math.max(kFit, Math.min(kMax(), W / PHONE_CROP_UNITS)) : kFit;
    const k = Math.max(kFit, Math.min(v.k < kWork ? kWork : v.k, kNeed));
    const sx0 = x0 * v.k + v.x, sy0 = y0 * v.k + v.y, sx1 = x1 * v.k + v.x, sy1 = y1 * v.k + v.y;
    if (Math.abs(k - v.k) < 1e-4 && sx0 >= pad * .5 && sy0 >= pad * .5 && sx1 <= W - pad * .5 && sy1 <= H - pad * .5) return;
    const t = centerOn(k, (x0 + x1) / 2, (y0 + y1) / 2);
    flyTo(t.k, t.x, t.y, 360);
  }
  function focusOn(kind, id) {
    if (disposed || userRecently()) return;
    if (kind === 'all') { const t = allView(); flyTo(t.k, t.x, t.y, 360); return; }
    const box = kind === 'route' ? routes[id]?.bb : kind === 'city' && CITY[id] ? [CITY[id].x - 120, CITY[id].y - 120, CITY[id].x + 120, CITY[id].y + 120] : null;
    if (kind === 'path' || kind === 'cities') {
      const pts = kind === 'path' ? (path || []).flatMap((r) => routes[r] ? [[routes[r].bb[0], routes[r].bb[1]], [routes[r].bb[2], routes[r].bb[3]]] : []) : (id || []).map((i) => CITY[i] ? [CITY[i].x, CITY[i].y] : null).filter(Boolean);
      if (!pts.length) return;
      showBox(Math.min(...pts.map((q) => q[0])) - 60, Math.min(...pts.map((q) => q[1])) - 60, Math.max(...pts.map((q) => q[0])) + 60, Math.max(...pts.map((q) => q[1])) + 60);
      return;
    }
    if (box) showBox(box[0], box[1], box[2], box[3]);
  }

  // ── влучання ──
  function slotDist(sl, mx, my) {
    const dx = mx - sl.x, dy = my - sl.y, ca = Math.cos(sl.a), sa = Math.sin(sl.a);
    const u = Math.abs(dx * ca + dy * sa) - sl.len / 2, w = Math.abs(-dx * sa + dy * ca) - sl.w / 2;
    return Math.hypot(Math.max(u, 0), Math.max(w, 0));
  }
  /** [{id, d}] за відстанню в CSS px, радіус — допуск пальця чи миші. */
  function nearRoutes(px, py, radius) {
    const mx = (px - v.x) / v.k, my = (py - v.y) / v.k, out = [];
    for (const R of routes) {
      let d = 1e9;
      for (const sl of R.slots) { const q = slotDist(sl, mx, my); if (q < d) d = q; }
      d *= v.k;
      if (d <= radius) out.push({ id: R.id, d });
    }
    const bonus = lastTouch ? 8 : 3;
    const score = (o) => o.d - (legal && legal.has(o.id) ? bonus : 0) + (closed.has(o.id) ? bonus : 0);
    out.sort((a, b) => score(a) - score(b));
    return out;
  }
  function routeAt(px, py, radius) {
    const r = radius != null ? radius : (lastTouch ? TOUCH_R : MOUSE_R);
    const list = nearRoutes(px, py, r), ids = list.map((o) => o.id);
    // парний шлях завжди поруч зі своєю парою: щоб його можна було вибрати
    for (let i = 0; i < ids.length; i++) { const t = routes[ids[i]].twin; if (t != null && !ids.includes(t)) ids.splice(i + 1, 0, t); }
    return ids;
  }
  function cityAtPx(px, py) {
    const mx = (px - v.x) / v.k, my = (py - v.y) / v.k; let best = null, bd = 1e9;
    for (const q of CITY) {
      const d = Math.hypot(q.x - mx, q.y - my) * v.k, r = Math.max(art.nodeR(q) * v.k, lastTouch ? 16 : 7);
      if (d <= r && d < bd) { bd = d; best = q.i; }
    }
    return best == null ? null : { i: best, d: bd };
  }
  /** Що під точкою: найближче. Місто лише коли палець справді на ньому; другий тап по обраному парному — його пара. */
  function pickAt(px, py) {
    const radius = lastTouch ? TOUCH_R : MOUSE_R, near = nearRoutes(px, py, radius), ct = cityAtPx(px, py);
    const hasCity = ct && (!near.length || ct.d <= Math.max(near[0].d, 2) || ct.d <= art.nodeR(CITY[ct.i]) * v.k);
    if (hasCity) return { kind: 'city', id: ct.i };
    if (near.length) {
      let id = near[0].id;
      const tw = routes[id].twin;
      if (tw != null && id === selected) id = tw;
      return { kind: 'route', id };
    }
    return { kind: null, id: null };
  }

  // ── жести ──
  const pts = new Map();
  let drag = null, pinch = null, lastTap = { t: 0, x: 0, y: 0 }, vel = { x: 0, y: 0, t: 0 };
  const local = (e) => { const b = wrap.getBoundingClientRect(); return { x: e.clientX - b.left, y: e.clientY - b.top }; };
  function markUser() { touched = true; lastUser = performance.now(); }
  function onDown(e) {
    if (e.button != null && e.button > 0) return;
    lastTouch = e.pointerType === 'touch' || e.pointerType === 'pen';
    try { wrap.setPointerCapture(e.pointerId); } catch { /* палець уже зник */ }
    stopFly();
    const p = local(e); pts.set(e.pointerId, p);
    if (pts.size === 1) { drag = { x: p.x, y: p.y, sx: p.x, sy: p.y, moved: false, t: performance.now() }; vel = { x: 0, y: 0, t: performance.now() }; if (!lastTouch) wrap.style.cursor = 'grabbing'; }
    else if (pts.size === 2) { const [a, b] = [...pts.values()]; pinch = { a: { ...a }, b: { ...b } }; if (drag) drag.moved = true; }
  }
  function onMove(e) {
    const p = local(e);
    if (!pts.has(e.pointerId)) { if (e.pointerType === 'mouse') { lastTouch = false; doHover(p.x, p.y); } return; }
    pts.set(e.pointerId, p);
    if (pts.size >= 2 && pinch) {
      const [a, b] = [...pts.values()], g = twoFinger(pinch.a, pinch.b, a, b), mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
      markUser();
      const k = Math.max(kFit, Math.min(kMax(), v.k * g.scale)), s = k / v.k;
      setView(k, mx - (mx - g.dx - v.x) * s, my - (my - g.dy - v.y) * s);
      pinch = { a: { ...a }, b: { ...b } };
      return;
    }
    if (!drag) return;
    if (!drag.moved && Math.hypot(p.x - drag.sx, p.y - drag.sy) < TAP_PX) return;
    if (!drag.moved) { drag.moved = true; drag.x = p.x; drag.y = p.y; }
    markUser();
    const dx = p.x - drag.x, dy = p.y - drag.y, now = performance.now(), dt = Math.max(1, now - vel.t);
    vel = { x: vel.x * .7 + (dx / dt) * .3, y: vel.y * .7 + (dy / dt) * .3, t: now };
    drag.x = p.x; drag.y = p.y;
    setView(v.k, v.x + dx, v.y + dy);
  }
  function onUp(e) {
    const had = pts.has(e.pointerId), p = had ? local(e) : null;
    pts.delete(e.pointerId);
    if (pts.size < 2) pinch = null;
    if (pts.size === 1) { const q = [...pts.values()][0]; drag = { x: q.x, y: q.y, sx: q.x, sy: q.y, moved: true, t: performance.now() }; vel = { x: 0, y: 0, t: performance.now() }; return; }
    if (pts.size === 0) {
      if (!lastTouch) wrap.style.cursor = 'grab';
      const d = drag; drag = null;
      if (!had || !d || e.type === 'pointercancel') return;
      if (d.moved) { glide(); return; }
      tap(p.x, p.y);
    }
  }
  /** Згасання руху після кидка: живе лише поки є швидкість. */
  function glide() {
    if (reduced || performance.now() - vel.t > 60 || Math.hypot(vel.x, vel.y) < .25) return;
    let last = performance.now();
    const step = (now) => {
      if (disposed) return;
      const dt = now - last; last = now;
      const decay = Math.exp(-dt / 190); vel.x *= decay; vel.y *= decay;
      const x0 = v.x, y0 = v.y;
      setView(v.k, v.x + vel.x * dt, v.y + vel.y * dt);
      if (Math.hypot(vel.x, vel.y) > .03 && (v.x !== x0 || v.y !== y0)) inertiaRaf = requestAnimationFrame(step); else inertiaRaf = 0;
    };
    inertiaRaf = requestAnimationFrame(step);
  }
  function tap(x, y) {
    const now = performance.now(), hit = pickAt(x, y);
    const dbl = now - lastTap.t < DOUBLE_MS && Math.hypot(x - lastTap.x, y - lastTap.y) < 24;
    lastTap = { t: dbl ? 0 : now, x, y };
    if (dbl && hit.kind == null) { touched = false; lastUser = -1e9; api.resetView(); return; }
    if (dbl && hit.kind === 'city') { markUser(); const q = CITY[hit.id], k = Math.min(kMax(), v.k * 2), t = centerOn(k, q.x, q.y); flyTo(t.k, t.x, t.y, 300); }
    opts.onPick?.(hit.kind, hit.id);
  }
  function doHover(x, y) {
    const hit = pickAt(x, y);
    if ((hover?.kind ?? null) === hit.kind && (hover?.id ?? null) === hit.id) return;
    hover = hit.kind == null ? null : hit;
    wrap.style.cursor = hit.kind == null ? 'grab' : 'pointer';
    renderTop();
    opts.onHover?.(hit.kind, hit.id);
  }
  function onLeave(e) { if (e.pointerType === 'mouse' && hover) { hover = null; renderTop(); opts.onHover?.(null, null); } }
  function onWheel(e) {
    e.preventDefault();
    const p = local(e); stopFly(); markUser();
    zoomAt(p.x, p.y, wheelZoomFactor(e.deltaY, e.deltaMode));
  }
  wrap.addEventListener('pointerdown', onDown);
  wrap.addEventListener('pointermove', onMove);
  wrap.addEventListener('pointerup', onUp);
  wrap.addEventListener('pointercancel', onUp);
  wrap.addEventListener('pointerleave', onLeave);
  wrap.addEventListener('wheel', onWheel, { passive: false });

  // ── розмір ──
  function applyResize() {
    if (disposed) return;
    const ow = W, oh = H, wasHome = !touched;
    const cx = (ow / 2 - v.x) / v.k, cy = (oh / 2 - v.y) / v.k, ratio = v.k / (kFit || 1);
    measure();
    if (wasHome) { const h = homeView(); v.k = h.k; v.x = h.x; v.y = h.y; }
    else { const k = kFit * ratio; v.k = k; v.x = W / 2 - cx * k; v.y = H / 2 - cy * k; clampView(v); }
    labelsKey = '';
    commit(true);
  }
  const ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => {
    if (wrap.clientWidth === W && wrap.clientHeight === H) return;
    clearTimeout(resizeTimer); resizeTimer = setTimeout(applyResize, RESIZE_MS);
  }) : null;
  ro?.observe(wrap);

  // ── анімації (усі ≤ 400 мс; reducedMotion — ≤ 150 мс) ──
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  function animate(el, frames, o) { try { return el.animate(frames, { fill: 'both', easing: 'ease-out', ...o }); } catch { return null; } }
  function flashOverlay(html, ms) {
    const g = svgEl('g'); g.innerHTML = html; G.anim.appendChild(g);
    animate(g, [{ opacity: 0 }, { opacity: 1, offset: .35 }, { opacity: 1, offset: .6 }, { opacity: 0 }], { duration: ms });
    return wait(ms).then(() => g.remove());
  }

  /** Вид (claimed, closed, players): ідемпотентно, без анімацій. Перемальовує лише те, що справді змінилось. */
  function applyState(view) {
    const newOwner = new Map(); for (const q of view.claimed || []) newOwner.set(q.route, q.seat);
    const newClosed = new Set(view.closed || []);
    let colorsChanged = false;
    for (const pl of view.players || []) {
      const col = pl.color == null ? pl.seat : pl.color;
      if (seatColor.get(pl.seat) !== col) { seatColor.set(pl.seat, col); colorsChanged = true; }
      const rr = pl.routes || []; lastBySeat.set(pl.seat, rr.length ? rr[rr.length - 1] : null);
    }
    mySeat = view.me && view.me.seat != null ? view.me.seat : null;
    const changed = [];
    for (const R of routes) {
      const id = R.id, was = owner.get(id), now = newOwner.get(id);
      if (was !== now || closed.has(id) !== newClosed.has(id) || (colorsChanged && now != null)) changed.push(id);
    }
    owner.clear(); for (const [a, b] of newOwner) owner.set(a, b);
    closed = newClosed;
    if (changed.length) {
      for (const id of changed) renderRoute(id);
      renderHalos(); renderUnder();
      if (selected != null && changed.includes(selected)) renderOver();
      labelsKey = '';
    }
    if (view.over && view.over.winners) { if (!finaleWinners) applyFinale(view.over.winners); }
    else if (finaleWinners) clearFinale();
    return changed.length > 0;
  }
  function applyFinale(winners) {
    finaleWinners = new Set(winners);
    for (const [id, seat] of owner) routeEls[id]?.setAttribute('opacity', finaleWinners.has(seat) ? '1' : '.4');
    G.free.setAttribute('opacity', '.55');
  }
  function clearFinale() {
    finaleWinners = null;
    for (const id of owner.keys()) routeEls[id]?.removeAttribute('opacity');
    G.free.removeAttribute('opacity');
  }
  /** Ланцюг моїх шляхів між двома містами (BFS за шляхами місця) — для сяйва виконаного замовлення. */
  function chainBetween(a, b, seat) {
    const prev = new Map([[a, null]]), q = [a];
    while (q.length) {
      const u = q.shift(); if (u === b) break;
      for (const [id, s] of owner) { if (s !== seat) continue; const R = routes[id], w = R.a === u ? R.b : R.b === u ? R.a : null; if (w != null && !prev.has(w)) { prev.set(w, [u, id]); q.push(w); } }
    }
    const out = []; let n = b; while (prev.get(n)) { const [u, id] = prev.get(n); out.push(id); n = u; }
    return out;
  }

  // ── API ──
  const api = {
    get theme() { return themeId; },
    setBoard(b) { if (!b) return; if (b.sig === sig && b.doubles === doubles) return; sig = b.sig; doubles = b.doubles; },
    setState(view) {
      if (disposed || !view) return;
      if (view.board) api.setBoard(view.board);
      const firstMe = mySeat == null && view.me && view.me.seat != null;
      applyState(view);
      relabel();
      if (firstMe && !touched && cropped()) { const h = homeView(); setView(h.k, h.x, h.y); }
    },
    setLegal(o) {
      if (disposed) return;
      legal = o && o.routes ? new Set(o.routes) : null; legalCities = o && o.cities ? new Set(o.cities) : null;
      renderUnder(); renderRims();
    },
    setMarks(m) { if (disposed) return; marks = m || null; renderUnder(); },
    setTickets(list) {
      if (disposed) return;
      tickets = (list || []).slice();
      renderRings(); labelsKey = ''; relabel();
    },
    setPath(ids) { if (disposed) return; path = ids && ids.length ? ids.slice() : null; renderUnder(); relabel(); },
    setSelected(id) {
      if (disposed) return;
      const nid = id == null ? null : id;
      if (nid === selected) return;
      selected = nid;
      renderOver(); renderRims(); renderTop(); relabel();
      if (selected != null) focusOn('route', selected);
    },
    setCursor(kind, id) { if (disposed) return; cursor = kind ? { kind, id } : null; renderTop(); if (cursor) focusOn(kind, id); },
    async claim(ev) {
      if (disposed || !ev) return;
      const id = ev.route, R = routes[id]; if (!R) return;
      const seat = ev.seat, col = colorOf(seat);
      owner.set(id, seat); lastBySeat.set(seat, id);
      if (doubles === 'one' && R.twin != null && !owner.has(R.twin) && !closed.has(R.twin)) { closed.add(R.twin); renderRoute(R.twin); }
      renderHalos(); renderUnder();
      const g = routeEls[id];
      if (g.parentNode !== G.taken) G.taken.appendChild(g);
      g.removeAttribute('opacity');
      if (seat === mySeat || mySeat == null || opts.followOthers) focusOn('route', id);
      // доріжка і вози по черзі від a до b
      const carts = R.slots.map((sl) => `<g>${art.cartOne(R, sl, col, ctx)}</g>`).join('');
      const plain = art.claimed(R, col, ctx), cartsOnly = R.slots.map((sl) => art.cartOne(R, sl, col, ctx)).join('');
      g.innerHTML = plain.replace(cartsOnly, carts);
      if (selected === id) renderOver();
      labelsKey = ''; relabel();
      const list = [...g.children].filter((n) => n.tagName === 'g');
      if (reduced) { for (const n of list) animate(n, [{ opacity: 0 }, { opacity: 1 }], { duration: 120 }); await wait(130); return; }
      const dur = 170, gap = list.length > 1 ? (ANIM_MAX - dur - 10) / (list.length - 1) : 0;
      list.forEach((n, i) => {
        const sl = R.slots[i];
        n.style.transformOrigin = `${sl.x}px ${sl.y}px`;
        animate(n, [{ opacity: 0, transform: 'scale(.35)' }, { opacity: 1, transform: 'scale(1.14)', offset: .7 }, { opacity: 1, transform: 'scale(1)' }], { duration: dur, delay: i * gap });
      });
      await wait(ANIM_MAX);
      if (disposed) return;
      g.innerHTML = plain;   // після анімації — звичайний вигляд, без зайвих обгорток
    },
    async ticketDone(ev) {
      if (disposed || !ev) return;
      const t = ticketOf(ev.ticket); if (!t) return;
      const ms = reduced ? 140 : ANIM_MAX, chain = chainBetween(t.a, t.b, ev.seat != null ? ev.seat : mySeat), pal = ctx.pal;
      const html = `<path d="${spanOf(chain)}" fill="none" stroke="${pal.legal}" stroke-width="${art.slotW + 22}" stroke-linecap="round" opacity=".85"/>` + [t.a, t.b].map((i) => art.rim(CITY[i], pal.legal)).join('');
      await flashOverlay(html, ms);
    },
    async last() {
      if (disposed) return;
      const ms = reduced ? 140 : ANIM_MAX, pal = ctx.pal, cx = (BOX.x0 + BOX.x1) / 2, cy = (BOX.y0 + BOX.y1) / 2;
      const g = svgEl('g'), dots = CITY.map((q) => art.rim(q, pal.legal)).join('');
      g.innerHTML = dots + `<circle class="w" cx="${cx}" cy="${cy}" r="2400" fill="none" stroke="${pal.legal}" stroke-width="70"/>`;
      G.anim.appendChild(g);
      animate(g, [{ opacity: 0 }, { opacity: 1, offset: .3 }, { opacity: 0 }], { duration: ms });
      const w = g.querySelector('.w');
      if (w && !reduced) { w.style.transformOrigin = `${cx}px ${cy}px`; animate(w, [{ transform: 'scale(.05)' }, { transform: 'scale(1)' }], { duration: ms }); }
      await wait(ms); g.remove();
    },
    async longest(ev) {
      if (disposed || !ev) return;
      const pal = ctx.pal, draw = reduced ? 120 : 320, fade = reduced ? 30 : 80;
      const g = svgEl('g');
      const all = Object.values(ev.routes || {}).flat().filter((id) => routes[id]);
      g.innerHTML = all.map((id) => `<path pathLength="1" d="${routes[id].spanD}" fill="none" stroke="${pal.legal}" stroke-width="${art.slotW + 20}" stroke-linecap="round" opacity=".9" stroke-dasharray="1" stroke-dashoffset="1"/>`).join('');
      G.anim.appendChild(g);
      const list = [...g.children], step = list.length ? draw / list.length : draw;
      list.forEach((n, i) => { n.style.strokeDashoffset = '1'; animate(n, [{ strokeDashoffset: 1 }, { strokeDashoffset: 0 }], { duration: step, delay: i * step, easing: 'linear' }); });
      await wait(draw);
      animate(g, [{ opacity: 1 }, { opacity: 0 }], { duration: fade });
      await wait(fade); g.remove();
    },
    async finale(over) {
      if (disposed || !over) return;
      applyFinale(over.winners || []);
      const ids = [...owner].filter(([, s]) => finaleWinners.has(s)).map(([id]) => id);
      const pal = ctx.pal, ms = reduced ? 140 : ANIM_MAX;
      if (ids.length) await flashOverlay(`<path d="${spanOf(ids)}" fill="none" stroke="${pal.legal}" stroke-width="${art.slotW + 24}" stroke-linecap="round" opacity=".85"/>`, ms);
    },
    focus(kind, id) { focusOn(kind, id); },
    zoomBy(k) { if (disposed) return; markUser(); stopFly(); const f = clampView({ k: v.k * k, x: v.x, y: v.y }); const g = f.k / v.k; const t = { k: f.k, x: W / 2 - (W / 2 - v.x) * g, y: H / 2 - (H / 2 - v.y) * g }; flyTo(t.k, t.x, t.y, 180); },
    routeAt,
    screenPos(kind, id) {
      if (disposed) return null;
      const at = (q) => ({ x: q.x * v.k + v.x, y: q.y * v.k + v.y });
      if (kind === 'route') return routes[id] ? at(routes[id].mid) : null;
      if (kind === 'city') return CITY[id] ? at(CITY[id]) : null;
      if (kind === 'seat') { const r = lastBySeat.get(id); return r != null && routes[r] ? at(routes[r].mid) : null; }
      return null;
    },
    setTheme(id) { if (disposed) return; const t = themeOf(id || DEFAULT_THEME).id; if (t === themeId) return; themeId = t; buildTheme(); api.resize(); },
    /** ⌂: додому. На телефоні «дім» — ділянка замовлень: уже там — наступний дотик показує всю мапу. */
    resetView() {
      if (disposed) return;
      const h = homeView(), atHome = !touched && Math.abs(v.k - h.k) < h.k * 0.02 && Math.abs(v.x - h.x) < 3 && Math.abs(v.y - h.y) < 3;
      if (cropped() && atHome && h.k > kFit * 1.03) { touched = true; const a = allView(); flyTo(a.k, a.x, a.y, 360); return; }
      touched = false; flyTo(h.k, h.x, h.y, 360);
    },
    /** Прямокутники HUD поверх поля (px від кута поля): кадр їх обходить. Дрібні рухи кадр не смикають. */
    setHud(list) {
      if (disposed) return;
      const next = (list || []).map((r) => ({ x0: r.x0, y0: r.y0, x1: r.x1, y1: r.y1 }));
      const sig = next.map((r) => [r.x0, r.y0, r.x1, r.y1].map((q) => Math.round(q / 12)).join()).join('|');
      if (sig === hudSig) return;
      hudSig = sig; hud = next;
      clearTimeout(resizeTimer); applyResize();
    },
    setCrop(on, tight = on) { if (disposed || (cropped() === !!on && denseNow() === !!tight)) return; crop = !!on; dense = !!tight; clearTimeout(resizeTimer); applyResize(); },
    /** Місця замовлень, навколо яких телефон відкриває мапу. Камеру рухаємо, лише поки людина її не чіпала. */
    setHome(list) {
      if (disposed) return;
      home = (list || []).slice();
      if (cropped() && !touched) { const h = homeView(); flyTo(h.k, h.x, h.y, 360); }
    },
    labelBoxes() {
      return labelRects.map((r, i) => ({ name: labelNames[i], x0: r.x * v.k + v.x, y0: r.y * v.k + v.y, x1: (r.x + r.w) * v.k + v.x, y1: (r.y + r.h) * v.k + v.y }));
    },
    resize() { if (!disposed) { clearTimeout(resizeTimer); applyResize(); } },
    dispose() {
      if (disposed) return;
      disposed = true; stopFly(); clearTimeout(commitTimer); clearTimeout(resizeTimer); ro?.disconnect();
      wrap.removeEventListener('pointerdown', onDown); wrap.removeEventListener('pointermove', onMove); wrap.removeEventListener('pointerup', onUp);
      wrap.removeEventListener('pointercancel', onUp); wrap.removeEventListener('pointerleave', onLeave); wrap.removeEventListener('wheel', onWheel);
      wrap.remove(); pts.clear();
    },
  };

  // ── старт ──
  art = artOf(themeId);
  hudSig = hud.map((r) => [r.x0, r.y0, r.x1, r.y1].map((q) => Math.round(q / 12)).join()).join('|');
  measure();
  const h0 = homeView(); v.k = h0.k; v.x = h0.x; v.y = h0.y;
  c.k = v.k; c.x = v.x; c.y = v.y;
  svg.setAttribute('viewBox', `${(-c.x / c.k).toFixed(3)} ${(-c.y / c.k).toFixed(3)} ${(W / c.k).toFixed(3)} ${(H / c.k).toFixed(3)}`);
  buildTheme();
  return api;
}
