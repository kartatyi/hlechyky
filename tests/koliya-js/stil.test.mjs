// «Колія» 28.10 «Стіл / Моє»: своє керування кожному гравцю (команда 'feel'), повтор із ним, запис 7 журналу, підсумок
// налаштувань столу. node --test tests/koliya-js/stil.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { encodeBytes, decodeBytes } from '../../web/games/koliya/replay.js';
import { toCmd, leftHere } from '../../web/games/koliya/net.js';
import { levelOf, routeOf, tableSummary, setDefaults, LEVELS } from '../../web/games/koliya/panel.js';

const { U, G, O, I, TPS } = R;

/** Порожня смуга (як у feel.test.mjs) */
function blank(params = {}, n = 1) {
  const p = { ...R.defaultParams(), forest: 0, rocks: 0, water: 0, winter: 'off', startSec: 90, ...params };
  const s = R.newGame(7, p, n);
  for (let i = 0; i < s.w * s.h; i++) {
    const y = (i / s.w) | 0;
    if (y > 0 && y < s.h - 1) { s.obj[i] = O.NONE; s.hp[i] = 0; s.drop[i] = I.NONE; s.dropN[i] = 0; if (s.ground[i] === G.WATER) s.ground[i] = G.GRASS; }
  }
  return s;
}
const at = (s, n, cx, cy, fx = 1, fy = 0) => { const pl = s.players[n]; pl.x = cx * U + U / 2; pl.y = cy * U + U / 2; pl.fx = fx; pl.fy = fy; return pl; };
const feel = (p, f) => ({ p, k: 'feel', f: R.feelCode(f) });

test('feelCode ↔ feelFrom: усі 18 поєднань туди й назад, чуже — ні', () => {
  let n = 0;
  for (const move of R.VARIANTS.move.values) for (const useMode of R.VARIANTS.useMode.values) for (const aim of R.VARIANTS.aim.values) {
    const c = R.feelCode({ move, useMode, aim });
    assert.ok(c >= 0 && c <= 26);
    assert.deepEqual(R.feelFrom(c), { move, useMode, aim });
    n++;
  }
  assert.equal(n, 18);
  assert.equal(R.feelCode({ move: 'fly', useMode: 'press', aim: 'front' }), -1);
  assert.equal(R.feelFrom(2), null, 'приціл має лише 2 значення');
  assert.equal(R.feelFrom(27), null);
  assert.equal(R.feelFrom('5'), null);
});

test("'feel' — кожному своє: П1 клітинками, П2 вільно; той самий натиск — різний рух", () => {
  const s = blank({}, 2);
  const a = at(s, 0, 60, 6, 1, 0), b = at(s, 1, 60, 10, 1, 0);
  R.step(s, [feel(0, { move: 'grid', useMode: 'press', aim: 'front' })]);
  assert.equal(R.feelOf(s, a).move, 'grid');
  assert.equal(R.feelOf(s, b).move, 'free', 'без команди — з params, як було');
  // короткий натиск угору: клітинками — лише поворот, вільно — уже йде
  R.step(s, [{ p: 0, k: 'in', mx: 0, my: -100 }, { p: 1, k: 'in', mx: 0, my: -100 }]);
  R.step(s, [{ p: 0, k: 'in', mx: 0, my: 0 }, { p: 1, k: 'in', mx: 0, my: 0 }]);
  assert.equal(a.y, 6 * U + U / 2, 'клітинками: поворот на місці');
  assert.ok(b.y < 10 * U + U / 2, 'вільно: зрушив');
});

test("'feel' — приціл і удар свої: розумний підсвічує, «б'є сам» рубає без кнопки лише тому, хто так обрав", () => {
  const s = blank({}, 2);
  const a = at(s, 0, 60, 6, 1, 0), b = at(s, 1, 60, 10, 1, 0);
  for (const [pl, y] of [[a, 6], [b, 10]]) { pl.hold.k = I.AXE; pl.hold.n = 1; s.obj[y * s.w + 61] = O.TREE; s.hp[y * s.w + 61] = 3; }
  R.step(s, [feel(0, { move: 'free', useMode: 'auto', aim: 'smart' })]);
  assert.deepEqual(a.aim, { x: 61, y: 6 }, 'П1: підсвітка розумного прицілу');
  assert.equal(b.aim, null);
  for (let i = 0; i < TPS; i++) R.step(s, []);
  assert.ok(s.hp[6 * s.w + 61] < 3 || s.obj[6 * s.w + 61] !== O.TREE, 'П1 рубає сам');
  assert.equal(s.hp[10 * s.w + 61], 3, 'П2 без кнопки не рубає');
  // назад на «перед собою» — підсвітка зникає одразу
  R.step(s, [feel(0, { move: 'free', useMode: 'press', aim: 'front' })]);
  assert.equal(a.aim, null);
});

test("'feel' детермінований: однакові команди — однаковий hash; без 'feel' hash такий самий, як без поля взагалі", () => {
  const go = (withFeel) => {
    const s = blank({}, 2);
    const cmds = [{ p: 0, k: 'in', mx: 100, my: 30 }, { p: 1, k: 'in', mx: -40, my: 100 }];
    if (withFeel) cmds.push(feel(1, { move: 'snappy', useMode: 'tap', aim: 'smart' }));
    R.step(s, cmds);
    for (let i = 0; i < 40; i++) R.step(s, []);
    return s;
  };
  assert.equal(R.hash(go(true)), R.hash(go(true)));
  assert.notEqual(R.hash(go(true)), R.hash(go(false)), 'своє керування — частина світу: звірка мережі його бачить');
  const s = go(false);
  assert.ok(s.players.every((p) => !('feel' in p)), 'без команди поля нема — старі хеші й повтори ті самі');
  // зміна руху посеред кроку клітинками — крок починається заново
  const g = blank({}, 1), pl = at(g, 0, 60, 8, 1, 0);
  R.step(g, [feel(0, { move: 'grid', useMode: 'press', aim: 'front' }), { p: 0, k: 'in', mx: 100, my: 0 }]);
  R.step(g, []);
  assert.ok(pl.gx >= 0, 'клітинками: має ціль кроку');
  R.step(g, [feel(0, { move: 'free', useMode: 'press', aim: 'front' })]);
  assert.equal(pl.gx, -1);
});

test("повтор: 'feel' кодується й розкодовується з кодом (старі коди без нього — ті самі)", () => {
  const rec = {
    seed: 7, n: 2, local: 2, net: false, end: 30, hash: 123, status: 'run', reason: '', at: 0, stations: 0, cells: 0,
    params: R.defaultParams(),
    ticks: [[0, [{ p: 0, k: 'feel', f: 13 }, { p: 1, k: 'in', mx: 100, my: 0 }]], [5, [{ p: 1, k: 'feel', f: 25 }, { p: 0, k: 'use' }]]],
  };
  const back = decodeBytes(encodeBytes(rec));
  assert.deepEqual(back.ticks[0][1][0], { p: 0, k: 'feel', f: 13 });
  assert.deepEqual(back.ticks[1][1][0], { p: 1, k: 'feel', f: 25 });
  assert.deepEqual(back.ticks[0][1][1], { p: 1, k: 'in', mx: 100, my: 0 });
  assert.equal(back.ticks[1][1][1].k, 'use');
});

test('журнал сервера: запис 7 — feel з кодом у a; leftHere у Node не падає', () => {
  const c = [0, 12, 1, 7, 22, 0, 0, 0, 0, 5];
  assert.deepEqual(toCmd(c, 0), { p: 1, k: 'feel', f: 22 });
  assert.equal(leftHere('abc'), false);
  assert.equal(leftHere('abc', true), false);
});

test('налаштування столу: рівень, маршрут і коротко для тосту', () => {
  const d = R.defaultParams();
  setDefaults(d);
  assert.equal(levelOf(d), 'normal');
  assert.equal(routeOf(d), 'mid');
  assert.equal(tableSummary(d), '🚂 Звично · звична');
  const cozy = { ...d, ...LEVELS.cozy.v, stripLen: 60, geese: 'on', winter: 'on' };
  assert.equal(levelOf(cozy), 'cozy');
  assert.equal(tableSummary(cozy), '☕ Затишно · коротка · 🪿❄');
  assert.equal(levelOf({ ...d, speedStart: 0.33 }), null);
  assert.match(tableSummary({ ...d, speedStart: 0.33, stationMode: 'endless' }), /^✏ свої числа · ♾ без кінця$/);
  assert.equal(routeOf({ ...d, stripLen: 130 }), null);
  const chaos = { ...d, ...LEVELS.chaos.v, ...LEVELS.chaos.addons };
  assert.equal(levelOf(chaos), 'chaos');
  assert.equal(tableSummary(chaos), '🌪 Хаос · звична · 🪿🤸');
});
