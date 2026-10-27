// «Колія» — чисті детерміновані правила. Без Math.random, Date.now, DOM і three.
// Цілі числа: 1 клітинка = U підодиниць, тик = TICK_MS. Стан — простий об'єкт із типізованими масивами
// (structuredClone копіює його цілком), тож однаковий потік команд дає однаковий стан у будь-якому браузері й у Node.
// Власник: K2 «Правила» (контракт — D:\or-wt\_kolia\CONTRACT.md).
import { generate } from './gen.js';

export const TICK_MS = 50;           // 20 тиків/с
export const TPS = 1000 / TICK_MS;
export const U = 1000;               // підодиниць у клітинці
export const MAX_PLAYERS = 4;

// Підлога (state.ground)
export const G = { GRASS: 0, WATER: 1, BRIDGE: 2, STATION: 3, SAND: 4 };
// Предмет на клітинці, що заважає ходити (state.obj)
export const O = { NONE: 0, TREE: 1, ROCK: 2, WALL: 3 };   // WALL — непробивна скеля по краях
// Речі на підлозі й у руках (state.drop / player.hold.k)
export const I = { NONE: 0, WOOD: 1, IRON: 2, RAIL: 3, AXE: 4, PICK: 5, BUCKET: 6, BUCKET_FULL: 7 };
export const STACKABLE = new Set([I.WOOD, I.IRON, I.RAIL]);
export const WAGONS = ['loco', 'store', 'craft'];  // від голови: паротяг, склад, майстерня

// Повзунки. Значення в «людських» одиницях; rules переводить у тики/підодиниці сам.
// panel.js будує панель з PARAM_META; gen.js читає параметри смуги.
export const PARAM_META = {
  speedStart:  { def: 0.20, min: 0.05, max: 1.5, step: 0.01, label: 'Швидкість паротяга, клітинок/с', group: 'Паротяг' },
  speedGrowth: { def: 0.05, min: 0,    max: 0.5, step: 0.01, label: 'Приріст швидкості, клітинок/с за хвилину', group: 'Паротяг' },
  speedMax:    { def: 1.00, min: 0.1,  max: 3,   step: 0.05, label: 'Стеля швидкості', group: 'Паротяг' },
  startSec:    { def: 20,   min: 0,    max: 90,  step: 1,    label: 'Пауза перед рушанням, с', group: 'Паротяг' },
  heatSec:     { def: 45,   min: 10,   max: 240, step: 5,    label: 'Від холодного до перегріву, с', group: 'Котел' },
  fireSec:     { def: 12,   min: 3,    max: 60,  step: 1,    label: 'Пожежа: скільки до поразки, с', group: 'Котел' },
  craftSec:    { def: 3,    min: 0.5,  max: 15,  step: 0.5,  label: 'Майстерня: секунд на рейку', group: 'Ресурси' },
  treeHits:    { def: 3,    min: 1,    max: 10,  step: 1,    label: 'Ударів на дерево', group: 'Ресурси' },
  rockHits:    { def: 4,    min: 1,    max: 12,  step: 1,    label: 'Ударів на скелю', group: 'Ресурси' },
  woodPerTree: { def: 1,    min: 1,    max: 5,   step: 1,    label: 'Дерева з дерева', group: 'Ресурси' },
  ironPerRock: { def: 1,    min: 1,    max: 5,   step: 1,    label: 'Заліза зі скелі', group: 'Ресурси' },
  storeMax:    { def: 8,    min: 2,    max: 40,  step: 1,    label: 'Склад: місткість кожного', group: 'Ресурси' },
  railsMax:    { def: 5,    min: 1,    max: 20,  step: 1,    label: 'Майстерня: готових рейок до', group: 'Ресурси' },
  startRails:  { def: 3,    min: 0,    max: 20,  step: 1,    label: 'Рейок на старті в майстерні', group: 'Ресурси' },
  playerSpeed: { def: 4.0,  min: 1,    max: 10,  step: 0.5,  label: 'Швидкість гравця, клітинок/с', group: 'Гравці' },
  carryMax:    { def: 3,    min: 1,    max: 10,  step: 1,    label: 'Скільки несеш у руках', group: 'Гравці' },
  hitMs:       { def: 250,  min: 80,   max: 800, step: 10,   label: 'Пауза між ударами, мс', group: 'Гравці' },
  stripLen:    { def: 150,  min: 40,   max: 400, step: 10,   label: 'Довжина смуги', group: 'Світ' },
  stripWidth:  { def: 20,   min: 10,   max: 40,  step: 1,    label: 'Ширина смуги', group: 'Світ' },
  forest:      { def: 0.30, min: 0,    max: 0.7, step: 0.01, label: 'Частка лісу', group: 'Світ' },
  rocks:       { def: 0.18, min: 0,    max: 0.6, step: 0.01, label: 'Частка скель', group: 'Світ' },
  water:       { def: 0.08, min: 0,    max: 0.4, step: 0.01, label: 'Частка води', group: 'Світ' },
};
// Перемикачі варіантів (рядкові значення; перше — типове)
export const VARIANTS = {
  overheat: { values: ['fire', 'stop'], labels: ['Пожежа', 'Зупинка'], label: 'Перегрів котла' },
};

export function defaultParams() {
  const p = {};
  for (const k in PARAM_META) p[k] = PARAM_META[k].def;
  for (const k in VARIANTS) p[k] = VARIANTS[k].values[0];
  return p;
}

// PRNG mulberry32: стан — uint32 у state.rng; повертає ціле 0..2^32-1
export function rnd(state) {
  let t = (state.rng = (state.rng + 0x6D2B79F5) >>> 0);
  t = Math.imul(t ^ (t >>> 15), t | 1);
  t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
  return (t ^ (t >>> 14)) >>> 0;
}
export const rndInt = (state, n) => rnd(state) % n;

export const idx = (s, x, y) => y * s.w + x;
export const inside = (s, x, y) => x >= 0 && y >= 0 && x < s.w && y < s.h;

/**
 * Нова партія. seed — uint32, params — з defaultParams() (+ правки панелі), nPlayers 1..4.
 * Стан (усе цілі; поля нижче — контракт для render/net/bot/audio):
 *  seed, tick, rng, params, status: 'run'|'won'|'lost', reason: ''|'derail'|'fire'
 *  w (уздовж, x), h (упоперек, y); ground/obj/hp/drop/dropN: Uint8Array(w*h)
 *  path: number[] — клітинки колії (idx) від хвоста до краю; потяг їде по ній
 *  train: { pos (підодиниці вздовж path, голова паротяга), speed (підодиниць/тик ×1000), heat 0..1000,
 *           fire (тиків горить, 0 — ні), wood, iron, rails, craftT (тиків до рейки, 0 — стоїть) }
 *  players: [{ x, y (підодиниці центру), fx, fy (-1..1, куди дивиться), hold: {k, n}, cd (тиків до удару) }]
 *  ev: [{ t, x, y, p? }] — події цього тику (для звуку й ефектів), чиститься на початку step
 *  stats: { railsLaid, hits, maxSpeed }
 */
export function newGame(seed, params = defaultParams(), nPlayers = 1) {
  seed = seed >>> 0;
  const world = generate(seed, params);   // { w, h, ground, obj, hp, drop, dropN, path, spawn: [{x,y}] (клітинки) }
  const s = {
    seed, tick: 0, rng: (seed ^ 0x9E3779B9) >>> 0, params: { ...params },
    status: 'run', reason: '',
    w: world.w, h: world.h,
    ground: world.ground, obj: world.obj, hp: world.hp, drop: world.drop, dropN: world.dropN,
    path: world.path.slice(),
    train: { pos: (WAGONS.length) * U, speed: 0, heat: 0, fire: 0, wood: 0, iron: 0, rails: params.startRails | 0, craftT: 0 },
    players: [],
    ev: [],
    stats: { railsLaid: 0, hits: 0, maxSpeed: 0 },
  };
  for (let i = 0; i < nPlayers; i++) {
    const c = world.spawn[i % world.spawn.length];
    s.players.push({ x: c.x * U + U / 2, y: c.y * U + U / 2, fx: 1, fy: 0, hold: { k: I.NONE, n: 0 }, cd: 0 });
  }
  return s;
}

/**
 * Один тик. cmds — команди цього тику в порядку надходження:
 *  { p, k: 'in', mx, my }            — рух гравця p: mx,my цілі -100..100 (локально й бот)
 *  { p, k: 'pose', x, y, fx, fy }    — поставити позу (мережа: позу шле власник)
 *  { p, k: 'use', x?, y?, fx?, fy? } — дія руками перед собою (з позою — для мережі); ігнор, якщо cd > 0
 *  { k: 'join' } / { p, k: 'leave' }
 * Рестарт — не команда: newGame(...) заново.
 * TODO K2: рух із колізіями, use (контекстно), паротяг, нагрів, майстерня, сходження, станція.
 */
export function step(s, cmds = []) {
  s.ev.length = 0;
  if (s.status !== 'run') return s;
  for (const c of cmds) applyCmd(s, c);
  s.tick++;
  return s;
}

function applyCmd(s, c) {
  // TODO K2
}

/** Клітинка, на яку дивиться гравець: { x, y } (може бути поза смугою) */
export function facingCell(pl) {
  const r = U * 0.75;
  return { x: Math.floor((pl.x + pl.fx * r) / U), y: Math.floor((pl.y + pl.fy * r) / U) };
}

/** Де зараз вагон k (0 — паротяг): { x, y } у підодиницях, кут — з сусідніх клітинок колії */
export function wagonPos(s, k) {
  const pos = s.train.pos - k * U;
  const i = Math.max(0, Math.min(s.path.length - 1, Math.floor(pos / U)));
  const j = Math.min(s.path.length - 1, i + 1);
  const f = pos - i * U;
  const a = s.path[i], b = s.path[j];
  const ax = (a % s.w) * U + U / 2, ay = Math.floor(a / s.w) * U + U / 2;
  const bx = (b % s.w) * U + U / 2, by = Math.floor(b / s.w) * U + U / 2;
  return { x: ax + Math.round((bx - ax) * f / U), y: ay + Math.round((by - ay) * f / U) };
}

export function clone(s) { return structuredClone(s); }

/** FNV-1a 32 по всьому, що впливає на гру (без ev) — для звірки мережі */
export function hash(s) {
  let h = 0x811C9DC5;
  const mix = (v) => { h ^= v & 0xFF; h = Math.imul(h, 16777619); h ^= (v >>> 8) & 0xFF; h = Math.imul(h, 16777619);
    h ^= (v >>> 16) & 0xFF; h = Math.imul(h, 16777619); h ^= (v >>> 24) & 0xFF; h = Math.imul(h, 16777619); };
  mix(s.tick); mix(s.rng);
  for (const a of [s.ground, s.obj, s.hp, s.drop, s.dropN]) for (let i = 0; i < a.length; i++) mix(a[i]);
  for (const c of s.path) mix(c);
  const t = s.train; mix(t.pos); mix(t.speed); mix(t.heat); mix(t.fire); mix(t.wood); mix(t.iron); mix(t.rails); mix(t.craftT);
  for (const p of s.players) { mix(p.x); mix(p.y); mix(p.hold.k); mix(p.hold.n); }
  return h >>> 0;
}
