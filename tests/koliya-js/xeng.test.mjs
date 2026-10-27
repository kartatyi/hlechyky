// «Колія» K52 — детермінізм між рушіями. Еталон — xeng-codes.json (коди повтору й hash на тиках 500…3000, записано в
// Node 24). Ті самі коди й ті самі партії ботами в headless Chrome і Firefox дали ті самі hash (tests/koliya-js/xeng.html).
// Тест ловить зміну правил/бота, що зсуває ці партії: тоді перезаписати xeng-codes.json і знову відкрити xeng.html
// у Chrome і Firefox (обидва мають сказати OK).
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as P from '../../web/games/koliya/replay.js';
import { XENG_CASES, xengRecord, xengPlay } from './xeng-cases.mjs';

const gold = JSON.parse(readFileSync(new URL('./xeng-codes.json', import.meta.url), 'utf8'));

test('K52: коди повтору програються в той самий hash (і на проміжних тиках)', async () => {
  for (const g of gold) {
    const p = xengPlay(await P.decode(g.code));
    assert.equal(p.end, g.hash, `сід ${g.seed}`);
    assert.deepEqual(p.marks, g.marks, `сід ${g.seed}`);
  }
});

test('K52: боти записують ті самі партії, що й в еталоні', () => {
  XENG_CASES.forEach((c, i) => {
    const r = xengRecord(c);
    assert.equal(r.rec.hash, gold[i].hash, `сід ${c.seed}`);
    assert.deepEqual(r.marks, gold[i].marks, `сід ${c.seed}`);
  });
});
