// Мапа-ілюстрація: контур, море, рельєф, шляхи, вози, міста. Будується окремо під масштаб екрана (px на одиницю сітки).
import { readFileSync } from 'node:fs';
import { TYPES, INK, rng } from './art.mjs';
const REPO = '/Users/admin/olimp/glechyk/';
export const FX = JSON.parse(readFileSync(new URL('../fixture.json', import.meta.url)));
export const GEO = JSON.parse(readFileSync(REPO + 'web/games/geo-map.json'));
export const GLEK = readFileSync(REPO + 'web/static/glek.svg', 'utf8');

// проєкція та сама, що в data/geo/build-map.py
const r_ = (d) => d * Math.PI / 180;
const PHI1 = 46, PHI2 = 51, PHI0 = 48.5, LAM0 = 31.5, X0 = -0.11, Y0 = 0.07, K = 19000;
const N = (Math.sin(r_(PHI1)) + Math.sin(r_(PHI2))) / 2, C = Math.cos(r_(PHI1)) ** 2 + 2 * N * Math.sin(r_(PHI1));
const RHO0 = Math.sqrt(C - 2 * N * Math.sin(r_(PHI0))) / N;
export function proj(lat, lon) {
  const rho = Math.sqrt(C - 2 * N * Math.sin(r_(lat))) / N, th = N * (r_(lon) - r_(LAM0));
  return [(rho * Math.sin(th) - X0) * K, (Y0 - (RHO0 - rho * Math.cos(th))) * K];
}

// ── геометрія країни ──
const arcs = GEO.arcs, refs = new Uint8Array(arcs.length);
for (const r of GEO.regions) for (const ring of r.rings) for (const id of ring) refs[Math.abs(id) - 1]++;
const rings = [];
let landD = '';
for (const r of GEO.regions) for (const ring of r.rings) {
  const pts = []; let first = true;
  for (const id of ring) {
    const a = arcs[Math.abs(id) - 1], n = a.length / 2;
    for (let k = first ? 0 : 1; k < n; k++) { const i = id > 0 ? k : n - 1 - k; pts.push([a[2 * i], a[2 * i + 1]]); }
    first = false;
  }
  rings.push(pts);
  landD += 'M' + pts.map((p) => p[0] + ' ' + p[1]).join('L') + 'Z';
}
let outlineD = '', bordersD = '';
arcs.forEach((a, i) => {
  let d = 'M' + a[0] + ' ' + a[1];
  for (let k = 2; k < a.length; k += 2) d += 'L' + a[k] + ' ' + a[k + 1];
  if (refs[i] === 1) outlineD += d; else bordersD += d;
});
export { landD, outlineD };
const inRing = (x, y, r) => { let c = false; for (let i = 0, j = r.length - 1; i < r.length; j = i++) { const [xi, yi] = r[i], [xj, yj] = r[j]; if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) c = !c; } return c; };
export const inLand = (x, y) => rings.some((r) => inRing(x, y, r));

// море: берег сусідів — справжні точки, північний край заходить під суходіл України
const SEA_LL = [
  [45.16, 29.72], [44.9, 29.62], [44.55, 28.95], [44.17, 28.66], [43.8, 28.6], [43.2, 28.0], [41.5, 28.0], [41.5, 42.0],
  [43.6, 39.7], [44.1, 39.07], [44.56, 38.07], [44.72, 37.8], [44.89, 37.31], [45.08, 36.75], [45.11, 36.62], [45.22, 36.65], [45.33, 36.9], [45.33, 37.38], [45.62, 37.62], [46.05, 38.12], [46.35, 37.85], [46.68, 38.24], [46.92, 38.42], [47.1, 38.6], [47.2, 38.93], [47.26, 38.3],
  [47.25, 38.0], [47.2, 37.5], [46.95, 36.6], [47.0, 35.4], [46.95, 34.5], [46.85, 33.4], [47.05, 32.2], [46.75, 31.1], [46.45, 30.5], [46.0, 30.1], [45.7, 29.9], [45.45, 29.65],
];
export const seaD = 'M' + SEA_LL.map(([a, b]) => proj(a, b).map((v) => v.toFixed(0)).join(' ')).join('L') + 'Z';

const cityOf = Object.fromEntries(FX.cities.map((c) => [c.name, c]));
export const CLAIM = {};
for (const [p, ids] of Object.entries(FX.mock.claimed)) for (const id of ids) CLAIM[id] = +p;

// ── дуги шляхів: добір вигинів, щоб клітинки не лізли одна на одну ──
function bez(a, b, bend) {
  const dx = b.x - a.x, dy = b.y - a.y, d = Math.hypot(dx, dy), nx = -dy / d, ny = dx / d;
  const c = { x: (a.x + b.x) / 2 + nx * bend, y: (a.y + b.y) / 2 + ny * bend };
  const pts = [];
  const M = 80; let L = 0, prev = null;
  for (let i = 0; i <= M; i++) {
    const t = i / M, u = 1 - t;
    const p = { x: u * u * a.x + 2 * u * t * c.x + t * t * b.x, y: u * u * a.y + 2 * u * t * c.y + t * t * b.y };
    if (prev) L += Math.hypot(p.x - prev.x, p.y - prev.y);
    p.s = L; pts.push(p); prev = p;
  }
  return { c, pts, L };
}
function atS(pts, s) {
  let i = 1; while (i < pts.length - 1 && pts[i].s < s) i++;
  const a = pts[i - 1], b = pts[i], f = (s - a.s) / (b.s - a.s || 1);
  return { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f, ang: Math.atan2(b.y - a.y, b.x - a.x) };
}
function layoutRoute(R, bend, o) {
  const a = cityOf[R.a], b = cityOf[R.b], B = bez(a, b, bend);
  const free = B.L - 2 * o.clear, gap = o.gap, n = R.len;
  let sl = Math.min(o.cap, (free - gap * (n - 1)) / n);
  const tot = sl * n + gap * (n - 1), s0 = (B.L - tot) / 2;
  const slots = [];
  for (let i = 0; i < n; i++) { const c = atS(B.pts, s0 + sl / 2 + i * (sl + gap)); slots.push({ ...c, len: sl }); }
  return { R, a, b, bend, B, slots, sl, s0, tot };
}
const samplePts = (L, step) => { const out = []; for (const sl of L.slots) { const k = Math.max(1, Math.round(sl.len / step)); for (let i = 0; i < k; i++) { const f = (i + .5) / k - .5; out.push([sl.x + Math.cos(sl.ang) * f * sl.len, sl.y + Math.sin(sl.ang) * f * sl.len]); } } return out; };

export function layoutAll(o, avoid = () => 0) {
  const routes = FX.routes;
  const cand = (d) => { const out = []; for (const f of [.05, -.05, .08, -.08, .11, -.11, .14, -.14, .17, -.17, .2, -.2]) out.push(f * d); return out; };
  const force = o.force || {};
  let lay = routes.map((R) => { const a = cityOf[R.a], b = cityOf[R.b]; return layoutRoute(R, force[R.id] ?? Math.hypot(a.x - b.x, a.y - b.y) * .06, o); });
  let spts = lay.map((L) => samplePts(L, o.T * .7));
  const nearD = o.T * 1.32;
  const cost = (L, idx) => {
    let c = Math.abs(L.bend) / Math.hypot(L.a.x - L.b.x, L.a.y - L.b.y) * 6;
    const P = samplePts(L, o.T * .7);
    for (let j = 0; j < lay.length; j++) {
      if (j === idx) continue;
      for (const [x, y] of P) for (const q of spts[j]) { const d = Math.hypot(x - q[0], y - q[1]); if (d < nearD) c += (nearD - d) / o.T * 3; }
    }
    for (const ci of FX.cities) {
      if (ci === L.a || ci === L.b) continue;
      for (const [x, y] of P) { const d = Math.hypot(x - ci.x, y - ci.y); if (d < o.node + o.T) c += 30 * (o.node + o.T - d) / o.T; }
    }
    if (L.sl < o.T * .9) c += 20;
    for (const [x, y] of P) c += avoid(x, y);
    return c;
  };
  for (let pass = 0; pass < 7; pass++) {
    lay.forEach((L, i) => {
      if (force[L.R.id] != null) return;
      const d = Math.hypot(L.a.x - L.b.x, L.a.y - L.b.y);
      let best = L, bc = cost(L, i);
      for (const b of cand(d)) { const T = layoutRoute(L.R, b, o), c = cost(T, i); if (c < bc - .001) { bc = c; best = T; } }
      lay[i] = best; spts[i] = samplePts(best, o.T * .7);
    });
  }
  return lay;
}

// ── підписи міст ──
const SHORT = { "Кам'янець-Подільський": ["Кам'янець-", 'Подільський'], 'Івано-Франківськ': ['Івано-', 'Франківськ'] };
export function placeLabels(lay, o, fixed = {}) {
  const sc = o.sc, fs = o.fs / sc, pad = 3 / sc, lh = fs * 1.14, out = {};
  const obst = [];
  for (const L of lay) for (const sl of L.slots) {
    const k = Math.max(1, Math.round(sl.len / (o.T * .6)));
    for (let i = 0; i < k; i++) { const f = (i + .5) / k - .5; obst.push([sl.x + Math.cos(sl.ang) * f * sl.len, sl.y + Math.sin(sl.ang) * f * sl.len, o.T * .62]); }
  }
  for (const c of FX.cities) obst.push([c.x, c.y, o.node * 1.15, 12]);
  const placed = [];
  const hitCost = (r) => {
    let c = 0;
    for (const [x, y, rad, w = 6] of obst) { const dx = Math.max(r.x0 - x, 0, x - r.x1), dy = Math.max(r.y0 - y, 0, y - r.y1), d = Math.hypot(dx, dy); if (d < rad) c += (rad - d) / rad * w; }
    for (const q of placed) { if (r.x0 < q.x1 && r.x1 > q.x0 && r.y0 < q.y1 && r.y1 > q.y0) c += 100; }
    if (o.bounds && (r.x0 < o.bounds[0] || r.x1 > o.bounds[2] || r.y0 < o.bounds[1] || r.y1 > o.bounds[3])) c += 60;
    return c;
  };
  const cities = [...FX.cities].sort((a, b) => b.name.length - a.name.length);
  const rectOf = (c, ax, ay, off) => {
    const lines = SHORT[c.name] || [c.name];
    const w = Math.max(...lines.map((l) => l.length)) * fs * .595 + pad * 3, h = lines.length * lh + pad * 1.4;
    const cx = c.x + ax * (o.node + off + w / 2 * Math.abs(ax)), cy = c.y + ay * (o.node + off + h / 2 * Math.abs(ay));
    return { x0: cx - w / 2, x1: cx + w / 2, y0: cy - h / 2, y1: cy + h / 2, cx, cy, w, h, lines, c };
  };
  const dirs = [[0, -1], [0, 1], [1, 0], [-1, 0], [.8, -.8], [-.8, -.8], [.8, .8], [-.8, .8], [.5, -1], [-.5, -1], [.5, 1], [-.5, 1]];
  const choose = (c) => {
    if (fixed[c.name]) { const [ax, ay, off = 2 / sc] = fixed[c.name]; return rectOf(c, ax, ay, off); }
    let best = null, bc = 1e9;
    for (const off of [2 / sc, 6 / sc, 11 / sc]) dirs.forEach(([ax, ay], di) => {
      const r = rectOf(c, ax, ay, off), cc = hitCost(r) + di * .02 + off * sc * .03;
      if (cc < bc) { bc = cc; best = r; }
    });
    return best;
  };
  for (const c of cities) { const r = choose(c); placed.push(r); out[c.name] = r; }
  // другий прохід: кожен ще раз шукає краще місце
  for (let pass = 0; pass < 3; pass++) for (const c of cities) {
    const i = placed.indexOf(out[c.name]); placed.splice(i, 1);
    const r = choose(c); placed.push(r); out[c.name] = r;
  }
  if (o.report) for (const c of cities) { const r = out[c.name]; const i = placed.indexOf(r); placed.splice(i, 1); const h = hitCost(r); placed.push(r);  }
  return out;
}

const f1 = (v) => (+v).toFixed(1);
const shade = (hex, k) => { const n = parseInt(hex.slice(1), 16); const r = n >> 16, g = (n >> 8) & 255, b = n & 255; const m = (c) => Math.round(k < 0 ? c * (1 + k) : c + (255 - c) * k); return '#' + ((1 << 24) | (m(r) << 16) | (m(g) << 8) | m(b)).toString(16).slice(1); };
export { shade };

// ── рельєф ──
const peak = (x, y, s, seed = 0) => {
  const R = rng(seed + 11), w = s * (.9 + R() * .25), h = s * (1 + R() * .3);
  return `<g transform="translate(${f1(x)} ${f1(y)})">
    <path d="M${f1(-w)} 0 L0 ${f1(-h)} L${f1(w)} 0Z" fill="#7d9a6a"/><path d="M0 ${f1(-h)} L${f1(w)} 0 L${f1(w * .15)} 0 L${f1(-w * .12)} ${f1(-h * .5)}Z" fill="#587a52"/>
    <path d="M0 ${f1(-h)} L${f1(w * .34)} ${f1(-h * .63)} L${f1(w * .12)} ${f1(-h * .72)} L${f1(-w * .06)} ${f1(-h * .58)} L${f1(-w * .36)} ${f1(-h * .62)}Z" fill="#fff"/>
    <path d="M${f1(-w)} 0 L0 ${f1(-h)} L${f1(w)} 0" fill="none" stroke="#2f4a33" stroke-width="${f1(s * .075)}" stroke-linejoin="round" stroke-linecap="round"/></g>`;
};
const hill = (x, y, s) => `<path d="M${f1(x - s)} ${f1(y)} Q${f1(x)} ${f1(y - s * 1.1)} ${f1(x + s)} ${f1(y)}" fill="#8db25c" stroke="#55793c" stroke-width="${f1(s * .12)}"/>`;
const tree = (x, y, s) => `<g transform="translate(${f1(x)} ${f1(y)})"><rect x="${f1(-s * .1)}" y="${f1(-s * .3)}" width="${f1(s * .2)}" height="${f1(s * .4)}" fill="#6b4a25"/><path d="M0 ${f1(-s * 1.5)} L${f1(s * .62)} ${f1(-s * .2)} L${f1(-s * .62)} ${f1(-s * .2)}Z" fill="#3f8a4a" stroke="#28663a" stroke-width="${f1(s * .1)}" stroke-linejoin="round"/></g>`;
const tuft = (x, y, s) => `<path d="M${f1(x - s * .5)} ${f1(y)} q${f1(s * .1)} ${f1(-s * .5)} ${f1(s * .2)} ${f1(-s * .8)} M${f1(x)} ${f1(y)} v${f1(-s)} M${f1(x + s * .5)} ${f1(y)} q${f1(-s * .1)} ${f1(-s * .5)} ${f1(-s * .2)} ${f1(-s * .8)}" stroke="#9aa84e" stroke-width="${f1(s * .16)}" fill="none" stroke-linecap="round"/>`;
const flower = (x, y, s) => { let p = ''; for (let i = 0; i < 8; i++) { const a = i * .785; p += `<ellipse cx="${f1(x + Math.cos(a) * s * .6)}" cy="${f1(y + Math.sin(a) * s * .6)}" rx="${f1(s * .34)}" ry="${f1(s * .22)}" transform="rotate(${f1(a * 57.3)} ${f1(x + Math.cos(a) * s * .6)} ${f1(y + Math.sin(a) * s * .6)})" fill="#ffcb2e"/>`; } return p + `<circle cx="${f1(x)}" cy="${f1(y)}" r="${f1(s * .36)}" fill="#6b3e16"/>`; };
const wave = (x, y, s) => `<path d="M${f1(x)} ${f1(y)} q${f1(s * .5)} ${f1(-s * .5)} ${f1(s)} 0 t${f1(s)} 0" fill="none" stroke="#e8f8ff" stroke-width="${f1(s * .13)}" stroke-linecap="round" opacity=".55"/>`;

// кільця заборони для малюнків (щоб ліс і хвилі не лізли під клітинки)
export function terrain(lay, o) {
  const sc = o.sc, u = (px) => px / sc; const R = rng(77);
  const pts = []; for (const L of lay) for (const [x, y] of samplePts(L, o.T * .8)) pts.push([x, y]);
  for (const c of FX.cities) pts.push([c.x, c.y]);
  for (let x = 1720; x <= 2300; x += 60) pts.push([x, 2388]);
  for (let x = 2930; x <= 3450; x += 60) pts.push([x, 2184]);
  for (const q of [[2375, 2570], [2375, 2500], [2375, 2640], [2300, 2570], [2450, 2570], [1700, 2510]]) pts.push(q);
  const clearOf = (x, y, d) => pts.every((p) => Math.hypot(p[0] - x, p[1] - y) > d);
  let trees = '', tufts = '', flowers = '', hills = '', waves = '';
  const placedT = [];
  const spaced = (x, y, d) => placedT.every((p) => Math.hypot(p[0] - x, p[1] - y) > d);
  for (let i = 0; i < 2600; i++) {
    const x = R() * 4000, y = R() * 2730;
    if (!inLand(x, y)) continue;
    const pole = y < 620 - (x - 2000) * .05 && x < 2700, sw = y > 1450;
    if (!clearOf(x, y, u(15))) continue;
    if (pole) { if (!spaced(x, y, u(19))) continue; placedT.push([x, y]); trees += tree(x, y, u(9 + R() * 3)); }
    else if (sw) { if (!spaced(x, y, u(34))) continue; placedT.push([x, y]); tufts += tuft(x, y, u(9)); }
    else if (y > 650 && y < 1300 && x > 1100 && x < 3100) { if (!spaced(x, y, u(44))) continue; placedT.push([x, y]); if (R() < .4) flowers += flower(x, y, u(5)); else tufts += tuft(x, y, u(8)); }
  }
  // Карпати: справжні вершини за широтою-довготою
  const CARP = [[49.05,22.75],[48.86,23.05],[48.92,23.5],[48.68,23.25],[48.72,23.75],[48.45,23.45],[48.48,23.95],[48.28,23.72],[48.27,24.22],[48.08,24.0],[48.14,24.52],[47.98,24.55],[48.0,24.92],[47.92,25.22],[48.2,24.95],[48.42,24.55],[48.08,25.35],[48.6,24.2]];
  const carp = CARP.map(([a, b]) => proj(a, b)).sort((p, q) => p[1] - q[1]);
  let mts = '';
  carp.forEach(([x, y], i) => { if (inLand(x, y) && clearOf(x, y, u(11))) mts += peak(x, y + u(7), u(13.5 + (i % 3) * 2), i); });
  // Кримські гори
  const CRM = [[44.56,33.92],[44.64,33.78],[44.66,34.12],[44.76,34.28],[44.84,34.58],[44.9,34.88],[44.97,35.14],[44.76,34.02],[44.88,34.34]];
  CRM.map(([a, b]) => proj(a, b)).sort((p, q) => p[1] - q[1]).forEach(([x, y], i) => { if (inLand(x, y) && clearOf(x, y, u(9))) mts += peak(x, y + u(6), u(12.5 + (i % 2) * 2), i + 40); });
  // Донецький кряж і Придніпровські горби
  [[3050,1140],[3180,1340],[3320,1520],[3640,1400],[3660,1560],[3580,1240],[2240,1190],[1650,1120],[1120,1160],[1680,1440]].forEach(([x, y]) => { if (inLand(x, y) && clearOf(x, y, u(13))) hills += hill(x, y, u(11)); });
  // хвилі
  for (let i = 0; i < 900; i++) {
    const x = R() * 4000, y = 1950 + R() * 800; const b = bbSea(x, y);
    if (!b || inLand(x, y) || !clearOf(x, y, u(14)) || !spaced(x, y, u(44))) continue;
    placedT.push([x, y]); waves += wave(x, y, u(8));
  }
  return { trees, tufts, flowers, hills, mts, waves };
}
const seaPts = SEA_LL.map(([a, b]) => proj(a, b));
const bbSea = (x, y) => inRing(x, y, seaPts);

// Підписи річок: на місці, де найвільніше від клітинок, міст і підписів
export function riverLabels(lay, labels, o, list) {
  const obst = [];
  for (const L of lay) for (const [x, y] of samplePts(L, o.T * .6)) obst.push([x, y, o.T * .6]);
  for (const c of FX.cities) obst.push([c.x, c.y, o.node * 1.2]);
  for (const r of Object.values(labels)) for (let i = 0; i <= 8; i++) for (const yy of [r.y0, r.y1]) obst.push([r.x0 + (r.x1 - r.x0) * i / 8, yy, 8 / o.sc]);
  const fs = o.riverFs / o.sc;
  const out = []; const taken = [];
  for (const [name, text, count = 1] of list) {
    const rv = GEO.rivers.find((r) => r.name === name); if (!rv) continue;
    const cands = [];
    const tw = text.length * fs * .56;
    for (const l of rv.lines) {
      const n = l.length / 2; if (n < 2) continue;
      for (let i = 0; i < n; i++) {
        const j0 = Math.max(0, i - 3), j1 = Math.min(n - 1, i + 3);
        let ang = Math.atan2(l[2 * j1 + 1] - l[2 * j0 + 1], l[2 * j1] - l[2 * j0]);
        if (ang > Math.PI / 2) ang -= Math.PI; if (ang < -Math.PI / 2) ang += Math.PI;
        for (const side of [-1, 1]) {
          const off = side * (fs * .85), cx = l[2 * i] - Math.sin(ang) * off, cy = l[2 * i + 1] + Math.cos(ang) * off;
          if (o.bounds && (cx - tw / 2 < o.bounds[0] || cx + tw / 2 > o.bounds[2] || cy - fs < o.bounds[1] || cy + fs > o.bounds[3])) continue;
          let minD = 1e9;
          for (let k = -3; k <= 3; k++) {
            const px = cx + Math.cos(ang) * k * tw / 6, py = cy + Math.sin(ang) * k * tw / 6;
            for (const [x, y, rad] of obst) { const d = Math.hypot(x - px, y - py) - rad; if (d < minD) minD = d; }
          }
          cands.push({ cx, cy, ang, minD: minD - Math.abs(ang) * 4 / o.sc });
        }
      }
    }
    cands.sort((a, b) => b.minD - a.minD);
    const got = [];
    for (const c of cands) { if (got.length >= count) break; if (got.some((g) => Math.hypot(g.cx - c.cx, g.cy - c.cy) < 900) || taken.some((g) => Math.hypot(g.cx - c.cx, g.cy - c.cy) < 150)) continue; if (c.minD < -fs * .1) continue; got.push(c); }
    for (const g of got) { out.push({ ...g, text }); taken.push(g); }
  }
  return out;
}

export function routePath(L) {
  const p = L.B.pts.filter((q) => q.s >= L.s0 - 4 && q.s <= L.s0 + L.tot + 4);
  return 'M' + p.map((q) => f1(q.x) + ' ' + f1(q.y)).join('L');
}

// знак місця: кола, ромб, квадрат, трикутник — вози розрізняються й без кольору
export const MARK = [
  (r) => `<circle r="${f1(r)}"/>`,
  (r) => `<path d="M0 ${f1(-r * 1.25)} L${f1(r * 1.25)} 0 L0 ${f1(r * 1.25)} L${f1(-r * 1.25)} 0Z"/>`,
  (r) => `<rect x="${f1(-r * .95)}" y="${f1(-r * .95)}" width="${f1(r * 1.9)}" height="${f1(r * 1.9)}" rx="${f1(r * .2)}"/>`,
  (r) => `<path d="M0 ${f1(-r * 1.25)} L${f1(r * 1.25)} ${f1(r)} L${f1(-r * 1.25)} ${f1(r)}Z"/>`,
];
export function cartSvg(sl, col, o, seat = 0) {
  const T = o.T * 1.08, L = Math.max(sl.len - o.T * .04, o.T * .9), a = sl.ang * 57.2958, sc = o.sc;
  const ox = 1.5 / sc, oy = 2.2 / sc, x = sl.x, y = sl.y, d = shade(col, -.42), lit = shade(col, .22), hi = shade(col, .42);
  const wh = L * .28, ww = Math.min(L * .24, 9 / sc), wt = 3.6 / sc;
  const sw = 1 / sc;
  const wheels = [[-wh, -1], [wh, -1], [-wh, 1], [wh, 1]].map(([wx, s]) => `<rect x="${f1(wx - ww / 2)}" y="${f1(s < 0 ? -T / 2 - wt * .62 : T / 2 - wt * .38)}" width="${f1(ww)}" height="${f1(wt)}" rx="${f1(wt * .4)}" fill="#20130a"/>`).join('');
  return `<g transform="translate(${f1(x + ox)} ${f1(y + oy)}) rotate(${f1(a)})"><rect x="${f1(-L / 2)}" y="${f1(-T / 2)}" width="${f1(L)}" height="${f1(T)}" rx="${f1(T * .24)}" fill="${d}"/></g>
  <g transform="translate(${f1(x)} ${f1(y)}) rotate(${f1(a)})">${wheels}
    <rect x="${f1(-L / 2)}" y="${f1(-T / 2)}" width="${f1(L)}" height="${f1(T)}" rx="${f1(T * .24)}" fill="${col}" stroke="${INK}" stroke-width="${f1(sw)}"/>
    <rect x="${f1(-L / 2 + T * .16)}" y="${f1(-T * .3)}" width="${f1(L - T * .32)}" height="${f1(T * .6)}" rx="${f1(T * .2)}" fill="${lit}"/>
    <path d="M${f1(-L / 2 + T * .3)} ${f1(-T * .18)} H${f1(L / 2 - T * .3)}" stroke="${hi}" stroke-width="${f1(1.4 / sc)}" stroke-linecap="round"/>
    ${L > T * 1.9 ? `<path d="M${f1(-L * .3)} ${f1(-T * .28)} V${f1(T * .28)} M${f1(L * .3)} ${f1(-T * .28)} V${f1(T * .28)}" stroke="${d}" stroke-width="${f1(1.1 / sc)}" opacity=".7"/>` : ''}
    <g transform="rotate(${f1(-a)})" fill="#fff" stroke="${d}" stroke-width="${f1(.8 / sc)}" stroke-linejoin="round">${MARK[seat](T * .22)}</g>
  </g>`;
}

export function slotSvg(sl, color, o, selected) {
  const T = o.T, a = sl.ang * 57.2958, sc = o.sc, any = color === 'any';
  const fill = any ? '#d9cfb6' : TYPES[color].slot;
  const L = sl.len, g = Math.min(T * .82, L * .85);
  let s = `<g transform="translate(${f1(sl.x)} ${f1(sl.y)}) rotate(${f1(a)})">
    <rect x="${f1(-L / 2)}" y="${f1(-T / 2)}" width="${f1(L)}" height="${f1(T)}" rx="${f1(T * .2)}" fill="${fill}" stroke="${selected ? '#1a0d05' : INK}" stroke-width="${f1((selected ? 1.5 : 1.1) / sc)}"/>
    <path d="M${f1(-L / 2 + T * .18)} ${f1(-T / 2 + 1.6 / sc)} H${f1(L / 2 - T * .18)}" stroke="#fff" stroke-width="${f1(1 / sc)}" opacity="${any ? .75 : .42}" stroke-linecap="round"/>`;
  if (!any) {
    // гліф лишається прямим до екрана, щоб не лежав догори дриґом
    s += `<g transform="rotate(${f1(-a)})"><use href="#gl-${color}" x="${f1(-g / 2)}" y="${f1(-g / 2)}" width="${f1(g)}" height="${f1(g)}"/></g>`;
  } else {
    s += `<rect x="${f1(-L / 2 + T * .2)}" y="${f1(-T * .24)}" width="${f1(L - T * .4)}" height="${f1(T * .48)}" rx="${f1(T * .14)}" fill="none" stroke="#8a7a58" stroke-width="${f1(1 / sc)}" stroke-dasharray="${f1(2.4 / sc)} ${f1(2 / sc)}"/>`;
  }
  return s + '</g>';
}
export { bordersD };
export const claimsByRoute = (id) => CLAIM[id];
export { cityOf };

// ── підказки до замовлень: найкоротший шлях з урахуванням уже своїх і чужих шляхів ──
export function orderHint(a, b) {
  const adj = {};
  for (const R of FX.routes) {
    const owner = CLAIM[R.id];
    if (owner != null && owner !== 0) continue;
    const w = owner === 0 ? 0 : R.len;
    (adj[R.a] = adj[R.a] || []).push([R.b, w, R]); (adj[R.b] = adj[R.b] || []).push([R.a, w, R]);
  }
  const D = { [a]: 0 }, P = {}, Q = new Set(FX.cities.map((c) => c.name));
  while (Q.size) {
    let u = null, best = Infinity; for (const q of Q) if ((D[q] ?? Infinity) < best) { best = D[q]; u = q; }
    if (u == null) break; Q.delete(u);
    for (const [v, w, R] of adj[u] || []) if (best + w < (D[v] ?? Infinity)) { D[v] = best + w; P[v] = [u, R]; }
  }
  const legs = []; let cur = b;
  while (cur !== a && P[cur]) { const [u, R] = P[cur]; legs.unshift({ from: u, to: cur, R, mine: CLAIM[R.id] === 0 }); cur = u; }
  return { legs, leftWagons: legs.filter((l) => !l.mine).reduce((s, l) => s + l.R.len, 0), doneWagons: legs.filter((l) => l.mine).reduce((s, l) => s + l.R.len, 0), leftRoutes: legs.filter((l) => !l.mine).length };
}
