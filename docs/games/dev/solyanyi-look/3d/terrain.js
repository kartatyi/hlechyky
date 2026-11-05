// «Соляний шлях» — земля: реальна геометрія України → маски, рельєф (поле висот), нерегулярна сітка (Delaunay, без жодної
// решітки). Сітка лише несе висоту; колір — картина, намальована в canvas (paint.js), тому «квадратів» взяти нізвідки.
// Система координат: карта (x, y↓) у одиницях сітки 4000×2730; світ: X = (x − W/2)·U, Z = (y − H/2)·U, Y — угору.

export const W = 4000, H = 2730, U = 1 / 400;
export const PAD = 100;               // скільки карти за межами сітки 4000×2730 (сусідні землі / море до рамки)
export const MAT = 160;               // ширина надрукованого поля навколо карти (картина покриває -PAD-MAT…W+PAD+MAT)
export const LV = { sea: 0, coast: 0.004, bed: -0.024, foreign: 0.016, slab: 0.036, mat: 0.05 };
const DX0 = -PAD, DY0 = -PAD, DW = W + 2 * PAD, DH = H + 2 * PAD;

export const wx = (x) => (x - W / 2) * U;
export const wz = (y) => (y - H / 2) * U;

// ---------------------------------------------------------------------------------------------------------- шум
function mulberry(seed) { let s = seed >>> 0; return () => { s = (s + 0x6D2B79F5) >>> 0; let t = s; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
export const rng = mulberry;
const NT = 256, NTAB = new Float32Array(NT * NT);
{ const r = mulberry(77); for (let i = 0; i < NTAB.length; i++) NTAB[i] = r(); }
export function noise(x, y) {
  const xi = Math.floor(x), yi = Math.floor(y), fx = x - xi, fy = y - yi;
  const sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
  const i0 = xi & 255, i1 = (xi + 1) & 255, j0 = (yi & 255) * NT, j1 = ((yi + 1) & 255) * NT;
  const a = NTAB[j0 + i0], b = NTAB[j0 + i1], c = NTAB[j1 + i0], d = NTAB[j1 + i1];
  return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
}
export function fbm(x, y, o = 4) { let s = 0, a = 0.5, n = 0; for (let i = 0; i < o; i++) { s += a * noise(x, y); n += a; x = x * 2.03 + 17.3; y = y * 2.03 - 9.1; a *= 0.5; } return s / n; }
export const smooth = (a, b, x) => { const t = Math.max(0, Math.min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

// ---------------------------------------------------------------------------------------------------------- Delaunator (компактний порт, ISC, © Mapbox/V. Agafonkin) — нерегулярна триангуляція
const EDGE_STACK = new Uint32Array(1024);
function orient(ax, ay, bx, by, cx, cy) { return (bx - cx) * (ay - cy) - (by - cy) * (ax - cx); }
function circumr(ax, ay, bx, by, cx, cy) { const dx = bx - ax, dy = by - ay, ex = cx - ax, ey = cy - ay, bl = dx * dx + dy * dy, cl = ex * ex + ey * ey, d = 0.5 / (dx * ey - dy * ex), x = (ey * bl - dy * cl) * d, y = (dx * cl - ex * bl) * d; return x * x + y * y; }
function circumc(ax, ay, bx, by, cx, cy) { const dx = bx - ax, dy = by - ay, ex = cx - ax, ey = cy - ay, bl = dx * dx + dy * dy, cl = ex * ex + ey * ey, d = 0.5 / (dx * ey - dy * ex); return [ax + (ey * bl - dy * cl) * d, ay + (dx * cl - ex * bl) * d]; }
function inCircle(ax, ay, bx, by, cx, cy, px, py) { const dx = ax - px, dy = ay - py, ex = bx - px, ey = by - py, fx = cx - px, fy = cy - py, ap = dx * dx + dy * dy, bp = ex * ex + ey * ey, cp = fx * fx + fy * fy; return dx * (ey * cp - bp * fy) - dy * (ex * cp - bp * fx) + ap * (ex * fy - ey * fx) < 0; }
function delaunay(coords) {
  const n = coords.length >> 1, maxT = Math.max(2 * n - 5, 0);
  const tri = new Uint32Array(maxT * 3), he = new Int32Array(maxT * 3);
  const hashSize = Math.ceil(Math.sqrt(n));
  const prev = new Uint32Array(n), next = new Uint32Array(n), hTri = new Uint32Array(n), hash = new Int32Array(hashSize).fill(-1);
  const ids = new Uint32Array(n), dists = new Float64Array(n);
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (let i = 0; i < n; i++) { const x = coords[2 * i], y = coords[2 * i + 1]; if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; ids[i] = i; }
  const cx0 = (minX + maxX) / 2, cy0 = (minY + maxY) / 2;
  const d2 = (ax, ay, bx, by) => (ax - bx) ** 2 + (ay - by) ** 2;
  let i0 = 0, i1 = 0, i2 = 0, m = Infinity;
  for (let i = 0; i < n; i++) { const d = d2(cx0, cy0, coords[2 * i], coords[2 * i + 1]); if (d < m) { i0 = i; m = d; } }
  const i0x = coords[2 * i0], i0y = coords[2 * i0 + 1]; m = Infinity;
  for (let i = 0; i < n; i++) { if (i === i0) continue; const d = d2(i0x, i0y, coords[2 * i], coords[2 * i + 1]); if (d < m && d > 0) { i1 = i; m = d; } }
  let i1x = coords[2 * i1], i1y = coords[2 * i1 + 1], mr = Infinity;
  for (let i = 0; i < n; i++) { if (i === i0 || i === i1) continue; const r = circumr(i0x, i0y, i1x, i1y, coords[2 * i], coords[2 * i + 1]); if (r < mr) { i2 = i; mr = r; } }
  let i2x = coords[2 * i2], i2y = coords[2 * i2 + 1];
  if (orient(i0x, i0y, i1x, i1y, i2x, i2y) < 0) { const a = i1, x = i1x, y = i1y; i1 = i2; i1x = i2x; i1y = i2y; i2 = a; i2x = x; i2y = y; }
  const [ccx, ccy] = circumc(i0x, i0y, i1x, i1y, i2x, i2y);
  for (let i = 0; i < n; i++) dists[i] = d2(coords[2 * i], coords[2 * i + 1], ccx, ccy);
  ids.sort((a, b) => dists[a] - dists[b]);
  let start = i0, hullSize = 3, tl = 0;
  next[i0] = prev[i2] = i1; next[i1] = prev[i0] = i2; next[i2] = prev[i1] = i0;
  hTri[i0] = 0; hTri[i1] = 1; hTri[i2] = 2;
  const hk = (x, y) => { const dx = x - ccx, dy = y - ccy, p = dx / (Math.abs(dx) + Math.abs(dy)); return Math.floor((dy > 0 ? 3 - p : 1 + p) / 4 * hashSize) % hashSize; };
  hash[hk(i0x, i0y)] = i0; hash[hk(i1x, i1y)] = i1; hash[hk(i2x, i2y)] = i2;
  const link = (a, b) => { he[a] = b; if (b !== -1) he[b] = a; };
  const add = (a, b, c, ha, hb, hc) => { const t = tl; tri[t] = a; tri[t + 1] = b; tri[t + 2] = c; link(t, ha); link(t + 1, hb); link(t + 2, hc); tl += 3; return t; };
  const legalize = (a) => {
    let i = 0, ar = 0;
    for (;;) {
      const b = he[a], a0 = a - a % 3; ar = a0 + (a + 2) % 3;
      if (b === -1) { if (i === 0) break; a = EDGE_STACK[--i]; continue; }
      const b0 = b - b % 3, al = a0 + (a + 1) % 3, bl = b0 + (b + 2) % 3;
      const p0 = tri[ar], pr = tri[a], pl = tri[al], p1 = tri[bl];
      if (inCircle(coords[2 * p0], coords[2 * p0 + 1], coords[2 * pr], coords[2 * pr + 1], coords[2 * pl], coords[2 * pl + 1], coords[2 * p1], coords[2 * p1 + 1])) {
        tri[a] = p1; tri[b] = p0;
        const hbl = he[bl];
        if (hbl === -1) { let e = start; do { if (hTri[e] === bl) { hTri[e] = a; break; } e = prev[e]; } while (e !== start); }
        link(a, hbl); link(b, he[ar]); link(ar, bl);
        const br = b0 + (b + 1) % 3;
        if (i < EDGE_STACK.length) EDGE_STACK[i++] = br;
      } else { if (i === 0) break; a = EDGE_STACK[--i]; }
    }
    return ar;
  };
  add(i0, i1, i2, -1, -1, -1);
  let xp = 0, yp = 0;
  for (let k = 0; k < n; k++) {
    const i = ids[k], x = coords[2 * i], y = coords[2 * i + 1];
    if (k > 0 && Math.abs(x - xp) <= 1e-9 && Math.abs(y - yp) <= 1e-9) continue;
    xp = x; yp = y;
    if (i === i0 || i === i1 || i === i2) continue;
    let s = 0;
    for (let j = 0, key = hk(x, y); j < hashSize; j++) { s = hash[(key + j) % hashSize]; if (s !== -1 && s !== next[s]) break; }
    s = prev[s];
    let e = s, q;
    while (q = next[e], orient(x, y, coords[2 * e], coords[2 * e + 1], coords[2 * q], coords[2 * q + 1]) >= 0) { e = q; if (e === s) { e = -1; break; } }
    if (e === -1) continue;
    let t = add(e, i, next[e], -1, -1, hTri[e]);
    hTri[i] = legalize(t + 2); hTri[e] = t; hullSize++;
    let nx = next[e];
    while (q = next[nx], orient(x, y, coords[2 * nx], coords[2 * nx + 1], coords[2 * q], coords[2 * q + 1]) < 0) { t = add(nx, i, q, hTri[i], -1, hTri[nx]); hTri[i] = legalize(t + 2); next[nx] = nx; hullSize--; nx = q; }
    if (e === s) while (q = prev[e], orient(x, y, coords[2 * q], coords[2 * q + 1], coords[2 * e], coords[2 * e + 1]) < 0) { t = add(q, i, e, -1, hTri[e], hTri[q]); legalize(t + 2); hTri[q] = t; next[e] = e; hullSize--; e = q; }
    start = prev[i] = e; next[e] = prev[nx] = i; next[i] = nx;
    hash[hk(x, y)] = i; hash[hk(coords[2 * e], coords[2 * e + 1])] = e;
  }
  return tri.subarray(0, tl);
}

// ---------------------------------------------------------------------------------------------------------- проєкція (конічна Альберса, як у data/geo)
export function makeProj(p) {
  const R = Math.PI / 180, p1 = p.phi1 * R, p2 = p.phi2 * R, p0 = p.phi0 * R;
  const n = (Math.sin(p1) + Math.sin(p2)) / 2, C = Math.cos(p1) ** 2 + 2 * n * Math.sin(p1);
  const rho = (ph) => Math.sqrt(C - 2 * n * Math.sin(ph)) / n;
  return (lat, lon) => { const l = (lon - p.lam0) * R, r = rho(lat * R); const X = r * Math.sin(n * l), Y = rho(p0) - r * Math.cos(n * l); return [(X - p.x0) * p.k, (p.y0 - Y) * p.k]; };
}

// ---------------------------------------------------------------------------------------------------------- геометрія країни
function chaikin(a) {
  const n = a.length / 2; if (n < 3) return a.slice();
  const o = [a[0], a[1]];
  for (let i = 0; i < n - 1; i++) {
    const x0 = a[2 * i], y0 = a[2 * i + 1], x1 = a[2 * i + 2], y1 = a[2 * i + 3];
    if (i > 0) o.push(0.75 * x0 + 0.25 * x1, 0.75 * y0 + 0.25 * y1);
    if (i < n - 2) o.push(0.25 * x0 + 0.75 * x1, 0.25 * y0 + 0.75 * y1);
  }
  o.push(a[2 * n - 2], a[2 * n - 1]);
  return o;
}
/** Згладжена ламана через Catmull-Rom із кроком step (річки, щоб стрічка не була «ламаною»). */
export function resample(l, step) {
  const pts = []; for (let i = 0; i < l.length; i += 2) pts.push([l[i], l[i + 1]]);
  if (pts.length < 2) return [];
  const o = []; const m = pts.length;
  for (let i = 0; i < m - 1; i++) {
    const p0 = pts[Math.max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.min(m - 1, i + 2)];
    const len = Math.hypot(p2[0] - p1[0], p2[1] - p1[1]), k = Math.max(1, Math.round(len / step));
    for (let j = 0; j < k; j++) {
      const t = j / k, t2 = t * t, t3 = t2 * t;
      o.push([0.5 * (2 * p1[0] + (p2[0] - p0[0]) * t + (2 * p0[0] - 5 * p1[0] + 4 * p2[0] - p3[0]) * t2 + (3 * p1[0] - p0[0] - 3 * p2[0] + p3[0]) * t3),
        0.5 * (2 * p1[1] + (p2[1] - p0[1]) * t + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t2 + (3 * p1[1] - p0[1] - 3 * p2[1] + p3[1]) * t3)]);
    }
  }
  o.push(pts[m - 1]);
  return o;
}

/** Дистанційне перетворення Фельценшвальба. inside[i] true → 0; вертає відстань до найближчого inside у пікселях. */
function edt1(f, n, v, z, d) {
  let k = 0; v[0] = 0; z[0] = -1e20; z[1] = 1e20;
  for (let q = 1; q < n; q++) {
    let s;
    do { const r = v[k]; s = (f[q] - f[r] + q * q - r * r) / (2 * q - 2 * r); } while (s <= z[k] && --k >= 0);
    k++; v[k] = q; z[k] = s; z[k + 1] = 1e20;
  }
  k = 0;
  for (let q = 0; q < n; q++) { while (z[k + 1] < q) k++; const r = v[k]; d[q] = (q - r) * (q - r) + f[r]; }
}
function edt(mask, w, h) {
  const g = new Float64Array(w * h), mx = Math.max(w, h), f = new Float64Array(mx), d = new Float64Array(mx), v = new Int32Array(mx), z = new Float64Array(mx + 1);
  for (let i = 0; i < w * h; i++) g[i] = mask[i] ? 0 : 1e20;
  for (let x = 0; x < w; x++) { for (let y = 0; y < h; y++) f[y] = g[y * w + x]; edt1(f, h, v, z, d); for (let y = 0; y < h; y++) g[y * w + x] = d[y]; }
  for (let y = 0; y < h; y++) { for (let x = 0; x < w; x++) f[x] = g[y * w + x]; edt1(f, w, v, z, d); for (let x = 0; x < w; x++) g[y * w + x] = d[x]; }
  const o = new Float32Array(w * h); for (let i = 0; i < w * h; i++) o[i] = Math.sqrt(g[i]); return o;
}

/** Берегова лінія чужих берегів і море: lat/lon руками; полігон моря перекриває сушу України, суша потім віднімається. */
const SEA_POLY = [[43.4, 28.6], [44.17, 28.66], [44.5, 28.85], [44.75, 29.05], [44.88, 29.62], [45.13, 29.76],
  [45.28, 29.68], [45.6, 29.4], [46.2, 29.9], [46.8, 30.6], [47.05, 32], [46.95, 33.5], [46.85, 35], [47.2, 36.5], [47.3, 37.5], [47.17, 38.1],
  [47.14, 38.3], [47.22, 38.92], [47.26, 39.25], [47.08, 39.28], [47.03, 38.9], [46.87, 38.6], [46.72, 38.27], [46.55, 38.3], [46.38, 38.02], [46.08, 38.14], [45.76, 37.95], [45.56, 37.7], [45.36, 37.45], [45.38, 37.05], [45.42, 36.75], [45.3, 36.66], [45.22, 36.62],
  [45.11, 36.7], [45.03, 37.05], [44.9, 37.3], [44.7, 37.8], [44.56, 38.08], [44.3, 38.6], [43.9, 39.2], [43.0, 40]];
const FOREIGN_COAST = [[0, 5], [15, 39]];   // ділянки SEA_POLY, що справжній берег (решта — всередині України)
const fix = (r, a) => a;

export function buildGeo(raw, fixture, hi) {
  const t0 = performance.now();
  const proj = makeProj(raw.proj);
  const r = mulberry(4242);
  // дуги згладжені один раз: і картина, і сітка беруть той самий контур
  const arcs = raw.arcs.map(chaikin);
  const refs = new Uint8Array(arcs.length);
  for (const g of raw.regions) for (const ring of g.rings) for (const id of ring) refs[Math.abs(id) - 1]++;
  const ringPts = (ring) => { const o = []; let first = true; for (const id of ring) { const a = arcs[Math.abs(id) - 1], n = a.length / 2; for (let k = first ? 0 : 1; k < n; k++) { const i = id > 0 ? k : n - 1 - k; o.push(a[2 * i], a[2 * i + 1]); } first = false; } return o; };
  const regions = raw.regions.map((g, i) => ({ name: g.name, short: g.short, rings: g.rings.map(ringPts), idx: i, arcIds: g.rings.flat().map((id) => Math.abs(id) - 1) }));
  const toPath = (rings) => { const p = new Path2D(); for (const rg of rings) { p.moveTo(rg[0], rg[1]); for (let k = 2; k < rg.length; k += 2) p.lineTo(rg[k], rg[k + 1]); p.closePath(); } return p; };
  const land = toPath(regions.flatMap((g) => g.rings));
  for (const g of regions) g.path = toPath(g.rings);
  const outlineArcs = [], innerArcs = [];
  arcs.forEach((a, i) => (refs[i] === 1 ? outlineArcs : innerArcs).push(a));
  const linePath = (ls) => { const p = new Path2D(); for (const a of ls) { p.moveTo(a[0], a[1]); for (let k = 2; k < a.length; k += 2) p.lineTo(a[k], a[k + 1]); } return p; };
  const outline = linePath(outlineArcs), inner = linePath(innerArcs);

  // море: чужий берег — ламаний фрактально, щоб не був «ламаною з шести точок»
  const seaRaw = SEA_POLY.map(([a, b]) => proj(a, b));
  const frac = (a, b, depth, out) => { if (depth === 0) { out.push(a); return; } const l = Math.hypot(b[0] - a[0], b[1] - a[1]); if (l < 30) { out.push(a); return; } const nx = -(b[1] - a[1]) / l, ny = (b[0] - a[0]) / l, o = (r() - 0.5) * l * 0.16; const m = [(a[0] + b[0]) / 2 + nx * o, (a[1] + b[1]) / 2 + ny * o]; frac(a, m, depth - 1, out); frac(m, b, depth - 1, out); };
  const seaPts = []; const foreignChains = FOREIGN_COAST.map(() => []);
  for (let i = 0; i < seaRaw.length; i++) {
    const a = seaRaw[i], b = seaRaw[(i + 1) % seaRaw.length];
    const ci = FOREIGN_COAST.findIndex(([s, e]) => i >= s && i < e);
    if (ci >= 0) { const o = []; frac(a, b, 4, o); seaPts.push(...o); foreignChains[ci].push(...o); } else seaPts.push(a);
  }
  foreignChains.forEach((c, i) => c.push(seaRaw[FOREIGN_COAST[i][1]]));
  const foreignPath = new Path2D(); for (const c of foreignChains) c.forEach((p, i) => (i ? foreignPath.lineTo(p[0], p[1]) : foreignPath.moveTo(p[0], p[1])));
  const seaPath = new Path2D(); seaPts.forEach((p, i) => i ? seaPath.lineTo(p[0], p[1]) : seaPath.moveTo(p[0], p[1])); seaPath.lineTo(DX0 + DW + 900, 4000); seaPath.lineTo(-900, 4000); seaPath.closePath();

  // маски
  const mw = hi ? 1100 : 760, ms = mw / DW, mh = Math.round(DH * ms);
  const cv = document.createElement('canvas'); cv.width = mw; cv.height = mh;
  const g = cv.getContext('2d', { willReadFrequently: true });
  g.setTransform(ms, 0, 0, ms, -DX0 * ms, -DY0 * ms);
  g.fillStyle = '#f00'; g.fill(seaPath);
  g.fillStyle = '#0f0'; g.fill(land, 'evenodd');
  // лимани, що «в суші»: лишаємо, бо контур їх уже не містить
  const img = g.getImageData(0, 0, mw, mh).data;
  const mask = new Uint8Array(mw * mh), inU = new Uint8Array(mw * mh), inS = new Uint8Array(mw * mh);
  for (let i = 0; i < mw * mh; i++) { const R = img[4 * i], G = img[4 * i + 1]; const m = G > 127 ? 1 : R > 127 ? 2 : 0; mask[i] = m; inU[i] = m === 1; inS[i] = m === 2; }
  const edtU = edt(inU, mw, mh), edtNU = edt(mask.map((m) => m !== 1), mw, mh);
  const edtS = edt(inS, mw, mh), edtNS = edt(mask.map((m) => m !== 2), mw, mh);
  const sU = new Float32Array(mw * mh), sS = new Float32Array(mw * mh);
  for (let i = 0; i < mw * mh; i++) { sU[i] = (edtNU[i] - edtU[i]) / ms; sS[i] = (edtNS[i] - edtS[i]) / ms; }  // + всередині, у сітці одиниць

  const pxOf = (x, y) => { const px = Math.max(0, Math.min(mw - 1, Math.round((x - DX0) * ms))), py = Math.max(0, Math.min(mh - 1, Math.round((y - DY0) * ms))); return py * mw + px; };
  const bil = (F, x, y) => { const fx = Math.max(0, Math.min(mw - 1.001, (x - DX0) * ms)), fy = Math.max(0, Math.min(mh - 1.001, (y - DY0) * ms)), xi = fx | 0, yi = fy | 0, tx = fx - xi, ty = fy - yi, i = yi * mw + xi; return F[i] * (1 - tx) * (1 - ty) + F[i + 1] * tx * (1 - ty) + F[i + mw] * (1 - tx) * ty + F[i + mw + 1] * tx * ty; };
  const geo = {
    proj, arcs, regions, land, outline, inner, seaPath, outlineArcs, innerArcs, foreignChains, foreignPath, mask, sU, sS, mw, mh, ms, pxOf, bil, DX0, DY0, DW, DH,
    maskAt: (x, y) => mask[pxOf(x, y)], rivers: [], raw,
  };
  // річки
  const relax = (P, n) => { let a = P; for (let k = 0; k < n; k++) { const b = a.map((p) => p.slice()); for (let i = 1; i < a.length - 1; i++) { b[i][0] = (a[i - 1][0] + 2 * a[i][0] + a[i + 1][0]) / 4; b[i][1] = (a[i - 1][1] + 2 * a[i][1] + a[i + 1][1]) / 4; } a = b; } return a; };
  geo.rivers = (raw.rivers || []).flatMap((rv) => rv.lines.map((l) => ({ name: rv.name, big: rv.big, pts: relax(resample(l, 10), 14), w: rv.big ? 15 : rv.name === 'Дунай' ? 11 : rv.name === 'Дністер' || rv.name === 'Десна' || rv.name === 'Південний Буг' ? 8.5 : 6.5 })));
  geo.ms_ = performance.now() - t0;
  return geo;
}

// ---------------------------------------------------------------------------------------------------------- рельєф
export function buildRelief(geo, routes, cities) {
  const t0 = performance.now();
  const { mw, mh, ms, proj } = geo, n = mw * mh;
  const line = (l) => l.map(([a, b]) => proj(a, b));
  const distPoly = (x, y, P) => { let m = 1e12, tt = 0; for (let i = 0; i < P.length - 1; i++) { const ax = P[i][0], ay = P[i][1], bx = P[i + 1][0] - ax, by = P[i + 1][1] - ay; const t = Math.max(0, Math.min(1, ((x - ax) * bx + (y - ay) * by) / (bx * bx + by * by))); const dx = x - ax - t * bx, dy = y - ay - t * by, d = dx * dx + dy * dy; if (d < m) { m = d; tt = (i + t) / (P.length - 1); } } return [Math.sqrt(m), tt]; };
  const carp = line([[49.3, 22.5], [49.05, 23.1], [48.72, 23.65], [48.4, 24.1], [48.16, 24.5], [47.98, 24.95], [47.85, 25.4]]);
  const crim = line([[44.5, 33.55], [44.45, 34.0], [44.62, 34.4], [44.82, 34.95], [44.98, 35.35]]);
  const don = line([[48.0, 37.6], [48.15, 38.2], [48.3, 38.8], [48.42, 39.45]]);
  const pod = line([[49.7, 24.2], [49.35, 25.8], [49.05, 27.3], [48.75, 28.6], [48.2, 29.6]]);
  const vol = line([[50.45, 24.6], [50.45, 26.2], [50.55, 27.6]]);
  const dnu = line([[50.0, 29.8], [49.1, 30.7], [48.3, 32.2], [47.9, 33.4]]);
  const azo = line([[47.6, 35.4], [47.35, 36.4], [47.3, 37.4]]);
  const ner = line([[51.2, 34.0], [50.4, 35.8], [49.8, 37.6]]);
  const R = new Float32Array(n);
  const angC = Math.atan2(carp[6][1] - carp[0][1], carp[6][0] - carp[0][0]), cc = Math.cos(angC), sc = Math.sin(angC);
  const angK = Math.atan2(crim[4][1] - crim[0][1], crim[4][0] - crim[0][0]), ck = Math.cos(angK), sk = Math.sin(angK);
  const near = (P, pad) => { let a = 1e9, b = 1e9, c = -1e9, d = -1e9; for (const p of P) { a = Math.min(a, p[0]); b = Math.min(b, p[1]); c = Math.max(c, p[0]); d = Math.max(d, p[1]); } return [a - pad, b - pad, c + pad, d + pad]; };
  const bbC = near(carp, 420), bbK = near(crim, 170), bbD = near(don, 320), bbP = near(pod, 480), bbV = near(vol, 300), bbU = near(dnu, 480), bbA = near(azo, 280), bbN = near(ner, 420);
  const inb = (b, x, y) => x > b[0] && x < b[2] && y > b[1] && y < b[3];
  const gs = (d, s) => Math.exp(-(d * d) / (s * s));
  for (let j = 0, i = 0; j < mh; j++) {
    const y = (j + 0.5) / ms + geo.DY0;
    for (let k = 0; k < mw; k++, i++) {
      const x = (k + 0.5) / ms + geo.DX0;
      let h = 0.006 * fbm(x / 240, y / 240, 3) + 0.004 * fbm(x / 70 + 9, y / 70, 2);
      const wa = (fbm(x / 170 + 3, y / 170 + 8, 2) - 0.5) * 120, wb = (fbm(x / 170 - 7, y / 170 + 2, 2) - 0.5) * 120;
      const X = x + wa, Y = y + wb;
      if (inb(bbC, x, y)) {
        const [d] = distPoly(X, Y, carp);
        const u = (x * cc + y * sc), v = (-x * sc + y * cc);
        const rg = 1 - Math.abs(2 * fbm(u / 210, v / 58, 3) - 1);
        const rg2 = 1 - Math.abs(2 * fbm(u / 80 + 7, v / 30 + 2, 2) - 1);
        const m = gs(d, 165);
        h += m * (0.075 + 0.17 * rg * rg + 0.035 * rg2) ;
      }
      if (inb(bbK, x, y)) {
        const [d] = distPoly(X * 0.98 + x * 0.02, Y * 0.98 + y * 0.02, crim);
        const u = (x * ck + y * sk), v = (-x * sk + y * ck);
        const rg = 1 - Math.abs(2 * fbm(u / 110, v / 34, 3) - 1);
        h += gs(d, 62) * (0.05 + 0.115 * rg * rg);
      }
      if (inb(bbD, x, y)) { const [d] = distPoly(X, Y, don); h += gs(d, 125) * (0.02 + 0.042 * fbm(x / 90, y / 60, 3)); }
      if (inb(bbP, x, y)) { const [d] = distPoly(X, Y, pod); h += gs(d, 210) * (0.012 + 0.028 * fbm(x / 120, y / 90, 3)); }
      if (inb(bbV, x, y)) { const [d] = distPoly(X, Y, vol); h += gs(d, 130) * 0.016 * fbm(x / 100, y / 100, 2); }
      if (inb(bbU, x, y)) { const [d] = distPoly(X, Y, dnu); h += gs(d, 210) * (0.006 + 0.022 * fbm(x / 130, y / 110, 3)); }
      if (inb(bbA, x, y)) { const [d] = distPoly(X, Y, azo); h += gs(d, 120) * 0.022 * fbm(x / 110, y / 90, 2); }
      if (inb(bbN, x, y)) { const [d] = distPoly(X, Y, ner); h += gs(d, 190) * 0.02 * fbm(x / 140, y / 110, 2); }
      R[i] = h;
    }
  }
  // біля кордону й берега рельєф сходить нанівець: уступ лишається низьким, скеля не тягне текстуру
  for (let i = 0; i < n; i++) { const d = Math.abs(geo.sU[i]); R[i] *= 0.08 + 0.92 * smooth(2, 62, d); }
  // долини: рельєф падає до річок
  const rm = new Uint8Array(n);
  for (const rv of geo.rivers) for (const p of rv.pts) { rm[geo.pxOf(p[0], p[1])] = 1; }
  const dR = edtRaw(rm, mw, mh);
  for (let i = 0; i < n; i++) { const d = dR[i] / ms; R[i] *= 0.45 + 0.55 * smooth(10, 170, d); }
  geo.riverDist = dR;
  // коридори шляхів і міста: тут земля пригладжена, щоб плашки не висіли і не тонули
  const rtm = new Uint8Array(n);
  for (const rt of routes) for (const p of rt.samples) rtm[geo.pxOf(p[0], p[1])] = 1;
  const dT = edtRaw(rtm, mw, mh);
  const blurred = boxBlur(R, mw, mh, Math.round(34 * ms));
  for (let i = 0; i < n; i++) { const d = dT[i] / ms; const m = 1 - smooth(34, 120, d); R[i] = R[i] + (blurred[i] - R[i]) * m; }
  const bl2 = boxBlur(R, mw, mh, Math.round(16 * ms));
  for (const c of cities) {
    const r0 = c.r + 16, r1 = c.r + 66, base = geo.bil(bl2, c.x, c.y);
    const px0 = Math.max(0, Math.floor((c.x - r1 - geo.DX0) * ms)), px1 = Math.min(mw - 1, Math.ceil((c.x + r1 - geo.DX0) * ms));
    const py0 = Math.max(0, Math.floor((c.y - r1 - geo.DY0) * ms)), py1 = Math.min(mh - 1, Math.ceil((c.y + r1 - geo.DY0) * ms));
    for (let j = py0; j <= py1; j++) for (let k = px0; k <= px1; k++) {
      const x = (k + 0.5) / ms + geo.DX0, y = (j + 0.5) / ms + geo.DY0, d = Math.hypot(x - c.x, y - c.y), m = 1 - smooth(r0, r1, d), i = j * mw + k;
      R[i] = R[i] + (base - R[i]) * m;
    }
  }
  geo.R = R;
  geo.reliefMs = performance.now() - t0;
  return R;
}
function edtRaw(mask, w, h) { return edt(mask, w, h); }
function boxBlur(F, w, h, r) {
  let a = Float32Array.from(F), b = new Float32Array(w * h);
  for (let pass = 0; pass < 3; pass++) {
    for (let y = 0; y < h; y++) { let s = 0; const row = y * w; for (let x = -r; x <= r; x++) s += a[row + Math.max(0, Math.min(w - 1, x))]; for (let x = 0; x < w; x++) { b[row + x] = s / (2 * r + 1); s += a[row + Math.min(w - 1, x + r + 1)] - a[row + Math.max(0, x - r)]; } }
    for (let x = 0; x < w; x++) { let s = 0; for (let y = -r; y <= r; y++) s += b[Math.max(0, Math.min(h - 1, y)) * w + x]; for (let y = 0; y < h; y++) { a[y * w + x] = s / (2 * r + 1); s += b[Math.min(h - 1, y + r + 1) * w + x] - b[Math.max(0, y - r) * w + x]; } }
  }
  return a;
}

/** Висота світу в точці карти: для всього, що ставимо на землю. */
export function heightAt(geo, x, y) {
  const m = geo.mask[geo.pxOf(x, y)];
  const r = geo.bil(geo.R, x, y);
  if (m === 1) return LV.slab + r;
  if (m === 2) return LV.sea + 0.003;
  return LV.foreign + r * 0.32;
}

// ---------------------------------------------------------------------------------------------------------- сітка
export function buildMesh(geo, hi) {
  const t0 = performance.now();
  const CELL = 40, gw = Math.ceil(DW / CELL) + 2, gh = Math.ceil(DH / CELL) + 2;
  const grid = new Array(gw * gh);
  const xs = [], ys = [], yv = []; // yv: NaN → рахувати з поля
  const add = (x, y, minD, fy) => {
    if (x < DX0 - 0.01 || y < DY0 - 0.01 || x > DX0 + DW + 0.01 || y > DY0 + DH + 0.01) return -1;
    const gx = Math.floor((x - DX0) / CELL), gy = Math.floor((y - DY0) / CELL), rr = Math.ceil(minD / CELL), d2 = minD * minD;
    for (let j = Math.max(0, gy - rr); j <= Math.min(gh - 1, gy + rr); j++) for (let i = Math.max(0, gx - rr); i <= Math.min(gw - 1, gx + rr); i++) {
      const c = grid[j * gw + i]; if (!c) continue;
      for (const q of c) { const dx = xs[q] - x, dy = ys[q] - y; if (dx * dx + dy * dy < d2) return -1; }
    }
    const id = xs.length; xs.push(x); ys.push(y); yv.push(fy === undefined ? NaN : fy);
    const k = gy * gw + gx; (grid[k] || (grid[k] = [])).push(id);
    return id;
  };
  const r = rng(991);
  const coastStep = hi ? 11 : 20, D = hi ? 9 : 12;
  // кордони: берег і суходільний кордон — точки лінії й кільця з обох боків (уступ без сходинок)
  const chains = [];
  for (const a of geo.outlineArcs) { const p = []; for (let k = 0; k < a.length; k += 2) p.push([a[k], a[k + 1]]); chains.push({ pts: p, kind: 'ukr' }); }
  for (const c of geo.foreignChains) chains.push({ pts: c, kind: 'foreign' });
  const seaNear = (x, y, rad) => geo.bil(geo.sS, x, y) > -rad;
  const edgeItems = [];
  for (const ch of chains) {
    const P = ch.pts; let acc = 1e9; const keep = [];
    for (let i = 0; i < P.length; i++) { if (i > 0) acc += Math.hypot(P[i][0] - P[i - 1][0], P[i][1] - P[i - 1][1]); if (acc >= coastStep || i === P.length - 1) { keep.push(P[i]); acc = 0; } }
    for (let i = 0; i < keep.length; i++) {
      const p = keep[i], a = keep[Math.max(0, i - 1)], b = keep[Math.min(keep.length - 1, i + 1)];
      const l = Math.hypot(b[0] - a[0], b[1] - a[1]) || 1, nx = -(b[1] - a[1]) / l, ny = (b[0] - a[0]) / l;
      edgeItems.push({ x: p[0], y: p[1], nx, ny, kind: ch.kind });
    }
  }
  for (const e of edgeItems) {
    const coast = seaNear(e.x, e.y, 16) || e.kind === 'foreign';
    e.coast = coast;
    add(e.x, e.y, coastStep * 0.5, coast ? LV.coast : NaN);
  }
  // кільця: зсув по нормалі з перевіркою, чи не заліз на інший берег
  const sdU = (x, y) => geo.bil(geo.sU, x, y), sdS = (x, y) => geo.bil(geo.sS, x, y);
  for (const e of edgeItems) {
    for (const sgn of [1, -1]) {
      const x = e.x + e.nx * D * sgn, y = e.y + e.ny * D * sgn;
      if (e.coast) {
        const s = sdS(x, y);
        if (s > D * 0.55) add(x, y, coastStep * 0.5, LV.bed);
        else if (s < -D * 0.55) add(x, y, coastStep * 0.5);
      } else {
        const s = sdU(x, y);
        if (Math.abs(s) > D * 0.55) add(x, y, coastStep * 0.5);
      }
    }
  }
  // друге кільце над морем: дно під водою, щоб вода різала землю чіткою лінією
  for (const e of edgeItems) if (e.coast) for (const sgn of [1, -1]) {
    const x = e.x + e.nx * D * 2.6 * sgn, y = e.y + e.ny * D * 2.6 * sgn;
    if (sdS(x, y) > D * 1.8) add(x, y, coastStep * 1.1, LV.bed);
  }
  // річки: береги, стрижень
  const rstep = hi ? 16 : 26;
  for (const rv of geo.rivers) {
    const P = rv.pts; let acc = 1e9;
    for (let i = 1; i < P.length - 1; i++) {
      acc += Math.hypot(P[i][0] - P[i - 1][0], P[i][1] - P[i - 1][1]);
      if (acc < rstep) continue; acc = 0;
      const dx = P[i + 1][0] - P[i - 1][0], dy = P[i + 1][1] - P[i - 1][1], l = Math.hypot(dx, dy) || 1, nx = -dy / l, ny = dx / l;
      for (const o of [0, 1, -1, 1.45, -1.45, 2.4, -2.4]) { const x = P[i][0] + nx * o * rv.w, y = P[i][1] + ny * o * rv.w; if (Math.abs(sdU(x, y)) > D * 1.3 && Math.abs(sdS(x, y)) > D * 1.3) add(x, y, rv.w * 0.45); }
    }
  }
  // нутро: розкидані точки (тремтяча стільникова сітка лише як генератор; після Delaunay й гладких нормалей її не видно)
  const scatter = (sp, test) => {
    const rowH = sp * 0.866, jit = sp * 0.42;
    for (let j = 0, y = DY0; y <= DY0 + DH; y += rowH, j++) for (let x = DX0 + (j & 1 ? sp / 2 : 0); x <= DX0 + DW; x += sp) {
      const X = x + (r() - 0.5) * 2 * jit, Y = y + (r() - 0.5) * 2 * jit;
      if (test(X, Y)) add(X, Y, sp * 0.62);
    }
  };
  const margin = (x, y, m) => Math.abs(sdU(x, y)) > m && Math.abs(sdS(x, y)) > m;
  const slope = (x, y) => { const e = 8, a = geo.bil(geo.R, x + e, y), b = geo.bil(geo.R, x - e, y), c = geo.bil(geo.R, x, y + e), d = geo.bil(geo.R, x, y - e); return Math.hypot(a - b, c - d) / (2 * e) * 400; };
  const f = hi ? 1 : 1.75;
  scatter(11 * f, (x, y) => geo.maskAt(x, y) === 1 && slope(x, y) > 0.07 && margin(x, y, D * 1.3));
  scatter(26 * f, (x, y) => geo.maskAt(x, y) === 1 && margin(x, y, D * 1.3));
  scatter(70 * f, (x, y) => geo.maskAt(x, y) === 0 && margin(x, y, D * 1.3));
  // край карти (під паспарту)
  for (let x = DX0; x <= DX0 + DW; x += 60) { add(x, DY0, 20); add(x, DY0 + DH, 20); }
  for (let y = DY0; y <= DY0 + DH; y += 60) { add(DX0, y, 20); add(DX0 + DW, y, 20); }

  const n = xs.length, coords = new Float64Array(2 * n);
  for (let i = 0; i < n; i++) { coords[2 * i] = xs[i]; coords[2 * i + 1] = ys[i]; }
  const tris = delaunay(coords);

  // річки: прогин. Спершу просторовий індекс точок річок
  const RC = 48, rix = new Map();
  for (const rv of geo.rivers) for (const p of rv.pts) { const k = Math.floor(p[0] / RC) + ',' + Math.floor(p[1] / RC); (rix.get(k) || rix.set(k, []).get(k)).push([p[0], p[1], rv.w]); }
  const riverNear = (x, y) => { let best = 1e9, bw = 1; const gx = Math.floor(x / RC), gy = Math.floor(y / RC); for (let j = gy - 1; j <= gy + 1; j++) for (let i = gx - 1; i <= gx + 1; i++) { const c = rix.get(i + ',' + j); if (!c) continue; for (const q of c) { const d = Math.hypot(q[0] - x, q[1] - y); if (d / q[2] < best / bw) { best = d; bw = q[2]; } } } return [best, bw]; };

  // висоти
  const pos = new Float32Array(n * 3), nor = new Float32Array(n * 3), uv = new Float32Array(n * 2);
  const CW = W + 2 * (PAD + MAT), CH = H + 2 * (PAD + MAT), OFF = PAD + MAT;
  for (let i = 0; i < n; i++) {
    const x = xs[i], y = ys[i], m = geo.maskAt(x, y);
    let h;
    if (!Number.isNaN(yv[i])) h = yv[i];
    else {
      const su = sdU(x, y), rr = geo.bil(geo.R, x, y);
      const ukr = LV.slab + rr, frn = LV.foreign + rr * 0.32;
      if (Math.abs(su) < D * 0.4) h = (ukr + frn) / 2;
      else h = su > 0 ? ukr : (m === 2 ? LV.bed : frn);
    }
    if (h > LV.coast + 0.001) {
      const [d, w] = riverNear(x, y);
      if (d < w * 1.5) { const depth = 0.013 + w * 0.0003; h -= depth * (1 - smooth(w * 1.1, w * 1.5, d)); }
    }
    pos[3 * i] = wx(x); pos[3 * i + 1] = h; pos[3 * i + 2] = wz(y);
    uv[2 * i] = (x + OFF) / CW; uv[2 * i + 1] = 1 - (y + OFF) / CH;
  }
  // трикутники: у морі — геть
  const idx = [];
  const isDry = (a, b, c) => {
    const cx = (xs[a] + xs[b] + xs[c]) / 3, cy = (ys[a] + ys[b] + ys[c]) / 3;
    if (geo.maskAt(cx, cy) === 2 && sdS(cx, cy) > D * 2.2) return false;
    return true;
  };
  for (let t = 0; t < tris.length; t += 3) {
    const a = tris[t], b = tris[t + 1], c = tris[t + 2];
    if (!isDry(a, b, c)) continue;
    // орієнтація: нормаль угору
    const ux = pos[3 * b] - pos[3 * a], uz = pos[3 * b + 2] - pos[3 * a + 2], vx = pos[3 * c] - pos[3 * a], vz = pos[3 * c + 2] - pos[3 * a + 2];
    if (uz * vx - ux * vz > 0) idx.push(a, b, c); else idx.push(a, c, b);
  }
  // нормалі: геометрія (згладжена), а всередині — аналітично з поля
  for (let t = 0; t < idx.length; t += 3) {
    const a = idx[t], b = idx[t + 1], c = idx[t + 2];
    const ux = pos[3 * b] - pos[3 * a], uy = pos[3 * b + 1] - pos[3 * a + 1], uz = pos[3 * b + 2] - pos[3 * a + 2];
    const vx = pos[3 * c] - pos[3 * a], vy = pos[3 * c + 1] - pos[3 * a + 1], vz = pos[3 * c + 2] - pos[3 * a + 2];
    const X = uy * vz - uz * vy, Y = uz * vx - ux * vz, Z = ux * vy - uy * vx;
    for (const v of [a, b, c]) { nor[3 * v] += X; nor[3 * v + 1] += Y; nor[3 * v + 2] += Z; }
  }
  for (let i = 0; i < n; i++) {
    const x = xs[i], y = ys[i];
    let nx = nor[3 * i], ny = nor[3 * i + 1], nz = nor[3 * i + 2];
    if (Math.abs(sdU(x, y)) > D * 2.5 && Math.abs(sdS(x, y)) > D * 2.5) {
      const e = 10, k = geo.maskAt(x, y) === 1 ? 1 : 0.32;
      nx = -(geo.bil(geo.R, x + e, y) - geo.bil(geo.R, x - e, y)) * k / (2 * e * U); nz = -(geo.bil(geo.R, x, y + e) - geo.bil(geo.R, x, y - e)) * k / (2 * e * U); ny = 1;
    }
    const l = Math.hypot(nx, ny, nz) || 1; nor[3 * i] = nx / l; nor[3 * i + 1] = ny / l; nor[3 * i + 2] = nz / l;
  }
  geo.meshMs = performance.now() - t0;
  return { pos, nor, uv, idx, verts: n, tris: idx.length / 3 };
}
