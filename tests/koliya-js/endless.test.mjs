// «Колія» — нескінченна колія K19 (stationMode = 'endless'): ділянки від сіду й номера, зупинки, зсув вікна, бот, пам'ять.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import * as E from '../../web/games/koliya/endless.js';
import { generate, findRoute } from '../../web/games/koliya/gen.js';
import { createBot } from '../../web/games/koliya/bot.js';

const { U, G, O, I, TPS } = R;
const P = (o = {}) => ({ ...R.defaultParams(), stationMode: 'endless', ...o });
const EASY = { stripLen: 60, speedStart: 0.12, speedGrowth: 0, heatSec: 240, startSec: 20, winter: 'off' };   // K26: світ сідів — як до зими (зима в нескінченній — biome.test)   // бот поки слабкий — легка смуга
const adj = (s, a, b) => Math.abs(a % s.w - b % s.w) + Math.abs(((a / s.w) | 0) - ((b / s.w) | 0)) === 1;

function playBots(seed, nBots, params, maxMin, onTick) {
  const s = R.newGame(seed, P(params), nBots);
  const bots = Array.from({ length: nBots }, (_, i) => createBot(i));
  while (s.status === 'run' && s.tick < maxMin * 60 * TPS) {
    const cmds = [];
    for (const b of bots) { const v = b.think(s); cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 }); if (v.use) cmds.push({ p: b.p, k: 'use' }); }
    R.step(s, cmds);
    if (onTick) onTick(s);
  }
  return s;
}

test('інші режими не чіпає: s.endless = null, ширина — stripLen', () => {
  for (const m of ['end', 'upgrade']) {
    const s = R.newGame(7, P({ stationMode: m }), 1);
    assert.equal(s.endless, null, m);
  }
  assert.equal(R.newGame(7, R.defaultParams(), 1).w, R.defaultParams().stripLen);
});

test('старт: вікно 2·L, ліва половина — той самий світ, що й звичайний; колія перерахована', () => {
  const p = P(), plain = R.newGame(42, { ...p, stationMode: 'end' }, 2), s = R.newGame(42, p, 2);
  const L = plain.w, W = s.w, h = s.h;
  assert.equal(W, 2 * L); assert.equal(s.endless.L, L);
  for (const k of ['ground', 'obj', 'hp', 'drop', 'dropN']) {
    assert.equal(s[k].length, W * h);
    for (let y = 0; y < h; y++) for (let x = 0; x < L; x++) assert.equal(s[k][y * W + x], plain[k][y * L + x], `${k} ${x},${y}`);
  }
  assert.deepEqual(s.path.map((c) => [c % W, (c / W) | 0]), plain.path.map((c) => [c % L, (c / L) | 0]));
  // інструменти — лише в нульовій ділянці
  for (let i = 0; i < W * h; i++) if (s.drop[i] >= I.AXE) assert.ok(i % W < L);
  assert.equal(s.endless.st.length, 2);
  assert.ok(s.endless.st[1].x0 >= 2 * L - 4 && s.endless.st[0].x1 < L);
});

test('ділянки від сіду й номера: детерміновані, різні, складність росте; кожна прохідна до своєї станції', () => {
  const p = P();
  const a = E.segment(7, p, 3), b = E.segment(7, p, 3), c = E.segment(7, p, 4);
  assert.deepEqual(a.ground, b.ground); assert.deepEqual(a.obj, b.obj);
  assert.notDeepEqual(a.obj, c.obj);
  assert.ok(E.segParams(p, 8).hardGrow > E.segParams(p, 1).hardGrow);
  assert.equal(E.segParams(p, 20).hardGrow, E.segParams(p, 8).hardGrow, 'стеля складності');
  assert.equal(E.segSeed(99, 0), 99);
  for (const seed of [7, 42, 1001, 31337]) for (let k = 1; k <= 6; k++) {
    const g = E.segment(seed, p, k);
    assert.ok(findRoute(g), `сід ${seed}, ділянка ${k}: нема маршруту`);
    assert.ok(!g.drop.some((d) => d >= I.AXE), 'інструменти лише в нульовій');
  }
});

test('межа ділянок прохідна: з колії нульової через її станцію до станції першої', () => {
  for (const seed of [7, 42, 1001]) {
    const s = R.newGame(seed, P(), 1);
    const f = s.endless.st[0];
    const g = s.ground.slice();
    for (let y = f.y0; y <= f.y1; y++) for (let x = f.x0; x <= f.x1; x++) g[y * s.w + x] = G.SAND;   // перша станція «пройдена»
    const r = findRoute({ w: s.w, h: s.h, ground: g, obj: s.obj, path: s.path });
    assert.ok(r, `сід ${seed}`);
    assert.ok(r[r.length - 1] % s.w >= s.endless.st[1].x0, `сід ${seed}: дійшли до другої`);
  }
});

/** Чиста смуга, колія прокладена до кінця вікна, швидкий паротяг */
function straight(params = {}, n = 1) {
  const s = R.newGame(5, P({ stripLen: 40, forest: 0, rocks: 0, water: 0, swamp: 0, speedStart: 1.5, speedMax: 1.5, startSec: 0, heatSec: 240, ...params }), n);
  for (let i = 0; i < s.w * s.h; i++) {
    const y = (i / s.w) | 0;
    if (y > 0 && y < s.h - 1) { s.obj[i] = O.NONE; s.hp[i] = 0; if (s.ground[i] === G.WATER) s.ground[i] = G.GRASS; }
  }
  const mid = s.h >> 1;
  for (let x = (s.path[s.path.length - 1] % s.w) + 1; x < s.w; x++) s.path.push(mid * s.w + x);
  return s;
}

test('станція: зупинка STOP_SEC, рахунок +1, таблички; обрав покращення — рушає швидше', () => {
  const s = straight();
  let arrived = -1, left = -1;
  for (let t = 0; t < 120 * TPS && left < 0; t++) {
    R.step(s, []);
    if (s.ev.some((e) => e.t === 'station')) { arrived = s.tick; assert.equal(s.endless.stations, 1); assert.ok(s.mid && s.mid.signs.length === 3); }
    if (arrived >= 0 && s.endless.stop === 0 && left < 0) left = s.tick;
  }
  assert.ok(arrived > 0, 'доїхав');
  assert.ok(Math.abs(left - arrived - E.STOP_SEC * TPS) <= 2, `стояв ${(left - arrived) / TPS} с`);
  assert.equal(s.mid, null, 'таблички прибрано, коли рушив');
  // ніколи не «перемога»: станції під колією — пісок
  assert.equal(s.status, 'run');
  for (const f of s.endless.st) for (let y = f.y0; y <= f.y1; y++) for (let x = f.x0; x <= f.x1; x++) assert.notEqual(s.ground[y * s.w + x], G.STATION);

  // з покращенням: стоїть ~PICK_SEC
  const q = straight();
  let at = -1;
  for (let t = 0; t < 120 * TPS; t++) {
    R.step(q, []);
    if (q.ev.some((e) => e.t === 'station')) { at = q.tick; break; }
  }
  const sign = q.mid.signs[0], pl = q.players[0];
  pl.x = sign.x * U + U / 2; pl.y = (sign.y - 1) * U + U / 2; pl.fx = 0; pl.fy = 1;
  const before = { carry: q.params.carryMax, store: q.params.storeMax, heat: q.params.heatSec, craft: q.params.craftSec };
  R.step(q, [{ p: 0, k: 'use' }]);
  assert.equal(q.mid.pick, sign.k);
  assert.ok(q.ev.some((e) => e.t === 'upgrade'));
  assert.notDeepEqual({ carry: q.params.carryMax, store: q.params.storeMax, heat: q.params.heatSec, craft: q.params.craftSec }, before);
  let go = -1;
  for (let t = 0; t < 10 * TPS && go < 0; t++) { R.step(q, []); if (q.endless.stop === 0) go = q.tick; }
  assert.ok(go - at <= PICK_TICKS() + 3, `рушив за ${(go - at) / TPS} с`);
});
const PICK_TICKS = () => E.PICK_SEC * TPS;

test('зсув вікна: ліва ділянка відрізана, усе на L ліворуч, позаду — до хвоста, інструменти врятовано', () => {
  const s = straight({}, 2);
  const L = s.endless.L, W = s.w, n0 = s.ground.length;
  // гравець 1 лишився на старті, сокира — теж
  const p1 = s.players[1]; p1.x = 2 * U + U / 2; p1.y = 3 * U + U / 2;
  let axes = 0; for (let i = 0; i < s.drop.length; i++) if (s.drop[i] === I.AXE) axes++;
  assert.equal(axes, 1);
  let prevX = 0, shifted = false;
  for (let t = 0; t < 200 * TPS && !shifted; t++) {
    prevX = R.wagonPos(s, 0).x;
    R.step(s, []);
    if (s.ev.some((e) => e.t === 'shift')) {
      shifted = true;
      const x = R.wagonPos(s, 0).x;
      assert.ok(Math.abs(x - (prevX - L * U)) <= 200, `голова ${prevX} → ${x}`);
    }
  }
  assert.ok(shifted, 'зсунули');
  assert.equal(s.w, W); assert.equal(s.ground.length, n0);
  assert.equal(s.endless.base, 1); assert.equal(s.endless.shift, 1); assert.equal(s.endless.dx, L);
  for (let k = 1; k < s.path.length; k++) assert.ok(adj(s, s.path[k - 1], s.path[k]), `колія суміжна, ${k}`);
  assert.ok(s.path.every((c) => c >= 0 && c < W * s.h));
  const tail = R.wagonPos(s, 2);
  assert.ok(p1.x >= 0 && Math.abs(p1.x - tail.x) + Math.abs(p1.y - tail.y) <= 8 * U, 'відсталого перенесено до хвоста');
  axes = 0; for (let i = 0; i < s.drop.length; i++) if (s.drop[i] === I.AXE) axes++;
  assert.equal(axes, 1, 'сокира не пропала');
  // права половина — нова ділянка 2 (без інструментів), станція — G.STATION
  const f = s.endless.st[1];
  assert.equal(s.ground[((f.y0 + f.y1) >> 1) * W + f.x0], G.STATION);
  const seg2 = E.segment(s.seed, s.params, 2);
  for (let y = 0; y < s.h; y++) for (let x = L; x < W; x++) if (!s.path.includes(y * W + x)) assert.equal(s.obj[y * W + x], seg2.obj[y * L + x - L]);
  // їде далі й рахує
  for (let t = 0; t < 200 * TPS && s.endless.stations < 3; t++) {
    const c = s.path[s.path.length - 1];
    if (c % W < W - 1) s.path.push(c + 1);   // «гравці» тягнуть колію по прямій
    R.step(s, []);
  }
  assert.ok(s.endless.stations >= 3, `станцій ${s.endless.stations}`);
  assert.equal(s.status, 'run');
});

test('hash враховує стан нескінченної колії; clone не розходиться', () => {
  const s = straight();
  for (let t = 0; t < 30 * TPS; t++) R.step(s, []);
  const c = R.clone(s);
  assert.equal(R.hash(c), R.hash(s));
  c.endless.stations++;
  assert.notEqual(R.hash(c), R.hash(s));
});

test('детермінізм: двоє ботів 15 хв (через зсув) — однаковий hash, і з clone посередині', () => {
  const run = () => {
    const s = R.newGame(42, P(EASY), 2);
    const bots = [createBot(0), createBot(1)];
    let shifts = 0;
    for (let t = 0; t < 15 * 60 * TPS && s.status === 'run'; t++) {
      const cmds = [];
      for (const b of bots) { const v = b.think(s); cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 }); if (v.use) cmds.push({ p: b.p, k: 'use' }); }
      R.step(s, cmds);
      if (s.ev.some((e) => e.t === 'shift')) shifts++;
    }
    return { h: R.hash(s), shifts, st: s.endless.stations };
  };
  const a = run(), b = run();
  assert.equal(a.h, b.h);
  assert.ok(a.shifts >= 1, `зсувів ${a.shifts}`);
  // clone посередині: правила ті самі — порівнюємо прогін «стан → clone → ті самі команди»
  const s1 = R.newGame(42, P(EASY), 2), bots = [createBot(0), createBot(1)];
  const log = [];
  for (let t = 0; t < 14 * 60 * TPS; t++) {
    const cmds = [];
    for (const bt of bots) { const v = bt.think(s1); cmds.push({ p: bt.p, k: 'in', mx: v.mx | 0, my: v.my | 0 }); if (v.use) cmds.push({ p: bt.p, k: 'use' }); }
    log.push(cmds);
    R.step(s1, cmds);
  }
  let s2 = R.newGame(42, P(EASY), 2);
  for (let t = 0; t < log.length; t++) { R.step(s2, log[t]); if (t === 6 * 60 * TPS) s2 = R.clone(s2); }
  assert.equal(R.hash(s2), R.hash(s1));
  assert.ok(s1.endless.shift >= 1);
});

test('бот їде через межу ділянок: ≥ 2 станцій, зсув, пам\'ять не росте', () => {
  for (const seed of [42, 1001]) {
    let maxPath = 0, maxZones = 0;
    const s = playBots(seed, 2, EASY, 20, (s) => { maxPath = Math.max(maxPath, s.path.length); maxZones = Math.max(maxZones, s.zones.length); });
    const e = s.endless;
    console.log(`сід ${seed}: ${s.status}${s.reason ? '/' + s.reason : ''} за ${Math.round(s.tick / TPS)} с, станцій ${e.stations}, зсувів ${e.shift}, колія ${s.path.length} (макс ${maxPath}), ділянок-біомів ${maxZones}`);
    assert.ok(e.stations >= 2, `сід ${seed}: станцій ${e.stations}`);
    assert.ok(e.shift >= 1);
    assert.equal(s.w, 2 * e.L);
    for (const k of ['ground', 'obj', 'hp', 'drop', 'dropN']) assert.equal(s[k].length, s.w * s.h);
    assert.ok(maxPath <= s.w * 2, `колія ${maxPath}`);
    assert.ok(maxZones <= 2 * Math.ceil(e.L / 8) + 2, `біоми ${maxZones}`);
  }
});
