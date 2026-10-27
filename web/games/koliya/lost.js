// «Колія» — K33 «Чесні правила: нічого не губиться, ніхто не застрягає». Чисте й детерміноване (цілі, без
// Math.random/Date/DOM). Гачки в rules.js позначено «K33».
//
// 🪓 Інструменти не губляться (keepTools): на старті рахуємо сокири/кайла/відра (s.keep.start). Раз на CHECK тиків
//   рахуємо «доступні»: у руках, у польоті (K18), у дзьобі гуски (K11) і на підлозі там, куди можна дійти пішки від
//   когось із гравців (клітинка чи її сусід по стороні; не під вагоном, що стоїть). Менше, ніж на старті, RESPAWN
//   тиків поспіль (утопили, впало в урвище, putDrop не знайшов місця, гуска впустила нікуди, лежить за скелями чи під
//   паротягом, що став) → один інструмент з'являється на вільній клітинці біля кінця колії, досяжній пішки, подія
//   { t:'respawn', x, y, k }. Якщо всього (з недосяжними) вже стільки, як на старті, — недосяжний ПЕРЕНОСИМО, а не
//   множимо: більше, ніж на старті, не буває. s.keep = { start: [сокир, кайл, відер], due: [тик появи | 0, ×3] }.
// 🚂 Потяг виштовхує (trainPush): гравець 1 с нічого не робить (ні руху, ні use), а вагон на ньому — щотику PUSH підодиниць до
//   найближчої клітинки, де він не зачіпає ні вагона, ні стіни (спершу поза колією); не просувається — ставимо одразу.
// 👻 Протиснутись крізь вагон (passTick/passMask): тиснеш рух у вагон PASS_T тиків поспіль — далі йдеш крізь
//   потяг (вузький коридір, обійти нема як). pl.wp — лічильник. Цифри бота до/після — D:\or-wt\_kolia\k33\.
// 🎒 Вихід зі столу (leaveHold): річ з рук — на підлогу поруч; нема місця — дерево/залізо у склад, рейки в майстерню,
//   інструмент (і що не влізло) — біля кінця колії.
// 🆘 Порятунок (rescue): гравець RESCUE_T тиків тисне рух і не зрушує з місця, а пішки до колії не дійти (обгороджений
//   водою/скелями) — переносимо до хвоста потяга, подія { t:'rescue', x, y, p }. pl.sk — лічильник.
// Нове в стані (s.keep, pl.wp, pl.sk) іде в hash() загально (K6).
// УВАГА: цикл імпорту rules → lost → rules: на верхньому рівні тут — лише літерали.
import { U, G, I, WAGONS, PR, cellBlocked, wagonAt, wagonPos, putDrop, hitsAt, wagonsXY, slide } from './rules.js';

/** Числа K33. Змінювати лише для дослідів (k33/botrun.mjs) — у грі це сталі, однакові в усіх. */
export const K33 = { keep: 1, push: 1, passT: 10, rescue: 1 };
const CHECK = 10;          // тиків між перевірками інструментів (0,5 с)
const RESPAWN = 200;       // 10 с без інструмента — з'являється знову
const PUSH = 150;          // підодиниць/тик, з якими вагон відсуває гравця (3 клітинки/с)
const RESCUE_T = 100;      // 5 с тиснути в стіну
const IDLE_T = 20;         // виштовхуємо лише того, хто 1 с нічого не робив (ні руху, ні use)
const TOOL_K = [4, 5, 6];  // I.AXE, I.PICK, I.BUCKET (повне відро — теж відро)
const toolType = (k) => (k === 4 ? 0 : k === 5 ? 1 : k === 6 || k === 7 ? 2 : -1);
const cellOf = (v) => Math.floor(v / U);

/** Кінець newGame: скільки інструментів кожного типу на старті */
export function lostInit(s) {
  const start = [0, 0, 0];
  for (let i = 0; i < s.drop.length; i++) { const j = toolType(s.drop[i]); if (j >= 0) start[j]++; }
  for (const p of s.players) if (p && !p.out) { const j = toolType(p.hold.k); if (j >= 0) start[j]++; }
  s.keep = { start, due: [0, 0, 0] };
}

/** Початок step (до руху гравців): де хто стояв — щоб знати, хто цього тику не зрушив */
export function lostPre(s) {
  const a = [];
  for (const p of s.players) a.push(p ? p.x : 0, p ? p.y : 0);
  return a;
}

/** Кінець step (після паротяга): виштовхнути, порятувати, перевірити інструменти */
export function lostStep(s, pre) {
  if (s.status !== 'run') return;
  for (let n = 0; n < s.players.length; n++) {
    const pl = s.players[n];
    if (!pl || pl.out) continue;
    const still = pl.x === pre[2 * n] && pl.y === pre[2 * n + 1];
    if (K33.push) trainPush(s, n, pl, still);
    if (K33.rescue) rescueTick(s, n, pl, still);
  }
  if (K33.keep && s.keep && s.tick % CHECK === 0) keepTools(s);
}

// ================= 🚂 виштовхування й протискання =================
const wants = (pl) => pl.mx * pl.mx + pl.my * pl.my >= 144;   // поза мертвою зоною руху (12)

/** Маска вагонів, крізь які гравець цього тику йде (протиснувся) */
export function passMask(pl) {
  return K33.passT >= 0 && (pl.wp | 0) >= K33.passT ? (1 << WAGONS.length) - 1 : 0;
}

/** Рух гравця (до зсуву): тисне в вагон (а не в стіну) — лічильник протискання росте, інакше скидається */
export function passTick(s, pl) {
  if (K33.passT < 0) return;
  let push = false;
  if (wants(pl)) {
    const wag = wagonsXY(s), ax = Math.abs(pl.mx), ay = Math.abs(pl.my), P = 60;
    const dx = ax * 2 >= ay ? Math.sign(pl.mx) * P : 0, dy = ay * 2 >= ax ? Math.sign(pl.my) * P : 0;
    for (const [ox, oy] of [[dx, 0], [0, dy]]) {
      if (!ox && !oy) continue;
      if (hitsAt(s, pl.x + ox, pl.y + oy, wag, 0) && !hitsAt(s, pl.x + ox, pl.y + oy, [], 0)) push = true;
    }
  }
  if (push) pl.wp = Math.min(255, (pl.wp | 0) + 1);
  else if (pl.wp) pl.wp = 0;
}

/** Гравець стоїть, а вагон наїхав — відсуваємо до найближчої вільної клітинки (спершу поза колією) */
function trainPush(s, n, pl, still) {
  // іде сам (виходить чи протискається) чи працює (use за останню IDLE_T) — не заважаємо: будівник, що кладе рейку
  // перед самим носом паротяга, мусить і далі класти (виштовхнутий — сходили з рейок; net-multi сід 9, бот 20 сідів)
  if (wants(pl) || s.tick + 1 - (pl.ut | 0) < IDLE_T) return;
  const wag = wagonsXY(s);
  let m = 0;
  for (let k = 0; k < wag.length; k++) if (hitsAt(s, pl.x, pl.y, [wag[k]], 0)) m |= 1 << k;
  if (!m) return;
  const c = pushTarget(s, pl, wag);
  if (!c) return;
  const tx = c.x * U + U / 2, ty = c.y * U + U / 2;
  const x0 = pl.x, y0 = pl.y;
  slide(s, pl, Math.max(-PUSH, Math.min(PUSH, tx - pl.x)), 0, wag, m);
  slide(s, pl, 0, Math.max(-PUSH, Math.min(PUSH, ty - pl.y)), wag, m);
  if (pl.x === x0 && pl.y === y0) { pl.x = tx; pl.y = ty; }   // уперлись (ріг) — ставимо одразу
  if (pl.gx !== undefined) { pl.gx = -1; pl.gy = -1; }         // K14 grid: ціль кроку вже не та
}

function pushTarget(s, pl, wag) {
  const cx = cellOf(pl.x), cy = cellOf(pl.y), onPath = new Set(s.path);
  for (let pass = 0; pass < 2; pass++) {
    let best = null, bd = Infinity;
    for (let r = 0; r <= 2; r++) for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
      if (Math.max(Math.abs(dx), Math.abs(dy)) !== r) continue;
      const x = cx + dx, y = cy + dy;
      if (cellBlocked(s, x, y) || (pass === 0 && onPath.has(y * s.w + x))) continue;
      const px = x * U + U / 2, py = y * U + U / 2;
      if (hitsAt(s, px, py, wag, 0)) continue;
      const d = (px - pl.x) * (px - pl.x) + (py - pl.y) * (py - pl.y);
      if (d < bd) { bd = d; best = { x, y }; }
    }
    if (best) return best;
  }
  return null;
}

// ================= 🆘 порятунок =================
function rescueTick(s, n, pl, still) {
  if (!(still && wants(pl) && !(pl.stun > 0))) { if (pl.sk) pl.sk = 0; return; }
  pl.sk = (pl.sk | 0) + 1;
  if (pl.sk < RESCUE_T) return;
  pl.sk = 0;
  if (reachesTrack(s, pl)) return;   // просто вперся — дійти можна, не чіпаємо
  const c = tailCell(s);
  if (!c) return;
  pl.x = c.x * U + U / 2; pl.y = c.y * U + U / 2;
  if (pl.gx !== undefined) { pl.gx = -1; pl.gy = -1; }
  s.ev.push({ t: 'rescue', x: c.x, y: c.y, p: n });
}

/** Чи дійде гравець пішки (без рубання й мостів) до колії */
function reachesTrack(s, pl) {
  const W = s.w, onPath = new Uint8Array(W * s.h);
  for (const c of s.path) onPath[c] = 1;
  const x0 = cellOf(pl.x), y0 = cellOf(pl.y);
  if (x0 < 0 || y0 < 0 || x0 >= W || y0 >= s.h) return false;
  const seen = new Uint8Array(W * s.h), q = [y0 * W + x0];
  seen[q[0]] = 1;
  for (let k = 0; k < q.length; k++) {
    const i = q[k];
    if (onPath[i]) return true;
    const x = i % W, y = (i / W) | 0;
    for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const nx = x + dx, ny = y + dy;
      if (cellBlocked(s, nx, ny)) continue;
      const m = ny * W + nx;
      if (!seen[m]) { seen[m] = 1; q.push(m); }
    }
  }
  return false;
}

/** Вільна клітинка біля хвоста потяга, до якої дійти пішки (BFS від хвоста): не колія, не вагон, не впирається */
export function tailCell(s) {
  const W = s.w, n = W * s.h, t = wagonPos(s, WAGONS.length - 1);
  const start = cellOf(t.y) * W + cellOf(t.x);
  if (start < 0 || start >= n) return null;
  const onPath = new Uint8Array(n); for (const c of s.path) onPath[c] = 1;
  const wag = wagonsXY(s), seen = new Uint8Array(n), q = [start];
  seen[start] = 1;
  for (let k = 0; k < q.length; k++) {
    const i = q[k], x = i % W, y = (i / W) | 0;
    if (!onPath[i] && !cellBlocked(s, x, y) && s.ground[i] !== G.STATION && wagonAt(s, x, y) < 0 &&
        !hitsAt(s, x * U + U / 2, y * U + U / 2, wag, 0)) return { x, y };
    for (const [dx, dy] of [[-1, 0], [0, -1], [0, 1], [1, 0]]) {
      const nx = x + dx, ny = y + dy;
      if (cellBlocked(s, nx, ny)) continue;
      const m = ny * W + nx;
      if (!seen[m]) { seen[m] = 1; q.push(m); }
    }
  }
  return null;
}

// ================= 🪓 інструменти =================
/** Куди можна дійти пішки від гравців (без рубання й мостів; колія прохідна, вагони — ні як перешкода не рахуємо) */
function reachMap(s) {
  const W = s.w, n = W * s.h, seen = new Uint8Array(n), q = [];
  for (const p of s.players) {
    if (!p || p.out) continue;
    const x = cellOf(p.x), y = cellOf(p.y);
    if (x < 0 || y < 0 || x >= W || y >= s.h) continue;
    const i = y * W + x;
    if (!seen[i]) { seen[i] = 1; q.push(i); }
  }
  for (let k = 0; k < q.length; k++) {
    const i = q[k], x = i % W, y = (i / W) | 0;
    for (const [dx, dy] of [[-1, 0], [0, -1], [0, 1], [1, 0]]) {
      const nx = x + dx, ny = y + dy;
      if (cellBlocked(s, nx, ny)) continue;
      const m = ny * W + nx;
      if (!seen[m]) { seen[m] = 1; q.push(m); }
    }
  }
  return { seen, q };
}

/** Інструмент на клітинці i можна взяти: дійти до неї чи до сусіда по стороні; не під вагоном, що стоїть */
function availAt(s, R, i) {
  const W = s.w, x = i % W, y = (i / W) | 0;
  if (s.train.speed === 0 && wagonAt(s, x, y) >= 0) return false;
  if (R.seen[i]) return true;
  return (x > 0 && R.seen[i - 1]) || (x < W - 1 && R.seen[i + 1]) || (y > 0 && R.seen[i - W]) || (y < s.h - 1 && R.seen[i + W]);
}

function keepTools(s) {
  const K = s.keep;
  let any = false;
  for (const p of s.players) if (p && !p.out) { any = true; break; }
  if (!any) return;   // за столом нікого — нема кому шукати
  const total = [0, 0, 0], avail = [0, 0, 0], bad = [-1, -1, -1];
  let R = null;
  for (let i = 0; i < s.drop.length; i++) {
    const j = toolType(s.drop[i]);
    if (j < 0) continue;
    total[j]++;
    if (!R) R = reachMap(s);
    if (availAt(s, R, i)) avail[j]++; else if (bad[j] < 0) bad[j] = i;
  }
  const add = (k) => { const j = toolType(k); if (j >= 0) { total[j]++; avail[j]++; } };
  for (const p of s.players) if (p && !p.out) add(p.hold.k);
  if (s.flying) for (const f of s.flying) add(f.k);
  if (s.geese) for (const g of s.geese) add(g.hold);
  for (let j = 0; j < 3; j++) {
    if (avail[j] >= K.start[j]) { if (K.due[j]) K.due[j] = 0; continue; }
    if (!K.due[j]) { K.due[j] = s.tick + RESPAWN; continue; }
    if (s.tick < K.due[j]) continue;
    if (total[j] >= K.start[j] && bad[j] < 0) { K.due[j] = 0; continue; }   // (не буває: недоступне — лише на підлозі)
    if (!R) R = reachMap(s);
    const c = spawnCell(s, R);
    if (!c) continue;   // нема куди — спробуємо на наступній перевірці
    if (total[j] >= K.start[j]) { s.drop[bad[j]] = I.NONE; s.dropN[bad[j]] = 0; }   // переносимо, а не множимо
    const i = c.y * s.w + c.x;
    s.drop[i] = TOOL_K[j]; s.dropN[i] = 1;
    s.ev.push({ t: 'respawn', x: c.x, y: c.y, k: TOOL_K[j] });
    K.due[j] = avail[j] + 1 < K.start[j] ? s.tick + RESPAWN : 0;
  }
}

/** Вільна клітинка для речі біля кінця колії, досяжна пішки (R — reachMap; нема біля кінця — найближча до гравців) */
function spawnCell(s, R) {
  const W = s.w, n = W * s.h, onPath = new Uint8Array(n);
  for (const c of s.path) onPath[c] = 1;
  const sign = new Set((s.mid ? s.mid.signs : []).map((g) => g.y * W + g.x));
  const ok = (i) => {
    const x = i % W, y = (i / W) | 0;
    return !onPath[i] && !cellBlocked(s, x, y) && s.drop[i] === I.NONE && s.ground[i] !== G.STATION && !sign.has(i) &&
      wagonAt(s, x, y) < 0 && (!R || R.seen[i]);
  };
  const end = s.path[s.path.length - 1], seen = new Uint8Array(n), q = [end];
  seen[end] = 1;
  for (let k = 0; k < q.length && k < 1500; k++) {
    const i = q[k], x = i % W, y = (i / W) | 0;
    if (ok(i)) return { x, y };
    for (const [dx, dy] of [[-1, 0], [0, -1], [0, 1], [1, 0]]) {
      const nx = x + dx, ny = y + dy;
      if (cellBlocked(s, nx, ny)) continue;
      const m = ny * W + nx;
      if (!seen[m]) { seen[m] = 1; q.push(m); }
    }
  }
  if (R) for (const i of R.q) if (ok(i)) return { x: i % W, y: (i / W) | 0 };
  return null;
}

// ================= 🎒 вихід зі столу =================
/** leave: річ з рук не пропадає */
export function leaveHold(s, pl) {
  const k = pl.hold.k;
  let n = pl.hold.n;
  if (k === I.NONE || n <= 0) return;
  if (putDrop(s, cellOf(pl.x), cellOf(pl.y), k, n)) return;
  const t = s.train, P = s.params;
  if (k === I.WOOD || k === I.IRON) {
    const key = k === I.WOOD ? 'wood' : 'iron';
    const put = Math.min(n, Math.max(0, (P.storeMax | 0) - t[key]));
    t[key] += put; n -= put;
    if (!n) return;
  } else if (k === I.RAIL) { t.rails += n; return; }
  const c = spawnCell(s, null);
  if (c) putDrop(s, c.x, c.y, k, n);   // не влізло й тут — інструмент поверне keepTools
}

// (для тестів) інструменти кожного типу: [усього, у руках/польоті/гусках]
export function toolCount(s) {
  const r = [0, 0, 0];
  for (let i = 0; i < s.drop.length; i++) { const j = toolType(s.drop[i]); if (j >= 0) r[j]++; }
  const add = (k) => { const j = toolType(k); if (j >= 0) r[j]++; };
  for (const p of s.players) if (p && !p.out) add(p.hold.k);
  if (s.flying) for (const f of s.flying) add(f.k);
  if (s.geese) for (const g of s.geese) add(g.hold);
  return r;
}
