// «Колія» K71 — стрес зсуву вікна нескінченної колії: ≥ 20 зсувів на 3 сідах, 4 боти, усі додатки (shift-lib.mjs).
// На кожному зсуві: інструменти не губляться, гравці й речі на прохідних клітинках, колія цілісна, кинуте в польоті —
// на своїй дузі (x/sx/tx зсунуті разом), гуси у вікні; два прогони — той самий hash; стан не пухне.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { shiftRun, toolCount, passable, isTool } from './shift-lib.mjs';

const { U, G } = R;

function check(s, before) {
  const W = s.w, E = s.endless, L = E.L, at = `тик ${s.tick}, зсув ${E.shift}`;
  assert.equal(toolCount(s), before.tools, `${at}: інструменти загубились`);
  for (let i = 0; i < s.drop.length; i++) if (isTool(s.drop[i])) assert.ok(passable(s, i % W, (i / W) | 0), `${at}: інструмент на непрохідній ${i % W},${(i / W) | 0}`);
  for (const pl of s.players) {
    if (!pl || pl.out) continue;
    const x = Math.floor(pl.x / U), y = Math.floor(pl.y / U);
    assert.ok(passable(s, x, y), `${at}: гравець ${pl.id ?? ''} на непрохідній ${x},${y} (obj ${s.obj[y * W + x]}, g ${s.ground[y * W + x]})`);
    if (pl.aim) assert.ok(pl.aim.x >= 0 && pl.aim.x < W, `${at}: приціл поза вікном`);
  }
  for (let j = 1; j < s.path.length; j++) {
    const a = s.path[j - 1], b = s.path[j];
    assert.equal(Math.abs(a % W - b % W) + Math.abs(((a / W) | 0) - ((b / W) | 0)), 1, `${at}: колія розірвана на ${j}`);
  }
  assert.ok(s.path.every((c) => c >= 0 && c < W * s.h), `${at}: колія поза вікном`);
  assert.ok(s.train.pos >= (R.WAGONS.length - 1) * U && s.train.pos <= s.path.length * U, `${at}: потяг поза колією`);
  for (const f of s.flying || []) {
    assert.ok(f.tx >= 0 && f.tx < W, `${at}: кидок у ${f.tx} поза вікном`);
    assert.equal(f.x, f.sx + Math.trunc((f.tx * U + U / 2 - f.sx) * f.t / f.T), `${at}: кидок зійшов з дуги (x/sx/tx зсунуті не разом)`);
  }
  for (const g of s.geese || []) {
    assert.ok(g.x >= 0 && g.x <= W * U, `${at}: гуска поза вікном x=${g.x}`);
    if (g.mode === 'go') assert.ok(g.tx >= 0 && g.tx < W, `${at}: гуска йде до ${g.tx}`);
  }
  const [a, b] = E.st;
  assert.ok(a.x1 < L && b.x0 >= L && b.x1 < W, `${at}: рамки станцій ${JSON.stringify(E.st)}`);
  if (s.mid) for (const g of s.mid.signs) assert.ok(g.x >= 0 && g.x < W, `${at}: табличка поза вікном`);
}

for (const seed of [1, 11, 42]) {
  test(`K71: сід ${seed} — 20 зсувів з гусьми, кидками, льодом/яром; усе на місці, hash сталий, стан не росте`, () => {
    let ice = 0, pit = 0;
    const a = shiftRun(seed, 12000, { minShifts: 20, check: (s, before) => {
      check(s, before);
      for (const g of s.ground) { if (g === G.ICE || g === G.SNOW) ice++; else if (g === G.CHASM || g === G.FRAME || g === G.BRIDGE) pit++; }
    } });
    assert.equal(a.s.status, 'run', `партія скінчилась: ${a.s.reason}`);
    assert.equal(a.s.endless.shift, 20);
    assert.ok(a.flyAt > 0 && a.geeseAt > 0, `кидок під час зсуву ${a.flyAt}, гуси ${a.geeseAt}`);
    assert.ok(ice > 0 && pit > 0, `зима ${ice}, яр ${pit} клітинок на зсувах`);
    assert.ok(a.maxSize < a.size0 * 1.15, `JSON стану ${a.size0} → ${a.maxSize}`);
    const b = shiftRun(seed, 12000, { minShifts: 20 });
    assert.equal(a.hash, b.hash);
  });
}
