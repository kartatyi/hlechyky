// «Колія» — бот проходить смугу (на тому gen, що зараз у гілці). Друкує довжину партії й причину кінця.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';

export function play(seed, nBots, params = {}, maxMin = 15) {
  const s = R.newGame(seed, { ...R.defaultParams(), ...params }, nBots);
  const bots = Array.from({ length: nBots }, (_, i) => createBot(i));
  const maxT = maxMin * 60 * R.TPS;
  while (s.status === 'run' && s.tick < maxT) {
    const cmds = [];
    for (const b of bots) {
      const v = b.think(s);
      cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 });
      if (v.use) cmds.push({ p: b.p, k: 'use' });
    }
    R.step(s, cmds);
  }
  const head = R.wagonPos(s, 0);
  return { s, status: s.status, reason: s.reason, sec: Math.round(s.tick / R.TPS), headX: Math.floor(head.x / R.U), w: s.w, stats: s.stats };
}

const fmt = (r) => `${r.status}${r.reason ? '/' + r.reason : ''} за ${Math.floor(r.sec / 60)}:${String(r.sec % 60).padStart(2, '0')}, голова x=${r.headX}/${r.w}, рейок ${r.stats.railsLaid}, полито ${r.stats.douses}`;

test('бот соло і двоє ботів на 3 сідах: доїжджають далеко (чи до станції)', () => {
  for (const seed of [7, 42, 1001]) {
    for (const n of [1, 2]) {
      const r = play(seed, n);
      console.log(`сід ${seed}, ботів ${n}: ${fmt(r)}`);
      assert.ok(r.status === 'won' || r.headX >= (n === 1 ? 45 : 70), `сід ${seed}/${n}: ${fmt(r)}`);
    }
  }
});

test('бот детермінований: дві однакові партії — однаковий hash', () => {
  const a = play(9, 2, {}, 2), b = play(9, 2, {}, 2);
  assert.equal(R.hash(a.s), R.hash(b.s));
});
