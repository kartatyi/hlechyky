// «Колія» — бот-автопілот (детермінований, бачить увесь стан). Власник: K2 (далі — задача «Бот»).
// Контракт: createBot(p) → { p, think(state) → { mx, my (цілі -100..100), use (bool) } }
//
// Як думає: щотику (1) оцінює світ — куди далі класти колію (маршрут Дейкстрою від кінця колії до станції, дерева/скелі/
// вода/речі на шляху дорожчі), де вагони; (2) має поточне завдання (рубати/бити/нести на склад/взяти рейки/класти/вода/
// покласти річ) і виконує його двома примітивами: «стати поруч і натиснути» (BFS до клітинки, сусідньої з ціллю, повернутись
// і натиснути use) та «стати на клітинку» (підбір свого стосу). Завдання переобирається, коли скінчилось чи стало неможливим.
// Кілька ботів ділять ролі за рангом серед ботів (парні — будівник: дерево, рейки, розчистка; непарні — копач: залізо, вода,
// скелі) і «займають» цілі (дерево, інструмент, «вода», «класти») у спільному реєстрі на стан, щоб не битися за одне.
import { U, G, O, I, WAGONS, TPS, wagonPos, wagonAt, canLay, startTick, noFoot } from './rules.js';
// K26: чим мостити клітинку c (вода/урвище — дерево, каркас мосту через яр — залізо), 0 — не міст
const bridgeRes = (s, c) => { const g = s.ground[c]; return g === G.WATER || g === G.CHASM ? I.WOOD : g === G.FRAME ? I.IRON : 0; };

const DIRS = [[1, 0], [0, 1], [0, -1], [-1, 0]];
// K13: пороги «запасу колії» (клітинок від голови до кінця): urg — по рейки будь-скільки, far — повний стос лише ближче
const TUNE = { urg: 10, far: 40, avB: 0.7, avS: 0.7, avH: 0.4, tol: 0, ...(globalThis.KOL_BOT_TUNE || {}) };
const shared = new WeakMap();    // state → { t, occ, route, routeSet, N, rt, pl, claims: Map, bots: Map }
// K34: клітинки, яких BFS обходить (людина й клітинка перед нею) — ставить напарник на час свого think
let AV = null;

function sh(s) {
  let o = shared.get(s);
  if (!o) { o = { t: -1, occ: null, route: [], routeSet: new Set(), N: -1, rt: -99, pl: -1, claims: new Map(), bots: new Map() }; shared.set(s, o); }
  if (o.t !== s.tick) {
    o.t = s.tick;
    // клітинки під вагонами (з запасом на розмір коробки)
    const occ = o.occ && o.occ.length === s.w * s.h ? o.occ.fill(0) : new Uint8Array(s.w * s.h);
    for (let k = 0; k < WAGONS.length; k++) {
      const w = wagonPos(s, k);
      for (const ox of [-420, 420]) for (const oy of [-420, 420]) {
        const cx = Math.floor((w.x + ox) / U), cy = Math.floor((w.y + oy) / U);
        if (cx >= 0 && cy >= 0 && cx < s.w && cy < s.h) occ[cy * s.w + cx] = 1;
      }
    }
    o.occ = occ;
    if (o.pl !== s.path.length || s.tick - o.rt >= 10) {
      o.pl = s.path.length; o.rt = s.tick;
      o.route = planRoute(s);
      o.routeSet = new Set(o.route.slice(0, 30));
      o.ahead = new Set(o.route.slice(1, 13));   // K13: найближчі клітинки маршруту — перешкоди тут рубаємо/б'ємо насамперед
      // «біля колії»: маршрут попереду і хвіст колії біля паротяга з сусідами — тут рубаємо/б'ємо насамперед,
      // щоб розширити коридор (інакше потяг затуляє вузький прохід)
      o.near = new Set();
      const hi = Math.floor(s.train.pos / U);
      const cells = [...s.path.slice(Math.max(0, hi - 3)), ...o.route.slice(1, 10)];
      for (const c of cells) {
        o.near.add(c);
        const x = c % s.w, y = (c / s.w) | 0;
        for (const [dx, dy] of DIRS) if (x + dx >= 0 && y + dy >= 0 && x + dx < s.w && y + dy < s.h) o.near.add((y + dy) * s.w + x + dx);
      }
      // «попереду»: біля кінця колії й перших клітинок маршруту — сюди кладемо відро, щоб потяг під'їхав до нього
      o.front = new Set();
      for (const c of o.route.slice(0, 6)) {
        const x = c % s.w, y = (c / s.w) | 0;
        for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) if (x + dx >= 0 && y + dy >= 0 && x + dx < s.w && y + dy < s.h) o.front.add((y + dy) * s.w + x + dx);
      }
      // K13: «біля паротяга попереду»: обабіч уже покладеної колії на 2–10 клітинок перед головою — тут тримаємо
      // повне відро (паротяг сам під'їде, гасити — два кроки), а не там, де набрали воду
      o.wet = new Set();
      for (const c of s.path.slice(hi + 2, hi + 11)) {
        const x = c % s.w, y = (c / s.w) | 0;
        for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) if (x + dx >= 0 && y + dy >= 0 && x + dx < s.w && y + dy < s.h) o.wet.add((y + dy) * s.w + x + dx);
      }
      // що далі класти: перша клітинка маршруту після кінця колії
    }
    o.N = o.route.length > 1 ? o.route[1] : -1;
    // бічний прохід: біля N з одного боку має бути прохідно (і біля кінця колії з того ж боку) — інакше потяг
    // у вузькому коридорі затулить прохід. Перешкоду збоку прибираємо перед тим, як класти N. 20 с не вийшло — забуваємо.
    if (o.sideN !== o.N) { o.sideN = o.N; o.sideT = s.tick; }
    o.side = s.tick - o.sideT < 20 * TPS ? sideNeed(s) : -1;
    if (!o.dS || s.tick - o.dst >= 10) {      // відстані від складу/майстерні (куди носимо) — щоб рубати там, звідки ближче нести
      o.dst = s.tick;
      const src = [];
      for (const k of [1, 2]) {
        const w = wagonPos(s, k), cx = Math.floor(w.x / U), cy = Math.floor(w.y / U);
        for (const [dx, dy] of DIRS) { const x = cx + dx, y = cy + dy; if (x >= 0 && y >= 0 && x < s.w && y < s.h) src.push(y * s.w + x); }
      }
      o.dS = distMap(s, o, src);
    }
    for (const [k, c] of o.claims) if (c.t < s.tick - 6) o.claims.delete(k);
    for (const [p, t] of o.bots) if (t < s.tick - 20) o.bots.delete(p);
  }
  return o;
}

// ---------- маршрут колії: Дейкстра від кінця колії до будь-якої клітинки станції ----------
// K51: Дейкстра — 60 % часу бота (смуга 300: ~1,4 мс/тик). Те саме до біта (купа з тими самими порівняннями й обмінами,
// лише на типізованих масивах; сусіди в тому ж порядку DIRS), плюс пам'ять: входи (w, h, path, obj, ground, drop) ті
// самі, що минулого разу, — маршрут той самий, не рахуємо. Порівняння точне (копії), не хеш — колізій нема.
const RT = { n: -1, inPath: null, dist: null, prev: null, hd: null, hc: null, cost: null,
             w: -1, h: -1, path: new Int32Array(0), pathN: -1, obj: null, ground: null, drop: null, route: null };
function rtBuf(n) {
  if (RT.n === n) return;
  RT.n = n; RT.inPath = new Uint8Array(n); RT.dist = new Int32Array(n); RT.prev = new Int32Array(n);
  RT.hd = new Int32Array(4 * n + 4); RT.hc = new Int32Array(4 * n + 4); RT.cost = new Int32Array(n);
  RT.obj = new Uint8Array(n); RT.ground = new Uint8Array(n); RT.drop = new Uint8Array(n); RT.route = null;
}
function rtSame(s) {
  if (!RT.route || RT.w !== s.w || RT.h !== s.h || RT.pathN !== s.path.length) return false;
  const P = s.path, Q = RT.path;
  for (let i = P.length - 1; i >= 0; i--) if (P[i] !== Q[i]) return false;
  const a = s.obj, b = RT.obj, g = s.ground, gb = RT.ground, d = s.drop, db = RT.drop, n = RT.n;
  for (let i = 0; i < n; i++) if (a[i] !== b[i] || d[i] !== db[i] || g[i] !== gb[i]) return false;
  return true;
}
function planRoute(s) {
  const n = s.w * s.h, W = s.w, H = s.h;
  rtBuf(n);
  if (rtSame(s)) return RT.route.slice();
  const path = s.path, obj = s.obj, ground = s.ground, drop = s.drop;
  const start = path[path.length - 1];
  const inPath = RT.inPath.fill(0);
  for (let i = 0; i < path.length; i++) inPath[path[i]] = 1;
  const dist = RT.dist.fill(0x3fffffff), prev = RT.prev.fill(-1);
  const hd = RT.hd, hc = RT.hc, cc = RT.cost.fill(-2);
  let hn = 0;
  const push = (d, c) => {
    let i = hn++; hd[i] = d; hc[i] = c;
    while (i > 0) { const p = (i - 1) >> 1; if (hd[p] <= hd[i]) break; const td = hd[p], tc = hc[p]; hd[p] = hd[i]; hc[p] = hc[i]; hd[i] = td; hc[i] = tc; i = p; }
  };
  dist[start] = 0; push(0, start);
  let goal = -1;
  while (hn) {
    // pop: верх, останній — на місце верху, просіяти вниз (як було)
    const d = hd[0], c = hc[0];
    hn--;
    if (hn) {
      hd[0] = hd[hn]; hc[0] = hc[hn];
      let i = 0;
      for (;;) { const l = 2 * i + 1, r = l + 1; let m = i; if (l < hn && hd[l] < hd[m]) m = l; if (r < hn && hd[r] < hd[m]) m = r; if (m === i) break; const td = hd[m], tc = hc[m]; hd[m] = hd[i]; hc[m] = hc[i]; hd[i] = td; hc[i] = tc; i = m; }
    }
    if (d !== dist[c]) continue;
    if (c !== start && ground[c] === G.STATION) { goal = c; break; }
    const x = c % W, y = (c / W) | 0;
    for (let k = 0; k < 4; k++) {
      const dx = DIRS[k][0], dy = DIRS[k][1];
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
      const m = ny * W + nx;
      // K51: ціна входу в клітинку (без «+2 поперек») залежить лише від клітинки — рахуємо раз за виклик (cc, -2 — ще ні)
      let cost = cc[m];
      if (cost === -2) {
        const ob = obj[m];
        if (inPath[m] || ob === O.WALL) cost = -1;
        else {
          cost = 10;
          const gm = ground[m];
          if (ob === O.TREE) cost = 30; else if (ob === O.ROCK) cost = 40;
          else if (gm === G.WATER) cost = 35;
          else if (gm === G.CHASM) cost = 60; else if (gm === G.FRAME) cost = 35;   // K26: яр — дерево + залізо
          if (drop[m]) cost += 25;
          // краще просторі місця: у вузькому коридорі потяг затуляє прохід, і гравцю не обійти його
          for (let e = 0; e < 4; e++) {
            const qx = nx + DIRS[e][0], qy = ny + DIRS[e][1];
            if (qx < 0 || qy < 0 || qx >= W || qy >= H) { cost += 8; continue; }
            const q = qy * W + qx;
            if (!inPath[q] && (obj[q] !== O.NONE || noFoot(ground[q]))) cost += 8;
          }
        }
        cc[m] = cost;
      }
      if (cost < 0) continue;
      if (dy) cost += 2;               // трохи краще прямо вздовж смуги
      const nd = d + cost;
      if (nd < dist[m]) { dist[m] = nd; prev[m] = c; push(nd, m); }
    }
  }
  let r;
  if (goal < 0) r = [start];
  else {
    r = [];
    for (let c = goal; c !== -1; c = prev[c]) r.push(c);
    r.reverse();
  }
  // K51: запам'ятати входи
  RT.w = W; RT.h = H; RT.pathN = path.length;
  if (RT.path.length < path.length) RT.path = new Int32Array(path.length * 2 + 16);
  for (let i = 0; i < path.length; i++) RT.path[i] = path[i];
  RT.obj.set(obj); RT.ground.set(ground); RT.drop.set(drop);
  RT.route = r.slice();
  return r;
}

const passable = (s, o, c, thruWagons) => s.obj[c] === O.NONE && !noFoot(s.ground[c]) && (thruWagons || !o.occ[c]);   // K26: noFoot

const freeCell = (s, c) => s.obj[c] === O.NONE && !noFoot(s.ground[c]);
const hittable = (s, c) => s.obj[c] === O.TREE || s.obj[c] === O.ROCK;
function sideNeed(s) {
  const E = s.path[s.path.length - 1], o = shared.get(s), N = o.N;
  if (N < 0) return -1;
  const ex = E % s.w, ey = (E / s.w) | 0, nx = N % s.w, ny = (N / s.w) | 0;
  const dx = nx - ex, dy = ny - ey;
  let best = -1, bc = 9;
  for (const [px, py] of [[dy, dx], [-dy, -dx]]) {
    const ax = ex + px, ay = ey + py, bx = nx + px, by = ny + py;
    if (ax < 0 || ay < 0 || bx < 0 || by < 0 || ax >= s.w || bx >= s.w || ay >= s.h || by >= s.h) continue;
    const a = ay * s.w + ax, b = by * s.w + bx;
    if (s.path.includes(a) || s.path.includes(b)) continue;
    const ok = (c) => freeCell(s, c) || hittable(s, c);
    if (!ok(a) || !ok(b)) continue;
    const cnt = (hittable(s, a) ? 1 : 0) + (hittable(s, b) ? 1 : 0);
    if (cnt === 0) return -1;
    if (cnt < bc) { bc = cnt; best = hittable(s, b) ? b : a; }
  }
  return best;
}

/** Карта відстаней BFS від набору клітинок (крізь вагони — потяг рухається, а нам потрібна оцінка). -1 — недосяжно */
function distMap(s, o, src) {
  const n = s.w * s.h, W = s.w;
  const d = new Int16Array(n).fill(-1), q = new Int32Array(n);
  let qh = 0, qt = 0;
  for (const c of src) if (d[c] < 0 && passable(s, o, c, true)) { d[c] = 0; q[qt++] = c; }
  while (qh < qt) {
    const c = q[qh++], x = c % W, y = (c / W) | 0;
    for (const [dx, dy] of DIRS) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= W || ny >= s.h) continue;
      const m = ny * W + nx;
      if (d[m] >= 0 || !passable(s, o, m, true)) continue;
      d[m] = d[c] + 1; q[qt++] = m;
    }
  }
  return d;
}

/**
 * BFS від клітинки гравця. mode 'adj': ціль — прохідна клітинка, в якої сусід задовольняє pred (повертає й того сусіда);
 * mode 'on': ціль — сама клітинка з pred. Повертає { next, goal, target } або null. maxD — межа глибини.
 */
/**
 * «Прокопатись»: Дейкстра від гравця, де дерево/скеля прохідні за ціну 6 — до клітинки, сусідньої з pred.
 * Повертає першу перешкоду на шляху (її рубати/бити) або -1, якщо перешкод нема чи шляху нема.
 */
function digTarget(s, o, start, pred) {
  const n = s.w * s.h, W = s.w;
  const dist = new Int32Array(n).fill(0x3fffffff), prev = new Int32Array(n).fill(-1);
  const heap = [];
  const push = (d, c) => { heap.push([d, c]); let i = heap.length - 1; while (i > 0) { const q = (i - 1) >> 1; if (heap[q][0] <= heap[i][0]) break; [heap[q], heap[i]] = [heap[i], heap[q]]; i = q; } };
  const pop = () => { const top = heap[0], last = heap.pop(); if (heap.length) { heap[0] = last; let i = 0; for (;;) { const l = 2 * i + 1, r = l + 1; let m = i; if (l < heap.length && heap[l][0] < heap[m][0]) m = l; if (r < heap.length && heap[r][0] < heap[m][0]) m = r; if (m === i) break; [heap[m], heap[i]] = [heap[i], heap[m]]; i = m; } } return top; };
  dist[start] = 0; push(0, start);
  while (heap.length) {
    const [d, c] = pop();
    if (d !== dist[c]) continue;
    const x = c % W, y = (c / W) | 0;
    let hit = false;
    if (s.obj[c] === O.NONE) for (const [dx, dy] of DIRS) {
      const nx = x + dx, ny = y + dy;
      if (nx >= 0 && ny >= 0 && nx < W && ny < s.h && pred(ny * W + nx)) { hit = true; break; }
    }
    if (hit) {
      let first = -1;
      for (let k = c; k !== -1; k = prev[k]) if (s.obj[k] !== O.NONE) first = k;
      return first;
    }
    if (d > 4000) continue;
    for (const [dx, dy] of DIRS) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= W || ny >= s.h) continue;
      const m = ny * W + nx;
      const ob = s.obj[m];
      if (ob === O.WALL || noFoot(s.ground[m])) continue;
      const nd = d + (ob === O.TREE || ob === O.ROCK ? 6 : 1);
      if (nd < dist[m]) { dist[m] = nd; prev[m] = c; push(nd, m); }
    }
  }
  return -1;
}

// K51: буфери BFS — спільні (раніше три нові масиви на кожен виклик, ~15 викликів на тик). «Відвідано» — мітка
// покоління vis[c] === gen замість заповнення par -2. Якщо pred раптом сам кличе bfs (busy) — свіжі буфери, як було.
const BF = { n: -1, gen: 0, vis: null, par: null, dep: null, q: null, busy: false };
function bfs(s, o, start, pred, mode, maxD = 400, thru = false) {
  const W = s.w, H = s.h, n = s.w * s.h;
  const own = !BF.busy;
  let vis, par, dep, q, gen;
  if (own) {
    if (BF.n !== n) { BF.n = n; BF.gen = 0; BF.vis = new Uint32Array(n); BF.par = new Int32Array(n); BF.dep = new Uint16Array(n); BF.q = new Int32Array(n); }
    if (++BF.gen >= 0xFFFFFFFF) { BF.gen = 1; BF.vis.fill(0); }
    vis = BF.vis; par = BF.par; dep = BF.dep; q = BF.q; gen = BF.gen;
    BF.busy = true;
  } else { vis = new Uint32Array(n); par = new Int32Array(n); dep = new Uint16Array(n); q = new Int32Array(n); gen = 1; }
  try {
    let qh = 0, qt = 0;
    vis[start] = gen; par[start] = -1; dep[start] = 0; q[qt++] = start;
    while (qh < qt) {
      const c = q[qh++];
      const x = c % W, y = (c / W) | 0;
      let tgt = -1;
      if (mode === 'on') { if (c !== start && pred(c)) tgt = c; }
      else for (let k = 0; k < 4; k++) {
        const nx = x + DIRS[k][0], ny = y + DIRS[k][1];
        if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
        if (pred(ny * W + nx)) { tgt = ny * W + nx; break; }
      }
      if (tgt >= 0) {
        let k = c;
        while (par[k] >= 0 && par[k] !== start) k = par[k];
        return { next: c === start ? start : k, goal: c, target: tgt, d: dep[c] };
      }
      if (dep[c] >= maxD) continue;
      for (let k = 0; k < 4; k++) {
        const nx = x + DIRS[k][0], ny = y + DIRS[k][1];
        if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
        const m = ny * W + nx;
        if (vis[m] === gen || !passable(s, o, m, thru) || (AV && AV.has(m))) continue;
        vis[m] = gen; par[m] = c; dep[m] = dep[c] + 1; q[qt++] = m;
      }
    }
    return null;
  } finally { if (own) BF.busy = false; }
}

/**
 * opts.buddy (K34) — напарник людини: дивиться, що роблять гравці, які не боти (що в руках, куди йдуть, що б'ють,
 *   чи стоять), і доповнює: людина з сокирою — бот на скелі й воду; з кайлом — дрова й рейки; з рейками — бот носить
 *   ресурси, а не кладе; стоїть ~10 с — бот бере найтерміновіше. Не бере інструмент/відро біля людини чи щойно нею
 *   покладене, не б'є її ціль, не підбирає купку з-під її рук, не стоїть на клітинці перед нею (обходить). Гудок 📯
 *   людини поблизу — «йди сюди»: бот іде до неї й 15 с робить найближче корисне. Людей нема — грає як звичайний бот.
 * opts.ghost — «імітація людини» для плейтестів: не реєструється ботом, заявок не ставить і не бачить.
 * opts.zeal (K60) — «характер» напарника 0..100: 0 — ввічливий (як K34), 100 — завзятий (звичайний бот-автопілот);
 *   між — плавно: радіус заявок людини, скільки триматимемо її інструменти й ціль, поріг «людина не будує», обхід.
 *   Лише вхід бота (бот шле звичайні команди) — у хеш стану не йде. Міняти на ходу — bot.setZeal(z).
 */
export const zealOf = (v) => (typeof v === 'number' && Number.isFinite(v) ? Math.max(0, Math.min(100, Math.round(v))) : null);
export function createBot(p, opts = {}) {
  const ghost = !!opts.ghost;
  let zeal = zealOf(opts.zeal) ?? (opts.buddy ? 0 : 100);
  let buddy = !!opts.buddy && zeal < 100;
  const m = { task: null, lastUse: -9, lx: 0, ly: 0, still: 0, jit: 0, since: 0, fail: {},
              hu: new Map(), follow: null, anchor: -1, anchorT: -1, hrole: null };
  // завдання, що щойно виявилось неможливим (вода за скелями, інструмент недосяжний), 5 с не обираємо знову
  const failed = (s, k) => m.fail[k] !== undefined && s.tick - m.fail[k] < 5 * TPS;
  const IDLE = { mx: 0, my: 0, use: false };

  function think(s) {
    const pl = s.players[p];
    if (!pl || pl.out || s.status !== 'run') return IDLE;
    const o = sh(s);
    if (!ghost) o.bots.set(p, s.tick);
    AV = null;
    const H = buddy ? watch(s, o, pl) : null;
    try { return think2(s, o, pl, H); } finally { AV = null; }
  }

  function think2(s, o, pl, H) {
    // застряг? (рухаємося, а позиція та сама) — коротко смикаємось убік
    if (m.jit > 0) { m.jit--; const a = (s.tick >> 3) & 1 ? 100 : -100; return m.jitX ? { mx: a, my: 0, use: false } : { mx: 0, my: a, use: false }; }
    const ctx = mkCtx(s, o, pl);
    if (!H || !H.length) { m.hrole = null; ctx.buddy = false; }   // людей нема — звичайний бот
    else {
      const r = buddyPre(ctx, H);
      if (r) { m.lx = pl.x; m.ly = pl.y; return r; }
    }
    for (let tries = 0; tries < 4; tries++) {
      if (!m.task || s.tick - m.since > 30 * TPS) { m.task = choose(ctx); m.since = s.tick; }
      // збирання ресурсів перериваємо, коли є рейки й нема чим їх класти: колія важливіша за запас
      else if (m.task.k === 'gather' && m.task.target === undefined && !m.task.bridge && s.tick % 10 === 0 && ctx.role !== 'mine' &&
               (pl.hold.k === I.NONE || pl.hold.k === I.AXE || pl.hold.k === I.PICK) && railsUrgent(ctx)) { m.task = { k: 'rails' }; m.since = s.tick; }
      // K13: котел гарячий — водонос кидає будь-яку роботу з порожніми руками/інструментом (інакше пожежа за розчисткою)
      else if (m.task.k !== 'water' && s.tick % 10 === 0 && (s.train.heat >= (ctx.role === 'all' ? 900 : 700) || s.train.fire) && (ctx.role !== 'build' || s.train.fire || ctx.buddy) &&
               (pl.hold.k === I.NONE || pl.hold.k === I.AXE || pl.hold.k === I.PICK || pl.hold.k === I.BUCKET || pl.hold.k === I.BUCKET_FULL) &&
               !claimedByOther(ctx.o, 'water', s) && feasible(ctx, { k: 'water' })) { m.task = { k: 'water' }; m.since = s.tick; }
      const out = runTask(ctx, m.task);
      if (globalThis.KOL_BOT_DEBUG) console.log('bot', p, JSON.stringify(m.task), '→', JSON.stringify(out));
      if (out) {
        if (out.mx || out.my) {
          if (Math.abs(pl.x - m.lx) + Math.abs(pl.y - m.ly) < 5 && (Math.abs(out.mx) > 20 || Math.abs(out.my) > 20)) {
            if (++m.still > 12) { m.still = 0; m.jit = 6; m.jitX = Math.abs(out.my) > Math.abs(out.mx); }
          } else m.still = 0;
        }
        m.lx = pl.x; m.ly = pl.y;
        return out;
      }
      if (m.since === s.tick) m.fail[m.task.k + (m.task.r || '')] = s.tick;
      m.task = null;
    }
    m.lx = pl.x; m.ly = pl.y;
    return IDLE;
  }

  function mkCtx(s, o, pl) {
    const bots = [...o.bots.keys()].sort((a, b) => a - b);
    const rank = bots.indexOf(p);
    let role = bots.length < 2 ? 'all' : rank % 2 === 0 ? 'build' : 'mine';
    if (m.hrole) role = m.hrole;   // K34: напарник — роль від того, що робить людина
    const cur = Math.floor(pl.y / U) * s.w + Math.floor(pl.x / U);
    return { s, o, pl, cur, role, nb: bots.length, P: s.params, cap: s.params.carryMax | 0, buddy: !!m.hrole };
  }

  // ---------- K34: напарник ----------
  const isTool = (k) => k === I.AXE || k === I.PICK || k === I.BUCKET || k === I.BUCKET_FULL;
  const cellOf = (s, q) => Math.floor(q.y / U) * s.w + Math.floor(q.x / U);
  const md = (s, a, b) => Math.abs((a % s.w) - (b % s.w)) + Math.abs(((a / s.w) | 0) - ((b / s.w) | 0));
  /** Обійти клітинки в ромбі радіуса r довкола c */
  function around(s, c, r, fn) {
    const x0 = c % s.w, y0 = (c / s.w) | 0;
    for (let y = Math.max(0, y0 - r); y <= Math.min(s.h - 1, y0 + r); y++) for (let x = Math.max(0, x0 - r); x <= Math.min(s.w - 1, x0 + r); x++) {
      const d = Math.abs(x - x0) + Math.abs(y - y0);
      if (d <= r) fn(y * s.w + x, d);
    }
  }
  /** Що роблять люди (гравці, що не боти): де були ~2 с, останній інструмент, що били, що поклали, коли щось робили */
  function watch(s, o, me) {
    const H = [], mc = cellOf(s, me);
    for (let q = 0; q < s.players.length; q++) {
      if (q === p || o.bots.has(q)) continue;
      const pl = s.players[q];
      if (!pl || pl.out) { m.hu.delete(q); continue; }
      let h = m.hu.get(q);
      if (!h) { h = { hx: [], tool: 0, toolT: -1e9, act: s.tick, hits: new Map(), put: new Map(), ax: pl.x, ay: pl.y }; m.hu.set(q, h); }
      h.hx.push(pl.x, pl.y);
      if (h.hx.length > 80) h.hx.splice(0, 2);       // 40 тиків = 2 с
      const k = pl.hold.k;
      if (k === I.AXE || k === I.PICK) { h.tool = k; h.toolT = s.tick; }
      if (Math.abs(pl.x - h.ax) + Math.abs(pl.y - h.ay) > 400) { h.act = s.tick; h.ax = pl.x; h.ay = pl.y; }
      const hc = cellOf(s, pl);
      for (const e of s.ev) {                          // події попереднього тику
        if (e.p !== q) continue;
        const c = e.y * s.w + e.x;
        if (e.t === 'chop' || e.t === 'rock') { h.hits.set(c, s.tick); h.act = s.tick; }
        else if (e.t === 'drop') { h.act = s.tick; if (c >= 0 && c < s.drop.length && isTool(s.drop[c])) h.put.set(c, s.tick); }
        else if (e.t === 'honk') { if (md(s, mc, hc) <= 12) { m.follow = { q, until: s.tick + 20 * TPS }; m.anchor = -1; } }
        else if (e.t !== 'step') h.act = s.tick;
      }
      for (const [c, t] of h.hits) if (s.tick - t > 3 * TPS || s.obj[c] === O.NONE) h.hits.delete(c);
      for (const [c, t] of h.put) if (s.tick - t > 8 * TPS || !isTool(s.drop[c])) h.put.delete(c);
      H.push({ q, pl, h, hc, d: md(s, mc, hc) });
    }
    H.sort((a, b) => a.d - b.d || a.q - b.q);
    return H;
  }
  /**
   * Перед вибором завдання: заявки від імені людей (їхні цілі, інструменти й купки поруч, «класти»/«вода», коли в
   * руках рейки/відро), клітинки, яких обходимо, роль від того, що робить найближча людина, і «йди за мною».
   * Повертає хід, якщо треба зійти з дороги чи йти до людини; інакше null.
   */
  function buddyPre(ctx, H) {
    const { s, o, cur } = ctx;
    const av = new Set();   // K60: клітинки людини; обхід у пошуку шляху (AV) і відступ — окремо
    let busy = false;
    // K60: характер — 0 ввічливий … 1 завзятий; заявки людини тануть плавно, обхід — східцями
    const z = zeal / 100, kz = 1 - z;
    const idleT = Math.round(10 * TPS * (1 - 0.8 * z));       // «людина не будує»: 10 с → 2 с
    const hitW = Math.round(3 * TPS * kz), putW = Math.round(8 * TPS * kz);   // скільки тримаємо її ціль / покладене
    const avZ = Math.max(TUNE.avB, TUNE.avS);
    const rTool = Math.round(2 * kz), rNear = Math.round(10 * kz), rWalk = 6 * kz, rLay = z < 0.5 ? Math.round(8 * (1 - 2 * z)) : -1;   // класти лишаємо їй лише до середини
    for (const { q, pl: hp, h, hc } of H) {
      const hx = hc % s.w, hy = (hc / s.w) | 0;
      const idle = s.tick - h.act >= idleT;
      if (!idle) busy = true;
      const hcl = (key) => o.claims.set(key, { p: q, t: s.tick });
      // не стояти на дорозі: клітинка людини, перед нею і та, куди вона йде (K60: завзятіший — менше обходить)
      if (z < avZ) av.add(hc);
      if (z < avZ && hx + hp.fx >= 0 && hy + hp.fy >= 0 && hx + hp.fx < s.w && hy + hp.fy < s.h) av.add((hy + hp.fy) * s.w + hx + hp.fx);
      if (z < TUNE.avH && h.hx.length >= 12) {
        const vx = hp.x - h.hx[h.hx.length - 12], vy = hp.y - h.hx[h.hx.length - 11];
        if (Math.abs(vx) + Math.abs(vy) > 400) {
          const ex = Math.abs(vx) >= Math.abs(vy) ? hx + Math.sign(vx) : hx, ey = Math.abs(vx) >= Math.abs(vy) ? hy : hy + Math.sign(vy);
          if (ex >= 0 && ey >= 0 && ex < s.w && ey < s.h) av.add(ey * s.w + ex);
        }
      }
      // її ціль: що била останні 3 с, а з інструментом — те, що поруч (≤ 2 клітинки)
      for (const [c, t] of h.hits) if (s.tick - t <= hitW) hcl('c' + c);
      const hk = hp.hold.k;
      if ((hk === I.AXE || hk === I.PICK) && rTool > 0) { const ob = hk === I.AXE ? O.TREE : O.ROCK; around(s, hc, rTool, (c) => { if (s.obj[c] === ob) hcl('c' + c); }); }
      // рейки в руках і йде класти (≤ 8 клітинок від кінця колії) чи запас колії ще є — класти лишаємо їй
      // (пробували лишати лише за ≤ 3 і не на тісному запасі: x̄ 65 проти 64, а перебивань 8,8 проти 1,6 на гру)
      // K34b: але запас колії < ~10 с ходу паротяга — кладемо й ми (небезпека важливіша за ввічливість)
      if (hk === I.RAIL && (md(s, hc, s.path[s.path.length - 1]) <= rLay || (z < 0.5 && s.path.length - 1 - Math.floor(s.train.pos / U) >= TUNE.urg)) && !leadShort(s)) hcl('lay');
      if ((hk === I.BUCKET || hk === I.BUCKET_FULL) && z < 0.8) hcl('water');
      if (idle || rNear <= 0) continue;
      // речі біля неї: інструмент/відро ≤ 2 клітинок, щойно нею покладене (≤ 10) чи те, до чого вона йде; купка під руками
      const ox = h.hx[0], oy = h.hx[1];
      around(s, hc, rNear, (c, d) => {
        const k = s.drop[c];
        if (k === I.NONE) return;
        if (isTool(k)) {
          const pt = h.put.get(c);
          if (d <= rTool || (pt !== undefined && s.tick - pt <= putW)) { hcl('c' + c); return; }
          if (d <= rWalk && hk !== k) {
            const cx = (c % s.w) * U + U / 2, cy = ((c / s.w) | 0) * U + U / 2;
            const was = Math.abs(cx - ox) + Math.abs(cy - oy), now = Math.abs(cx - hp.x) + Math.abs(cy - hp.y);
            if (was - now >= 1500) hcl('c' + c);    // за 2 с наблизилась на 1,5 клітинки — йде брати
          }
        } else if (d <= rTool && (hk === I.NONE || hk === k)) hcl('c' + c);
      });
    }
    // роль: людина з сокирою — ми на скелі й воду; з кайлом — дрова й рейки; стоїть ~10 с чи без інструмента — усе
    AV = z < TUNE.avB ? av : null;
    const a = H[0];
    let role = 'all';
    // (жорсткі ролі 'mine'/'build' пробували: повільна людина + бот, що чекає, — x̄ 58 зі 100 проти 72; тож роль
    //  лишається 'all', а «протилежне» — пріоритет ресурсу: людина з сокирою — ми спершу залізо, з кайлом — дрова)
    m.hpref = busy && a.h.toolT >= s.tick - 20 * TPS ? (a.h.tool === I.AXE ? I.IRON : I.WOOD) : 0;
    if (role !== m.hrole) {
      if (m.hrole && m.task && (m.task.k === 'gather' || m.task.k === 'idle' || m.task.k === 'rails')) m.task = null;
      m.hrole = role;
    }
    ctx.role = role;
    ctx.buddy = true;
    // гудок людини поруч — іти до неї; дійшли — 15 с робимо найближче до неї
    if (m.follow) {
      const f = m.follow, hp = s.players[f.q];
      if (!hp || hp.out || s.tick > f.until) m.follow = null;
      else {
        const hc = cellOf(s, hp);
        if (md(s, cur, hc) > 2) {
          const r = bfs(s, o, cur, (n) => n === hc, 'adj', 400) || bfs(s, o, cur, (n) => n === hc, 'adj', 400, true);
          if (r && r.next !== cur) return steer(ctx, r.next);
        }
        m.anchor = hc; m.anchorT = s.tick + 15 * TPS; m.follow = null;
        if (m.task && m.task.k !== 'deliver' && m.task.k !== 'lay') m.task = null;
      }
    }
    if (m.anchor >= 0 && s.tick > m.anchorT) m.anchor = -1;
    // стоїмо перед людиною чи на її дорозі — відступити вбік
    // K60: відступ — не одразу, а після терпіння tol·z с (0 — одразу, як K34)
    m.inFront = av.has(cur) ? (m.inFront | 0) + 1 : 0;
    if (z < TUNE.avS && m.inFront > Math.round(TUNE.tol * z * TPS)) {
      const x = cur % s.w, y = (cur / s.w) | 0;
      let best = -1, bd = -1;
      for (const [dx, dy] of DIRS) {
        const nx = x + dx, ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= s.w || ny >= s.h) continue;
        const n = ny * s.w + nx;
        if (!passable(s, o, n, false) || av.has(n)) continue;
        const d = Math.min(...H.map((u) => md(s, n, u.hc)));
        if (d > bd) { bd = d; best = n; }
      }
      if (best >= 0) return steer(ctx, best);
    }
    return null;
  }

  // ---------- заявки ----------
  const claimedByOther = (o, key, s) => { if (ghost) return false; const c = o.claims.get(key); return !!c && c.p !== p && c.t >= s.tick - 6; };
  const claim = (o, key, s) => { if (!ghost) o.claims.set(key, { p, t: s.tick }); };

  // ---------- примітиви ----------
  function steer(ctx, cell) {
    const { s, pl } = ctx;
    const tx = (cell % s.w) * U + U / 2, ty = ((cell / s.w) | 0) * U + U / 2;
    const dx = tx - pl.x, dy = ty - pl.y;
    const l = Math.max(Math.abs(dx), Math.abs(dy)) || 1;
    return { mx: Math.round(100 * dx / l), my: Math.round(100 * dy / l), use: false };
  }
  function press(ctx) {
    const { s, pl } = ctx;
    if (pl.cd > 0 || m.lastUse === s.tick - 1) return { mx: 0, my: 0, use: false };
    m.lastUse = s.tick;
    return { mx: 0, my: 0, use: true };
  }
  /** Стати поруч із клітинкою, що задовольняє pred, повернутись і натиснути. null — нема досяжної цілі. */
  function actAdj(ctx, pred, key, maxD) {
    const { s, o, pl, cur } = ctx;
    const cx = cur % s.w, cy = (cur / s.w) | 0;
    // уже поруч: спершу та, куди дивимось
    const order = [[pl.fx, pl.fy], ...DIRS.filter(([a, b]) => a !== pl.fx || b !== pl.fy)];
    for (const [dx, dy] of order) {
      const nx = cx + dx, ny = cy + dy;
      if (nx < 0 || ny < 0 || nx >= s.w || ny >= s.h) continue;
      const n = ny * s.w + nx;
      if (!pred(n)) continue;
      m.tg = n;
      if (key) claim(o, key(n), s);
      if (pl.fx === dx && pl.fy === dy) return press(ctx);
      return { mx: dx * 10, my: dy * 10, use: false };     // лише повернутись (менше мертвої зони — не рушаємо)
    }
    const r = bfs(s, o, cur, pred, 'adj', maxD) || bfs(s, o, cur, pred, 'adj', maxD, true);   // за потягом — чекаємо, доки проїде
    if (!r) return null;
    m.tg = r.target;
    if (key) claim(o, key(r.target), s);
    return steer(ctx, r.next);
  }
  /** K13: покласти річ на сусідню вільну клітинку, найближчу до goal (клітинка); нема поруч — як actAdj */
  function dropToward(ctx, goal) {
    const { s, cur } = ctx;
    const gx = goal % s.w, gy = (goal / s.w) | 0, cx = cur % s.w, cy = (cur / s.w) | 0;
    let best = -1, bd = 1e9;
    for (const [dx, dy] of DIRS) {
      const x = cx + dx, y = cy + dy;
      if (x < 0 || y < 0 || x >= s.w || y >= s.h || !freeDrop(ctx, y * s.w + x)) continue;
      const d = Math.abs(x - gx) + Math.abs(y - gy);
      if (d < bd) { bd = d; best = y * s.w + x; }
    }
    if (best >= 0) return actAdj(ctx, (n) => n === best);
    return actAdj(ctx, (n) => freeDrop(ctx, n));
  }
  /** Стати на клітинку з pred (підбір стосу ногами) */
  function walkOn(ctx, pred, maxD) {
    const { s, o, cur } = ctx;
    const r = bfs(s, o, cur, pred, 'on', maxD) || bfs(s, o, cur, pred, 'on', maxD, true);
    if (!r) return null;
    return steer(ctx, r.next);
  }

  // ---------- оцінки ----------
  const tool = (k) => (k === I.WOOD ? I.AXE : I.PICK);
  const objOf = (k) => (k === I.WOOD ? O.TREE : O.ROCK);
  const stockKey = (k) => (k === I.WOOD ? 'wood' : 'iron');
  function waterNeeded(ctx) {
    const { s } = ctx, t = s.train;
    if (s.tick < startTick(s) - 3 * TPS) return false;
    return t.fire > 0 || t.heat >= 600;
  }
  function freeDrop(ctx, n) {
    const { s, o } = ctx;
    if (s.obj[n] !== O.NONE || noFoot(s.ground[n]) || s.drop[n] !== I.NONE || o.occ[n]) return false;
    if (o.routeSet.has(n) || s.path.includes(n)) return false;
    const e = s.path[s.path.length - 1];
    if (Math.abs((e % s.w) - (n % s.w)) + Math.abs(((e / s.w) | 0) - ((n / s.w) | 0)) <= 1) return false;
    return true;
  }
  /** K13: скільки речей k лежить у ромбі радіуса r довкола клітинки c */
  function looseAround(s, c, k, r) {
    const x0 = c % s.w, y0 = (c / s.w) | 0;
    let n = 0;
    for (let y = Math.max(0, y0 - r); y <= Math.min(s.h - 1, y0 + r); y++) for (let x = Math.max(0, x0 - r); x <= Math.min(s.w - 1, x0 + r); x++) {
      if (Math.abs(x - x0) + Math.abs(y - y0) > r) continue;
      const i = y * s.w + x;
      if (s.drop[i] === k) n += s.dropN[i];
    }
    return n;
  }
  const hasDrop = (s, k) => { for (let i = 0; i < s.drop.length; i++) if (s.drop[i] === k) return true; return false; };
  function needOf(ctx, k) {    // скільки ще влізе на склад (з урахуванням того, що в руках)
    const { s, P } = ctx;
    return (P.storeMax | 0) - s.train[stockKey(k)];
  }

  function railsUrgent(ctx) {
    const { s, o } = ctx, N = o.N;
    if (N < 0 || o.side >= 0 || !canLay(s, N % s.w, (N / s.w) | 0) || claimedByOther(o, 'lay', s)) return false;
    if (!(s.train.rails > 0 || hasDrop(s, I.RAIL))) return false;
    const lead = s.path.length - 1 - Math.floor(s.train.pos / U);
    return lead < TUNE.urg || (railsFull(ctx) && lead < TUNE.far);   // K13: повний стос у майстерні — теж пора
  }
  /** K13: у майстерні вже повний стос (ходити по рейки варто лише по повний — інакше бігаємо туди-сюди по 2) */
  const railsFull = (ctx) => ctx.s.train.rails >= Math.min(ctx.cap, ctx.P.railsMax | 0);

  // ---------- вибір завдання ----------
  function choose(ctx) {
    const { s, o, pl, role } = ctx;
    const h = pl.hold.k, N = o.N;
    if (h === I.WOOD || h === I.IRON) return { k: 'deliver' };
    if (h === I.RAIL) return { k: 'lay' };
    if (h === I.BUCKET_FULL) return s.train.heat >= 250 || s.train.fire ? { k: 'water' } : { k: 'drop' };
    const wn = waterNeeded(ctx);
    const waterOk = !claimedByOther(o, 'water', s);
    if (wn && waterOk && !failed(s, 'water') && (role !== 'build' || s.train.fire || ctx.buddy)) return { k: 'water' };
    // до води не дійти (за скелями/лісом) — прокопуємось, доки котел ще терпить
    if (s.train.started && s.train.heat >= 300 && !bfs(s, o, ctx.cur, (n) => s.ground[n] === G.WATER, 'adj', 400, true)) {
      const c = digTarget(s, o, ctx.cur, (n) => s.ground[n] === G.WATER);
      if (c >= 0) {
        const tk = { k: 'gather', r: s.obj[c] === O.TREE ? I.WOOD : I.IRON, target: c };
        if (!failed(s, tk.k + tk.r) && feasible(ctx, tk)) return tk;
      }
    }
    if (h === I.BUCKET) return { k: bfs(s, o, ctx.cur, (n) => s.ground[n] === G.WATER, 'adj', 12) ? 'fill' : 'drop' };
    const list = [];
    // перешкода на маршруті просто перед кінцем колії (K13: першою — лише коли є що класти; інакше спершу ресурс,
    // якого бракує, — розчистку маршруту gather і так обирає першою)
    const railsAvail = s.train.rails > 0 || hasDrop(s, I.RAIL) || s.train.craftT > 0;
    if (N >= 0 && (railsAvail || role !== 'all')) {
      if (s.drop[N]) list.push({ k: 'clearN' });
      else if (s.obj[N] === O.TREE) list.push({ k: 'gather', r: I.WOOD, target: N });
      else if (s.obj[N] === O.ROCK) list.push({ k: 'gather', r: I.IRON, target: N });
      else if (bridgeRes(s, N)) list.push({ k: 'gather', r: bridgeRes(s, N), bridge: 1 });   // K26: і яр
      else if (o.side >= 0) list.push({ k: 'gather', r: s.obj[o.side] === O.TREE ? I.WOOD : I.IRON, target: o.side });
    }
    const lead = s.path.length - 1 - Math.floor(s.train.pos / U);
    const rails = { k: 'rails' };
    const canRails = (s.train.rails > 0 || hasDrop(s, I.RAIL)) && N >= 0 && canLay(s, N % s.w, (N / s.w) | 0) && !claimedByOther(o, 'lay', s);
    const wNeed = needOf(ctx, I.WOOD), iNeed = needOf(ctx, I.IRON);
    const wood = { k: 'gather', r: I.WOOD }, iron = { k: 'gather', r: I.IRON };
    let res = s.train.wood <= s.train.iron ? [wood, iron] : [iron, wood];
    // K34: «протилежний» ресурс лише на рівних (жорсткий пріоритет: x̄ 65 проти 72 — склад важливіший)
    if (ctx.buddy && m.hpref && Math.abs(s.train.wood - s.train.iron) <= 2) res = m.hpref === I.IRON ? [iron, wood] : [wood, iron];
    if (role === 'all') {
      // K13: майстерня повна (стоїть) чи склад майже повний — класти, а не збирати ще
      if (canRails && (lead < TUNE.urg || (railsFull(ctx) && lead < TUNE.far) || (wNeed <= 2 && iNeed <= 2))) list.push(rails);
      list.push(...res);
      if (canRails) list.push(rails);
    } else if (role === 'build') {
      if (canRails) list.push(rails);
      list.push(wood, iron);
    } else {
      if (wn && waterOk) list.push({ k: 'water' });
      list.push(iron, wood);
      if (canRails) list.push(rails);
    }
    if (wn && waterOk) list.push({ k: 'water' });
    for (const t of list) {
      if (failed(s, t.k + (t.r || ''))) continue;
      if (t.k === 'gather' && !t.target && !t.bridge && needOf(ctx, t.r) <= 0) continue;
      if (feasible(ctx, t)) return t;
    }
    // K13: склад повний, рейки несе інший — не стояти: розчищати маршрут наперед (дрова/залізо ляжуть біля колії)
    for (const c of o.route.slice(2, 30)) {
      if ((s.obj[c] !== O.TREE && s.obj[c] !== O.ROCK) || claimedByOther(o, 'c' + c, s)) continue;
      const t = { k: 'gather', r: s.obj[c] === O.TREE ? I.WOOD : I.IRON, target: c };
      if (!failed(s, t.k + t.r) && feasible(ctx, t)) return t;
    }
    // K34: класти лишили людині — тримаємо рейки готовими: приносимо стос із майстерні їй під руку попереду
    if (ctx.buddy && s.train.rails > 0 && !hasDrop(s, I.RAIL) && pl.hold.k === I.NONE && !failed(s, 'rails')) return { k: 'rails' };
    return { k: 'idle' };
  }

  /** K34b: запас колії перед паротягом менший за ~10 с його ходу (до старту — не поспішаємо) */
  function leadShort(s) {
    if (!s.train.started) return false;
    const lead = s.path.length - 1 - Math.floor(s.train.pos / U);
    const cps = Math.max(1, s.train.speed) * TPS / (1000 * U);   // клітинок/с
    return lead < Math.max(4, 10 * cps);
  }
  function feasible(ctx, t) {
    const { s, pl } = ctx;
    if (t.k === 'gather') {
      if (t.target !== undefined && claimedByOther(ctx.o, 'c' + t.target, s)) return false;   // K34: її б'є людина
      // інструмент: у руках, на підлозі — або вже є що збирати
      return pl.hold.k === tool(t.r) || hasDrop(s, tool(t.r)) || (!t.target && hasDrop(s, t.r));
    }
    if (t.k === 'water') return pl.hold.k === I.BUCKET || pl.hold.k === I.BUCKET_FULL || hasDrop(s, I.BUCKET) || hasDrop(s, I.BUCKET_FULL);
    return true;
  }

  // ---------- виконання ----------
  function runTask(ctx, t) {
    const { s, o, pl } = ctx;
    const h = pl.hold.k, N = o.N;
    const isN = (n) => n === N;
    switch (t.k) {
      case 'idle': {
        if (s.tick % 40 === 0) return null;      // зрідка переобирати
        return { mx: 0, my: 0, use: false };
      }
      case 'drop': {
        if (h === I.NONE) return null;
        if (h === I.BUCKET_FULL) { const r = actAdj(ctx, (n) => o.wet.has(n) && freeDrop(ctx, n), null, 80); if (r) return r; }   // K13
        if (h === I.BUCKET || h === I.BUCKET_FULL) { const r = actAdj(ctx, (n) => o.front.has(n) && freeDrop(ctx, n), null, 25); if (r) return r; }
        return actAdj(ctx, (n) => freeDrop(ctx, n));
      }
      case 'fill': {
        if (h !== I.BUCKET) return null;
        return actAdj(ctx, (n) => s.ground[n] === G.WATER, null, 12);
      }
      case 'deliver': {
        if (h !== I.WOOD && h !== I.IRON) return null;
        if (N >= 0 && bridgeRes(s, N) === h) return actAdj(ctx, isN);   // K26: дерево на воду/урвище, залізо на каркас
        if (pl.hold.n < ctx.cap) {     // добрати поруч ногами
          const r = walkOn(ctx, (n) => s.drop[n] === h && !claimedByOther(o, 'c' + n, s), 8);   // K13: 4 → 8 — добрати свої купки
          if (r) return r;
        }
        if (needOf(ctx, h) <= 0) { t.k = 'drop'; return runTask(ctx, t); }
        return actAdj(ctx, (n) => { const w = o.occ[n] ? wagonAt(s, n % s.w, (n / s.w) | 0) : -1; return w === 1 || w === 2; });
      }
      case 'lay': {
        if (h !== I.RAIL) return null;
        const sideTool = o.side >= 0 ? (s.obj[o.side] === O.TREE ? I.AXE : I.PICK) : 0;
        if (ctx.buddy && claimedByOther(o, 'lay', s)) { const r = actAdj(ctx, (n) => o.front.has(n) && freeDrop(ctx, n), null, 40); if (r) return r; }
        if (N >= 0 && canLay(s, N % s.w, (N / s.w) | 0) && !(sideTool && hasDrop(s, sideTool))) { claim(o, 'lay', s); return actAdj(ctx, isN); }
        // K34: рейки несе людина — свої кладемо стосом попереду (їй під руку), а не обганяємо її
        if (ctx.buddy && claimedByOther(o, 'lay', s)) { const r = actAdj(ctx, (n) => o.front.has(n) && freeDrop(ctx, n), null, 40); if (r) return r; }
        t.k = 'drop'; return runTask(ctx, t);
      }
      case 'rails': {
        if (h === I.RAIL) return null;
        if (h !== I.NONE) { const r = actAdj(ctx, (n) => freeDrop(ctx, n)); return r; }
        claim(o, 'lay', s);
        const loose = actAdj(ctx, (n) => s.drop[n] === I.RAIL && !claimedByOther(o, 'c' + n, s), (n) => 'c' + n, 12);   // рейки, що хтось поклав
        if (loose) return loose;
        if (s.train.rails <= 0) return null;
        return actAdj(ctx, (n) => o.occ[n] && wagonAt(s, n % s.w, (n / s.w) | 0) === 2);
      }
      case 'clearN': {
        if (N < 0 || !s.drop[N]) return null;
        if (h !== I.NONE) return actAdj(ctx, (n) => freeDrop(ctx, n));
        return actAdj(ctx, isN);
      }
      case 'water': {
        if (!waterNeeded(ctx) && s.train.heat < 250 && h !== I.BUCKET_FULL) return null;
        claim(o, 'water', s);
        if (h === I.BUCKET_FULL) return actAdj(ctx, (n) => o.occ[n] && wagonAt(s, n % s.w, (n / s.w) | 0) === 0);
        if (h === I.BUCKET) return actAdj(ctx, (n) => s.ground[n] === G.WATER);
        if (h === I.WOOD || h === I.IRON || h === I.RAIL) return null;   // спершу віднести (choose)
        // порожні руки чи інструмент (обміняється) — по відро, спершу повне
        const full = actAdj(ctx, (n) => s.drop[n] === I.BUCKET_FULL && !claimedByOther(o, 'c' + n, s), (n) => 'c' + n, 60);
        if (full) return full;
        return actAdj(ctx, (n) => s.drop[n] === I.BUCKET && !claimedByOther(o, 'c' + n, s), (n) => 'c' + n);
      }
      case 'gather': return gather(ctx, t);
    }
    return null;
  }

  function gather(ctx, t) {
    const { s, o, pl, cap } = ctx;
    const k = t.r, T = tool(k), ob = objOf(k), h = pl.hold.k, N = o.N;
    if (h === k) return null;                                  // уже несемо — choose дасть deliver
    if (t.target !== undefined && s.obj[t.target] !== ob && h !== T) return null;
    if (t.target !== undefined && claimedByOther(o, 'c' + t.target, s)) return null;   // K34: ціль узяла людина
    if (t.bridge && !(N >= 0 && bridgeRes(s, N) === k)) return null;
    // скільки нашого ресурсу лежить поруч
    const loose = (maxD) => bfs(s, o, ctx.cur, (n) => s.drop[n] === k && !claimedByOther(o, 'c' + n, s), 'adj', maxD);
    if (h === T) {
      let near = 0;
      const cx = ctx.cur % s.w, cy = (ctx.cur / s.w) | 0;
      for (let y = Math.max(0, cy - 3); y <= Math.min(s.h - 1, cy + 3); y++) for (let x = Math.max(0, cx - 3); x <= Math.min(s.w - 1, cx + 3); x++) if (s.drop[y * s.w + x] === k) near += s.dropN[y * s.w + x];
      // K13: і купки з того, що ми самі щойно збили (вони могли лишитися далі 3 клітинок)
      if (t.cut) { let mine = 0; for (const c of t.cut) if (s.drop[c] === k) mine += s.dropN[c]; if (mine > near) near = mine; }
      if (t.bridge ? near >= 1 : near >= Math.min(cap, Math.max(1, needOf(ctx, k)))) t.enough = 1;   // K13: досить — скільки треба складу    // рішення «досить» тримаємо (інакше смикається на межі радіуса)
      const enough = t.enough && near > 0;
      if (!enough ||(t.target !== undefined && s.obj[t.target] === ob)) {
        // що бити: ціль на маршруті, інакше найближче (спершу ті, що на маршруті попереду)
        if (t.target !== undefined && s.obj[t.target] === ob) { const r = actAdj(ctx, (n) => n === t.target); if (r) return r; }
        // обрану ціль тримаємо, доки вона є (інакше межа пошуку смикає туди-сюди між двома)
        if (t.hit !== undefined && s.obj[t.hit] === ob && !claimedByOther(o, 'c' + t.hit, s)) { const r = actAdj(ctx, (n) => n === t.hit, (n) => 'c' + n); if (r) return r; }
        t.hit = undefined;
        // нова ціль: найкоротший рейс «я → ціль → склад»; біля колії (розширити коридор) — з бонусом
        const dB = distMap(s, o, [ctx.cur]);
        const storeX = Math.floor(wagonPos(s, 2).x / U);
        let best = -1, bs = 1e9;
        for (let c = 0; c < s.obj.length; c++) {
          if (s.obj[c] !== ob || claimedByOther(o, 'c' + c, s)) continue;
          const x = c % s.w, y = (c / s.w) | 0;
          let sc = 1e9;
          for (const [dx, dy] of DIRS) {
            const nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= s.w || ny >= s.h) continue;
            const q = ny * s.w + nx;
            if (dB[q] >= 0 && o.dS[q] >= 0) sc = Math.min(sc, dB[q] + o.dS[q]);
          }
          if (sc >= 1e9) continue;
          if (o.near.has(c)) sc -= 4;
          if (m.anchor >= 0) sc += 3 * md(s, c, m.anchor);   // K34: покликали гудком — найближче до людини
          if (o.ahead.has(c)) sc -= 10;   // K13: розчистка маршруту — однаково треба
          if (x < storeX) sc += 2 * (storeX - x);   // K13: позаду складу — інструмент лишився б там, де потяг уже проїхав
          if (sc < bs) { bs = sc; best = c; }
        }
        if (best >= 0) {
          t.hit = best;
          (t.cut || (t.cut = [])).push(best);
          const r = actAdj(ctx, (n) => n === best, (n) => 'c' + n);
          if (r) return r;
        }
        if (!near) return null;
      }
      // досить — кладемо інструмент поруч (K13: з того боку, що ближче до кінця колії — туди ще повернемось) і збираємо
      return dropToward(ctx, s.path[s.path.length - 1]);
    }
    if (h !== I.NONE && h !== I.AXE && h !== I.PICK && h !== I.BUCKET && h !== I.BUCKET_FULL) return null;
    // порожні руки (або інший інструмент): є що зібрати поруч — збираємо, ні — по інструмент
    if (t.target === undefined) {
      const l = loose(h === I.NONE ? 30 : 6);
      // K13: не носити по одній — купка далі 3 кроків варта ходіння, лише якщо біля неї набереться min(руки, потреба)
      const worth = l && (l.d <= 3 || looseAround(s, l.target, k, 4) >= Math.min(cap, Math.max(1, needOf(ctx, k))) ||
        !(h === T || hasDrop(s, T)));
      if (l && worth) {
        if (h !== I.NONE) return actAdj(ctx, (n) => freeDrop(ctx, n));
        return actAdj(ctx, (n) => s.drop[n] === k && !claimedByOther(o, 'c' + n, s), (n) => 'c' + n, 30);
      }
    }
    return actAdj(ctx, (n) => s.drop[n] === T && !claimedByOther(o, 'c' + n, s), (n) => 'c' + n);
  }

  // K60: характер на ходу (повзунок у панелі); 100 — звичайний бот, ролі/роль-пам'ять напарника скидаємо
  const setZeal = (v) => {
    const zz = zealOf(v); if (zz === null || !opts.buddy) return;
    zeal = zz; const was = buddy; buddy = zeal < 100;
    if (was && !buddy) { m.hrole = null; m.hpref = 0; m.follow = null; m.anchor = -1; m.task = null; }
  };
  return { p, think, setZeal, get zeal() { return zeal; }, dbg: () => (m.task ? m.task.k + (m.task.r ? ':' + m.task.r : '') + (m.task.target !== undefined ? '@' + m.task.target : '') + (m.task.bridge ? ':br' : '') : '-') };
}

/** Для налагодження й тестів: що бот думає про світ (маршрут, наступна клітинка N, бічна перешкода) */
export function botView(s) { return shared.get(s) || null; }
