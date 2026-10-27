// «Колія» K50 — досягнення: тригери з s.ev і стану. node --test tests/koliya-js/achievements.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { ACH, createAch, STORE_KEY } from '../../web/games/koliya/achievements.js';

const { U, G, O, I, TPS } = R;
const mem = () => { let v = null; return { get: () => v, set: (x) => { v = x; }, raw: () => v }; };
function blank(params = {}, n = 2) {
  const p = { ...R.defaultParams(), forest: 0, rocks: 0, water: 0, winter: 'off', startSec: 90, ...params };
  const s = R.newGame(7, p, n);
  for (let i = 0; i < s.w * s.h; i++) {
    const y = (i / s.w) | 0;
    if (y > 0 && y < s.h - 1) { s.obj[i] = O.NONE; s.hp[i] = 0; s.drop[i] = I.NONE; s.dropN[i] = 0; if (s.ground[i] === G.WATER) s.ground[i] = G.GRASS; }
  }
  return s;
}
const at = (s, n, cx, cy, fx = 1, fy = 0) => { const pl = s.players[n]; pl.x = cx * U + U / 2; pl.y = cy * U + U / 2; pl.fx = fx; pl.fy = fy; return pl; };
const hold = (pl, k, n = 1) => { pl.hold.k = k; pl.hold.n = n; };
/** крок правил + досягнення; повертає id нових */
const step = (a, s, cmds = [], ctx = {}) => { R.step(s, cmds); return a.tick(s.ev, s, ctx).map((x) => x.id); };
const run = (a, s, n, cmds = [], ctx = {}) => { const out = []; for (let i = 0; i < n; i++) out.push(...step(a, s, i === 0 ? cmds : [], ctx)); return out; };
const fake = (over = {}) => ({ tick: 1, status: 'run', params: { mode: 'normal' }, stats: { railsLaid: 0 }, train: { heat: 0, fire: 0 }, players: [{}, {}], w: 10, h: 5, ground: [], ...over });

test('15 досягнень, id унікальні, у накопичувальних є goal', () => {
  assert.ok(ACH.length >= 15);
  assert.equal(new Set(ACH.map((a) => a.id)).size, ACH.length);
  for (const id of ['fireman', 'railman']) assert.ok(ACH.find((a) => a.id === id).goal > 0);
});

test('данк: кинуте дерево в склад — «Данк у вагон», пам\'ять у store', () => {
  const st = mem(), a = createAch({ store: st });
  const s = blank({ throw: 'on' }, 1);
  const w = R.wagonPos(s, 1), wx = Math.floor(w.x / U), wy = Math.floor(w.y / U);
  hold(at(s, 0, wx, wy - 3, 0, 1), I.WOOD, 3);
  const got = run(a, s, 21, [{ p: 0, k: 'throw' }]);
  assert.equal(s.train.wood, 3);
  assert.deepEqual(got, ['dunk']);
  assert.ok(JSON.parse(st.raw()).got.dunk);
  // той самий store — вже відкрите, вдруге не тостить
  const b = createAch({ store: st });
  assert.equal(b.list().find((x) => x.id === 'dunk').got, true);
});

test('звичайне «покласти в склад» руками — не данк; бот — не рахується', () => {
  const a = createAch({ store: mem() });
  const s = blank({ throw: 'on' }, 2);
  const w = R.wagonPos(s, 1), wx = Math.floor(w.x / U), wy = Math.floor(w.y / U);
  hold(at(s, 0, wx, wy - 1, 0, 1), I.WOOD, 2);
  assert.deepEqual(run(a, s, 3, [{ p: 0, k: 'use' }]), []);
  assert.equal(s.train.wood, 2);
  hold(at(s, 1, wx, wy - 3, 0, 1), I.WOOD, 2);
  at(s, 0, 3, 3);
  assert.deepEqual(run(a, s, 21, [{ p: 1, k: 'throw' }], { bot: (p) => p === 1 }), [], 'бот');
});

test('міст: дерево на воду — «Міст через річку»', () => {
  const a = createAch({ store: mem() });
  const s = blank({}, 1);
  const X = 30, Y = 3;
  s.ground[Y * s.w + X + 1] = G.WATER;
  hold(at(s, 0, X, Y, 1, 0), I.WOOD, 1);
  assert.deepEqual(run(a, s, 2, [{ p: 0, k: 'use' }]), ['bridge']);
  assert.equal(s.ground[Y * s.w + X + 1], G.BRIDGE);
});

test('пожежник: 5 гасінь за кілька партій; бац — стан і подія stun', () => {
  const a = createAch({ store: mem() });
  let got = [];
  for (let k = 0; k < 5; k++) {
    const s = fake({ tick: 1 });
    got.push(...a.tick([{ t: 'douse', x: 0, y: 0, p: 0 }], s).map((x) => x.id));
  }
  assert.deepEqual(got, ['fireman']);
  assert.equal(a.list().find((x) => x.id === 'fireman').n, 5);
  const s = fake();
  assert.deepEqual(a.tick([{ t: 'stun', x: 0, y: 0, p: 1, by: 0 }], s).map((x) => x.id), ['bonk']);
});

test('тик рахуємо раз: той самий тик удруге (мережевий повтор) — без подвоєння', () => {
  const a = createAch({ store: mem() });
  const s = fake({ tick: 5 });
  a.tick([{ t: 'douse', p: 0 }], s); a.tick([{ t: 'douse', p: 0 }], s);
  assert.equal(a.list().find((x) => x.id === 'fireman').n, 1);
});

test('колійник: найвища вода railsLaid — розібрав і поклав знову не рахується; 100 за партії', () => {
  const a = createAch({ store: mem() });
  const s = fake({ tick: 1, stats: { railsLaid: 0 } });
  a.tick([], s);
  s.tick = 2; s.stats.railsLaid = 60; a.tick([], s);
  s.tick = 3; s.stats.railsLaid = 59; a.tick([], s);
  s.tick = 4; s.stats.railsLaid = 60; a.tick([], s);
  assert.equal(a.list().find((x) => x.id === 'railman').n, 60);
  const s2 = fake({ tick: 1, stats: { railsLaid: 0 } });
  a.tick([], s2);
  s2.tick = 2; s2.stats.railsLaid = 40;
  assert.deepEqual(a.tick([], s2).map((x) => x.id), ['railman']);
});

test('перемога: перша станція, без пожеж, маршрут дня, чумак, удвох за 5 хв, гуска не вкрала', () => {
  const a = createAch({ store: mem() });
  const s = fake({ tick: 10, geese: [{ id: 1, hold: 0 }] });
  a.tick([], s);
  s.tick = 11; s.status = 'won';
  const got = a.tick([{ t: 'won' }], s, { daily: true, chumak: true }).map((x) => x.id).sort();
  assert.deepEqual(got, ['chumak', 'daily', 'duo5', 'first', 'goose', 'nofire'].sort());
  s.tick = 12;
  assert.deepEqual(a.tick([], s), [], 'фінал — раз');
});

test('перемога з перегрівом, крадіжкою гуски, ботом-напарником і довго — лише «Перша станція»', () => {
  const a = createAch({ store: mem() });
  const s = fake({ tick: 10, geese: [{ id: 1, hold: I.AXE }], train: { heat: 1000, fire: 3 } });
  a.tick([], s);
  s.tick = 6 * 60 * TPS; s.status = 'won'; s.train.heat = 0; s.train.fire = 0; s.geese = [];
  const got = a.tick([], s, { bot: (p) => p === 1 }).map((x) => x.id);
  assert.deepEqual(got, ['first']);
});

test('навчання: перемога — лише «Навчання пройдено»; нескінченна — 5 станцій', () => {
  const a = createAch({ store: mem() });
  const s = fake({ tick: 3, status: 'won', params: { mode: 'tutorial' } });
  assert.deepEqual(a.tick([], s).map((x) => x.id), ['tutorial']);
  const e = fake({ tick: 3, endless: { stations: 4 } });
  assert.deepEqual(a.tick([{ t: 'station', p: 4 }], e).map((x) => x.id), ['first']);
  e.tick = 4; e.endless.stations = 5;
  assert.deepEqual(a.tick([{ t: 'station', p: 5 }], e).map((x) => x.id), ['endless5']);
});

test('сховище зламане (приватне вікно) — не падає; count/reset', () => {
  const a = createAch({ store: { get() { throw new Error('x'); }, set() { throw new Error('x'); } } });
  assert.deepEqual(a.tick([{ t: 'catch', p: 0, from: 1 }, { t: 'scare', p: 0 }], fake()).map((x) => x.id), ['catch', 'scare']);
  assert.deepEqual(a.count(), [2, ACH.length]);
  a.reset(); assert.deepEqual(a.count(), [0, ACH.length]);
  assert.equal(STORE_KEY, 'kol-ach');
});
