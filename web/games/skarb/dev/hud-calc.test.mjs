// node --test web/games/skarb/dev/hud-calc.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { makeView, makeEvent, SCENARIOS, legalFor, applyAct } from './fixture.js';
import {
  isSym, maskOf, pathTargets, rotFor, cardInfo, brokenText, seatTargets, whatNow, stepCursor, cycle, goldRows, goldText,
  titles, roundBars, tipTarget,
} from '../hud-calc.js';

const nick = (v) => (s) => (v.players.find((p) => p.seat === s) || {}).nick || '?';

test('симетрія й маски', () => {
  for (const s of ['x', 'v', 'h', 'dx', 'dv', 'dh']) assert.ok(isSym(s), s);
  for (const s of ['tv', 'th', 'ce', 'cw', 'dtv', 'dth', 'dce', 'dcw', 'ds', 'de']) assert.ok(!isSym(s), s);
  assert.equal(maskOf('th', 0), 14);
  assert.equal(maskOf('th', 1), 11);
  assert.equal(maskOf('tv', 1), 13);
});

test('поле з §5: законні місця «th» містять три з контракту', () => {
  const v = makeView('play', 0);
  const L = v.me.legal['12'] || v.me.legal[12];
  for (const want of [[2, -1, 0], [3, -1, 1], [4, 1, 1]]) assert.ok(L.some((x) => x.join() === want.join()), 'нема ' + want);
  // Глуха з тією самою маскою лягає так само.
  assert.deepEqual(v.me.legal[33], L);
  assert.deepEqual(v.me.legal[44], [1, 2, 4, 5, 6]);
  assert.deepEqual(v.me.legal[55], [[3, 'lamp']]);
  assert.deepEqual(v.me.legal[60], [0, 1]);
});

test('pathTargets: поточний поворот, alt — лише інший, симетрична — без alt', () => {
  const list = [[2, -1, 0], [3, -1, 1], [4, 1, 1]];
  const a = pathTargets(list, 'th', 0);
  assert.deepEqual(a.cells, [[2, -1]]);
  assert.deepEqual(a.alt, [[3, -1], [4, 1]]);
  assert.deepEqual(a.ghost, { m: 14, mAlt: 11, c: true });
  const b = pathTargets(list, 'th', 1);
  assert.deepEqual(b.cells, [[3, -1], [4, 1]]);
  assert.deepEqual(b.alt, [[2, -1]]);
  const both = pathTargets([[5, 5, 0], [5, 5, 1]], 'th', 1);
  assert.deepEqual(both.cells, [[5, 5]]);
  assert.deepEqual(both.alt, []);
  const sym = pathTargets([[1, 1, 0]], 'x', 1);
  assert.deepEqual(sym.cells, [[1, 1]]);
  assert.equal(sym.ghost.m, 15);
  assert.equal(rotFor(list, 'th', 0, 3, -1), 1);
  assert.equal(rotFor(list, 'th', 0, 2, -1), 0);
  assert.equal(rotFor(list, 'th', 0, 9, 9), null);
  assert.equal(rotFor([[1, 1, 0]], 'x', 1, 1, 1), 0);
});

test('підписи карт', () => {
  assert.equal(cardInfo({ k: 'path', s: 'th' }).name, 'Трійник лежачий');
  assert.equal(cardInfo({ k: 'break', tool: 'cart' }).name, 'Тачка без колеса');
  assert.equal(cardInfo({ k: 'fix', tools: ['pick', 'lamp'] }).name, 'Кирка й лампа');
  assert.equal(cardInfo({ k: 'map' }).icon, '🗺');
  assert.equal(brokenText(['lamp', 'cart']), 'Лампу розбито');
  assert.equal(brokenText([]), '');
  const t = seatTargets({ k: 'fix', tools: ['pick', 'lamp'] }, [[0, 'pick'], [0, 'lamp'], [3, 'lamp']]);
  assert.deepEqual(t.get(0), ['pick', 'lamp']);
  assert.deepEqual(t.get(3), ['lamp']);
});

test('що зараз', () => {
  const v = makeView('play', 0);
  assert.match(whatNow(v, v.me, null, nick(v)).l, /Твій хід: поклади хід від колодязя/);
  const b = makeView('broken', 0);
  assert.match(whatNow(b, b.me, null, nick(b)).l, /Кирку зламано: ходу не покладеш/);
  const o = makeView('other', 0);
  assert.equal(whatNow(o, o.me, null, nick(o)).s, 'Копає Тарас');
  assert.match(whatNow(o, o.me, null, nick(o)).l, /Зараз копає Тарас/);
  const sp = makeView('spectator');
  assert.match(whatNow(sp, null, null, nick(sp)).l, /Дивишся збоку/);
  assert.equal(whatNow(makeView('round', 0), null, null, nick(v)).s, 'Підсумок раунду');
  const ov = makeView('over', 2);
  assert.equal(whatNow(ov, ov.me, null, nick(ov)).s, 'Перемога! 🏆');
  assert.equal(whatNow(makeView('lobby'), null, null, () => '').s, '');
  const sel = { card: v.me.hand[2], r: 0 };
  assert.match(whatNow(v, v.me, sel, nick(v)).l, /Кому зламати лампу/);
});

test('курсор', () => {
  const T = [[2, -1], [3, -1], [4, 1]];
  assert.deepEqual(stepCursor(T, null, 'E'), [2, -1]);
  assert.deepEqual(stepCursor(T, [2, -1], 'E'), [3, -1]);
  assert.deepEqual(stepCursor(T, [3, -1], 'S'), [4, 1]);
  assert.deepEqual(stepCursor(T, [4, 1], 'S'), [4, 1]);   // далі нема — стоїмо
  assert.deepEqual(cycle(T, [4, 1], 1), [2, -1]);
  assert.deepEqual(cycle(T, null, -1), [4, 1]);
});

test('підсумок раунду й партії', () => {
  const v = makeView('round', 0);
  const rows = goldRows(v.roundEnd, v.order);
  assert.equal(rows.length, 7);
  assert.equal(rows.find((r) => r.seat === 4).n, 5);
  assert.equal(rows.find((r) => r.seat === 1).n, 0);
  assert.equal(rows[4].total, 14);
  assert.equal(goldText(1), '1 злиток');
  assert.equal(goldText(3), '3 злитки');
  assert.equal(goldText(11), '11 злитків');
  const o = makeView('over', 0);
  const T = titles(o.over.stats);
  assert.equal(T.length, 3);
  assert.equal(T[0].name, 'Золота лопата');
  assert.equal(T[0].seat, 4);
  assert.equal(T[1].name, 'Підземний лис');
  const bars = roundBars(o.over.stats.timeline, 7);
  assert.deepEqual(bars.bars[2], [4, 2, 1]);
  assert.equal(bars.max, 7);
});

test('порада Глека', () => {
  const v = makeView('play', 0);
  const t = tipTarget(v.me.tip, v.me.hand);
  assert.equal(t.kind, 'cell');
  assert.equal(t.r, 1);
  assert.equal(tipTarget({ card: 60, goal: 1 }, v.me.hand).kind, 'goal');
  assert.equal(tipTarget({ card: 999, pass: true }, v.me.hand), null);
});

test('фікстура: усі сценарії збираються, події мають поля', () => {
  for (const s of SCENARIOS) {
    const v = makeView(s, 0);
    assert.ok(v.grid && Array.isArray(v.players), s);
    if (v.phase) assert.equal(v.board[0].s, 'start', s);
  }
  assert.equal(makeView('ten', 0).players.length, 10);
  assert.equal(makeEvent('place', { s: 'tv', r: 1 }).m, 13);
  const v = makeView('play', 0);
  const r = applyAct(v, 0, 'play', { card: 12, x: 2, y: -1, r: 0 });
  assert.equal(r.error, null);
  assert.ok(r.view.board.some((b) => b.x === 2 && b.y === -1));
  assert.ok(r.view.events.some((e) => e.k === 'place' && e.seq > v.seq));
  const n = makeView('near', 0);
  assert.ok(legalFor(n, 0, n.me.hand).legal[4].some(([x, y]) => x === 7 && y === 0));
});
