// «Колія» K22 — навчальна смуга: розкладка, гачок у newGame, бот проходить соло й удвох, підказки йдуть по черзі.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';
import { pickTips } from '../../web/games/koliya/tips.js';
import { findRoute } from '../../web/games/koliya/gen.js';
import { TUTOR_MAP, TUTOR_PARAMS, tutorialWorld, tutorialParams } from '../../web/games/koliya/tutorial.js';

const TUT = { ...R.defaultParams(), mode: 'tutorial' };

function play(nBots, maxMin = 15) {
  const s = R.newGame(1, TUT, nBots);
  const bots = Array.from({ length: nBots }, (_, i) => createBot(i));
  const mem = {}, order = [];   // перша поява кожної підказки (будь-кому)
  while (s.status === 'run' && s.tick < maxMin * 60 * R.TPS) {
    const cmds = [];
    for (const b of bots) {
      const v = b.think(s);
      cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 });
      if (v.use) cmds.push({ p: b.p, k: 'use' });
    }
    R.step(s, cmds);
    for (const t of pickTips(s, mem)) if (!order.includes(t.id)) order.push(t.id);
  }
  return { s, order, sec: Math.round(s.tick / R.TPS) };
}

test('мапа: рядки однакові, стіни по краях, інструменти, станція, маршрут через одну річку', () => {
  const w = TUTOR_MAP[0].length;
  for (const r of TUTOR_MAP) assert.equal(r.length, w);
  assert.ok(w >= 50 && w <= 60, `довжина ${w}`);
  const wd = tutorialWorld(tutorialParams(R.defaultParams()));
  assert.equal(wd.w, w); assert.equal(wd.h, TUTOR_MAP.length);
  assert.equal(wd.path.length, 6);
  for (const k of [R.I.AXE, R.I.PICK, R.I.BUCKET]) assert.ok(wd.drop.includes(k), `інструмент ${k}`);
  assert.equal(wd.ground.filter((g) => g === R.G.STATION).length, 9);
  const route = findRoute(wd);
  assert.ok(route, 'маршрут є');
  assert.equal(route.filter((i) => wd.ground[i] === R.G.WATER).length, 1, 'один місток');
  // дерев і скель на всю колію (рейка = дерево + залізо, railsPerCraft рейок; + місток)
  const P = R.defaultParams();
  const trees = wd.obj.filter((o) => o === R.O.TREE).length, rocks = wd.obj.filter((o) => o === R.O.ROCK).length;
  const crafts = Math.ceil((route.length - P.startRails) / P.railsPerCraft);
  assert.ok(trees * P.woodPerTree >= crafts + 1 && rocks * P.ironPerRock >= crafts, `дерев ${trees}, скель ${rocks}, виробів ${crafts}`);
});

test('гачок newGame: mode=tutorial — своя смуга й повільні числа незалежно від сіду; звичайна — як була', () => {
  const a = R.newGame(1, TUT, 1), b = R.newGame(999, TUT, 1);
  assert.equal(a.w, TUTOR_MAP[0].length);
  assert.deepEqual([...a.obj], [...b.obj]);
  for (const k in TUTOR_PARAMS) assert.equal(a.params[k], TUTOR_PARAMS[k], k);
  assert.equal(a.params.overheat, 'stop');
  assert.ok(a.zones.length >= 4);
  const n = R.newGame(7, R.defaultParams(), 1);
  assert.equal(n.params.mode, 'normal');
  assert.equal(n.w, R.PARAM_META.stripLen.def);
  assert.deepEqual(R.VARIANTS.mode.values, ['normal', 'tutorial']);
});

test('бот соло проходить навчальну смугу, підказки йдуть по черзі', () => {
  const r = play(1);
  console.log(`навчання соло: ${r.s.status} за ${r.sec} с, рейок ${r.s.stats.railsLaid}, полито ${r.s.stats.douses}; підказки: ${r.order.join(' → ')}`);
  assert.equal(r.s.status, 'won');
  for (const id of ['axe', 'chop', 'store', 'pick', 'mine', 'bridge', 'heat']) assert.ok(r.order.includes(id), `підказка ${id}`);
  const at = (id) => r.order.indexOf(id);
  assert.ok(at('axe') < at('chop') && at('chop') < at('store') && at('pick') < at('mine') && at('mine') < at('bridge') && at('bridge') < at('heat'),
    r.order.join(' → '));
});

test('двоє ботів теж проходять', () => {
  const r = play(2);
  console.log(`навчання удвох: ${r.s.status} за ${r.sec} с; підказки: ${r.order.join(' → ')}`);
  assert.equal(r.s.status, 'won');
  assert.ok(r.order[0] === 'role');
});
