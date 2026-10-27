// «Колія» — генератор смуги від сіду. Чистий і детермінований: свій PRNG і хеш-шум, лише +,−,×,÷, floor і sqrt
// (без Math.random/sin/exp/pow, DOM і three) — однаковий світ у кожному браузері й у Node (мережа генерує сама).
// Власник: K1 «Світ» → K7 «Генератор 2».
// Контракт: generate(seed, params) → { w, h, ground, obj, hp, drop, dropN, path, spawn, station, zones }
//  w = params.stripLen (уздовж, x), h = params.stripWidth (упоперек, y); масиви Uint8Array(w*h)
//  path — idx клітинок початкової колії (6) від хвоста; станція — G.STATION 3×3 біля x = w-1 (station — її рамка)
//  на підлозі лежать сокира, кайло й відро (drop) біля старту; spawn — клітинки появи гравців (4)
//  zones — ділянки (біоми) вздовж смуги: [{ x0, x1 (не включно), kind, d (складність 0..hardGrow), traps }]
//
// Як будується: галявина зі старту (x < ~9) → смуга ріжеться на ділянки по ~zoneLen стовпців, кожній — біом від сіду
// (перша — легка: степ чи гай; далі важчі частіше; поспіль однакових нема; спершу ті, яких ще не було):
//   steppe «степ»  — просторо, рідкі гайки й каміння, калюжа;
//   grove  «гай»   — густий ліс із галявинами, іноді струмок (міст на 1);
//   stone  «кам'яне поле» — багато скель (залізо), непробивні брили (WALL) і «кишеня» — глухий кут із брил;
//   river  «річкова долина» — річка впоперек: броди (суха мілина або міст на 1–2) і широкі плеса (3–5, довгий міст
//          чи непрохідно), озеро, яке краще обійти;
//   swamp  «болото» — G.SWAMP (ходити повільно, swampSlow; рейки класти можна), баюри з водою, рідкі верби;
//   pass   «прохід між скелями» — хребет WALL поперек смуги з 1–3 проходами (часом завалені скелями) і сліпими
//          коридорами, що ведуть у глухий кут.
// Складність d росте від 0 біля старту до hardGrow біля станції: ширші річки, менше бродів, вужчі й рідші проходи,
// більше брил і пасток. Далі: озера з шуму (частка води), калюжі для відра щонайменше кожні ~20 стовпців, болото
// латками до частки swamp, пісок на берегах → ліс і скелі за множниками біому → маршрут колії BFS (вода поспіль ≤ 3;
// нема — пробиваємо WALL скелею й намиваємо мілини) → дерево й залізо не далі за RES_R від будь-якої прохідної
// клітинки → запас дерев/скель на весь маршрут.
import { G, O, I } from './rules.js';

export const GLADE = 9;          // ширина стартової галявини (± 1 по рядках)
export const STATION_W = 3;      // станція: 3 стовпці біля кінця × 3 рядки
export const MAX_WATER_RUN = 3;  // скільки води поспіль може перетнути колія (мости)
export const RES_R = 10;         // дерево й залізо — не далі за стільки клітинок від будь-якої прохідної клітинки
export const BIOMES = ['steppe', 'grove', 'stone', 'river', 'swamp', 'pass'];
export const BIOME_NAMES = { steppe: 'степ', grove: 'гай', stone: "кам'яне поле", river: 'річкова долина', swamp: 'болото', pass: 'прохід між скелями' };
const FEAT0 = GLADE + 3;         // ближче до старту — жодних річок/стін
// множники лісу й скель за біомом (середнє ≈ 0,9 — загальна частка лишається близькою до повзунка)
const MF = { steppe: 0.35, grove: 2.0, stone: 0.45, river: 1.0, swamp: 0.55, pass: 0.6 };
const MR = { steppe: 0.45, grove: 0.45, stone: 2.1, river: 0.6, swamp: 0.35, pass: 1.3 };

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
const num = (v, d) => (Number.isFinite(+v) && v !== null && v !== '' ? +v : d);
const DIRS = [[1, 0], [0, -1], [0, 1], [-1, 0]];

/** Ділянки вздовж смуги: межі з тремтінням, біом від сіду з вагами за складністю */
function planZones(r, x0, x1, P) {
  const span = Math.max(1, x1 - x0);
  const L = clamp(num(P.zoneLen, 28), 8, 2000);
  const nZ = Math.max(1, Math.round(span / L));
  const cuts = [x0];
  for (let k = 1; k < nZ; k++) cuts.push(Math.round(x0 + span * k / nZ + (r() - 0.5) * 0.4 * span / nZ));
  cuts.push(x1);
  const grow = clamp(num(P.hardGrow, 0.6), 0, 5);
  const fF = num(P.forest, 0.3), fR = num(P.rocks, 0.18), fW = num(P.water, 0.08), fS = num(P.swamp, 0.06);
  const zones = [], used = {};
  let prev = '';
  for (let k = 0; k < nZ; k++) {
    const d = nZ > 1 ? grow * k / (nZ - 1) : 0, dd = Math.min(1, d);
    const wt = {
      steppe: 1.2 - 0.6 * dd,
      grove: fF >= 0.05 ? 1.2 - 0.3 * dd : 0,
      stone: fR >= 0.03 ? 0.8 + 0.4 * dd : 0,
      river: fW > 0 ? 0.7 + 0.7 * dd : 0,
      swamp: fS > 0 ? (0.6 + 0.6 * dd) * clamp(fS / 0.06, 0.4, 2) : 0,
      pass: 0.5 + 0.9 * dd,
    };
    if (k === 0) { for (const b of BIOMES) if (b !== 'steppe' && b !== 'grove') wt[b] = 0; wt.steppe *= 1.6; }
    if (prev) wt[prev] = 0;
    for (const b of BIOMES) if (!used[b]) wt[b] *= 5;
    let sum = 0; for (const b of BIOMES) sum += Math.max(0, wt[b]);
    let kind = 'steppe';
    if (sum > 0) {
      let t = r() * sum;
      for (const b of BIOMES) { const v = Math.max(0, wt[b]); if (t < v) { kind = b; break; } t -= v; }
    }
    used[kind] = 1; prev = kind;
    zones.push({ x0: cuts[k], x1: cuts[k + 1], kind, d: Math.round(d * 100) / 100, traps: 0 });
  }
  return zones;
}

export function generate(seed, params) {
  const P = params;
  const w = clamp(P.stripLen | 0, 20, 2000), h = clamp(P.stripWidth | 0, 8, 200), n = w * h;
  const r = prng(seed);
  const salt = () => (r() * 0x7FFFFFFF) | 0;
  const sW = salt(), sL = salt(), sS = salt(), sF = salt(), sR = salt(), sV = salt(), sJ = salt(), sG = salt(), sM = salt();
  const ground = new Uint8Array(n), obj = new Uint8Array(n), hp = new Uint8Array(n);
  const drop = new Uint8Array(n), dropN = new Uint8Array(n);
  const clear = new Uint8Array(n);                  // тут ліс/скелі не садимо (проходи, кишені, броди)
  const bF = new Float32Array(n), bR = new Float32Array(n);   // надбавки до балу лісу/скель від біому
  const mid = h >> 1;
  const X1 = w - STATION_W - 3;                     // за X1 — привокзальна чиста смуга
  const glade = new Int16Array(h);                  // межа галявини по рядках
  for (let y = 0; y < h; y++) glade[y] = GLADE - 1 + Math.floor(hash3(y, 0, sG) * 3);
  const inZone = (x, y) => y > 0 && y < h - 1 && x >= glade[y] && x < X1;
  const zone = [];
  for (let y = 1; y < h - 1; y++) for (let x = 0; x < w; x++) if (inZone(x, y)) zone.push(y * w + x);
  const area = zone.length;
  // частки: сума > 0,88 — стискаємо пропорційно (інакше ліс з'їв би все місце скель і проходи)
  let fF = clamp(+P.forest || 0, 0, 1), fR = clamp(+P.rocks || 0, 0, 1), fW = clamp(+P.water || 0, 0, 1);
  const fS = clamp(num(P.swamp, 0.06), 0, 1);
  const sum = fF + fR + fW;
  if (sum > 0.88) { fF *= 0.88 / sum; fR *= 0.88 / sum; fW *= 0.88 / sum; }

  const zones = planZones(r, GLADE, X1, { ...P, forest: fF, rocks: fR, water: fW, swamp: fS });
  const zoneOf = new Int16Array(w).fill(-1);
  zones.forEach((z, k) => { for (let x = z.x0; x < z.x1; x++) zoneOf[x] = k; });
  const fx0 = (z) => Math.max(z.x0, FEAT0), fx1 = (z) => Math.min(z.x1, X1 - 1);   // де можна ставити риси

  const setW = (x, y) => { if (y > 0 && y < h - 1 && x >= FEAT0 && x < X1 - 1) { const i = y * w + x; if (obj[i] !== O.WALL) ground[i] = G.WATER; } };
  const setWall = (x, y) => { if (y > 0 && y < h - 1 && x >= FEAT0 && x < X1 - 1) { const i = y * w + x; obj[i] = O.WALL; ground[i] = G.GRASS; } };
  const lake = (cx, cy, rx, ry) => {
    for (let y = Math.floor(cy - ry - 1); y <= cy + ry + 1; y++) for (let x = Math.floor(cx - rx - 1); x <= cx + rx + 1; x++) {
      const dx = (x - cx) / rx, dy = (y - cy) / ry;
      if (dx * dx + dy * dy + (hash3(x, y, sL + 9) - 0.5) * 0.4 < 1) setW(x, y);
    }
  };

  // --- Біоми: вода, стіни, пастки ---
  for (const z of zones) {
    const dd = Math.min(1, z.d), a = fx0(z), b = fx1(z), len = b - a;
    if (len < 4) continue;
    if (z.kind === 'river' || (z.kind === 'grove' && fW > 0 && r() < 0.5)) {
      const stream = z.kind === 'grove';
      const nR = !stream && len >= 26 && dd > 0.5 && r() < 0.5 ? 2 : 1;
      for (let k = 0; k < nR; k++) {
        const cx = a + len * (k + 0.5) / nR + (r() - 0.5) * len * 0.25 / nR;
        const amp = 1.5 + r() * 2.5, fq = 0.08 + r() * 0.07;
        const base = stream ? 1 : 2 + (dd > 0.45 ? 1 : 0) - (dd < 0.15 && r() < 0.5 ? 1 : 0) + (fW > 0.2 ? 1 : 0);
        const wide = stream ? 0 : 1 + (dd > 0.6 ? 1 : 0);
        // броди: суха мілина (ширина 0 — пройти й класти без мосту) або вузина на 1–2 (короткий міст)
        const fords = [];
        if (!stream) {
          const nF = 1 + (r() < 0.6 ? 1 : 0) + (h >= 26 ? 1 : 0);
          for (let f = 0; f < nF; f++) {
            const y = 2 + Math.floor(r() * Math.max(1, h - 5));
            const dry = r() < 0.6 - 0.5 * dd;
            fords.push({ y0: y, y1: y + (dry ? 1 : 2), wd: dry ? 0 : (r() < 0.5 ? 1 : 2) });
          }
        }
        let prevL = null;
        for (let y = 1; y < h - 1; y++) {
          let wd = base + (vnoise(y * 0.3, k * 3.1 + z.x0 * 0.37, sW + 7) > 0.56 ? wide : 0);
          let ford = false;
          for (const f of fords) if (y >= f.y0 && y <= f.y1) { wd = Math.min(wd, f.wd); ford = true; }
          let L = Math.round(cx + (vnoise(y * fq, k * 5.7 + z.x0 * 0.13, sW) - 0.5) * 2 * amp - wd / 2);
          if (prevL !== null) L = clamp(L, prevL - 1, prevL + 1);
          L = clamp(L, a, b - Math.max(wd, 2));
          prevL = L;
          for (let x = L; x < L + wd; x++) setW(x, y);
          if (ford) for (let x = L - 1; x < L + Math.max(wd, 2) + 1; x++) {
            const i = y * w + x;
            if (x >= a && x < b) { clear[i] = 1; if (ground[i] !== G.WATER) ground[i] = G.SAND; }
          }
        }
        // береги лісисті
        for (let y = 1; y < h - 1; y++) for (let x = a; x < b; x++) {
          const i = y * w + x;
          if (ground[i] !== G.WATER && (ground[i - 1] === G.WATER || ground[i + 1] === G.WATER)) bF[i] += 0.12;
        }
      }
      // озеро — краще обійти, ніж мостити
      if (!stream && fW > 0 && r() < 0.75) {
        const rx = 2.4 + dd * 1.8 + r(), ry = Math.min((h - 2) * 0.3, 2 + dd * 1.5 + r());
        const top = r() < 0.5;
        const cy = top ? 1 + ry * 0.8 : h - 2 - ry * 0.8;
        const cx = a + 3 + r() * Math.max(1, len - 6);
        lake(cx, cy, rx, ry);
      }
    } else if (z.kind === 'steppe') {
      if (fW > 0 && r() < 0.45) lake(a + 3 + r() * Math.max(1, len - 6), r() < 0.5 ? 2.5 : h - 3.5, 1.6 + r(), 1.4);
    } else if (z.kind === 'stone') {
      // брили WALL: 1–4 клітинки, частіше з глибиною
      const nB = Math.round(len * (h - 2) / 70 * (0.6 + dd));
      for (let k = 0; k < nB; k++) {
        const x = a + Math.floor(r() * len), y = 2 + Math.floor(r() * Math.max(1, h - 4));
        const shp = Math.floor(r() * 4);
        const cells = [[0, 0], ...[[[1, 0]], [[0, 1]], [[1, 0], [0, 1], [1, 1]], [[1, 0], [2, 0]]][shp]];
        for (const [dx, dy] of cells) if (y + dy < h - 2) setWall(x + dx, y + dy);
      }
      for (let y = 2; y < h - 2; y++) for (let x = a; x < b; x++) {
        const i = y * w + x;
        if (obj[i - 1] === O.WALL || obj[i + 1] === O.WALL || obj[i - w] === O.WALL || obj[i + w] === O.WALL) bR[i] += 0.15;
      }
      // кишеня — глухий кут, роззявлений назад до старту, якраз на прямій (колія заводить)
      if (len >= 10 && h >= 12 && (dd > 0.15 || r() < 0.5)) {
        const D = 3 + Math.floor(r() * 3), Wd = 2, y0 = clamp(mid - 1 + Math.floor((r() - 0.5) * 6), 2, h - 3 - Wd);
        const x0 = a + 2 + Math.floor(r() * Math.max(1, len - D - 4));
        for (let x = x0; x <= x0 + D; x++) { setWall(x, y0 - 1); setWall(x, y0 + Wd); }
        for (let y = y0 - 1; y <= y0 + Wd; y++) setWall(x0 + D, y);
        for (let y = y0; y < y0 + Wd; y++) for (let x = x0; x < x0 + D; x++) { const i = y * w + x; obj[i] = O.NONE; ground[i] = G.GRASS; clear[i] = 1; }
        z.traps++;
      }
    } else if (z.kind === 'swamp') {
      // баюри: найнижчі місця шуму — вода (невеликими плямами)
      if (fW > 0) {
        const c = [], sc = new Float32Array(n);
        for (let y = 2; y < h - 2; y++) for (let x = a; x < b; x++) { const i = y * w + x; sc[i] = fbm(x / 3.2, y / 3.2, sM + 3) + hash3(x, y, sM) * 0.1; c.push(i); }
        for (const i of top(c, sc, Math.round(c.length * (0.05 + 0.04 * dd)))) setW(i % w, (i / w) | 0);
      }
    } else if (z.kind === 'pass') {
      const nBand = len >= 24 && dd > 0.5 ? 2 : 1;
      for (let k = 0; k < nBand; k++) {
        const t = clamp(4 + Math.floor(r() * 2 + dd * 3), 3, Math.max(3, Math.floor(len / nBand) - 4));
        const seg = len / nBand;
        const bx = Math.round(a + seg * k + 2 + r() * Math.max(0, seg - t - 4));
        // рядки, зайняті проходами/коридорами (з проміжком у стіну ≥ 1)
        const taken = new Uint8Array(h);
        const fit = (y0, wd) => { if (y0 < 1 || y0 + wd > h - 1) return false; for (let y = y0 - 1; y <= y0 + wd; y++) if (y >= 0 && y < h && taken[y]) return false; return true; };
        const take = (y0, wd) => { for (let y = y0; y < y0 + wd; y++) taken[y] = 1; };
        // хребет (по рядках трохи кривий)
        const xl = new Int16Array(h), xr = new Int16Array(h);
        for (let y = 1; y < h - 1; y++) {
          const j = vnoise(y * 0.4, k * 7.3 + z.x0 * 0.21, sV) < 0.33 ? -1 : vnoise(y * 0.4, k * 7.3 + z.x0 * 0.21, sV) > 0.67 ? 1 : 0;
          xl[y] = bx + j; xr[y] = bx + t + (vnoise(y * 0.45, k * 3.9 + 2, sV + 5) > 0.6 ? 1 : 0);
          for (let x = xl[y]; x < xr[y]; x++) setWall(x, y);
        }
        const nP = dd < 0.3 || r() < 0.35 ? 2 : 1, pw = dd < 0.5 ? 3 : 2;
        let made = 0;
        for (let p = 0; p < nP; p++) {
          const lo = 1 + Math.floor((h - 2) * p / nP), hi = 1 + Math.floor((h - 2) * (p + 1) / nP) - pw;
          for (let tr = 0; tr < 6; tr++) {
            const y0 = lo + Math.floor(r() * Math.max(1, hi - lo + 1));
            if (!fit(y0, pw)) continue;
            take(y0, pw);
            // прохід: наскрізь, посередині може зсунутись на рядок
            const kink = pw >= 2 && r() < 0.5 ? (r() < 0.5 ? -1 : 1) : 0;
            for (let y = y0; y < y0 + pw; y++) for (let x = bx - 2; x <= bx + t + 1; x++) {
              const half = x >= bx + (t >> 1);
              const yy = half ? y + kink : y;
              if (yy < 1 || yy > h - 2) continue;
              const i = yy * w + x; if (obj[i] === O.WALL) obj[i] = O.NONE; clear[i] = 1;
            }
            if (kink) take(clamp(y0 + kink, 1, h - 2), pw);
            // завал: 1–2 скелі в проході — бити кайлом
            if (r() < 0.25 + 0.5 * dd) {
              const xx = bx + 1 + Math.floor(r() * Math.max(1, t - 2)), yy = y0 + Math.floor(r() * pw) + (xx >= bx + (t >> 1) ? kink : 0);
              const i = yy * w + xx; if (yy > 0 && yy < h - 1 && obj[i] === O.NONE) obj[i] = O.ROCK;
            }
            made++;
            break;
          }
        }
        if (!made) { const y0 = clamp(mid - 1, 1, h - 3); for (let y = y0; y < y0 + 2; y++) for (let x = bx - 1; x <= bx + t + 1; x++) { const i = y * w + x; if (obj[i] === O.WALL) obj[i] = O.NONE; } take(y0, 2); }
        // сліпі коридори з боку старту: заходять у хребет і впираються в стіну
        const nD = 1 + (dd > 0.3 ? 1 : 0) + (r() < 0.4 ? 1 : 0);
        for (let q = 0; q < nD; q++) {
          for (let tr = 0; tr < 8; tr++) {
            const y0 = 1 + Math.floor(r() * Math.max(1, h - 4));
            if (!fit(y0, 2)) continue;
            take(y0, 2);
            for (let y = y0; y < y0 + 2; y++) for (let x = xl[y] - 1; x < xr[y] - 1; x++) { const i = y * w + x; if (obj[i] === O.WALL) obj[i] = O.NONE; clear[i] = 1; }
            z.traps++;
            break;
          }
        }
        for (let y = 1; y < h - 1; y++) for (let x = xl[y] - 3; x <= xr[y] + 3; x++) if (x >= 0 && x < w) bR[y * w + x] += 0.12;
      }
    }
  }

  // --- Озера з шуму до частки води (≈ 65 % повзунка разом з річками) — не в проходах і не в кам'яних полях ---
  let wat = 0; for (const i of zone) if (ground[i] === G.WATER) wat++;
  const waterTarget = Math.round(fW * area * 0.65);
  if (wat < waterTarget) {
    const sc = new Float32Array(n), c = [];
    for (const i of zone) {
      const x = i % w, y = (i / w) | 0;
      if (ground[i] === G.WATER || obj[i] === O.WALL || clear[i] || x < FEAT0 || x >= X1 - 1) continue;
      const z = zones[zoneOf[x]];
      if (!z || z.kind === 'pass' || z.kind === 'stone') continue;
      const edge = Math.abs(y - mid) / (h / 2);   // до країв охочіше — менше перегороджують
      sc[i] = fbm(x / 5.5, y / 4.2, sL) + edge * 0.08 + hash3(x, y, sL + 3) * 0.05;
      c.push(i);
    }
    for (const i of top(c, sc, waterTarget - wat)) ground[i] = G.WATER;
  }

  // --- Калюжі для відра: вода щонайменше кожні ~20 стовпців ---
  if (fW > 0) {
    let last = 0;
    for (let x = FEAT0; x < X1 - 1; x++) {
      let has = false;
      for (let y = 1; y < h - 1; y++) if (ground[y * w + x] === G.WATER) { has = true; break; }
      if (has) { last = x; continue; }
      if (x - last < 20) continue;
      // калюжа (3 клітинки) ближче до краю смуги, щоб не перегороджувати; шукаємо назад по стовпцях і вглиб по рядках
      const up = hash3(x, 1, sL + 5) < 0.5;
      let done = false;
      for (const dx of [3, 5, 1, 7, 9, 11, 13]) {
        const px = x - dx;
        if (px < FEAT0) continue;
        for (let k = 0; k < h - 4 && !done; k++) {
          const py = up ? 2 + k : h - 3 - k, sy = up ? 1 : -1;
          const cells = [[px, py], [px + 1, py], [px, py + sy]];
          if (!cells.every(([cx, cy]) => cy > 0 && cy < h - 1 && cx < X1 - 1 && obj[cy * w + cx] !== O.WALL && !clear[cy * w + cx])) continue;
          for (const [cx, cy] of cells) setW(cx, cy);
          done = true;
        }
        if (done) { last = px; break; }
      }
      if (!done) last = x;   // ніде — далі
    }
  }

  // --- Болото: ділянки «болото» (більшість площі) + латки біля води до частки swamp ---
  let sw = 0;
  if (fS > 0) {
    for (const z of zones) {
      if (z.kind !== 'swamp') continue;
      const c = [], sc = new Float32Array(n);
      for (let y = 1; y < h - 1; y++) for (let x = z.x0; x < z.x1; x++) {
        const i = y * w + x;
        if (x < glade[y] || ground[i] !== G.GRASS || obj[i] === O.WALL) continue;
        sc[i] = fbm(x / 5, y / 4, sM) + hash3(x, y, sM + 1) * 0.08; c.push(i);
      }
      const cov = Math.min(0.9, (0.5 + 0.3 * Math.min(1, z.d)) * clamp(fS / 0.06, 0.4, 1.5));
      for (const i of top(c, sc, Math.round(c.length * cov))) { ground[i] = G.SWAMP; sw++; }
    }
    const target = Math.round(fS * area);
    if (sw < target) {
      const c = [], sc = new Float32Array(n);
      for (const i of zone) {
        const x = i % w, y = (i / w) | 0;
        if (ground[i] !== G.GRASS || obj[i] === O.WALL || x < FEAT0 || clear[i]) continue;
        const z = zones[zoneOf[x]];
        if (!z || z.kind === 'pass' || z.kind === 'stone') continue;
        let wet = 0;
        for (let dy = -2; dy <= 2 && !wet; dy++) for (let dx = -2; dx <= 2; dx++) {
          const yy = y + dy, xx = x + dx;
          if (yy > 0 && yy < h - 1 && xx >= 0 && xx < w && ground[yy * w + xx] === G.WATER) { wet = 1; break; }
        }
        sc[i] = fbm(x / 7, y / 5, sM + 7) + wet * 0.25; c.push(i);
      }
      for (const i of top(c, sc, target - sw)) { ground[i] = G.SWAMP; sw++; }
    }
  }

  // --- Пісок: береги й плями ---
  for (let y = 1; y < h - 1; y++) for (let x = 0; x < w; x++) {
    const i = y * w + x;
    if (ground[i] !== G.GRASS || obj[i] === O.WALL) continue;
    let wet = false;
    if (x > 0 && ground[i - 1] === G.WATER) wet = true;
    if (x < w - 1 && ground[i + 1] === G.WATER) wet = true;
    if (ground[i - w] === G.WATER || (y < h - 1 && ground[i + w] === G.WATER)) wet = true;
    if (wet && hash3(x, y, sS + 1) < 0.6) ground[i] = G.SAND;
    else if (fbm(x / 6, y / 5, sS) > 0.72) ground[i] = G.SAND;
  }

  // --- Ліс і скелі (кластери з шуму, кількість — за біомом) ---
  const scF = new Float32Array(n), scR = new Float32Array(n);
  for (const i of zone) {
    const x = i % w, y = (i / w) | 0;
    const z = zones[zoneOf[x]];
    let gl = x < GLADE + 6 ? -0.25 : 0;                                           // узлісся за галявиною рідке
    if (z && z.kind === 'grove' && fbm(x / 4, y / 4, sJ) > 0.66) gl -= 0.45;    // галявини в гаю
    scF[i] = fbm(x / 6.5, y / 4.8, sF) + hash3(x, y, sF + 5) * 0.16 + (ground[i] === G.SAND ? -0.12 : 0) + (ground[i] === G.SWAMP ? -0.05 : 0) + bF[i] + gl;
    const ridge = 1 - Math.abs(2 * fbm(x / 8.5, y / 7, sV) - 1);
    scR[i] = fbm(x / 5.5, y / 4.2, sR) * 0.55 + ridge * ridge * 0.45 + hash3(x, y, sR + 5) * 0.1 + bR[i] + (ground[i] === G.SWAMP ? -0.1 : 0);
  }
  for (const z of zones) {
    const cells = [];
    for (let y = 1; y < h - 1; y++) for (let x = Math.max(z.x0, glade[y]); x < z.x1; x++) {
      const i = y * w + x;
      if (ground[i] !== G.WATER && obj[i] === O.NONE && !clear[i]) cells.push(i);
    }
    const dd = Math.min(1, z.d);
    const mf = z.kind === 'grove' ? MF.grove * (0.7 + 0.3 * dd) : MF[z.kind];     // гай біля старту рідший
    const nT = Math.min(Math.round(fF * mf * cells.length), Math.round(cells.length * 0.7));
    for (const i of top(cells, scF, nT)) obj[i] = O.TREE;
    const free = cells.filter((i) => obj[i] === O.NONE);
    const nQ = Math.min(Math.round(fR * MR[z.kind] * (1 + 0.3 * dd) * cells.length), Math.round(free.length * 0.6));
    for (const i of top(free, scR, nQ)) obj[i] = O.ROCK;
  }

  // --- Стіни по краях, станція, галявина ---
  for (let x = 0; x < w; x++) { obj[x] = O.WALL; obj[(h - 1) * w + x] = O.WALL; ground[x] = G.GRASS; ground[(h - 1) * w + x] = G.GRASS; }
  const station = { x0: w - STATION_W, y0: mid - 1, x1: w - 1, y1: mid + 1 };
  for (let y = 1; y < h - 1; y++) for (let x = X1; x < w; x++) {
    const i = y * w + x;
    obj[i] = O.NONE;
    ground[i] = x >= station.x0 && y >= station.y0 && y <= station.y1 ? G.STATION : (ground[i] === G.WATER || ground[i] === G.SWAMP ? G.SAND : ground[i]);
  }
  for (let y = 1; y < h - 1; y++) for (let x = 0; x < glade[y]; x++) {
    const i = y * w + x;
    obj[i] = O.NONE;
    if (ground[i] === G.WATER || ground[i] === G.SWAMP) ground[i] = G.SAND;
  }
  const path = [];
  for (let x = 0; x < 6; x++) { path.push(mid * w + x); ground[mid * w + x] = G.GRASS; }
  const onPath = new Uint8Array(n); for (const c of path) onPath[c] = 1;

  // --- Маршрут колії: існує, вода поспіль ≤ MAX_WATER_RUN; інакше пробиваємо WALL (скелею) й намиваємо мілини ---
  let route = findRoute({ w, h, ground, obj, path });
  if (!route) {
    const loose = cheapest({ w, h, ground, obj, path });
    let run = 0;
    for (const i of loose) {
      if (obj[i] === O.WALL) { obj[i] = O.ROCK; }
      if (ground[i] === G.WATER) { run++; if (run > MAX_WATER_RUN) { ground[i] = G.SAND; run = 0; } } else run = 0;
    }
    route = findRoute({ w, h, ground, obj, path });
  }

  // --- Дерево й залізо поруч із будь-яким маршрутом: кожна прохідна клітинка — не далі RES_R від обох ---
  const wpt = Math.max(1, P.woodPerTree | 0), ipr = Math.max(1, P.ironPerRock | 0);
  const canPut = (i, strict) => {
    const x = i % w, y = (i / w) | 0;
    // нестрого — можна й у привокзальну смугу (не на станцію): біля кінця інакше буває ніде
    if (y < 1 || y > h - 2 || x < glade[y] || x >= (strict ? X1 : station.x0 - 1)) return false;
    if (obj[i] !== O.NONE || ground[i] === G.WATER || ground[i] === G.STATION || onPath[i]) return false;
    return !strict || !clear[i];
  };
  const cover = (kind, sc) => {
    const dist = new Int16Array(n).fill(0x3fff), q = new Int32Array(n);
    let qh = 0, qt = 0;
    const flood = () => {
      while (qh < qt) {
        const c = q[qh++], x = c % w, y = (c / w) | 0, d = dist[c] + 1;
        if (d > RES_R) continue;
        for (const [dx, dy] of DIRS) {
          const nx = x + dx, ny = y + dy;
          if (nx < 0 || ny < 1 || nx >= w || ny > h - 2) continue;
          const m = ny * w + nx;
          if (obj[m] === O.WALL || dist[m] <= d) continue;
          dist[m] = d; q[qt++] = m;
        }
      }
    };
    for (let i = 0; i < n; i++) if (obj[i] === kind) { dist[i] = 0; q[qt++] = i; }
    flood();
    const ld = new Int16Array(n), lq = new Int32Array(n);
    for (let x = GLADE - 1; x < w; x++) for (let y = 1; y < h - 1; y++) {
      const c = y * w + x;
      if (dist[c] <= RES_R || obj[c] === O.WALL || ground[c] === G.WATER) continue;
      // найкраща вільна клітинка поруч — у межах ходу (BFS крізь усе, крім WALL), ближча й «природніша» за шумом
      let best = -1, bs = -1e9;
      ld.fill(-1); lq[0] = c; ld[c] = 0;
      const cand = [];
      for (let qa = 0, qb = 1; qa < qb; qa++) {
        const m = lq[qa], mx = m % w, my = (m / w) | 0;
        cand.push(m);
        if (ld[m] >= RES_R - 3) continue;
        for (const [dx, dy] of DIRS) {
          const nx = mx + dx, ny = my + dy;
          if (nx < 0 || nx >= w || ny < 1 || ny > h - 2) continue;
          const k = ny * w + nx;
          if (ld[k] >= 0 || obj[k] === O.WALL) continue;
          ld[k] = ld[m] + 1; lq[qb++] = k;
        }
      }
      for (const strict of [true, false]) {
        for (const m of cand) {
          if (!canPut(m, strict)) continue;
          const s = sc[m] - 0.05 * ld[m] + ((m % w) > x ? 0.02 : 0);
          if (s > bs) { bs = s; best = m; }
        }
        if (best >= 0) break;
      }
      if (best < 0) continue;
      obj[best] = kind; dist[best] = 0; qh = qt = 0; q[qt++] = best; flood();
    }
  };
  cover(O.TREE, scF);      // навіть з лісом 0 — без дерева колію не покласти
  cover(O.ROCK, scR);

  // --- Запас на весь маршрут (рейка = дерево + залізо, міст = дерево) із запасом 15 % ---
  if (route) {
    const len = route.length, br = route.filter((i) => ground[i] === G.WATER).length;
    let trees = 0, rocks = 0;
    for (let i = 0; i < n; i++) { if (obj[i] === O.TREE) trees++; else if (obj[i] === O.ROCK) rocks++; }
    let dw = Math.ceil(((len + br) * 1.15) / wpt) - trees, di = Math.ceil((len * 1.15) / ipr) - rocks;
    const CH = 10, nCh = Math.max(1, Math.ceil((X1 - GLADE) / CH));
    const addIn = (ch, kind, sc) => {
      const cand = [];
      for (let y = 1; y < h - 1; y++) for (let x = GLADE + ch * CH; x < Math.min(X1, GLADE + (ch + 1) * CH); x++) { const i = y * w + x; if (canPut(i, true)) cand.push(i); }
      const t = top(cand, sc, 1);
      for (const i of t) obj[i] = kind;
      return t.length;
    };
    for (let ch = 0, idle = 0; (dw > 0 || di > 0) && idle < nCh; ch = (ch + 1) % nCh) {
      let moved = 0;
      if (dw > 0) { const d = addIn(ch, O.TREE, scF); dw -= d; moved += d; }
      if (di > 0) { const d = addIn(ch, O.ROCK, scR); di -= d; moved += d; }
      idle = moved ? 0 : idle + 1;
    }
  }

  for (let i = 0; i < n; i++) hp[i] = obj[i] === O.TREE ? clamp(P.treeHits | 0, 1, 255) : obj[i] === O.ROCK ? clamp(P.rockHits | 0, 1, 255) : 0;

  // Інструменти й місця появи біля старту
  const put = (x, y, k) => { const i = y * w + x; drop[i] = k; dropN[i] = 1; };
  put(4, mid - 2, I.AXE); put(5, mid - 2, I.PICK); put(4, mid + 2, I.BUCKET);
  for (const [x, y] of [[4, mid - 2], [5, mid - 2], [4, mid + 2]]) if (ground[y * w + x] === G.WATER) ground[y * w + x] = G.GRASS;
  const spawn = [{ x: 2, y: mid - 1 }, { x: 3, y: mid + 1 }, { x: 1, y: mid - 2 }, { x: 2, y: mid + 2 }];
  return { w, h, ground, obj, hp, drop, dropN, path, spawn, station, zones };
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
    for (const [dx, dy] of DIRS) {
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

// Найдешевший маршрут, якщо вода — дорога (6), а WALL — дуже (40, пробиваємо): Дейкстра на кошиках
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
      for (const [dx, dy] of DIRS) {
        const nx = x + dx, ny = y + dy;
        if (nx < 0 || ny < 1 || nx >= w || ny > h - 2) continue;
        const nc = ny * w + nx;
        if (onPath[nc]) continue;
        const nd = d + (obj[nc] === O.WALL ? 40 : ground[nc] === G.WATER ? 6 : 1);
        if (nd < dist[nc]) { dist[nc] = nd; prev[nc] = c; (buckets[nd] ||= []).push(nc); }
      }
    }
  }
  return [];
}
