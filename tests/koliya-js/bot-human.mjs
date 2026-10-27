// «Колія» K34 — імітація людини для плейтестів напарника (не тест сам — помічник для bot*.test.mjs і _kolia/k34).
// Людина = бот-привид (createBot(p, { ghost: true }) — не реєструється ботом, заявок не бачить), але гірший:
// повільніший (0,7 швидкості), реагує раз на 3 тики, іноді стоїть 2–6 с, рідко — 12 с, іноді блукає 1–2 с.
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';

const { I, U, TPS } = R;

export function createHuman(p, seed, opt = {}) {
  const b = createBot(p, { ghost: true });
  let r = ((seed + 1) * 2654435761 ^ (p + 7) * 40503) >>> 0 || 1;
  const rnd = () => { r ^= r << 13; r >>>= 0; r ^= r >>> 17; r ^= r << 5; r >>>= 0; return r / 4294967296; };
  const speed = opt.speed ?? 0.7, react = opt.react ?? 3;
  let mode = 'play', until = 0, last = { mx: 0, my: 0, use: false }, wx = 0, wy = 0;
  return {
    p, horn: false,
    think(s) {
      this.horn = false;
      if (s.tick % TPS === 0 && mode === 'play' && s.tick >= until) {
        const x = rnd();
        if (x < 0.004) { mode = 'stand'; until = s.tick + 12 * TPS; }
        else if (x < 0.044) { mode = 'stand'; until = s.tick + Math.round((2 + 4 * rnd()) * TPS); }
        else if (x < 0.074) { mode = 'wander'; until = s.tick + Math.round((1 + rnd()) * TPS); const a = rnd() * 6.283; wx = Math.round(100 * Math.cos(a)); wy = Math.round(100 * Math.sin(a)); }
      }
      if (mode !== 'play' && s.tick >= until) mode = 'play';
      if (mode === 'stand') return { mx: 0, my: 0, use: false };
      if (mode === 'wander') return { mx: Math.round(wx * speed), my: Math.round(wy * speed), use: false };
      if (s.tick % react === 0) { const v = b.think(s); last = { mx: Math.round(v.mx * speed), my: Math.round(v.my * speed), use: v.use }; return last; }
      return { mx: last.mx, my: last.my, use: false };
    },
  };
}

/**
 * Партія: людина-імітація (гравець 0) + помічник ('none' — соло, 'old' — звичайний бот, 'buddy' — напарник).
 * Рахує «перебивання» помічника: узяв інструмент/відро біля людини (≤ 2) чи щойно нею покладене (15 с); ударив ціль,
 * яку людина била останні 3 с; підібрав купку з-під її рук (≤ 2, руки порожні чи той самий ресурс); поклав рейку,
 * коли людина несла рейки. Плюс скільки секунд стояв на клітинці перед людиною.
 * hornAt — тики, коли людина гуде (для «йди за мною»).
 */
export function playMix(seed, kind, { maxMin = 15, params = {}, hornAt = [], onTick } = {}) {
  const n = kind === 'none' ? 1 : 2;
  const s = R.newGame(seed, { ...R.defaultParams(), ...params }, n);
  const hu = createHuman(0, seed);
  const bot = kind === 'none' ? null : createBot(1, kind === 'buddy' ? { buddy: true } : {});
  const it = { tool: 0, target: 0, loot: 0, rails: 0, block: 0 };
  const hHits = new Map(), hPut = new Map(), counted = new Set();
  const maxT = maxMin * 60 * TPS, W = s.w;
  const cellOf = (q) => Math.floor(q.y / U) * W + Math.floor(q.x / U);
  const md = (a, b) => Math.abs((a % W) - (b % W)) + Math.abs(((a / W) | 0) - ((b / W) | 0));
  const isTool = (k) => k === I.AXE || k === I.PICK || k === I.BUCKET || k === I.BUCKET_FULL;
  const hs = new Set(hornAt);
  while (s.status === 'run' && s.tick < maxT) {
    const cmds = [];
    const v = hu.think(s);
    cmds.push({ p: 0, k: 'in', mx: v.mx | 0, my: v.my | 0 });
    if (v.use) cmds.push({ p: 0, k: 'use' });
    if (hs.has(s.tick)) cmds.push({ p: 0, k: 'horn' });
    if (bot) {
      const u = bot.think(s);
      cmds.push({ p: 1, k: 'in', mx: u.mx | 0, my: u.my | 0 });
      if (u.use) cmds.push({ p: 1, k: 'use' });
    }
    const pre = bot ? s.drop.slice() : null, h0 = s.players[0], hHold = h0.hold.k, hc = cellOf(h0);
    if (bot) { const bc = cellOf(s.players[1]), fc = (Math.floor(h0.y / U) + h0.fy) * W + Math.floor(h0.x / U) + h0.fx; if (bc === fc) it.block++; }
    R.step(s, cmds);
    if (onTick) onTick(s, bot);
    for (const e of s.ev) {
      const c = e.y * W + e.x;
      if (e.p === 0) {
        if (e.t === 'chop' || e.t === 'rock') hHits.set(c, s.tick);
        if (e.t === 'drop' && isTool(s.drop[c])) hPut.set(c, s.tick);
        continue;
      }
      if (e.p !== 1) continue;
      if (e.t === 'chop' || e.t === 'rock') {
        const t = hHits.get(c);
        if (t !== undefined && s.tick - t <= 3 * TPS && !counted.has(c)) { counted.add(c); it.target++; }
      } else if (e.t === 'pick' && pre && pre[c] !== I.NONE) {
        const k = pre[c], d = md(c, hc);
        if (isTool(k)) { const t = hPut.get(c); if (d <= 2 || (t !== undefined && s.tick - t <= 15 * TPS)) it.tool++; }
        else if (d <= 2 && (hHold === I.NONE || hHold === k)) it.loot++;
      } else if (e.t === 'rail' && hHold === I.RAIL && s.ground[c] !== R.G.BRIDGE) it.rails++;
    }
  }
  const sec = Math.round(s.tick / TPS);
  const sum = it.tool + it.target + it.loot + it.rails;
  return { s, status: s.status, reason: s.reason, sec, headX: Math.floor(R.wagonPos(s, 0).x / U), it, sum, blockSec: +(it.block / TPS).toFixed(1) };
}
