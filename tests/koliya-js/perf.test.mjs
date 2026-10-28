// «Колія» K51 — швидкодія правил і бота без зміни поведінки: 10 сідів × 3000 тиків з ботами й усіма додатками дають
// ТІ САМІ hash, що й до оптимізації (очікувані числа нижче зняті ДО K51). Хеш — ланцюжок hash() кожні 100 тиків.
// Заміри: node tests/koliya-js/perf.test.mjs --bench (друкує мс на step і на think).
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';

// усі додатки (K11 гуси/проміжна, K18 кооп, K26 зима/яр); кожен третій сід — нескінченна колія (K19)
export const ALL = { geese: 'on', stationMode: 'upgrade', bump: 'on', throw: 'on', stun: 'on', winter: 'on', yar: 'on' };
export const CASES = Array.from({ length: 10 }, (_, i) => ({
  seed: 101 + i * 37, n: 1 + (i % 4), params: { ...ALL, stripLen: i % 2 ? 300 : 100, ...(i % 3 === 2 ? { stationMode: 'endless' } : {}) },
}));

/** Партія ботами; tm — { step, think } для замірів (мс) або null. Повертає ланцюжок hash */
export function run(c, ticks = 3000, tm = null) {
  const s = R.newGame(c.seed, { ...R.defaultParams(), ...c.params }, c.n);
  const bots = Array.from({ length: c.n }, (_, i) => createBot(i));
  let h = 0;
  for (let t = 0; t < ticks && s.status === 'run'; t++) {
    const cmds = [];
    const t0 = tm ? performance.now() : 0;
    for (const b of bots) {
      const v = b.think(s);
      cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 });
      if (v.use) cmds.push({ p: b.p, k: 'use' });
    }
    const t1 = tm ? performance.now() : 0;
    R.step(s, cmds);
    if (tm) { const t2 = performance.now(); tm.think += t1 - t0; tm.step += t2 - t1; tm.n++; if (t2 - t0 > tm.max) tm.max = t2 - t0; }
    if (s.tick % 100 === 0) h = (Math.imul(h, 31) + R.hash(s)) >>> 0;
  }
  return (Math.imul(h, 31) + R.hash(s)) >>> 0;
}

// Зняті БЕЗ оптимізацій K51 — на koliya 944eabe4 після K48 (зміни extras/endless; на 35ed4602 були інші). Якщо свідомо
// міняєш поведінку правил/бота — перезніми: node tests/koliya-js/perf.test.mjs --print
// 28.10 перезнято: крізь вагон — одразу, але повільно (K33.passT 0); сіди без зустрічей з потягом не змінились
const EXPECT = [3441856869, 1972851392, 2770344575, 3222387144, 3813203626, 1848867695, 2476463392, 650828885, 3001156907, 205519207];

test('K51: hash 10 сідів × 3000 тиків з ботами й усіма додатками — як до оптимізації', () => {
  const got = CASES.map((c) => run(c));
  assert.deepEqual(got, EXPECT);
});

if (process.argv.includes('--print')) console.log(JSON.stringify(CASES.map((c) => run(c))));
if (process.argv.includes('--bench')) {
  for (const len of [100, 300]) for (const n of [1, 4]) {
    const tm = { step: 0, think: 0, n: 0, max: 0 };
    for (const seed of [11, 22, 33]) run({ seed, n, params: { ...ALL, stripLen: len } }, 3000, tm);
    console.log(`смуга ${len}, гравців ${n}: step ${(tm.step / tm.n).toFixed(3)} мс, think (усі боти) ${(tm.think / tm.n).toFixed(3)} мс, найдовший тик ${tm.max.toFixed(2)} мс, тиків ${tm.n}`);
  }
}
