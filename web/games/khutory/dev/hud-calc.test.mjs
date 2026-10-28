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

// ─── Торг і увага (khutory-look.md §1, §10) ─────────────────────────────
import { resPhraseAcc, resListAcc, bankChips, bankFix, allRefused, incomingOffers } from '../hud-calc.js';
import { COST } from '../consts.js';

test('знахідний відмінок: дає 1 глину, 2 глини, 5 глин, 21 вовну, 11 глин', () => {
  assert.equal(resPhraseAcc('clay', 1), '1 глину');
  assert.equal(resPhraseAcc('wool', 1), '1 вовну');
  assert.equal(resPhraseAcc('wood', 1), '1 дерево');
  assert.equal(resPhraseAcc('clay', 2), '2 глини');
  assert.equal(resPhraseAcc('clay', 5), '5 глин');
  assert.equal(resPhraseAcc('wool', 21), '21 вовну');
  assert.equal(resPhraseAcc('clay', 11), '11 глин');
  assert.equal(resListAcc({ wool: 1, clay: 1 }), '1 глину, 1 вовну');
  assert.equal(resListAcc({}), 'нічого');
});

test('чипи банку: найкращий курс із me.rates і скільки разів можна', () => {
  const ch = bankChips({ wood: 9, clay: 2, wool: 0, grain: 4, ore: 1 }, { wood: 4, clay: 3, wool: 3, grain: 2, ore: 4 });
  assert.deepEqual(ch.map((c) => c.res), ['wood', 'clay', 'wool', 'grain', 'ore']);
  assert.deepEqual(ch.map((c) => c.max), [2, 0, 0, 2, 0]);
  assert.deepEqual(ch.map((c) => c.port), [false, true, true, true, false]);
  assert.equal(bankChips({}, null)[0].rate, 4);
});

test('обміняй і будуй: бракує 1 вовни — 3 зерна з пристані', () => {
  const hand = { wood: 2, clay: 1, wool: 0, grain: 4, ore: 0 };
  assert.deepEqual(bankFix(hand, COST.town, { wood: 4, clay: 4, wool: 4, grain: 3, ore: 4 }), [{ give: 'grain', get: 'wool', n: 1, rate: 3 }]);
  // зерна лише 3 понад ціну хати (1) — 4 - 1 = 3 ≥ 3: так; при курсі 4 — ні
  assert.equal(bankFix(hand, COST.town, { wood: 4, clay: 4, wool: 4, grain: 4, ore: 4 }), null);
});

test('обміняй і будуй: бракує 2 однакових — один обмін n = 2; різних — два; > 2 — ні', () => {
  const r4 = { wood: 4, clay: 4, wool: 4, grain: 4, ore: 4 };
  assert.deepEqual(bankFix({ wood: 9, grain: 2, ore: 1 }, COST.city, r4), [{ give: 'wood', get: 'ore', n: 2, rate: 4 }]);
  assert.deepEqual(bankFix({ wood: 5, clay: 4, grain: 1 }, COST.town, r4), [{ give: 'wood', get: 'wool', n: 1, rate: 4 }]);
  const two = bankFix({ wood: 4, clay: 5, ore: 1 }, COST.dev, r4);
  assert.deepEqual(two, [{ give: 'clay', get: 'wool', n: 1, rate: 4 }, { give: 'wood', get: 'grain', n: 1, rate: 4 }]);
  assert.equal(bankFix({ wood: 20 }, COST.city, r4), null);              // бракує 5
  assert.equal(bankFix({ wood: 1, clay: 1 }, COST.road, r4), null);      // не бракує
  assert.equal(bankFix({ wood: 1, clay: 0, grain: 3 }, COST.road, r4), null);   // банк не покриє
});

test('усі відмовились: усім — коли відмовили всі інші; одному — коли відмовив він', () => {
  const o = { id: 3, from: 0, to: null, give: { clay: 1 }, get: { wool: 1 }, acc: [], dec: [1, 2, 3] };
  assert.equal(allRefused(o, [0, 1, 2, 3]), true);
  assert.equal(allRefused({ ...o, dec: [1, 2] }, [0, 1, 2, 3]), false);
  assert.equal(allRefused({ ...o, acc: [1] }, [0, 1, 2, 3]), false);
  assert.equal(allRefused({ ...o, to: 2, dec: [2] }, [0, 1, 2, 3]), true);
});

test('вхідні пропозиції: мені/усім від поточного без моєї відповіді; зустрічні — лише в мій хід; найновіша першою', () => {
  const offers = [
    { id: 1, from: 0, to: null, acc: [], dec: [] },
    { id: 4, from: 0, to: 1, acc: [], dec: [] },
    { id: 5, from: 0, to: 2, acc: [], dec: [] },
    { id: 6, from: 0, to: null, acc: [1], dec: [] },
    { id: 7, from: 2, to: 0, acc: [], dec: [] },
  ];
  assert.deepEqual(incomingOffers(offers, 1, 0).map((x) => x.o.id), [4, 1]);
  assert.deepEqual(incomingOffers(offers, 0, 0).map((x) => [x.o.id, x.kind]), [[7, 'counter']]);
  assert.deepEqual(incomingOffers(offers, null, 0), []);
});
