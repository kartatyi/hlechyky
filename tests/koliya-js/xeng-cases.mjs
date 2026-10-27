// «Колія» K52 — детермінізм між рушіями: 5 партій (сід × додатки) з 3 ботами по 3000 тиків.
// Спільне для Node (xeng.test.mjs) і браузера (сторінка імпортує цей файл і порівнює hash з XENG_GOLD).
// Боти теж у мережі шлють команди, тож у браузері партію і програємо з коду, і записуємо наново ботами.
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';
import * as P from '../../web/games/koliya/replay.js';

const ALL = { geese: 'on', bump: 'on', throw: 'on', pass: 'on', stun: 'on', winter: 'on', yar: 'on' };
export const XENG_CASES = [
  { seed: 7,    params: { ...ALL, stationMode: 'upgrade', move: 'free' } },
  { seed: 42,   params: { ...ALL, stationMode: 'endless', move: 'grid', useMode: 'tap', speedCurve: 'steps', overheat: 'stop' } },
  { seed: 1001, params: { ...ALL, stationMode: 'endless', move: 'snappy', aim: 'smart', useMode: 'auto', speedCurve: 'distance' } },
  { seed: 19,   params: { ...ALL, stationMode: 'upgrade', newBiomes: 3, geeseSec: 10, geeseSpeed: 3.5, iceSlide: 0.95 } },
  { seed: 53,   params: { ...ALL, stationMode: 'endless', newBiomes: 2.5, swamp: 0.3, swampSlow: 0.35, snowSlow: 0.45 } },
];
export const XENG_TICKS = 3000;
export const XENG_MARKS = [500, 1000, 1500, 2000, 2500, 3000];

/** Записати партію ботами (як main.js tickOnce: 'in' щотику + use; бот 0 зрідка кидає й гудить) */
export function xengRecord(c, nBots = 3) {
  const prm = { ...R.defaultParams(), ...c.params };
  const s = R.newGame(c.seed, prm, nBots);
  const rec = P.recBegin(c.seed, prm, nBots, { local: 0 });
  const bots = Array.from({ length: nBots }, (_, i) => createBot(i));
  const marks = [];
  while (s.status === 'run' && s.tick < XENG_TICKS) {
    const cmds = [];
    for (const b of bots) {
      const v = b.think(s);
      cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 });
      if (v.use) cmds.push({ p: b.p, k: 'use' });
    }
    if (s.tick % 557 === 300) cmds.push({ p: 0, k: 'throw' });
    if (s.tick % 401 === 200) cmds.push({ p: 1, k: 'horn' });
    P.recordTick(rec, s, cmds);
    R.step(s, cmds);
    if (XENG_MARKS.includes(s.tick)) marks.push(R.hash(s) >>> 0);
  }
  P.recFinish(rec, s);
  return { s, rec, marks };
}

/** Програти запис: hash на XENG_MARKS і в кінці */
export function xengPlay(rec) {
  const marks = [];
  const pl = P.createPlayer(rec, { every: 1e9, onTick: (s) => { if (XENG_MARKS.includes(s.tick)) marks.push(R.hash(s) >>> 0); } });
  pl.stepTo(P.lastTick(rec));
  return { s: pl.s, marks, end: R.hash(pl.s) >>> 0 };
}
