// «Колія» K10 — підказки новачкові: pickTips на ручно зібраних станах.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { U, G, O, I, TPS } from '../../web/games/koliya/rules.js';
import { pickTips, pathFront, didIt } from '../../web/games/koliya/tips.js';

const W = 20, H = 9, MID = 4;
/** Смуга 20×9: стіни по краях, колія x=0..5 по середині, сокира/кайло/відро біля старту */
function mk(nPlayers = 1) {
  const n = W * H;
  const s = {
    seed: 7, tick: 0, status: 'run', reason: '', w: W, h: H, params: {},
    ground: new Uint8Array(n), obj: new Uint8Array(n), hp: new Uint8Array(n), drop: new Uint8Array(n), dropN: new Uint8Array(n),
    path: [], train: { pos: 3 * U, speed: 0, heat: 0, fire: 0, wood: 0, iron: 0, rails: 3, craftT: 0, started: 0 },
    players: [], ev: [], stats: { railsLaid: 0, hits: 0, douses: 0, bridges: 0 },
  };
  for (let x = 0; x < W; x++) { s.obj[x] = O.WALL; s.obj[(H - 1) * W + x] = O.WALL; }
  for (let x = 0; x < 6; x++) s.path.push(MID * W + x);
  put(s, 4, 2, I.AXE); put(s, 5, 2, I.PICK); put(s, 4, 6, I.BUCKET);
  tree(s, 10, 3); rock(s, 12, 6);
  const sp = [[2, 3], [3, 5], [1, 2], [2, 6]];
  for (let i = 0; i < nPlayers; i++) s.players.push(pl(...sp[i]));
  return s;
}
const at = (s, x, y) => y * s.w + x;
function put(s, x, y, k, n = 1) { s.drop[at(s, x, y)] = k; s.dropN[at(s, x, y)] = n; }
function tree(s, x, y) { s.obj[at(s, x, y)] = O.TREE; s.hp[at(s, x, y)] = 3; }
function rock(s, x, y) { s.obj[at(s, x, y)] = O.ROCK; s.hp[at(s, x, y)] = 4; }
function pl(x, y, k = I.NONE, n = 0) { return { x: x * U + U / 2, y: y * U + U / 2, fx: 1, fy: 0, hold: { k, n }, cd: 0, out: 0 }; }
const hold = (p, k, n = 1) => { p.hold.k = k; p.hold.n = n; };
const tick = (s, n = 1) => { s.tick += n; s.ev = []; };
const ids = (tips) => tips.map((t) => `${t.p}:${t.id}`).sort();
const tipOf = (tips, p) => tips.find((t) => t.p === p);

test('новачок без нічого: «Візьми сокиру» зі стрілкою на сокиру; узяв — більше не каже', () => {
  const s = mk(), m = {};
  const t = pickTips(s, m);
  assert.deepEqual(ids(t), ['0:axe']);
  assert.match(t[0].text, /сокиру/);
  assert.deepEqual([t[0].x, t[0].y], [4, 2]);
  hold(s.players[0], I.AXE); s.drop[at(s, 4, 2)] = 0; tick(s);
  assert.notEqual(tipOf(pickTips(s, m), 0)?.id, 'axe');
  // поклав сокиру назад — про сокиру вже не нагадуємо
  hold(s.players[0], I.NONE, 0); put(s, 4, 2, I.AXE); tick(s);
  assert.notEqual(tipOf(pickTips(s, m), 0)?.id, 'axe');
});

test('з сокирою — «Рубай дерево» на найближче; удар по дереву перед собою знімає підказку назавжди', () => {
  const s = mk(), m = {};
  const p = s.players[0]; hold(p, I.AXE);
  let t = pickTips(s, m);
  assert.equal(t[0].id, 'chop');
  assert.match(t[0].text, /Space/);
  assert.deepEqual([t[0].x, t[0].y], [10, 3]);
  // підійшов до дерева, дивиться на нього
  p.x = 9 * U + U / 2; p.y = 3 * U + U / 2; p.fx = 1; p.fy = 0; tick(s);
  assert.equal(pickTips(s, m)[0].id, 'chop');
  s.hp[at(s, 10, 3)]--; tick(s);              // удар (без події — за знімком hp)
  assert.equal(pickTips(s, m).find((x) => x.id === 'chop'), undefined);
  tree(s, 15, 5); tick(s);
  assert.equal(pickTips(s, m).find((x) => x.id === 'chop'), undefined);
});

test('удар зчитується й з події chop цього гравця', () => {
  const s = mk(), m = {};
  hold(s.players[0], I.AXE);
  pickTips(s, m);
  tick(s); s.ev.push({ t: 'chop', x: 10, y: 3, p: 0 });
  assert.equal(pickTips(s, m).find((x) => x.id === 'chop'), undefined);
});

test('дрова в руках — «Неси на склад» на зелений вагон; здав — більше не каже', () => {
  const s = mk(), m = {};
  hold(s.players[0], I.WOOD, 2);
  let t = pickTips(s, m);
  assert.equal(t[0].id, 'store');
  assert.match(t[0].text, /склад/);
  assert.deepEqual([t[0].x, t[0].y], [2, MID]);   // склад — вагон 1 (позиція колії 2)
  hold(s.players[0], I.NONE, 0); s.train.wood = 2; tick(s);
  pickTips(s, m);
  hold(s.players[0], I.IRON, 1); tick(s);
  assert.equal(pickTips(s, m).find((x) => x.id === 'store'), undefined);
});

test('рейки готові й порожні руки — «Візьми з майстерні» (коли сокиру вже знає)', () => {
  const s = mk(), m = {};
  s.drop[at(s, 4, 2)] = 0; s.drop[at(s, 5, 2)] = 0;   // інструменти хтось забрав
  let t = pickTips(s, m);
  assert.equal(t[0].id, 'rails');
  assert.deepEqual([t[0].x, t[0].y], [1, MID]);     // майстерня — вагон 2
  hold(s.players[0], I.RAIL, 2); s.train.rails = 1; tick(s);
  t = pickTips(s, m);
  assert.equal(t[0].id, 'lay');
  assert.deepEqual([t[0].x, t[0].y], [6, MID]);     // прямо перед колією
});

test('«Клади рейку перед колією»: поклав — зникає й не повторюється', () => {
  const s = mk(), m = {};
  hold(s.players[0], I.RAIL, 2);
  assert.equal(pickTips(s, m)[0].id, 'lay');
  s.path.push(at(s, 6, MID)); hold(s.players[0], I.RAIL, 1); tick(s);
  assert.equal(pickTips(s, m).find((x) => x.id === 'lay'), undefined);
});

test('котел гріється: підказка одному — тому, хто з відром; полили — не повторюється нікому', () => {
  const s = mk(2), m = {};
  s.train.heat = 700;
  hold(s.players[1], I.BUCKET);
  let t = pickTips(s, m);
  assert.equal(tipOf(t, 1).id, 'heat');
  assert.match(tipOf(t, 1).text, /вод/);
  assert.notEqual(tipOf(t, 0)?.id, 'heat');
  hold(s.players[1], I.BUCKET_FULL); tick(s);
  t = pickTips(s, m);
  assert.match(tipOf(t, 1).text, /паротяг/);
  assert.deepEqual([tipOf(t, 1).x, tipOf(t, 1).y], [3, MID]);   // паротяг — голова
  s.train.heat = 0; s.stats.douses = 1; hold(s.players[1], I.BUCKET); tick(s);
  pickTips(s, m);
  s.train.heat = 900; tick(s);
  assert.equal(pickTips(s, m).find((x) => x.id === 'heat'), undefined);
});

test('котел без відра в руках — «Відро → вода → паротяг» на відро', () => {
  const s = mk(), m = {};
  s.train.heat = 650;
  const t = pickTips(s, m);
  assert.equal(t[0].id, 'heat');
  assert.match(t[0].text, /Відро → вода → паротяг/);
  assert.deepEqual([t[0].x, t[0].y], [4, 6]);
});

test('річка перед колією — «місток»; міст збудували — не повторюється', () => {
  const s = mk(), m = {};
  s.ground[at(s, 6, MID)] = G.WATER; s.ground[at(s, 6, MID - 1)] = G.WATER; s.ground[at(s, 6, MID + 1)] = G.WATER;
  s.ground[at(s, 5, MID - 1)] = G.WATER; s.ground[at(s, 5, MID + 1)] = G.WATER;
  assert.equal(pathFront(s).kind, 'water');
  hold(s.players[0], I.WOOD, 2);
  let t = pickTips(s, m);
  assert.equal(t[0].id, 'bridge');
  assert.match(t[0].text, /Річка/);
  s.ground[at(s, 6, MID)] = G.BRIDGE; s.stats.bridges = 1; hold(s.players[0], I.WOOD, 1); tick(s);
  t = pickTips(s, m);
  assert.equal(t.find((x) => x.id === 'bridge'), undefined);
});

test('глухий кут — «Розбери останню рейку»; розібрав — більше не каже', () => {
  const s = mk(), m = {};
  // колія впирається в стіну: …(5,4)→(5,3)→(5,2)→(5,1), обабіч — стіни
  s.drop[at(s, 5, 2)] = 0;
  s.path.push(at(s, 5, 3), at(s, 5, 2), at(s, 5, 1));
  s.obj[at(s, 4, 1)] = O.WALL; s.obj[at(s, 6, 1)] = O.WALL;
  assert.equal(pathFront(s).dead, true);
  let t = pickTips(s, m);
  assert.equal(t[0].id, 'dead');
  assert.deepEqual([t[0].x, t[0].y], [5, 1]);
  s.path.pop(); hold(s.players[0], I.RAIL); tick(s);
  t = pickTips(s, m);
  assert.equal(t.find((x) => x.id === 'dead'), undefined);
});

test('двоє: ролі «один рубає, другий кладе», різні кожному; за 8 с зникають', () => {
  const s = mk(2), m = {};
  let t = pickTips(s, m);
  assert.deepEqual(ids(t), ['0:role', '1:role']);
  assert.match(tipOf(t, 0).text, /Ти — рубаєш/);
  assert.match(tipOf(t, 1).text, /Ти — кладеш/);
  assert.match(tipOf(t, 0).text, /двоє/);
  tick(s, 8 * TPS);
  t = pickTips(s, m);
  assert.equal(t.find((x) => x.id === 'role'), undefined);
  // далі: рубач іде по сокиру, кладільник — по рейки
  assert.equal(tipOf(t, 0).id, 'axe');
  assert.equal(tipOf(t, 1).id, 'rails');
  assert.equal(didIt(m, 0, 'role'), false);    // відвисіла — не хвалимо
  hold(s.players[0], I.AXE); tick(s); pickTips(s, m);
  assert.equal(didIt(m, 0, 'axe'), true);
});

test('роль зникає й раніше — щойно гравець узявся за свою справу', () => {
  const s = mk(2), m = {};
  pickTips(s, m);
  hold(s.players[0], I.AXE); tick(s);
  const t = pickTips(s, m);
  assert.notEqual(tipOf(t, 0).id, 'role');
  assert.equal(tipOf(t, 1).id, 'role');
});

test('спільну біду отримує один, решта — своє; opts.players відсікає ботів', () => {
  const s = mk(2), m = {};
  s.train.heat = 800;
  let t = pickTips(s, m, { players: [0] });
  assert.deepEqual(t.map((x) => x.p), [0]);
  assert.equal(t[0].id, 'heat');
});

test('пам\'ять переживає рестарт: зроблене не повторюється в новій партії', () => {
  const s = mk(), m = {};
  pickTips(s, m);
  hold(s.players[0], I.AXE); tick(s, 50); pickTips(s, m);
  const s2 = mk();   // newGame: тик 0
  assert.notEqual(pickTips(s2, m)[0]?.id, 'axe');
});

test('після перемоги чи поразки — тиша', () => {
  const s = mk(); s.status = 'won';
  assert.deepEqual(pickTips(s, {}), []);
});

test('справжня партія (newGame + step): соло — сокира, двоє — ролі; 30 с тиків без падінь', async () => {
  const { newGame, step } = await import('../../web/games/koliya/rules.js');
  const s1 = newGame(7, undefined, 1), m1 = {};
  assert.equal(pickTips(s1, m1)[0]?.id, 'axe');
  const s2 = newGame(7, undefined, 2), m2 = {};
  assert.deepEqual(ids(pickTips(s2, m2)), ['0:role', '1:role']);
  for (let i = 0; i < 30 * TPS; i++) { step(s2, []); pickTips(s2, m2); }
  assert.ok(Array.isArray(pickTips(s2, m2)));
});

test('бідний стан (поля бракує) не падає', () => {
  const s = mk(); delete s.stats; delete s.train.heat; s.ev = undefined;
  assert.doesNotThrow(() => pickTips(s, {}));
});

// ---------------- K16 ----------------
import { keysFor } from '../../web/games/koliya/tips.js';

test('K16: кнопки за пристроєм — пад «A», клавіатура Space/Enter; opts.keys іде в текст', () => {
  assert.deepEqual(keysFor(0, true, 1), { use: 'A', throw: 'B' });
  assert.deepEqual(keysFor(0, false, 1), { use: 'Space', throw: 'Q' });
  assert.equal(keysFor(1, false, 2).use, 'Enter');
  const s = mk(), m = {};
  hold(s.players[0], I.AXE);
  const t = pickTips(s, m, { keys: () => ({ use: 'A', throw: 'B' }) });
  assert.match(t[0].text, /\bA\b/);
});

test('K16: болото під ногами — раз, 5 с, далі звичайна порада', () => {
  const s = mk(), m = {};
  s.ground[at(s, 2, 3)] = G.SWAMP;
  let t = pickTips(s, m);
  assert.equal(t[0].id, 'swamp'); assert.match(t[0].text, /Болото/);
  tick(s, 5 * TPS + 1); pickTips(s, m);
  tick(s); t = pickTips(s, m);
  assert.equal(t[0].id, 'axe');
});

test('K16: гуси — найближчому порада наздогнати; налякав — більше не каже', () => {
  const s = mk(2), m = {};
  s.params.geese = 'on';
  m.done = [{ role: 1 }, { role: 1 }];
  s.geese = [{ id: 1, x: 3 * U, y: 5 * U, hold: I.AXE, mode: 'run' }];
  let t = pickTips(s, m);
  assert.equal(tipOf(t, 1)?.id, 'goose');
  tick(s); s.ev = [{ t: 'scare', x: 3, y: 5, p: 1 }];
  t = pickTips(s, m);
  assert.notEqual(tipOf(t, 1)?.id, 'goose');
});

test('K16: розумний приціл — пояснення одинакові', () => {
  const s = mk(), m = {};
  s.params.aim = 'smart';
  const t = pickTips(s, m);
  assert.equal(t[0].id, 'aim');
});

// ---------------- K39 ----------------
import { hornFor } from '../../web/games/koliya/tips.js';

test('K39: лід і сніг під ногами — раз, 5 с, далі звичайна порада', () => {
  const s = mk(), m = {};
  s.ground[at(s, 2, 3)] = G.ICE;
  let t = pickTips(s, m);
  assert.equal(t[0].id, 'ice'); assert.match(t[0].text, /гальмуй заздалегідь/);
  tick(s, 5 * TPS + 1); pickTips(s, m);
  tick(s); assert.equal(pickTips(s, m)[0].id, 'axe');
  s.ground[at(s, 2, 3)] = G.SNOW; tick(s);
  t = pickTips(s, m);
  assert.equal(t[0].id, 'snow'); assert.match(t[0].text, /Сніг/);
});

test('K39: яр перед колією — «спершу дрова (каркас)»; каркас ліг — «неси залізо»; міст — тиша', () => {
  const s = mk(), m = {};
  for (let y = 1; y < H - 1; y++) s.ground[at(s, 7, y)] = G.CHASM;
  hold(s.players[0], I.WOOD, 2);
  let t = pickTips(s, m);
  assert.equal(t[0].id, 'chasm'); assert.match(t[0].text, /дрова \(каркас\), потім залізо/);
  assert.equal(t[0].x, 7);
  s.ground[at(s, t[0].x, t[0].y)] = G.FRAME; hold(s.players[0], I.IRON, 1); tick(s);
  t = pickTips(s, m);
  assert.equal(didIt(m, 0, 'chasm'), true);
  assert.equal(t[0].id, 'frame'); assert.match(t[0].text, /залізо/);
  s.ground[at(s, t[0].x, t[0].y)] = G.BRIDGE; hold(s.players[0], I.NONE, 0); tick(s);
  t = pickTips(s, m);
  assert.equal(didIt(m, 0, 'frame'), true);
  assert.equal(t.find((x) => x.id === 'chasm' || x.id === 'frame'), undefined);
});

test('K39: нескінченна — на зупинці «оберіть покращення» на найближчу табличку; обрали — більше не каже', () => {
  const s = mk(2), m = {};
  m.done = [{ role: 1 }, { role: 1 }];
  s.endless = { stop: 50 };
  s.mid = { signs: [{ x: 3, y: 2, k: 'tank' }, { x: 3, y: 6, k: 'craft' }], pick: '' };
  let t = pickTips(s, m);
  assert.deepEqual(ids(t), ['0:station', '1:station']);
  assert.match(tipOf(t, 0).text, /Станція! Оберіть покращення/);
  assert.deepEqual([tipOf(t, 0).x, tipOf(t, 0).y], [3, 2]);
  assert.deepEqual([tipOf(t, 1).x, tipOf(t, 1).y], [3, 6]);
  tick(s); s.mid.pick = 'tank'; s.ev = [{ t: 'upgrade', x: 3, y: 2, p: 0 }];
  s.mid.pick = ''; s.endless.stop = 50;   // наступна станція
  t = pickTips(s, m);
  assert.equal(t.find((x) => x.id === 'station'), undefined);
});

test('K39: кидок — несе дрова за кілька кроків від складу: «Кинь у вагон — B»; кинув — більше не каже', () => {
  const s = mk(), m = {};
  s.params.throw = 'on';
  const p = s.players[0]; hold(p, I.WOOD, 2);
  p.x = 6 * U + U / 2; p.y = MID * U + U / 2;   // склад на (2, MID) — 4 клітинки
  let t = pickTips(s, m, { keys: () => ({ use: 'A', throw: 'B' }) });
  assert.equal(t[0].id, 'throw'); assert.match(t[0].text, /Кинь у вагон — B/);
  tick(s); s.ev = [{ t: 'throw', x: 6, y: MID, p: 0 }];
  t = pickTips(s, m);
  assert.notEqual(t[0]?.id, 'throw');
  // без перемикача — звичайне «неси на склад»
  const s2 = mk(); hold(s2.players[0], I.WOOD, 2); s2.players[0].x = 6 * U + U / 2; s2.players[0].y = MID * U + U / 2;
  assert.equal(pickTips(s2, {})[0].id, 'store');
});

test('K39: гудок біля бота — «📯 F — покликати бота»; гукнув — більше не каже; без бота поруч — мовчить', () => {
  assert.deepEqual(hornFor(0, true), { horn: 'Y', react: 'LB/RB/LT/RT' });
  assert.equal(hornFor(0, false, 1).horn, 'F');
  const s = mk(2), m = {};
  m.done = [{ role: 1, axe: 1 }, {}];
  tick(s, 20 * TPS);
  let t = pickTips(s, m, { players: [0], bots: [1] });   // бот (3,5) — поруч з (2,3)
  assert.equal(t[0].id, 'horn'); assert.match(t[0].text, /📯 F — покликати бота/);
  assert.deepEqual([t[0].x, t[0].y], [3, 5]);
  tick(s); s.ev = [{ t: 'honk', x: 3, y: 4, p: 0 }];
  t = pickTips(s, m, { players: [0], bots: [1] });
  assert.notEqual(t[0]?.id, 'horn');
  assert.equal(didIt(m, 0, 'horn'), true);
  const s2 = mk(2), m2 = { done: [{ role: 1, axe: 1 }, {}] };
  s2.players[1].x = 15 * U; tick(s2, 20 * TPS);
  assert.notEqual(pickTips(s2, m2, { players: [0], bots: [1] })[0]?.id, 'horn');
  // на старті (до LATER) — не відволікаємо
  const s3 = mk(2), m3 = { done: [{ role: 1, axe: 1 }, {}] };
  assert.notEqual(pickTips(s3, m3, { players: [0], bots: [1] })[0]?.id, 'horn');
});

test('K39: реакції в мережі — «1–4 — реакції» раз за партію; без мережі — ні', () => {
  const s = mk(), m = { done: [{ axe: 1 }] };
  tick(s, 20 * TPS);
  let t = pickTips(s, m, { net: true });
  assert.equal(t[0].id, 'react'); assert.match(t[0].text, /1–4 — реакції/);
  tick(s, 5 * TPS + 1); pickTips(s, m, { net: true });
  tick(s); assert.notEqual(pickTips(s, m, { net: true })[0]?.id, 'react');
  const s2 = mk(); tick(s2, 20 * TPS);
  assert.notEqual(pickTips(s2, { done: [{ axe: 1 }] })[0]?.id, 'react');
});
