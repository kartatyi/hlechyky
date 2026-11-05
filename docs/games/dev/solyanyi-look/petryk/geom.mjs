// Геометрія: регіони/річки з geo-map.json, моря з проєкції, криві шляхів, вагон-слоти, розстановка підписів.
import { readFileSync } from 'node:fs';
export const SP = '/private/tmp/claude-501/-Users-admin-olimp-glechyk/a2ec8d08-6007-4765-b570-a92fbb040f3d/scratchpad';
export const geo = JSON.parse(readFileSync('/Users/admin/olimp/glechyk/web/games/geo-map.json', 'utf8'));
export const fx = JSON.parse(readFileSync(SP + '/sol/fixture.json', 'utf8'));
const r1 = (v) => Math.round(v);
export const f1 = (v) => Math.round(v * 10) / 10;

// ── регіони ──────────────────────────────────────────────────
const refs = new Uint8Array(geo.arcs.length);
for (const r of geo.regions) for (const ring of r.rings) for (const id of ring) refs[Math.abs(id) - 1]++;
export const regions = geo.regions.map((r) => {
  let d = ''; const bb = [1e9, 1e9, -1e9, -1e9]; let area = 0, cx = 0, cy = 0;
  for (const ring of r.rings) {
    let first = true; const pts = [];
    for (const id of ring) {
      const a = geo.arcs[Math.abs(id) - 1]; const n = a.length / 2;
      for (let k = first ? 0 : 1; k < n; k++) {
        const i = id > 0 ? k : n - 1 - k; const X = a[2 * i], Y = a[2 * i + 1];
        d += (first ? 'M' : 'L') + X + ' ' + Y; first = false; pts.push([X, Y]);
        bb[0] = Math.min(bb[0], X); bb[1] = Math.min(bb[1], Y); bb[2] = Math.max(bb[2], X); bb[3] = Math.max(bb[3], Y);
      }
    }
    d += 'Z';
    for (let i = 0; i < pts.length; i++) { const [x0, y0] = pts[i], [x1, y1] = pts[(i + 1) % pts.length]; const c = x0 * y1 - x1 * y0; area += c; cx += (x0 + x1) * c; cy += (y0 + y1) * c; }
  }
  return { ...r, d, bb, cx: cx / (3 * area), cy: cy / (3 * area) };
});
export let outlineD = '', bordersD = '';
geo.arcs.forEach((a, i) => { let d = 'M' + a[0] + ' ' + a[1]; for (let k = 2; k < a.length; k += 2) d += 'L' + a[k] + ' ' + a[k + 1]; if (refs[i] === 1) outlineD += d; else bordersD += d; });
function chain(lines) {
  if (lines.length < 2) return lines;
  const left = lines.map((l) => l.slice()); left.sort((a, b) => Math.min(a[1], a[a.length - 1]) - Math.min(b[1], b[b.length - 1]));
  let cur = left.shift(); if (cur[1] > cur[cur.length - 1]) { const r = []; for (let i = cur.length - 2; i >= 0; i -= 2) r.push(cur[i], cur[i + 1]); cur = r; }
  const out = cur.slice();
  while (left.length) {
    const ex = out[out.length - 2], ey = out[out.length - 1]; let bi = 0, bd = 1e9, rev = false;
    left.forEach((l, i) => { const d0 = Math.hypot(l[0] - ex, l[1] - ey), d1 = Math.hypot(l[l.length - 2] - ex, l[l.length - 1] - ey); if (d0 < bd) { bd = d0; bi = i; rev = false; } if (d1 < bd) { bd = d1; bi = i; rev = true; } });
    let l = left.splice(bi, 1)[0]; if (rev) { const r = []; for (let i = l.length - 2; i >= 0; i -= 2) r.push(l[i], l[i + 1]); l = r; }
    out.push(...l);
  }
  return [out];
}
export const rivers = geo.rivers.map((rv) => {
  let d = ''; let best = null, bl = 0;
  for (const l of (rv.big ? chain(rv.lines) : rv.lines)) { d += 'M' + l[0] + ' ' + l[1]; let len = 0; for (let k = 2; k < l.length; k += 2) { d += 'L' + l[k] + ' ' + l[k + 1]; len += Math.hypot(l[k] - l[k - 2], l[k + 1] - l[k - 1]); } if (len > bl) { bl = len; best = l; } }
  return { name: rv.name, big: rv.big, d, best };
});
// суміжність областей
export function adjacency() {
  const byArc = new Map(); geo.regions.forEach((r, i) => { for (const ring of r.rings) for (const id of ring) { const k = Math.abs(id); (byArc.get(k) || byArc.set(k, []).get(k)).push(i); } });
  const adj = geo.regions.map(() => new Set()); for (const l of byArc.values()) if (l.length > 1) for (const a of l) for (const b of l) if (a !== b) adj[a].add(b);
  return adj;
}

// ── проєкція для узбереж чужих морів ─────────────────────────
const P = geo.proj, rad = (d) => d * Math.PI / 180;
const N = (Math.sin(rad(P.phi1)) + Math.sin(rad(P.phi2))) / 2;
const F = Math.cos(rad(P.phi1)) * Math.tan(Math.PI / 4 + rad(P.phi1) / 2) ** N / N;
const rho = (ph) => F / Math.tan(Math.PI / 4 + rad(ph) / 2) ** N;
export function prj(la, lo) { const th = N * rad(lo - P.lam0), R = rho(la), R0 = rho(P.phi0); return [(R * Math.sin(th) - P.x0) * P.k, (P.y0 - (R0 - R * Math.cos(th))) * P.k]; }
const poly = (ll) => { const p = ll.map(([a, b]) => prj(a, b)); const n = p.length; let d = ''; for (let i = 0; i < n; i++) { const a = p[i], b = p[(i + 1) % n], mx = (a[0] + b[0]) / 2, my = (a[1] + b[1]) / 2; d += (i ? 'Q' + r1(a[0]) + ' ' + r1(a[1]) + ' ' : 'M') + r1(mx) + ' ' + r1(my); } const a = p[0], b = p[1]; return d + 'Q' + r1(a[0]) + ' ' + r1(a[1]) + ' ' + r1((a[0] + b[0]) / 2) + ' ' + r1((a[1] + b[1]) / 2) + 'Z'; };
export const blackSea = poly([[45.6, 29.75], [45.17, 29.75], [44.75, 29.0], [44.2, 28.68], [43.4, 28.5], [42.5, 27.7], [41.6, 28.2], [41.3, 31], [41.9, 34], [41.9, 37], [41.2, 41], [42.5, 41.5], [43.6, 39.7], [44.1, 38.9], [44.7, 37.8], [44.92, 37.3], [45.1, 36.75], [45.15, 36.5], [45.32, 36.2], [45.5, 35], [45.9, 34.2], [46.3, 33.4], [46.75, 32.3], [46.75, 31.2], [46.35, 30.3]]);
export const azovSea = poly([[45.42, 36.72], [45.45, 36.1], [45.6, 35.3], [46.2, 34.9], [46.65, 35.5], [46.95, 36.8], [47.3, 37.9], [47.32, 38.3], [47.22, 38.92], [47.27, 39.3], [47.05, 39.25], [46.95, 38.9], [46.72, 38.2], [46.42, 37.95], [46.05, 38.15], [45.75, 37.75], [45.35, 37.38]]);

// ── шляхи ────────────────────────────────────────────────────
const byName = Object.fromEntries(fx.cities.map((c) => [c.name, c]));
export const city = (n) => byName[n];
const CLEAR = 44;       // від центру міста до першого слота
function bez(a, c, b, t) { const u = 1 - t; return [u * u * a[0] + 2 * u * t * c[0] + t * t * b[0], u * u * a[1] + 2 * u * t * c[1] + t * t * b[1]]; }
function sample(a, c, b, n) { const o = []; for (let i = 0; i <= n; i++) o.push(bez(a, c, b, i / n)); return o; }
function ctrl(R, bow) { const a = [R.A.x, R.A.y], b = [R.B.x, R.B.y]; const dx = b[0] - a[0], dy = b[1] - a[1]; const L = Math.hypot(dx, dy); return [(a[0] + b[0]) / 2 - dy / L * bow * L, (a[1] + b[1]) / 2 + dx / L * bow * L]; }
function segX(p, q, r, s) { const d = (q[0] - p[0]) * (s[1] - r[1]) - (q[1] - p[1]) * (s[0] - r[0]); if (!d) return false; const t = ((r[0] - p[0]) * (s[1] - r[1]) - (r[1] - p[1]) * (s[0] - r[0])) / d, u = ((r[0] - p[0]) * (q[1] - p[1]) - (r[1] - p[1]) * (q[0] - p[0])) / d; return t > 0 && t < 1 && u > 0 && u < 1; }
const CAND = [0.04, -0.04, 0.07, -0.07, 0.1, -0.1, 0.13, -0.13, 0.17, -0.17];

export function solveRoutes(forced = {}) {
  const R = fx.routes.map((r) => ({ ...r, A: city(r.a), B: city(r.b), bow: forced[r.id] ?? 0.06 }));
  const pl = (X, bow) => sample([X.A.x, X.A.y], ctrl(X, bow), [X.B.x, X.B.y], 18);
  R.forEach((X) => { X.pl = pl(X, X.bow); });
  const cost = (X, bow) => {
    const p = pl(X, bow); let c = Math.abs(bow) * 55;
    for (const C of fx.cities) { if (C === X.A || C === X.B) continue; for (const q of p) { const d = Math.hypot(q[0] - C.x, q[1] - C.y); if (d < 90) c += (90 - d) * 0.35; } }
    for (const Y of R) {
      if (Y === X) continue;
      const shareA = X.A === Y.A || X.A === Y.B, shareB = X.B === Y.A || X.B === Y.B;
      for (let i = 0; i < 18; i++) for (let j = 0; j < 18; j++) {
        if (segX(p[i], p[i + 1], Y.pl[j], Y.pl[j + 1])) c += 40;
      }
      // близькість до чужих шляхів (за межами спільного міста)
      for (let i = 1; i < 18; i++) {
        const q = p[i]; let dm = 1e9;
        for (let j = 1; j < 18; j++) { const d = Math.hypot(q[0] - Y.pl[j][0], q[1] - Y.pl[j][1]); if (d < dm) dm = d; }
        const dA = Math.hypot(q[0] - X.A.x, q[1] - X.A.y), dB = Math.hypot(q[0] - X.B.x, q[1] - X.B.y);
        if ((shareA && dA < 130) || (shareB && dB < 130)) { if (dm < 34) c += (34 - dm) * 0.5; continue; }
        if (dm < 56) c += (56 - dm) * 0.14;
      }
    }
    return c;
  };
  for (let pass = 0; pass < 6; pass++) for (const X of R) {
    if (forced[X.id] != null) continue;
    let best = X.bow, bc = cost(X, X.bow);
    for (const b of CAND) { const c = cost(X, b); if (c < bc - 1e-6) { bc = c; best = b; } }
    X.bow = best; X.pl = pl(X, best);
  }
  // слоти
  for (const X of R) {
    const a = [X.A.x, X.A.y], b = [X.B.x, X.B.y], c = ctrl(X, X.bow);
    const fine = sample(a, c, b, 120); const ls = [0]; for (let i = 1; i < fine.length; i++) ls.push(ls[i - 1] + Math.hypot(fine[i][0] - fine[i - 1][0], fine[i][1] - fine[i - 1][1]));
    const total = ls[ls.length - 1];
    const at = (s) => { let i = 1; while (i < ls.length - 1 && ls[i] < s) i++; const t = (s - ls[i - 1]) / (ls[i] - ls[i - 1] || 1); return [fine[i - 1][0] + (fine[i][0] - fine[i - 1][0]) * t, fine[i - 1][1] + (fine[i][1] - fine[i - 1][1]) * t, Math.atan2(fine[i][1] - fine[i - 1][1], fine[i][0] - fine[i - 1][0])]; };
    const clr = Math.min(CLEAR, total * 0.28);
    const use = total - 2 * clr, sl = use / X.len;
    X.total = total; X.at = at; X.clr = clr; X.d = 'M' + r1(a[0]) + ' ' + r1(a[1]) + 'Q' + r1(c[0]) + ' ' + r1(c[1]) + ' ' + r1(b[0]) + ' ' + r1(b[1]);
    X.slots = []; for (let i = 0; i < X.len; i++) { const [x, y, ang] = at(clr + sl * (i + 0.5)); X.slots.push({ x, y, ang, l: sl }); }
    X.mid = at(total / 2);
  }
  return R;
}
export function crossings(R) { let n = 0; const L = []; for (let i = 0; i < R.length; i++) for (let j = i + 1; j < R.length; j++) { let hit = false; for (let a = 0; a < 18 && !hit; a++) for (let b = 0; b < 18 && !hit; b++) if (segX(R[i].pl[a], R[i].pl[a + 1], R[j].pl[b], R[j].pl[b + 1])) hit = true; if (hit) { n++; L.push(R[i].a + '-' + R[i].b + ' x ' + R[j].a + '-' + R[j].b); } } return L; }

// ── розстановка підписів ─────────────────────────────────────
export function placeLabels(R, fu, opts = {}) {
  const obs = []; for (const X of R) for (const s of X.slots) obs.push([s.x, s.y, 30]);
  for (const C of fx.cities) obs.push([C.x, C.y, 40]);
  const placed = [];
  const out = {};
  const lines = (n) => (opts.lines && opts.lines[n]) || [n.replace(/'/g, '’')];
  const size = (n) => { const big = opts.big && opts.big.includes(n); const f = fu * (big ? 1.18 : 1); const ls = lines(n); return { f, ls, w: Math.max(...ls.map((s) => s.length)) * f * 0.585, h: ls.length * f * 1.08 }; };
  const order = [...fx.cities].sort((a, b) => b.name.length - a.name.length);
  const rectCost = (x0, y0, x1, y1) => {
    let c = 0;
    for (const [ox, oy, rr] of obs) { const dx = Math.max(x0 - ox, 0, ox - x1), dy = Math.max(y0 - oy, 0, oy - y1); const d = Math.hypot(dx, dy); if (d < rr) c += (rr - d) * 1.3; }
    for (const p of placed) { if (x0 < p[2] && x1 > p[0] && y0 < p[3] && y1 > p[1]) c += 600; }
    if (x0 < 8) c += 500; if (x1 > 3990) c += 500;
    return c;
  };
  for (let pass = 0; pass < 3; pass++) {
    placed.length = 0; if (pass) for (const n in out) placed.push(out[n].rect);
    for (const C of order) {
      if (pass) { const i = placed.indexOf(out[C.name].rect); if (i >= 0) placed.splice(i, 1); }
      const S = size(C.name), m = 42; let best = null, bc = 1e12;
      const man = opts.manual && opts.manual[C.name];
      const cand = man ? [man] : [['r', 0], ['l', 0], ['t', 0], ['b', 0], ['r', -.6], ['r', .6], ['l', -.6], ['l', .6], ['t', -.5], ['t', .5], ['b', -.5], ['b', .5]];
      for (const [side, k] of cand) {
        let x0, y0;
        if (side === 'r') { x0 = C.x + m; y0 = C.y - S.h / 2 + k * S.h; }
        else if (side === 'l') { x0 = C.x - m - S.w; y0 = C.y - S.h / 2 + k * S.h; }
        else if (side === 't') { x0 = C.x - S.w / 2 + k * S.w; y0 = C.y - m - S.h; }
        else if (side === 'b') { x0 = C.x - S.w / 2 + k * S.w; y0 = C.y + m; }
        else { x0 = C.x + side; y0 = C.y + k; }
        const c = rectCost(x0, y0, x0 + S.w, y0 + S.h) + (side === 'r' ? 0 : side === 'l' ? 2 : 5) + Math.abs(k || 0) * 4;
        if (c < bc) { bc = c; best = { x: x0, y: y0, w: S.w, h: S.h, f: S.f, ls: S.ls, rect: [x0, y0, x0 + S.w, y0 + S.h], cost: c }; }
      }
      out[C.name] = best; placed.push(best.rect);
    }
  }
  return out;
}

// ── залишок замовлень (Дейкстра: своє — 0, чуже — заборонено) ─
export function orderPath(a, b, R, claimed, me = 0) {
  const owner = {}; for (const p in claimed) for (const id of claimed[p]) owner[id] = +p;
  const dist = { [a]: 0 }, prev = {}; const Q = new Set(fx.cities.map((c) => c.name));
  while (Q.size) {
    let u = null, du = 1e9; for (const n of Q) if ((dist[n] ?? 1e9) < du) { du = dist[n]; u = n; }
    if (u == null) break; Q.delete(u);
    for (const X of R) {
      if (X.a !== u && X.b !== u) continue; const v = X.a === u ? X.b : X.a;
      if (owner[X.id] != null && owner[X.id] !== me) continue;
      const w = owner[X.id] === me ? 0 : X.len; if (du + w < (dist[v] ?? 1e9)) { dist[v] = du + w; prev[v] = [u, X]; }
    }
  }
  const path = []; let n = b; while (prev[n]) { path.unshift(prev[n][1]); n = prev[n][0]; }
  return { left: dist[b], own: path.filter((X) => owner[X.id] === me).reduce((s, X) => s + X.len, 0), todo: path.filter((X) => owner[X.id] == null), path };
}
