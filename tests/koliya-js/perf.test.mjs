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
    if (tm) { tm.think += t1 - t0; tm.step += performance.now() - t1; tm.n++; }
    if (s.tick % 100 === 0) h = (Math.imul(h, 31) + R.hash(s)) >>> 0;
  }
  return (Math.imul(h, 31) + R.hash(s)) >>> 0;
}

// Зняті ДО оптимізації K51 (35ed4602)
const EXPECT = [2638656582, 134742427, 3363191730, 3790388077, 1475501953, 1848867695, 3233664096, 3953439033, 3001156907, 427195850];

test('K51: hash 10 сідів × 3000 тиків з ботами й усіма додатками — як до оптимізації', () => {
  const got = CASES.map((c) => run(c));
  assert.deepEqual(got, EXPECT);
});

if (process.argv.includes('--print')) console.log(JSON.stringify(CASES.map((c) => run(c))));
if (process.argv.includes('--bench')) {
  for (const len of [100, 300]) for (const n of [1, 4]) {
    const tm = { step: 0, think: 0, n: 0 };
    for (const seed of [11, 22, 33]) run({ seed, n, params: { ...ALL, stripLen: len } }, 3000, tm);
    console.log(`смуга ${len}, гравців ${n}: step ${(tm.step / tm.n).toFixed(3)} мс, think (усі боти) ${(tm.think / tm.n).toFixed(3)} мс, тиків ${tm.n}`);
  }
}
