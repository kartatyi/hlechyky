// «Колія» — бот-автопілот (детермінований, бачить увесь стан). Власник: K2 (далі — задача «Бот»).
// Контракт: createBot(p) → { p, think(state) → { mx, my (цілі -100..100), use (bool) } }
//
// Як думає: щотику (1) оцінює світ — куди далі класти колію (маршрут Дейкстрою від кінця колії до станції, дерева/скелі/
// вода/речі на шляху дорожчі), де вагони; (2) має поточне завдання (рубати/бити/нести на склад/взяти рейки/класти/вода/
// покласти річ) і виконує його двома примітивами: «стати поруч і натиснути» (BFS до клітинки, сусідньої з ціллю, повернутись
// і натиснути use) та «стати на клітинку» (підбір свого стосу). Завдання переобирається, коли скінчилось чи стало неможливим.
// Кілька ботів ділять ролі за рангом серед ботів (парні — будівник: дерево, рейки, розчистка; непарні — копач: залізо, вода,
// скелі) і «займають» цілі (дерево, інструмент, «вода», «класти») у спільному реєстрі на стан, щоб не битися за одне.
import { U, G, O, I, WAGONS, TPS, wagonPos, wagonAt, canLay, startTick } from './rules.js';

const DIRS = [[1, 0], [0, 1], [0, -1], [-1, 0]];
const shared = new WeakMap();    // state → { t, occ, route, routeSet, N, rt, pl, claims: Map, bots: Map }

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
function planRoute(s) {
  const n = s.w * s.h, W = s.w;
  const start = s.path[s.path.length - 1];
  const inPath = new Uint8Array(n);
  for (const c of s.path) inPath[c] = 1;
  const dist = new Int32Array(n).fill(0x3fffffff), prev = new Int32Array(n).fill(-1);
  const heap = [];   // [d, c]
  const push = (d, c) => { heap.push([d, c]); let i = heap.length - 1; while (i > 0) { const p = (i - 1) >> 1; if (heap[p][0] <= heap[i][0]) break; [heap[p], heap[i]] = [heap[i], heap[p]]; i = p; } };
  const pop = () => { const top = heap[0], last = heap.pop(); if (heap.length) { heap[0] = last; let i = 0; for (;;) { const l = 2 * i + 1, r = l + 1; let m = i; if (l < heap.length && heap[l][0] < heap[m][0]) m = l; if (r < heap.length && heap[r][0] < heap[m][0]) m = r; if (m === i) break; [heap[m], heap[i]] = [heap[i], heap[m]]; i = m; } } return top; };
  dist[start] = 0; push(0, start);
  let goal = -1;
  while (heap.length) {
    const [d, c] = pop();
    if (d !== dist[c]) continue;
    if (c !== start && s.ground[c] === G.STATION) { goal = c; break; }
    const x = c % W, y = (c / W) | 0;
    for (const [dx, dy] of DIRS) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= W || ny >= s.h) continue;
      const m = ny * W + nx;
      if (inPath[m]) continue;
      const ob = s.obj[m];
      if (ob === O.WALL) continue;
      let cost = 10;
      if (ob === O.TREE) cost = 30; else if (ob === O.ROCK) cost = 40;
      else if (s.ground[m] === G.WATER) cost = 35;
      if (s.drop[m]) cost += 25;
      if (dy) cost += 2;               // трохи краще прямо вздовж смуги
      // краще просторі місця: у вузькому коридорі потяг затуляє прохід, і гравцю не обійти його
      for (const [ex, ey] of DIRS) {
        const qx = nx + ex, qy = ny + ey;
        if (qx < 0 || qy < 0 || qx >= W || qy >= s.h) { cost += 8; continue; }
        const q = qy * W + qx;
        if (!inPath[q] && (s.obj[q] !== O.NONE || s.ground[q] === G.WATER)) cost += 8;
      }
      const nd = d + cost;
      if (nd < dist[m]) { dist[m] = nd; prev[m] = c; push(nd, m); }
    }
  }
  if (goal < 0) return [start];
  const r = [];
  for (let c = goal; c !== -1; c = prev[c]) r.push(c);
  return r.reverse();
}

const passable = (s, o, c, thruWagons) => s.obj[c] === O.NONE && s.ground[c] !== G.WATER && (thruWagons || !o.occ[c]);

const freeCell = (s, c) => s.obj[c] === O.NONE && s.ground[c] !== G.WATER;
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
function bfs(s, o, start, pred, mode, maxD = 400, thru = false) {
  const W = s.w, n = s.w * s.h;
  const par = new Int32Array(n).fill(-2), dep = new Uint16Array(n);
  const q = new Int32Array(n);
  let qh = 0, qt = 0;
  par[start] = -1; q[qt++] = start;
  while (qh < qt) {
    const c = q[qh++];
    const x = c % W, y = (c / W) | 0;
    let tgt = -1;
    if (mode === 'on') { if (c !== start && pred(c)) tgt = c; }
    else for (const [dx, dy] of DIRS) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= W || ny >= s.h) continue;
      if (pred(ny * W + nx)) { tgt = ny * W + nx; break; }
    }
    if (tgt >= 0) {
      let k = c;
      while (par[k] >= 0 && par[k] !== start) k = par[k];
      return { next: c === start ? start : k, goal: c, target: tgt, d: dep[c] };
    }
    if (dep[c] >= maxD) continue;
    for (const [dx, dy] of DIRS) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= W || ny >= s.h) continue;
      const m = ny * W + nx;
      if (par[m] !== -2 || !passable(s, o, m, thru)) continue;
      par[m] = c; dep[m] = dep[c] + 1; q[qt++] = m;
    }
  }
  return null;
}

export function createBot(p) {
  const m = { task: null, lastUse: -9, lx: 0, ly: 0, still: 0, jit: 0, since: 0 };
  const IDLE = { mx: 0, my: 0, use: false };

  function think(s) {
    const pl = s.players[p];
    if (!pl || pl.out || s.status !== 'run') return IDLE;
    const o = sh(s);
    o.bots.set(p, s.tick);
    // застряг? (рухаємося, а позиція та сама) — коротко смикаємось убік
    if (m.jit > 0) { m.jit--; const a = (s.tick >> 3) & 1 ? 100 : -100; return m.jitX ? { mx: a, my: 0, use: false } : { mx: 0, my: a, use: false }; }
    const ctx = mkCtx(s, o, pl);
    for (let tries = 0; tries < 4; tries++) {
      if (!m.task || s.tick - m.since > 30 * TPS) { m.task = choose(ctx); m.since = s.tick; }
      // збирання ресурсів перериваємо, коли є рейки й нема чим їх класти: колія важливіша за запас
      else if (m.task.k === 'gather' && m.task.target === undefined && !m.task.bridge && s.tick % 10 === 0 && ctx.role !== 'mine' &&
               (pl.hold.k === I.NONE || pl.hold.k === I.AXE || pl.hold.k === I.PICK) && railsUrgent(ctx)) { m.task = { k: 'rails' }; m.since = s.tick; }
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
      m.task = null;
    }
    m.lx = pl.x; m.ly = pl.y;
    return IDLE;
  }

  function mkCtx(s, o, pl) {
    const bots = [...o.bots.keys()].sort((a, b) => a - b);
    const rank = bots.indexOf(p);
    const role = bots.length < 2 ? 'all' : rank % 2 === 0 ? 'build' : 'mine';
    const cur = Math.floor(pl.y / U) * s.w + Math.floor(pl.x / U);
    return { s, o, pl, cur, role, nb: bots.length, P: s.params, cap: s.params.carryMax | 0 };
  }

  // ---------- заявки ----------
  const claimedByOther = (o, key, s) => { const c = o.claims.get(key); return !!c && c.p !== p && c.t >= s.tick - 6; };
  const claim = (o, key, s) => o.claims.set(key, { p, t: s.tick });

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
    if (s.obj[n] !== O.NONE || s.ground[n] === G.WATER || s.drop[n] !== I.NONE || o.occ[n]) return false;
    if (o.routeSet.has(n) || s.path.includes(n)) return false;
    const e = s.path[s.path.length - 1];
    if (Math.abs((e % s.w) - (n % s.w)) + Math.abs(((e / s.w) | 0) - ((n / s.w) | 0)) <= 1) return false;
    return true;
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
    return s.path.length - 1 - Math.floor(s.train.pos / U) < 12;
  }

  // ---------- вибір завдання ----------
  function choose(ctx) {
    const { s, o, pl, role } = ctx;
    const h = pl.hold.k, N = o.N;
    if (h === I.WOOD || h === I.IRON) return { k: 'deliver' };
    if (h === I.RAIL) return { k: 'lay' };
    if (h === I.BUCKET_FULL) return s.train.heat >= 250 || s.train.fire ? { k: 'water' } : { k: 'drop' };
    const wn = waterNeeded(ctx);
    const waterOk = !claimedByOther(o, 'water', s);
    if (wn && waterOk && (role !== 'build' || s.train.fire)) return { k: 'water' };
    if (h === I.BUCKET) return { k: bfs(s, o, ctx.cur, (n) => s.ground[n] === G.WATER, 'adj', 12) ? 'fill' : 'drop' };
    const list = [];
    // перешкода на маршруті просто перед кінцем колії
    if (N >= 0) {
      if (s.drop[N]) list.push({ k: 'clearN' });
      else if (s.obj[N] === O.TREE) list.push({ k: 'gather', r: I.WOOD, target: N });
      else if (s.obj[N] === O.ROCK) list.push({ k: 'gather', r: I.IRON, target: N });
      else if (s.ground[N] === G.WATER) list.push({ k: 'gather', r: I.WOOD, bridge: 1 });
      else if (o.side >= 0) list.push({ k: 'gather', r: s.obj[o.side] === O.TREE ? I.WOOD : I.IRON, target: o.side });
    }
    const lead = s.path.length - 1 - Math.floor(s.train.pos / U);
    const rails = { k: 'rails' };
    const canRails = (s.train.rails > 0 || hasDrop(s, I.RAIL)) && N >= 0 && canLay(s, N % s.w, (N / s.w) | 0) && !claimedByOther(o, 'lay', s);
    const wNeed = needOf(ctx, I.WOOD), iNeed = needOf(ctx, I.IRON);
    const wood = { k: 'gather', r: I.WOOD }, iron = { k: 'gather', r: I.IRON };
    const res = s.train.wood <= s.train.iron ? [wood, iron] : [iron, wood];
    if (role === 'all') {
      if (canRails && (lead < 15 || (wNeed <= 1 && iNeed <= 1))) list.push(rails);
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
      if (t.k === 'gather' && !t.target && !t.bridge && needOf(ctx, t.r) <= 0) continue;
      if (feasible(ctx, t)) return t;
    }
    return { k: 'idle' };
  }

  function feasible(ctx, t) {
    const { s, pl } = ctx;
    if (t.k === 'gather') {
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
        if (h === I.BUCKET || h === I.BUCKET_FULL) { const r = actAdj(ctx, (n) => o.front.has(n) && freeDrop(ctx, n), null, 25); if (r) return r; }
        return actAdj(ctx, (n) => freeDrop(ctx, n));
      }
      case 'fill': {
        if (h !== I.BUCKET) return null;
        return actAdj(ctx, (n) => s.ground[n] === G.WATER, null, 12);
      }
      case 'deliver': {
        if (h !== I.WOOD && h !== I.IRON) return null;
        if (h === I.WOOD && N >= 0 && s.ground[N] === G.WATER) return actAdj(ctx, isN);
        if (pl.hold.n < ctx.cap) {     // добрати поруч ногами
          const r = walkOn(ctx, (n) => s.drop[n] === h, 4);
          if (r) return r;
        }
        if (needOf(ctx, h) <= 0) { t.k = 'drop'; return runTask(ctx, t); }
        return actAdj(ctx, (n) => { const w = o.occ[n] ? wagonAt(s, n % s.w, (n / s.w) | 0) : -1; return w === 1 || w === 2; });
      }
      case 'lay': {
        if (h !== I.RAIL) return null;
        const sideTool = o.side >= 0 ? (s.obj[o.side] === O.TREE ? I.AXE : I.PICK) : 0;
        if (N >= 0 && canLay(s, N % s.w, (N / s.w) | 0) && !(sideTool && hasDrop(s, sideTool))) { claim(o, 'lay', s); return actAdj(ctx, isN); }
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
    if (t.bridge && !(N >= 0 && s.ground[N] === G.WATER)) return null;
    // скільки нашого ресурсу лежить поруч
    const loose = (maxD) => bfs(s, o, ctx.cur, (n) => s.drop[n] === k && !claimedByOther(o, 'c' + n, s), 'adj', maxD);
    if (h === T) {
      let near = 0;
      const cx = ctx.cur % s.w, cy = (ctx.cur / s.w) | 0;
      for (let y = Math.max(0, cy - 3); y <= Math.min(s.h - 1, cy + 3); y++) for (let x = Math.max(0, cx - 3); x <= Math.min(s.w - 1, cx + 3); x++) if (s.drop[y * s.w + x] === k) near += s.dropN[y * s.w + x];
      if (t.bridge ? near >= 1 : near >= cap) t.enough = 1;    // рішення «досить» тримаємо (інакше смикається на межі радіуса)
      const enough = t.enough && near > 0;
      if (!enough ||(t.target !== undefined && s.obj[t.target] === ob)) {
        // що бити: ціль на маршруті, інакше найближче (спершу ті, що на маршруті попереду)
        if (t.target !== undefined && s.obj[t.target] === ob) { const r = actAdj(ctx, (n) => n === t.target); if (r) return r; }
        // обрану ціль тримаємо, доки вона є (інакше межа пошуку смикає туди-сюди між двома)
        if (t.hit !== undefined && s.obj[t.hit] === ob) { const r = actAdj(ctx, (n) => n === t.hit, (n) => 'c' + n); if (r) return r; }
        t.hit = undefined;
        // нова ціль: найкоротший рейс «я → ціль → склад»; біля колії (розширити коридор) — з бонусом
        const dB = distMap(s, o, [ctx.cur]);
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
          if (sc < bs) { bs = sc; best = c; }
        }
        if (best >= 0) {
          t.hit = best;
          const r = actAdj(ctx, (n) => n === best, (n) => 'c' + n);
          if (r) return r;
        }
        if (!near) return null;
      }
      // досить — кладемо інструмент поруч і збираємо
      return actAdj(ctx, (n) => freeDrop(ctx, n));
    }
    if (h !== I.NONE && h !== I.AXE && h !== I.PICK && h !== I.BUCKET && h !== I.BUCKET_FULL) return null;
    // порожні руки (або інший інструмент): є що зібрати поруч — збираємо, ні — по інструмент
    if (t.target === undefined) {
      const l = loose(h === I.NONE ? 30 : 6);
      if (l) {
        if (h !== I.NONE) return actAdj(ctx, (n) => freeDrop(ctx, n));
        return actAdj(ctx, (n) => s.drop[n] === k && !claimedByOther(o, 'c' + n, s), (n) => 'c' + n, 30);
      }
    }
    return actAdj(ctx, (n) => s.drop[n] === T && !claimedByOther(o, 'c' + n, s), (n) => 'c' + n);
  }

  return { p, think, dbg: () => (m.task ? m.task.k + (m.task.r ? ':' + m.task.r : '') + (m.task.target !== undefined ? '@' + m.task.target : '') + (m.task.bridge ? ':br' : '') : '-') };
}

/** Для налагодження й тестів: що бот думає про світ (маршрут, наступна клітинка N, бічна перешкода) */
export function botView(s) { return shared.get(s) || null; }
