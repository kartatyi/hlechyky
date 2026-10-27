// «Колія» — чисті детерміновані правила. Без Math.random, Date.now, DOM і three.
// Цілі числа: 1 клітинка = U підодиниць, тик = TICK_MS. Стан — простий об'єкт із типізованими масивами
// (structuredClone копіює його цілком), тож однаковий потік команд дає однаковий стан у будь-якому браузері й у Node.
// Власник: K2 «Правила» (контракт — D:\or-wt\_kolia\CONTRACT.md).
import { generate } from './gen.js';
import { extrasInit, extrasStep, extrasUse } from './extras.js';   // K11: додатки за перемикачем
import { coopInit, coopCmd, coopUse, coopStep, coopHash } from './coop.js';   // K18: кооп-фішки за перемикачем
import { tutorialParams, tutorialWorld } from './tutorial.js';   // K22: навчальна смуга (mode='tutorial')
import { endlessHash } from './endless.js';                          // K19: нескінченна колія

export const TICK_MS = 50;           // 20 тиків/с
export const TPS = 1000 / TICK_MS;
export const U = 1000;               // підодиниць у клітинці
export const MAX_PLAYERS = 4;

// Підлога (state.ground)
export const G = { GRASS: 0, WATER: 1, BRIDGE: 2, STATION: 3, SAND: 4, SWAMP: 5 };   // SWAMP — болото: ходити повільно (swampSlow), рейки класти можна
// Предмет на клітинці, що заважає ходити (state.obj)
export const O = { NONE: 0, TREE: 1, ROCK: 2, WALL: 3 };   // WALL — непробивна скеля по краях
// Речі на підлозі й у руках (state.drop / player.hold.k)
export const I = { NONE: 0, WOOD: 1, IRON: 2, RAIL: 3, AXE: 4, PICK: 5, BUCKET: 6, BUCKET_FULL: 7 };
export const STACKABLE = new Set([I.WOOD, I.IRON, I.RAIL]);
export const WAGONS = ['loco', 'store', 'craft'];  // від голови: паротяг, склад, майстерня

// Повзунки. Значення в «людських» одиницях; rules переводить у тики/підодиниці сам.
// panel.js будує панель з PARAM_META; gen.js читає параметри смуги.
// Типові числа — K13 (баланс на 20 сідах ботами, D:\or-wt\_kolia\k13\BALANCE.md): соло ~8 хв і ~2/3 перемог, удвох ~5,5 хв і ~85 %.
export const PARAM_META = {
  speedStart:  { def: 0.20, min: 0.05, max: 1.5, step: 0.01, label: 'Швидкість паротяга, клітинок/с', group: 'Паротяг' },
  speedGrowth: { def: 0.06, min: 0,    max: 0.5, step: 0.01, label: 'Приріст швидкості, клітинок/с за хвилину', group: 'Паротяг' },
  speedMax:    { def: 0.60, min: 0.1,  max: 3,   step: 0.05, label: 'Стеля швидкості', group: 'Паротяг' },
  stepN:       { def: 10,   min: 2,    max: 40,  step: 1,    label: 'Розгін не від часу: кожні N рейок (сходинки) / клітинок шляху', group: 'Паротяг' },   // K14
  stepGain:    { def: 0.02, min: 0,    max: 0.2, step: 0.005, label: 'Розгін не від часу: + клітинок/с за N', group: 'Паротяг' },                     // K14
  startSec:    { def: 30,   min: 0,    max: 90,  step: 1,    label: 'Пауза перед рушанням, с', group: 'Паротяг' },
  soloSpeed:   { def: 0.55, min: 0.4,  max: 1,   step: 0.05, label: 'Соло-знижка: паротяг їде й гріється повільніше, коли гравець один (частка)', group: 'Паротяг' },   // K13
  finishSpeed: { def: 2,    min: 0,    max: 5,   step: 0.25, label: 'Колію дотягли до станції — паротяг мчить, клітинок/с (0 — ні)', group: 'Паротяг' },  // K13
  heatSec:     { def: 50,   min: 10,   max: 240, step: 5,    label: 'Від холодного до перегріву, с', group: 'Котел' },
  fireSec:     { def: 12,   min: 3,    max: 60,  step: 1,    label: 'Пожежа: скільки до поразки, с', group: 'Котел' },
  craftSec:    { def: 2,    min: 0.5,  max: 15,  step: 0.5,  label: 'Майстерня: секунд на рейку', group: 'Ресурси' },
  treeHits:    { def: 3,    min: 1,    max: 10,  step: 1,    label: 'Ударів на дерево', group: 'Ресурси' },
  rockHits:    { def: 4,    min: 1,    max: 12,  step: 1,    label: 'Ударів на скелю', group: 'Ресурси' },
  woodPerTree: { def: 2,    min: 1,    max: 5,   step: 1,    label: 'Дерева з дерева', group: 'Ресурси' },
  ironPerRock: { def: 2,    min: 1,    max: 5,   step: 1,    label: 'Заліза зі скелі', group: 'Ресурси' },
  storeMax:    { def: 10,   min: 2,    max: 40,  step: 1,    label: 'Склад: місткість кожного', group: 'Ресурси' },
  railsMax:    { def: 8,    min: 1,    max: 20,  step: 1,    label: 'Майстерня: готових рейок до', group: 'Ресурси' },
  railsPerCraft: { def: 3,  min: 1,    max: 4,   step: 1,    label: 'Майстерня: рейок з дерева+заліза', group: 'Ресурси' },
  startRails:  { def: 4,    min: 0,    max: 20,  step: 1,    label: 'Рейок на старті в майстерні', group: 'Ресурси' },
  playerSpeed: { def: 5,  min: 1,    max: 10,  step: 0.5,  label: 'Швидкість гравця, клітинок/с', group: 'Гравці' },
  carryMax:    { def: 6,    min: 1,    max: 10,  step: 1,    label: 'Скільки несеш у руках', group: 'Гравці' },
  hitMs:       { def: 200,  min: 80,   max: 800, step: 10,   label: 'Пауза між ударами, мс', group: 'Гравці' },
  stripLen:    { def: 100,  min: 40,   max: 400, step: 10,   label: 'Довжина смуги', group: 'Світ' },
  stripWidth:  { def: 20,   min: 10,   max: 40,  step: 1,    label: 'Ширина смуги', group: 'Світ' },
  forest:      { def: 0.30, min: 0,    max: 0.7, step: 0.01, label: 'Частка лісу', group: 'Світ' },
  rocks:       { def: 0.18, min: 0,    max: 0.6, step: 0.01, label: 'Частка скель', group: 'Світ' },
  water:       { def: 0.08, min: 0,    max: 0.4, step: 0.01, label: 'Частка води', group: 'Світ' },
  geeseSec:    { def: 45,   min: 10,   max: 180, step: 5,    label: 'Гуси: зграя раз на, с', group: 'Додатки' },      // K11
  geeseMax:    { def: 3,    min: 1,    max: 3,   step: 1,    label: 'Гуси: у зграї до', group: 'Додатки' },           // K11
  geeseSpeed:  { def: 2.5,  min: 1,    max: 6,   step: 0.5,  label: 'Гуси: швидкість, клітинок/с', group: 'Додатки' }, // K11
  zoneLen:     { def: 28,   min: 12,   max: 80,  step: 1,    label: 'Довжина ділянки (біому), клітинок', group: 'Світ' },
  hardGrow:    { def: 0.6,  min: 0,    max: 1.5, step: 0.05, label: 'Складність до станції (приріст)', group: 'Світ' },
  swamp:       { def: 0.06, min: 0,    max: 0.4, step: 0.01, label: 'Частка болота', group: 'Світ' },
  swampSlow:   { def: 0.55, min: 0.2,  max: 1,   step: 0.05, label: 'Болото: швидкість ходи (частка)', group: 'Світ' },
};
// Перемикачі варіантів (рядкові значення; перше — типове)
export const VARIANTS = {
  overheat: { values: ['fire', 'stop'], labels: ['Пожежа', 'Зупинка'], label: 'Перегрів котла' },
  geese:      { values: ['off', 'on'], labels: ['Ні', 'Так'], label: 'Гуси-злодюжки 🪿' },                      // K11
  stationMode: { values: ['end', 'upgrade', 'endless'], labels: ['Кінець', 'Проміжна з покращеннями', 'Нескінченна ♾'], label: 'Станція 🏪' }, // K11, K19 (endless.js)
  // K14 «Відчуття ядра»: move/useMode/aim — блоки «K14» біля руху гравця, speedCurve — trainSpeed()
  move: { values: ['free', 'grid', 'snappy'], labels: ['Вільно', 'Клітинками', 'Чітко'], label: 'Рух гравця' },
  useMode: { values: ['press', 'tap', 'auto'], labels: ['Затиснув — б\'є', 'Кожен удар — натиск', 'Б\'є сам'], label: 'Дія (рубати/бити)' },
  speedCurve: { values: ['linear', 'steps', 'distance'], labels: ['Від часу', 'Сходинками за рейки', 'Від шляху'], label: 'Як розганяється паротяг' },
  aim: { values: ['front', 'smart'], labels: ['Перед собою', 'Розумний (поруч, з підсвіткою)'], label: 'Куди діє кнопка' },
  // K18 «Кооп-фішки» (coop.js): штовханина, кидок, передача в руки, «зірочки» від сокири
  bump: { values: ['off', 'on'], labels: ['Ні', 'Так'], label: 'Штовханина: гравці не проходять крізь одне одного 👐' },
  throw: { values: ['off', 'on'], labels: ['Ні', 'Так'], label: 'Кидок речі на 3 клітинки (ловиться руками) 🎯' },
  pass: { values: ['on', 'off'], labels: ['Так', 'Ні'], label: 'Передати річ партнерові в руки (дія на нього) 🤝' },
  stun: { values: ['off', 'on'], labels: ['Ні', 'Так'], label: 'Сокирою по партнеру — 1 с зірочок 💫' },
  mode: { values: ['normal', 'tutorial'], labels: ['Звичайна смуга', 'Навчання 🎓'], label: 'Смуга', group: 'Світ' },   // K22 (tutorial.js)
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

// ---- похідні числа з параметрів (усе цілі) ----
const ticks = (sec) => Math.max(1, Math.round(sec * TPS));
/** швидкість у підодиницях/тик ×1000 з клітинок/с */
const spd = (cps) => Math.round(cps * U * 1000 / TPS);
export const PR = 300;              // радіус гравця, підодиниць (0,3 клітинки)
const WAGON_HALF = 420;             // напівширина вагона-коробки
const FLOOR_STACK_MAX = 99;         // стос на підлозі
const ACT_CD = 3;                   // тиків паузи після взяти/покласти

/**
 * Нова партія. seed — uint32, params — з defaultParams() (+ правки панелі), nPlayers 1..4.
 * Стан (усе цілі; поля нижче — контракт для render/net/bot/audio):
 *  seed, tick, rng, params, status: 'run'|'won'|'lost', reason: ''|'derail'|'fire'
 *  w (уздовж, x), h (упоперек, y); ground/obj/hp/drop/dropN: Uint8Array(w*h)
 *  path: number[] — клітинки колії (idx) від хвоста до краю; потяг їде по ній
 *  train: { pos (підодиниці вздовж path, голова паротяга), speed (підодиниць/тик ×1000), heat 0..1000,
 *           fire (тиків горить, 0 — ні), wood, iron, rails, craftT (тиків до рейки, 0 — стоїть),
 *           sub (залишок руху ×1000), ht (тиків нагріву від останнього поливу), started (0/1) }
 *  players: [{ x, y (підодиниці центру), fx, fy (-1..1, куди дивиться; лише одна вісь ≠ 0), hold: {k, n}, cd (тиків до удару),
 *              mx, my (останній намір руху -100..100), ut (тик останнього use + 1 — щоб відрізнити «затиснуто»),
 *              out (1 — вийшов зі столу, не малювати), st (лічильник кроків для події 'step') }]
 *  spawn: [{x,y}] — клітинки появи з генератора
 *  ev: [{ t, x, y, p? }] — події цього тику (для звуку й ефектів), чиститься на початку step; x,y — клітинка
 *  stats: { railsLaid, hits, maxSpeed, wood, iron, crafted, douses, bridges, cells (скільки клітинок проїхав) }
 */
export function newGame(seed, params = defaultParams(), nPlayers = 1) {
  seed = seed >>> 0;
  params = { ...defaultParams(), ...params };
  if (params.mode === 'tutorial') params = tutorialParams(params);   // K22: навчальна смуга — свої повільні числа
  const world = params.mode === 'tutorial' ? tutorialWorld(params) : generate(seed, params);   // { w, h, ground, obj, hp, drop, dropN, path, spawn: [{x,y}] (клітинки) }
  const s = {
    seed, tick: 0, rng: (seed ^ 0x9E3779B9) >>> 0, params,
    status: 'run', reason: '',
    w: world.w, h: world.h,
    ground: world.ground, obj: world.obj, hp: world.hp, drop: world.drop, dropN: world.dropN,
    path: world.path.slice(),
    zones: world.zones || [],   // K7: ділянки-біоми (для HUD); на правила не впливають
    spawn: world.spawn.map((c) => ({ x: c.x, y: c.y })),
    train: { pos: (WAGONS.length) * U, speed: 0, heat: 0, fire: 0, wood: 0, iron: 0, rails: params.startRails | 0, craftT: 0,
             sub: 0, ht: 0, started: 0 },
    players: [],
    ev: [],
    stats: { railsLaid: 0, hits: 0, maxSpeed: 0, wood: 0, iron: 0, crafted: 0, douses: 0, bridges: 0, cells: 0 },
  };
  // початкова колія коротша за потяг — подовжимо позицію, щоб голова була на path
  if (s.train.pos > (s.path.length - 1) * U) s.train.pos = (s.path.length - 1) * U;
  nPlayers = Math.max(1, Math.min(MAX_PLAYERS, nPlayers | 0));
  for (let i = 0; i < nPlayers; i++) s.players.push(newPlayer(s.spawn[i % s.spawn.length]));
  extrasInit(s, params);   // K11: гуси, проміжна станція (вимкнене — лише порожні поля)
  coopInit(s);             // K18: s.flying
  return s;
}

function newPlayer(c) {
  return { x: c.x * U + U / 2, y: c.y * U + U / 2, fx: 1, fy: 0, hold: { k: I.NONE, n: 0 }, cd: 0,
           mx: 0, my: 0, ut: 0, out: 0, st: 0,
           gx: -1, gy: -1, gt: 0,     // K14: ціль кроку й лічильник повороту (лише move='grid')
           aim: null };               // K14: aim='smart' — клітинка, де спрацює use ({x,y}), null — нема що робити (для підсвітки)
}

/**
 * Один тик. cmds — команди цього тику в порядку надходження:
 *  { p, k: 'in', mx, my }            — намір руху гравця p: mx,my цілі -100..100 (тримається, доки не прийде новий)
 *  { p, k: 'pose', x, y, fx, fy }    — поставити позу (мережа: позу шле власник)
 *  { p, k: 'use', x?, y?, fx?, fy? } — дія руками перед собою (з позою — для мережі); ігнор, якщо cd > 0.
 *     Якщо use приходить і в попередньому тику («затиснуто»), повторюються лише удари, класти рейки й мости —
 *     взяти/покласти спрацьовує тільки на натиск (інакше стос перекидався б щотику).
 *  { k: 'join' } / { p, k: 'leave' }
 * Рестарт — не команда: newGame(...) заново.
 * Порядок тику: команди → рух гравців (+ підбирання свого стосу під ногами) → майстерня → паротяг і котел → тик++.
 */
export function step(s, cmds = []) {
  s.ev.length = 0;
  if (s.status !== 'run') return s;
  for (const c of cmds) applyCmd(s, c);
  for (let i = 0; i < s.players.length; i++) { movePlayer(s, i); feelTick(s, i); }   // K14: feelTick — автоудар, приціл
  coopStep(s);   // K18: штовханина, політ кинутого
  stepCraft(s);
  if (!extrasStep(s, cmds)) stepTrain(s);   // K11: true — паротяг стоїть на проміжній станції
  s.tick++;
  return s;
}

const ev = (s, t, x, y, p) => { const e = { t, x, y }; if (p !== undefined) e.p = p; s.ev.push(e); };
const clampI = (v, a, b) => (v < a ? a : v > b ? b : v);

export function applyCmd(s, c) {
  if (!c || s.status !== 'run') return;
  if (c.k === 'join') { join(s); return; }
  const pl = s.players[c.p | 0];
  if (!pl || pl.out) return;
  switch (c.k) {
    case 'in':
      pl.mx = clampI(c.mx | 0, -100, 100); pl.my = clampI(c.my | 0, -100, 100);
      break;
    case 'pose': setPose(s, pl, c); break;
    case 'use':
      if (c.x !== undefined) setPose(s, pl, c, true);   // K24: поза з дією — лише поруч і не в стіну
      use(s, c.p | 0, pl);
      break;
    case 'leave': leave(s, pl); break;
    case 'throw': case 'horn': if (c.x !== undefined) setPose(s, pl, c, true); coopCmd(s, c.p | 0, pl, c.k); break;   // K18
  }
}

// K24: поза, що їде з use/throw, — це «де я стояв у своєму передбаченому світі»; запізніла команда (сервер зсунув її
// на кілька тиків, через зсув вікна endless чи з минулої партії) несла б позу з іншого місця — телепорт, може й у
// скелю/воду, звідки не вийти. Тож таку позу (далі POSE_NEAR чи в непрохідне) не ставимо: дія — з того місця, де є.
const POSE_NEAR = 5 * U;
function setPose(s, pl, c, near = false) {
  if (Number.isFinite(c.x) && Number.isFinite(c.y)) {
    const x = clampI(Math.round(c.x), PR, s.w * U - PR), y = clampI(Math.round(c.y), PR, s.h * U - PR);
    if (!near || (Math.abs(x - pl.x) + Math.abs(y - pl.y) <= POSE_NEAR && !hitsAt(s, x, y, [], 0))) { pl.x = x; pl.y = y; }
  }
  if (Number.isFinite(c.fx) && Number.isFinite(c.fy)) {
    const fx = Math.sign(c.fx) | 0, fy = Math.sign(c.fy) | 0;
    if (fx || fy) { pl.fx = fx; pl.fy = fx ? 0 : fy; }
  }
}

// ---------------- прохідність ----------------
/** Клітинку не пройти: поза смугою, obj, вода (міст — можна) */
export function cellBlocked(s, cx, cy) {
  if (!inside(s, cx, cy)) return true;
  const i = cy * s.w + cx;
  return s.obj[i] !== O.NONE || s.ground[i] === G.WATER;
}

function circleBox(px, py, r, x0, y0, x1, y1) {
  const qx = px < x0 ? x0 : px > x1 ? x1 : px, qy = py < y0 ? y0 : py > y1 ? y1 : py;
  const dx = px - qx, dy = py - qy;
  return dx * dx + dy * dy < r * r;
}

function wagonsXY(s) {
  const a = [];
  for (let k = 0; k < WAGONS.length; k++) a.push(wagonPos(s, k));
  return a;
}

/** Чи коло гравця в (x,y) зачіпає стіну/obj/воду або вагон (крім тих, що в маску ign) */
function hitsAt(s, x, y, wag, ign) {
  const cx0 = Math.floor((x - PR) / U), cx1 = Math.floor((x + PR) / U);
  const cy0 = Math.floor((y - PR) / U), cy1 = Math.floor((y + PR) / U);
  for (let cy = cy0; cy <= cy1; cy++) for (let cx = cx0; cx <= cx1; cx++) {
    if (cellBlocked(s, cx, cy) && circleBox(x, y, PR, cx * U, cy * U, cx * U + U, cy * U + U)) return true;
  }
  for (let k = 0; k < wag.length; k++) {
    if (ign & (1 << k)) continue;
    const w = wag[k];
    if (circleBox(x, y, PR, w.x - WAGON_HALF, w.y - WAGON_HALF, w.x + WAGON_HALF, w.y + WAGON_HALF)) return true;
  }
  return false;
}

/** Найбільший зсув по одній осі (0..d), що не впирається (бінарний пошук, цілі) */
function slide(s, pl, dx, dy, wag, ign) {
  if (!dx && !dy) return 0;
  if (!hitsAt(s, pl.x + dx, pl.y + dy, wag, ign)) { pl.x += dx; pl.y += dy; return 1; }
  let lo = 0, hi = 1024;   // частка ×1024
  for (let it = 0; it < 8; it++) {
    const m = (lo + hi) >> 1;
    if (hitsAt(s, pl.x + Math.trunc(dx * m / 1024), pl.y + Math.trunc(dy * m / 1024), wag, ign)) hi = m; else lo = m;
  }
  pl.x += Math.trunc(dx * lo / 1024); pl.y += Math.trunc(dy * lo / 1024);
  return -1;   // уперся
}

function movePlayer(s, n) {
  const pl = s.players[n];
  if (pl.out) return;
  if (pl.cd > 0) pl.cd--;
  if (pl.stun > 0) { pl.stun--; return; }   // K18: зірочки — стоїть
  if (s.params.move === 'grid') { moveGrid(s, n, pl); return; }       // K14
  if (s.params.move === 'snappy') { moveSnappy(s, n, pl); return; }   // K14
  let mx = pl.mx, my = pl.my;
  if (!mx && !my) return;
  // куди дивиться — переважна вісь (на рівних лишаємо поточну, якщо вона серед натиснутих)
  const ax = Math.abs(mx), ay = Math.abs(my);
  if (ax > ay || (ax === ay && !(pl.fy && Math.sign(my) === pl.fy))) { pl.fx = Math.sign(mx); pl.fy = 0; }
  else { pl.fx = 0; pl.fy = Math.sign(my); }
  // мертва зона й нормування (довжина ≤ 100)
  const len = Math.floor(Math.sqrt(mx * mx + my * my));
  if (len < 12) return;
  let v = Math.round(s.params.playerSpeed * U / TPS);
  if (s.ground[Math.floor(pl.y / U) * s.w + Math.floor(pl.x / U)] === G.SWAMP) v = Math.round(v * clampI(+s.params.swampSlow || 1, 0.05, 1));
  const d = Math.max(100, len);
  const dx = Math.trunc(v * mx / d), dy = Math.trunc(v * my / d);
  const wag = wagonsXY(s);
  let ign = 0;   // вагони, у яких уже стоїмо (потяг наїхав) — з них можна вийти
  for (let k = 0; k < wag.length; k++) if (hitsAt(s, pl.x, pl.y, [wag[k]], 0)) ign |= 1 << k;
  const x0 = pl.x, y0 = pl.y;
  const rx = slide(s, pl, dx, 0, wag, ign);
  const ry = slide(s, pl, 0, dy, wag, ign);
  // «Підтягування» до середини клітинки: уперся в ріг, а поперечного руху нема — ковзаємо вбік до проходу
  if (rx < 0 && !dy) nudge(s, pl, 'y', v, wag, ign);
  if (ry < 0 && !dx) nudge(s, pl, 'x', v, wag, ign);
  if (pl.x !== x0 || pl.y !== y0) {
    if (++pl.st >= 7) { pl.st = 0; ev(s, 'step', Math.floor(pl.x / U), Math.floor(pl.y / U), n); }
    autoPick(s, n, pl);
  }
}

/** K18: зсунути гравця на (dx,dy) з упором у стіни/вагони (штовханина) → [зсув x, зсув y] */
export function shove(s, pl, dx, dy) {
  const wag = wagonsXY(s), ign = wagIgnore(s, pl, wag), x0 = pl.x, y0 = pl.y;
  slide(s, pl, dx, 0, wag, ign); slide(s, pl, 0, dy, wag, ign);
  return [pl.x - x0, pl.y - y0];
}

function nudge(s, pl, axis, v, wag, ign) {
  const c = axis === 'y' ? pl.y : pl.x;
  const centre = Math.floor(c / U) * U + U / 2;
  const diff = centre - c;
  if (!diff) return;
  const m = Math.sign(diff) * Math.min(Math.abs(diff), v >> 1);
  if (axis === 'y') slide(s, pl, 0, m, wag, ign); else slide(s, pl, m, 0, wag, ign);
}

// ================= K14 «Відчуття ядра»: варіанти руху (move) =================
const GRID_TURN = 3;   // move='grid': з місця в новий бік — поворот; рушаємо, якщо тримати ще ~0,1 с

/** Намір (mx,my) → крок по одній осі [dx,dy]; на рівних — поточна вісь, якщо вона серед натиснутих */
function axisDir(pl, mx, my) {
  const ax = Math.abs(mx), ay = Math.abs(my);
  if (ax > ay || (ax === ay && !(pl.fy && Math.sign(my) === pl.fy))) return [Math.sign(mx), 0];
  return [0, Math.sign(my)];
}
/** Вагони, у яких уже стоїмо (потяг наїхав) — з них можна вийти */
function wagIgnore(s, pl, wag) {
  let ign = 0;
  for (let k = 0; k < wag.length; k++) if (hitsAt(s, pl.x, pl.y, [wag[k]], 0)) ign |= 1 << k;
  return ign;
}
/** Після зсуву: подія кроку й підбір свого стосу (як у movePlayer) */
function movedFeel(s, n, pl) {
  if (++pl.st >= 7) { pl.st = 0; ev(s, 'step', Math.floor(pl.x / U), Math.floor(pl.y / U), n); }
  autoPick(s, n, pl);
}
/** Ціль кроку — сусідня клітинка, куди дивимось; false — там стіна/вода/вагон */
function gridTarget(s, pl, wag, ign) {
  const nx = Math.floor(pl.x / U) + pl.fx, ny = Math.floor(pl.y / U) + pl.fy;
  if (cellBlocked(s, nx, ny) || hitsAt(s, nx * U + U / 2, ny * U + U / 2, wag, ign)) return false;
  pl.gx = nx; pl.gy = ny; return true;
}

/** move='grid' (як Unrailed): ходиш від центру до центру клітинок; з місця в новий бік — спершу поворот на місці
 *  (короткий натиск лише повертає — легко націлитись), утримання — крок; у русі повороти без паузи, назад — одразу. */
function moveGrid(s, n, pl) {
  const mx = pl.mx, my = pl.my;
  const len = Math.floor(Math.sqrt(mx * mx + my * my));
  const want = mx || my ? axisDir(pl, mx, my) : null;
  const go = want && len >= 12;              // легкий нахил (< мертвої зони) — лише дивитись
  const wag = wagonsXY(s), ign = wagIgnore(s, pl, wag);
  const x0 = pl.x, y0 = pl.y;
  if (pl.gx >= 0 && Math.abs(pl.gx - Math.floor(pl.x / U)) + Math.abs(pl.gy - Math.floor(pl.y / U)) > 1) pl.gx = pl.gy = -1;  // поза (мережа) перенесла
  if (pl.gx < 0) {   // стоїмо
    if (!want) { pl.gt = 0; return; }
    if (want[0] !== pl.fx || want[1] !== pl.fy) { pl.fx = want[0]; pl.fy = want[1]; pl.gt = 1; return; }
    if (!go) { pl.gt = 0; return; }
    if (pl.gt > 0 && pl.gt < GRID_TURN) { pl.gt++; return; }
    if (!gridTarget(s, pl, wag, ign)) return;
  } else if (go) {   // у русі: назад — розвертаємось одразу
    const dx = Math.sign(pl.gx * U + U / 2 - pl.x), dy = Math.sign(pl.gy * U + U / 2 - pl.y);
    if ((dx || dy) && want[0] === -dx && want[1] === -dy) { pl.gx -= dx; pl.gy -= dy; pl.fx = want[0]; pl.fy = want[1]; }
  }
  let rest = Math.round(s.params.playerSpeed * U / TPS);
  for (let seg = 0; seg < 3 && rest > 0 && pl.gx >= 0; seg++) {
    const tx = pl.gx * U + U / 2, ty = pl.gy * U + U / 2;
    const ax = pl.x, ay = pl.y;
    slide(s, pl, clampI(tx - pl.x, -rest, rest), 0, wag, ign);
    rest -= Math.abs(pl.x - ax);
    slide(s, pl, 0, clampI(ty - pl.y, -rest, rest), wag, ign);
    rest -= Math.abs(pl.y - ay);
    if (pl.x === tx && pl.y === ty) {        // доїхали до центру: тримаєш — без паузи далі
      pl.gx = pl.gy = -1; pl.gt = 0;
      if (!go) break;
      pl.fx = want[0]; pl.fy = want[1];
      if (!gridTarget(s, pl, wag, ign)) break;
    } else if (pl.x === ax && pl.y === ay) { // уперлись (вагон наїхав) — назад до центру своєї клітинки
      const cx = Math.floor(pl.x / U), cy = Math.floor(pl.y / U);
      if (cx === pl.gx && cy === pl.gy) break;
      pl.gx = cx; pl.gy = cy;
    }
  }
  if (pl.x !== x0 || pl.y !== y0) movedFeel(s, n, pl);
}

/** move='snappy': вільно, але 8 чітких напрямків на повній швидкості за будь-якого нахилу; ідеш уздовж осі — тягне до
 *  середини ряду (прохід у клітинку не муляє); погляд «прилипає» — по діагоналі не перескакує, легкий нахил лише повертає. */
function moveSnappy(s, n, pl) {
  const mx = pl.mx, my = pl.my;
  if (!mx && !my) return;
  const ax = Math.abs(mx), ay = Math.abs(my), m = Math.max(ax, ay);
  if (Math.floor(Math.sqrt(mx * mx + my * my)) < 12) { const d = axisDir(pl, mx, my); pl.fx = d[0]; pl.fy = d[1]; return; }
  const sx = ax * 1000 >= 414 * m ? Math.sign(mx) : 0, sy = ay * 1000 >= 414 * m ? Math.sign(my) : 0;   // сектори по 45°
  if (!sy) { pl.fx = sx; pl.fy = 0; } else if (!sx) { pl.fx = 0; pl.fy = sy; }
  else if (!((pl.fx && pl.fx === sx) || (pl.fy && pl.fy === sy))) { const d = axisDir(pl, mx, my); pl.fx = d[0]; pl.fy = d[1]; }
  const v = Math.round(s.params.playerSpeed * U / TPS), k = sx && sy ? 707 : 1000;
  const wag = wagonsXY(s), ign = wagIgnore(s, pl, wag);
  const x0 = pl.x, y0 = pl.y;
  const rx = slide(s, pl, Math.trunc(sx * v * k / 1000), 0, wag, ign);
  const ry = slide(s, pl, 0, Math.trunc(sy * v * k / 1000), wag, ign);
  if (!sy) nudge(s, pl, 'y', rx < 0 ? v : (v * 2 / 3) | 0, wag, ign);   // до середини ряду (уперся — швидше)
  if (!sx) nudge(s, pl, 'x', ry < 0 ? v : (v * 2 / 3) | 0, wag, ign);
  if (pl.x !== x0 || pl.y !== y0) movedFeel(s, n, pl);
}

// ================= K14: дія (useMode) і приціл — щотику після руху гравця =================
/** useMode: 'press' — затиснув → удари/рейки/мости повторюються з hitMs, взяти/покласти лише на натиск (як було);
 *  'tap' — кожна дія лише на новий натиск; 'auto' — з сокирою/кайлом перед деревом/скелею б'є сам (як Unrailed),
 *  кнопка — для решти. */
function feelTick(s, n) {
  const pl = s.players[n];
  if (!pl || pl.out) return;
  const h = pl.hold.k;
  if (s.params.useMode === 'auto' && pl.cd === 0 && (h === I.AXE || h === I.PICK)) {
    const f = aimCell(s, pl);
    if (inside(s, f.x, f.y) && s.obj[f.y * s.w + f.x] === (h === I.AXE ? O.TREE : O.ROCK)) {
      const ut = pl.ut; use(s, n, pl); pl.ut = ut;   // автоудар не рахується натиском кнопки
    }
  }
  if (s.params.aim === 'smart') {
    const f = aimCell(s, pl);
    const ok = useful(s, pl, f.x, f.y);
    if (!ok) pl.aim = null;
    else if (!pl.aim || pl.aim.x !== f.x || pl.aim.y !== f.y) pl.aim = { x: f.x, y: f.y };
  }
}

/** Чи дія на клітинці має сенс (для aim='smart'): удар інструментом, вагон, набрати воду, рейка в колію, взяти річ,
 *  долити свій стос. Просто «покласти на порожнє», міст і розібрати рейку — лише перед собою (щоб не сюрпризило). */
function useful(s, pl, cx, cy) {
  if (!inside(s, cx, cy)) return false;
  const i = cy * s.w + cx, h = pl.hold, P = s.params, t = s.train;
  if ((h.k === I.AXE && s.obj[i] === O.TREE) || (h.k === I.PICK && s.obj[i] === O.ROCK)) return true;
  const wk = wagonAt(s, cx, cy);
  if (wk >= 0) {
    if (wk === 0) return h.k === I.BUCKET_FULL;
    if (h.k === I.WOOD || h.k === I.IRON) return t[h.k === I.WOOD ? 'wood' : 'iron'] < (P.storeMax | 0);
    return wk === 2 && t.rails > 0 && (h.k === I.NONE || (h.k === I.RAIL && h.n < P.carryMax));
  }
  if (h.k === I.BUCKET) return s.ground[i] === G.WATER;
  if (h.k === I.RAIL && canLay(s, cx, cy)) return true;
  if (s.obj[i] !== O.NONE || s.ground[i] === G.WATER || s.drop[i] === I.NONE) return false;
  if (h.k === I.NONE) return true;
  return s.drop[i] === h.k && STACKABLE.has(h.k) && s.dropN[i] < FLOOR_STACK_MAX;
}

// сусіди в радіусі 1: спершу по сторонах, потім навскіс (на рівних відстанях)
const AIM8 = [[1, 0], [0, 1], [0, -1], [-1, 0], [1, 1], [1, -1], [-1, 1], [-1, -1]];
/** Клітинка, на яку діє use. aim='front' — facingCell; 'smart' — facingCell, якщо там є що робити, інакше найближча
 *  доречна з 8 сусідніх (трохи краще ті, що спереду), а як нема — facingCell. turn — повернутись до неї (по сторонах). */
export function aimCell(s, pl, turn = false) {
  const f = facingCell(pl);
  if (s.params.aim !== 'smart' || useful(s, pl, f.x, f.y)) return f;
  const cx = Math.floor(pl.x / U), cy = Math.floor(pl.y / U);
  let best = null, bd = Infinity;
  for (const [dx, dy] of AIM8) {
    const x = cx + dx, y = cy + dy;
    if ((x === f.x && y === f.y) || !useful(s, pl, x, y)) continue;
    const ex = x * U + U / 2 - pl.x, ey = y * U + U / 2 - pl.y;
    const d = ex * ex + ey * ey - (dx * pl.fx + dy * pl.fy) * 1000;
    if (d < bd) { bd = d; best = { x, y }; }
  }
  if (!best) return f;
  if (turn && (best.x === cx || best.y === cy)) { pl.fx = best.x - cx; pl.fy = best.y - cy; }
  return best;
}

/** Несеш стос — і стаєш на такий самий: добираєш до carryMax */
function autoPick(s, n, pl) {
  const k = pl.hold.k;
  if (!STACKABLE.has(k)) return;
  const cap = s.params.carryMax | 0;
  if (pl.hold.n >= cap) return;
  const cx = Math.floor(pl.x / U), cy = Math.floor(pl.y / U);
  const i = cy * s.w + cx;
  if (s.drop[i] !== k) return;
  const take = Math.min(cap - pl.hold.n, s.dropN[i]);
  pl.hold.n += take; s.dropN[i] -= take;
  if (!s.dropN[i]) s.drop[i] = I.NONE;
  ev(s, 'pick', cx, cy, n);
}

// ---------------- дія ----------------
/** Клітинка, на яку дивиться гравець: { x, y } (може бути поза смугою) — сусідня до тієї, де стоїть центр */
export function facingCell(pl) {
  return { x: Math.floor(pl.x / U) + pl.fx, y: Math.floor(pl.y / U) + pl.fy };
}

/** Який вагон стоїть на клітинці (cx,cy): індекс 0..2 або -1 */
export function wagonAt(s, cx, cy) {
  const mx = cx * U + U / 2, my = cy * U + U / 2;
  let best = -1, bd = U;   // центр вагона ближче ніж на клітинку (манхеттен)
  for (let k = 0; k < WAGONS.length; k++) {
    const w = wagonPos(s, k);
    const d = Math.abs(w.x - mx) + Math.abs(w.y - my);
    if (d < bd) { bd = d; best = k; }
  }
  return best;
}

/** Чи можна продовжити колію на клітинку (cx,cy) */
export function canLay(s, cx, cy) {
  if (!inside(s, cx, cy)) return false;
  const last = s.path[s.path.length - 1];
  const lx = last % s.w, ly = (last / s.w) | 0;
  if (Math.abs(lx - cx) + Math.abs(ly - cy) !== 1) return false;
  const i = cy * s.w + cx;
  if (s.obj[i] !== O.NONE || s.ground[i] === G.WATER || s.drop[i] !== I.NONE) return false;
  if (s.path.includes(i)) return false;
  return true;
}

/** Чи можна розібрати останню рейку (потяг до неї ще не під'їхав) */
export function canUnlay(s) {
  const L = s.path.length - 1;
  return L >= WAGONS.length && L * U > s.train.pos + U;
}

function cellFreeForDrop(s, cx, cy) {
  if (!inside(s, cx, cy)) return false;
  const i = cy * s.w + cx;
  return s.obj[i] === O.NONE && s.ground[i] !== G.WATER && wagonAt(s, cx, cy) < 0;
}

function hold0(pl) { pl.hold.k = I.NONE; pl.hold.n = 0; }

function use(s, n, pl) {
  const held = pl.ut > 0 && pl.ut === s.tick;   // use був і в попередньому тику
  pl.ut = s.tick + 1;
  if (held && s.params.useMode === 'tap') return;   // K14: без автоповтору — лише натиск
  if (pl.cd > 0 || pl.stun > 0) return;   // K18: stun
  const P = s.params;
  const f = aimCell(s, pl, true);   // K14: aim='front' — facingCell, 'smart' — доречна поруч
  const cx = f.x, cy = f.y;
  if (extrasUse(s, n, f)) { pl.cd = ACT_CD; return; }   // K11: гуска / табличка покращень
  if (!inside(s, cx, cy)) return;
  const i = cy * s.w + cx;
  const h = pl.hold;
  // 1) удар інструментом
  if ((h.k === I.AXE && s.obj[i] === O.TREE) || (h.k === I.PICK && s.obj[i] === O.ROCK)) {
    pl.cd = Math.max(1, Math.round(P.hitMs / TICK_MS));
    s.stats.hits++;
    const tree = s.obj[i] === O.TREE;
    if (s.hp[i] > 1) { s.hp[i]--; ev(s, tree ? 'chop' : 'rock', cx, cy, n); return; }
    s.hp[i] = 0; s.obj[i] = O.NONE;
    const k = tree ? I.WOOD : I.IRON, cnt = tree ? P.woodPerTree | 0 : P.ironPerRock | 0;
    putDrop(s, cx, cy, k, cnt);
    ev(s, tree ? 'chop' : 'rock', cx, cy, n);
    return;
  }
  // 2) вагони
  const wk = wagonAt(s, cx, cy);
  if (wk >= 0) {
    if (held) return;
    const t = s.train;
    if (wk === 0 && h.k === I.BUCKET_FULL) {
      t.heat = 0; t.ht = 0; t.fire = 0; h.k = I.BUCKET; s.stats.douses++;
      ev(s, 'douse', cx, cy, n); pl.cd = ACT_CD; return;
    }
    if (wk >= 1 && (h.k === I.WOOD || h.k === I.IRON)) {     // на склад (майстерня теж приймає — вона бере зі складу)
      const key = h.k === I.WOOD ? 'wood' : 'iron';
      const put = Math.min(h.n, Math.max(0, (P.storeMax | 0) - t[key]));
      if (put > 0) { t[key] += put; h.n -= put; if (!h.n) hold0(pl); ev(s, 'drop', cx, cy, n); pl.cd = ACT_CD; }
      return;
    }
    if (wk === 2 && (h.k === I.NONE || (h.k === I.RAIL && h.n < P.carryMax)) && t.rails > 0) {
      const take = Math.min(t.rails, (P.carryMax | 0) - h.n);
      t.rails -= take; h.k = I.RAIL; h.n += take;
      ev(s, 'pick', cx, cy, n); pl.cd = ACT_CD; return;
    }
    return;
  }
  // 3) відро біля води
  if (h.k === I.BUCKET && s.ground[i] === G.WATER) {
    if (held) return;
    h.k = I.BUCKET_FULL; ev(s, 'water', cx, cy, n); pl.cd = ACT_CD; return;
  }
  // 4) рейка в продовження колії
  if (h.k === I.RAIL && canLay(s, cx, cy)) {
    s.path.push(i); h.n--; if (!h.n) hold0(pl);
    s.stats.railsLaid++; ev(s, 'rail', cx, cy, n); pl.cd = ACT_CD; return;
  }
  // 5) дерево на воду — місток
  if (h.k === I.WOOD && s.ground[i] === G.WATER && s.obj[i] === O.NONE) {
    s.ground[i] = G.BRIDGE; h.n--; if (!h.n) hold0(pl);
    s.stats.bridges++; ev(s, 'rail', cx, cy, n); pl.cd = ACT_CD; return;
  }
  if (coopUse(s, n, pl, cx, cy, held)) return;   // K18: передати партнерові / «бац»
  if (held) return;   // далі — лише на натиск
  // 6) порожні руки: взяти стос / розібрати останню рейку
  if (h.k === I.NONE) {
    if (s.drop[i] !== I.NONE) {
      const k = s.drop[i];
      const take = STACKABLE.has(k) ? Math.min(P.carryMax | 0, s.dropN[i]) : 1;
      h.k = k; h.n = take; s.dropN[i] -= take; if (!s.dropN[i]) s.drop[i] = I.NONE;
      ev(s, 'pick', cx, cy, n); pl.cd = ACT_CD; return;
    }
    if (s.path[s.path.length - 1] === i && canUnlay(s)) {
      s.path.pop(); h.k = I.RAIL; h.n = 1; s.stats.railsLaid--;
      ev(s, 'pick', cx, cy, n); pl.cd = ACT_CD; return;
    }
    return;
  }
  // 7) з річчю: покласти на порожню / той самий стос; інша річ — обмін, якщо її стос поміщається в руки
  if (!cellFreeForDrop(s, cx, cy)) return;
  if (s.drop[i] === I.NONE) {
    s.drop[i] = h.k; s.dropN[i] = h.n; hold0(pl);
    ev(s, 'drop', cx, cy, n); pl.cd = ACT_CD; return;
  }
  if (s.drop[i] === h.k && STACKABLE.has(h.k)) {
    const put = Math.min(h.n, FLOOR_STACK_MAX - s.dropN[i]);
    if (put > 0) { s.dropN[i] += put; h.n -= put; if (!h.n) hold0(pl); ev(s, 'drop', cx, cy, n); pl.cd = ACT_CD; }
    return;
  }
  if (s.drop[i] !== h.k && s.dropN[i] <= (P.carryMax | 0)) {
    const k = s.drop[i], cnt = s.dropN[i];
    s.drop[i] = h.k; s.dropN[i] = h.n; h.k = k; h.n = cnt;
    ev(s, 'pick', cx, cy, n); pl.cd = ACT_CD; return;
  }
}

/** Покласти cnt речей k на клітинку або (якщо там інше) на найближчу вільну поруч; не влізло — пропало */
export function putDrop(s, cx, cy, k, cnt) {
  const tryAt = (x, y) => {
    if (!cellFreeForDrop(s, x, y)) return false;
    const i = y * s.w + x;
    if (s.drop[i] === I.NONE) { s.drop[i] = k; s.dropN[i] = Math.min(cnt, FLOOR_STACK_MAX); return true; }
    if (s.drop[i] === k && STACKABLE.has(k) && s.dropN[i] + cnt <= FLOOR_STACK_MAX) { s.dropN[i] += cnt; return true; }
    return false;
  };
  if (tryAt(cx, cy)) return true;
  for (let r = 1; r <= 3; r++) for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
    if (Math.max(Math.abs(dx), Math.abs(dy)) !== r) continue;
    if (tryAt(cx + dx, cy + dy)) return true;
  }
  return false;
}

function join(s) {
  let n = s.players.findIndex((p) => p.out);
  if (n < 0) { if (s.players.length >= MAX_PLAYERS) return; n = s.players.length; s.players.push(null); }
  // з'являємося біля потяга (старт міг лишитися далеко позаду)
  const c = nearFree(s, wagonPos(s, WAGONS.length - 1));
  s.players[n] = newPlayer(c || s.spawn[n % s.spawn.length]);
}

function leave(s, pl) {
  if (pl.hold.k !== I.NONE) putDrop(s, Math.floor(pl.x / U), Math.floor(pl.y / U), pl.hold.k, pl.hold.n);
  hold0(pl); pl.out = 1; pl.mx = pl.my = 0; pl.cd = 0;
  pl.x = -5 * U; pl.y = -5 * U;
}

/** Найближча прохідна клітинка поза колією біля точки (підодиниці) */
function nearFree(s, pt) {
  const x0 = Math.floor(pt.x / U), y0 = Math.floor(pt.y / U);
  for (let r = 1; r <= 6; r++) for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
    if (Math.max(Math.abs(dx), Math.abs(dy)) !== r) continue;
    const x = x0 + dx, y = y0 + dy;
    if (!cellBlocked(s, x, y) && !s.path.includes(y * s.w + x)) return { x, y };
  }
  return null;
}

// ---------------- майстерня, паротяг, котел ----------------
function stepCraft(s) {
  const t = s.train, P = s.params;
  if (t.craftT > 0) {
    if (--t.craftT === 0) {
      t.rails = Math.min(Math.max(t.rails, P.railsMax | 0), t.rails + Math.max(1, P.railsPerCraft | 0)); s.stats.crafted++;
      const w = wagonPos(s, 2); ev(s, 'craft', Math.floor(w.x / U), Math.floor(w.y / U));
    }
    return;
  }
  if (t.wood >= 1 && t.iron >= 1 && t.rails < (P.railsMax | 0)) {
    t.wood--; t.iron--; t.craftT = ticks(P.craftSec);
  }
}

/** Тик, з якого паротяг рушає */
export const startTick = (s) => Math.round(s.params.startSec * TPS);

function stepTrain(s) {
  const t = s.train, P = s.params;
  const t0 = startTick(s);
  if (s.tick < t0) return;
  if (s.tick === t0) { t.started = 1; const w = wagonPos(s, 0); ev(s, 'whistle', Math.floor(w.x / U), Math.floor(w.y / U)); }
  const head = wagonPos(s, 0), hx = Math.floor(head.x / U), hy = Math.floor(head.y / U);
  // котел
  const heatT = ticks(P.heatSec / soloK(s));   // K13: соло — котел гріється так само повільніше, як їде
  if (t.heat < 1000) {
    t.ht++;
    const prev = t.heat;
    t.heat = Math.min(1000, Math.floor(t.ht * 1000 / heatT));
    if (prev < 750 && t.heat >= 750) ev(s, 'alarm', hx, hy);
  }
  let stopped = false;
  if (t.heat >= 1000) {
    if (P.overheat === 'stop') stopped = true;
    else {
      if (t.fire === 0) ev(s, 'alarm', hx, hy);
      t.fire++;
      if (t.fire >= ticks(P.fireSec)) { lose(s, 'fire', hx, hy); return; }
    }
  }
  // швидкість
  const el = s.tick - t0;
  t.speed = stopped ? 0 : Math.min(spd(P.speedMax), trainSpeed(s, el));   // K14: крива — варіант speedCurve
  if (!stopped) t.speed = balanceSpeed(s, t.speed);   // K13: соло-знижка, ривок до станції
  if (t.speed > s.stats.maxSpeed) s.stats.maxSpeed = t.speed;
  t.sub += t.speed;
  const d = Math.floor(t.sub / 1000);
  t.sub -= d * 1000;
  const before = Math.floor(t.pos / U);
  const end = (s.path.length - 1) * U;
  const over = t.pos + d > end;   // голова вже в кінці колії й хоче далі — сходить
  t.pos = Math.min(end, t.pos + d);
  const hi = Math.floor(t.pos / U);
  s.stats.cells += hi - before;
  const hc = s.path[hi];
  if (s.ground[hc] === G.STATION) {
    s.status = 'won'; s.reason = '';
    ev(s, 'whistle', hc % s.w, (hc / s.w) | 0); ev(s, 'won', hc % s.w, (hc / s.w) | 0);
    return;
  }
  if (over) lose(s, 'derail', hc % s.w, (hc / s.w) | 0);
}

/** K14: швидкість паротяга до стелі (підодиниць/тик ×1000). speedCurve: 'linear' — speedStart + speedGrowth·хвилини
 *  (як було); 'steps' — + stepGain за кожні stepN покладених рейок (повільно класти — повільно їде);
 *  'distance' — + stepGain за кожні stepN проїханих клітинок, плавно (повільний старт не карає). */
function trainSpeed(s, el) {
  const P = s.params, base = spd(P.speedStart), n = Math.max(1, P.stepN | 0);
  if (P.speedCurve === 'steps') return base + spd(P.stepGain) * Math.floor(Math.max(0, s.stats.railsLaid) / n);
  if (P.speedCurve === 'distance') return base + Math.floor(spd(P.stepGain) * s.stats.cells / n);
  return base + Math.floor(spd(P.speedGrowth) * el / (60 * TPS));
}

/** K13: множник соло-знижки: 1 — за столом двоє й більше (чи знижку вимкнено) */
function soloK(s) {
  const k = +s.params.soloSpeed;
  if (!(k > 0 && k < 1)) return 1;
  let n = 0;
  for (const p of s.players) if (p && !p.out) n++;
  return n <= 1 ? k : 1;
}

/** K13: соло (за столом один гравець) — швидкість × soloSpeed; колія вже дотягнута до станції — не повільніше за
 *  finishSpeed (кінець без нудного чекання, як «прискорити» в Unrailed). */
function balanceSpeed(s, v) {
  const P = s.params, k = soloK(s);
  if (k < 1) v = Math.round(v * k);
  if (P.finishSpeed > 0 && s.ground[s.path[s.path.length - 1]] === G.STATION) v = Math.max(v, spd(P.finishSpeed));
  return v;
}

function lose(s, why, x, y) {
  s.status = 'lost'; s.reason = why;
  ev(s, 'lost', x, y);
}

/** Де зараз вагон k (0 — паротяг): { x, y } у підодиницях, кут — з сусідніх клітинок колії */
export function wagonPos(s, k) {
  const pos = s.train.pos - k * U;
  const i = Math.max(0, Math.min(s.path.length - 1, Math.floor(pos / U)));
  const j = Math.min(s.path.length - 1, i + 1);
  const f = Math.max(0, Math.min(U, pos - i * U));
  const a = s.path[i], b = s.path[j];
  const ax = (a % s.w) * U + U / 2, ay = Math.floor(a / s.w) * U + U / 2;
  const bx = (b % s.w) * U + U / 2, by = Math.floor(b / s.w) * U + U / 2;
  return { x: ax + Math.round((bx - ax) * f / U), y: ay + Math.round((by - ay) * f / U) };
}

export function clone(s) { return structuredClone(s); }

const STATUS_CODE = { run: 0, won: 1, lost: 2 };
const REASON_CODE = { '': 0, derail: 1, fire: 2 };

/** FNV-1a 32 по всьому, що впливає на гру (без ev і params) — для звірки мережі */
export function hash(s) {
  let h = 0x811C9DC5;
  const mix = (v) => { h ^= v & 0xFF; h = Math.imul(h, 16777619); h ^= (v >>> 8) & 0xFF; h = Math.imul(h, 16777619);
    h ^= (v >>> 16) & 0xFF; h = Math.imul(h, 16777619); h ^= (v >>> 24) & 0xFF; h = Math.imul(h, 16777619); };
  mix(s.tick); mix(s.rng); mix(STATUS_CODE[s.status] | 0); mix(REASON_CODE[s.reason] | 0);
  for (const a of [s.ground, s.obj, s.hp, s.drop, s.dropN]) for (let i = 0; i < a.length; i++) mix(a[i]);
  mix(s.path.length);
  for (const c of s.path) mix(c);
  const t = s.train; mix(t.pos); mix(t.speed); mix(t.heat); mix(t.fire); mix(t.wood); mix(t.iron); mix(t.rails); mix(t.craftT);
  mix(t.sub); mix(t.ht); mix(t.started);
  mix(s.players.length);
  for (const p of s.players) {
    mix(p.x); mix(p.y); mix(p.fx); mix(p.fy); mix(p.hold.k); mix(p.hold.n); mix(p.cd);
    mix(p.mx); mix(p.my); mix(p.ut); mix(p.out); mix(p.st);
    if (s.params.move === 'grid') { mix(p.gx); mix(p.gy); mix(p.gt); }   // K14 (типовий hash не змінюється)
    for (const k of Object.keys(p).sort()) if (!HASH_PL[k]) { mixStr(mix, k); mixAny(mix, p[k]); }   // K6: нові поля гравця
  }
  coopHash(s, mix);   // K18 (лише ввімкнене)
  for (const k of Object.keys(t).sort()) if (!HASH_TR[k]) { mixStr(mix, k); mixAny(mix, t[k]); }      // K6: нові поля потяга
  for (const k of Object.keys(s).sort()) if (!HASH_TOP[k]) { mixStr(mix, k); mixAny(mix, s[k]); }     // K6: гуси, s.mid, s.flying…
  if (s.endless) mix(endlessHash(s));   // K19 (типовий hash не змінюється)
  return h >>> 0;
}

// K6: що hash уже мішає поштучно (чи що не впливає на світ: ev, stats, params, spawn, aim) — решта полів стану (додатки
// K11 гуси/s.mid, K18 s.flying і що з'явиться далі) іде в hash сама, загально, за відсортованими ключами.
const HASH_TOP = Object.fromEntries('seed tick rng params status reason w h ground obj hp drop dropN path spawn train players ev stats'.split(' ').map((k) => [k, 1]));
const HASH_PL = Object.fromEntries('x y fx fy hold cd mx my ut out st gx gy gt aim'.split(' ').map((k) => [k, 1]));
const HASH_TR = Object.fromEntries('pos speed heat fire wood iron rails craftT sub ht started'.split(' ').map((k) => [k, 1]));
function mixStr(mix, str) { mix(str.length); for (let i = 0; i < str.length; i++) mix(str.charCodeAt(i)); }
function mixAny(mix, v) {
  if (v === null || v === undefined) { mix(v === null ? -1 : -2); return; }
  if (typeof v === 'number') { if (Number.isInteger(v) && Math.abs(v) < 2 ** 31) mix(v); else mixStr(mix, String(v)); return; }
  if (typeof v === 'boolean') { mix(v ? -3 : -4); return; }
  if (typeof v === 'string') { mix(-5); mixStr(mix, v); return; }
  if (typeof v !== 'object') return;   // функції тощо — не стан
  if (Array.isArray(v) || ArrayBuffer.isView(v)) { mix(-6); mix(v.length); for (let i = 0; i < v.length; i++) mixAny(mix, v[i]); return; }
  mix(-7);
  for (const k of Object.keys(v).sort()) { mixStr(mix, k); mixAny(mix, v[k]); }
}
