// «Колія» K60 — характер бота-напарника (createBot(p, { buddy: true, zeal })): 0 — ввічливий K34, 100 — автопілот.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { createBot, zealOf } from '../../web/games/koliya/bot.js';
import { playMix } from './bot-human.mjs';

test('zealOf: числа 0..100 з округленням, решта — null', () => {
  assert.equal(zealOf(30), 30); assert.equal(zealOf(-5), 0); assert.equal(zealOf(140), 100); assert.equal(zealOf(44.6), 45);
  assert.equal(zealOf('30'), null); assert.equal(zealOf(NaN), null); assert.equal(zealOf(undefined), null);
});

test('край 0 — нинішній напарник, край 100 — звичайний бот (той самий хеш партії)', () => {
  const h = (kind, zeal) => R.hash(playMix(4, kind, { maxMin: 2, zeal }).s);
  assert.equal(h('buddy', 0), h('buddy', undefined));
  assert.equal(h('buddy', 100), h('old'));
  assert.notEqual(h('buddy', 60), h('buddy', 0));
});

test('характер — лише вхід бота: та сама партія з тим самим характером (і зміною на ходу) детермінована', () => {
  const run = () => playMix(6, 'buddy', { maxMin: 2, zeal: 30, onTick: (s, b) => { if (s.tick === 30 * R.TPS) b.setZeal(80); } });
  const a = run(), b = run();
  assert.equal(R.hash(a.s), R.hash(b.s));
  const bot = createBot(1, { buddy: true, zeal: 30 });
  bot.setZeal(100); assert.equal(bot.zeal, 100); bot.setZeal('x'); assert.equal(bot.zeal, 100);
  const plain = createBot(1, {}); plain.setZeal(0); assert.equal(plain.zeal, 100, 'звичайного бота повзунок не робить напарником');
});

test('завзятіший перебиває більше за ввічливого', () => {
  let lo = 0, hi = 0;
  for (const seed of [1, 2]) { lo += playMix(seed, 'buddy', { maxMin: 6, zeal: 0 }).sum; hi += playMix(seed, 'buddy', { maxMin: 6, zeal: 100 }).sum; }
  assert.ok(lo < hi, `перебивань: 0 → ${lo}, 100 → ${hi}`);
});
