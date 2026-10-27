// K28 «Мережа 3: люди за столом» — чисті частини net.js: ніки, реакції, пінг, глядачі.
import test from 'node:test';
import assert from 'node:assert/strict';
import { shortNick, namesFrom, REACTS, createReactGate, freshReacts, createPinger, pingColor, lostFrom, watchersText } from '../../web/games/koliya/net.js';

test('пінг: раз на секунду, луна мінус час на сервері, згладжено', () => {
  const pg = createPinger(1000);
  const i = pg.due(0);
  assert.equal(i, 1);
  assert.equal(pg.due(500), 0);           // ще зарано
  assert.equal(pg.echo(i, 30, 150), 120); // 150 мс туди-назад, 30 з них — чекав кадру на сервері
  assert.equal(pg.echo(i, 0, 900), 120);  // повтор луни — уже не рахується
  const j = pg.due(1000);
  assert.equal(pg.echo(j, 0, 1220), 150); // 220 → 0,7·120 + 0,3·220 = 150
  const k = pg.due(2000);
  assert.equal(pg.echo(k, 10, 2060), 100); // менший — одразу наполовину
  assert.equal(pingColor(50), '#8fe36b');
  assert.equal(pingColor(120), '#ffd23f');
  assert.equal(pingColor(null), '#9a9a9a');
});

test('глядачі: відмінок за числом', () => {
  assert.equal(watchersText(1), '👀 1 глядач');
  assert.equal(watchersText(3), '👀 3 глядачі');
  assert.equal(watchersText(5), '👀 5 глядачів');
  assert.equal(watchersText(11), '👀 11 глядачів');
  assert.equal(watchersText(22), '👀 22 глядачі');
  assert.equal(watchersText(12), '👀 12 глядачів');
});

test('«відпав»: -1 з кадру, а без кадрів > 3 с — і свої', () => {
  assert.deepEqual(lostFrom([120, -1, 80], false, [0]), [false, true, false]);
  assert.deepEqual(lostFrom([120, -1, 80], true, [0, -1]), [true, true, false]);
  assert.equal(lostFrom(null, true, [1])[1], true);
  assert.equal(!!lostFrom(null, true, [1])[0], false);
});
import * as R from '../../web/games/koliya/rules.js';

test('реакції: ліміт раз на секунду з гравця, кожен свій', () => {
  const g = createReactGate();
  assert.equal(REACTS.length, 4);
  assert.equal(g(0, 1000), true);
  assert.equal(g(0, 1500), false);
  assert.equal(g(1, 1500), true);
  assert.equal(g(0, 2000), true);
});

test('реакції з кадру: лише нові, без своїх, номери з нуля після перезапуску сервера', () => {
  const mine = (p, i) => p === 0 && i === 5;
  let r = freshReacts([1, 1, 2, 7, 2, 0, 1, 5], -1, mine);
  assert.deepEqual(r, { seen: 2, list: [{ p: 1, e: 2 }] });
  r = freshReacts([1, 1, 2, 7, 2, 0, 1, 5, 3, 1, 9, 1], r.seen, mine);   // e=9 — нема такої
  assert.deepEqual(r, { seen: 3, list: [] });
  r = freshReacts([1, 2, 0, 1], 5000, mine);
  assert.deepEqual(r, { seen: 1, list: [{ p: 2, e: 0 }] });
  assert.deepEqual(freshReacts(undefined, 4), { seen: 4, list: [] });
});

test('реакції не в правилах: хеш світу від game.reacts/names не залежить', () => {
  const s = R.newGame(7, R.defaultParams(), 2);
  const h = R.hash(s);
  const game = { state: s, reacts: [{ e: '👍', k: 1, at: 0 }], names: ['Оля', 'Петро'] };
  assert.equal(R.hash(game.state), h);
});

test('нік над гравцем — до 10 символів, «²» другого за ПК не ріжемо', () => {
  assert.equal(shortNick('Оля'), 'Оля');
  assert.equal(shortNick('Владислав12'), 'Владислав…');
  assert.equal(shortNick('Владислав1'), 'Владислав1');
  assert.equal(shortNick('Дуже довгий нік ²'), 'Дуже довг… ²');
  assert.equal(shortNick('Оля ²'), 'Оля ²');
  assert.equal(shortNick(''), null);
  assert.equal(shortNick(null), null);
  assert.deepEqual(namesFrom(['Оля', null, 'Оля ²']), ['Оля', null, 'Оля ²']);
  assert.deepEqual(namesFrom(undefined), []);
});
