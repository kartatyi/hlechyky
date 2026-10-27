// «Колія» — картка столу на сайті (web/games/koliya.js) і шар пада сайту (web/static/pad.js): пад гра читає сама
// в рамці, тож сайт за столом мовчить. 27.10 без цього Ⓑ («кинути») виводила зі столу, а плечі гортали розділи.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const src = readFileSync(new URL('../../web/games/koliya.js', import.meta.url), 'utf8');
let mod = null;
vm.runInNewContext(src, { HGames: { register: (m) => { mod = m; } }, URLSearchParams, location: { search: '' } });

test('картка Колії забирає в шару пада сайту стік і всі кнопки, крім Ⓑ', () => {
  assert.ok(mod && mod.pad, 'pad оголошено');
  assert.equal(mod.pad.dirs, true, 'стік не водить кільце фокуса сторінкою');
  assert.equal(mod.pad.rstick, true, 'правий стік не гортає сторінку, R3 не вмикає курсор');
  for (const b of ['a', 'x', 'y', 'lb', 'rb', 'lt', 'rt', 'start', 'select', 'r3']) assert.equal(mod.pad.on(b), true, b);
  assert.equal(mod.pad.on('b'), false, 'Ⓑ лишається сайту: утримання — вийти зі столу');
  assert.notEqual(mod.pad.exit, false);
  assert.match(mod.pad.hint, /\{a\}/);
});
