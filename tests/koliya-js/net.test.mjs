// «Колія» — тести мережевої частини без мережі: перекочування (rollback) дає той самий світ, що й прямий прогін журналу.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { copyInto, prep, toCmd, createDriver } from '../../web/games/koliya/net.js';

const PARAMS = { ...R.defaultParams(), startSec: 90, overheat: 'stop' };

/** Журнал на n тиків: двоє водіїв грають в одному світі; команди — як їх запише сервер ('in' лише на зміну). */
function makeLog(n, seed = 7) {
  const s = R.newGame(seed, PARAMS, 2);
  const d = [createDriver(), createDriver()];
  const last = [{}, {}], log = [];
  for (let t = 0; t < n; t++) {
    const cmds = [];
    for (let p = 0; p < 2; p++) {
      const v = d[p].think(s, p);
      if (v.mx !== last[p].mx || v.my !== last[p].my) { last[p] = v; cmds.push({ p, k: 'in', mx: v.mx, my: v.my }); }
      if (v.use) { const pl = s.players[p]; cmds.push({ p, k: 'use', x: pl.x, y: pl.y, fx: pl.fx, fy: pl.fy, h: 0 }); }
    }
    for (const c of cmds) log.push({ t, c });
    R.step(s, cmds);
  }
  return { log, hash: R.hash(s), s };
}

function straight(log, n, seed = 7) {
  const s = R.newGame(seed, PARAMS, 2);
  for (let t = 0; t < n; t++) {
    const cmds = log.filter((x) => x.t === t).map((x) => x.c);
    prep(s, cmds);
    R.step(s, cmds);
  }
  return s;
}

test('прямий прогін журналу повторює гру', () => {
  const { log, hash } = makeLog(600);
  assert.ok(log.length > 50);
  assert.equal(R.hash(straight(log, 600)), hash);
});

test('перекочування: чужі команди з запізненням, після перерахунку світ той самий', () => {
  const N = 600, DELAY = 9;
  const { log, hash, s: ref } = makeLog(N);
  // браузер гравця 0: свої команди знає одразу, чужі (гравця 1) — через DELAY тиків; C — остаточний, P — передбачений
  const C = R.newGame(7, PARAMS, 2), P = R.clone(C);
  const arrays = [P.ground, P.obj, P.drop];
  let rebuilds = 0;
  for (let now = 0; now < N + DELAY; now++) {
    const known = (t) => log.filter((x) => x.t === t && (x.c.p === 0 || x.t + DELAY <= now)).map((x) => x.c);
    // остаточне — лише тики, де все вже прийшло
    while (C.tick + DELAY <= now && C.tick < N) { const c = known(C.tick); prep(C, c); R.step(C, c); }
    // щойно прийшли чужі команди в уже пораховані тики — перерахунок від C
    if (log.some((x) => x.c.p === 1 && x.t + DELAY === now && x.t < P.tick)) {
      rebuilds++;
      const back = P.tick;
      copyInto(P, C);
      while (P.tick < back) { const c = known(P.tick); prep(P, c); R.step(P, c); }
    }
    if (P.tick < Math.min(now + 1, N)) { const c = known(P.tick); prep(P, c); R.step(P, c); }
  }
  assert.ok(rebuilds > 5, 'мало перерахунків — тест нічого не перевірив');
  assert.equal(R.hash(C), hash);
  assert.equal(R.hash(P), hash);
  assert.equal(R.hash(P), R.hash(ref));
  // масиви ті самі об'єкти — рендер не перебудовує світ
  assert.equal(P.ground, arrays[0]); assert.equal(P.obj, arrays[1]); assert.equal(P.drop, arrays[2]);
});

test('затиснутий use з журналу (h=1) ставить ut, як локальне затискання', () => {
  const s = R.newGame(3, PARAMS, 1);
  prep(s, [{ p: 0, k: 'use', h: 1 }]);
  assert.equal(s.players[0].ut, s.tick);
  const t = R.newGame(3, PARAMS, 1);
  prep(t, [{ p: 0, k: 'use', h: 0 }]);
  assert.equal(t.players[0].ut, 0);
});

test('запис кадру → команда rules', () => {
  const c = [5, 10, 1, 2, 1500, 2500, 0, -1, 1, 7, 6, 11, 0, 1, -100, 40, 0, 0, 0, 8, 7, 12, 2, 3, 0, 0, 0, 0, 0, 0];
  assert.deepEqual(toCmd(c, 0), { p: 1, k: 'use', x: 1500, y: 2500, fx: 0, fy: -1, h: 1 });
  assert.deepEqual(toCmd(c, 10), { p: 0, k: 'in', mx: -100, my: 40 });
  assert.deepEqual(toCmd(c, 20), { p: 2, k: 'join' });
});
