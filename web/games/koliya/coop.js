// «Колія» — кооп-фішки (K18) за перемикачами: штовханина, кидок, передача з рук у руки, «зірочки» від сокири, гудок.
// Чисті й детерміновані: цілі числа, без Math.random/Date/DOM. Вимкнене не чіпає стан (крім порожнього s.flying).
//
// 👐 bump ('on'): гравці — кола, крізь одне одного не ходять; перекриття розводимо навпіл уздовж лінії центрів
//   (+ легкий відштовх KNOCK), хто впертий у стіну — той не рухається, усе забирає інший. Можна штовхати партнера.
//   Подія { t:'bump', x, y, p } — не частіше раз на BUMP_EV тиків на гравця (pl.bt).
// 🎯 throw ('on'): команда { p, k:'throw' } кидає те, що в руках, на 3 клітинки вперед (дерево/скеля на шляху —
//   ближче: перша вільна з 3, 2, 1; перед носом стіна — не кидається). Дерево/залізо — весь стос, інструмент — одну
//   річ, рейки — по одній (стос рейок у річку одним махом не вилетить). Летить 0,4–0,6 с дугою:
//   s.flying = [{ k, n, x, y, z (підодиниці; z — висота дуги для малювання), sx, sy, tx, ty (клітинка), t, T, p }].
//   Приземлення: вагон-склад/майстерня + дерево/залізо — у склад («данк»); паротяг + повне відро — гасить котел;
//   на клітинці гравець з порожніми руками (чи з тим самим стосом) — ловить (подія 'catch'); вода — тоне ('sink');
//   інакше лягає на землю (зайнято — поруч, як putDrop) ('land'). Подія кидка — 'throw'.
// 🤝 pass ('on', типово): use з річчю на партнера, що стоїть ТОЧНО на клітинці дії, — річ у його руки (порожні руки —
//   усе; той самий стос — скільки влізе). Лише на натиск. Раніше в цьому місці річ просто падала йому під ноги.
// 💫 stun ('on'): сокира/кайло, дія на клітинку, де ТОЧНО стоїть партнер (а дерева/скелі там нема), — бац: партнер
//   STUN тиків (1 с) не рухається й не діє, річ з рук падає під нього; потім STUN_IMM тиків імунітет (без «замкнути»).
//   Якщо ввімкнено й pass, на натиск спершу пробуємо передати (подати сокиру), «бац» — коли тримаєш кнопку й рубаєш
//   далі (той самий випадковий удар, як у житті) або коли передати не можна. Подія { t:'stun', x, y, p (кого), by }.
// 📯 horn (завжди): { p, k:'horn' } — подія { t:'honk', x, y (паротяг), p }, щоб гукнути партнера; не частіше 0,5 с.
//
// Гачки в rules.js: VARIANTS (4 рядки); newGame → coopInit(s); applyCmd → 'throw'/'horn' → coopCmd; step → coopStep(s)
//   після руху гравців; movePlayer/use — пропуск, якщо pl.stun > 0; use → coopUse перед «покласти»; hash → coopHash.
// УВАГА: rules.js імпортує цей файл, а цей — rules.js (цикл). На верхньому рівні тут — лише літерали.
import { U, G, O, I, PR, STACKABLE, inside, wagonAt, shove, putDrop } from './rules.js';

const KNOCK = 25;          // легкий відштовх понад перекриття, підодиниць на кожного
const BUMP_EV = 10;        // тиків між подіями 'bump' одного гравця
const STUN = 20;           // 1 с зірочок
const STUN_IMM = 60;       // 3 с після удару — не приголомшити знову
const HORN_CD = 10;        // 0,5 с між гудками
const ACT = 3;             // як ACT_CD у rules
const ARC = 700;           // висота дуги кидка, підодиниць

const on = (s, k) => s.params[k] === 'on';
const ev = (s, e) => s.ev.push(e);
const cellX = (pl) => Math.floor(pl.x / U), cellY = (pl) => Math.floor(pl.y / U);
const isTool = (k) => k === I.AXE || k === I.PICK;

/** Ціла частина кореня (Ньютон) — однаково всюди */
function isqrt(n) {
  if (n <= 0) return 0;
  let x = n, y = Math.floor((x + 1) / 2);
  while (y < x) { x = y; y = Math.floor((x + Math.floor(n / x)) / 2); }
  return x;
}

/** Кінець newGame */
export function coopInit(s) {
  s.flying = [];
  return s;
}

/** Хто стоїть (центром) на клітинці cx,cy, крім гравця n; -1 — нікого */
function playerOn(s, cx, cy, n = -1) {
  for (let q = 0; q < s.players.length; q++) {
    const o = s.players[q];
    if (q !== n && o && !o.out && cellX(o) === cx && cellY(o) === cy) return q;
  }
  return -1;
}

/** Скільки речі k з n влізе в руки гравця o (0 — ніскільки) */
function room(s, o, k, n) {
  if (o.hold.k === I.NONE) return STACKABLE.has(k) ? Math.min(n, s.params.carryMax | 0) : n;
  if (o.hold.k === k && STACKABLE.has(k)) return Math.max(0, Math.min(n, (s.params.carryMax | 0) - o.hold.n));
  return 0;
}
function give(o, k, n) { if (o.hold.k === I.NONE) { o.hold.k = k; o.hold.n = n; } else o.hold.n += n; }
function take(pl, n) { pl.hold.n -= n; if (pl.hold.n <= 0) { pl.hold.k = I.NONE; pl.hold.n = 0; } }

// ================= команди =================
/** applyCmd: k = 'throw' | 'horn' */
export function coopCmd(s, n, pl, k) {
  if (k === 'horn') {
    if (pl.hk !== undefined && s.tick < pl.hk) return;
    pl.hk = s.tick + HORN_CD;
    const c = s.path[Math.max(0, Math.min(s.path.length - 1, Math.floor(s.train.pos / U)))];
    ev(s, { t: 'honk', x: c % s.w, y: (c / s.w) | 0, p: n });
    return;
  }
  if (k !== 'throw' || !on(s, 'throw') || pl.cd > 0 || pl.stun > 0 || pl.hold.k === I.NONE) return;
  const x0 = cellX(pl), y0 = cellY(pl);
  let d = 3;
  for (; d >= 1; d--) {
    const x = x0 + pl.fx * d, y = y0 + pl.fy * d;
    if (inside(s, x, y) && s.obj[y * s.w + x] === O.NONE) break;
  }
  if (d < 1) return;
  const kk = pl.hold.k, cnt = kk === I.RAIL ? 1 : pl.hold.n;
  take(pl, cnt);
  const tx = x0 + pl.fx * d, ty = y0 + pl.fy * d;
  s.flying.push({ k: kk, n: cnt, x: pl.x, y: pl.y, z: 0, sx: pl.x, sy: pl.y, tx, ty, t: 0, T: 6 + 2 * d, p: n });
  ev(s, { t: 'throw', x: x0, y: y0, p: n });
  pl.cd = ACT;
}

/**
 * use, після удару/вагонів/відра/рейки/мосту, перед «лише на натиск» і «покласти»: передача й «бац».
 * true — дію виконано (rules виходить з use).
 */
export function coopUse(s, n, pl, cx, cy, held) {
  if (!on(s, 'pass') && !on(s, 'stun')) return false;
  const q = playerOn(s, cx, cy, n);
  if (q < 0) return false;
  const o = s.players[q], h = pl.hold;
  if (!held && on(s, 'pass') && h.k !== I.NONE && !(o.stun > 0)) {
    const m = room(s, o, h.k, h.n);
    if (m > 0) {
      const k = h.k;
      take(pl, m); give(o, k, m);
      ev(s, { t: 'pass', x: cx, y: cy, p: n, to: q });
      pl.cd = ACT;
      return true;
    }
  }
  if (on(s, 'stun') && isTool(h.k) && !(o.stun > 0) && !(o.imm !== undefined && s.tick < o.imm)) {
    o.stun = STUN; o.imm = s.tick + STUN + STUN_IMM;
    o.mx = 0; o.my = 0;
    if (o.hold.k !== I.NONE) { putDrop(s, cx, cy, o.hold.k, o.hold.n); o.hold.k = I.NONE; o.hold.n = 0; }
    ev(s, { t: 'stun', x: cx, y: cy, p: q, by: n });
    pl.cd = Math.max(1, Math.round(s.params.hitMs / 50));
    return true;
  }
  return false;
}

// ================= тик =================
/** Після руху всіх гравців (до майстерні й паротяга) */
export function coopStep(s) {
  if (on(s, 'bump')) stepBump(s);
  if (s.flying && s.flying.length) stepFlying(s);
}

function stepBump(s) {
  const P = s.players, MIN = 2 * PR;
  for (let a = 0; a < P.length; a++) for (let b = a + 1; b < P.length; b++) {
    const A = P[a], B = P[b];
    if (!A || !B || A.out || B.out) continue;
    let dx = B.x - A.x, dy = B.y - A.y;
    const d2 = dx * dx + dy * dy;
    if (d2 >= MIN * MIN) continue;
    let d = isqrt(d2);
    if (!d) { dx = 1; dy = 0; d = 1; }                         // одне в одному — розводимо по x, молодший ліворуч
    const nx = Math.trunc(dx * 1000 / d), ny = Math.trunc(dy * 1000 / d);
    const over = MIN - d;
    const half = (over >> 1) + 1 + KNOCK;
    const ma = shove(s, A, -Math.trunc(nx * half / 1000), -Math.trunc(ny * half / 1000));
    const mb = shove(s, B, Math.trunc(nx * half / 1000), Math.trunc(ny * half / 1000));
    // хто впертий — того не зсунули; решту перекриття забирає інший
    const got = Math.trunc(((mb[0] - ma[0]) * nx + (mb[1] - ma[1]) * ny) / 1000);
    const left = over + 1 - got;
    if (left > 0) {
      if (Math.abs(mb[0]) + Math.abs(mb[1]) < Math.abs(ma[0]) + Math.abs(ma[1])) shove(s, A, -Math.trunc(nx * left / 1000), -Math.trunc(ny * left / 1000));
      else shove(s, B, Math.trunc(nx * left / 1000), Math.trunc(ny * left / 1000));
    }
    if (over > 60) for (const [o, q] of [[A, a], [B, b]]) {
      if (o.bt !== undefined && s.tick < o.bt) continue;
      o.bt = s.tick + BUMP_EV;
      ev(s, { t: 'bump', x: cellX(o), y: cellY(o), p: q });
    }
  }
}

function stepFlying(s) {
  const F = s.flying;
  for (let j = 0; j < F.length; j++) {
    const f = F[j];
    f.t++;
    const ex = f.tx * U + U / 2, ey = f.ty * U + U / 2;
    f.x = f.sx + Math.trunc((ex - f.sx) * f.t / f.T);
    f.y = f.sy + Math.trunc((ey - f.sy) * f.t / f.T);
    f.z = Math.trunc(4 * ARC * f.t * (f.T - f.t) / (f.T * f.T));
    if (f.t >= f.T) { land(s, f); F.splice(j, 1); j--; }
  }
}

function land(s, f) {
  const { tx: x, ty: y } = f;
  let k = f.k, n = f.n;
  if (!inside(s, x, y)) return;
  const i = y * s.w + x, t = s.train, P = s.params;
  const wk = wagonAt(s, x, y);
  if (wk >= 1 && (k === I.WOOD || k === I.IRON)) {          // данк у склад
    const key = k === I.WOOD ? 'wood' : 'iron';
    const put = Math.min(n, Math.max(0, (P.storeMax | 0) - t[key]));
    if (put > 0) { t[key] += put; n -= put; ev(s, { t: 'drop', x, y, p: f.p }); }
    if (!n) return;
  } else if (wk === 0 && k === I.BUCKET_FULL) {             // відро в котел
    t.heat = 0; t.ht = 0; t.fire = 0; s.stats.douses++;
    ev(s, { t: 'douse', x, y, p: f.p });
    k = I.BUCKET;
  }
  if (wk < 0) {
    const q = playerOn(s, x, y);
    if (q >= 0 && !(s.players[q].stun > 0)) {
      const o = s.players[q], m = room(s, o, k, n);
      if (m > 0) { give(o, k, m); n -= m; ev(s, { t: 'catch', x, y, p: q, from: f.p }); if (!n) return; }
    }
    if (s.ground[i] === G.WATER) { ev(s, { t: 'sink', x, y, p: f.p }); return; }
  }
  putDrop(s, x, y, k, n);
  ev(s, { t: 'land', x, y, p: f.p });
}

/** Додаток до hash — лише ввімкнене (типовий hash не змінюється) */
export function coopHash(s, mix) {
  if (on(s, 'stun')) for (const p of s.players) { mix(p.stun | 0); mix(p.imm | 0); }
  if (s.flying && s.flying.length) {
    mix(s.flying.length);
    for (const f of s.flying) { mix(f.k); mix(f.n); mix(f.x); mix(f.y); mix(f.tx); mix(f.ty); mix(f.t); mix(f.T); }
  }
}
