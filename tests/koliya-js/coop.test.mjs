// «Колія» K18 — кооп-фішки: bump, throw, pass, stun, horn. node --test tests/koliya-js/coop.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';

const { U, G, O, I, PR } = R;

/** Порожня смуга (як у feel.test.mjs), паротяг стоїть довго */
function blank(params = {}, n = 2) {
  const p = { ...R.defaultParams(), forest: 0, rocks: 0, water: 0, winter: 'off', startSec: 90, ...params };   // K26: порожня смуга — без снігу й льоду
  const s = R.newGame(7, p, n);
  for (let i = 0; i < s.w * s.h; i++) {
    const y = (i / s.w) | 0;
    if (y > 0 && y < s.h - 1) { s.obj[i] = O.NONE; s.hp[i] = 0; s.drop[i] = I.NONE; s.dropN[i] = 0; if (s.ground[i] === G.WATER) s.ground[i] = G.GRASS; }
  }
  return s;
}
const at = (s, n, cx, cy, fx = 1, fy = 0) => { const pl = s.players[n]; pl.x = cx * U + U / 2; pl.y = cy * U + U / 2; pl.fx = fx; pl.fy = fy; return pl; };
const id = (s, x, y) => y * s.w + x;
const run = (s, n, cmds = []) => { for (let i = 0; i < n; i++) R.step(s, i === 0 ? cmds : []); };
const evs = (s, n, cmds, t) => { const out = []; for (let i = 0; i < n; i++) { R.step(s, i === 0 ? cmds : []); out.push(...s.ev.filter((e) => e.t === t)); } return out; };
const Y = 3, X = 30;   // далеко від колії (вона посередині смуги)
const hold = (pl, k, n = 1) => { pl.hold.k = k; pl.hold.n = n; };

test('типові: bump/throw/stun вимкнені, pass увімкнений; s.flying порожній', () => {
  const d = R.defaultParams();
  assert.equal(d.bump, 'off'); assert.equal(d.throw, 'off'); assert.equal(d.stun, 'off'); assert.equal(d.pass, 'on');
  const s = blank();
  assert.deepEqual(s.flying, []);
  hold(at(s, 0, X, Y), I.WOOD, 2);
  run(s, 1, [{ p: 0, k: 'throw' }]);
  assert.equal(s.players[0].hold.n, 2, 'throw вимкнено — нічого');
  assert.equal(s.flying.length, 0);
});

// ---------------- bump ----------------
test('bump: зустрічні не проходять крізь одне одного; вимкнено — проходять', () => {
  for (const mode of ['off', 'on']) {
    const s = blank({ bump: mode });
    at(s, 0, X, Y); at(s, 1, X + 3, Y, -1);
    let crossed = false, minD = 1e9;
    for (let t = 0; t < 40; t++) {
      R.step(s, t === 0 ? [{ p: 0, k: 'in', mx: 100, my: 0 }, { p: 1, k: 'in', mx: -100, my: 0 }] : []);
      const a = s.players[0], b = s.players[1];
      if (a.x > b.x) crossed = true;
      minD = Math.min(minD, Math.hypot(a.x - b.x, a.y - b.y));
    }
    if (mode === 'on') { assert.ok(!crossed, 'не перетнулись'); assert.ok(minD >= 2 * PR - 10, `відстань ${minD}`); }
    else assert.ok(crossed, 'без bump проходять крізь');
  }
});

test('bump: можна штовхати партнера, що стоїть; є подія bump', () => {
  const s = blank({ bump: 'on' });
  at(s, 0, X, Y); at(s, 1, X + 1, Y);
  const x1 = s.players[1].x;
  const e = evs(s, 30, [{ p: 0, k: 'in', mx: 100, my: 0 }], 'bump');
  assert.ok(s.players[1].x > x1 + U, `штовхнули на ${s.players[1].x - x1}`);
  assert.ok(s.players[0].x < s.players[1].x - 2 * PR + 20);
  assert.ok(e.length >= 1 && e.length <= 8, `подій ${e.length}`);
});

test('bump: притиснутий до стіни не рухається, штовхач не пролазить', () => {
  const s = blank({ bump: 'on' });
  s.obj[id(s, X + 2, Y)] = O.WALL;
  at(s, 0, X, Y); at(s, 1, X + 1, Y);
  run(s, 40, [{ p: 0, k: 'in', mx: 100, my: 0 }]);
  const a = s.players[0], b = s.players[1];
  assert.ok(b.x <= (X + 2) * U - PR, 'не в стіні');
  assert.ok(b.x - a.x >= 2 * PR - 300, `перекриття ${2 * PR - (b.x - a.x)}`);
  assert.ok(a.x < b.x);
});

test('bump: одне в одному (однакова точка) — розводить детерміновано', () => {
  const s = blank({ bump: 'on' });
  at(s, 0, X, Y); at(s, 1, X, Y);
  run(s, 3);
  assert.ok(s.players[0].x < s.players[1].x);
  assert.ok(s.players[1].x - s.players[0].x >= 2 * PR - 5);
});

// ---------------- throw ----------------
test('throw: стос дерева летить на 3 клітинки й лягає; подія throw і land', () => {
  const s = blank({ throw: 'on' }, 1);
  hold(at(s, 0, X, Y), I.WOOD, 3);
  const e = evs(s, 1, [{ p: 0, k: 'throw' }], 'throw');
  assert.equal(e.length, 1);
  assert.equal(s.players[0].hold.k, I.NONE);
  assert.equal(s.flying.length, 1);
  const f = s.flying[0];
  assert.equal(f.tx, X + 3); assert.equal(f.ty, Y);
  let zMax = 0, land = [];
  for (let t = 0; t < 20 && s.flying.length; t++) { R.step(s, []); if (s.flying[0]) zMax = Math.max(zMax, s.flying[0].z); land.push(...s.ev.filter((x) => x.t === 'land')); }
  assert.ok(zMax > 300, 'дуга');
  assert.equal(land.length, 1);
  assert.equal(s.drop[id(s, X + 3, Y)], I.WOOD); assert.equal(s.dropN[id(s, X + 3, Y)], 3);
});

test('throw: рейки — по одній; інструмент — одна річ', () => {
  const s = blank({ throw: 'on' }, 1);
  const pl = at(s, 0, X, Y); hold(pl, I.RAIL, 3);
  run(s, 1, [{ p: 0, k: 'throw' }]);
  assert.equal(pl.hold.k, I.RAIL); assert.equal(pl.hold.n, 2);
  assert.equal(s.flying[0].n, 1);
  run(s, 20);
  assert.equal(s.drop[id(s, X + 3, Y)], I.RAIL); assert.equal(s.dropN[id(s, X + 3, Y)], 1);
});

test('throw: дерево на 3-й — падає на 2-гу; дерево впритул на всіх — не кидається', () => {
  const s = blank({ throw: 'on' }, 1);
  s.obj[id(s, X + 3, Y)] = O.TREE;
  const pl = at(s, 0, X, Y); hold(pl, I.IRON, 2);
  run(s, 1, [{ p: 0, k: 'throw' }]);
  assert.equal(s.flying[0].tx, X + 2);
  run(s, 20);
  assert.equal(s.drop[id(s, X + 2, Y)], I.IRON);
  // стіна в усі три
  const s2 = blank({ throw: 'on' }, 1);
  for (let d = 1; d <= 3; d++) s2.obj[id(s2, X + d, Y)] = O.TREE;
  const p2 = at(s2, 0, X, Y); hold(p2, I.AXE);
  run(s2, 1, [{ p: 0, k: 'throw' }]);
  assert.equal(p2.hold.k, I.AXE); assert.equal(s2.flying.length, 0);
});

test('throw: у воду — тоне з подією sink', () => {
  const s = blank({ throw: 'on' }, 1);
  s.ground[id(s, X + 3, Y)] = G.WATER;
  hold(at(s, 0, X, Y), I.PICK);
  run(s, 1, [{ p: 0, k: 'throw' }]);
  const e = evs(s, 20, [], 'sink');
  assert.equal(e.length, 1);
  let picks = 0; for (let i = 0; i < s.w * s.h; i++) if (s.drop[i] === I.PICK) picks++;
  const s0 = blank({}, 1); let picks0 = 0; for (let i = 0; i < s0.w * s0.h; i++) if (s0.drop[i] === I.PICK) picks0++;
  assert.equal(picks, picks0, 'кайло не лягло ніде');
});

test('throw: партнер з порожніми руками на клітинці приземлення — ловить; зі своїм стосом — добирає', () => {
  const s = blank({ throw: 'on' });
  hold(at(s, 0, X, Y), I.WOOD, 3);
  at(s, 1, X + 3, Y);
  run(s, 1, [{ p: 0, k: 'throw' }]);
  const e = evs(s, 20, [], 'catch');
  assert.equal(e.length, 1); assert.equal(e[0].p, 1);
  assert.equal(s.players[1].hold.k, I.WOOD); assert.equal(s.players[1].hold.n, 3);
  assert.equal(s.drop[id(s, X + 3, Y)], I.NONE);
  // з дровами в руках (carryMax−1) ловить одне, решта лягає (числа — від params, не від типових)
  const s2 = blank({ throw: 'on' });
  const cap = s2.params.carryMax | 0;
  hold(at(s2, 0, X, Y), I.WOOD, 3);
  hold(at(s2, 1, X + 3, Y), I.WOOD, cap - 1);
  run(s2, 1, [{ p: 0, k: 'throw' }]); run(s2, 20);
  assert.equal(s2.players[1].hold.n, cap);
  assert.equal(s2.drop[id(s2, X + 3, Y)], I.WOOD); assert.equal(s2.dropN[id(s2, X + 3, Y)], 2);
});

test('throw: дерево в склад «данком»; повне відро в паротяг — гасить', () => {
  const s = blank({ throw: 'on' }, 1);
  const w = R.wagonPos(s, 1), wx = Math.floor(w.x / U), wy = Math.floor(w.y / U);
  hold(at(s, 0, wx, wy - 3, 0, 1), I.WOOD, 3);
  run(s, 1, [{ p: 0, k: 'throw' }]); run(s, 20);
  assert.equal(s.train.wood, 3);
  const h = R.wagonPos(s, 0), hx = Math.floor(h.x / U), hy = Math.floor(h.y / U);
  s.train.heat = 900; s.train.ht = 999;
  hold(at(s, 0, hx, hy + 3, 0, -1), I.BUCKET_FULL);
  run(s, 1, [{ p: 0, k: 'throw' }]);
  const e = evs(s, 20, [], 'douse');
  assert.equal(e.length, 1); assert.equal(s.train.heat, 0);
});

// ---------------- pass ----------------
test('pass: use з річчю на партнера — у його руки; той самий стос — скільки влізе; інша річ — падає під ноги', () => {
  const s = blank();
  hold(at(s, 0, X, Y), I.WOOD, 2);
  at(s, 1, X + 1, Y);
  const e = evs(s, 1, [{ p: 0, k: 'use' }], 'pass');
  assert.equal(e.length, 1);
  assert.equal(s.players[0].hold.k, I.NONE);
  assert.deepEqual({ ...s.players[1].hold }, { k: I.WOOD, n: 2 });
  run(s, 5);
  const cap = s.params.carryMax | 0;   // у партнера 2: влізе cap−2, одне лишається в руках
  hold(s.players[0], I.WOOD, cap - 1);
  run(s, 1, [{ p: 0, k: 'use' }]);
  assert.equal(s.players[1].hold.n, cap); assert.equal(s.players[0].hold.n, 1);
  // в партнера сокира — як раніше: річ лягає на землю
  run(s, 5);
  hold(s.players[1], I.AXE);
  run(s, 1, [{ p: 0, k: 'use' }]);
  assert.equal(s.players[1].hold.k, I.AXE);
  assert.equal(s.drop[id(s, X + 1, Y)], I.WOOD);
});

test('pass вимкнено — річ падає під ноги партнерові (як було)', () => {
  const s = blank({ pass: 'off' });
  hold(at(s, 0, X, Y), I.IRON, 2);
  at(s, 1, X + 1, Y);
  run(s, 1, [{ p: 0, k: 'use' }]);
  assert.equal(s.players[1].hold.k, I.NONE);
  assert.equal(s.drop[id(s, X + 1, Y)], I.IRON);
});

// ---------------- stun ----------------
test('stun: сокира по партнеру з річчю — 1 с не рухається, річ падає; імунітет після', () => {
  const s = blank({ stun: 'on' });
  hold(at(s, 0, X, Y), I.AXE);
  hold(at(s, 1, X + 1, Y), I.WOOD, 2);
  const e = evs(s, 1, [{ p: 0, k: 'use' }, { p: 1, k: 'in', mx: 0, my: 100 }], 'stun');
  assert.equal(e.length, 1); assert.equal(e[0].p, 1); assert.equal(e[0].by, 0);
  assert.equal(s.players[1].hold.k, I.NONE);
  assert.equal(s.drop[id(s, X + 1, Y)], I.WOOD);
  const y1 = s.players[1].y;
  run(s, 10, [{ p: 1, k: 'in', mx: 0, my: 100 }]);
  assert.equal(s.players[1].y, y1, 'стоїть із зірочками');
  run(s, 1, [{ p: 1, k: 'use' }]);
  assert.equal(s.players[1].hold.k, I.NONE, 'не діє');
  // повторний удар під час імунітету — нічого
  for (let t = 0; t < 30; t++) R.step(s, [{ p: 0, k: 'use' }]);
  assert.ok(s.players[1].y > y1, 'потім пішов');
  let n = 0; for (let t = 0; t < 10; t++) { R.step(s, []); }
  at(s, 1, X + 1, Y);
  for (let t = 0; t < 10; t++) { R.step(s, [{ p: 0, k: 'use' }]); n += s.ev.filter((x) => x.t === 'stun').length; }
  assert.equal(n, 0, 'імунітет');
});

test('stun + pass: на натиск сокиру подає (порожні руки), затиснута кнопка — «бац»', () => {
  const s = blank({ stun: 'on' });
  hold(at(s, 0, X, Y), I.AXE);
  at(s, 1, X + 1, Y);
  run(s, 1, [{ p: 0, k: 'use' }]);
  assert.equal(s.players[1].hold.k, I.AXE, 'подав');
  // рубаємо дерево, тримаючи кнопку; дерево зникло, партнер став туди — випадковий «бац»
  const s2 = blank({ stun: 'on' });
  hold(at(s2, 0, X, Y), I.AXE);
  s2.obj[id(s2, X + 1, Y)] = O.TREE; s2.hp[id(s2, X + 1, Y)] = 99;
  const pl1 = at(s2, 1, X + 1, Y + 2);
  for (let t = 0; t < 6; t++) R.step(s2, [{ p: 0, k: 'use' }]);
  s2.obj[id(s2, X + 1, Y)] = O.NONE;
  at(s2, 1, X + 1, Y);
  let st = 0;
  for (let t = 0; t < 10; t++) { R.step(s2, [{ p: 0, k: 'use' }]); st += s2.ev.filter((x) => x.t === 'stun').length; }
  assert.equal(st, 1);
  assert.equal(pl1.hold.k, I.NONE, 'сокиру не подав — затиснуто');
  assert.ok(pl1.stun > 0 || pl1.imm > s2.tick);
  assert.equal(pl1.hold.k, I.NONE);
});

test('stun вимкнено: сокира по партнеру не б\'є', () => {
  const s = blank({ pass: 'off' });
  hold(at(s, 0, X, Y), I.AXE);
  hold(at(s, 1, X + 1, Y), I.WOOD, 2);
  run(s, 1, [{ p: 0, k: 'use' }]);
  assert.ok(!(s.players[1].stun > 0));
  assert.equal(s.players[1].hold.k, I.WOOD);
});

// ---------------- horn ----------------
test('horn: подія honk біля паротяга, не частіше 0,5 с', () => {
  const s = blank({}, 1);
  let n = 0;
  for (let t = 0; t < 20; t++) { R.step(s, [{ p: 0, k: 'horn' }]); n += s.ev.filter((e) => e.t === 'honk').length; }
  assert.equal(n, 2);
});

// ---------------- детермінізм і боти ----------------
function playBots(seed, params, sec) {
  const s = R.newGame(seed, { ...R.defaultParams(), ...params }, 2);
  const bots = [createBot(0), createBot(1)];
  const T = sec * R.TPS;
  while (s.status === 'run' && s.tick < T) {
    const cmds = [];
    for (const b of bots) {
      const v = b.think(s);
      cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 });
      if (v.use) cmds.push({ p: b.p, k: 'use' });
      if (s.tick % 97 === b.p * 13 && s.params.throw === 'on') cmds.push({ p: b.p, k: 'throw' });
    }
    R.step(s, cmds);
  }
  return s;
}

const ALL = { bump: 'on', throw: 'on', pass: 'on', stun: 'on' };

test('усе ввімкнено: два боти (і зрідка кидки) — однаковий hash двічі, clone не розходиться', () => {
  const a = playBots(9, ALL, 90), b = playBots(9, ALL, 90);
  assert.equal(R.hash(a), R.hash(b));
  const c = R.clone(a);
  for (let t = 0; t < 40; t++) { R.step(a, [{ p: 0, k: 'throw' }]); R.step(c, [{ p: 0, k: 'throw' }]); }
  assert.equal(R.hash(a), R.hash(c));
});

test('боти з усіма фішками (без кидків) доїжджають не гірше ніж удвічі ближче', () => {
  for (const seed of [7, 42]) {
    const s0 = playBots(seed, { bump: 'off', pass: 'off' }, 400);
    const s1 = playBots(seed, { ...ALL, throw: 'off' }, 400);
    const x0 = Math.floor(R.wagonPos(s0, 0).x / U), x1 = Math.floor(R.wagonPos(s1, 0).x / U);
    console.log(`сід ${seed}: без фішок x=${x0} (${s0.status}), з фішками x=${x1} (${s1.status})`);
    assert.ok(x1 * 2 >= x0, `сід ${seed}: ${x1} проти ${x0}`);
  }
});
