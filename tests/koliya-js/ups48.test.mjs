// «Колія» — тести K48: покращення 🚿 вагон-цистерна, 🧲 магніт, 🐢 гальма (extras.js, стан s.ups).
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import * as X from '../../web/games/koliya/extras.js';

const { U, G, O, I, TPS } = R;
const id = (s, x, y) => y * s.w + x;

/** Порожня смуга (як у extras.test), колія продовжена рядком паротяга на ext клітинок, щоб не зійти */
function blank(params = {}, n = 1, ext = 30) {
  const p = { ...R.defaultParams(), forest: 0, rocks: 0, water: 0, winter: 'off', soloSpeed: 1, heatSec: 240, ...params };
  const s = R.newGame(7, p, n);
  for (let i = 0; i < s.w * s.h; i++) {
    const y = (i / s.w) | 0;
    if (y > 0 && y < s.h - 1) { s.obj[i] = O.NONE; s.hp[i] = 0; s.drop[i] = I.NONE; s.dropN[i] = 0; if (s.ground[i] === G.WATER) s.ground[i] = G.GRASS; }
  }
  const last = s.path[s.path.length - 1], row = (last / s.w) | 0;
  for (let x = (last % s.w) + 1, k = 0; k < ext && x < s.w - 1; x++, k++) { const i = id(s, x, row); s.ground[i] = G.GRASS; s.path.push(i); }
  return s;
}
const cellOf = (s, k) => { const w = R.wagonPos(s, k); return { x: Math.floor(w.x / U), y: Math.floor(w.y / U) }; };
/** Гравець n під вагоном k, обличчям до нього */
const under = (s, n, k) => { const c = cellOf(s, k), pl = s.players[n]; pl.x = c.x * U + U / 2; pl.y = (c.y + 1) * U + U / 2; pl.fx = 0; pl.fy = -1; pl.cd = 0; return pl; };

test('K48: тексти для таблички й тосту — у форматі UPGRADES', () => {
  for (const k of ['cist', 'magnet', 'brake']) {
    const u = X.UPGRADES[k];
    assert.ok(u && u.icon && u.short && u.label, k);
  }
  assert.equal(X.UPGRADES.cist.icon, '🚿');
  assert.equal(X.UPGRADES.magnet.icon, '🧲');
  assert.equal(X.UPGRADES.brake.icon, '🐢');
});

test('K48: без вибору s.ups нема (типовий hash не змінюється); проміжна станція пропонує й нові', () => {
  const s = R.newGame(42, R.defaultParams(), 1);
  assert.equal(s.ups, undefined);
  for (let t = 0; t < 200; t++) R.step(s, []);
  assert.equal(s.ups, undefined);
  const seen = new Set();
  for (let seed = 1; seed <= 12; seed++) {
    const m = R.newGame(seed, { ...R.defaultParams(), stationMode: 'upgrade' }, 1).mid;
    assert.equal(m.signs.length, 3);
    assert.equal(new Set(m.signs.map((g) => g.k)).size, 3, 'без повторів');
    for (const g of m.signs) { assert.ok(X.UPGRADES[g.k]); seen.add(g.k); }
  }
  assert.ok(['cist', 'magnet', 'brake'].some((k) => seen.has(k)), 'нові трапляються: ' + [...seen]);
});

test('K48 🚿: цистерна поливає теплий котел раз на CIST_SEC, поки є вода; відро в склад доливає', () => {
  const s = blank({ startSec: 0 });
  R.step(s, []);
  X.applyUpgrade(s, 'cist');
  const u = s.ups, t = s.train;
  assert.equal(u.cw, X.CIST_MAX);
  assert.equal(u.ct, X.CIST_SEC * TPS);
  // таймер ще йде — не поливає
  t.ht = 10000; t.heat = 900;
  R.step(s, []);
  assert.ok(t.heat >= 900 && u.cw === X.CIST_MAX);
  // таймер вийшов — полив
  u.ct = 1;
  R.step(s, []);
  assert.ok(s.ev.some((e) => e.t === 'douse' && e.u === 'cist'));
  assert.ok(t.heat < X.CIST_HEAT, 'котел холодний: ' + t.heat);
  assert.equal(u.cw, X.CIST_MAX - 1);
  assert.equal(u.ct, X.CIST_SEC * TPS);
  // холодний котел — воду не ллє
  u.ct = 1; R.step(s, []);
  assert.equal(u.cw, X.CIST_MAX - 1);
  // порожня цистерна — не поливає
  u.cw = 0; u.ct = 1; t.ht = 10000; t.heat = 900;
  R.step(s, []);
  assert.ok(t.heat >= 900 && !s.ev.some((e) => e.u === 'cist'));
  t.ht = 0; t.heat = 0;   // інакше долита вода одразу ж поллється на гарячий котел
  // повне відро на склад (вагон 1) — +CIST_BUCKET, відро порожнє; котел (вагон 0) відро далі гасить як було
  const pl = under(s, 0, 1);
  pl.hold.k = I.BUCKET_FULL; pl.hold.n = 1;
  R.step(s, [{ p: 0, k: 'use' }]);
  assert.equal(u.cw, X.CIST_BUCKET);
  assert.equal(pl.hold.k, I.BUCKET);
  assert.ok(s.ev.some((e) => e.t === 'drop' && e.u === 'cist' && e.p === 0));
  // повна цистерна — відро в склад не з'їдається
  u.cw = X.CIST_MAX; pl.hold.k = I.BUCKET_FULL; under(s, 0, 1);
  assert.equal(X.extrasUse(s, 0, cellOf(s, 1)), false);
  // друга цистерна — місткість росте
  X.applyUpgrade(s, 'cist');
  assert.equal(u.cist, 2); assert.equal(u.cw, 2 * X.CIST_MAX);
});

test('K48 🧲: магніт тягне дрова/залізо з радіусу 2 у склад, далі — ні, склад не переповнює', () => {
  const s = blank({ startSec: 9000, storeMax: 10 });
  X.applyUpgrade(s, 'magnet');
  const c = cellOf(s, 0);
  const a = id(s, c.x - 1, c.y + 2), b = id(s, c.x, c.y - 2), far = id(s, c.x + 3, c.y + 1);
  s.drop[a] = I.WOOD; s.dropN[a] = 4;
  s.drop[b] = I.IRON; s.dropN[b] = 3;
  s.drop[far] = I.WOOD; s.dropN[far] = 5;
  s.train.wood = 8; s.train.rails = s.params.railsMax | 0;   // майстерня стоїть — не з'їдає дрова/залізо
  for (let t = 0; t < X.MAG_TICKS * 4; t++) R.step(s, []);
  assert.equal(s.train.wood, 10, 'до стелі складу');
  assert.equal(s.drop[a], I.WOOD); assert.equal(s.dropN[a], 2, 'решта лишилась на землі');
  assert.equal(s.train.iron, 3); assert.equal(s.drop[b], I.NONE);
  assert.equal(s.dropN[far], 5, 'за радіусом не чіпає');
});

test('K48 🐢: гальма — дія на паротяг, 10 с удвічі повільніше; раз за станцію; на станції — знову', () => {
  const s = blank({ startSec: 0, speedStart: 1, speedGrowth: 0, speedCurve: 'linear' }, 1, 40);
  R.step(s, []);
  X.applyUpgrade(s, 'brake');
  assert.equal(s.ups.bn, 1);
  const a = R.clone(s);
  under(s, 0, 0); under(a, 0, 0);
  const p0 = s.train.pos;
  R.step(s, [{ p: 0, k: 'use' }]); R.step(a, []);
  assert.ok(s.ev.some((e) => e.t === 'whistle' && e.u === 'brake' && e.p === 0));
  assert.equal(s.ups.bn, 0);
  for (let t = 1; t < X.BRAKE_SEC * TPS; t++) { R.step(s, []); R.step(a, []); }
  assert.equal(s.ups.bt, 0);
  const dB = s.train.pos - p0, dA = a.train.pos - p0;
  assert.ok(dA > 0 && Math.abs(dB * 2 - dA) <= 2 * U / 10 + 100, `удвічі повільніше: ${dB} проти ${dA}`);
  // ще раз — зарядів нема, use на паротяг нічого не робить
  under(s, 0, 0);
  assert.equal(X.extrasUse(s, 0, cellOf(s, 0)), false);
  // нова станція (K19) — гальма знову заряджені
  X.upsStation(s);
  assert.equal(s.ups.bn, 1);
  // повне відро на паротяг — гасить, а не гальмує
  const pl = under(s, 0, 0); pl.hold.k = I.BUCKET_FULL; pl.hold.n = 1;
  R.step(s, [{ p: 0, k: 'use' }]);
  assert.equal(s.ups.bn, 1); assert.equal(pl.hold.k, I.BUCKET);
});

test('K48: детермінізм — усі три покращення, випадкові команди, clone посередині — той самий hash; hash бачить s.ups', () => {
  const run = (cut) => {
    let s = R.newGame(4848, { ...R.defaultParams(), startSec: 2, heatSec: 30 }, 2);
    for (const k of ['cist', 'magnet', 'brake']) X.applyUpgrade(s, k);
    let r = 11;
    const rr = () => (r = (Math.imul(r, 1103515245) + 12345) >>> 0) >>> 8;
    for (let t = 0; t < 1500 && s.status === 'run'; t++) {
      if (t === cut) s = R.clone(s);
      const cmds = [];
      for (let p = 0; p < 2; p++) {
        cmds.push({ p, k: 'in', mx: (rr() % 201) - 100, my: (rr() % 201) - 100 });
        if (rr() % 3 === 0) cmds.push({ p, k: 'use' });
      }
      if (t % 300 === 0) X.upsStation(s);
      R.step(s, cmds);
    }
    return JSON.stringify([R.hash(s), s.ups, s.train, s.tick]);
  };
  const a = run(-1);
  assert.equal(a, run(-1));
  assert.equal(a, run(700));
  const s = R.newGame(4848, R.defaultParams(), 1);
  X.applyUpgrade(s, 'cist');
  const c = R.clone(s);
  assert.equal(R.hash(c), R.hash(s));
  c.ups.cw--;
  assert.notEqual(R.hash(c), R.hash(s));
  // усе в s.ups — цілі
  for (const v of Object.values(JSON.parse(a)[1])) assert.ok(Number.isInteger(v));
});
