// «Соляний шлях» — 3D-діорама мапи. const sc = await createScene(host, { look, quality, onPick, reducedMotion });
//   sc.setClaims({ '0': [routeId…], … }); sc.setSelected(routeId|null); sc.resetView(); sc.resize(); sc.stats(); sc.dispose();
// Будова (дешево й статично):
//   земля + поле — один меш (нерегулярний Delaunay, колір із картини-канвасу); море — площина + мерехтіння шейдером;
//   річки — стрічка в прорізаному руслі; плашки шляхів — один злитий меш з атласом кольорів і значків;
//   поселення й декор — один злитий меш із кольором вершин; дерева, вози, воли, дим — InstancedMesh;
//   підписи — CSS-шар (завжди чіткі, не залежать від DPR і нахилу).
// Рендер на вимогу; живе лише море/річки/дим/сяйво — таймером 30/15 fps, без руху JS-об'єктів.

import * as THREE from './vendor/three-0.186.1/three.module.js';
import { LOOKS } from './looks.js';
import { W, H, U, PAD, MAT, LV, wx, wz, rng, smooth, buildGeo, buildRelief, buildMesh, heightAt } from './terrain.js';
import { paintMap, paintAtlas, paintWood, ATLAS, forestAt, CANVAS_DOMAIN } from './paint.js';
import * as M from './models.js';

const D2R = Math.PI / 180;
const FOV = 20, PW = 46;  // PW — ширина плашки в одиницях сітки
const clamp = (x, a, b) => Math.max(a, Math.min(b, x));

// ---------------------------------------------------------------------------------------------------------- шляхи
function layoutRoutes(geo, fx, cities) {
  const byName = Object.fromEntries(cities.map((c) => [c.name, c]));
  const routes = fx.routes.map((r) => {
    const A = byName[r.a], B = byName[r.b], L = Math.hypot(B.x - A.x, B.y - A.y);
    return { ...r, A, B, L, nx: -(B.y - A.y) / L, ny: (B.x - A.x) / L, bow: 0.05 * L * (r.id % 2 ? 1 : -1) };
  });
  const curve = (r, bow, n) => { const o = []; for (let i = 0; i <= n; i++) { const t = i / n, k = 2 * t * (1 - t) * 2 * bow; o.push([r.A.x + (r.B.x - r.A.x) * t + r.nx * k, r.A.y + (r.B.y - r.A.y) * t + r.ny * k]); } return o; };
  const mids = (r, bow) => { const n = Math.max(6, Math.ceil(r.L / 34)), P = curve(r, bow, n), hubA = r.A.r + 44, hubB = r.B.r + 44; return P.filter((p) => Math.hypot(p[0] - r.A.x, p[1] - r.A.y) > hubA && Math.hypot(p[0] - r.B.x, p[1] - r.B.y) > hubB); };
  for (const r of routes) r.mid = mids(r, r.bow);
  const CAND = [0.03, -0.03, 0.06, -0.06, 0.09, -0.09, 0.12, -0.12, 0.16, -0.16, 0.2, -0.2, 0.25, -0.25];
  const TH = PW + 16;
  for (let pass = 0; pass < 5; pass++) for (const r of routes) {
    const near = routes.filter((o) => o !== r && Math.hypot((o.A.x + o.B.x - r.A.x - r.B.x) / 2, (o.A.y + o.B.y - r.A.y - r.B.y) / 2) < (o.L + r.L) * 0.62 + 100);
    const nc = cities.filter((c) => c !== r.A && c !== r.B && Math.hypot((r.A.x + r.B.x) / 2 - c.x, (r.A.y + r.B.y) / 2 - c.y) < r.L * 0.62 + 160);
    let best = r.bow, bs = Infinity;
    for (const f of CAND) {
      const bow = f * r.L, m = mids(r, bow);
      let sc = Math.abs(Math.abs(f) - 0.06) * 60 + Math.max(0, Math.abs(f) - 0.12) * 200;
      for (const p of m) {
        for (const o of near) for (const q of o.mid) { const dx = p[0] - q[0], dy = p[1] - q[1], d2 = dx * dx + dy * dy; if (d2 < TH * TH) { const d = Math.sqrt(d2); sc += (TH - d) * (TH - d) * 0.25; } }
        for (const c of nc) { const d = Math.hypot(p[0] - c.x, p[1] - c.y), th = c.r + PW / 2 + 14; if (d < th) sc += (th - d) * (th - d) * 0.6; }
        if (geo.maskAt(p[0], p[1]) === 2) sc += 90;
      }
      if (sc < bs) { bs = sc; best = bow; }
    }
    r.bow = best; r.mid = mids(r, best);
  }
  for (const r of routes) {
    r.samples = curve(r, r.bow, 64);
    const acc = [0]; for (let i = 1; i < r.samples.length; i++) acc.push(acc[i - 1] + Math.hypot(r.samples[i][0] - r.samples[i - 1][0], r.samples[i][1] - r.samples[i - 1][1]));
    r.acc = acc; r.len_ = acc[acc.length - 1]; r.sA = r.A.r + 7; r.sB = r.B.r + 7;
  }
  // вузли: шляхи розходяться під малим кутом — перша плашка відступає від міста рівно настільки, щоб не налізти на сусідню
  for (const c of cities) {
    const ends = [];
    for (const r of routes) {
      if (r.A === c) { const q = atS(r, c.r + 70); ends.push({ r, side: 'sA', a: Math.atan2(q.y - c.y, q.x - c.x) }); }
      if (r.B === c) { const q = atS(r, r.len_ - c.r - 70); ends.push({ r, side: 'sB', a: Math.atan2(q.y - c.y, q.x - c.x) }); }
    }
    ends.sort((p, q) => p.a - q.a);
    ends.forEach((e, i) => {
      if (ends.length < 2) return;
      const pv = ends[(i - 1 + ends.length) % ends.length], nx = ends[(i + 1) % ends.length];
      const da = (x, y) => { let d = Math.abs(x - y) % (Math.PI * 2); return d > Math.PI ? Math.PI * 2 - d : d; };
      const th = Math.min(da(e.a, pv.a), da(e.a, nx.a));
      const need = (PW / 2 + 5) / Math.sin(Math.min(th, 2.6) / 2) ;
      const maxS = Math.min(c.r + 62, e.r.len_ * 0.3);
      e.r[e.side] = clamp(need, c.r + 7, maxS);
    });
  }
  // добивання: якщо плашки ще зачіпаються біля вузла — відсуваємо кінці шляхів на кілька кроків
  const rectOf = (r) => { const gap = 9, s0 = r.sA, s1 = r.len_ - r.sB, N = r.len, pl = (s1 - s0 - gap * (N - 1)) / N, out = []; for (let i = 0; i < N; i++) { const m = s0 + i * (pl + gap) + pl / 2, c = atS(r, m); out.push({ x: c.x, y: c.y, tx: c.tx, ty: c.ty, l: pl, i, N, r }); } return out; };
  const hit = (a, b) => { const hw = PW / 2 + 2; const ax = [[a.tx, a.ty], [-a.ty, a.tx], [b.tx, b.ty], [-b.ty, b.tx]]; const dx = b.x - a.x, dy = b.y - a.y; const ea = [a.l / 2, hw], eb = [b.l / 2, hw]; for (const n of ax) { const d = Math.abs(dx * n[0] + dy * n[1]); const pa = ea[0] * Math.abs(a.tx * n[0] + a.ty * n[1]) + ea[1] * Math.abs(-a.ty * n[0] + a.tx * n[1]), pb = eb[0] * Math.abs(b.tx * n[0] + b.ty * n[1]) + eb[1] * Math.abs(-b.ty * n[0] + b.tx * n[1]); if (d > pa + pb) return false; } return true; };
  for (let pass = 0; pass < 8; pass++) {
    const R = routes.flatMap(rectOf); let any = false;
    for (let i = 0; i < R.length; i++) for (let j = i + 1; j < R.length; j++) {
      const a = R[i], b = R[j]; if (a.r === b.r || Math.hypot(a.x - b.x, a.y - b.y) > 230 || !hit(a, b)) continue; any = true;
      for (const q of [a, b]) { if (q.i === 0) q.r.sA = Math.min(q.r.sA + 10, q.r.len_ * 0.42); if (q.i === q.N - 1) q.r.sB = Math.min(q.r.sB + 10, q.r.len_ * 0.42); }
    }
    if (!any) break;
  }
  return routes;
}
function atS(r, s) {
  const a = r.acc; let lo = 0, hi = a.length - 1; while (hi - lo > 1) { const m = (lo + hi) >> 1; if (a[m] < s) lo = m; else hi = m; }
  const k = (s - a[lo]) / (a[hi] - a[lo] || 1), p = r.samples[lo], q = r.samples[hi], dx = q[0] - p[0], dy = q[1] - p[1], l = Math.hypot(dx, dy) || 1;
  return { x: p[0] + dx * k, y: p[1] + dy * k, tx: dx / l, ty: dy / l, nx: -dy / l, ny: dx / l };
}
/** Плашки шляху: обчислені кінці, висоти й нахил. */
function layoutPlates(geo, r) {
  const gap = 9, s0 = r.sA, s1 = r.len_ - r.sB, N = r.len, pl = (s1 - s0 - gap * (N - 1)) / N, plates = [];
  for (let i = 0; i < N; i++) {
    const a = s0 + i * (pl + gap), b = a + pl, m = (a + b) / 2, c = atS(r, m);
    const samp = (s) => { const q = atS(r, s); let h = -1; for (const t of [-PW / 2, 0, PW / 2]) h = Math.max(h, heightAt(geo, q.x + q.nx * t, q.y + q.ny * t)); return h; };
    const y0 = samp(a) + 0.003, y1 = samp(b) + 0.003, ym = samp(m) + 0.003, e = Math.max(0, ym - (y0 + y1) / 2);
    plates.push({ route: r, i, a, b, m, len: pl, x: c.x, y: c.y, tx: c.tx, ty: c.ty, y0: y0 + e, y1: y1 + e });
  }
  return plates;
}

/** Силует плашки — третя ознака товару (крім кольору й значка), видно навіть у дрібному масштабі. */
const SHAPE = { red: 'chev', pink: 'chev', yellow: 'pill', white: 'pill', orange: 'bone', black: 'bone', green: 'rect', blue: 'rect', any: 'rect' };
function writePlate(out, r, pl, look, hi, lift = 0, atlasRow, seg) {
  const shape = SHAPE[r.color] || 'rect';
  const H0 = 0.013, inset = 4, rc = shape === 'pill' ? Math.min(PW / 2 - 0.5, pl.len / 2 - 0.5) : shape === 'rect' ? 7 : 6;
  const ring = [];
  const nS = hi ? 4 : 2, nC = shape === 'pill' ? (hi ? 5 : 3) : hi ? 3 : 1;
  const hw = PW / 2, hl = pl.len / 2, kS = Math.min(13, hl * 0.3);
  // контур заокругленого прямокутника в (s,t): кут — дуга, довгі боки — кілька вершин, щоб плашка гнулася за шляхом
  const edge = [];
  const arc = (cs, ct, a0) => { for (let i = 0; i <= nC; i++) { const a = a0 + i / nC * Math.PI / 2; edge.push([cs + Math.cos(a) * rc, ct + Math.sin(a) * rc]); } };
  const sL = hl - rc, tL = hw - rc;
  arc(sL, tL, 0);
  arc(-sL, tL, Math.PI / 2);
  for (let i = 0; i < 0; i++);
  arc(-sL, -tL, Math.PI);
  arc(sL, -tL, Math.PI * 1.5);
  // додаткові вершини на довгих боках
  const full = [];
  for (let i = 0; i < edge.length; i++) {
    const p = edge[i], q = edge[(i + 1) % edge.length]; full.push(p);
    const l = Math.abs(q[0] - p[0]);
    if (l > hl * 0.8) for (let k = 1; k < nS; k++) full.push([p[0] + (q[0] - p[0]) * k / nS, p[1] + (q[1] - p[1]) * k / nS]);
    else if (Math.abs(q[1] - p[1]) > hw * 0.5) full.push([(p[0] + q[0]) / 2, (p[1] + q[1]) / 2]);
  }
  // кінці: «ялинка» (плашки вкладаються одна в одну) чи увігнута «кістка»
  if (shape === 'chev' || shape === 'bone') for (const e of full) {
    const f = Math.max(0, 1 - Math.abs(e[1]) / hw), w = Math.abs(e[0]) / hl;
    const k = kS * f * smooth(0.2, 1, w);
    e[0] += shape === 'chev' ? k : -Math.sign(e[0]) * k * 0.5;
  }
  const place = (s, t, y) => { const c = atS(r, pl.m + s), k = (s + hl) / (2 * hl), yy = pl.y0 + (pl.y1 - pl.y0) * k + y + lift; return [wx(c.x + c.nx * t), yy, wz(c.y + c.ny * t)]; };
  const cellH = 1 / atlasRow.rows, v0 = 1 - (atlasRow.i + 1) * cellH;
  const uvTop = (s, t) => [clamp(0.5 + s / (PW * 4), 0.04, 0.96), v0 + cellH * (0.5 + t / PW * 0.96)];
  const solid = [0.015, 1 - (atlasRow.rows - 1 + 0.5) * cellH];       // білий ряд
  const white = [1, 1, 1];
  const rim = new THREE.Color(look.plateRim), side = new THREE.Color(look.plateSide);
  const push = (p, n, uv, col) => { out.pos.push(p[0], p[1], p[2]); out.nor.push(n[0], n[1], n[2]); out.uv.push(uv[0], uv[1]); out.col.push(col[0], col[1], col[2]); };
  const fnorm = (a, b, c) => { const ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2]; let X = uy * vz - uz * vy, Y = uz * vx - ux * vz, Z = ux * vy - uy * vx; const l = Math.hypot(X, Y, Z) || 1; if (Y < -1e-6) return null; return [X / l, Y / l, Z / l]; };
  const shrink = (e, d) => { const s = e[0], t = e[1]; const fs = (Math.abs(s) >= hl - 1e-3 || Math.abs(t) > tL) ? 1 : 1; const ls = Math.max(1e-3, hl), lt = hw; return [s * (1 - d / ls), t * (1 - d / lt)]; };
  const TOP = full.map((e) => { const q = shrink(e, inset); return { s: q[0], t: q[1], p: place(q[0], q[1], H0) }; });
  const MID = full.map((e) => ({ s: e[0], t: e[1], p: place(e[0], e[1], H0 - 0.0065) }));
  const BOT = full.map((e) => ({ s: e[0], t: e[1], p: place(e[0], e[1], -0.03) }));
  // верх: віяло
  const cp = place(0, 0, H0), N = [0, 1, 0];
  for (let i = 0; i < TOP.length; i++) {
    const a = TOP[i], b = TOP[(i + 1) % TOP.length];
    // проти годинникової згори: порядок центр → b → a (s = +X, t = нормаль)
    const tn = triOriented(cp, a.p, b.p);
    const uva = uvTop(a.s, a.t), uvb = uvTop(b.s, b.t), uvc = uvTop(0, 0);
    if (tn.flip) { push(cp, N, uvc, white); push(b.p, N, uvb, white); push(a.p, N, uva, white); } else { push(cp, N, uvc, white); push(a.p, N, uva, white); push(b.p, N, uvb, white); }
  }
  const quad = (A, B, C, D, col) => {
    // A,B — верхня пара; C,D — нижня; нормаль назовні вгору
    const Cn = [col.r, col.g, col.b];
    let n = triNorm(A, B, C); const cen = [(A[0] + B[0] + C[0] + D[0]) / 4, (A[2] + B[2] + C[2] + D[2]) / 4];
    const out_ = [cen[0] - wx(pl.x), cen[1] - wz(pl.y)];
    if (n[0] * out_[0] + n[2] * out_[1] < 0) { n = [-n[0], -n[1], -n[2]]; push(A, n, solid, Cn); push(C, n, solid, Cn); push(B, n, solid, Cn); push(B, n, solid, Cn); push(C, n, solid, Cn); push(D, n, solid, Cn); }
    else { push(A, n, solid, Cn); push(B, n, solid, Cn); push(C, n, solid, Cn); push(B, n, solid, Cn); push(D, n, solid, Cn); push(C, n, solid, Cn); }
  };
  for (let i = 0; i < TOP.length; i++) {
    const j = (i + 1) % TOP.length;
    quad(TOP[i].p, TOP[j].p, MID[i].p, MID[j].p, rim);
    quad(MID[i].p, MID[j].p, BOT[i].p, BOT[j].p, side);
  }
}
function triNorm(a, b, c) { const ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2]; const X = uy * vz - uz * vy, Y = uz * vx - ux * vz, Z = ux * vy - uy * vx, l = Math.hypot(X, Y, Z) || 1; return [X / l, Y / l, Z / l]; }
function triOriented(a, b, c) { const n = triNorm(a, b, c); return { flip: n[1] < 0 }; }
function bufGeom(o) {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(o.pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(o.nor, 3));
  if (o.uv) g.setAttribute('uv', new THREE.Float32BufferAttribute(o.uv, 2));
  if (o.col) g.setAttribute('color', new THREE.Float32BufferAttribute(o.col, 3));
  return g;
}

// ---------------------------------------------------------------------------------------------------------- спільний шматок шейдера: м'яке мерехтіння (лише синуси)
const WAVE_GLSL = `
float waveA(vec2 p, float t){
  p += 0.22*vec2(sin(p.y*1.9+t*0.31), cos(p.x*1.6-t*0.27));
  float a = sin(p.x*3.3 + p.y*1.2 + t*0.62)*0.5+0.5;
  float b = sin(p.x*-2.1 + p.y*4.4 - t*0.47)*0.5+0.5;
  float c = sin((p.x+p.y)*1.1 + t*0.2)*0.5+0.5;
  return a*b*(0.55+0.45*c);
}
`;

export async function createScene(host, opts = {}) {
  const lookName = LOOKS[opts.look] ? opts.look : 'board', look = LOOKS[lookName];
  const hi = opts.quality !== 'low';
  const reduced = !!opts.reducedMotion;
  const T0 = performance.now(), timings = {};
  const [raw, fx] = await Promise.all([fetch('./data/geo-map.json').then((r) => r.json()), fetch('./data/fixture.json').then((r) => r.json())]);
  const onPick = opts.onPick || (() => {});

  // ------------------------------------------------------------------------------------------------ рендерер
  const renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance' });
  const dprMax = hi ? 2 : 1.5;
  renderer.setPixelRatio(Math.min(dprMax, window.devicePixelRatio || 1));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = look.tone === 'aces' ? THREE.ACESFilmicToneMapping : look.tone === 'neutral' ? THREE.NeutralToneMapping : THREE.NoToneMapping;
  renderer.toneMappingExposure = look.exp;
  renderer.shadowMap.enabled = hi;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  renderer.shadowMap.autoUpdate = false;
  renderer.info.autoReset = true;
  const canvas = renderer.domElement;
  canvas.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;display:block;touch-action:none;outline:none;-webkit-tap-highlight-color:transparent;user-select:none';
  if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
  host.style.overflow = 'hidden'; host.style.background = look.bg;
  host.appendChild(canvas);
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(look.bg);
  const cam = new THREE.PerspectiveCamera(FOV, 1, 1, 80);
  const hemi = new THREE.HemisphereLight(look.hemi[0], look.hemi[1], look.hemi[2]); scene.add(hemi);
  const sun = new THREE.DirectionalLight(look.sun[0], look.sun[1]);
  sun.position.set(-7, 10, 2.8); sun.target.position.set(0, 0, 0);
  if (hi) {
    sun.castShadow = true; sun.shadow.mapSize.set(2048, 2048);
    Object.assign(sun.shadow.camera, { left: -8, right: 8, top: 6.5, bottom: -6.5, near: 2, far: 26 });
    sun.shadow.bias = -0.0005; sun.shadow.normalBias = 0.012; sun.shadow.radius = 2.2;
  }
  scene.add(sun); scene.add(sun.target);

  // ------------------------------------------------------------------------------------------------ країна
  const lvl = Object.fromEntries((raw.cities || []).map((c) => [c.name, c.lvl]));
  const cities = fx.cities.map((c) => { const L = lvl[c.name]; const tier = L === 1 ? 3 : L === 2 ? 2 : 1; return { ...c, tier, r: c.name === 'Опішня' ? 25 : tier === 3 ? 48 : tier === 2 ? 38 : 30 }; });
  let t = performance.now();
  const geo = buildGeo(raw, fx, hi);
  timings.geo = performance.now() - t; t = performance.now();
  const routes = layoutRoutes(geo, fx, cities);
  timings.routes = performance.now() - t; t = performance.now();
  buildRelief(geo, routes, cities);
  // відстань до шляхів — для картини (квіти, поля, ліс не лізуть під плашки)
  {
    const n = geo.mw * geo.mh, m = new Uint8Array(n);
    for (const r of routes) for (const p of r.samples) m[geo.pxOf(p[0], p[1])] = 1;
    for (const c of cities) m[geo.pxOf(c.x, c.y)] = 1;
    geo.routeDist = geo.edtOf ? geo.edtOf(m) : routeEDT(m, geo.mw, geo.mh);
  }
  timings.relief = performance.now() - t; t = performance.now();
  const mesh = buildMesh(geo, hi);
  timings.mesh = performance.now() - t; t = performance.now();
  const map = paintMap(look, lookName, geo, { routes, cities }, hi);
  timings.paint = performance.now() - t; t = performance.now();

  const mapTex = new THREE.CanvasTexture(map);
  mapTex.colorSpace = THREE.SRGBColorSpace; mapTex.anisotropy = Math.min(renderer.capabilities.getMaxAnisotropy(), hi ? 12 : 4); mapTex.generateMipmaps = true; mapTex.minFilter = THREE.LinearMipmapLinearFilter;

  const time = { value: 0 };
  // земля + поле (паспарту) — один меш
  const { OFF, CW, CH } = CANVAS_DOMAIN;
  const landMat = new THREE.MeshLambertMaterial({ map: mapTex });
  const landGeo = (() => {
    const P = Array.from(mesh.pos), Nn = Array.from(mesh.nor), UV = Array.from(mesh.uv), IDX = mesh.idx.slice();
    const base = () => P.length / 3;
    const uvOf = (x, y) => [(x + OFF) / CW, 1 - (y + OFF) / CH];
    const paper = look.frame === 'paper';
    const a0 = -PAD, b0 = W + PAD, c0 = -PAD, d0 = H + PAD, a1 = -PAD - MAT, b1 = W + PAD + MAT, c1 = -PAD - MAT, d1 = H + PAD + MAT;
    // обхід прямокутника: рядки вершин, щоб поле могло мати рваний край і підкручені кути (пергамент)
    const perim = [], NP = paper ? 260 : 4, TT = 2 * (b0 - a0) + 2 * (d0 - c0);
    const on = (u, a, b, c, d) => { const w = b - a, h = d - c, p = u * (2 * w + 2 * h); if (p < w) return [a + p, c]; if (p < w + h) return [b, c + p - w]; if (p < 2 * w + h) return [b - (p - w - h), d]; return [a, d - (p - 2 * w - h)]; };
    const rows = paper ? 4 : 2;
    const v0 = base();
    const idxAt = (i, j) => v0 + ((i + NP) % NP) * rows + j;
    for (let i = 0; i < NP; i++) {
      const u = paper ? i / NP : [0, (b0 - a0) / TT, ((b0 - a0) + (d0 - c0)) / TT, (2 * (b0 - a0) + (d0 - c0)) / TT][i];
      const [xi, yi] = on(u, a0, b0, c0, d0);
      for (let j = 0; j < rows; j++) {
        const f = j / (rows - 1);
        const [xo, yo] = paper ? on(u, a1, b1, c1, d1) : (i === 0 ? [a1, c1] : i === 1 ? [b1, c1] : i === 2 ? [b1, d1] : [a1, d1]);
        let x = xi + (xo - xi) * f, y = yi + (yo - yi) * f, yy = LV.mat;
        if (paper && j > 0) {
          const nn = (Math.sin(u * 91) + Math.sin(u * 213 + 1) * 0.6 + Math.sin(u * 511 + 3) * 0.3) * 13 * f * f;
          const ln = Math.hypot(xo - xi, yo - yi) || 1; x += (xo - xi) / ln * nn; y += (yo - yi) / ln * nn;
          // кути трохи підкручені
          const cd = Math.min(Math.hypot(x - a1, y - c1), Math.hypot(x - b1, y - c1), Math.hypot(x - b1, y - d1), Math.hypot(x - a1, y - d1));
          yy += 0.1 * Math.pow(smooth(420, 40, cd), 1.6) * f + 0.006 * Math.sin(u * 77) * f;
        }
        P.push(wx(x), yy, wz(y)); Nn.push(0, 1, 0); const q = uvOf(x, y); UV.push(q[0], q[1]);
      }
    }
    const stIdx = IDX.length;
    for (let i = 0; i < NP; i++) for (let j = 0; j < rows - 1; j++) { const a = idxAt(i, j), b = idxAt(i + 1, j), c = idxAt(i + 1, j + 1), d = idxAt(i, j + 1); IDX.push(a, b, c, a, c, d); }
    // нормалі поля
    if (paper) {
      const tmp = new Float32Array(NP * rows * 3);
      for (let k = stIdx; k < IDX.length; k += 3) { const A = IDX[k], B = IDX[k + 1], C = IDX[k + 2]; const a = [P[3 * A], P[3 * A + 1], P[3 * A + 2]], b = [P[3 * B], P[3 * B + 1], P[3 * B + 2]], c = [P[3 * C], P[3 * C + 1], P[3 * C + 2]]; const n = triNorm(a, b, c); for (const V of [A, B, C]) { const o = V - v0; tmp[3 * o] += n[0]; tmp[3 * o + 1] += n[1]; tmp[3 * o + 2] += n[2]; } }
      for (let o = 0; o < NP * rows; o++) { const l = Math.hypot(tmp[3 * o], tmp[3 * o + 1], tmp[3 * o + 2]) || 1; Nn[3 * (v0 + o)] = tmp[3 * o] / l; Nn[3 * (v0 + o) + 1] = tmp[3 * o + 1] / l; Nn[3 * (v0 + o) + 2] = tmp[3 * o + 2] / l; }
    }
    // внутрішня стінка поля (край «вікна»), щоб не було щілини
    const wallPts = [[a0, c0], [b0, c0], [b0, d0], [a0, d0]];
    for (let i = 0; i < 4; i++) {
      const p = wallPts[i], q = wallPts[(i + 1) % 4], v = base(), n = [[0, 0, 1], [-1, 0, 0], [0, 0, -1], [1, 0, 0]][i];
      for (const [x, y, yy] of [[p[0], p[1], LV.mat], [q[0], q[1], LV.mat], [q[0], q[1], LV.bed - 0.02], [p[0], p[1], LV.bed - 0.02]]) { P.push(wx(x), yy, wz(y)); Nn.push(n[0], n[1], n[2]); const u = uvOf(x + n[0] * 3, y + n[2] * 3); UV.push(u[0], u[1]); }
      IDX.push(v, v + 2, v + 1, v, v + 3, v + 2);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3)); g.setAttribute('normal', new THREE.Float32BufferAttribute(Nn, 3)); g.setAttribute('uv', new THREE.Float32BufferAttribute(UV, 2));
    g.setIndex(IDX);
    return g;
  })();
  const land = new THREE.Mesh(landGeo, landMat); land.receiveShadow = hi; scene.add(land);

  // море: площина з картини + м'яке мерехтіння
  const seaMat = new THREE.MeshLambertMaterial({ map: mapTex });
  const gl = new THREE.Color(look.seaGlint);
  seaMat.onBeforeCompile = (sh) => {
    sh.uniforms.uT = time; sh.uniforms.uG = { value: gl }; sh.uniforms.uK = { value: look.seaGlintK }; sh.uniforms.uD = { value: look.seaDark };
    sh.vertexShader = 'varying vec2 vW;\n' + sh.vertexShader.replace('#include <begin_vertex>', '#include <begin_vertex>\n vW = position.xz;');
    sh.fragmentShader = 'uniform float uT,uK,uD; uniform vec3 uG; varying vec2 vW;\n' + WAVE_GLSL + sh.fragmentShader.replace('#include <map_fragment>', `#include <map_fragment>
      float w1 = waveA(vW*1.15, uT);
      float brk = smoothstep(0.42,0.82, waveA(vW*0.9+vec2(4.0,1.0), uT*0.8));
      float line = sin(vW.y*52. + 2.6*sin(vW.x*3.3+uT*0.33) + uT*0.75);
      float gl = smoothstep(0.86,1.0,line)*brk;
      diffuseColor.rgb *= 1.0 + uD*(w1*2.0-0.6);
      diffuseColor.rgb += uG * uK * gl;`);
  };
  {
    const a = -PAD, b = W + PAD, c = -PAD, d = H + PAD, g = new THREE.BufferGeometry();
    const uvOf = (x, y) => [(x + OFF) / CW, 1 - (y + OFF) / CH];
    g.setAttribute('position', new THREE.Float32BufferAttribute([wx(a), LV.sea, wz(c), wx(b), LV.sea, wz(c), wx(b), LV.sea, wz(d), wx(a), LV.sea, wz(d)], 3));
    g.setAttribute('normal', new THREE.Float32BufferAttribute([0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0], 3));
    g.setAttribute('uv', new THREE.Float32BufferAttribute([...uvOf(a, c), ...uvOf(b, c), ...uvOf(b, d), ...uvOf(a, d)], 2));
    g.setIndex([0, 2, 1, 0, 3, 2]);
    const sea = new THREE.Mesh(g, seaMat); sea.receiveShadow = hi; scene.add(sea);
  }

  // річки: стрічка в прорізаному руслі. Вода — плоска смуга, далі з кожного боку — м'який схил берега, що тане в землю.
  // Край задає ця стрічка (гладка), а не перетин із нерівною сіткою землі: жодних зубців.
  {
    const P = [], A = [], I = [], k = hi ? 1 : 2, ROWS = [-1.4, -1, -0.45, 0.45, 1, 1.4], nr = ROWS.length;
    for (const rv of geo.rivers) {
      const Q = rv.pts; if (Q.length < 3) continue;
      let along = 0; const n = Q.length; let prevIdx = -1, prevI = 0;
      for (let i = 0; i < n; i += k) {
        const a = Q[Math.max(0, i - 2)], b = Q[Math.min(n - 1, i + 2)], dx = b[0] - a[0], dy = b[1] - a[1], l = Math.hypot(dx, dy) || 1, nx = -dy / l, ny = dx / l;
        if (i > 0) along += Math.hypot(Q[i][0] - Q[prevI][0], Q[i][1] - Q[prevI][1]);
        prevI = i;
        const tap = Math.min(1, 0.42 + i / Math.max(1, n) * 3.2), hw = rv.w * 0.92 * tap;
        const hc = heightAt(geo, Q[i][0], Q[i][1]), dep = 0.013 + rv.w * 0.0003;
        const yw = Math.max(LV.sea + 0.0015, hc - dep * 0.6);
        const v = P.length / 3;
        for (const f of ROWS) {
          const af = Math.abs(f), X = Q[i][0] + nx * hw * f, Y = Q[i][1] + ny * hw * f;
          const yy = af > 1.2 ? Math.max(yw, heightAt(geo, X, Y) + 0.0016) : yw;
          P.push(wx(X), yy, wz(Y)); A.push(along * U, f, rv.w);
        }
        if (prevIdx >= 0) for (let e = 0; e < nr - 1; e++) I.push(prevIdx + e, prevIdx + e + 1, v + e, prevIdx + e + 1, v + e + 1, v + e);
        prevIdx = v;
      }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3)); g.setAttribute('aS', new THREE.Float32BufferAttribute(A, 3)); g.setIndex(I);
    const m = new THREE.ShaderMaterial({
      transparent: true,
      uniforms: { uT: time, uA: { value: new THREE.Color(look.river.a) }, uB: { value: new THREE.Color(look.river.b) }, uG: { value: new THREE.Color(look.river.glint) }, uBank: { value: new THREE.Color(look.river.bank) }, uL: { value: look.tone === 'aces' ? 0.95 : 1.0 } },
      vertexShader: `attribute vec3 aS; varying vec3 vS; void main(){ vS=aS; gl_Position=projectionMatrix*modelViewMatrix*vec4(position,1.); }`,
      fragmentShader: `uniform float uT,uL; uniform vec3 uA,uB,uG,uBank; varying vec3 vS;
        void main(){ float c=abs(vS.y), s=vS.x;
          vec3 col=mix(uA,uB,smoothstep(0.15,1.0,c));
          float f=sin(s*34.-uT*1.15+sin(s*7.+vS.y*2.)*1.6)*0.5+0.5; float h=sin(s*13.-uT*0.7+vS.y*3.)*0.5+0.5;
          col+=uG*(smoothstep(0.76,0.97,f*h+0.12)*0.3*(1.0-c*0.7));
          // схил берега: темний біля води, тане в землю; бік, що до сонця, світліший (світло із заходу-північного заходу)
          float bank=smoothstep(0.95,1.03,c), side=vS.y>0.?0.72:1.0;
          col=mix(col,uBank*side,bank);
          col=mix(col,uBank*0.45,smoothstep(0.0,0.08,c-0.92)*(1.-smoothstep(0.08,0.2,c-0.92))*0.5);
          float a=1.0-smoothstep(1.03,1.4,c);
          gl_FragColor=vec4(col*uL,a);
          #include <tonemapping_fragment>
          #include <colorspace_fragment>
        }`,
      side: THREE.DoubleSide, depthWrite: false,
    });
    const river = new THREE.Mesh(g, m); river.renderOrder = 1; scene.add(river);
  }

  // ------------------------------------------------------------------------------------------------ рамка і стіл
  const woodTex = new THREE.CanvasTexture(paintWood(look, hi ? 1024 : 512));
  woodTex.colorSpace = THREE.SRGBColorSpace; woodTex.wrapS = woodTex.wrapT = THREE.MirroredRepeatWrapping; woodTex.anisotropy = 4;
  const tableY = look.frame === 'paper' ? -0.075 : -0.24;
  {
    const P = [], Nn = [], Cc = [], Uv = [], I = [], rings = 14, seg = 72, R = 42;
    const c0 = new THREE.Color('#ffffff');
    for (let j = 0; j <= rings; j++) { const rr = R * Math.pow(j / rings, 1.5); for (let i = 0; i < seg; i++) { const a = i / seg * 6.2832, x = Math.cos(a) * rr * 1.25, z = Math.sin(a) * rr; P.push(x, tableY, z); Nn.push(0, 1, 0); Uv.push((x + 3.1) / 7.5, (z + 1.3) / 7.5); const dd = Math.hypot(x / 1.5, z), f = look.tableLight * (0.28 + 0.72 * Math.pow(smooth(18, 4, dd), 1.2)); Cc.push(f, f, f); } }
    for (let j = 0; j < rings; j++) for (let i = 0; i < seg; i++) { const a = j * seg + i, b = j * seg + (i + 1) % seg, c = (j + 1) * seg + i, d = (j + 1) * seg + (i + 1) % seg; I.push(a, b, c, b, d, c); }
    const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3)); g.setAttribute('normal', new THREE.Float32BufferAttribute(Nn, 3)); g.setAttribute('color', new THREE.Float32BufferAttribute(Cc, 3)); g.setAttribute('uv', new THREE.Float32BufferAttribute(Uv, 2)); g.setIndex(I);
    const m = new THREE.MeshLambertMaterial({ map: woodTex, vertexColors: true });
    const table = new THREE.Mesh(g, m); table.receiveShadow = hi; scene.add(table);
  }
  const OUTER = PAD + MAT;
  const fx0 = wx(-OUTER), fx1 = wx(W + OUTER), fz0 = wz(-OUTER), fz1 = wz(H + OUTER);
  const frameO = (() => {
    const o = { pos: [], nor: [], uv: [], col: [] };
    const white = new THREE.Color('#ffffff');
    // профіль: зсув від краю паспарту назовні, висота
    let prof;
    const cA = new THREE.Color(look.frameCol), cB = new THREE.Color(look.frameCol2);
    const light = look.frame === 'lacquer' ? cA.clone() : cA.clone().lerp(new THREE.Color('#ffffff'), 0.14);
    const dark = cA.clone().multiplyScalar(0.72);
    if (look.frame === 'paper') prof = [[-0.02, LV.mat - 0.012, cA], [0.0, LV.mat - 0.012, cA], [0.035, -0.002, cA], [0.055, tableY, dark]];
    else if (look.frame === 'lacquer') prof = [[-0.01, LV.mat + 0.001, cA], [0.015, LV.mat + 0.06, cA], [0.04, LV.mat + 0.075, cB], [0.075, LV.mat + 0.075, cB], [0.09, LV.mat + 0.082, light], [0.21, LV.mat + 0.082, light], [0.245, LV.mat + 0.058, cA], [0.255, tableY, dark]];
    else prof = [[-0.01, LV.mat + 0.001, dark], [0.02, LV.mat + 0.052, cA], [0.055, LV.mat + 0.062, cB], [0.085, LV.mat + 0.062, cB], [0.095, LV.mat + 0.082, white.clone().multiplyScalar(0.95)], [0.22, LV.mat + 0.088, white], [0.26, LV.mat + 0.056, white.clone().multiplyScalar(0.86)], [0.27, tableY, white.clone().multiplyScalar(0.62)]];
    const corners = [[fx0, fz1, -1, 1], [fx1, fz1, 1, 1], [fx1, fz0, 1, -1], [fx0, fz0, -1, -1]];
    const uvS = 1 / 7.5;
    for (let s = 0; s < 4; s++) {
      const a = corners[s], b = corners[(s + 1) % 4], horizontal = s % 2 === 0;
      for (let k = 0; k < prof.length - 1; k++) {
        const [o0, y0, col0] = prof[k], [o1, y1, col1] = prof[k + 1];
        const pa0 = [a[0] + a[2] * o0, y0, a[1] + a[3] * o0], pb0 = [b[0] + b[2] * o0, y0, b[1] + b[3] * o0], pa1 = [a[0] + a[2] * o1, y1, a[1] + a[3] * o1], pb1 = [b[0] + b[2] * o1, y1, b[1] + b[3] * o1];
        const n = triNorm(pa0, pa1, pb0), up = (n[1] > 0 || (Math.abs(n[1]) < 1e-3 && (n[0] * (pa1[0]) + n[2] * pa1[2] > 0)));
        const N = up ? n : [-n[0], -n[1], -n[2]];
        const tri = (A, B, C, cc) => { const m = triNorm(A, B, C), good = m[0] * N[0] + m[1] * N[1] + m[2] * N[2] > 0; for (const V of good ? [A, B, C] : [A, C, B]) { o.pos.push(V[0], V[1], V[2]); o.nor.push(N[0], N[1], N[2]); o.col.push(cc[V === A ? 0 : V === B ? 1 : 2].r, cc[V === A ? 0 : V === B ? 1 : 2].g, cc[V === A ? 0 : V === B ? 1 : 2].b); o.uv.push(horizontal ? V[0] * uvS : V[2] * uvS + 0.37, horizontal ? (V[2] + V[1]) * uvS + s * 0.2 : (V[0] + V[1]) * uvS + s * 0.2); } };
        tri(pa0, pb0, pa1, [col0, col0, col1]); tri(pb0, pb1, pa1, [col0, col1, col1]);
      }
    }
    return o;
  })();
  {
    const g = bufGeom(frameO);
    const usesWood = look.frame === 'wood';
    const m = look.frame === 'lacquer' ? new THREE.MeshPhongMaterial({ vertexColors: true, specular: new THREE.Color('#6a5a44'), shininess: 60 }) : new THREE.MeshLambertMaterial({ vertexColors: true, map: usesWood ? woodTex : null });
    const fr = new THREE.Mesh(g, m); fr.receiveShadow = hi; fr.castShadow = hi; scene.add(fr);
  }

  // ------------------------------------------------------------------------------------------------ плашки шляхів
  const atlasCv = paintAtlas(look, hi);
  const atlasTex = new THREE.CanvasTexture(atlasCv); atlasTex.colorSpace = THREE.SRGBColorSpace; atlasTex.anisotropy = 8; atlasTex.minFilter = THREE.LinearMipmapLinearFilter; atlasTex.generateMipmaps = true;
  const plates = [];
  const plOut = { pos: [], nor: [], uv: [], col: [] };
  const rowOf = Object.fromEntries(ATLAS.order.map((c, i) => [c, i])), rows = ATLAS.order.length + 1;
  for (const r of routes) { r.plates = layoutPlates(geo, r); for (const pl of r.plates) { plates.push(pl); writePlate(plOut, r, pl, look, hi, 0, { i: rowOf[r.color], rows }); } }
  const plateMat = new THREE.MeshLambertMaterial({ map: atlasTex, vertexColors: true });
  const plateMesh = new THREE.Mesh(bufGeom(plOut), plateMat); plateMesh.castShadow = false; plateMesh.receiveShadow = hi; scene.add(plateMesh);

  // ------------------------------------------------------------------------------------------------ поселення
  const town = new M.Builder(21, 0.05), chimneys = [];
  const TP = look.town;
  const rndTown = rng(7001);
  const HUT = 25 * U;
  const col = (h) => new THREE.Color(h);
  for (const c of cities) {
    const x = wx(c.x), z = wz(c.y), y = heightAt(geo, c.x, c.y), R = c.r * U, big = c.tier === 3;
    town.m.identity();
    const seg = hi ? 22 : 14, rimC = col(TP.plazaRim), plaza = col(big ? TP.plazaBig : TP.plaza);
    const y1 = y + 0.012;
    town.j = 0;
    town.frustum(x, y - 0.03, z, R * 1.04, y1 - 0.002, R, seg, rimC.clone().multiplyScalar(0.8));
    town.frustum(x, y1 - 0.002, z, R, y1, R * 0.91, seg, rimC);
    town.frustum(x, y1, z, R * 0.91, y1 - 0.0002, 0.0001, seg, plaza);
    town.j = 0.05;
    const n = c.tier === 3 ? (hi ? 6 : 5) : c.tier === 2 ? 4 : 3;
    const rin = R * (big ? 0.68 : 0.58);
    for (let i = 0; i < n; i++) {
      const a = i / n * 6.283 + rndTown() * 0.6 + c.x, d = rin * (0.86 + rndTown() * 0.2), hs = HUT * (0.88 + rndTown() * 0.22) * (big ? 1 : 0.94);
      const hx = x + Math.cos(a) * d, hz = z + Math.sin(a) * d;
      const face = -a + Math.PI / 2 + (rndTown() - 0.5) * 0.4, chim = i % 2 === 0;
      M.hut(town, hx, y1 + 0.003, hz, Math.round(face / (Math.PI / 2)) * Math.PI / 2 + (rndTown() - 0.5) * 0.35, hs, TP, { chim, detail: hi });
      if (chim && (c.tier >= 2 || i === 0)) chimneys.push([hx, y1 + hs * 1.0, hz, rndTown()]);
    }
    const s = HUT;
    if (c.name === 'Київ') M.church(town, x, y1 + 0.003, z, s * 1.1, TP, { gold: true });
    else if (c.name === 'Львів') M.tower(town, x, y1 + 0.003, z, s * 1.08, TP);
    else if (c.name === 'Одеса') M.lighthouse(town, x, y1 + 0.003, z, s * 1.05, TP);
    else if (c.name === "Кам'янець-Подільський") M.fortress(town, x, y1 + 0.003, z, s * 0.86, TP);
    else if (big || c.name === 'Чернігів' || c.name === 'Полтава') M.church(town, x, y1 + 0.003, z, s * 0.98, TP);
    else if (c.tier === 2) M.church(town, x, y1 + 0.003, z, s * 0.8, TP);
    else if (c.name === 'Перекоп') { M.saltHeap(town, x, y1 + 0.003, z, s * 0.9, TP); }
  }
  // декор: вітряки, кургани, соляні купи — лише там, де вільно
  const dr = rng(8123);
  const freeForDecor = (mx, my, dR, dC) => {
    if (geo.routeDist[geo.pxOf(mx, my)] / geo.ms < dR) return false;
    for (const c of cities) if (Math.hypot(c.x - mx, c.y - my) < c.r + dC) return false;
    return true;
  };
  let mills = 0, kurg = 0, salt = 0;
  const kurgC = lookName === 'petryk' ? '#54462a' : lookName === 'parchment' ? '#a48c5c' : '#93b64e';
  for (let i = 0; i < 2600; i++) {
    const mx = dr() * W, my = dr() * H; if (geo.maskAt(mx, my) !== 1 || geo.bil(geo.sU, mx, my) < 26) continue;
    const y = heightAt(geo, mx, my);
    if (my > 1000 && mills < 16 && freeForDecor(mx, my, 56, 46)) { M.windmill(town, wx(mx), y, wz(my), HUT * 1.05, TP, 0.3 + dr() * 0.4 - 0.3); mills++; continue; }
    if (my > 1300 && kurg < 8 && freeForDecor(mx, my, 56, 40)) { M.kurgan(town, wx(mx), y, wz(my), HUT * 0.82, kurgC, TP); kurg++; continue; }
  }
  for (let i = 0; i < 800 && salt < 7; i++) {
    const c = cities.find((q) => q.name === 'Перекоп'), a = dr() * 6.28, rr = c.r + 20 + dr() * 80, mx = c.x + Math.cos(a) * rr, my = c.y + Math.sin(a) * rr;
    if (geo.maskAt(mx, my) === 2 || !freeForDecor(mx, my, 40, 8)) continue;
    M.saltHeap(town, wx(mx), heightAt(geo, mx, my), wz(my), HUT * (0.42 + dr() * 0.22), TP); salt++;
  }
  const townMesh = new THREE.Mesh(town.geometry(), new THREE.MeshLambertMaterial({ vertexColors: true })); townMesh.castShadow = hi; townMesh.receiveShadow = hi; scene.add(townMesh);

  // дерева
  {
    const tg = M.treeGeometry(look.trees.kind, hi), cap = hi ? (lookName === 'parchment' ? 520 : 820) : (lookName === 'parchment' ? 220 : 340), pts = [], tr = rng(1234);
    const CA = col(look.trees.a), CB = col(look.trees.b), CC = col(look.trees.c);
    const grid = new Map(), G = 18;
    for (let i = 0; i < cap * 40 && pts.length < cap; i++) {
      const mx = tr() * W, my = tr() * H, f = forestAt(geo, mx, my);
      if (f < 0.3 || tr() > f) continue;
      if (geo.routeDist[geo.pxOf(mx, my)] / geo.ms < 44 || geo.riverDist[geo.pxOf(mx, my)] / geo.ms < 16 || geo.bil(geo.sU, mx, my) < 16) continue;
      const k = (mx / G | 0) + ',' + (my / G | 0); if (grid.has(k)) continue; grid.set(k, 1);
      let ok = true; for (const c of cities) if (Math.hypot(c.x - mx, c.y - my) < c.r + 28) { ok = false; break; } if (!ok) continue;
      pts.push([mx, my]);
    }
    const inst = new THREE.InstancedMesh(tg, new THREE.MeshLambertMaterial({ vertexColors: true }), pts.length);
    const m4 = new THREE.Matrix4(), q = new THREE.Quaternion(), UP = new THREE.Vector3(0, 1, 0);
    pts.forEach((p, i) => {
      const s = (19 + tr() * 13) * U * (lookName === 'board' ? 1 : 0.96), y = heightAt(geo, p[0], p[1]) - 0.003;
      m4.compose(new THREE.Vector3(wx(p[0]), y, wz(p[1])), q.setFromAxisAngle(UP, tr() * 6.28), new THREE.Vector3(s, s * (0.9 + tr() * 0.3), s));
      inst.setMatrixAt(i, m4);
      const t0 = tr(), cc = t0 < 0.5 ? CA.clone().lerp(CC, t0 * 2) : CA.clone().lerp(CB, (t0 - 0.5) * 2);
      inst.setColorAt(i, cc);
    });
    inst.castShadow = hi; inst.receiveShadow = false; inst.frustumCulled = false; scene.add(inst);
  }

  // ------------------------------------------------------------------------------------------------ дим
  {
    const puffs = hi ? 3 : 2, use = chimneys.slice(0, hi ? 46 : 22), n = use.length * puffs;
    const g = new THREE.InstancedBufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0], 3)); g.setIndex([0, 1, 2, 0, 2, 3]);
    const at = new Float32Array(n * 4); let k = 0;
    for (const c of use) for (let i = 0; i < puffs; i++) { at[k++] = c[0]; at[k++] = c[1]; at[k++] = c[2]; at[k++] = (c[3] + i / puffs) % 1; }
    g.setAttribute('aP', new THREE.InstancedBufferAttribute(at, 4)); g.instanceCount = n;
    const m = new THREE.ShaderMaterial({
      transparent: true, depthWrite: false,
      uniforms: { uT: time, uC: { value: col(look.smoke) } },
      vertexShader: `attribute vec4 aP; uniform float uT; varying vec2 vU; varying float vA;
        void main(){ float t=fract(aP.w+uT*0.075); float s=0.014+t*0.062;
          vec3 c=aP.xyz; c.y+=t*0.34; c.x+=sin(t*5.+aP.w*20.)*0.018*t+t*0.05; c.z+=cos(t*4.+aP.w*11.)*0.012*t;
          vec4 mv=modelViewMatrix*vec4(c,1.); mv.xy+=position.xy*s; vU=position.xy; vA=smoothstep(0.,0.12,t)*(1.-t)*(1.-t); gl_Position=projectionMatrix*mv; }`,
      fragmentShader: `uniform vec3 uC; varying vec2 vU; varying float vA; void main(){ float d=dot(vU,vU); if(d>1.)discard; float a=(1.-d); a*=a; gl_FragColor=vec4(uC,a*vA*0.62);
        #include <tonemapping_fragment>
        #include <colorspace_fragment>
      }`,
    });
    const smoke = new THREE.Mesh(g, m); smoke.frustumCulled = false; smoke.renderOrder = 5; scene.add(smoke);
  }

  // ------------------------------------------------------------------------------------------------ вози
  const CG = M.cartGeometries(hi, look.cart);
  const CAP = 45 * 4, neutralI = new THREE.InstancedMesh(CG.neutral, new THREE.MeshLambertMaterial({ vertexColors: true }), CAP);
  neutralI.castShadow = hi; neutralI.frustumCulled = false; scene.add(neutralI);
  const bodyI = CG.bodies.map((g) => { const m = new THREE.InstancedMesh(g, new THREE.MeshLambertMaterial({ vertexColors: true }), 45); m.castShadow = hi; m.frustumCulled = false; scene.add(m); m.count = 0; return m; });
  neutralI.count = 0;
  const pcol = look.players.map((c) => col(c));
  function placeCarts(claims) {
    const m4 = new THREE.Matrix4(), q = new THREE.Quaternion(), UP = new THREE.Vector3(0, 1, 0); let nn = 0; const nb = [0, 0, 0, 0];
    for (const key of Object.keys(claims)) {
      const pi = +key;
      for (const id of claims[key]) {
        const r = routes[id]; if (!r) continue;
        for (const pl of r.plates) {
          const s = Math.min(pl.len * 0.94, 112) * U, y = (pl.y0 + pl.y1) / 2 + 0.0135;
          const dir = pl.tx >= 0 ? 1 : -1;                                 // вози їдуть «обличчям» праворуч — так їх видно збоку
          m4.compose(new THREE.Vector3(wx(pl.x), y, wz(pl.y)), q.setFromAxisAngle(UP, -Math.atan2(pl.ty * dir, pl.tx * dir)), new THREE.Vector3(s, s, s));
          neutralI.setMatrixAt(nn++, m4); bodyI[pi].setMatrixAt(nb[pi]++, m4);
          bodyI[pi].setColorAt(nb[pi] - 1, pcol[pi]);
        }
      }
    }
    neutralI.count = nn; neutralI.instanceMatrix.needsUpdate = true;
    bodyI.forEach((b, i) => { b.count = nb[i]; b.instanceMatrix.needsUpdate = true; if (b.instanceColor) b.instanceColor.needsUpdate = true; });
    renderer.shadowMap.needsUpdate = true;
  }

  // ------------------------------------------------------------------------------------------------ обрана
  const selMat = new THREE.MeshLambertMaterial({ map: atlasTex, vertexColors: true, emissive: new THREE.Color(look.sel), emissiveIntensity: 0.1 });
  const selMesh = new THREE.Mesh(new THREE.BufferGeometry(), selMat); selMesh.visible = false; scene.add(selMesh);
  const glowMat = new THREE.ShaderMaterial({
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
    uniforms: { uT: time, uC: { value: col(look.sel) }, uK: { value: lookName === 'petryk' ? 1.0 : 0.9 }, uStatic: { value: reduced ? 1 : 0 } },
    vertexShader: `attribute vec4 aR; varying vec4 vR; void main(){ vR=aR; gl_Position=projectionMatrix*modelViewMatrix*vec4(position,1.); }`,
    fragmentShader: `uniform float uT,uK,uStatic; uniform vec3 uC; varying vec4 vR;
      void main(){ vec2 d=abs(vR.xy)-vR.zw; float o=length(max(d,0.)); float m=26.; float a=1.-clamp(o/m,0.,1.); a=a*a;
        float pulse=mix(0.72+0.28*sin(uT*2.0),1.0,uStatic);
        gl_FragColor=vec4(uC, a*uK*pulse);
        #include <tonemapping_fragment>
        #include <colorspace_fragment>
      }`,
  });
  const glowMesh = new THREE.Mesh(new THREE.BufferGeometry(), glowMat); glowMesh.visible = false; glowMesh.renderOrder = 3; scene.add(glowMesh);
  let selected = null;
  function setSelected(id) {
    selected = id == null ? null : routes[id] || null;
    selMesh.visible = glowMesh.visible = !!selected;
    if (selected) {
      const o = { pos: [], nor: [], uv: [], col: [] };
      for (const pl of selected.plates) writePlate(o, selected, pl, look, hi, 0.016, { i: rowOf[selected.color], rows });
      selMesh.geometry.dispose(); selMesh.geometry = bufGeom(o);
      const P = [], Rr = [], I = [], m = 30, y = 0.01;
      for (const pl of selected.plates) {
        const hl = pl.len / 2 + m, hw = PW / 2 + m, n = 5, v0 = P.length / 3;
        for (let i = 0; i <= n; i++) for (const sg of [-1, 1]) {
          const s = -hl + 2 * hl * i / n, c = atS(selected, pl.m + s), t = sg * hw, k = (s + pl.len / 2) / pl.len;
          P.push(wx(c.x + c.nx * t), (pl.y0 + (pl.y1 - pl.y0) * clamp(k, 0, 1)) + y, wz(c.y + c.ny * t)); Rr.push(s, t, pl.len / 2, PW / 2);
        }
        for (let i = 0; i < n; i++) { const a = v0 + i * 2; I.push(a, a + 1, a + 2, a + 1, a + 3, a + 2); }
      }
      const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3)); g.setAttribute('aR', new THREE.Float32BufferAttribute(Rr, 4)); g.setIndex(I);
      glowMesh.geometry.dispose(); glowMesh.geometry = g; glowMat.side = THREE.DoubleSide;
    }
    renderer.shadowMap.needsUpdate = true; wake();
  }

  // ------------------------------------------------------------------------------------------------ підписи
  const overlay = document.createElement('div');
  overlay.style.cssText = 'position:absolute;inset:0;overflow:hidden;pointer-events:none;';
  host.appendChild(overlay);
  const vig = document.createElement('div'); vig.style.cssText = `position:absolute;inset:0;pointer-events:none;background:${look.vignette}`; host.insertBefore(vig, overlay);
  const css = look.cssVars;
  const labels = cities.map((c) => {
    const el = document.createElement('span');
    el.textContent = c.name;
    const pill = css.bg !== 'none';
    el.style.cssText = `position:absolute;left:0;top:0;white-space:nowrap;will-change:transform;font:${c.tier === 3 ? css.big : css.font};color:${css.color};` +
      (pill ? `background:${css.bg};padding:2px 5px 2px;border-radius:${lookName === 'board' ? 7 : 3}px;` + (lookName === 'board' ? 'box-shadow:0 1px 2px rgba(30,30,10,.42);' : 'box-shadow:0 0 0 1px rgba(217,173,60,.28);')
        : `text-shadow:0 0 2px ${css.halo},0 0 2px ${css.halo},0 0 3px ${css.halo},1px 1px 1px ${css.halo},-1px -1px 1px ${css.halo},1px -1px 1px ${css.halo},-1px 1px 1px ${css.halo};`);
    overlay.appendChild(el);
    return { c, el, w: 0, h: 0, ox: 0, oy: 0, sx: 0, sy: 0, vis: true };
  });

  // ------------------------------------------------------------------------------------------------ камера
  let Wp = 1, Hp = 1, portrait = false, defPol = 40 * D2R, defDist = 12, defZoomed = false;
  const cs = { az: 0, pol: 40 * D2R, dist: 12, tx: 0, tz: 0 }, def = { az: 0, pol: 40 * D2R, dist: 12, tx: 0, tz: 0 };
  let touched = false;
  const v = new THREE.Vector3();
  function applyCam() {
    const s = Math.sin(cs.pol), c = Math.cos(cs.pol);
    cam.position.set(cs.tx + cs.dist * s * Math.sin(cs.az), cs.dist * c, cs.tz + cs.dist * s * Math.cos(cs.az));
    cam.lookAt(cs.tx, 0, cs.tz);
    cam.near = Math.max(0.5, cs.dist * 0.2); cam.far = cs.dist * 4 + 40;
    cam.updateProjectionMatrix(); cam.updateMatrixWorld();
  }
  const FM = look.frame === 'paper' ? 0.1 : 0.34; const bx0 = fx0 - FM, bx1 = fx1 + FM, bz0 = fz0 - FM, bz1 = fz1 + FM;
  const boardPts = [[bx0, 0, bz0], [bx1, 0, bz0], [bx1, 0, bz1], [bx0, 0, bz1]].map((p) => new THREE.Vector3(...p));
  function fit() {
    portrait = Hp / Wp > 1.15;
    const save = { ...cs };
    if (!portrait) {
      cs.az = 0; cs.pol = 41 * D2R; cs.tx = 0; cs.tz = 0;
      const lx = 0.985, ly = 0.97;
      for (let pass = 0; pass < 3; pass++) {
        let lo = 3, h = 60;
        for (let i = 0; i < 24; i++) { cs.dist = (lo + h) / 2; applyCam(); let ok = true; for (const p of boardPts) { v.copy(p).project(cam); if (Math.abs(v.x) > lx || Math.abs(v.y) > ly) { ok = false; break; } } if (ok) h = cs.dist; else lo = cs.dist; }
        cs.dist = h; applyCam();
        let y0 = 1, y1 = -1; for (const p of boardPts) { v.copy(p).project(cam); y0 = Math.min(y0, v.y); y1 = Math.max(y1, v.y); }
        cs.tz += ((y0 + y1) / 2) * -cs.dist * Math.tan(FOV * D2R / 2) / Math.cos(cs.pol) * -1 * -1;
      }
    } else {
      // вузька рамка: нахил менший, видно смугу близько половини країни; центр — обраний шлях
      cs.az = 0; cs.pol = 25 * D2R;
      const focus = (selected || routes[37]);
      const fxm = focus ? wx((focus.A.x + focus.B.x) / 2) : 0, fzm = focus ? wz((focus.A.y + focus.B.y) / 2) : 0;
      const wantUnits = 1500;  // скільки одиниць карти влазить по ширині
      cs.tx = fxm; cs.tz = fzm + 0.65;
      cs.dist = (wantUnits * U / (Wp / Hp)) / (2 * Math.tan(FOV * D2R / 2));
      applyCam();
      // низ кадру — край поля дошки: згори лишається місце для чужих шляхів, а не порожній стіл унизу
      for (let pass = 0; pass < 4; pass++) {
        v.set(cs.tx, 0, fz1 + 0.06).project(cam);
        cs.tz += (v.y + 0.985) * (-1) * cs.dist * Math.tan(FOV * D2R / 2) / Math.cos(cs.pol) ;
        applyCam();
      }
    }
    def.az = cs.az; def.pol = cs.pol; def.dist = cs.dist; def.tx = cs.tx; def.tz = cs.tz;
    if (touched) { Object.assign(cs, save); applyCam(); }
  }
  function resize() {
    const w = Math.max(1, host.clientWidth | 0), h = Math.max(1, host.clientHeight | 0);
    if (w === Wp && h === Hp) return;
    Wp = w; Hp = h; renderer.setSize(w, h, false); cam.aspect = w / h; cam.updateProjectionMatrix();
    fit(); updateLabels(true);
  }

  // ------------------------------------------------------------------------------------------------ розкладка підписів
  const pj = new THREE.Vector3();
  function project(x, y, z, out) { pj.set(x, y, z).project(cam); out.x = (pj.x + 1) / 2 * Wp; out.y = (1 - pj.y) / 2 * Hp; out.z = pj.z; return out; }
  const _o = { x: 0, y: 0, z: 0 };
  const plateScreen = new Float32Array(plates.length * 2);
  const ANCH = [[0, 1, 0], [0, -1, 1], [1, 0, 1.4], [-1, 0, 1.4], [1, 1, 1.8], [-1, 1, 1.8], [1, -1, 2.3], [-1, -1, 2.3]];
  let placed = false;
  function layoutLabels() {
    for (const l of labels) if (!l.w0) { l.el.style.transform = 'none'; const b = l.el.getBoundingClientRect(); l.w0 = b.width; l.h0 = b.height; }
    for (const l of labels) { l.w = l.w0 * labelK; l.h = l.h0 * labelK; l.el.style.transformOrigin = '0 0'; }
    plates.forEach((pl, i) => { project(wx(pl.x), (pl.y0 + pl.y1) / 2, wz(pl.y), _o); plateScreen[2 * i] = _o.x; plateScreen[2 * i + 1] = _o.y; });
    for (const l of labels) {
      const c = l.c; project(wx(c.x), heightAt(geo, c.x, c.y) + 0.03, wz(c.y), _o); l.px = _o.x; l.py = _o.y;
      project(wx(c.x + c.r), heightAt(geo, c.x, c.y) + 0.03, wz(c.y), _o); l.rx = Math.abs(_o.x - l.px);
      project(wx(c.x), heightAt(geo, c.x, c.y) + 0.03, wz(c.y + c.r), _o); l.ry = Math.abs(_o.y - l.py);
    }
    const order = labels.slice().sort((a, b) => b.c.tier - a.c.tier || a.c.y - b.c.y);
    const rect = new Map();
    const costAt = (l, ax, ay, base) => {
      const gx = ax === 0 ? 0 : ax * (l.rx + 3 + l.w / 2), gy = ay === 0 ? 0 : ay * (l.ry + 2 + l.h / 2);
      const cx = l.px + gx, cy = l.py + gy + (ay === -1 ? -l.ry * 0.5 - 3 : 0);
      const x0 = cx - l.w / 2, x1 = cx + l.w / 2, y0 = cy - l.h / 2, y1 = cy + l.h / 2;
      let s = base;
      if (x0 < 2) s += 60 + (2 - x0); if (x1 > Wp - 2) s += 60 + (x1 - Wp + 2); if (y0 < 2) s += 60; if (y1 > Hp - 2) s += 60;
      for (const [m, r] of rect) { if (m === l) continue; if (x0 < r[2] && x1 > r[0] && y0 < r[3] && y1 > r[1]) s += 140; }
      for (const o of labels) {
        if (o === l) continue;
        if (x1 > o.px - o.rx && x0 < o.px + o.rx && y1 > o.py - o.ry && y0 < o.py + o.ry) s += 70;
        // підпис має бути ближче до свого поселення, ніж до чужого
        const dO = Math.hypot(Math.max(x0 - o.px, 0, o.px - x1), Math.max(y0 - o.py, 0, o.py - y1)), dM = Math.hypot(Math.max(x0 - l.px, 0, l.px - x1), Math.max(y0 - l.py, 0, l.py - y1));
        if (dO < dM + 4) s += 40;
      }
      for (let i = 0; i < plates.length; i++) {
        const X = plateScreen[2 * i], Y = plateScreen[2 * i + 1];
        if (X > x0 - 6 && X < x1 + 6 && Y > y0 - 4 && Y < y1 + 4) s += plates[i].route === selected ? 14 : 3.5;
      }
      return { s, x0, y0, x1, y1 };
    };
    for (let pass = 0; pass < 3; pass++) for (const l of order) {
      let best = null, bs = Infinity;
      for (const a of ANCH) { const r = costAt(l, a[0], a[1], a[2]); if (r.s < bs) { bs = r.s; best = r; } }
      rect.set(l, [best.x0, best.y0, best.x1, best.y1]);
      l.ox = best.x0 - l.px; l.oy = best.y0 - l.py;
    }
    placed = true;
  }
  let labelK = 1;
  function updateLabels(relayout) {
    labelK = clamp(Math.pow(def.dist / cs.dist, 0.42), 0.92, 1.5) * (portrait ? 0.95 : 1);
    for (const l of labels) { project(wx(l.c.x), heightAt(geo, l.c.x, l.c.y) + 0.03, wz(l.c.y), _o); l.px = _o.x; l.py = _o.y; }
    if (relayout || !placed) layoutLabels();
    for (const l of labels) {
      const x = l.px + l.ox, y = l.py + l.oy, vis = x > -l.w && y > -20 && x < Wp && y < Hp;
      if (vis !== l.vis) { l.el.style.display = vis ? '' : 'none'; l.vis = vis; }
      if (vis) l.el.style.transform = `translate3d(${x.toFixed(1)}px,${y.toFixed(1)}px,0) scale(${labelK.toFixed(2)})`;
    }
  }

  // ------------------------------------------------------------------------------------------------ керування
  const ptrs = new Map(); let down = null, pinch = null, settleT = 0;
  const camMoved = () => { touched = true; updateLabels(false); clearTimeout(settleT); settleT = setTimeout(() => { updateLabels(true); wake(); }, 130); wake(); };
  const raycastPlane = (cx, cy, out) => { pj.set(cx / Wp * 2 - 1, 1 - cy / Hp * 2, 0.5).unproject(cam); const dx = pj.x - cam.position.x, dy = pj.y - cam.position.y, dz = pj.z - cam.position.z, t = -cam.position.y / dy; out.x = cam.position.x + dx * t; out.z = cam.position.z + dz * t; return out; };
  const clampT = () => { const fw = fx1 + 0.1, fd = fz1 + 0.1; cs.tx = clamp(cs.tx, -fw, fw); cs.tz = clamp(cs.tz, -fd, fd); };
  canvas.addEventListener('pointerdown', (e) => {
    canvas.setPointerCapture(e.pointerId); ptrs.set(e.pointerId, [e.offsetX, e.offsetY]);
    if (ptrs.size === 1) down = { x: e.offsetX, y: e.offsetY, moved: false, btn: e.button, az: cs.az, pol: cs.pol, tx: cs.tx, tz: cs.tz, a: raycastPlane(e.offsetX, e.offsetY, {}) };
    if (ptrs.size === 2) { const [a, b] = [...ptrs.values()]; pinch = { d: Math.hypot(a[0] - b[0], a[1] - b[1]), dist: cs.dist, my: (a[1] + b[1]) / 2, pol: cs.pol }; down = null; }
  });
  canvas.addEventListener('pointermove', (e) => {
    if (!ptrs.has(e.pointerId)) return; ptrs.set(e.pointerId, [e.offsetX, e.offsetY]);
    if (pinch && ptrs.size === 2) { const [a, b] = [...ptrs.values()]; const d = Math.hypot(a[0] - b[0], a[1] - b[1]); cs.dist = clamp(pinch.dist * pinch.d / d, 2.2, def.dist * 1.25); cs.pol = clamp(pinch.pol + ((a[1] + b[1]) / 2 - pinch.my) * 0.004, 12 * D2R, 62 * D2R); applyCam(); camMoved(); return; }
    if (!down) return;
    if (!down.moved && Math.hypot(e.offsetX - down.x, e.offsetY - down.y) < 7) return; down.moved = true;
    if (down.btn === 2 || e.shiftKey) { cs.az = clamp(down.az + (e.offsetX - down.x) * 0.004, -0.6, 0.6); cs.pol = clamp(down.pol - (e.offsetY - down.y) * 0.004, 12 * D2R, 62 * D2R); applyCam(); camMoved(); return; }
    const hit = raycastPlane(e.offsetX, e.offsetY, {}); cs.tx += down.a.x - hit.x; cs.tz += down.a.z - hit.z; clampT(); applyCam(); camMoved();
  });
  const up = (e) => {
    const wasClick = down && !down.moved && ptrs.size === 1;
    ptrs.delete(e.pointerId); if (ptrs.size < 2) pinch = null;
    if (wasClick) { const best = pickAt(e.offsetX, e.offsetY); if (best != null) { setSelected(best); onPick(best); } }
    if (!ptrs.size) down = null;
  };
  canvas.addEventListener('pointerup', up); canvas.addEventListener('pointercancel', up);
  canvas.addEventListener('contextmenu', (e) => e.preventDefault());
  canvas.addEventListener('wheel', (e) => { e.preventDefault(); const a = raycastPlane(e.offsetX, e.offsetY, {}); cs.dist = clamp(cs.dist * Math.exp(e.deltaY * 0.0012), 2.2, def.dist * 1.25); applyCam(); const b = raycastPlane(e.offsetX, e.offsetY, {}); cs.tx += a.x - b.x; cs.tz += a.z - b.z; clampT(); applyCam(); camMoved(); }, { passive: false });
  canvas.addEventListener('dblclick', () => resetView());
  function pickAt(x, y) { let best = null, bd = 28; for (const pl of plates) { project(wx(pl.x), (pl.y0 + pl.y1) / 2, wz(pl.y), _o); const d = Math.hypot(_o.x - x, _o.y - y); if (d < bd) { bd = d; best = pl.route.id; } } return best; }
  function resetView() { touched = false; Object.assign(cs, def); applyCam(); updateLabels(true); wake(); }

  // ------------------------------------------------------------------------------------------------ цикл
  let alive = true, raf = 0, dirty = true, lastRender = 0;
  const minDt = reduced ? 1e9 : hi ? 32 : 66;
  const stats = { frames: 0, last: {} };
  function frame(now) {
    raf = 0; if (!alive) return;
    const due = now - lastRender >= minDt;
    if (dirty || due) {
      if (!reduced) time.value = now / 1000;
      selMat.emissiveIntensity = reduced ? 0.12 : 0.06 + 0.06 * Math.sin(time.value * 2.0);
      renderer.render(scene, cam); lastRender = now; dirty = false; stats.frames++;
    }
    if (!reduced || dirty) raf = requestAnimationFrame(frame);
  }
  function wake() { dirty = true; if (alive && !raf && !document.hidden) raf = requestAnimationFrame(frame); }
  const onVis = () => { if (document.hidden) { if (raf) cancelAnimationFrame(raf); raf = 0; } else wake(); };
  document.addEventListener('visibilitychange', onVis);
  const ro = new ResizeObserver(() => { resize(); wake(); }); ro.observe(host);

  function setClaims(c) { placeCarts(c); wake(); }
  setClaims(fx.mock.claimed || {});
  selected = routes[fx.mock.selectedRoute] || null;
  resize(); applyCam();
  setSelected(fx.mock.selectedRoute ?? null);
  fit(); applyCam(); updateLabels(true);
  renderer.shadowMap.needsUpdate = true;
  timings.total = performance.now() - T0;
  wake();

  return {
    setClaims, setSelected, resetView, resize,
    measure() {
      // чесний вимір: один кадр без тіней (звичайний) і один з оновленням карти тіней
      const out = {};
      renderer.info.autoReset = true; renderer.shadowMap.needsUpdate = false; renderer.render(scene, cam);
      out.frame = { calls: renderer.info.render.calls, triangles: renderer.info.render.triangles };
      renderer.info.autoReset = false; renderer.info.reset(); renderer.shadowMap.needsUpdate = hi; renderer.render(scene, cam);
      out.withShadow = { calls: renderer.info.render.calls, triangles: renderer.info.render.triangles };
      renderer.info.autoReset = true;
      out.memory = { ...renderer.info.memory }; out.programs = renderer.info.programs?.length;
      out.mesh = { verts: mesh.verts, tris: mesh.tris };
      out.timings = Object.fromEntries(Object.entries(timings).map(([k, v]) => [k, Math.round(v)]));
      out.quality = hi ? 'high' : 'low'; out.dpr = renderer.getPixelRatio(); out.size = [Wp, Hp]; out.mapPx = [map.width, map.height]; out.frames = stats.frames;
      return out;
    },
    view(o) { Object.assign(cs, o); touched = true; applyCam(); updateLabels(true); wake(); },
    get cam() { return { ...cs }; }, get def() { return { ...def }; },
    focusRoute(id, k = 0.28) { const r = routes[id]; cs.tx = wx((r.A.x + r.B.x) / 2); cs.tz = wz((r.A.y + r.B.y) / 2) ; cs.dist = def.dist * k; touched = true; applyCam(); updateLabels(true); wake(); },
    focusAt(x, y, dist, pol) { cs.tx = wx(x); cs.tz = wz(y); cs.dist = dist; if (pol) cs.pol = pol * D2R; touched = true; applyCam(); updateLabels(true); wake(); },
    plateOverlaps() {
      const out = [];
      const corners = (pl) => { const hl = pl.len / 2, hw = PW / 2, tx = pl.tx, ty = pl.ty, nx = -ty, ny = tx; return [[pl.x + tx * hl + nx * hw, pl.y + ty * hl + ny * hw], [pl.x - tx * hl + nx * hw, pl.y - ty * hl + ny * hw], [pl.x - tx * hl - nx * hw, pl.y - ty * hl - ny * hw], [pl.x + tx * hl - nx * hw, pl.y + ty * hl - ny * hw]]; };
      const sat = (A, B) => { for (const P of [A, B]) for (let i = 0; i < 4; i++) { const a = P[i], b = P[(i + 1) % 4], nx = b[1] - a[1], ny = a[0] - b[0]; let a0 = 1e9, a1 = -1e9, b0 = 1e9, b1 = -1e9; for (const p of A) { const d = p[0] * nx + p[1] * ny; a0 = Math.min(a0, d); a1 = Math.max(a1, d); } for (const p of B) { const d = p[0] * nx + p[1] * ny; b0 = Math.min(b0, d); b1 = Math.max(b1, d); } if (a1 < b0 || b1 < a0) return false; } return true; };
      for (let i = 0; i < plates.length; i++) for (let j = i + 1; j < plates.length; j++) { const a = plates[i], b = plates[j]; if (a.route === b.route) continue; if (Math.hypot(a.x - b.x, a.y - b.y) > 240) continue; if (sat(corners(a), corners(b))) out.push(a.route.a + '-' + a.route.b + '#' + a.i + ' x ' + b.route.a + '-' + b.route.b + '#' + b.i); }
      const cit = []; for (const pl of plates) for (const c of cities) { if (c === pl.route.A || c === pl.route.B) continue; if (Math.hypot(pl.x - c.x, pl.y - c.y) < c.r + Math.max(pl.len, PW) / 2 - 8) cit.push(pl.route.a + '-' + pl.route.b + ' over ' + c.name); }
      const short = plates.filter((p) => p.len < 70).map((p) => p.route.a + '-' + p.route.b + ':' + Math.round(p.len));
      return { overlaps: out, cit, short, lens: [Math.min(...plates.map((p) => p.len)), Math.max(...plates.map((p) => p.len))] };
    },
    labelsInfo() { let hit = 0; const R = labels.filter((l) => l.vis).map((l) => { const x = l.px + l.ox, y = l.py + l.oy; return [x, y, x + l.w, y + l.h, l.c.name]; }); for (let i = 0; i < R.length; i++) for (let j = i + 1; j < R.length; j++) if (R[i][0] < R[j][2] && R[i][2] > R[j][0] && R[i][1] < R[j][3] && R[i][3] > R[j][1]) hit++; return { visible: R.length, overlaps: hit }; },
    dispose() { alive = false; if (raf) cancelAnimationFrame(raf); ro.disconnect(); document.removeEventListener('visibilitychange', onVis); renderer.dispose(); canvas.remove(); overlay.remove(); vig.remove(); },
  };
}
function routeEDT(mask, w, h) {
  // проста двопрохідна дистанція (chamfer) — досить для «не лізти під плашки»
  const d = new Float32Array(w * h).fill(1e6); for (let i = 0; i < w * h; i++) if (mask[i]) d[i] = 0;
  const A = 1, B = 1.41421;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) { const i = y * w + x; let v = d[i]; if (x > 0) v = Math.min(v, d[i - 1] + A); if (y > 0) { v = Math.min(v, d[i - w] + A); if (x > 0) v = Math.min(v, d[i - w - 1] + B); if (x < w - 1) v = Math.min(v, d[i - w + 1] + B); } d[i] = v; }
  for (let y = h - 1; y >= 0; y--) for (let x = w - 1; x >= 0; x--) { const i = y * w + x; let v = d[i]; if (x < w - 1) v = Math.min(v, d[i + 1] + A); if (y < h - 1) { v = Math.min(v, d[i + w] + A); if (x < w - 1) v = Math.min(v, d[i + w + 1] + B); if (x > 0) v = Math.min(v, d[i + w - 1] + B); } d[i] = v; }
  return d;
}
