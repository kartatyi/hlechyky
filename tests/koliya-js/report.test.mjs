// Звіт партії (report.js, 27.10): підсумок зі стану; кінець партії — один звіт; нова партія посеред живої — «restart»;
// коротке клацання, глядач і ?report=0 — без звіту. Відправку (sendBeacon) підмінено.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';

globalThis.location = { search: '' };
Object.defineProperty(globalThis, 'navigator', { value: { userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/141.0 Safari/537.36', hardwareConcurrency: 8 },
  configurable: true, writable: true });
globalThis.document = { hidden: false, documentElement: { classList: { contains: () => false } }, addEventListener: () => { } };
globalThis.addEventListener = () => { };
Object.assign(globalThis, { innerWidth: 1280, innerHeight: 800, devicePixelRatio: 1 });
const { buildReport, createReport, MIN_SEC } = await import('../../web/games/koliya/report.js');

const play = (s, ticks) => { for (let i = 0; i < ticks && s.status === 'run'; i++) R.step(s, []); return s; };

test('підсумок зі стану: кінець, режим, секунди, поступ до станції, лише змінені повзунки', () => {
  const s = play(R.newGame(8, { ...R.defaultParams(), speedStart: 0.3 }, 2), 20 * R.TPS);
  const r = buildReport(s, { end: 'run', local: 1, bots: 1 });
  assert.equal(r.end, 'run');
  assert.equal(r.mode, 'normal');
  assert.equal(r.sec, 20);
  assert.equal(r.pl, 2);
  assert.deepEqual([r.local, r.bots], [1, 1]);
  assert.deepEqual(r.prm, { speedStart: 0.3 });
  assert.ok(r.prog >= 0 && r.prog <= 1, String(r.prog));
  for (const k of ['rails', 'crafted', 'wood', 'iron', 'hits', 'douses', 'bridges']) assert.equal(typeof r.st[k], 'number', k);
  assert.ok(JSON.stringify(r).length < 2000);
  const t = buildReport(R.newGame(1, { ...R.defaultParams(), mode: 'tutorial' }, 1), { daily: true });
  assert.equal(t.mode, 'tutorial');
  assert.ok(!('mode' in t.prm), 'режим — окремим полем, не в повзунках');
  assert.equal(buildReport(R.newGame(5, R.defaultParams(), 1), { daily: true }).mode, 'daily');
});

test('без мережі: кінець партії — один звіт; «ще раз» посеред живої — restart; коротке клацання — нічого', () => {
  const sent = [];
  const game = { local: 1, bots: 0, state: null };
  const rep = createReport(game, { send: (r) => sent.push(r), prefs: { tips: true } });
  let now = 1000;
  const run = (s, sec) => { for (let i = 0; i < sec * 10; i++) { now += 100; if (i % 2 === 0) play(s, 4); rep.frame(now, s); } };

  const a = game.state = R.newGame(8, R.defaultParams(), 1);
  run(a, 3);
  const b = game.state = R.newGame(9, R.defaultParams(), 1);   // клацнули «ще раз» за 3 с — не партія
  rep.frame(now += 16, b);
  assert.equal(sent.length, 0);
  run(b, MIN_SEC + 4);
  const c = game.state = R.newGame(10, R.defaultParams(), 1);
  rep.frame(now += 16, c);
  assert.equal(sent.length, 1);
  assert.equal(sent[0].end, 'restart');
  assert.equal(sent[0].seed, 9);
  assert.ok(sent[0].sec >= MIN_SEC, String(sent[0].sec));
  assert.ok(sent[0].fps.avg > 0 && sent[0].dev.ua === 'Chrome · Windows', JSON.stringify(sent[0]));

  run(c, 2);
  c.status = 'lost'; c.reason = 'derail';
  for (let i = 0; i < 5; i++) rep.frame(now += 16, c);
  assert.equal(sent.length, 2, 'кінець — рівно один звіт');
  assert.deepEqual([sent[1].end, sent[1].why, sent[1].seed], ['lost', 'derail', 10]);
  const d = game.state = R.newGame(11, R.defaultParams(), 1);   // після кінця «ще раз» — не restart (звіт уже пішов)
  rep.frame(now += 16, d);
  assert.equal(sent.length, 2);
});

test('мережа: звіт шле той, хто сидить (з пінгом і столом), глядач — ні; партію впізнаємо за епохою', () => {
  for (const p of [0, -1]) {
    const sent = [];
    const net = { p, p2: -1, epoch: 3, room: 'abc', ping: 80, desync: -1, wait: false, info: () => ({ stats: { rebuilds: 4, resyncs: 1, dropped: 0 }, lag: 0 }) };
    const game = { local: 1, bots: 0, names: ['Оля', 'Петро'], state: null };
    const rep = createReport(game, { net, send: (r) => sent.push(r) });
    const s = game.state = R.newGame(8, R.defaultParams(), 2);
    let now = 0;
    for (let i = 0; i < 80; i++) { now += 100; if (i % 2 === 0) play(s, 4); rep.frame(now, s); }
    s.status = 'won';
    rep.frame(now += 16, s);
    if (p < 0) { assert.equal(sent.length, 0, 'глядач — без звіту'); continue; }
    assert.equal(sent.length, 1);
    const n = sent[0].net;
    assert.deepEqual([n.room, n.e, n.p, n.me, n.ping, n.pingMax, n.rebuilds, n.names], ['abc', 3, 0, 'Оля', 80, 80, 4, 2]);
    assert.equal(sent[0].end, 'won');
    // перезапуск посеред: стан той самий об'єкт (мережа копіює світ на місці), нову партію видно лише з епохи
    net.epoch = 4; Object.assign(s, R.newGame(9, R.defaultParams(), 2));
    for (let i = 0; i < 80; i++) { now += 100; if (i % 2 === 0) play(s, 4); rep.frame(now, s); }
    net.epoch = 5;
    rep.frame(now += 16, s);
    assert.equal(sent.length, 2);
    assert.deepEqual([sent[1].end, sent[1].seed, sent[1].net.e], ['restart', 9, 4], 'епоха — тієї партії, що скінчилась');
  }
});

test('мережа: кінець — з остаточного світу C; передбачена «поразка», яку відкіт скасує, звіту не дає', () => {
  const sent = [];
  const s = R.newGame(8, R.defaultParams(), 2), C = R.newGame(8, R.defaultParams(), 2);
  const net = { p: 0, p2: -1, epoch: 1, room: 'r', ping: 50, desync: -1, wait: false, C, info: () => ({ stats: {}, lag: 0 }) };
  const game = { local: 1, bots: 0, names: ['Оля'], state: s };
  const rep = createReport(game, { net, send: (r) => sent.push(r) });
  let now = 0;
  for (let i = 0; i < 60; i++) { now += 100; if (i % 2 === 0) { play(s, 4); play(C, 4); } rep.frame(now, s); }
  s.status = 'lost'; s.reason = 'derail';   // передбачене: друг ще не «поклав» рейку
  for (let i = 0; i < 5; i++) rep.frame(now += 16, s);
  assert.equal(sent.length, 0, 'звіт за передбаченим світом');
  s.status = 'run'; s.reason = '';          // відкіт: рейка таки лягла
  C.status = 'won';
  rep.frame(now += 16, s);
  assert.equal(sent.length, 1);
  assert.equal(sent[0].end, 'won');
});

test('?report=0 — не шле нічого', () => {
  globalThis.location = { search: '?report=0' };
  try {
    const sent = [];
    const game = { local: 1, bots: 0, state: null };
    const rep = createReport(game, { send: (r) => sent.push(r) });
    const s = game.state = R.newGame(8, R.defaultParams(), 1);
    rep.frame(100, s); s.status = 'lost'; rep.frame(200, s);
    assert.equal(sent.length, 0);
  } finally { globalThis.location = { search: '' }; }
});
