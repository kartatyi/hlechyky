// «Колія» K33 — нічого не губиться, ніхто не застрягає. node --test tests/koliya-js/lost.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import * as L from '../../web/games/koliya/lost.js';
import { createBot } from '../../web/games/koliya/bot.js';

const { U, G, O, I, TPS } = R;
const id = (s, x, y) => y * s.w + x;

/** Порожня смуга (як у coop/extras тестах), паротяг стоїть довго; інструменти генератора лишаються */
function blank(params = {}, n = 1, keepTools = true) {
  const p = { ...R.defaultParams(), forest: 0, rocks: 0, water: 0, startSec: 90, ...params };
  const s = R.newGame(7, p, n);
  for (let i = 0; i < s.w * s.h; i++) {
    const y = (i / s.w) | 0;
    if (y > 0 && y < s.h - 1) {
      s.obj[i] = O.NONE; s.hp[i] = 0;
      if (!(keepTools && s.drop[i] >= I.AXE)) { s.drop[i] = I.NONE; s.dropN[i] = 0; }
      if (s.ground[i] === G.WATER) s.ground[i] = G.GRASS;
    }
  }
  return s;
}
const at = (s, n, cx, cy, fx = 1, fy = 0) => { const pl = s.players[n]; pl.x = cx * U + U / 2; pl.y = cy * U + U / 2; pl.fx = fx; pl.fy = fy; return pl; };
const run = (s, n, cmds = []) => { const out = []; for (let i = 0; i < n; i++) { R.step(s, i === 0 ? cmds : []); out.push(...s.ev); } return out; };
const findTool = (s, k) => { for (let i = 0; i < s.drop.length; i++) if (s.drop[i] === k) return i; return -1; };
const RESPAWN_MAX = 10 * TPS + 2 * 10;   // 10 с + дві перевірки

// ================= 1. інструменти =================
test('K33-1: на старті рахуємо інструменти; типовий стан має s.keep', () => {
  const s = R.newGame(7, R.defaultParams(), 1);
  assert.deepEqual(s.keep.start, [1, 1, 1]);
  assert.deepEqual(L.toolCount(s), [1, 1, 1]);
});

test('K33-1: відро кинули у воду — за ~10 с з\'являється біля кінця колії (подія respawn), не більше одного', () => {
  const s = blank({ throw: 'on' });
  const b = findTool(s, I.BUCKET); s.drop[b] = I.NONE; s.dropN[b] = 0;
  const pl = at(s, 0, 30, 3); pl.hold = { k: I.BUCKET_FULL, n: 1 };
  for (let x = 31; x <= 34; x++) s.ground[id(s, x, 3)] = G.WATER;
  const ev = run(s, 20, [{ p: 0, k: 'throw' }]);
  assert.ok(ev.some((e) => e.t === 'sink'), 'потонуло');
  assert.deepEqual(L.toolCount(s), [1, 1, 0]);
  const ev2 = run(s, RESPAWN_MAX);
  const r = ev2.filter((e) => e.t === 'respawn');
  assert.equal(r.length, 1);
  assert.equal(r[0].k, I.BUCKET);
  assert.equal(s.drop[id(s, r[0].x, r[0].y)], I.BUCKET);
  assert.deepEqual(L.toolCount(s), [1, 1, 1]);
  // біля кінця колії
  const end = s.path[s.path.length - 1], ex = end % s.w, ey = (end / s.w) | 0;
  assert.ok(Math.abs(r[0].x - ex) + Math.abs(r[0].y - ey) <= 3, 'біля кінця колії');
  assert.equal(run(s, 600).filter((e) => e.t === 'respawn').length, 0, 'більше не з\'являється');
});

test('K33-1: putDrop не знайшов місця (вихід гравця на воді) — сокира повертається', () => {
  const s = blank({}, 2);
  const a = findTool(s, I.AXE); s.drop[a] = I.NONE; s.dropN[a] = 0;
  // навколо гравця 7×7 вода (поставили руками просто на воду)
  for (let y = 2; y <= 8; y++) for (let x = 40; x <= 46; x++) s.ground[id(s, x, y)] = G.WATER;
  const pl = at(s, 1, 43, 5); pl.hold = { k: I.AXE, n: 1 };
  run(s, 1, [{ p: 1, k: 'leave' }]);
  run(s, RESPAWN_MAX);
  assert.deepEqual(L.toolCount(s), [1, 1, 1]);
});

test('K33-1: гуска впустила нікуди (dropNear і lostCell порожні) — кайло повертається', () => {
  const s = blank({ geese: 'on', startSec: 900 });
  const k = findTool(s, I.PICK); s.drop[k] = I.NONE; s.dropN[k] = 0;
  s.geese.push({ id: 1, x: 30 * U + U / 2, y: 5 * U + U / 2, hold: I.PICK, mode: 'run', t: 0, tx: 30, ty: 5, side: -1, sp: 50 });
  // усе довкола — скелі, гравець замурований: падати нема куди, досяжного нема
  for (let i = 0; i < s.w * s.h; i++) if (s.obj[i] === O.NONE && s.ground[i] !== G.STATION) s.obj[i] = O.ROCK;
  const pl = at(s, 0, 31, 5); s.obj[id(s, 31, 5)] = O.NONE;
  s.drop[id(s, 31, 5)] = I.WOOD; s.dropN[id(s, 31, 5)] = 1;   // і під ним уже щось лежить
  run(s, 1, [{ p: 0, k: 'use' }]);
  assert.equal(s.geese[0].hold, I.NONE, 'гуска кинула');
  assert.equal(L.toolCount(s)[1], 0, 'кайло пропало');
  for (let i = 0; i < s.w * s.h; i++) if (s.obj[i] === O.ROCK) s.obj[i] = O.NONE;   // відкопали — є куди класти
  run(s, RESPAWN_MAX);
  assert.equal(L.toolCount(s)[1], 1);
  void pl;
});

test('K33-1: інструмент за скелями (недосяжний) — переноситься, а не множиться', () => {
  const s = blank();
  const a = findTool(s, I.AXE); s.drop[a] = I.NONE; s.dropN[a] = 0;
  const box = id(s, 60, 3);
  s.drop[box] = I.AXE; s.dropN[box] = 1;
  for (const [dx, dy] of [[-1, 0], [1, 0], [0, -1], [0, 1], [-1, -1], [1, -1], [-1, 1], [1, 1]]) s.obj[id(s, 60 + dx, 3 + dy)] = O.ROCK;
  // на сусідах по стороні — скелі; сусід «по стороні» має бути досяжним, тож обгородимо й другий ряд
  for (let y = 1; y <= 5; y++) for (let x = 58; x <= 62; x++) if (Math.max(Math.abs(x - 60), Math.abs(y - 3)) === 2) s.obj[id(s, x, y)] = O.ROCK;
  const ev = run(s, RESPAWN_MAX);
  assert.equal(ev.filter((e) => e.t === 'respawn').length, 1);
  assert.equal(s.drop[box], I.NONE, 'з-за скель забрали');
  assert.deepEqual(L.toolCount(s), [1, 1, 1], 'не більше, ніж на старті');
});

test('K33-1: відро під паротягом, що стоїть, — переноситься', () => {
  const s = blank({ startSec: 900 });
  const b = findTool(s, I.BUCKET); s.drop[b] = I.NONE; s.dropN[b] = 0;
  const c = s.path[Math.floor(s.train.pos / U)];   // клітинка під паротягом
  s.drop[c] = I.BUCKET; s.dropN[c] = 1;
  run(s, RESPAWN_MAX);
  assert.equal(s.drop[c], I.NONE, 'з-під вагона забрали');
  assert.deepEqual(L.toolCount(s), [1, 1, 1]);
});

test('K33-1: 15 хв ботів з гусьми й кидком — інструментів ніколи не більше, ніж на старті, і жоден тип не зникає надовго', () => {
  for (const seed of [3, 8]) {
    const s = R.newGame(seed, { ...R.defaultParams(), geese: 'on', geeseSec: 15, throw: 'on' }, 2);
    const bots = [createBot(0), createBot(1)];
    const zero = [0, 0, 0];
    for (let t = 0; t < 15 * 60 * TPS && s.status === 'run'; t++) {
      const cmds = [];
      for (const b of bots) { const v = b.think(s); cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 }); if (v.use) cmds.push({ p: b.p, k: 'use' }); }
      if (t % 97 === 0) cmds.push({ p: 1, k: 'throw' });   // кидаємо що в руках — куди попаде
      R.step(s, cmds);
      const c = L.toolCount(s);
      for (let j = 0; j < 3; j++) {
        assert.ok(c[j] <= s.keep.start[j], `сід ${seed} тик ${t}: ${c}`);
        zero[j] = c[j] ? 0 : zero[j] + 1;
        assert.ok(zero[j] <= RESPAWN_MAX + 20, `сід ${seed}: тип ${j} зник надовго`);
      }
    }
  }
});

// ================= детермінізм =================
test('K33: детермінізм — однакові команди дають однаковий hash; s.keep іде в hash', () => {
  const mk = () => { const s = blank({ throw: 'on' }); const pl = at(s, 0, 30, 3); pl.hold = { k: I.BUCKET_FULL, n: 1 }; for (let x = 31; x <= 34; x++) s.ground[id(s, x, 3)] = G.WATER; return s; };
  const a = mk(), b = mk();
  for (let t = 0; t < 400; t++) { const c = t === 0 ? [{ p: 0, k: 'throw' }] : []; R.step(a, c); R.step(b, c); assert.equal(R.hash(a), R.hash(b)); }
  const h = R.hash(a);
  a.keep.due[0] = 12345;
  assert.notEqual(R.hash(a), h, 'keep у hash');
});
