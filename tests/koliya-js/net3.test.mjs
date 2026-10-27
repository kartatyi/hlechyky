// K28 «Мережа 3: люди за столом» — чисті частини net.js: ніки, реакції, пінг, глядачі.
import test from 'node:test';
import assert from 'node:assert/strict';
import { shortNick, namesFrom, REACTS, createReactGate, freshReacts } from '../../web/games/koliya/net.js';
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
