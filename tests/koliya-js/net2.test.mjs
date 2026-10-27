// «Колія» K6 — мережа 2: згладжування поз після перерахунку, емуляція затримки, hash додатків (гуси, s.mid, s.flying).
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { copyInto, prep, createDriver, createSmoother, createLag } from '../../web/games/koliya/net.js';

const PARAMS = { ...R.defaultParams(), startSec: 90, overheat: 'stop' };

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
  return { log, hash: R.hash(s) };
}

/** Браузер гравця 0 з запізненням чужих команд на DELAY тиків; малює раз на тик через smoother. */
function play(on, N = 900, DELAY = 4) {
  const { log, hash } = makeLog(N);
  const C = R.newGame(7, PARAMS, 2), P = R.clone(C), sm = createSmoother(on);
  for (let now = 0; now < N + DELAY; now++) {
    const known = (t) => log.filter((x) => x.t === t && (x.c.p === 0 || x.t + DELAY <= now)).map((x) => x.c);
    while (C.tick + DELAY <= now && C.tick < N) { const c = known(C.tick); prep(C, c); R.step(C, c); }
    if (log.some((x) => x.c.p === 1 && x.t + DELAY === now && x.t < P.tick)) {
      const back = P.tick;
      sm.before(P);
      copyInto(P, C);
      while (P.tick < back) { const c = known(P.tick); prep(P, c); R.step(P, c); }
      sm.after(P);
    }
    if (P.tick < Math.min(now + 1, N)) { const c = known(P.tick); prep(P, c); R.step(P, c); }
    const h0 = R.hash(P);
    const undo = sm.apply(P);
    undo();
    assert.equal(R.hash(P), h0, 'малювання не змінює світ');
  }
  assert.equal(R.hash(P), hash);
  return sm.st;
}

test('згладжування: після перерахунку видима поза не стрибає, світ той самий', () => {
  const raw = play(false), sm = play(true);
  console.log('без згладжування:', raw, '\nзі згладжуванням:', sm);
  assert.ok(raw.corr > 5, 'перерахунки зсували гравців — інакше тест нічого не міряв');
  assert.ok(raw.visJerk > 0, 'без згладжування ривки видно');
  assert.equal(sm.visJerk, 0, 'зі згладжуванням — жодного стрибка понад 3,5 кроку за тик');
  const step = Math.round(PARAMS.playerSpeed * R.U / R.TPS);
  assert.ok(sm.visMax <= 3 * step + 2, `найбільший видимий крок ${sm.visMax} ≤ 3 кроки гравця`);
});

test('згладжування: зсув рівно гасить стрибок і тане до нуля', () => {
  const s = R.newGame(7, PARAMS, 2), sm = createSmoother(true);
  sm.apply(s)();
  sm.before(s);
  s.players[1].x += 600;                 // перерахунок посунув чужого на 0,6 клітинки
  sm.after(s);
  assert.equal(sm.vis(s, 1).x, s.players[1].x - 600, 'видима поза на місці');
  for (let i = 0; i < 6; i++) { s.tick++; sm.apply(s)(); }
  assert.ok(Math.abs(sm.vis(s, 1).x - s.players[1].x) < 30, 'за 6 тиків (300 мс) зсув майже зник');
  sm.before(s); s.players[0].y += 5000; sm.after(s);   // телепорт — не згладжуємо
  assert.equal(sm.vis(s, 0).y, s.players[0].y);
});

test('затримка: порядок зберігається, середнє ≈ половина RTT', async () => {
  let r = 0;
  const rnd = () => (r = (r * 9301 + 49297) % 233280) / 233280;
  const lag = createLag(120, 60, rnd), got = [], t0 = performance.now();
  for (let i = 0; i < 20; i++) lag(() => got.push([i, performance.now() - t0]));
  await new Promise((ok) => setTimeout(ok, 200));
  assert.deepEqual(got.map((x) => x[0]), [...Array(20).keys()]);
  assert.ok(got[0][1] >= 25 && got[19][1] <= 140, JSON.stringify(got.map((x) => Math.round(x[1]))));
  let now = false; createLag(0, 0)(() => { now = true; }); assert.ok(now, 'без затримки — одразу');
});

test('hash бачить додатки: гуси, проміжну станцію, летючі речі, нові поля гравця й потяга', () => {
  const s = R.newGame(7, PARAMS, 2), h0 = R.hash(s);
  const mod = [
    (t) => { t.geese = [{ id: 1, x: 500, y: 500, hold: 0, mode: 'go', t: 0, tx: 1, ty: 1, side: 1, sp: 125 }]; },
    (t) => { t.mid = { x0: 1, y0: 1, x1: 3, y1: 3, signs: [], pick: '', wait: 0, len1: 40 }; },
    (t) => { t.flying = [{ k: 2, n: 1, x: 100, y: 200, vx: 1, vy: 0, t: 5 }]; },
    (t) => { t.players[1].stun = 7; },
    (t) => { t.train.boost = 1; },
  ];
  for (const f of mod) { const t = R.clone(s); f(t); assert.notEqual(R.hash(t), h0, f.toString()); }
  // порядок ключів не важить (copyInto міг його змінити), ev/stats/aim — не стан світу
  const a = R.clone(s); a.flying = [{ x: 1, y: 2 }]; const b = R.clone(s); b.flying = [{ y: 2, x: 1 }];
  assert.equal(R.hash(a), R.hash(b));
  const c = R.clone(s); c.ev.push({ t: 'chop', x: 1, y: 1 }); c.stats.hits++; c.players[0].aim = { x: 1, y: 1 };
  assert.equal(R.hash(c), h0);
  // гуси, що рухаються, міняють hash (без них світи могли б розійтися непомітно)
  const g = R.newGame(7, { ...PARAMS, geese: 'on', geeseSec: 10 }, 1), hs = new Set();
  for (let i = 0; i < 400; i++) { R.step(g, []); if (i % 20 === 0) hs.add(R.hash(g)); }
  assert.ok(hs.size > 10);
});
