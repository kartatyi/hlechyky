// «Колія» K24 — мережа «наживо» в Node: модель сервера-секретаря (той самий алгоритм нумерації, що Koliya.cs) +
// 3–4 клієнти net.js з ботами bot.js на віртуальному годиннику; випадкові затримки, джитер і перестановки доставки,
// пізній вхід на 1-й хвилині (журнал GET), вихід і вхід на звільнене місце, перезапуск посеред. Перевіряємо: остаточні
// світи (C) усіх клієнтів мають однаковий hash на кожній звірці, такий самий дає прямий прогін журналу сервера,
// сервер не бачив розбіжності, кожен гравець керує своїм (не вибулим) гравцем rules. Додатки ввімкнені всі.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { createNet, prep, toCmd } from '../../web/games/koliya/net.js';
import { createBot } from '../../web/games/koliya/bot.js';

const PARAMS = { ...R.defaultParams(), geese: 'on', geeseSec: 15, throw: 'on', bump: 'on', stun: 'on', pass: 'on',
  stationMode: 'endless', stripLen: 40, zoneLen: 14, startSec: 8, overheat: 'stop', speedStart: 0.3 };
const ROOM = 'k24';
const HAS_THROW = toCmd([0, 0, 0, 5, 0, 0, 0, 0, 0, 0], 0).k === 'throw';   // K6 дописує throw/horn у журнал

// ---------------- віртуальний час ----------------
let clock = 0, seqN = 0;
const queue = [];   // [{ at, n, fn }] за (at, n)
function at(ms, fn) {
  const e = { at: ms, n: seqN++, fn };
  let lo = 0, hi = queue.length;
  while (lo < hi) { const m = (lo + hi) >> 1; const q = queue[m]; if (q.at < e.at || (q.at === e.at && q.n < e.n)) lo = m + 1; else hi = m; }
  queue.splice(lo, 0, e);
}
const flush = () => new Promise((r) => setImmediate(r));
async function runUntil(t) {
  while (queue.length && queue[0].at <= t) {
    const e = queue.shift();
    clock = Math.max(clock, e.at);
    e.fn();
    await flush();
  }
  clock = Math.max(clock, t);
}
function mulberry(a) {
  return () => { a = (a + 0x6D2B79F5) >>> 0; let t = Math.imul(a ^ (a >>> 15), a | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}

// ---------------- модель сервера: перенесено з src/Hlechyky/Games/Impl/Koliya.cs ----------------
const StepMs = 50, Grace = 6, MaxAhead = 40, KeepTicks = 10, Stride = 10, MaxRules = 4;
const KIND = { in: 1, use: 2, join: 3, leave: 4, throw: 5, horn: 6 };
class GameError extends Error {}
class Srv {
  constructor(rng) {
    this.rng = rng; this.seats = [null, null, null, null];
    this.pOf = [-1, -1, -1, -1]; this.p2Of = [-1, -1, -1, -1]; this.out = []; this.lastT = [0, 0, 0, 0];
    this.hashes = new Map(); this.epoch = 0; this.log = []; this.desyncs = [];
  }
  start() { this.epoch = 0; this.newEpoch((this.rng() * 2 ** 32) >>> 0, {}); }
  newEpoch(seed, prm) {
    this.epoch++; this.seed = seed; this.params = JSON.stringify(prm);
    this.log = []; this.out = []; this.hashes.clear(); this.desync = -1; this.keepFrom = 0; this.w = -1; this.leaveT = 0;
    this.lastT.fill(0); this.n = 0;
    for (let s = 0; s < 4; s++) { this.pOf[s] = this.seats[s] != null && this.n < MaxRules ? this.n++ : -1; if (this.pOf[s] >= 0) this.out.push(false); }
    for (let s = 0; s < 4; s++) { this.p2Of[s] = this.p2Of[s] >= 0 && this.pOf[s] >= 0 && this.n < MaxRules ? this.n++ : -1; if (this.p2Of[s] >= 0) this.out.push(false); }
    this.startAt = clock; this.dirty = true;
  }
  nowTick() { return Math.max(0, Math.floor((clock - this.startAt) / StepMs)); }
  mark() { return (this.w = Math.max(this.w, this.nowTick() - Grace - 1)); }
  onJoin(seat) { if (this.pOf[seat] >= 0) return; const p = this.newPlayer(seat); if (p != null) this.pOf[seat] = p; }
  newPlayer(seat) {
    let p = this.out.indexOf(true);
    if (p < 0 && this.out.length >= MaxRules) return null;
    if (p < 0) { p = this.out.length; this.out.push(false); } else this.out[p] = false;
    this.add(seat, { p, k: KIND.join }, Math.max(this.nowTick(), this.leaveT));   // K24: join не раніше останнього leave
    this.dirty = true;
    return p;
  }
  gone(seat, p) { this.leaveT = Math.max(this.leaveT, this.add(seat, { p, k: KIND.leave }, null)); this.out[p] = true; this.dirty = true; }
  onLeave(seat) {
    if (this.p2Of[seat] >= 0) { this.gone(seat, this.p2Of[seat]); this.p2Of[seat] = -1; }
    if (this.pOf[seat] < 0) return;
    this.gone(seat, this.pOf[seat]); this.pOf[seat] = -1;
  }
  act(seat, action, pl) {
    if (seat == null || seat < 0) throw new GameError('Ти не за столом');
    if (!['restart', 'join2', 'leave2'].includes(action) && Number.isInteger(pl?.e) && pl.e !== this.epoch) { this.stale = (this.stale | 0) + 1; return; }   // K24: стара епоха
    const c = (k, a, b) => { const v = pl?.[k]; if (v === undefined) return Math.min(b, Math.max(a, 0)); if (!Number.isFinite(v)) throw new GameError('Погана команда'); return Math.min(b, Math.max(a, Math.round(v))); };
    const req = Number.isInteger(pl?.t) ? pl.t : null;
    const player = () => { const p = c('q', 0, 1) ? this.p2Of[seat] : this.pOf[seat]; if (p < 0) throw new GameError('нема'); return p; };
    const XY = 10_000_000;
    switch (action) {
      case 'restart': this.newEpoch(pl.seed >>> 0, pl.params || {}); return;
      case 'in': this.add(seat, { p: player(), k: KIND.in, a: c('mx', -100, 100), b: c('my', -100, 100), i: c('i', 0, 2 ** 31 - 1) }, req); return;
      case 'use': this.add(seat, { p: player(), k: KIND.use, a: c('x', 0, XY), b: c('y', 0, XY), c: c('fx', -1, 1), d: c('fy', -1, 1), e: c('h', 0, 1), i: c('i', 0, 2 ** 31 - 1) }, req); return;
      case 'throw': this.add(seat, { p: player(), k: KIND.throw, a: c('x', 0, XY), b: c('y', 0, XY), c: c('fx', -1, 1), d: c('fy', -1, 1), i: c('i', 0, 2 ** 31 - 1) }, req); return;
      case 'horn': this.add(seat, { p: player(), k: KIND.horn, i: c('i', 0, 2 ** 31 - 1) }, req); return;
      case 'hash': {
        const t = c('t', 0, 2 ** 31 - 1), h = pl.h;
        if (t > this.w + 1) return;
        if (this.hashes.has(t)) { if (this.hashes.get(t) !== h && this.desync < 0) { this.desync = t; this.desyncs.push(t); this.dirty = true; } return; }
        this.hashes.set(t, h);
        if (this.hashes.size > 64) this.hashes.delete(Math.min(...this.hashes.keys()));
        return;
      }
      default: throw new GameError('Тут так не ходять');
    }
  }
  add(seat, e, want) {
    const now = this.nowTick();
    let t = Math.max(want ?? now, this.mark() + 1);
    t = Math.min(t, now + MaxAhead);
    t = Math.max(t, this.lastT[seat]);
    this.lastT[seat] = t;
    this.log.push({ a: 0, b: 0, c: 0, d: 0, e: 0, i: 0, ...e, T: t });
    return t;
  }
  tick() {
    const w = this.mark();
    while (this.keepFrom < this.log.length && this.log[this.keepFrom].T <= w - KeepTicks) this.keepFrom++;
    if (!this.dirty) return false;
    this.dirty = false; return true;
  }
  flat(from) {
    const a = [];
    for (let i = Math.max(0, Math.min(from, this.log.length)); i < this.log.length; i++) {
      const e = this.log[i]; a.push(i, e.T, e.p, e.k, e.a, e.b, e.c, e.d, e.e, e.i);
    }
    return a;
  }
  frame() { return { e: this.epoch, t: this.nowTick(), w: this.w, n: this.log.length, f: this.keepFrom, c: this.flat(this.keepFrom), d: this.desync }; }
  view(seat) {
    return { e: this.epoch, seed: this.seed, prm: JSON.parse(this.params), n0: this.n, p: seat != null && seat >= 0 ? this.pOf[seat] : -1,
      pOf: this.pOf.slice(), p2: seat != null && seat >= 0 ? this.p2Of[seat] : -1, p2Of: this.p2Of.slice(), d: this.desync };
  }
  logBody(from) {
    return { e: this.epoch, seed: this.seed, prm: JSON.parse(this.params), n0: this.n, t: this.nowTick(), w: this.mark(), n: this.log.length,
      f: Math.max(0, Math.min(from, this.log.length)), c: this.flat(from), pOf: this.pOf.slice(), p2Of: this.p2Of.slice(), d: this.desync };
  }
}

// ---------------- «мережа»: затримки, перестановки, хаб і fetch ----------------
function world({ seed = 1, base = 40, jit = 80, reorder = 0 }) {
  const rnd = mulberry(seed);
  const srv = new Srv(mulberry(seed * 31 + 7));
  const clients = [], watchers = new Set(), warns = [];
  const delay = () => base / 2 + rnd() * jit;
  // доставка в один бік: як TCP (по черзі), але з імовірністю reorder — обганяє попереднє
  // cur — чий зараз код (і мікрозадачі після нього): fetch з net.js не каже, хто питає
  const pipe = (cl) => { let last = 0; return (fn) => { let t = clock + delay(); if (rnd() >= reorder) t = Math.max(t, last); last = Math.max(last, t); at(t, () => { cur = cl; fn(); }); }; };
  const clone = (x) => JSON.parse(JSON.stringify(x));
  let building = null, cur = null;

  function hubCall(cl, method, a) {
    if (method === 'JoinRoom') {
      if (srv.seats.includes(cl.nick)) { cl.seat = srv.seats.indexOf(cl.nick); return { ok: false, message: 'Ти вже за столом' }; }
      const seat = srv.seats.indexOf(null);
      if (seat < 0) return { ok: false, message: 'Місць нема' };
      srv.seats[seat] = cl.nick; cl.seat = seat; srv.onJoin(seat);
      for (const w of watchers) sendView(w);
      return { ok: true };
    }
    if (method === 'WatchRoom') { watchers.add(cl); sendView(cl); return null; }
    if (method === 'Act') {
      try { srv.act(cl.seat, a[1], clone(a[2])); return { ok: true }; } catch (x) { if (x instanceof GameError) return { ok: false, message: x.message }; throw x; }
    }
    throw new Error('невідомий виклик ' + method);
  }
  function sendView(cl) { const m = clone({ room: { id: ROOM }, seat: cl.seat, view: srv.view(cl.seat) }); cl.down(() => cl.h.room?.(m)); }

  globalThis.window = globalThis;
  globalThis.signalR = {
    HubConnectionBuilder: class {
      withUrl() { return this; }
      withAutomaticReconnect() { return this; }
      build() {
        const cl = building;
        return {
          on: (n, fn) => { cl.h[n] = fn; },
          onreconnecting() {}, onreconnected() {},
          start: () => Promise.resolve(),
          invoke: (method, ...a) => new Promise((ok) => cl.up(() => { const r = clone(hubCall(cl, method, a)); cl.down(() => ok(r)); })),
          send: (method, room, kind, payload) => {
            const p = clone(payload), sec = Math.floor(clock / 1000);
            cl.rate.set(sec, (cl.rate.get(sec) || 0) + 1);   // квота каркаса: 30 Input/с з з'єднання (зайве мовчки викидає)
            cl.up(() => { if (cl.gone) return; try { srv.act(cl.seat, kind, p); } catch (x) { if (!(x instanceof GameError)) throw x; } });
            return Promise.resolve();
          },
        };
      }
    },
  };
  globalThis.fetch = (url) => {
    const cl = cur;
    if (!cl) throw new Error('fetch поза клієнтом');
    const from = +new URL(url, 'http://x').searchParams.get('from') || 0;
    return new Promise((ok) => cl.up(() => { const body = clone(srv.logBody(from)); cl.down(() => ok({ ok: true, status: 200, json: async () => body })); }));
  };
  Object.defineProperty(globalThis, 'performance', { value: { now: () => clock }, configurable: true, writable: true });
  const warn0 = console.warn, info0 = console.info;
  console.warn = (...a) => warns.push(a.map(String).join(' '));
  console.info = () => {};

  function addClient(nick, phase) {
    const cl = { nick, seat: null, h: {}, gone: false, rate: new Map(), game: { state: null, params: PARAMS, seed: 0, local: 1 } };
    cl.up = pipe(cl); cl.down = pipe(cl);
    let n = 0;
    cl.net = createNet(cl.game, { table: ROOM, nick, makeBot: (p) => {
      const b = createBot(p);
      return { p, think: (s, q) => { const v = { ...b.think(s, q) }; if (++n % 53 === 0) v.throw = true; if (n % 197 === 0) v.horn = true; return v; } };
    } });
    clients.push(cl);
    building = cl; cur = cl;
    cl.net.start();
    building = null;
    const loop = () => { if (cl.gone) return; cur = cl; cl.net.update(clock); at(clock + 50, loop); };
    at(clock + phase, loop);
    return cl;
  }
  function leave(cl) {   // людина встала з-за столу (вкладку закрито)
    cl.gone = true; watchers.delete(cl);
    srv.seats[cl.seat] = null; srv.onLeave(cl.seat);
    for (const w of watchers) sendView(w);
  }
  function serverLoop() {
    cur = null;
    const both = srv.tick();
    for (const w of watchers) { if (both) sendView(w); const m = clone({ id: ROOM, f: srv.frame() }); w.down(() => w.h.frame?.(m)); }
    at(clock + StepMs, serverLoop);
  }
  const as = (cl, fn) => { cur = cl; return fn(); };
  function restore() { console.warn = warn0; console.info = info0; }
  return { srv, clients, warns, addClient, leave, serverLoop, restore, as };
}

/** Прямий прогін журналу сервера (поточна епоха) до тику T */
function straight(srv, T) {
  const s = R.newGame(srv.seed >>> 0, JSON.parse(srv.params), srv.n || 1);
  const c = srv.flat(0), by = new Map();
  for (let j = 0; j < c.length; j += Stride) { const t = c[j + 1]; if (!by.has(t)) by.set(t, []); by.get(t).push(toCmd(c, j)); }
  while (s.tick < T && s.status === 'run') { const cmds = by.get(s.tick) || []; prep(s, cmds); R.step(s, cmds); }
  return s;
}

/** Звірки hash: на кожному спільному тику однаково в усіх клієнтів, і так само в прямому прогоні журналу сервера */
function checkHashes(srv, live, label) {
  const maps = live.map((cl) => new Map(cl.net.hashes()));
  const common = [...maps[0].keys()].filter((t) => maps.every((m) => m.has(t))).sort((a, b) => a - b);
  assert.ok(common.length >= 5, label + ': мало спільних звірок: ' + common.length);
  for (const t of common) {
    const hs = maps.map((m) => m.get(t));
    assert.ok(hs.every((h) => h === hs[0]), `${label}: розбіжність на тику ${t}: ${hs}`);
  }
  const last = common[common.length - 1];
  assert.equal(R.hash(straight(srv, last)), maps[0].get(last), label + ': прямий прогін журналу сервера дає інший світ');
  return common;
}

async function scenario(opts) {
  clock = 0; queue.length = 0;
  const W = world(opts);
  try {
    const { srv } = W;
    srv.seats[0] = 'A';   // творець столу сидить, партія стартує одразу (StartMode.Immediate)
    srv.start();
    W.serverLoop();
    const A = W.addClient('A', 7);
    await runUntil(300);
    const B = W.addClient('B', 23);
    await runUntil(900);
    const Cc = W.addClient('C', 41);
    await runUntil(opts.lateAt);
    const D = W.addClient('D', 13);   // пізній вхід: світ з журналу GET
    await runUntil(opts.lateAt + 15_000);
    W.leave(B);                        // вийшов посеред партії — речі на землю, слот вільний
    await runUntil(opts.lateAt + 15_500);
    const E = W.addClient('E', 29);   // сів на звільнене місце — займає той самий слот rules
    await runUntil(opts.restartAt);
    checkHashes(srv, [A, Cc, D, E], 'до перезапуску');
    W.as(A, () => A.net.restart(0xC0FFEE + opts.seed, PARAMS));   // перезапуск посеред: нова епоха всім
    await runUntil(opts.endAt);
    const live = [A, Cc, D, E];
    for (const cl of live) assert.ok(cl.net.ready, cl.nick + ': не готовий — ' + cl.net.status);
    const ep = live.map((cl) => cl.net.info().e);
    assert.deepEqual(ep, ep.map(() => srv.epoch), 'усі в останній епосі');
    const common = checkHashes(srv, live, 'після перезапуску');
    assert.deepEqual(srv.desyncs, [], 'сервер бачив розбіжність хешів');
    assert.ok(!W.warns.some((x) => /розійшлись/.test(x)), 'net.js кричав про розбіжність: ' + W.warns.join(' | '));
    // кожен керує своїм гравцем: місце → слот rules, гравець не вибулий
    const C0 = A.net.C;
    for (const cl of live) {
      const p = cl.net.p;
      assert.equal(p, srv.pOf[cl.seat], cl.nick + ': net.p ≠ сервер');
      assert.ok(C0.players[p] && !C0.players[p].out, cl.nick + `: гравець ${p} вибулий чи відсутній`);
    }
    const st = live.map((cl) => cl.net.info().stats);
    for (const cl of W.clients) {
      const top = Math.max(...cl.rate.values());
      assert.ok(top <= 30, `${cl.nick}: ${top} Input за секунду — квота каркаса 30 з'їла б власні команди`);
    }
    if (process.env.K24_LOG) console.log(JSON.stringify({ tick: C0.tick, status: C0.status, common: common.length, path: C0.path.length, E: C0.endless && [C0.endless.stations, C0.endless.shift], stale: srv.stale, flying: C0.flying.length, geese: C0.geese.length, st, players: C0.players.map((p) => [p.out, p.hold.k]) }));
    return { srv, common, st, C: C0, warns: W.warns };
  } finally { W.restore(); }
}

test('мережа наживо: 4 клієнти з ботами, джитер, пізній вхід, вихід/вхід, перезапуск — однаковий світ', { timeout: 600_000 }, async () => {
  const r = await scenario({ seed: 5, base: 60, jit: 120, reorder: 0, lateAt: 60_000, restartAt: 80_000, endAt: 380_000 });
  assert.ok(r.st.some((x) => x.rebuilds > 20), 'мало перерахунків — тест не перевірив rollback');
  assert.ok(r.C.tick > 800);
});

test('мережа наживо: перестановки доставки (кадри, вид, ввід обганяють одне одного), пізній вхід на 3-й хвилині', { timeout: 600_000 }, async () => {
  const r = await scenario({ seed: 9, base: 30, jit: 200, reorder: 0.25, lateAt: 200_000, restartAt: 240_000, endAt: 300_000 });
  assert.ok(r.C.tick > 600);
});

test('модель сервера: join після leave не раніше за тик leave (слот rules той самий)', () => {
  clock = 0;
  const srv = new Srv(mulberry(3));
  srv.seats = ['A', 'B', 'C', 'D'];
  srv.start();
  clock = 5000;   // тик 100
  srv.act(1, 'in', { mx: 100, my: 0, t: 135, i: 1 });   // B просить тик наперед (годинник браузера біжить швидше)
  srv.seats[1] = null; srv.onLeave(1);                   // leave B — не раніше за його попередню команду (135)
  srv.seats[1] = 'E'; srv.onJoin(1);                     // E на те саме місце, сервер віддає слот B
  const c = srv.flat(0), ev = [];
  for (let j = 0; j < c.length; j += Stride) ev.push({ t: c[j + 1], k: toCmd(c, j).k, p: c[j + 2] });
  const lv = ev.find((x) => x.k === 'leave'), jn = ev.find((x) => x.k === 'join');
  assert.ok(jn.t >= lv.t, `join на тику ${jn.t} раніше за leave на ${lv.t}`);
  // і rules погоджується: join займає саме звільнений слот
  const s = straight(srv, jn.t + 1);
  assert.equal(s.players.length, 4);
  assert.equal(s.players[srv.pOf[1]].out, 0);
});
