// «Колія» — тести правил (node --test tests/koliya-js). Стани будуємо руками поверх newGame (gen може змінитися).
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';

const { U, G, O, I, TPS } = R;

/** Порожня смуга: без лісу/скель/води, гравці подалі від колії */
function blank(params = {}, n = 1) {
  const p = { ...R.defaultParams(), forest: 0, rocks: 0, water: 0, soloSpeed: 1, ...params };   // K13: числа тестів — без соло-знижки
  const s = R.newGame(7, p, n);
  for (let i = 0; i < s.w * s.h; i++) {
    const y = (i / s.w) | 0;
    if (y > 0 && y < s.h - 1) { s.obj[i] = O.NONE; s.hp[i] = 0; s.drop[i] = I.NONE; s.dropN[i] = 0; if (s.ground[i] === G.WATER) s.ground[i] = G.GRASS; }
  }
  return s;
}
const at = (s, n, cx, cy, fx = 1, fy = 0) => { const pl = s.players[n]; pl.x = cx * U + U / 2; pl.y = cy * U + U / 2; pl.fx = fx; pl.fy = fy; return pl; };
const id = (s, x, y) => y * s.w + x;
/** натиснути use і відпустити (щоб наступний use був «натиском», а не «затиснуто») */
function press(s, p = 0) { R.step(s, [{ p, k: 'use' }]); R.step(s, []); for (let i = 0; i < 6; i++) R.step(s, []); }
const last = (s) => s.path[s.path.length - 1];
const lastXY = (s) => ({ x: last(s) % s.w, y: (last(s) / s.w) | 0 });

test('детермінізм: однакові команди — однаковий hash; clone посеред гри не розходиться', () => {
  const run = (cut) => {
    let s = R.newGame(12345, R.defaultParams(), 2);
    let r = 99;
    const rnd = () => (r = (Math.imul(r, 1103515245) + 12345) >>> 0) >>> 8;
    for (let t = 0; t < 1500; t++) {
      if (t === cut) s = R.clone(s);
      const cmds = [];
      for (let p = 0; p < 2; p++) {
        cmds.push({ p, k: 'in', mx: (rnd() % 201) - 100, my: (rnd() % 201) - 100 });
        if (rnd() % 3 === 0) cmds.push({ p, k: 'use' });
      }
      R.step(s, cmds);
    }
    return R.hash(s);
  };
  const a = run(-1), b = run(-1), c = run(700);
  assert.equal(a, b);
  assert.equal(a, c);
  // hash чутливий до руху гравця
  const s1 = R.newGame(5), s2 = R.newGame(5);
  R.step(s1, [{ p: 0, k: 'in', mx: 100, my: 0 }]); R.step(s2, []);
  assert.notEqual(R.hash(s1), R.hash(s2));
});

test('рух: не крізь дерево, ковзання вздовж стіни, швидкість playerSpeed', () => {
  const s = blank();
  const pl = at(s, 0, 10, 10);
  s.obj[id(s, 12, 10)] = O.TREE; s.hp[id(s, 12, 10)] = 3;
  for (let i = 0; i < 40; i++) R.step(s, [{ p: 0, k: 'in', mx: 100, my: 0 }]);
  assert.equal(pl.x, 12 * U - R.PR, 'упирається колом r=0,3 у дерево');
  assert.equal(pl.y, 10 * U + U / 2);
  // по діагоналі вздовж стіни дерев — ковзає по y
  for (let y = 0; y < s.h; y++) if (s.obj[id(s, 12, y)] === O.NONE) { s.obj[id(s, 12, y)] = O.TREE; s.hp[id(s, 12, y)] = 3; }
  const y0 = pl.y;
  R.step(s, [{ p: 0, k: 'in', mx: 100, my: 100 }]);
  assert.equal(pl.x, 12 * U - R.PR);
  assert.ok(pl.y > y0, 'ковзає вниз');
  // швидкість: 4,5 кл/с → 225 підодиниць/тик
  const t = blank(); const q = at(t, 0, 20, 10);
  R.step(t, [{ p: 0, k: 'in', mx: 100, my: 0 }]);
  assert.equal(q.x - (20 * U + U / 2), Math.round(t.params.playerSpeed * U / TPS));
  // діагональ нормується
  const x0 = q.x, yy = q.y;
  R.step(t, [{ p: 0, k: 'in', mx: 100, my: 100 }]);
  const dd = Math.hypot(q.x - x0, q.y - yy);
  assert.ok(Math.abs(dd - t.params.playerSpeed * U / TPS) < 3, `діагональ ${dd}`);
  assert.deepEqual([q.fx, q.fy], [1, 0], 'на рівних лишається поточна вісь');
});

test('рух: не крізь воду (міст — можна) і не крізь вагони', () => {
  const s = blank();
  const pl = at(s, 0, 20, 5);
  s.ground[id(s, 21, 5)] = G.WATER;
  for (let i = 0; i < 20; i++) R.step(s, [{ p: 0, k: 'in', mx: 100, my: 0 }]);
  assert.equal(pl.x, 21 * U - R.PR);
  s.ground[id(s, 21, 5)] = G.BRIDGE;
  for (let i = 0; i < 20; i++) R.step(s, [{ p: 0, k: 'in', mx: 100, my: 0 }]);
  assert.ok(pl.x > 22 * U);
  // вагон: паротяг на path[3] — підходимо згори
  const hc = s.path[3], hx = hc % s.w, hy = (hc / s.w) | 0;
  at(s, 0, hx, hy - 3, 0, 1);
  for (let i = 0; i < 14; i++) R.step(s, [{ p: 0, k: 'in', mx: 0, my: 100 }]);   // K33: тиснути довше 0,5 с — протиснешся (lost.test)
  assert.ok(pl.y < hy * U + U / 2 - 420 - R.PR + 5, 'не залазить у паротяг');
  assert.deepEqual(R.facingCell(pl), { x: hx, y: hy });
});

test('удари: сокира рубає дерево за treeHits з паузою hitMs, дрова на підлогу', () => {
  const s = blank({ treeHits: 3, woodPerTree: 2, hitMs: 200 });
  const pl = at(s, 0, 20, 10);
  pl.hold = { k: I.AXE, n: 1 };
  const t = id(s, 21, 10);
  s.obj[t] = O.TREE; s.hp[t] = 3;
  R.step(s, [{ p: 0, k: 'use' }]);
  assert.equal(s.hp[t], 2);
  assert.equal(s.ev[0].t, 'chop');
  R.step(s, [{ p: 0, k: 'use' }]);           // cd ще не минув
  assert.equal(s.hp[t], 2);
  for (let i = 0; i < 10; i++) R.step(s, [{ p: 0, k: 'use' }]);   // затиснуто — удари повторюються
  assert.equal(s.obj[t], O.NONE);
  assert.equal(s.drop[t], I.WOOD);
  assert.equal(s.dropN[t], 2);
  assert.equal(s.stats.hits, 3);
  // кайлом по дереву — нічого; кайлом по скелі — залізо
  pl.hold = { k: I.PICK, n: 1 };
  s.obj[t] = O.TREE; s.hp[t] = 3; s.drop[t] = 0; s.dropN[t] = 0;
  press(s); assert.equal(s.hp[t], 3);
  s.obj[t] = O.ROCK; s.hp[t] = 2;
  press(s); press(s);
  assert.equal(s.obj[t], O.NONE);
  assert.equal(s.drop[t], I.IRON);
  assert.equal(s.dropN[t], s.params.ironPerRock);
});

test('взяти/покласти: стоси до carryMax, затиснуте use не перекидає стос, обмін інструментів', () => {
  const s = blank({ carryMax: 3 });
  const pl = at(s, 0, 20, 10);
  const c = id(s, 21, 10);
  s.drop[c] = I.WOOD; s.dropN[c] = 5;
  press(s);
  assert.deepEqual(pl.hold, { k: I.WOOD, n: 3 });
  assert.equal(s.dropN[c], 2);
  press(s);                                  // з річчю — кладе на той самий стос
  assert.deepEqual(pl.hold, { k: I.NONE, n: 0 });
  assert.equal(s.dropN[c], 5);
  for (let i = 0; i < 12; i++) R.step(s, [{ p: 0, k: 'use' }]);   // затиснуто: взяв один раз, не кладе назад
  assert.equal(pl.hold.n, 3);
  // стати на свій стос — добирає сам
  R.step(s, []);
  for (let i = 0; i < 6; i++) R.step(s, [{ p: 0, k: 'in', mx: 100, my: 0 }]);
  R.step(s, [{ p: 0, k: 'in', mx: 0, my: 0 }]);
  assert.equal(pl.hold.n, 3);                // повні руки — не добирає
  // обмін: несу сокиру, переді мною кайло
  const s2 = blank(); const q = at(s2, 0, 20, 10);
  q.hold = { k: I.AXE, n: 1 };
  s2.drop[id(s2, 21, 10)] = I.PICK; s2.dropN[id(s2, 21, 10)] = 1;
  press(s2);
  assert.equal(q.hold.k, I.PICK);
  assert.equal(s2.drop[id(s2, 21, 10)], I.AXE);
  // покласти на воду не можна — інструмент не губиться
  s2.ground[id(s2, 19, 10)] = G.WATER; q.fx = -1;
  press(s2);
  assert.equal(q.hold.k, I.PICK);
});

test('склад і майстерня: дрова й залізо → рейки за craftSec, до railsMax; рейки з майстерні в руки', () => {
  const s = blank({ craftSec: 2, startRails: 0, railsMax: 2, railsPerCraft: 1, storeMax: 4, startSec: 90 });
  const st = R.wagonPos(s, 1), sx = Math.floor(st.x / U), sy = Math.floor(st.y / U);
  const pl = at(s, 0, sx, sy - 1, 0, 1);
  pl.hold = { k: I.WOOD, n: 3 };
  press(s);
  assert.equal(s.train.wood, 3);
  assert.equal(pl.hold.k, I.NONE);
  pl.hold = { k: I.IRON, n: 3 };
  press(s);                                   // залізо: 3, а дерево вже пішло в роботу
  assert.ok(s.train.craftT > 0);
  const total = s.train.wood + s.train.iron;
  assert.equal(total, 4);
  for (let i = 0; i < 2 * TPS * 3; i++) R.step(s, []);
  assert.equal(s.train.rails, 2, 'зупиняється на railsMax');
  assert.equal(s.stats.crafted, 2);
  assert.equal(s.train.wood, 1); assert.equal(s.train.iron, 1);
  // повний склад не бере більше storeMax
  pl.hold = { k: I.WOOD, n: 5 };
  press(s);
  assert.equal(s.train.wood, 4); assert.equal(pl.hold.n, 2);
  // рейки з майстерні
  const cw = R.wagonPos(s, 2), cx = Math.floor(cw.x / U), cy = Math.floor(cw.y / U);
  at(s, 0, cx, cy - 1, 0, 1).hold = { k: I.NONE, n: 0 };
  press(s);
  assert.deepEqual(s.players[0].hold, { k: I.RAIL, n: 2 });
  assert.equal(s.train.rails, 0);
});

test('рейки кладуться лише в продовження колії; міст із дерева на воду; розібрати останню', () => {
  const s = blank({ startSec: 90 });
  const L = lastXY(s);
  const pl = at(s, 0, L.x + 3, L.y);
  pl.hold = { k: I.RAIL, n: 4 };
  const n00 = s.path.length;
  press(s);                                   // клітинка L.x+4 — не продовження: стос просто лягає на підлогу
  assert.equal(s.path.length, n00);
  assert.equal(s.drop[id(s, L.x + 4, L.y)], I.RAIL);
  press(s);                                   // і назад у руки
  assert.equal(pl.hold.n, 4);
  at(s, 0, L.x, L.y - 1, 1, 0);              // дивиться на (L.x+1, L.y-1) — діагональ від кінця
  press(s);
  assert.equal(s.path.length, n00);
  press(s);
  assert.equal(pl.hold.n, 4);
  at(s, 0, L.x + 1, L.y - 1, 0, 1);          // дивиться на (L.x+1, L.y) — продовження
  const n0 = s.path.length;
  press(s);
  assert.equal(s.path.length, n0 + 1);
  assert.equal(last(s), id(s, L.x + 1, L.y));
  assert.equal(s.stats.railsLaid, 1);
  // вода: рейку не можна, дерево — міст, тоді можна
  const w = id(s, L.x + 2, L.y);
  s.ground[w] = G.WATER;
  at(s, 0, L.x + 2, L.y - 1, 0, 1);
  press(s);
  assert.equal(s.path.length, n0 + 1);
  pl.hold = { k: I.WOOD, n: 1 };
  press(s);
  assert.equal(s.ground[w], G.BRIDGE);
  assert.equal(pl.hold.k, I.NONE);
  pl.hold = { k: I.RAIL, n: 1 };
  press(s);
  assert.equal(last(s), w);
  // на дерево — не можна
  s.obj[id(s, L.x + 3, L.y)] = O.TREE;
  at(s, 0, L.x + 3, L.y - 1, 0, 1).hold = { k: I.RAIL, n: 1 };
  press(s);
  assert.equal(last(s), w);
  // порожні руки на останню рейку — розібрати
  at(s, 0, L.x + 2, L.y - 1, 0, 1).hold = { k: I.NONE, n: 0 };
  press(s);
  assert.deepEqual(s.players[0].hold, { k: I.RAIL, n: 1 });
  assert.equal(s.path.length, n0 + 1);
  // ...але не там, де вже стоїть потяг
  const t = blank({ startSec: 90 });
  const h = R.wagonPos(t, 0), hx = Math.floor(h.x / U), hy = Math.floor(h.y / U);
  t.path.length = 4;                          // голова на path[3] — це й остання
  at(t, 0, hx, hy - 1, 0, 1);
  press(t);
  assert.equal(t.path.length, 4);
});

test('паротяг: рушає після startSec зі свистком, розганяється, у кінці колії — сходить', () => {
  const s = blank({ startSec: 2, speedStart: 0.5, speedGrowth: 0.5, speedMax: 1 });
  const p0 = s.train.pos;
  for (let i = 0; i < 2 * TPS; i++) R.step(s, []);
  assert.equal(s.train.pos, p0);
  R.step(s, []);
  assert.ok(s.ev.some((e) => e.t === 'whistle'));
  let v0 = s.train.speed;
  for (let i = 0; i < 40 && s.status === 'run'; i++) R.step(s, []);
  assert.ok(s.train.speed > v0, 'розганяється');
  while (s.status === 'run' && s.tick < 5000) R.step(s, []);
  assert.equal(s.status, 'lost');
  assert.equal(s.reason, 'derail');
  assert.equal(s.train.pos, (s.path.length - 1) * U);
  // після кінця гри step нічого не робить
  const h = R.hash(s); R.step(s, [{ p: 0, k: 'in', mx: 100, my: 0 }]); assert.equal(R.hash(s), h);
});

test('станція: колія до станції — перемога', () => {
  const s = blank({ startSec: 0, speedStart: 1.5, speedMax: 3, heatSec: 240 });
  const L = lastXY(s);
  // прокладаємо руками прямо до станції
  for (let x = L.x + 1; x < s.w; x++) { const c = id(s, x, L.y); s.path.push(c); if (s.ground[c] === G.STATION) break; }
  assert.equal(s.ground[last(s)], G.STATION);
  while (s.status === 'run' && s.tick < 20000) R.step(s, []);
  assert.equal(s.status, 'won');
  assert.ok(s.ev.some((e) => e.t === 'won'));
  assert.ok(s.stats.cells > s.w / 2);
});

test('котел: перегрів — пожежа й поразка; повне відро гасить; варіант «stop» — стоїть', () => {
  const P = { startSec: 0, heatSec: 10, fireSec: 3, speedStart: 0.05, speedGrowth: 0 };
  const s = blank(P);
  for (let i = 0; i < 10 * TPS; i++) R.step(s, []);
  assert.equal(s.train.heat, 1000);
  assert.ok(s.train.fire > 0);
  // наповнити відро й полити
  const hd = R.wagonPos(s, 0), hx = Math.floor(hd.x / U), hy = Math.floor(hd.y / U);
  s.ground[id(s, hx + 1, hy - 2)] = G.WATER;
  const pl = at(s, 0, hx, hy - 2, 1, 0);
  pl.hold = { k: I.BUCKET, n: 1 };
  R.step(s, [{ p: 0, k: 'use' }]);
  assert.equal(pl.hold.k, I.BUCKET_FULL);
  assert.ok(s.ev.some((e) => e.t === 'water'));
  R.step(s, []);
  const h2 = R.wagonPos(s, 0);
  at(s, 0, Math.floor(h2.x / U), Math.floor(h2.y / U) - 1, 0, 1);
  for (let i = 0; i < 4; i++) R.step(s, []);
  R.step(s, [{ p: 0, k: 'use' }]);
  assert.equal(pl.hold.k, I.BUCKET);
  assert.equal(s.train.fire, 0);
  assert.ok(s.train.heat < 10);
  assert.equal(s.stats.douses, 1);
  while (s.status === 'run' && s.tick < 5000) R.step(s, []);
  assert.equal(s.reason, 'fire');
  // stop: на перегріві не їде, але й не горить
  const t = blank({ ...P, overheat: 'stop', speedStart: 0.2 });
  for (let i = 0; i < 10 * TPS + 5; i++) R.step(t, []);
  const pos = t.train.pos;
  for (let i = 0; i < 200; i++) R.step(t, []);
  assert.equal(t.status, 'run');
  assert.equal(t.train.pos, pos);
  assert.equal(t.train.fire, 0);
});

test('join/leave: вихід кладе річ на підлогу, гра не ламається без інструмента', () => {
  const s = blank({}, 2);
  const pl = at(s, 1, 20, 10);
  pl.hold = { k: I.AXE, n: 1 };
  R.step(s, [{ p: 1, k: 'leave' }]);
  assert.equal(s.players[1].out, 1);
  assert.equal(s.drop[id(s, 20, 10)], I.AXE);
  R.step(s, [{ p: 1, k: 'in', mx: 100, my: 0 }, { p: 1, k: 'use' }]);   // команди вибулого — ігнор
  assert.equal(s.players[1].x, -5 * U);
  R.step(s, [{ k: 'join' }]);
  assert.equal(s.players.length, 2);
  assert.equal(s.players[1].out, 0);
  for (let i = 0; i < 3; i++) R.step(s, [{ k: 'join' }]);
  assert.equal(s.players.length, R.MAX_PLAYERS);
  // непотрібні/биті команди
  R.step(s, [{ p: 9, k: 'use' }, { k: 'xx' }, null, { p: 0, k: 'pose', x: NaN, y: 5 }]);
  assert.equal(s.status, 'run');
  // поза: мережа ставить позицію й погляд
  R.step(s, [{ p: 0, k: 'pose', x: 30500, y: 7500, fx: 0, fy: -1 }]);
  assert.deepEqual([s.players[0].x, s.players[0].y, s.players[0].fx, s.players[0].fy], [30500, 7500, 0, -1]);
});

test('K13: соло-знижка — один гравець їде повільніше; двоє — повна швидкість; вийшов партнер — знову знижка', () => {
  const spdOf = (cps) => Math.round(cps * U * 1000 / TPS);
  const P = { startSec: 0, speedStart: 0.4, speedGrowth: 0, speedMax: 1, heatSec: 240, soloSpeed: 0.75, finishSpeed: 0 };
  const one = blank(P, 1), two = blank(P, 2);
  R.step(one, []); R.step(two, []);
  assert.equal(one.train.speed, Math.round(spdOf(0.4) * 0.75));
  assert.equal(two.train.speed, spdOf(0.4));
  R.step(two, [{ p: 1, k: 'leave' }]);
  assert.equal(two.train.speed, Math.round(spdOf(0.4) * 0.75));
  const off = blank({ ...P, soloSpeed: 1 }, 1); R.step(off, []);
  assert.equal(off.train.speed, spdOf(0.4), 'soloSpeed 1 — без знижки');
});

test('K13: колію дотягли до станції — паротяг мчить finishSpeed і швидко виграє; 0 — як було', () => {
  const spdOf = (cps) => Math.round(cps * U * 1000 / TPS);
  const mk = (fs) => {
    const s = blank({ startSec: 0, speedStart: 1, speedGrowth: 0, speedMax: 3, heatSec: 240, finishSpeed: fs }, 2);
    const L = lastXY(s);
    for (let x = L.x + 1; x < s.w; x++) { const c = id(s, x, L.y); s.path.push(c); if (s.ground[c] === G.STATION) break; }
    while (s.status === 'run' && s.tick < 60000) R.step(s, []);
    return s;
  };
  const fast = mk(4), slow = mk(0);
  assert.equal(fast.status, 'won'); assert.equal(slow.status, 'won');
  assert.equal(fast.stats.maxSpeed, spdOf(4));
  assert.ok(fast.tick * 3 < slow.tick, `ривок: ${fast.tick} проти ${slow.tick} тиків`);
});
