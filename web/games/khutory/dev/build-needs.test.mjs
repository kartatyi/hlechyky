// node --test web/games/khutory/dev/build-needs.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { COST } from '../consts.js';
import { buildNeeds, BUILD_KEYS } from '../hud-calc.js';

test('порядок і кружечки — з COST (consts.js), по одному на карту', () => {
  const b = buildNeeds({}, COST);
  assert.deepEqual(b.map((x) => x.k), BUILD_KEYS);
  assert.deepEqual(b.map((x) => x.dots.length), [2, 4, 5, 3]);
  assert.deepEqual(b[2].dots.map((d) => d.res), ['grain', 'grain', 'ore', 'ore', 'ore']);
});

test('порожня рука — бракує всього', () => {
  const [road, town, city, dev] = buildNeeds({}, COST);
  assert.equal(road.ok, false);
  assert.deepEqual(road.lack, { wood: 1, clay: 1 });
  assert.equal(town.n, 4);
  assert.equal(city.n, 5);
  assert.deepEqual(dev.lack, { wool: 1, grain: 1, ore: 1 });
  assert.ok(road.dots.every((d) => d.have === false));
});

test('часткова рука: кожен кружечок — за рукою', () => {
  const hand = { wood: 2, clay: 0, wool: 1, grain: 1, ore: 2 };
  const [road, town, city, dev] = buildNeeds(hand, COST);
  assert.deepEqual(road.dots.map((d) => d.have), [true, false]);
  assert.deepEqual(road.lack, { clay: 1 });
  assert.equal(road.n, 1);
  assert.deepEqual(town.lack, { clay: 1 });
  assert.deepEqual(city.dots.map((d) => d.have), [true, false, true, true, false]);
  assert.deepEqual(city.lack, { grain: 1, ore: 1 });
  assert.equal(dev.ok, true);
  assert.deepEqual(dev.lack, {});
});

test('глядач (руки нема) — нейтрально', () => {
  const b = buildNeeds(null, COST);
  assert.ok(b.every((x) => !x.ok && x.n === 0 && x.dots.every((d) => d.have === null)));
});

test('свій перелік будов', () => {
  assert.deepEqual(buildNeeds({ grain: 2, ore: 3 }, COST, ['city']).map((x) => [x.k, x.ok]), [['city', true]]);
});
