// K46: «?net=new» тим самим ніком, поки старий стіл живий, — зрозуміле питання замість вічних 404
import test from 'node:test';
import assert from 'node:assert/strict';
import { resyncDelay, seatedElsewhereMsg } from '../../web/games/koliya/net.js';

test('K46: відмову «сидиш за іншим столом» відрізняємо від «ти вже за цим столом»', () => {
  assert.equal(seatedElsewhereMsg('Ти вже за столом. Встань, якщо хочеш новий'), true);
  assert.equal(seatedElsewhereMsg('Ти вже за цим столом'), false);
  assert.equal(seatedElsewhereMsg('Столів уже задосить, дограйте ті, що є'), false);
  assert.equal(seatedElsewhereMsg(undefined), false);
});

test('K46: журнал 404 — щонайбільше 3 спроби, інші збої — з паузою, що росте до 15 с', () => {
  let n = 0, tries = 1;
  while (resyncDelay(++n, true) >= 0) tries++;
  assert.equal(tries, 3);
  assert.deepEqual([1, 2, 3, 4, 5, 9].map((i) => resyncDelay(i, false)), [2000, 4000, 8000, 15000, 15000, 15000]);
});
