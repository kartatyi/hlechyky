// «Колія» — генератор смуги від сіду. Чистий і детермінований: свій PRNG і хеш-шум, лише +,−,×,÷, floor і sqrt
// (без Math.random/sin/exp/pow, DOM і three) — однаковий світ у кожному браузері й у Node (мережа генерує сама).
// Власник: K1 «Світ».
// Контракт: generate(seed, params) → { w, h, ground, obj, hp, drop, dropN, path, spawn, station }
//  w = params.stripLen (уздовж, x), h = params.stripWidth (упоперек, y); масиви Uint8Array(w*h)
//  path — idx клітинок початкової колії (6) від хвоста; станція — G.STATION 3×3 біля x = w-1 (station — її рамка)
//  на підлозі лежать сокира, кайло й відро (drop) біля старту; spawn — клітинки появи гравців (4)
// Як будується: галявина зі старту (x < ~9) → річки впоперек (ширина 2–3, звиваються) й озера з шуму → пісок (береги й
// плями) → ліс (верх шуму, кластерами) → скелі (шум + «хребти» — жили) → мінімум ресурсів у кожному відрізку з 10
// стовпців → маршрут колії BFS (вода підряд ≤ 3; ширше — намиваємо мілину-пісок) → дерева/заліза на весь маршрут.
import { G, O, I } from './rules.js';

export const GLADE = 9;          // ширина стартової галявини (± 1 по рядках)
export const STATION_W = 3;      // станція: 3 стовпці біля кінця × 3 рядки
export const MAX_WATER_RUN = 3;  // скільки води поспіль може перетнути колія (мости)
const CHUNK = 10;

function prng(seed) {
  let a = seed >>> 0;
  return () => {
    let t = (a = (a + 0x6D2B79F5) >>> 0);
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// Цілий хеш (x, y, сіль) → [0, 1)
export function hash3(x, y, s) {
  let h = Math.imul(x | 0, 0x27D4EB2D) ^ Math.imul(y | 0, 0x165667B1) ^ Math.imul(s | 0, 0x9E3779B1);
  h = Math.imul(h ^ (h >>> 15), 0x85EBCA6B);
  h = Math.imul(h ^ (h >>> 13), 0xC2B2AE35);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}
// Ґратковий шум зі згладжуванням (smoothstep) → [0, 1)
function vnoise(x, y, s) {
  const ix = Math.floor(x), iy = Math.floor(y), fx = x - ix, fy = y - iy;
  const ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
  const a = hash3(ix, iy, s), b = hash3(ix + 1, iy, s), c = hash3(ix, iy + 1, s), d = hash3(ix + 1, iy + 1, s);
  return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
}
function fbm(x, y, s) {
  return vnoise(x, y, s) * 0.6 + vnoise(x * 2.03 + 5.1, y * 2.03 + 1.7, s + 17) * 0.28 + vnoise(x * 4.1 + 9.3, y * 4.1 + 3.3, s + 31) * 0.12;
}

// k клітинок з найбільшим балом (рівні — за меншим idx): детерміновано
function top(cands, score, k) {
  if (k <= 0) return [];
  const a = cands.slice().sort((p, q) => (score[q] - score[p]) || (p - q));
  return a.slice(0, Math.min(k, a.length));
}

const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);

export function generate(seed, params) {
  const w = clamp(params.stripLen | 0, 20, 2000), h = clamp(params.stripWidth | 0, 8, 200), n = w * h;
  const r = prng(seed);
  const salt = () => (r() * 0x7FFFFFFF) | 0;
  const sW = salt(), sL = salt(), sS = salt(), sF = salt(), sR = salt(), sV = salt(), sJ = salt(), sG = salt();
  const ground = new Uint8Array(n), obj = new Uint8Array(n), hp = new Uint8Array(n);
  const drop = new Uint8Array(n), dropN = new Uint8Array(n);
  const mid = h >> 1;
  const X1 = w - STATION_W - 3;                     // за X1 — привокзальна чиста смуга
  const glade = new Int16Array(h);                  // межа галявини по рядках
  for (let y = 0; y < h; y++) glade[y] = GLADE - 1 + Math.floor(hash3(y, 0, sG) * 3);
  const inZone = (x, y) => y > 0 && y < h - 1 && x >= glade[y] && x < X1;
  const zone = [];
  for (let y = 1; y < h - 1; y++) for (let x = 0; x < w; x++) if (inZone(x, y)) zone.push(y * w + x);
  const area = zone.length;
  // частки: сума > 0,88 — стискаємо пропорційно (інакше ліс з'їв би все місце скель і проходи)
  let fF = clamp(+params.forest || 0, 0, 1), fR = clamp(+params.rocks || 0, 0, 1), fW = clamp(+params.water || 0, 0, 1);
  const sum = fF + fR + fW;
  if (sum > 0.88) { fF *= 0.88 / sum; fR *= 0.88 / sum; fW *= 0.88 / sum; }

  // --- Вода: річки впоперек смуги + озера ---
  let waterLeft = Math.round(fW * area);
  const riverEst = (h - 2) * 2.4;
  const span = X1 - (GLADE + 3) - 3;
  let nRiv = Math.round(waterLeft * 0.55 / riverEst);
  if (waterLeft >= riverEst * 0.8 && nRiv < 1) nRiv = 1;
  nRiv = clamp(nRiv, 0, Math.max(0, Math.floor(span / 9)));
  for (let k = 0; k < nRiv; k++) {
    const seg = span / nRiv;
    const cx = GLADE + 3 + seg * (k + 0.5) + (r() - 0.5) * seg * 0.4;
    const base = 2;                                  // 2, місцями 3
    const amp = 2 + r() * 3, fq = 0.09 + r() * 0.06;
    let prevL = null;
    for (let y = 1; y < h - 1; y++) {
      const wd = base + (vnoise(y * 0.35, k * 3.1 + 0.5, sW + 7) > 0.62 ? 1 : 0);
      let L = Math.round(cx + (vnoise(y * fq, k * 5.7 + 0.3, sW) - 0.5) * 2 * amp - wd / 2);
      if (prevL !== null) L = clamp(L, prevL - 1, prevL + 1);
      L = clamp(L, GLADE + 2, X1 - 1 - wd);
      prevL = L;
      for (let x = L; x < L + wd; x++) if (ground[y * w + x] !== G.WATER) { ground[y * w + x] = G.WATER; waterLeft--; }
    }
  }
  if (waterLeft > 0) {
    const sc = new Float32Array(n), c = [];
    for (const i of zone) {
      if (ground[i] === G.WATER) continue;
      const x = i % w, y = (i / w) | 0;
      if (x < GLADE + 2) continue;
      // озера тяжіють до середини смуги впоперек менше — до країв більше, щоб не перегороджувати все
      const edge = Math.abs(y - mid) / (h / 2);
      sc[i] = fbm(x / 5.5, y / 4.2, sL) + edge * 0.08 + hash3(x, y, sL + 3) * 0.05;
      c.push(i);
    }
    for (const i of top(c, sc, waterLeft)) ground[i] = G.WATER;
  }

  // --- Пісок: береги й плями ---
  for (let y = 1; y < h - 1; y++) for (let x = 0; x < w; x++) {
    const i = y * w + x;
    if (ground[i] !== G.GRASS) continue;
    let wet = false;
    if (x > 0 && ground[i - 1] === G.WATER) wet = true;
    if (x < w - 1 && ground[i + 1] === G.WATER) wet = true;
    if (ground[i - w] === G.WATER || (y < h - 1 && ground[i + w] === G.WATER)) wet = true;
    if (wet && hash3(x, y, sS + 1) < 0.6) ground[i] = G.SAND;
    else if (fbm(x / 6, y / 5, sS) > 0.7) ground[i] = G.SAND;
  }

  // --- Ліс і скелі (кластери з шуму) ---
  const scF = new Float32Array(n), scR = new Float32Array(n);
  for (const i of zone) {
    const x = i % w, y = (i / w) | 0;
    scF[i] = fbm(x / 6.5, y / 4.8, sF) + hash3(x, y, sF + 5) * 0.16 + (ground[i] === G.SAND ? -0.12 : 0);
    const ridge = 1 - Math.abs(2 * fbm(x / 8.5, y / 7, sV) - 1);
    scR[i] = fbm(x / 5.5, y / 4.2, sR) * 0.55 + ridge * ridge * 0.45 + hash3(x, y, sR + 5) * 0.1;
  }
  const dry = zone.filter((i) => ground[i] !== G.WATER);
  for (const i of top(dry, scF, Math.round(fF * area))) obj[i] = O.TREE;
  const free = dry.filter((i) => obj[i] === O.NONE);
  for (const i of top(free, scR, Math.round(fR * area))) obj[i] = O.ROCK;

  // Мінімум ресурсів у кожному відрізку з CHUNK стовпців (з лісу 0 — все одно є з чого класти колію)
  const wpt = Math.max(1, params.woodPerTree | 0), ipr = Math.max(1, params.ironPerRock | 0);
  const needT = Math.ceil(CHUNK * 1.3 / wpt), needR = Math.ceil(CHUNK * 1.1 / ipr);
  const chunkOf = (i) => Math.floor(((i % w) - GLADE + 1) / CHUNK);
  const nChunks = Math.max(1, Math.ceil((X1 - GLADE + 1) / CHUNK));
  const addIn = (ch, kind, k) => {
    const cand = zone.filter((i) => chunkOf(i) === ch && ground[i] !== G.WATER && obj[i] === O.NONE);
    for (const i of top(cand, kind === O.TREE ? scF : scR, k)) obj[i] = kind;
  };
  const count = (ch, kind) => { let c = 0; for (const i of zone) if (obj[i] === kind && chunkOf(i) === ch) c++; return c; };
  for (let ch = 0; ch < nChunks; ch++) {
    const t = count(ch, O.TREE); if (t < needT) addIn(ch, O.TREE, needT - t);
    const q = count(ch, O.ROCK); if (q < needR) addIn(ch, O.ROCK, needR - q);
  }

  // --- Стіни, станція, галявина ---
  for (let x = 0; x < w; x++) { obj[x] = O.WALL; obj[(h - 1) * w + x] = O.WALL; ground[x] = G.GRASS; ground[(h - 1) * w + x] = G.GRASS; }
  const station = { x0: w - STATION_W, y0: mid - 1, x1: w - 1, y1: mid + 1 };
  for (let y = 1; y < h - 1; y++) for (let x = X1; x < w; x++) {
    const i = y * w + x;
    obj[i] = O.NONE;
    ground[i] = x >= station.x0 && y >= station.y0 && y <= station.y1 ? G.STATION : (ground[i] === G.WATER ? G.SAND : ground[i]);
  }
  for (let y = 1; y < h - 1; y++) for (let x = 0; x < glade[y]; x++) {
    const i = y * w + x;
    obj[i] = O.NONE;
    if (ground[i] === G.WATER) ground[i] = G.SAND;
  }
  const path = [];
  for (let x = 0; x < 6; x++) { path.push(mid * w + x); ground[mid * w + x] = G.GRASS; }

  // --- Маршрут колії: існує, вода поспіль ≤ MAX_WATER_RUN; інакше намиваємо мілини ---
  let route = findRoute({ w, h, ground, obj, path });
  if (!route) {
    const loose = cheapest({ w, h, ground, obj, path });
    let run = 0;
    for (const i of loose) {
      if (ground[i] === G.WATER) { run++; if (run > MAX_WATER_RUN) { ground[i] = G.SAND; run = 0; } } else run = 0;
    }
    route = findRoute({ w, h, ground, obj, path });
  }

  // Ресурсів на весь маршрут (рейка = дерево + залізо, міст = дерево) із запасом 15 %
  if (route) {
    const len = route.length, br = route.filter((i) => ground[i] === G.WATER).length;
    let trees = 0, rocks = 0;
    for (let i = 0; i < n; i++) { if (obj[i] === O.TREE) trees++; else if (obj[i] === O.ROCK) rocks++; }
    let dw = Math.ceil(((len + br) * 1.15) / wpt) - trees, di = Math.ceil((len * 1.15) / ipr) - rocks;
    let moved = false;
    for (let ch = 0; dw > 0 || di > 0; ch = (ch + 1) % nChunks) {
      if (ch === 0) { if (moved === null) break; moved = null; }   // ціле коло без жодної нової — місця нема
      if (dw > 0) { const t = count(ch, O.TREE); addIn(ch, O.TREE, 1); const d = count(ch, O.TREE) - t; dw -= d; if (d) moved = true; }
      if (di > 0) { const q = count(ch, O.ROCK); addIn(ch, O.ROCK, 1); const d = count(ch, O.ROCK) - q; di -= d; if (d) moved = true; }
    }
  }

  for (let i = 0; i < n; i++) hp[i] = obj[i] === O.TREE ? clamp(params.treeHits | 0, 1, 255) : obj[i] === O.ROCK ? clamp(params.rockHits | 0, 1, 255) : 0;

  // Інструменти й місця появи біля старту
  const put = (x, y, k) => { const i = y * w + x; drop[i] = k; dropN[i] = 1; };
  put(4, mid - 2, I.AXE); put(5, mid - 2, I.PICK); put(4, mid + 2, I.BUCKET);
  for (const [x, y] of [[4, mid - 2], [5, mid - 2], [4, mid + 2]]) if (ground[y * w + x] === G.WATER) ground[y * w + x] = G.GRASS;
  const spawn = [{ x: 2, y: mid - 1 }, { x: 3, y: mid + 1 }, { x: 1, y: mid - 2 }, { x: 2, y: mid + 2 }];
  return { w, h, ground, obj, hp, drop, dropN, path, spawn, station };
}

// BFS від кінця колії до станції по 4 сусідах. Прохідне все, крім WALL і клітинок колії; вода — поспіль ≤ MAX_WATER_RUN.
// Повертає клітинки маршруту (без кінця колії, до станції включно) або null.
export function findRoute(wd) {
  const { w, h, ground, obj, path } = wd;
  const R = MAX_WATER_RUN + 1, n = w * h;
  const onPath = new Uint8Array(n); for (const c of path) onPath[c] = 1;
  const from = new Int32Array(n * R).fill(-2);
  const start = path[path.length - 1];
  const q = [start * R]; from[start * R] = -1;
  for (let qi = 0; qi < q.length; qi++) {
    const st = q[qi], c = (st / R) | 0, run = st % R;
    if (ground[c] === G.STATION) {
      const out = [];
      for (let s = st; from[s] !== -1; s = from[s]) out.push((s / R) | 0);
      return out.reverse();
    }
    const x = c % w, y = (c / w) | 0;
    for (const [dx, dy] of [[1, 0], [0, -1], [0, 1], [-1, 0]]) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
      const nc = ny * w + nx;
      if (obj[nc] === O.WALL || onPath[nc]) continue;
      const nr = ground[nc] === G.WATER ? run + 1 : 0;
      if (nr > MAX_WATER_RUN) continue;
      const ns = nc * R + nr;
      if (from[ns] !== -2) continue;
      from[ns] = st; q.push(ns);
    }
  }
  return null;
}

// Найдешевший маршрут, якщо вода — дорога (вага 6): Дейкстра на кошиках (ваги цілі й малі)
function cheapest({ w, h, ground, obj, path }) {
  const n = w * h, dist = new Int32Array(n).fill(1e9), prev = new Int32Array(n).fill(-1);
  const onPath = new Uint8Array(n); for (const c of path) onPath[c] = 1;
  const start = path[path.length - 1];
  const buckets = [[start]]; dist[start] = 0;
  for (let d = 0; d < buckets.length; d++) {
    const b = buckets[d]; if (!b) continue;
    for (const c of b) {
      if (dist[c] !== d) continue;
      if (ground[c] === G.STATION) {
        const out = []; for (let s = c; s !== start; s = prev[s]) out.push(s);
        return out.reverse();
      }
      const x = c % w, y = (c / w) | 0;
      for (const [dx, dy] of [[1, 0], [0, -1], [0, 1], [-1, 0]]) {
        const nx = x + dx, ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
        const nc = ny * w + nx;
        if (obj[nc] === O.WALL || onPath[nc]) continue;
        const nd = d + (ground[nc] === G.WATER ? 6 : 1);
        if (nd < dist[nc]) { dist[nc] = nd; prev[nc] = c; (buckets[nd] ||= []).push(nc); }
      }
    }
  }
  return [];
}
