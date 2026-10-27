// «Колія» K14 — тести варіантів відчуття (move, useMode, speedCurve, aim). node --test tests/koliya-js/feel.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';

const { U, G, O, I, TPS } = R;

/** Порожня смуга: без лісу/скель/води (як у rules.test.mjs) */
function blank(params = {}, n = 1) {
  const p = { ...R.defaultParams(), forest: 0, rocks: 0, water: 0, startSec: 90, ...params };
  const s = R.newGame(7, p, n);
  for (let i = 0; i < s.w * s.h; i++) {
    const y = (i / s.w) | 0;
    if (y > 0 && y < s.h - 1) { s.obj[i] = O.NONE; s.hp[i] = 0; s.drop[i] = I.NONE; s.dropN[i] = 0; if (s.ground[i] === G.WATER) s.ground[i] = G.GRASS; }
  }
  return s;
}
const at = (s, n, cx, cy, fx = 1, fy = 0) => { const pl = s.players[n]; pl.x = cx * U + U / 2; pl.y = cy * U + U / 2; pl.fx = fx; pl.fy = fy; return pl; };
const id = (s, x, y) => y * s.w + x;
const inp = (mx, my, p = 0) => ({ p, k: 'in', mx, my });
const run = (s, n, cmds = []) => { for (let i = 0; i < n; i++) R.step(s, i === 0 ? cmds : []); };
const cellOf = (pl) => ({ x: Math.floor(pl.x / U), y: Math.floor(pl.y / U) });

test('типові варіанти K14 — перші значення; defaultParams їх має', () => {
  const d = R.defaultParams();
  assert.equal(d.move, 'free');
  for (const k of ['move']) assert.equal(R.VARIANTS[k].values[0], d[k]);
});

// ---------------- move ----------------
test("move='grid': короткий натиск у новий бік лише повертає, утримання — крок; зупинка завжди в центрі клітинки", () => {
  const s = blank({ move: 'grid' });
  const pl = at(s, 0, 60, 8, 1, 0);
  R.step(s, [inp(0, -100)]); R.step(s, [inp(0, 0)]);
  assert.deepEqual([pl.fx, pl.fy], [0, -1]);
  assert.equal(pl.x, 60 * U + U / 2); assert.equal(pl.y, 8 * U + U / 2);
  // тримаємо вгору: вже дивимось туди — рушаємо одразу, за 1 с проходимо ~4,5 клітинки
  R.step(s, [inp(0, -100)]);
  assert.ok(pl.y < 8 * U + U / 2);
  run(s, TPS - 1);
  R.step(s, [inp(0, 0)]);
  run(s, 20);
  assert.equal(pl.x, 60 * U + U / 2);
  assert.equal(pl.y % U, U / 2, 'стоїть у центрі клітинки');
  const c = cellOf(pl);
  assert.ok(8 - c.y >= 4 && 8 - c.y <= 5, `пройшов ${8 - c.y}`);
  // з місця вбік: перші GRID_TURN тиків — поворот на місці, далі йде
  const x0 = pl.x;
  R.step(s, [inp(100, 0)]); R.step(s, []);
  assert.equal(pl.x, x0); assert.deepEqual([pl.fx, pl.fy], [1, 0]);
  run(s, 4);
  assert.ok(pl.x > x0);
});

test("move='grid': у дерево не йде; у русі розворот назад одразу; detерміновано з clone", () => {
  const s = blank({ move: 'grid' });
  const pl = at(s, 0, 60, 8, 1, 0);
  s.obj[id(s, 61, 8)] = O.TREE; s.hp[id(s, 61, 8)] = 3;
  R.step(s, [inp(100, 0)]); run(s, 10);
  assert.equal(pl.x, 60 * U + U / 2);
  R.step(s, [inp(-100, 0)]); run(s, 3);       // поворот + пауза
  run(s, 2);
  assert.ok(pl.x < 60 * U + U / 2 && pl.x > 59 * U + U / 2, 'на півдорозі');
  R.step(s, [inp(100, 0)]); run(s, 10);
  R.step(s, [inp(0, 0)]); run(s, 5);
  assert.equal(pl.x, 60 * U + U / 2, 'повернувся назад до центру й уперся в дерево');
  // детермінізм
  const go = (cut) => {
    let g = R.newGame(4242, { ...R.defaultParams(), move: 'grid' }, 2);
    let r = 7;
    const rnd = () => (r = (Math.imul(r, 1103515245) + 12345) >>> 0) >>> 8;
    for (let t = 0; t < 1200; t++) {
      if (t === cut) g = R.clone(g);
      const cmds = [];
      for (let p = 0; p < 2; p++) { if (rnd() % 6 === 0) cmds.push(inp((rnd() % 201) - 100, (rnd() % 201) - 100, p)); if (rnd() % 4 === 0) cmds.push({ p, k: 'use' }); }
      R.step(g, cmds);
    }
    return R.hash(g);
  };
  assert.equal(go(-1), go(600));
});

test("move='snappy': неповний нахил — повна швидкість; по діагоналі погляд не перескакує; тягне до середини ряду", () => {
  const f = blank(), sn = blank({ move: 'snappy' });
  const a = at(f, 0, 60, 8), b = at(sn, 0, 60, 8);
  R.step(f, [inp(40, 0)]); R.step(sn, [inp(40, 0)]);
  run(f, 9); run(sn, 9);
  const x0 = 60 * U + U / 2;
  assert.equal(b.x - x0, 10 * Math.round(4.5 * U / TPS), 'повна швидкість');
  assert.ok(b.x - x0 > (a.x - x0) * 2, `snappy ${b.x} vs free ${a.x}`);
  // діагональ: дивився вправо — лишається вправо, хоч вертикаль трохи більша
  const c = at(sn, 0, 60, 8, 1, 0);
  R.step(sn, [inp(90, -100)]); run(sn, 5);
  assert.deepEqual([c.fx, c.fy], [1, 0]);
  assert.ok(c.y < 8 * U + U / 2 && c.x > 60 * U + U / 2, 'іде по діагоналі');
  // ряд: зміщений на 0,3 клітинки — після кількох кроків уздовж x у центрі ряду
  const d = at(sn, 0, 60, 8, 1, 0); d.y += 300;
  R.step(sn, [inp(100, 0)]); run(sn, 6);
  assert.equal(d.y, 8 * U + U / 2);
  // легкий нахил (бот «повертається») — без руху
  const e = at(sn, 0, 60, 8, 1, 0);
  R.step(sn, [inp(0, 10)]);
  assert.deepEqual([e.fx, e.fy, e.x, e.y], [0, 1, 60 * U + U / 2, 8 * U + U / 2]);
});
