// «Колія» K71 — стрес ЗСУВУ вікна нескінченної колії (endless.js shift). K68 за 30 хв давав 0–2 станції й 1 зсув: боти
// будують, а паротяг повільний (0,12–0,6 клітинки/с), тож за тест зсувів майже нема. Тут: коротка ділянка (stripLen 40),
// 4 боти, усі додатки (гуси часто, кидок часто, зима/лід, яр), а щоб зсувів було ≥ 20 за секунди — «колійник» тесту
// докладає рейку до найближчої непроведеної станції, коли до кінця колії лишилось < 6 клітинок (як гравець canLay; що
// заважає — прибирає). Паротяг 3 клітинки/с (котел — «зупинка», не пожежа) — швидкий, але не мчить крізь зсув за один тик.
// На кожному зсуві check(s, до) звіряє: інструменти не губляться, гравці/речі на прохідних, кинуте в польоті й гуси
// зсунуті разом зі своїми цілями, таблички s.mid — у вікні. Спільне для shift.test.mjs і браузерної перевірки (LIGHT).
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';

export const LIGHT = { geese: 'on', throw: 'on', bump: 'on', stun: 'on', pass: 'on', winter: 'on', yar: 'on', stationMode: 'endless',
  stripLen: 40, startSec: 0, speedStart: 3, speedGrowth: 0, speedMax: 3, finishSpeed: 3, heatSec: 240, overheat: 'stop',
  geeseSec: 10, geeseMax: 3, newBiomes: 3, zoneLen: 12 };   // zoneLen 12 — на 40 клітинок 2–3 біоми (зима, яр)

const { I, O, G, U } = R;
export const isTool = (k) => k === I.AXE || k === I.PICK || k === I.BUCKET || k === I.BUCKET_FULL;
const size = (s) => JSON.stringify(s, (k, v) => (ArrayBuffer.isView(v) ? Array.from(v) : v)).length;
export const passable = (s, x, y) => x >= 0 && y >= 0 && x < s.w && y < s.h && s.obj[y * s.w + x] === O.NONE && !R.noFoot(s.ground[y * s.w + x]);

/** Скільки інструментів у світі: на землі, в руках, у гусей, у польоті */
export function toolCount(s) {
  let n = 0;
  for (let i = 0; i < s.drop.length; i++) if (isTool(s.drop[i])) n++;
  for (const pl of s.players) if (pl && !pl.out && isTool(pl.hold?.k)) n++;
  for (const g of s.geese || []) if (isTool(g.hold)) n++;
  for (const f of s.flying || []) if (isTool(f.k)) n++;
  return n;
}

/** «Колійник»: колія коротка попереду паротяга — +1 рейка до найближчої непроведеної станції */
function fairy(s) {
  const E = s.endless, W = s.w;
  if (s.path.length * U - s.train.pos > 6 * U) return;
  const f = E.st[E.conn - E.base] || E.st[E.st.length - 1];
  const last = s.path[s.path.length - 1], lx = last % W, ly = (last / W) | 0;
  const cy = (f.y0 + f.y1) >> 1;
  const dy = Math.sign(cy - ly);
  // спершу до рядка станції, далі праворуч, далі в обхід (бот міг завернути колію сам)
  const cand = [[lx, ly + dy], [lx + 1, ly], [lx, ly + 1], [lx, ly - 1]].filter(([x, y], j) => (j || dy) && x < W && y > 0 && y < s.h - 1);
  for (const [nx, ny] of cand) {
    const i = ny * W + nx;
    if (s.path.includes(i)) continue;   // скелі «проходу» (O.WALL) усередині теж прибирає — крайні рядки не чіпає (фільтр вище)
    s.obj[i] = O.NONE; s.hp[i] = 0;
    if (R.noFoot(s.ground[i])) s.ground[i] = G.SAND;
    if (s.drop[i] !== I.NONE) {   // річ — на сусідню вільну клітинку (інструменти не губимо), не вийшло — прибираємо лише не-інструмент
      const j = [i - W, i + W, i + 1].find((m) => s.obj[m] === O.NONE && !R.noFoot(s.ground[m]) && s.drop[m] === I.NONE && !s.path.includes(m));
      if (j !== undefined) { s.drop[j] = s.drop[i]; s.dropN[j] = s.dropN[i]; s.drop[i] = I.NONE; s.dropN[i] = 0; }
      else if (!isTool(s.drop[i])) { s.drop[i] = I.NONE; s.dropN[i] = 0; }
    }
    if (R.canLay(s, nx, ny)) { s.path.push(i); return; }
  }
}

/** Прогін до ticks тиків або до minShifts зсувів. check(s, до) — на кожному зсуві (до — знімок перед тиком) */
export function shiftRun(seed, ticks, { minShifts = Infinity, check = null } = {}) {
  const P = { ...R.defaultParams(), ...LIGHT };
  const s = R.newGame(seed >>> 0, P, 4);
  const bots = [0, 1, 2, 3].map((p) => createBot(p));
  let h = 0, maxSize = 0, flyAt = 0, geeseAt = 0, midAt = 0;
  const size0 = size(s);
  for (let t = 0; t < ticks && s.status === 'run' && s.endless.shift < minShifts; t++) {
    fairy(s);
    const cmds = [];
    for (const b of bots) {
      const v = b.think(s);
      cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 });
      if (v.use) cmds.push({ p: b.p, k: 'use' });
      const r = (s.tick * 2654435761 + b.p * 97) >>> 0;
      if (r % 7 === 0) cmds.push({ p: b.p, k: 'throw' });   // часто — щоб кидок траплявся й під час зсуву
    }
    const sh = s.endless.shift;
    const before = { tools: toolCount(s) };
    R.step(s, cmds);
    if (s.endless.shift !== sh) {
      if (s.flying?.length) flyAt++;
      if (s.geese?.length) geeseAt++;
      if (s.mid) midAt++;
      if (check) check(s, before);
    }
    if (s.tick % 50 === 0) { h = (Math.imul(h, 31) + R.hash(s)) >>> 0; const z = size(s); if (z > maxSize) maxSize = z; }
  }
  return { s, hash: (Math.imul(h, 31) + R.hash(s)) >>> 0, size0, maxSize, flyAt, geeseAt, midAt };
}
