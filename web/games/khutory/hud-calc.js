// «Хутори» — чисті розрахунки картки столу (без DOM): підказка вершини, «Глек радить», пропозиція скидання, банк,
// графік підсумку. Правил тут нема — сервер вирішує сам; це лише те, що показати людині, щоб вона зрозуміла хід.
// Покрито тестами: web/games/khutory/dev/hud-calc.test.mjs (node --test).

import { RES, PIPS, TERRAIN_RES, RES_NAME } from './consts.js';

/** Форми іменників ресурсів для «1 дерево · 2 дерева · 5 дерев». */
const FORMS = {
  wood: ['дерево', 'дерева', 'дерев'],
  clay: ['глина', 'глини', 'глин'],
  wool: ['вовна', 'вовни', 'вовни'],
  grain: ['зерно', 'зерна', 'зерна'],
  ore: ['залізо', 'заліза', 'заліза'],
};

function form(n, f) {
  const a = Math.abs(n) % 100, b = a % 10;
  if (a > 10 && a < 20) return f[2];
  if (b === 1) return f[0];
  if (b >= 2 && b <= 4) return f[1];
  return f[2];
}

/** «2 глини» */
export function resPhrase(res, n) { return n + ' ' + form(n, FORMS[res] || [res, res, res]); }

/** { wood: 1, ore: 2 } → «1 дерево, 2 заліза» (порядок — як у руці; нулі пропускаються). */
export function resList(obj, sep = ', ') {
  const out = [];
  for (const r of RES) if (obj && obj[r] > 0) out.push(resPhrase(r, obj[r]));
  return out.join(sep) || 'нічого';
}

export function total(hand) {
  let s = 0;
  for (const r of RES) s += (hand && hand[r]) || 0;
  return s;
}

/** Для кожної вершини — гекси, яких вона торкається. */
export function vertexHexes(board) {
  const out = board.vx.map(() => []);
  board.hexes.forEach((h, i) => { for (const v of h.v) if (out[v].indexOf(i) < 0) out[v].push(i); });
  return out;
}

const vhCache = new WeakMap();
function vh(board) {
  let m = vhCache.get(board);
  if (!m) { m = vertexHexes(board); vhCache.set(board, m); }
  return m;
}

/**
 * Що родить вершина: { items: [{ hex, res, num, pips, robbed }], pips, pct }.
 * robber — id гекса з Кармелюком (його числа зараз не родять; в pips не рахуємо).
 */
export function vertexYield(board, v, robber = null) {
  const items = [];
  let pips = 0;
  for (const h of vh(board)[v] || []) {
    const hx = board.hexes[h];
    const res = TERRAIN_RES[hx.t];
    if (!res || !hx.num) continue;
    const p = PIPS[hx.num] || 0;
    const robbed = h === robber;
    items.push({ hex: h, res, num: hx.num, pips: p, robbed });
    if (!robbed) pips += p;
  }
  items.sort((a, b) => b.pips - a.pips);
  return { items, pips, pct: Math.round(pips / 36 * 100) };
}

/** «дерево (4) · вовна (5) — родить у 19 % кидків» — для підказки «Хата тут дасть: …». */
export function yieldText(y) {
  if (!y.items.length) return 'нічого — тут лише Дике поле та море';
  const parts = y.items.map((it) => RES_NAME[it.res] + ' (' + it.num + (it.robbed ? ', там Кармелюк' : '') + ')');
  return parts.join(' · ') + ' — родить у ' + y.pct + ' % кидків';
}

/** Пристань при вершині: { kind, rate } або null. */
export function portAt(board, v) {
  for (const p of board.ports || []) {
    const e = board.edges[p.e];
    if (e && (e[0] === v || e[1] === v)) return p;
  }
  return null;
}

/**
 * «Глек радить»: k найкращих вершин зі spots. Бал = крапки + різноманіття (по 1,2 за кожен різний ресурс)
 * + 0,6 за пристань; рівні — менший id (стабільно). have — уже мої ресурси з хат (друга хата добирає чого бракує).
 */
export function adviseTowns(board, spots, k = 3, have = null) {
  const scored = [];
  for (const v of spots || []) {
    const y = vertexYield(board, v);
    if (!y.pips) continue;
    const kinds = new Set(y.items.map((it) => it.res));
    let score = y.pips + 1.2 * kinds.size + (portAt(board, v) ? 0.6 : 0);
    if (have) for (const r of kinds) if (!have[r]) score += 0.8;
    scored.push({ v, score });
  }
  scored.sort((a, b) => b.score - a.score || a.v - b.v);
  return scored.slice(0, k).map((s) => s.v);
}

/**
 * Готова пропозиція скидання: n карт, щоразу з найчисленнішого ресурсу (рівні — з того, що далі в RES:
 * залізо й зерно бережемо останніми). Повертає повний об'єкт за RES.
 */
export function discardPlan(hand, n) {
  const left = {}, out = {};
  for (const r of RES) { left[r] = (hand && hand[r]) || 0; out[r] = 0; }
  const keep = ['wood', 'clay', 'wool', 'grain', 'ore'];   // порядок «що віддати першим» при рівності
  for (let i = 0; i < n; i++) {
    let best = null;
    for (const r of keep) if (left[r] > 0 && (best === null || left[r] > left[best])) best = r;
    if (best === null) break;
    left[best]--; out[best]++;
  }
  return out;
}

/** Лише ненульові — так payload `discard`/`offer` чистіший. */
export function compact(obj) {
  const o = {};
  for (const r of RES) if (obj && obj[r] > 0) o[r] = obj[r];
  return o;
}

/** Банк: що можна віддати й за яким курсом, найвигідніше — першим. */
export function bankChoices(hand, rates) {
  return RES.map((r) => ({ res: r, rate: (rates && rates[r]) || 4, have: (hand && hand[r]) || 0 }))
    .map((c) => ({ ...c, can: c.have >= c.rate, max: Math.floor(c.have / c.rate) }))
    .sort((a, b) => (b.can - a.can) || (a.rate - b.rate) || (RES.indexOf(a.res) - RES.indexOf(b.res)));
}

/** Скільки бракує на ціну: { ore: 3 } або {} — лише для кружечків на кнопках (причину пише сервер). */
export function lacking(hand, cost) {
  const o = {};
  for (const r of RES) {
    const need = (cost && cost[r]) || 0, have = (hand && hand[r]) || 0;
    if (need > have) o[r] = need - have;
  }
  return o;
}

/**
 * Графік очок за колами: polyline-точки для кожного місця в прямокутнику w×h з полями pad.
 * timeline — [[очки за місцями після кола]]; target — межа (пунктир).
 */
export function chartGeom(timeline, w, h, target = 0, pad = 22) {
  const rows = timeline || [];
  const seats = rows.length ? rows[0].length : 0;
  let max = target || 0;
  for (const row of rows) for (const p of row) if (p > max) max = p;
  max = Math.max(1, max);
  const n = Math.max(1, rows.length - 1);
  const x = (i) => Math.round((pad + (w - 2 * pad) * i / n) * 10) / 10;
  const y = (p) => Math.round((h - pad - (h - 2 * pad) * p / max) * 10) / 10;
  const lines = [];
  for (let s = 0; s < seats; s++) lines.push(rows.map((row, i) => [x(i), y(row[s] || 0)]));
  return { lines, max, x, y, targetY: target ? y(target) : null };
}
