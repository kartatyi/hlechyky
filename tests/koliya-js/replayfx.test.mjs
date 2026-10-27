// «Колія» K59 — повтор без побічних ефектів: перегляд (і перемотка) не пише сховище (kol-replays/kol-ach/kol-best/
// kol-daily), не кладе новий повтор у «останні 3», не тостить; вихід повертає живий світ; B, яким вийшли, не тисне в грі.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';

// --- мінімальний DOM, localStorage і пади ---
const mem = new Map();
const writes = [];
globalThis.localStorage = {
  getItem: (k) => (mem.has(k) ? mem.get(k) : null),
  setItem: (k, v) => { writes.push(k); mem.set(k, String(v)); },
  removeItem: (k) => { writes.push(k); mem.delete(k); },
};
function node(tag) {
  const e = {
    tag, className: '', textContent: '', innerHTML: '', title: '', type: '', value: '', style: {}, children: [], parent: null, ls: {},
    classList: { toggle() { } },
    appendChild(c) { c.parent = e; e.children.push(c); return c; },
    remove() { if (e.parent) e.parent.children = e.parent.children.filter((x) => x !== e); e.parent = null; },
    addEventListener(t, f) { (e.ls[t] ??= []).push(f); },
    removeEventListener() { }, blur() { }, focus() { }, select() { },
    querySelector: () => null, contains: () => false,
    getBoundingClientRect: () => ({ left: 0, width: 100 }),
  };
  return e;
}
globalThis.document = { createElement: node, head: node('head'), body: node('body'), execCommand: () => false };
const keyLs = new Set();
globalThis.addEventListener = (t, f) => { if (t === 'keydown') keyLs.add(f); };
globalThis.removeEventListener = (t, f) => { if (t === 'keydown') keyLs.delete(f); };
const key = (code) => { for (const f of [...keyLs]) f({ code, repeat: false, shiftKey: false, preventDefault() { }, stopPropagation() { } }); };
let pads = [];
Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { getGamepads: () => pads, clipboard: { writeText: async () => { } } } });
const pad = (pressed = []) => ({ index: 0, id: 'X', connected: true, buttons: Array.from({ length: 17 }, (_, i) => ({ pressed: pressed.includes(i), value: pressed.includes(i) ? 1 : 0 })) });

const P = await import('../../web/games/koliya/replay.js');
const settle = async () => { for (let i = 0; i < 20; i++) await new Promise((r) => setTimeout(r, 5)); };

function setup() {
  mem.clear(); writes.length = 0; pads = [];
  mem.set('kol-best', '{"time|end|7|2":999}'); mem.set('kol-daily', '{}'); mem.set('kol-ach', '{"got":{},"n":{}}');
  const worlds = [];
  const view = { setWorld: (s) => worlds.push(s), setFollow() { }, draw() { }, events() { } };
  const said = [], swallowed = [];
  const game = { view, state: null, tips: null, touch: null };
  const hud = node('div');
  const rp = P.createReplay(game, { hud, say: (t) => said.push(t), input: { swallow: () => swallowed.push(1) }, audio: { play() { }, setTrain() { } } });
  return { rp, game, view, worlds, said, swallowed, hud };
}

// партія двох ботів, записана гачками як у main.js (begin → rec щотику → watch)
function playGame(rp, game, seed = 7) {
  const prm = { ...R.defaultParams() };
  const s = game.state = R.newGame(seed, prm, 2);
  rp.begin(s, seed, prm, 2, { local: 0 });
  const bots = [createBot(0), createBot(1)];
  while (s.status === 'run' && s.tick < 12 * 60 * R.TPS) {   // сіди 7 і 11 боти доїжджають за ~5,5 хв
    const cmds = [];
    for (const b of bots) { const v = b.think(s); cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 }); if (v.use) cmds.push({ p: b.p, k: 'use' }); }
    rp.rec(s, cmds);
    R.step(s, cmds);
  }
  assert.notEqual(s.status, 'run');
  rp.rec(s, []);
  rp.watch();
  return s;
}

test('K59: перегляд і перемотка не пишуть сховище й не кладуть повтор поверх', async () => {
  const { rp, game, worlds, said, hud } = setup();
  const live = playGame(rp, game);
  await settle();
  const st0 = P.loadStore();
  assert.equal(st0.last.length, 1, 'партія записалась раз');
  const liveHash = R.hash(live), snap = JSON.stringify([...mem]);
  writes.length = 0;

  for (let round = 0; round < 2; round++) {
    assert.ok(rp.open());
    assert.equal(hud.style.visibility, 'hidden');
    for (let i = 0; i < 30; i++) rp.frame(1000);
    key('Digit4'); for (let i = 0; i < 30; i++) rp.frame(1000);
    key('ArrowLeft'); key('ArrowLeft'); rp.frame(500);
    key('Home'); rp.frame(500);
    key('ArrowRight'); rp.frame(500);
    const v = rp.runToEnd();
    assert.equal(v.ok, true, 'повтор зійшовся');
    for (let i = 0; i < 5; i++) rp.frame(1000);   // фінал повтору — кадри після кінця
    key('Home'); rp.runToEnd(); rp.frame(16);       // ще раз до кінця — без другого запису
    key('Escape');
    assert.equal(rp.active, false);
    assert.equal(hud.style.visibility, '');
    assert.equal(worlds.at(-1), live, 'після виходу — знову живий світ');
    for (let i = 0; i < 3; i++) rp.watch();         // main.js щокадру після виходу
  }
  // повтор чужого коду — теж без запису
  const code = await rp.code();
  const friend = await P.decode(code);
  rp.open(friend); rp.runToEnd(); for (let i = 0; i < 3; i++) rp.frame(1000); key('Escape'); rp.watch();
  await settle();

  assert.deepEqual(writes, [], 'перегляд нічого не писав у localStorage');
  assert.equal(JSON.stringify([...mem]), snap);
  assert.equal(P.loadStore().last.length, 1, '«останні 3» — та сама одна партія');
  assert.equal(R.hash(live), liveHash, 'живий світ не зачеплено');
  assert.equal(game.state, live);
  assert.ok(!said.some((t) => /🏆/.test(t)), 'жодного тосту ачівки');
});

test('K59: B, яким вийшли з повтору, — padHeld, поки не відпустять (і не довше 2 с)', async () => {
  const { rp, game, swallowed } = setup();
  playGame(rp, game, 11);
  rp.open();
  rp.frame(16);
  assert.equal(rp.padHeld(), false, 'під час повтору — не про це');
  pads = [pad()]; rp.frame(16);               // перший погляд на пад
  pads = [pad([1])]; rp.frame(16);            // B — вийти
  assert.equal(rp.active, false);
  assert.ok(swallowed.length >= 1);
  assert.equal(rp.padHeld(), true, 'B ще затиснуте');
  assert.equal(rp.padHeld(), true);
  pads = [pad()];
  assert.equal(rp.padHeld(), false, 'відпустили — ввід гри знову');
  pads = [pad([1])];
  assert.equal(rp.padHeld(), false, 'новий натиск після відпускання — уже гри');
  // Esc з клавіатури при затиснутій (сторонній) кнопці — теж чекаємо її; стара кнопка не тримає вічно
  rp.open(); key('Escape');
  assert.equal(rp.padHeld(), true);
  const now = Date.now; Date.now = () => now() + 2500;
  try { assert.equal(rp.padHeld(), false, 'не довше 2 с'); } finally { Date.now = now; }
  pads = [];
});
