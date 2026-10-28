// «Колія» — підсумок «хто що робив» рахують правила (s.stats.pl). node --test tests/koliya-js/tally.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';

const { U, O, I } = R;
const id = (s, x, y) => y * s.w + x;
const at = (s, n, cx, cy, fx = 1, fy = 0) => { const pl = s.players[n]; pl.x = cx * U + U / 2; pl.y = cy * U + U / 2; pl.fx = fx; pl.fy = fy; return pl; };

test('удар сокирою — chop гравцю, що бив; дрова в склад — store штуками', () => {
  const s = R.newGame(7, { ...R.defaultParams(), treeHits: 3, hitMs: 50 }, 2);
  const pl = at(s, 1, 20, 10);
  pl.hold = { k: I.AXE, n: 1 };
  s.obj[id(s, 21, 10)] = O.TREE; s.hp[id(s, 21, 10)] = 3;
  for (let i = 0; i < 6; i++) R.step(s, [{ p: 1, k: 'use' }]);
  assert.equal(s.stats.pl[1].chop, 3);
  assert.equal(s.stats.pl[0]?.chop ?? 0, 0);
  const w = R.wagonPos(s, 1), wx = Math.floor(w.x / U), wy = Math.floor(w.y / U);
  const p0 = at(s, 0, wx, wy - 1, 0, 1);
  p0.hold = { k: I.WOOD, n: 3 };
  R.step(s, [{ p: 0, k: 'use' }]);
  assert.equal(s.stats.pl[0].store, 3);
  assert.equal(s.stats.pl[0].carry, 1);
});

test('відкат мережі: перерахунок тих самих тиків дає ті самі цифри (не губить і не подвоює)', () => {
  // партія ботами — записуємо команди кожного тику, як їх нумерує секретар
  const a = R.newGame(11, R.defaultParams(), 3), bots = [0, 1, 2].map((p) => createBot(p)), log = [];
  while (a.tick < 1500) {
    const cmds = [];
    for (const b of bots) { const v = b.think(a); cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 }); if (v.use) cmds.push({ p: b.p, k: 'use' }); }
    log.push(cmds); R.step(a, cmds);
  }
  // той самий потік, але з 700 гравець 0 «передбачав» без команд гравців 1–2, а тоді відкат до знімка й перерахунок
  const b = R.newGame(11, R.defaultParams(), 3);
  while (b.tick < 700) R.step(b, log[b.tick]);
  const snap = R.clone(b);
  while (b.tick < 900) R.step(b, log[b.tick].filter((c) => c.p === 0));
  const back = R.clone(snap);
  while (back.tick < 1500) R.step(back, log[back.tick]);
  assert.deepEqual(back.stats.pl, a.stats.pl);
  const sum = a.stats.pl.reduce((n, r) => n + r.chop + r.rock + r.rail + r.carry, 0);
  assert.ok(sum > 20, 'боти щось робили: ' + sum);
  assert.ok(a.stats.pl.every((r) => r.ticks > 0 && r.idle <= r.ticks));
  assert.equal(R.hash(back), R.hash(a));
});
