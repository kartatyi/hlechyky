// node --test web/games/khutory/dev/hud-plates.test.mjs
// Плашки гравців, смуга чисел і звання підсумку (khutory-look.md §4, §9, §12).
import test from 'node:test';
import assert from 'node:assert/strict';
import { makeView } from './fixture.js';
import { awardNeed, seatPorts, numberYield, sumTitles, myMoment, chartLines, chartGeom } from '../hud-calc.js';

test('нагороди: скільки бракує', () => {
  // Власник шляху — місце 2 з 6 відтинків: іншим треба 7; самому власнику — has.
  const L = { seat: 2, len: 6 };
  assert.deepEqual(awardNeed(L, null, { seat: 0, road: 3, knights: 1 }).road, { n: 3, need: 7, has: false, near: false, pct: 43 });
  assert.equal(awardNeed(L, null, { seat: 1, road: 6, knights: 0 }).road.near, true);
  assert.equal(awardNeed(L, null, { seat: 2, road: 6, knights: 0 }).road.has, true);
  // Без власника — щонайменше 5 і 3.
  const a = awardNeed(null, null, { seat: 0, road: 4, knights: 2 });
  assert.equal(a.road.need, 5); assert.equal(a.road.near, true);
  assert.equal(a.army.need, 3); assert.equal(a.army.near, true);
  assert.equal(awardNeed(null, { seat: 1, n: 4 }, { seat: 0, road: 0, knights: 2 }).army.need, 5);
  assert.equal(awardNeed(null, null, { seat: 0, road: 9, knights: 0 }).road.pct, 100);
});

test('пристані гравця', () => {
  const v = makeView('main');
  const b = v.board;
  // Ставимо хату місця 0 на вершину першої пристані.
  const p = b.ports[1];
  const towns = [{ v: b.edges[p.e][0], seat: 0, city: false }, { v: b.edges[b.ports[0].e][1], seat: 0, city: false }];
  assert.deepEqual(seatPorts(b, towns, 0), ['any', p.kind]);
  assert.deepEqual(seatPorts(b, towns, 1), []);
});

test('що дасть кожне число', () => {
  const v = makeView('main', { seat: 0 });
  const { cells, avg } = numberYield(v, 0);
  assert.equal(cells.length, 11);
  assert.equal(cells[5].n, 7);
  assert.equal(cells[5].cnt, 0);
  // Ручний підрахунок: усі гекси з моїми хатами (×2 за містечко), крім Кармелюкового.
  let want = 0;
  v.board.hexes.forEach((hx, i) => {
    if (!hx.num || i === v.robber) return;
    for (const t of v.towns) if (t.seat === 0 && hx.v.includes(t.v)) want += (t.city ? 2 : 1) * ({ 2: 1, 3: 2, 4: 3, 5: 4, 6: 5, 8: 5, 9: 4, 10: 3, 11: 2, 12: 1 })[hx.num] / 36;
  });
  assert.ok(Math.abs(avg - want) < 1e-9);
  assert.ok(avg > 0);
  for (const c of cells) if (c.n !== 7) assert.deepEqual(c.hexes, v.board.hexes.map((h, i) => (h.num === c.n ? i : -1)).filter((i) => i >= 0));
  // Містечко родить удвічі.
  const city = makeView('main', { seat: 0 });
  city.towns = city.towns.map((t) => (t.seat === 0 ? { ...t, city: true } : t));
  assert.ok(Math.abs(numberYield(city, 0).avg - avg * 2) < 1e-9);
  // Кармелюк на моїй землі — число позначено robbed і нічого не дає.
  const hx = v.board.hexes.findIndex((h) => h.num && v.towns.some((t) => t.seat === 0 && h.v.includes(t.v)));
  const rv = { ...v, robber: hx };
  const cell = numberYield(rv, 0).cells[v.board.hexes[hx].num - 2];
  assert.equal(cell.robbed, true);
});

test('звання: чесний лідер, не більше двох на людину', () => {
  const v = makeView('over', { seat: 0 });
  const t = sumTitles(v.over, v);
  const per = {};
  for (const x of t) if (x.seat != null) per[x.seat] = (per[x.seat] || 0) + 1;
  for (const k of Object.keys(per)) assert.ok(per[k] <= 2, 'не більше двох звань');
  const by = (n) => t.find((x) => x.name.startsWith(n));
  assert.equal(by('Чумак року').seat, 2);           // longest у фікстурі — місце 2
  assert.equal(by('Улюбленець Кармелюка').seat, 0); // robbed [3,1,2,0]
  assert.match(by('Улюбленець Кармелюка').desc, /3 рази/);
  assert.equal(by('Отаман').seat, 2);               // козаки [1,0,2,0]
  assert.equal(by('Майстер торгу'), undefined);     // trades [4,2,6,1] — лідер 2, але в нього вже два звання
  assert.equal(by('Щасливе число').seat, null);
  assert.match(by('Щасливе число').name, /7$/);     // dice: 9 разів сімка
  // Нічия — звання нема.
  const tie = { stats: { seats: [{ seat: 0, trades: 2 }, { seat: 1, trades: 2 }], dice: [] } };
  assert.equal(sumTitles(tie).find((x) => x.name === 'Майстер торгу'), undefined);
  // Нулі — звання нема.
  const zero = { stats: { seats: [{ seat: 0, robbed: 0 }, { seat: 1, robbed: 0 }] } };
  assert.equal(sumTitles(zero).length, 0);
});

test('звання: третє звання тій самій людині не дається', () => {
  const over = { stats: { seats: [
    { seat: 0, trades: 9, stolen: 5, robbed: 4, got: 40, built: { road: 9, town: 4, city: 3 } },
    { seat: 1, trades: 1, stolen: 0, robbed: 1, got: 10, built: { road: 2, town: 2, city: 0 } }] } };
  const t = sumTitles(over, {});
  assert.equal(t.filter((x) => x.seat === 0).length, 2);
  assert.ok(t.some((x) => x.seat === 1));          // «Кубики не люблять»
});

test('твій момент — за номерами місць', () => {
  const tl = [[0, 0, 0, 0], [2, 2, 0, 2], [3, 3, 0, 2], [3, 4, 0, 4], [5, 5, 0, 4], [5, 7, 0, 5], [6, 8, 0, 5]];
  const m = myMoment(tl, 1, [0, 1, 3], 12);
  assert.equal(m.place, 1);
  assert.equal(m.tie, false);
  assert.equal(myMoment(tl, 0, [0, 1, 3], 12).tie, true);   // місце 0 наздогнало, не вирвалось
  assert.ok(m.up > 0);
  assert.equal(m.i, 5);   // 5 → 7 при 5 і 5 у решти: вирвався вперед сам
  assert.ok(m.round >= 1 && m.round <= 12);
  assert.equal(myMoment([[0, 0], [0, 0]], 0, [0, 1]), null);
  assert.equal(myMoment(tl, null, [0, 1, 3]), null);
});

test('графік: лінія за номером місця, порожні місця пропущено', () => {
  const tl = [[0, 0, 0, 0], [2, 3, 0, 1]];
  const g = chartGeom(tl, 320, 170, 8);
  const lines = chartLines(g, [0, 1, 3]);
  assert.deepEqual(lines.map((l) => l.seat), [0, 1, 3]);
  assert.deepEqual(lines[2].pts, g.lines[3]);
});
