// «Колія» K35 — повтор партії: запис → програвання дає той самий hash; код повтору туди-назад; розмір коду.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';
import * as P from '../../web/games/koliya/replay.js';

// як main.js tickOnce: локальні 'in' щотику (навіть незмінні) + use; боти — так само
function record(seed, nBots, { params = {}, maxT = 8 * 60 * R.TPS, human = null } = {}) {
  const prm = { ...R.defaultParams(), ...params };
  const s = R.newGame(seed, prm, nBots + (human ? 1 : 0));
  const rec = P.recBegin(seed, prm, s.players.length, { local: human ? 1 : 0 });
  const off = human ? 1 : 0;
  const bots = Array.from({ length: nBots }, (_, i) => createBot(i + off));
  while (s.status === 'run' && s.tick < maxT) {
    const cmds = [];
    if (human) for (const c of human(s)) cmds.push(c);
    for (const b of bots) {
      const v = b.think(s);
      cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 });
      if (v.use) cmds.push({ p: b.p, k: 'use' });
    }
    P.recordTick(rec, s, cmds);
    R.step(s, cmds);
  }
  P.recFinish(rec, s);
  return { s, rec };
}

// «живий» гравець з аналоговим стіком: намір міняється майже щотику, use натисками, зрідка кидок/гудок
function stickHuman() {
  let a = 0, u = 0;
  return (s) => {
    a += 0.07 + ((s.tick * 7919) % 13) / 200;
    const out = [{ p: 0, k: 'in', mx: Math.round(Math.cos(a) * 100), my: Math.round(Math.sin(a * 0.7) * 100) }];
    if ((s.tick >> 3) % 3 === 0) { out.push({ p: 0, k: 'use' }); u++; }
    if (s.tick % 401 === 0) out.push({ p: 0, k: 'horn' });
    if (s.tick % 557 === 0) out.push({ p: 0, k: 'throw' });
    return out;
  };
}

test('запис → програвання: той самий hash на 5 сідах з ботами', () => {
  for (const [seed, n] of [[7, 2], [42, 1], [1001, 3], [19, 2], [53, 4]]) {
    const { s, rec } = record(seed, n, { maxT: 4 * 60 * R.TPS });
    const r = P.playAll(rec);
    const v = P.verify(rec, r);
    console.log(`сід ${seed}, ботів ${n}: ${s.status} на тику ${s.tick}, тиків з командами ${rec.ticks.length}, hash ${rec.hash}`);
    if (s.status === 'run') assert.equal(R.hash(r), R.hash(s));   // обрізано за часом: звіряємо стан напряму
    else assert.ok(v.ok, `сід ${seed}: ${JSON.stringify(v)}`);
    assert.equal(r.tick, s.tick);
  }
});

test('повтор розійшовся — verify каже ok: false, не падає', () => {
  const { rec } = record(7, 2, { maxT: 3000 });
  rec.end = 3000; rec.hash = 12345;
  const r = P.playAll(rec);
  assert.equal(P.verify(rec, r).ok, false);
  // зіпсована команда посеред партії
  const { rec: b } = record(7, 2, { maxT: 3 * 60 * R.TPS });
  const j = b.ticks.findIndex(([, c]) => c.some((x) => x.k === 'use'));
  b.ticks.splice(j, 1);
  assert.doesNotThrow(() => P.verify(b, P.playAll(b)));
});

test('перемотка назад (точки відновлення) дає той самий стан', () => {
  const { rec } = record(42, 2, { maxT: 2000 });
  const a = P.createPlayer(rec, { every: 300 });
  a.seek(1500); const h1500 = R.hash(a.s);
  a.seek(1900); a.seek(700); const h700 = R.hash(a.s);
  const b = P.createPlayer(rec); b.seek(700);
  assert.equal(h700, R.hash(b.s));
  a.seek(1500); assert.equal(R.hash(a.s), h1500);
});

test('код повтору: кодування/декодування дає ту саму партію (і з позою й h з мережі)', async () => {
  const { s, rec } = record(19, 2, { maxT: 3 * 60 * R.TPS, human: stickHuman(), params: { overheat: 'stop' } });
  const code = await P.encode(rec);
  assert.match(code, /^K1z?\./);
  const back = await P.decode('  ' + code + '\n');
  assert.equal(back.seed, rec.seed); assert.equal(back.n, rec.n); assert.equal(back.end, rec.end); assert.equal(back.hash, rec.hash);
  assert.equal(back.params.overheat, 'stop');
  assert.deepEqual(back.ticks, rec.ticks);
  assert.equal(R.hash(P.playAll(back)), R.hash(s));
  // мережеві команди: поза, затиснутий use, невідомий вид, join
  const net = P.recBegin(5, {}, 2, { net: true });
  net.ticks = [[0, [{ p: 1, k: 'use', x: 1500, y: -20, fx: 1, fy: 0, h: 1 }, { k: 'join' }]], [9, [{ p: 3, k: 'k9' }, { p: 2, k: 'throw', fx: 0, fy: -1 }]]];
  const nb = P.decodeBytes(P.encodeBytes(net));
  assert.deepEqual(nb.ticks, [[0, [{ p: 1, k: 'use', x: 1500, y: -20, fx: 1, fy: 0, h: 1 }, { k: 'join' }]], [9, [{ p: 3, k: 'k9' }, { p: 2, k: 'throw', fx: 0, fy: -1 }]]]);
  assert.equal(nb.net, true);
  await assert.rejects(P.decode('привіт'));
  await assert.rejects(P.decode('K1z.AAAA'));
});

// клавіатурний гравець: 8 напрямків + стоп, міняє кожні 0,4–2 с, use — серіями
function keysHuman() {
  let dir = 0, next = 0, useT = 0;
  const D = [[0, 0], [100, 0], [100, 100], [0, 100], [-100, 100], [-100, 0], [-100, -100], [0, -100], [100, -100]];
  return (s) => {
    const r = (s.tick * 2654435761) >>> 0;
    if (s.tick >= next) { dir = r % 9; next = s.tick + 8 + (r >>> 8) % 32; if ((r >>> 4) % 3 === 0) useT = s.tick + 10 + (r >>> 12) % 30; }
    const out = [{ p: 0, k: 'in', mx: D[dir][0], my: D[dir][1] }];
    if (s.tick < useT) out.push({ p: 0, k: 'use' });
    if (s.tick % 401 === 0) out.push({ p: 0, k: 'horn' });
    return out;
  };
}

test('розмір коду 8-хв партії: 4 гравці (живий + 3 боти) — до ~20 КБ', async () => {
  const prm = { overheat: 'stop', speedStart: 0.05, speedMax: 0.1 };   // повільний паротяг — партія на всі 8 хв
  for (const [name, human, lim] of [['клавіатура', keysHuman(), 20480], ['аналоговий стік (найгірше)', stickHuman(), 40000]]) {
    const { s, rec } = record(8, 3, { human, params: prm });
    const code = await P.encode(rec);
    console.log(`${name}: партія ${Math.round(s.tick / R.TPS)} с (${s.status}), тиків з командами ${rec.ticks.length}, сирі ${P.encodeBytes(rec).length} Б, код ${code.length} символів`);
    assert.ok(s.tick >= 7 * 60 * R.TPS, `партія коротка: ${s.tick}`);
    assert.ok(code.length <= lim, `${name}: код ${code.length}`);
    assert.equal(R.hash(P.playAll(await P.decode(code))), R.hash(s));
  }
});

test('збережені: останні 3 + найкраща, ліміт розміру', () => {
  const mem = new Map(), ls = { getItem: (k) => mem.get(k) ?? null, setItem: (k, v) => mem.set(k, v) };
  let st = P.loadStore(ls);
  const mk = (end, status) => ({ seed: 1, n: 1, local: 1, end, status, reason: '', params: R.defaultParams(), stations: 0, cells: end });
  P.pushStore(st, 'K1.a', mk(900, 'won'));
  for (let i = 0; i < 4; i++) P.pushStore(st, 'K1.' + i, mk(2000 + i, 'lost'));
  assert.ok(P.saveStore(st, ls));
  st = P.loadStore(ls);
  assert.equal(st.last.length, 3); assert.equal(st.best.code, 'K1.a');
  P.pushStore(st, 'K1.b', mk(800, 'won')); assert.equal(st.best.code, 'K1.b');
  P.pushStore(st, 'K1.' + 'x'.repeat(1_100_000), mk(700, 'won'));
  assert.ok(JSON.stringify(st).length <= 1_000_000);
  mem.set(P.STORE_KEY, '{зламане'); assert.deepEqual(P.loadStore(ls), { last: [], best: null });
});
