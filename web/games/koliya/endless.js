// «Колія» — нескінченна колія (K19): stationMode = 'endless'. Станція за станцією, рахунок — скільки проїхали.
// Чисте й детерміноване: лише generate() від сіду й номера ділянки, rnd(s) і цілі числа (без Math.random/Date/DOM/three).
//
// Як влаштовано:
//  • Світ — «вікно» з ДВОХ ділянок однакової довжини L (= stripLen): s.w = 2·L сталий усю партію (рендер не мусить
//    уміти змінну ширину, пам'ять не росте). Ділянка k — generate(segSeed(seed, k), segParams(params, k)): нульова —
//    той самий світ, що й у звичайній грі; далі складність росте (hardGrow, вода, скелі, болото — до стелі на 8-й).
//    Інструменти лежать лише в нульовій.
//  • Станція в кінці кожної ділянки (3×3, G.STATION). Колію довели до її стовпців — станція стає піском (SAND): так
//    rules не оголосить перемогу, а бот (ціль — найближча G.STATION) веде колію до наступної. Рамки станцій — s.endless.st.
//  • Паротяг головою дійшов до стовпця станції → зупинка STOP_SEC (s.endless.stop тиків; rules пропускає stepTrain,
//    як на проміжній станції K11), stations+1, подія 'station'. Під час зупинки — таблички покращень K11 (s.mid, той
//    самий extrasUse/applyUpgrade): обрав — рушає за PICK_SEC; покращення зі стелею (як у панелі) не пропонуються.
//  • Хвіст потяга відійшов від межі ділянок на MARGIN клітинок → ЗСУВ: першу ділянку відрізаємо, усе сунемо ліворуч
//    на L (масиви — copyWithin, колія — idx − L, path без відрізаного початку, train.pos −= cut·U, гравці/гуси/таблички —
//    −L клітинок), праворуч дописуємо наступну. Хто лишився позаду — переносимо до хвоста; інструменти з відрізаного
//    теж. Подія { t: 'shift', x: L } — сигнал рендеру перебудувати сцену (main.js: view.setWorld(s)).
//  • Поразка — як завжди (сходить з рейок/пожежа); фінальний рахунок — s.endless.stations.
//
// Стан (для HUD; усе цілі): s.endless = { L, base (номер ділянки в лівій половині вікна), stations (проїхано станцій),
//   segment (1-based номер ділянки, де голова паротяга), conn (до скількох станцій довели колію), stop (тиків зупинки,
//   0 — їде), stopMax, toNext (клітинок від голови до наступної станції), shift (скільки разів зсували), dx (на скільки
//   клітинок останній зсув), st: [{x0,y0,x1,y1}, …] — рамки станцій обох ділянок вікна (координати вікна) }.
//   Решту (найкращий рахунок) — HUD у localStorage: правила про браузер не знають.
// Гачки: extras.js — extrasInit/extrasStep кличуть endlessInit/endlessStep; rules.js — VARIANTS.stationMode +'endless',
//   hash() домішує endlessHash(s).
// УВАГА: цикл імпорту rules → extras → endless → rules: на верхньому рівні тут лише літерали.
import { U, TPS, G, O, I, WAGONS, rnd, wagonPos, wagonAt, noFoot } from './rules.js';
import { generate } from './gen.js';
import { upsStation } from './extras.js';   // K48: гальма заряджаються на кожній станції (цикл імпорту — лише у функції)

export const STOP_SEC = 6;       // зупинка на станції, с
export const PICK_SEC = 1;       // обрали покращення — рушаємо за стільки
const UP_KEYS = ['tank', 'craft', 'store', 'carry', 'cist', 'magnet', 'brake'];   // K48: + цистерна, магніт, гальма
const isTool = (k) => k === 4 || k === 5 || k === 6 || k === 7;   // I.AXE, I.PICK, I.BUCKET, I.BUCKET_FULL
const ev = (s, t, x, y, p) => { const e = { t, x, y }; if (p !== undefined) e.p = p; s.ev.push(e); };

/** Сід ділянки k (0 — сам сід партії) */
export function segSeed(seed, k) {
  if (!k) return seed >>> 0;
  let h = (seed ^ Math.imul(k, 0x9E3779B1) ^ 0x51ED270B) >>> 0;
  h = Math.imul(h ^ (h >>> 16), 0x85EBCA6B);
  h = Math.imul(h ^ (h >>> 13), 0xC2B2AE35);
  return (h ^ (h >>> 16)) >>> 0;
}

/** Параметри генератора для ділянки k: складність росте до 8-ї ділянки (k = 0 — без змін) */
export function segParams(P, k) {
  const g = Math.min(k, 8), up = (v, f, cap) => { v = +v || 0; return Math.max(v, Math.min(cap, v * f)); };
  const hg = +P.hardGrow || 0;
  return { ...P,
    hardGrow: Math.max(hg, Math.min(1.5, hg + 0.12 * g)),
    water: up(P.water, 1 + 0.08 * g, 0.3),
    rocks: up(P.rocks, 1 + 0.04 * g, 0.45),
    swamp: up(P.swamp, 1 + 0.08 * g, 0.3),
  };
}

/** Ділянка k від сіду й номера: світ generate() без інструментів (крім нульової) */
export function segment(seed, P, k) {
  const b = generate(segSeed(seed, k), segParams(P, k));
  if (k) for (let i = 0; i < b.drop.length; i++) if (isTool(b.drop[i])) { b.drop[i] = I.NONE; b.dropN[i] = 0; }
  return b;
}

/** Рамка станції (G.STATION) у смузі шириною w; нема — типова біля кінця */
function stationFrame(ground, w, h) {
  let x0 = w, y0 = h, x1 = -1, y1 = -1;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (ground[y * w + x] === G.STATION) {
    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
  }
  if (x1 < 0) { x0 = w - 3; x1 = w - 1; y0 = (h >> 1) - 1; y1 = (h >> 1) + 1; }
  return { x0, y0, x1, y1 };
}
const moved = (f, dx) => ({ x0: f.x0 + dx, y0: f.y0, x1: f.x1 + dx, y1: f.y1 });
const zonesOf = (zs, dx) => (zs || []).map((z) => ({ ...z, x0: z.x0 + dx, x1: z.x1 + dx }));

/** Кінець newGame (через extrasInit): вікно з ділянок 0 і 1 */
export function endlessInit(s) {
  const L = s.w, h = s.h, W = 2 * L;
  const st0 = stationFrame(s.ground, L, h);
  const b = segment(s.seed, s.params, 1);
  const cat = (a, c) => {
    const r = new Uint8Array(W * h);
    for (let y = 0; y < h; y++) { r.set(a.subarray(y * L, y * L + L), y * W); r.set(c.subarray(y * L, y * L + L), y * W + L); }
    return r;
  };
  s.ground = cat(s.ground, b.ground); s.obj = cat(s.obj, b.obj); s.hp = cat(s.hp, b.hp);
  s.drop = cat(s.drop, b.drop); s.dropN = cat(s.dropN, b.dropN);
  s.path = s.path.map((c) => ((c / L) | 0) * W + (c % L));
  s.w = W;
  s.zones = [...zonesOf(s.zones, 0), ...zonesOf(b.zones, L)];
  s.endless = { L, base: 0, stations: 0, segment: 1, conn: 0, stop: 0, stopMax: Math.round(STOP_SEC * TPS), toNext: 0,
                shift: 0, dx: 0, st: [st0, moved(stationFrame(b.ground, L, h), L)] };
  info(s);
  return s;
}

const MARGIN = (L) => Math.max(8, Math.min(30, (L / 3) | 0));
const headX = (s) => Math.floor(wagonPos(s, 0).x / U);

/** Щотику з extrasStep (до паротяга). true — паротяг цього тику стоїть на станції */
export function endlessStep(s) {
  const E = s.endless;
  maybeShift(s);
  connect(s);
  let stand = false;
  if (E.stop > 0) {
    if (s.mid && s.mid.pick && E.stop > PICK_SEC * TPS) E.stop = PICK_SEC * TPS;
    if (--E.stop === 0) {
      s.mid = null;
      const w = wagonPos(s, 0); ev(s, 'whistle', Math.floor(w.x / U), Math.floor(w.y / U));
    }
    s.train.speed = 0;
    stand = true;
  } else {
    const k = E.stations - E.base, f = E.st[k];
    if (f && s.train.started && headX(s) >= f.x0) { arrive(s, f); stand = true; }
  }
  info(s);
  return stand;
}

function info(s) {
  const E = s.endless, hx = headX(s), f = E.st[E.stations - E.base];
  E.segment = E.base + (hx >= E.L ? 2 : 1);
  E.toNext = f ? Math.max(0, f.x0 - hx) : 0;
}

/** Колію довели до стовпців станції — вона більше не G.STATION (не перемога, бот веде далі) */
function connect(s) {
  const E = s.endless, W = s.w;
  for (;;) {
    const f = E.st[E.conn - E.base];
    if (!f) return;
    const last = s.path[s.path.length - 1];
    if (last % W < f.x0) return;
    for (let y = f.y0; y <= f.y1; y++) for (let x = f.x0; x <= f.x1; x++) {
      const i = y * W + x;
      if (s.ground[i] === G.STATION) s.ground[i] = G.SAND;
    }
    E.conn++;
  }
}

function arrive(s, f) {
  const E = s.endless, P = s.params, W = s.w, h = s.h;
  E.stations++;
  E.stop = E.stopMax;
  s.train.speed = 0;
  const w = wagonPos(s, 0), hx = Math.floor(w.x / U), hy = Math.floor(w.y / U);
  ev(s, 'whistle', hx, hy); ev(s, 'station', hx, hy, E.stations);
  // таблички покращень K11 (3 з доступних, вибір за rnd стану); що вперлося в стелю панелі — не пропонуємо
  const u = s.ups || {};
  const ok = { tank: P.heatSec * 2 <= 240, craft: P.craftSec > 0.5, store: (P.storeMax | 0) < 40, carry: (P.carryMax | 0) < 10,
               cist: (u.cist | 0) < 3, magnet: !u.mag, brake: (u.brake | 0) < 3 };   // K48
  const keys = UP_KEYS.filter((k) => ok[k]);
  while (keys.length > 3) keys.splice(rnd(s) % keys.length, 1);
  upsStation(s);   // K48
  const sy = f.y1 + 2 <= h - 2 ? f.y1 + 2 : f.y0 - 2;
  const signs = [];
  for (let j = 0; j < keys.length; j++) {
    const x = f.x0 + j;
    if (x >= W) break;
    const i = sy * W + x;
    if (s.obj[i] !== O.WALL) { s.obj[i] = O.NONE; s.hp[i] = 0; }
    if (noFoot(s.ground[i])) s.ground[i] = G.SAND;   // K26: і урвище
    signs.push({ x, y: sy, k: keys[j] });
  }
  s.mid = signs.length ? { x0: f.x0, y0: f.y0, x1: f.x1, y1: f.y1, signs, pick: '', wait: 1, len1: 0 } : null;
}

/** Хвіст потяга далеко за межею ділянок — відрізаємо ліву, дописуємо наступну */
function maybeShift(s) {
  const E = s.endless, L = E.L, W = s.w;
  if (E.stations <= E.base) return;                    // станцію лівої ділянки ще не проїхали
  const ti = Math.floor((s.train.pos - (WAGONS.length - 1) * U) / U);
  if (ti < 0 || ti >= s.path.length || (s.path[ti] % W) < L + MARGIN(L)) return;
  let c = s.path.length;
  while (c > 0 && s.path[c - 1] % W >= L) c--;          // з c і до кінця колія — уся в правій ділянці
  if (c > ti) return;                                  // колія повертається в ліву — зсунемо згодом
  shift(s, c);
}

function shift(s, cut) {
  const E = s.endless, L = E.L, W = s.w, h = s.h, LU = L * U;
  // інструменти з лівої ділянки — врятувати
  const tools = [];
  for (let y = 0; y < h; y++) for (let x = 0; x < L; x++) { const i = y * W + x; if (isTool(s.drop[i])) tools.push(s.drop[i]); }
  // масиви: ліву геть, праву на її місце, праворуч — нова ділянка
  const b = segment(s.seed, s.params, E.base + 2);
  for (const [a, c] of [[s.ground, b.ground], [s.obj, b.obj], [s.hp, b.hp], [s.drop, b.drop], [s.dropN, b.dropN]]) {
    for (let y = 0; y < h; y++) { a.copyWithin(y * W, y * W + L, y * W + W); a.set(c.subarray(y * L, y * L + L), y * W + L); }
  }
  s.path = s.path.slice(cut).map((c) => c - L);
  s.train.pos -= cut * U;
  s.zones = [...(s.zones || []).filter((z) => z.x1 > L).map((z) => ({ ...z, x0: Math.max(0, z.x0 - L), x1: z.x1 - L })), ...zonesOf(b.zones, L)];
  E.st = [moved(E.st[1], -L), moved(stationFrame(b.ground, L, h), L)];
  E.base++; E.shift++; E.dx = L;
  if (s.mid) { const m = s.mid; m.x0 -= L; m.x1 -= L; m.signs = m.signs.filter((g) => g.x >= L).map((g) => ({ ...g, x: g.x - L })); }
  // гравці: −L; хто лишився позаду — до хвоста потяга
  for (const pl of s.players) {
    if (!pl || pl.out) continue;
    pl.x -= LU;
    if (pl.gx >= 0) { pl.gx -= L; if (pl.gx < 0) pl.gx = pl.gy = -1; }
    if (pl.aim) pl.aim = pl.aim.x - L >= 0 ? { x: pl.aim.x - L, y: pl.aim.y } : null;
    if (pl.x < U / 2) {
      const c = freeNearTail(s, false);
      if (c) { pl.x = c.x * U + U / 2; pl.y = c.y * U + U / 2; pl.gx = pl.gy = -1; }
    }
  }
  // гуси
  if (s.geese) {
    for (let j = s.geese.length - 1; j >= 0; j--) {
      const g = s.geese[j];
      g.x -= LU; g.tx -= L;
      if (g.x < 0) { if (isTool(g.hold)) tools.push(g.hold); s.geese.splice(j, 1); continue; }
      if (g.tx < 0 && g.mode === 'go') g.mode = 'flee';
    }
  }
  // K24: кинуте в польоті (K18) — теж −L: coopStep іде раніше за зсув, і наступного тику річ летіла б зі старих
  // координат і впала на L клітинок правіше; що летіло у відрізане — рятуємо до хвоста, як інструменти звідти
  if (s.flying) {
    for (let j = s.flying.length - 1; j >= 0; j--) {
      const f = s.flying[j];
      f.x -= LU; f.sx -= LU; f.tx -= L;
      if (f.tx < 0) { if (isTool(f.k)) tools.push(f.k); s.flying.splice(j, 1); }
    }
  }
  for (const k of tools) {
    const c = freeNearTail(s, true);
    if (c) { const i = c.y * W + c.x; s.drop[i] = k; s.dropN[i] = 1; }
  }
  ev(s, 'shift', L, 0);
}

/** Вільна клітинка біля хвоста потяга (BFS пішки): не колія, не вагон, не станція; forDrop — ще й без речі */
function freeNearTail(s, forDrop) {
  const W = s.w, h = s.h, n = W * h;
  const t = wagonPos(s, WAGONS.length - 1);
  const start = Math.floor(t.y / U) * W + Math.floor(t.x / U);
  const onPath = new Uint8Array(n); for (const c of s.path) onPath[c] = 1;
  const seen = new Uint8Array(n), q = [start]; seen[start] = 1;
  const pass = (i) => s.obj[i] === O.NONE && !noFoot(s.ground[i]);   // K26: і не на урвище яру
  for (let k = 0; k < q.length && k < 4000; k++) {
    const i = q[k], x = i % W, y = (i / W) | 0;
    if (!onPath[i] && pass(i) && s.ground[i] !== G.STATION && wagonAt(s, x, y) < 0 && (!forDrop || s.drop[i] === I.NONE)) return { x, y };
    for (const [dx, dy] of [[-1, 0], [0, -1], [0, 1], [1, 0]]) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= W || ny >= h) continue;
      const m = ny * W + nx;
      if (seen[m] || !(pass(m) || onPath[m])) continue;
      seen[m] = 1; q.push(m);
    }
  }
  return null;
}

/** Для rules.hash: усе, що впливає на гру й не в масивах світу */
export function endlessHash(s) {
  const E = s.endless;
  let h = 0x811C9DC5;
  const mix = (v) => { h = Math.imul(h ^ (v >>> 0), 16777619) >>> 0; };
  mix(E.base); mix(E.stations); mix(E.conn); mix(E.stop); mix(E.shift);
  for (const f of E.st) { mix(f.x0); mix(f.y0); mix(f.x1); mix(f.y1); }
  mix(s.mid ? 1 + UP_KEYS.indexOf(s.mid.pick) : 0);
  if (s.mid) for (const g of s.mid.signs) { mix(g.x); mix(g.y); mix(UP_KEYS.indexOf(g.k)); }
  const P = s.params;
  mix(Math.round(P.heatSec * 100)); mix(Math.round(P.craftSec * 100)); mix(P.storeMax | 0); mix(P.carryMax | 0);
  return h >>> 0;
}
