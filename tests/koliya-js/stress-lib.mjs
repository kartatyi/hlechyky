// «Колія» K68 — стрес-прогін правил і ботів: 4 боти, усі додатки, нескінченна колія, варіанти move/useMode/aim/speedCurve
// по черзі. Партія скінчилась (поразка) — нова з наступним сідом того ж ланцюжка (стрес — це довга гра, не одна партія).
// Боти зрідка ще й кидають речі (throw) і сигналять — бот сам цього не робить, а кооп-фішки теж треба трусити.
// Повний прогін (30 хв гри × 3 сіди): node tests/koliya-js/stress-lib.mjs --full
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';

export const ALL = { geese: 'on', throw: 'on', bump: 'on', stun: 'on', pass: 'on', winter: 'on', yar: 'on', stationMode: 'endless' };
const V = R.VARIANTS;
/** Варіанти ядра для прогону i — по черзі */
export const variantOf = (i) => ({
  move: V.move.values[i % V.move.values.length], useMode: V.useMode.values[i % V.useMode.values.length],
  aim: V.aim.values[i % V.aim.values.length], speedCurve: V.speedCurve.values[i % V.speedCurve.values.length],
  overheat: V.overheat.values[i % V.overheat.values.length],
});
// JSON стану: типізовані масиви — як масиви чисел (розмір, а не {"0":…})
const size = (s) => JSON.stringify(s, (k, v) => (ArrayBuffer.isView(v) ? Array.from(v) : v)).length;

/** Прогін: ticks тиків, 4 боти. Повертає { hash, games, sizes: [на старті кожної партії й наприкінці], blockMs: [мс/тик по блоках], maxSize } */
export function stress(seed, i, ticks, { blocks = 10 } = {}) {
  const P = { ...R.defaultParams(), ...ALL, ...variantOf(i) };
  let g = 0, s = null, bots = null, h = 0;
  const sizes = [], blockMs = [], blk = Math.max(1, Math.floor(ticks / blocks));
  let maxSize = 0, maxPlain = 0;
  const fresh = () => { s = R.newGame((seed + g * 7919) >>> 0, P, 4); bots = [0, 1, 2, 3].map((p) => createBot(p)); g++; sizes.push(size(s)); };
  fresh();
  let t0 = performance.now();
  for (let t = 0; t < ticks; t++) {
    if (s.status !== 'run') { h = (Math.imul(h, 31) + R.hash(s)) >>> 0; fresh(); }
    const cmds = [];
    for (const b of bots) {
      const v = b.think(s);
      cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 });
      if (v.use) cmds.push({ p: b.p, k: 'use' });
      const r = (s.tick * 2654435761 + b.p * 97) >>> 0;   // детерміновано, без Math.random
      if (r % 157 === 0) cmds.push({ p: b.p, k: 'throw' });
      else if (r % 911 === 0) cmds.push({ p: b.p, k: 'horn' });
    }
    R.step(s, cmds);
    if (s.tick % 100 === 0) h = (Math.imul(h, 31) + R.hash(s)) >>> 0;
    if ((t + 1) % blk === 0) {
      const t1 = performance.now(); blockMs.push((t1 - t0) / blk); t0 = t1;
      const z = size(s); if (z > maxSize) maxSize = z;
      if (s.ev.length > maxPlain) maxPlain = s.ev.length;
    }
  }
  sizes.push(size(s));
  return { hash: (Math.imul(h, 31) + R.hash(s)) >>> 0, games: g, sizes, blockMs, maxSize, maxEv: maxPlain, state: s };
}

if (process.argv.includes('--full')) {
  const ticks = +(process.argv.find((a) => a.startsWith('--ticks='))?.slice(8) || 36000);
  for (const [i, seed] of [11, 222, 3333].entries()) {
    const a = stress(seed, i, ticks), b = stress(seed, i, ticks);
    const ms = a.blockMs.map((x) => x.toFixed(2)).join(' ');
    console.log(`сід ${seed} ${JSON.stringify(variantOf(i))}: партій ${a.games}, hash ${a.hash} ${a.hash === b.hash ? '=' : '≠ ' + b.hash},` +
      ` JSON старт ${a.sizes[0]} кінець ${a.sizes.at(-1)} макс ${a.maxSize}, станцій ${a.state.endless?.stations}, зсувів ${a.state.endless?.shift}, ev макс ${a.maxEv}\n  мс/тик по блоках: ${ms}`);
  }
}
