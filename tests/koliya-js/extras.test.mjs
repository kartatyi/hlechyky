// «Колія» — тести додатків K11 (гуси, проміжна станція). Стани будуємо руками поверх newGame.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import * as X from '../../web/games/koliya/extras.js';

const { U, G, O, I, TPS } = R;
const id = (s, x, y) => y * s.w + x;

function blank(params = {}, n = 1) {
  const p = { ...R.defaultParams(), forest: 0, rocks: 0, water: 0, ...params };
  const s = R.newGame(7, p, n);
  for (let i = 0; i < s.w * s.h; i++) {
    const y = (i / s.w) | 0;
    if (y > 0 && y < s.h - 1) { s.obj[i] = O.NONE; s.hp[i] = 0; s.drop[i] = I.NONE; s.dropN[i] = 0; if (s.ground[i] === G.WATER) s.ground[i] = G.GRASS; }
  }
  return s;
}
const at = (s, n, cx, cy, fx = 1, fy = 0) => { const pl = s.players[n]; pl.x = cx * U + U / 2; pl.y = cy * U + U / 2; pl.fx = fx; pl.fy = fy; return pl; };
const toolsOnFloor = (s) => { let c = 0; for (let i = 0; i < s.drop.length; i++) if (s.drop[i] >= I.AXE) c++; return c; };
const heldByGeese = (s) => s.geese.filter((g) => g.hold).length;

test('вимкнено: стан той самий, що й без додатків (rng і світ не чіпаємо)', () => {
  const s = R.newGame(42, R.defaultParams(), 2);
  assert.deepEqual(s.geese, []);
  assert.equal(s.mid, null);
  assert.equal(s.w, s.params.stripLen);
  const h0 = R.hash(s);
  for (let t = 0; t < 2000; t++) R.step(s, []);
  assert.equal(s.geese.length, 0);
  // інший прогін без змін — той самий hash
  const s2 = R.newGame(42, R.defaultParams(), 2);
  assert.equal(R.hash(s2), h0);
});

test('гуси: детермінізм — однакові команди дають однакові гуси й hash, clone не розходиться', () => {
  const run = (cut) => {
    let s = R.newGame(777, { ...R.defaultParams(), geese: 'on', geeseSec: 10, startSec: 90 }, 2);
    let r = 5;
    const rr = () => (r = (Math.imul(r, 1103515245) + 12345) >>> 0) >>> 8;
    for (let t = 0; t < 1500; t++) {
      if (t === cut) s = R.clone(s);
      const cmds = [];
      for (let p = 0; p < 2; p++) {
        cmds.push({ p, k: 'in', mx: (rr() % 201) - 100, my: (rr() % 201) - 100 });
        if (rr() % 4 === 0) cmds.push({ p, k: 'use' });
      }
      R.step(s, cmds);
    }
    return JSON.stringify([R.hash(s), s.geese, s.geeseId]);
  };
  const a = run(-1);
  assert.equal(a, run(-1));
  assert.equal(a, run(700));
  assert.ok(JSON.parse(a)[2] > 0, 'гуси з\'являлись');
});

test('гуска йде до сокири, хапає, несе до краю й губить попереду на досяжній клітинці', () => {
  const s = blank({ geese: 'on', geeseSec: 10, geeseMax: 1, startSec: 90 });
  for (let i = 0; i < s.drop.length; i++) if (s.drop[i]) { s.drop[i] = 0; s.dropN[i] = 0; }
  // стіна каміння, що відрізає далеку частину: туди річ падати не має
  for (let y = 1; y < s.h - 1; y++) s.obj[id(s, 30, y)] = O.ROCK;
  const ax = id(s, 12, 4);
  s.drop[ax] = I.AXE; s.dropN[ax] = 1;
  at(s, 0, 3, 10);
  s.geeseT = 1;
  let grabbed = false, lost = null;
  for (let t = 0; t < 40 * TPS && !lost; t++) {
    R.step(s, []);
    if (s.geese.some((g) => g.hold === I.AXE)) { grabbed = true; assert.equal(s.drop[ax], I.NONE); }
    const e = s.ev.find((e) => e.t === 'goose_lost');
    if (e) lost = e;
  }
  assert.ok(grabbed, 'гуска схопила сокиру');
  assert.ok(lost, 'гуска дійшла до краю');
  assert.equal(s.drop[id(s, lost.x, lost.y)], I.AXE);
  assert.ok(lost.x < 30, 'не за кам\'яною стіною');
  const hx = Math.floor(R.wagonPos(s, 0).x / U);
  assert.ok(lost.x >= hx + 3, 'попереду паротяга');
  assert.ok(!s.path.includes(id(s, lost.x, lost.y)));
});

test('use поруч лякає гуску: вона кидає річ і тікає порожня; use з\'їдено', () => {
  const s = blank({ geese: 'on' });
  s.geese.push({ id: 1, x: 10 * U + U / 2, y: 6 * U + U / 2, hold: I.PICK, mode: 'run', t: 0, tx: 10, ty: 6, side: -1, sp: 100 });
  at(s, 0, 9, 6, 1, 0);
  s.players[0].hold.k = I.WOOD; s.players[0].hold.n = 1;
  R.step(s, [{ p: 0, k: 'use' }]);
  const g = s.geese[0];
  assert.equal(g.hold, I.NONE);
  assert.equal(g.mode, 'flee');
  assert.ok(s.ev.some((e) => e.t === 'scare'));
  assert.equal(toolsOnFloor(s), 1, 'кайло на землі');
  assert.equal(s.players[0].hold.k, I.WOOD, 'дерево не поклали — use з\'їла гуска');
  // далеко — не лякається, use іде в правила
  const s2 = blank({ geese: 'on' });
  s2.geese.push({ id: 1, x: 20 * U, y: 6 * U, hold: I.PICK, mode: 'run', t: 0, tx: 20, ty: 6, side: -1, sp: 100 });
  at(s2, 0, 9, 6, 1, 0);
  assert.equal(X.extrasUse(s2, 0, R.facingCell(s2.players[0])), false);
});

test('гра не глухне: 30 хв з гусьми — інструменти не зникають і лежать досяжно', () => {
  const s = R.newGame(31337, { ...R.defaultParams(), geese: 'on', geeseSec: 10, startSec: 3000, heatSec: 240 }, 1);
  const total = () => toolsOnFloor(s) + heldByGeese(s) + s.players.filter((p) => p.hold.k >= I.AXE).length;
  const t0 = total();
  assert.equal(t0, 3);
  let lost = 0;
  for (let t = 0; t < 30 * 60 * TPS; t++) {
    R.step(s, []);
    if (s.ev.some((e) => e.t === 'goose_lost')) lost++;
    assert.equal(total(), t0, 'тик ' + t);
  }
  assert.ok(lost > 3, 'гуси крали: ' + lost);
  // кожен інструмент на землі досяжний пішки від гравця
  const seen = new Uint8Array(s.w * s.h), q = [];
  const p = s.players[0], st = Math.floor(p.y / U) * s.w + Math.floor(p.x / U);
  seen[st] = 1; q.push(st);
  for (let k = 0; k < q.length; k++) {
    const i = q[k];
    for (const j of [i - 1, i + 1, i - s.w, i + s.w]) {
      if (j < 0 || j >= seen.length || seen[j]) continue;
      if (s.obj[j] !== O.NONE || s.ground[j] === G.WATER) continue;
      seen[j] = 1; q.push(j);
    }
  }
  for (let i = 0; i < s.drop.length; i++) if (s.drop[i] >= I.AXE) assert.ok(seen[i], 'інструмент досяжний ' + i);
});

test('проміжна станція: смуга вдвічі, станція перша — SAND, паротяг чекає, покращення діє', () => {
  const p = { ...R.defaultParams(), stationMode: 'upgrade', startSec: 0, speedStart: 1.5, heatSec: 240 };
  const s = R.newGame(9, p, 1);
  const m = s.mid;
  assert.ok(m);
  assert.equal(m.len1, p.stripLen);
  assert.ok(s.w > p.stripLen);
  assert.equal(s.ground[id(s, m.x0, m.y0)], G.SAND);
  assert.equal(m.signs.length, 3);
  let st = 0; for (let i = 0; i < s.ground.length; i++) if (s.ground[i] === G.STATION) st++;
  assert.equal(st, 9, 'лише кінцева станція');
  // кладемо колію до проміжної станції рядком mid
  const mid = (s.path[0] / s.w) | 0;
  for (let x = (s.path[s.path.length - 1] % s.w) + 1; x <= m.x1 + 2; x++) {
    const i = id(s, x, mid); s.obj[i] = O.NONE; if (s.ground[i] === G.WATER) s.ground[i] = G.BRIDGE; s.path.push(i);
  }
  at(s, 0, m.signs[0].x, m.signs[0].y + 1, 0, -1);
  let waited = 0;
  for (let t = 0; t < 400 * TPS && s.status === 'run'; t++) {
    R.step(s, []);
    if (m.wait) { waited++; s.train.heat = 0; s.train.ht = 0; }
    if (waited > 60) break;
  }
  assert.equal(s.status, 'run');
  assert.equal(m.wait, 1);
  const pos = s.train.pos;
  for (let t = 0; t < 40; t++) R.step(s, []);
  assert.equal(s.train.pos, pos, 'стоїть');
  assert.equal(s.train.speed, 0);
  const k = m.signs[0].k, before = { ...s.params };
  R.step(s, [{ p: 0, k: 'use' }]);
  assert.equal(m.pick, k);
  assert.ok(s.ev.some((e) => e.t === 'upgrade'));
  if (k === 'tank') assert.equal(s.params.heatSec, before.heatSec * 2);
  if (k === 'craft') assert.equal(s.params.craftSec, before.craftSec / 2);
  if (k === 'store') assert.ok(s.params.storeMax > before.storeMax);
  if (k === 'carry') assert.equal(s.params.carryMax, before.carryMax + 1);
  for (let t = 0; t < 40; t++) R.step(s, []);
  assert.ok(s.train.pos > pos, 'рушив');
});

test('покращення: кожне змінює своє', () => {
  const s = blank();
  const P = s.params, b = { ...P };
  s.train.ht = 100;
  X.applyUpgrade(s, 'tank'); assert.equal(P.heatSec, b.heatSec * 2); assert.equal(s.train.ht, 200);
  X.applyUpgrade(s, 'craft'); assert.equal(P.craftSec, Math.max(0.5, b.craftSec / 2));
  X.applyUpgrade(s, 'store'); assert.ok(P.storeMax >= b.storeMax + 3);
  X.applyUpgrade(s, 'carry'); assert.equal(P.carryMax, b.carryMax + 1);
});

test('isqrt', () => {
  for (const n of [0, 1, 2, 3, 4, 15, 16, 17, 999999, 1e6, 160000000000]) assert.equal(X.isqrt(n), Math.floor(Math.sqrt(n)));
});
