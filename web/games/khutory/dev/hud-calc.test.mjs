// node --test web/games/khutory/dev/hud-calc.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { board } from './fixture.js';
import {
  resPhrase, resList, total, vertexHexes, vertexYield, yieldText, portAt, adviseTowns, discardPlan, compact,
  bankChoices, lacking, chartGeom,
} from '../hud-calc.js';

const b = board();

test('форми ресурсів', () => {
  assert.equal(resPhrase('wood', 1), '1 дерево');
  assert.equal(resPhrase('clay', 3), '3 глини');
  assert.equal(resPhrase('wood', 5), '5 дерев');
  assert.equal(resPhrase('wood', 11), '11 дерев');
  assert.equal(resPhrase('wood', 21), '21 дерево');
  assert.equal(resList({ ore: 2, wood: 1 }), '1 дерево, 2 заліза');
  assert.equal(resList({}), 'нічого');
  assert.equal(total({ wood: 3, ore: 2 }), 5);
});

test('вершини торкаються 1–3 гексів', () => {
  const vh = vertexHexes(b);
  assert.equal(vh.length, 54);
  for (const hs of vh) assert.ok(hs.length >= 1 && hs.length <= 3);
  assert.equal(vh.filter((hs) => hs.length === 3).length, 24);   // внутрішні вершини поля радіуса 2
});

test('вершина: крапки й відсоток', () => {
  // Спільна вершина гексів 0 (ліс 10), 3 (лан 12), 4 (глинище 6)? — шукаємо вершину з гексами 4 і 0.
  const v = b.hexes[4].v.find((x) => b.hexes[0].v.includes(x));
  const y = vertexYield(b, v);
  const nums = y.items.map((it) => it.num).sort((a, c) => a - c);
  assert.ok(nums.includes(6) && nums.includes(10));
  assert.equal(y.pips, y.items.reduce((s, it) => s + it.pips, 0));
  assert.equal(y.pct, Math.round(y.pips / 36 * 100));
  assert.match(yieldText(y), /глина \(6\)/);
  assert.match(yieldText(y), /родить у \d+ % кидків$/);
  // Кармелюк на глинищі — його крапки не рахуються, але в списку видно.
  const yr = vertexYield(b, v, 4);
  assert.equal(yr.pips, y.pips - 5);
  assert.match(yieldText(yr), /там Кармелюк/);
});

test('Дике поле нічого не дає', () => {
  const v = b.hexes[9].v[0];
  const y = vertexYield(b, v);
  assert.ok(y.items.every((it) => it.hex !== 9));
});

test('пристані при вершинах', () => {
  const p = b.ports[0];
  assert.equal(portAt(b, b.edges[p.e][0]), p);
  assert.equal(portAt(b, b.hexes[9].v[0]), null);   // центр поля — не берег
});

test('Глек радить — 3 різні найродючіші', () => {
  const all = b.vx.map((_, v) => v);
  const top = adviseTowns(b, all, 3);
  assert.equal(top.length, 3);
  assert.equal(new Set(top).size, 3);
  const best = vertexYield(b, top[0]).pips;
  assert.ok(best >= 10, 'найкраща вершина має ≥ 10 крапок, а має ' + best);
  // Лише з дозволених
  assert.deepEqual(adviseTowns(b, [top[2], top[1]], 3).sort(), [top[1], top[2]].sort());
  assert.deepEqual(adviseTowns(b, [], 3), []);
});

test('скидання: половина з найчисленніших', () => {
  const plan = discardPlan({ wood: 3, clay: 2, wool: 1, grain: 2, ore: 1 }, 4);
  assert.equal(total(plan), 4);
  assert.equal(plan.wood, 2);           // 3→2→… найбільша купа першою
  assert.ok(plan.ore === 0);            // залізо при рівності береже
  for (const r of Object.keys(plan)) assert.ok(plan[r] <= ({ wood: 3, clay: 2, wool: 1, grain: 2, ore: 1 })[r]);
  assert.deepEqual(compact(plan), Object.fromEntries(Object.entries(plan).filter(([, n]) => n > 0)));
  const big = discardPlan({ wood: 0, clay: 0, wool: 0, grain: 10, ore: 0 }, 5);
  assert.equal(big.grain, 5);
  assert.equal(total(discardPlan({ wood: 1 }, 3)), 1);   // більше, ніж є, не скине
});

test('банк: найкращий курс першим', () => {
  const ch = bankChoices({ wood: 4, clay: 2, wool: 3, grain: 0, ore: 1 }, { wood: 4, clay: 2, wool: 3, grain: 3, ore: 4 });
  assert.equal(ch[0].res, 'clay');
  assert.equal(ch[0].can, true);
  assert.deepEqual(ch.filter((c) => c.can).map((c) => c.res), ['clay', 'wool', 'wood']);
  assert.equal(ch.find((c) => c.res === 'wood').max, 1);
});

test('бракує на ціну', () => {
  assert.deepEqual(lacking({ grain: 1, ore: 0 }, { grain: 2, ore: 3 }), { grain: 1, ore: 3 });
  assert.deepEqual(lacking({ wood: 1, clay: 1 }, { wood: 1, clay: 1 }), {});
});

test('графік очок', () => {
  const g = chartGeom([[0, 0], [2, 3], [5, 8]], 300, 160, 8);
  assert.equal(g.lines.length, 2);
  assert.equal(g.lines[0].length, 3);
  assert.equal(g.max, 8);
  assert.equal(g.lines[1][2][1], g.targetY);       // 8 очок — на лінії мети
  assert.equal(g.lines[0][0][0], 22);
  assert.equal(g.lines[0][2][0], 278);
  assert.equal(chartGeom([], 100, 100).lines.length, 0);
});

import { robberInfo } from '../hud-calc.js';
import { makeView } from './fixture.js';

test('Кармелюк: бейджі жертв, своя хата, «Глек радить»', () => {
  const v = makeView('robber', { seat: 0 });
  const info = robberInfo(v, 0);
  assert.equal(info.length, v.me.robber.length);
  assert.ok(!info.some((x) => x.h === v.robber));
  for (const x of info) {
    const vs = new Set(v.board.hexes[x.h].v);
    const on = v.towns.filter((t) => vs.has(t.v));
    assert.equal(x.own, on.some((t) => t.seat === 0));
    assert.deepEqual(x.victims.map((y) => y.seat), [...new Set(on.map((t) => t.seat).filter((s) => s !== 0))].sort());
    for (const y of x.victims) assert.equal(y.cards, v.players.find((p) => p.seat === y.seat).cards);
  }
  const best = info.filter((x) => x.best);
  assert.equal(best.length, 1);
  assert.ok(!best[0].own && best[0].victims.length > 0);
  assert.ok(info.some((x) => x.own), 'у фікстурі є гекс із моєю хатою');
});

test('Кармелюк: «Глек радить» — більше крапок × жертв', () => {
  const b = board();
  // Дві жертви на гексі 4 (pit 6, 5 крапок) і одна — на гексі 0 (forest 10, 3 крапки).
  const v = { board: b, robber: 9, players: [{ seat: 0, cards: 3, points: 2 }, { seat: 1, cards: 5, points: 2 }, { seat: 2, cards: 0, points: 2 }],
    towns: [{ v: b.hexes[4].v[0], seat: 1 }, { v: b.hexes[4].v[3], seat: 2 }, { v: b.hexes[0].v[0], seat: 1 }], me: { seat: 0, robber: [0, 4, 7] } };
  const info = robberInfo(v, 0);
  assert.deepEqual(info.map((x) => x.best), [false, true, false]);
  assert.deepEqual(info[1].victims, [{ seat: 1, cards: 5 }, { seat: 2, cards: 0 }]);
  assert.deepEqual(info[2].victims, []);
  // Моя хата на найкращому — порада переходить на інший.
  v.towns.push({ v: b.hexes[4].v[1], seat: 0 });
  const i2 = robberInfo(v, 0);
  assert.equal(i2[1].own, true);
  assert.deepEqual(i2.map((x) => x.best), [true, false, false]);
  // Глядач (seat null) — своїх нема, жертви всі.
  assert.equal(robberInfo({ ...v, me: null }, null).filter((x) => x.own).length, 0);
  assert.deepEqual(robberInfo({ board: null }, 0), []);
});
