// K28 «Мережа 3: люди за столом» — чисті частини net.js: ніки, реакції, пінг, глядачі.
import test from 'node:test';
import assert from 'node:assert/strict';
import { shortNick, namesFrom } from '../../web/games/koliya/net.js';

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
