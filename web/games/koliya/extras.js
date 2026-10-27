// «Колія» — додатки за перемикачем (K11). Обидва типово ВИМКНЕНІ й тоді не чіпають стан (ні rng, ні світ).
// Чисті й детерміновані: лише rnd(s) з rules.js, цілі числа, без Math.random/Date/DOM/three.
//
// 🪿 Гуси (geese: 'on'): раз на geeseSec з бокового краю смуги приходить зграйка 1..geeseMax гусей, кожна йде до
//   інструмента, що лежить на землі (сокира/кайло/відро), хапає й тікає до свого краю. use гравця поруч (на клітинку
//   перед собою або впритул) лякає гуску — річ падає, гуска тікає порожня. Гуска дійшла до краю з річчю — річ падає
//   на випадкову вільну клітинку ПОПЕРЕДУ паротяга, досяжну пішки від гравців (BFS без рубання), тож гра не глухне.
//   Гуси — не перешкода. Стан: s.geese = [{ id, x, y (підодиниці), hold (I.*), mode: 'go'|'run'|'flee', t (тиків живе),
//   tx, ty (ціль-клітинка), side (-1 верх, 1 низ), sp (підодиниць/тик) }], s.geeseT (тиків до зграї), s.geeseId.
//
// 🏪 Станція з покращеннями (stationMode: 'upgrade'): до смуги дописується друга (той самий генератор, інший сід),
//   станція першої стає проміжною: підлога SAND (а не STATION — інакше перемога), рамка в s.mid. Паротяг, що в'їхав
//   головою на проміжну, стоїть (extrasStep повертає true — rules пропускає stepTrain), доки хтось не зробить use
//   на одну з табличок над платформою (3 з 4 покращень, вибір за rnd). Тоді покращення діє, потяг рушає.
//   s.mid = { x0, y0, x1, y1, signs: [{ x, y, k }], pick: '' | ключ UPGRADES, wait (0/1), len1 (довжина першої смуги) }
//
// Гачки в rules.js: newGame → extrasInit(s, params) перед return; step → if (!extrasStep(s, cmds)) stepTrain(s);
//   use → після обчислення клітинки: if (extrasUse(s, n, {x: cx, y: cy})) { pl.cd = ACT_CD; return; }
// УВАГА: rules.js імпортує цей файл, а цей — rules.js (цикл). Тому на верхньому рівні тут — лише літерали, без G/I/U.
import { U, TPS, G, O, I, rnd, wagonPos } from './rules.js';
import { generate } from './gen.js';

export const EXTRA_PARAMS = {
  geeseSec:   { def: 45,  min: 10, max: 180, step: 5,   label: 'Гуси: зграя раз на, с', group: 'Додатки' },
  geeseMax:   { def: 3,   min: 1,  max: 3,   step: 1,   label: 'Гуси: у зграї до', group: 'Додатки' },
  geeseSpeed: { def: 2.5, min: 1,  max: 6,   step: 0.5, label: 'Гуси: швидкість, клітинок/с', group: 'Додатки' },
};
export const EXTRA_VARIANTS = {
  geese:       { values: ['off', 'on'], labels: ['Ні', 'Так'], label: 'Гуси-злодюжки 🪿' },
  stationMode: { values: ['end', 'upgrade'], labels: ['Кінець', 'Проміжна з покращеннями'], label: 'Станція 🏪' },
};

/** Покращення проміжної станції: ключ → { label, icon } (застосування — applyUpgrade) */
export const UPGRADES = {
  tank:  { icon: '🛢', label: 'Цистерна: котел гріється вдвічі повільніше' },
  craft: { icon: '⚒', label: 'Майстерня вдвічі швидша' },
  store: { icon: '📦', label: 'Склад більший у півтора раза' },
  carry: { icon: '💪', label: '+1 до того, скільки несеш' },
};
const UP_KEYS = ['tank', 'craft', 'store', 'carry'];

const par = (s, k) => {
  const v = s.params[k];
  if (v !== undefined) return v;
  return EXTRA_PARAMS[k] ? EXTRA_PARAMS[k].def : EXTRA_VARIANTS[k].values[0];
};
const ev = (s, t, x, y, p) => { const e = { t, x, y }; if (p !== undefined) e.p = p; s.ev.push(e); };
const isTool = (k) => k === I.AXE || k === I.PICK || k === I.BUCKET || k === I.BUCKET_FULL;
const rndN = (s, n) => rnd(s) % n;

/** Ціла частина кореня (Ньютон) — без Math.sqrt, щоб однаково всюди */
export function isqrt(n) {
  if (n <= 0) return 0;
  let x = n, y = Math.floor((x + 1) / 2);
  while (y < x) { x = y; y = Math.floor((x + Math.floor(n / x)) / 2); }
  return x;
}

// ================= ініціалізація =================
/** Кінець newGame: готує поля додатків. Вимкнене — лише порожні поля, rng не чіпаємо. */
export function extrasInit(s, params = s.params) {
  for (const k in EXTRA_PARAMS) if (s.params[k] === undefined) s.params[k] = params[k] ?? EXTRA_PARAMS[k].def;
  for (const k in EXTRA_VARIANTS) if (s.params[k] === undefined) s.params[k] = params[k] ?? EXTRA_VARIANTS[k].values[0];
  s.geese = [];
  s.geeseId = 0;
  s.geeseT = Math.max(1, Math.round(par(s, 'geeseSec') * TPS));
  s.mid = null;
  if (par(s, 'stationMode') === 'upgrade') addMidStation(s);
  return s;
}

/** Дописує другу смугу праворуч; станція першої — проміжна */
function addMidStation(s) {
  const w1 = s.w, h = s.h;
  // рамка станції першої смуги
  let x0 = w1, y0 = h, x1 = -1, y1 = -1;
  for (let y = 0; y < h; y++) for (let x = 0; x < w1; x++) if (s.ground[y * w1 + x] === G.STATION) {
    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
  }
  if (x1 < 0) { x0 = w1 - 3; x1 = w1 - 1; y0 = (h >> 1) - 1; y1 = (h >> 1) + 1; }
  const b = generate((s.seed ^ 0x5EED5EED) >>> 0, s.params);
  const w2 = b.h === h ? b.w : 0;
  const W = w1 + w2, n = W * h;
  const cat = (a, c) => {
    const r = new Uint8Array(n);
    for (let y = 0; y < h; y++) { r.set(a.subarray(y * w1, y * w1 + w1), y * W); if (w2) r.set(c.subarray(y * w2, y * w2 + w2), y * W + w1); }
    return r;
  };
  // інструменти другої смуги прибираємо (вони вже є в першій)
  for (let i = 0; i < b.drop.length; i++) if (isTool(b.drop[i])) { b.drop[i] = I.NONE; b.dropN[i] = 0; }
  s.ground = cat(s.ground, b.ground); s.obj = cat(s.obj, b.obj); s.hp = cat(s.hp, b.hp);
  s.drop = cat(s.drop, b.drop); s.dropN = cat(s.dropN, b.dropN);
  s.path = s.path.map((c) => ((c / w1) | 0) * W + (c % w1));
  s.w = W;
  // платформа: SAND (не STATION — інакше rules оголосить перемогу); довкола чисто
  for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) s.ground[y * W + x] = G.SAND;
  const sy = y0 - 2 >= 1 ? y0 - 2 : y1 + 2;
  for (let y = Math.max(1, Math.min(y0, sy) - 1); y <= Math.min(h - 2, Math.max(y1, sy) + 1); y++)
    for (let x = x0 - 1; x <= x1 + 1; x++) {
      const i = y * W + x;
      if (s.obj[i] !== O.WALL) { s.obj[i] = O.NONE; s.hp[i] = 0; }
      if (s.ground[i] === G.WATER) s.ground[i] = G.GRASS;
    }
  // 3 з 4 покращень (за rnd стану)
  const keys = UP_KEYS.slice();
  keys.splice(rndN(s, keys.length), 1);
  const signs = keys.map((k, j) => ({ x: x0 + j, y: sy, k }));
  for (const g of signs) { const i = g.y * W + g.x; s.drop[i] = I.NONE; s.dropN[i] = 0; }
  s.mid = { x0, y0, x1, y1, signs, pick: '', wait: 0, len1: w1 };
}

// ================= крок =================
/**
 * Після руху гравців і майстерні, до паротяга. Повертає true, якщо паротяг цього тику стоїть (проміжна станція) —
 * тоді rules пропускає stepTrain.
 */
export function extrasStep(s, cmds) {
  if (s.status !== 'run') return false;
  if (s.geese && par(s, 'geese') === 'on') stepGeese(s);
  return s.mid ? stepMid(s) : false;
}

function headCell(s) {
  const t = s.train;
  const hi = Math.max(0, Math.min(s.path.length - 1, Math.floor(t.pos / U)));
  const c = s.path[hi];
  return { x: c % s.w, y: (c / s.w) | 0 };
}

function stepMid(s) {
  const m = s.mid;
  if (m.pick) return false;
  const h = headCell(s);
  const at = h.x >= m.x0 && h.x <= m.x1 && h.y >= m.y0 && h.y <= m.y1;
  if (!at) return false;
  if (!m.wait) { m.wait = 1; ev(s, 'whistle', h.x, h.y); }
  s.train.speed = 0;
  return true;
}

// ---- гуси ----
function stepGeese(s) {
  if (--s.geeseT <= 0) spawnFlock(s);
  for (let j = s.geese.length - 1; j >= 0; j--) {
    const g = s.geese[j];
    g.t++;
    if (g.mode === 'go') {
      let i = g.ty * s.w + g.tx;
      if (!isTool(s.drop[i])) {          // хтось забрав — шукаємо іншу річ поблизу, нема — геть
        const c = nearestTool(s, g, 12);
        if (c) { g.tx = c.x; g.ty = c.y; i = g.ty * s.w + g.tx; } else { g.mode = 'flee'; g.sp = fleeSp(s); continue; }
      }
      if (moveTo(g, g.tx * U + U / 2, g.ty * U + U / 2)) {
        g.hold = s.drop[i]; s.drop[i] = I.NONE; s.dropN[i] = 0;
        g.mode = 'run'; g.sp = Math.max(1, Math.round(g.sp * 4 / 5));
        ev(s, 'honk', g.tx, g.ty);
      }
    } else {
      const ey = g.side < 0 ? -U / 2 : s.h * U + U / 2;
      if (moveTo(g, g.x, ey)) {
        if (g.hold) {
          const c = lostCell(s);
          if (!c) continue;                  // нікуди — тупцює на краю до наступного тику
          const i = c.y * s.w + c.x;
          s.drop[i] = g.hold; s.dropN[i] = 1;
          ev(s, 'goose_lost', c.x, c.y);
        }
        s.geese.splice(j, 1);
      }
    }
  }
}

const walkSp = (s) => Math.max(1, Math.round(par(s, 'geeseSpeed') * U / TPS));
const fleeSp = (s) => Math.max(1, Math.round(par(s, 'geeseSpeed') * U * 3 / (2 * TPS)));

/** Крок до точки; true — дійшла */
function moveTo(g, x, y) {
  const dx = x - g.x, dy = y - g.y;
  const d = isqrt(dx * dx + dy * dy);
  if (d <= g.sp) { g.x = x; g.y = y; return true; }
  g.x += Math.trunc(dx * g.sp / d); g.y += Math.trunc(dy * g.sp / d);
  return false;
}

function toolCells(s) {
  const r = [];
  for (let i = 0; i < s.drop.length; i++) if (isTool(s.drop[i])) r.push(i);
  return r;
}

function nearestTool(s, g, maxR) {
  const gx = Math.floor(g.x / U), gy = Math.floor(g.y / U);
  let best = null, bd = maxR + 1;
  for (const i of toolCells(s)) {
    const x = i % s.w, y = (i / s.w) | 0, d = Math.abs(x - gx) + Math.abs(y - gy);
    if (d < bd) { bd = d; best = { x, y }; }
  }
  return best;
}

function spawnFlock(s) {
  s.geeseT = Math.max(1, Math.round(par(s, 'geeseSec') * TPS));
  const tools = toolCells(s);
  if (!tools.length || s.geese.length >= 3) { s.geeseT = 2 * TPS; return; }   // нема чого красти — спробуємо згодом
  const n = Math.min(3 - s.geese.length, 1 + rndN(s, Math.max(1, Math.min(3, par(s, 'geeseMax') | 0))));
  const side = rndN(s, 2) ? 1 : -1;
  for (let j = 0; j < n; j++) {
    const c = tools[rndN(s, tools.length)];
    const tx = c % s.w, ty = (c / s.w) | 0;
    const x = Math.max(U / 2, Math.min(s.w * U - U / 2, tx * U + U / 2 + (rndN(s, 5) - 2) * U));
    s.geese.push({ id: ++s.geeseId, x, y: side < 0 ? -U / 2 : s.h * U + U / 2, hold: I.NONE, mode: 'go', t: 0,
                   tx, ty, side, sp: walkSp(s) });
  }
  ev(s, 'honk', tools[0] % s.w, side < 0 ? 0 : s.h - 1);
}

/**
 * Куди падає вкрадена річ: випадкова вільна клітинка попереду голови (3..30 стовпців), досяжна пішки від
 * будь-якого гравця без рубання/мостів. Нема попереду — будь-яка досяжна вільна ближче.
 */
export function lostCell(s) {
  const { w, h } = s, n = w * h;
  const seen = new Uint8Array(n), q = [];
  const pass = (i) => s.obj[i] === O.NONE && s.ground[i] !== G.WATER;
  for (const p of s.players) {
    if (p.out) continue;
    const x = Math.floor(p.x / U), y = Math.floor(p.y / U);
    if (x < 0 || y < 0 || x >= w || y >= h) continue;
    const i = y * w + x;
    if (!seen[i]) { seen[i] = 1; q.push(i); }
  }
  for (let k = 0; k < q.length; k++) {
    const i = q[k], x = i % w, y = (i / w) | 0;
    if (x > 0 && !seen[i - 1] && pass(i - 1)) { seen[i - 1] = 1; q.push(i - 1); }
    if (x < w - 1 && !seen[i + 1] && pass(i + 1)) { seen[i + 1] = 1; q.push(i + 1); }
    if (y > 0 && !seen[i - w] && pass(i - w)) { seen[i - w] = 1; q.push(i - w); }
    if (y < h - 1 && !seen[i + w] && pass(i + w)) { seen[i + w] = 1; q.push(i + w); }
  }
  const onPath = new Set(s.path);
  const sign = new Set((s.mid ? s.mid.signs : []).map((g) => g.y * w + g.x));
  const hx = Math.floor(wagonPos(s, 0).x / U);
  const ok = (i) => pass(i) && s.drop[i] === I.NONE && !onPath.has(i) && !sign.has(i) && s.ground[i] !== G.STATION;
  const ahead = [], near = [];
  for (const i of q) {
    if (!ok(i)) continue;
    const x = i % w;
    if (x >= hx + 3 && x <= hx + 30) ahead.push(i); else near.push(i);
  }
  const pool = ahead.length ? ahead : near;
  if (!pool.length) return null;
  const i = pool[rndN(s, pool.length)];
  return { x: i % w, y: (i / w) | 0 };
}

// ================= use =================
/**
 * Дія гравця p на клітинку cell ({x,y}). true — дію з'їв додаток (гуску налякано / обрано покращення),
 * rules далі нічого не робить.
 */
export function extrasUse(s, p, cell) {
  const pl = s.players[p];
  if (!pl) return false;
  if (s.geese && s.geese.length && scare(s, p, pl, cell)) return true;
  if (s.mid && !s.mid.pick) {
    const g = s.mid.signs.find((g) => g.x === cell.x && g.y === cell.y);
    if (g) { applyUpgrade(s, g.k); s.mid.pick = g.k; ev(s, 'upgrade', g.x, g.y, p); return true; }
  }
  return false;
}

function scare(s, p, pl, cell) {
  const cx = cell.x * U + U / 2, cy = cell.y * U + U / 2;
  const R1 = (U * 4) / 5, R2 = (U * 11) / 10;
  let hit = false;
  for (const g of s.geese) {
    if (g.mode === 'flee') continue;
    const d1 = isqrt((g.x - cx) ** 2 + (g.y - cy) ** 2), d2 = isqrt((g.x - pl.x) ** 2 + (g.y - pl.y) ** 2);
    if (d1 > R1 && d2 > R2) continue;
    hit = true;
    if (g.hold) {
      const c = dropNear(s, Math.floor(g.x / U), Math.floor(g.y / U)) || lostCell(s);
      if (c) { const i = c.y * s.w + c.x; s.drop[i] = g.hold; s.dropN[i] = 1; }
      g.hold = I.NONE;
    }
    g.mode = 'flee'; g.sp = fleeSp(s);
    ev(s, 'scare', Math.floor(g.x / U), Math.floor(g.y / U), p);
  }
  return hit;
}

/** Найближча вільна клітинка (кільця до 3) — куди гуска впустила річ */
function dropNear(s, x0, y0) {
  const onPath = new Set(s.path);
  const free = (x, y) => {
    if (x < 0 || y < 0 || x >= s.w || y >= s.h) return false;
    const i = y * s.w + x;
    return s.obj[i] === O.NONE && s.ground[i] !== G.WATER && s.drop[i] === I.NONE && !onPath.has(i);
  };
  for (let r = 0; r <= 3; r++) for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
    if (Math.max(Math.abs(dx), Math.abs(dy)) !== r) continue;
    if (free(x0 + dx, y0 + dy)) return { x: x0 + dx, y: y0 + dy };
  }
  return null;
}

export function applyUpgrade(s, k) {
  const P = s.params, t = s.train;
  if (k === 'tank') { P.heatSec = P.heatSec * 2; if (t.ht) t.ht *= 2; }
  else if (k === 'craft') { P.craftSec = Math.max(0.5, P.craftSec / 2); if (t.craftT) t.craftT = Math.ceil(t.craftT / 2); }
  else if (k === 'store') P.storeMax = (P.storeMax | 0) + Math.max(3, Math.round((P.storeMax | 0) / 2));
  else if (k === 'carry') P.carryMax = (P.carryMax | 0) + 1;
}
