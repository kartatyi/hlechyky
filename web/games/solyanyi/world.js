// «Соляний шлях» — усе, що лежить на столі: земля, море, річки, рамка, плашки шляхів, поселення, дерева, дим, вози.
// buildBase — не залежить від теми (маски, рельєф, сітка, слоти); buildWorld — один вигляд, повністю в окремій групі, щоб
// перемикання теми лише міняло групу.
//   Плашки, значки й підсвітка — злиті меші з атрибутом aS (номер слота): стан слота читається з текстури (scene.js), тому зміна
//   власника, підсвітки чи підйому не торкається геометрії. Вози й дерева — InstancedMesh.
import * as THREE from '/static/vendor/three-0.186.1/three.module.js';
import { CITIES, ROUTES, MAP, route as routeOf, city as cityOf, routeCurve, routeSlots } from './consts.js';
import { W, H, U, PAD, MAT, LV, wx, wz, rng, smooth, buildGeo, buildRelief, buildMesh, heightAt } from './terrain.js';
import { paintMap, paintAtlas, paintIcons, paintSeatBadges, paintWood, ATLAS, forestAt, CANVAS_DOMAIN } from './paint.js';
import * as M from './models.js';

export const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
const pause = () => new Promise((r) => setTimeout(r));
const WMAX = 44, WMIN = 28, GAP_TO_NEIGHBOUR = 6;

// ---------------------------------------------------------------------------------------------------------- шляхи
export function atS(r, s) {
  const a = r.acc; let lo = 0, hi = a.length - 1;
  while (hi - lo > 1) { const m = (lo + hi) >> 1; if (a[m] < s) lo = m; else hi = m; }
  const k = (s - a[lo]) / (a[hi] - a[lo] || 1), p = r.samples[lo], q = r.samples[hi], dx = q[0] - p[0], dy = q[1] - p[1], l = Math.hypot(dx, dy) || 1;
  return { x: p[0] + dx * k, y: p[1] + dy * k, tx: dx / l, ty: dy / l, nx: -dy / l, ny: dx / l };
}
function segDist(a, b, c, d) {
  // відстань між відрізками ab і cd (2D)
  const ux = b[0] - a[0], uy = b[1] - a[1], vx = d[0] - c[0], vy = d[1] - c[1], wx0 = a[0] - c[0], wy0 = a[1] - c[1];
  const A = ux * ux + uy * uy, B = ux * vx + uy * vy, C = vx * vx + vy * vy, D = ux * wx0 + uy * wy0, E = vx * wx0 + vy * wy0, den = A * C - B * B;
  let s = den > 1e-9 ? clamp((B * E - C * D) / den, 0, 1) : 0, t = C > 0 ? (B * s + E) / C : 0;
  if (t < 0) { t = 0; s = A > 0 ? clamp(-D / A, 0, 1) : 0; } else if (t > 1) { t = 1; s = A > 0 ? clamp((B - D) / A, 0, 1) : 0; }
  return Math.hypot(wx0 + s * ux - t * vx, wy0 + s * uy - t * vy);
}
/** Шляхи й слоти лише з consts.js. Ширина плашки — скільки дозволяють сусіди (28…44): на типовому масштабі значок тоді ≥ 12 px. */
function layoutRoutes() {
  const routes = ROUTES.map(([id]) => {
    const rt = routeOf(id), C = routeCurve(id), A = cityOf(rt.a), B = cityOf(rt.b), raw = routeSlots(id);
    const cell = (C.total - A.r - B.r) / rt.len;
    const r = { id, cargo: rt.cargo, len: rt.len, twin: rt.twin, a: rt.a, b: rt.b, A, B, samples: C.pts, acc: C.arc, total: C.total, slots: [] };
    raw.forEach((q, n) => {
      const hl = q.len / 2, tx = Math.cos(q.a), ty = Math.sin(q.a);
      r.slots.push({ route: r, n, x: q.x, y: q.y, a: q.a, tx, ty, len: q.len, w: WMAX, m: A.r + (n + 0.5) * cell, P: [q.x - tx * hl, q.y - ty * hl], Q: [q.x + tx * hl, q.y + ty * hl] });
    });
    return r;
  });
  const slots = routes.flatMap((r) => r.slots);
  slots.forEach((s, i) => { s.i = i; });
  for (let i = 0; i < slots.length; i++) for (let j = i + 1; j < slots.length; j++) {
    const a = slots[i], b = slots[j]; if (a.route === b.route || Math.hypot(a.x - b.x, a.y - b.y) > 260) continue;
    const w = segDist(a.P, a.Q, b.P, b.Q) - GAP_TO_NEIGHBOUR;
    if (w < a.w) a.w = w; if (w < b.w) b.w = w;
  }
  for (const r of routes) {
    for (const s of r.slots) s.w = clamp(s.w, WMIN, Math.min(WMAX, s.len * 1.02));
    // сусідні плашки одного шляху — без різких перепадів ширини
    for (let n = 1; n < r.slots.length; n++) r.slots[n].w = Math.min(r.slots[n].w, r.slots[n - 1].w + 5);
    for (let n = r.slots.length - 2; n >= 0; n--) r.slots[n].w = Math.min(r.slots[n].w, r.slots[n + 1].w + 5);
  }
  return { routes, slots };
}

let geoRaw = null;
const fetchGeo = () => (geoRaw ||= fetch(MAP.url).then((r) => { if (!r.ok) throw new Error('geo-map ' + r.status); return r.json(); }).catch((e) => { geoRaw = null; throw e; }));

/** Те, що спільне для всіх тем. */
export async function buildBase(hi) {
  const raw = await fetchGeo();
  const cities = CITIES.map(([i, slug, name, x, y, r, lvl]) => ({ i, slug, name, x, y, r, lvl }));
  const { routes, slots } = layoutRoutes();
  const geo = buildGeo(raw, null, hi);
  await pause();
  buildRelief(geo, routes, cities);
  {
    const n = geo.mw * geo.mh, m = new Uint8Array(n);
    for (const r of routes) for (const p of r.samples) m[geo.pxOf(p[0], p[1])] = 1;
    for (const c of cities) m[geo.pxOf(c.x, c.y)] = 1;
    geo.routeDist = chamfer(m, geo.mw, geo.mh);
  }
  await pause();
  const mesh = buildMesh(geo, hi);
  for (const r of routes) for (const s of r.slots) {
    const samp = (d) => { const q = atS(r, d); let h = -1; for (const t of [-s.w / 2, 0, s.w / 2]) h = Math.max(h, heightAt(geo, q.x + q.nx * t, q.y + q.ny * t)); return h; };
    const y0 = samp(s.m - s.len / 2) + 0.003, y1 = samp(s.m + s.len / 2) + 0.003, ym = samp(s.m) + 0.003, e = Math.max(0, ym - (y0 + y1) / 2);
    s.y0 = y0 + e; s.y1 = y1 + e;
  }
  for (const c of cities) c.h = heightAt(geo, c.x, c.y);
  return { geo, mesh, routes, slots, cities };
}
function chamfer(mask, w, h) {
  const d = new Float32Array(w * h).fill(1e6); for (let i = 0; i < w * h; i++) if (mask[i]) d[i] = 0;
  const A = 1, B = 1.41421;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) { const i = y * w + x; let v = d[i]; if (x > 0) v = Math.min(v, d[i - 1] + A); if (y > 0) { v = Math.min(v, d[i - w] + A); if (x > 0) v = Math.min(v, d[i - w - 1] + B); if (x < w - 1) v = Math.min(v, d[i - w + 1] + B); } d[i] = v; }
  for (let y = h - 1; y >= 0; y--) for (let x = w - 1; x >= 0; x--) { const i = y * w + x; let v = d[i]; if (x < w - 1) v = Math.min(v, d[i + 1] + A); if (y < h - 1) { v = Math.min(v, d[i + w] + A); if (x < w - 1) v = Math.min(v, d[i + w + 1] + B); if (x > 0) v = Math.min(v, d[i + w - 1] + B); } d[i] = v; }
  return d;
}

// ---------------------------------------------------------------------------------------------------------- плашки
/** Силует плашки — ще одна ознака вантажу, крім кольору, значка й фактури. */
const SHAPE = { salt: 'chev', linen: 'chev', honey: 'pill', wool: 'pill', hides: 'bone', tar: 'bone', grain: 'rect', fish: 'rect', grey: 'rect' };
function triNorm(a, b, c) { const ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2]; const X = uy * vz - uz * vy, Y = uz * vx - ux * vz, Z = ux * vy - uy * vx, l = Math.hypot(X, Y, Z) || 1; return [X / l, Y / l, Z / l]; }
function writePlate(out, r, s, look, hi, rowIdx, rows) {
  const shape = SHAPE[r.cargo] || 'rect', hw = s.w / 2, hl = s.len / 2, H0 = 0.013;
  const rc = shape === 'pill' ? Math.min(hw, hl) - 0.5 : 6, nC = shape === 'pill' ? (hi ? 4 : 2) : 1, nS = Math.max(1, Math.round(2 * hl / (hi ? 26 : 48)));
  const sL = hl - rc, tL = hw - rc, edge = [];
  const arc = (cs, ct, a0) => { for (let i = 0; i <= nC; i++) { const a = a0 + i / nC * Math.PI / 2; edge.push([cs + Math.cos(a) * rc, ct + Math.sin(a) * rc]); } };
  arc(sL, tL, 0); arc(-sL, tL, Math.PI / 2); arc(-sL, -tL, Math.PI); arc(sL, -tL, Math.PI * 1.5);
  const full = [];
  for (let i = 0; i < edge.length; i++) {
    const p = edge[i], q = edge[(i + 1) % edge.length]; full.push(p);
    if (Math.abs(q[0] - p[0]) > hl * 0.8) for (let k = 1; k < nS; k++) full.push([p[0] + (q[0] - p[0]) * k / nS, p[1] + (q[1] - p[1]) * k / nS]);
  }
  // кінці: «ялинка» (плашки вкладаються одна в одну) чи увігнута «кістка»
  const kS = Math.min(12, hl * 0.3);
  if (shape === 'chev' || shape === 'bone') for (const e of full) {
    const f = Math.max(0, 1 - Math.abs(e[1]) / hw), w = Math.abs(e[0]) / hl, k = kS * f * smooth(0.2, 1, w);
    e[0] += shape === 'chev' ? k : -Math.sign(e[0]) * k * 0.5;
  }
  const place = (ss, t, y) => { const c = atS(r, s.m + ss), k = (ss + hl) / (2 * hl), yy = s.y0 + (s.y1 - s.y0) * k + y; return [wx(c.x + c.nx * t), yy, wz(c.y + c.ny * t)]; };
  const cellH = 1 / rows, v0 = 1 - (rowIdx + 1) * cellH;
  const uvTop = (ss, t) => [clamp(0.5 + ss / (WMAX * 4), 0.02, 0.98), v0 + cellH * (0.5 + t / WMAX * 0.96)];
  const solid = [0.5, 1 - (rows - 0.5) * cellH], white = [1, 1, 1];
  const rim = new THREE.Color(look.plateRim), side = [rim.r, rim.g, rim.b];
  const push = (p, n, uv, col, top) => { out.pos.push(p[0], p[1], p[2]); out.nor.push(n[0], n[1], n[2]); out.uv.push(uv[0], uv[1]); out.col.push(col[0], col[1], col[2]); out.st.push(s.i, top); };
  const inset = Math.min(3.6, hw * 0.16);
  const TOP = full.map((e) => { const a = e[0] * (1 - inset / hl), b = e[1] * (1 - inset / hw); return { s: a, t: b, p: place(a, b, H0) }; });
  const BOT = full.map((e) => ({ p: place(e[0], e[1], -0.02) }));
  const cp = place(0, 0, H0), N = [0, 1, 0], uvc = uvTop(0, 0);
  for (let i = 0; i < TOP.length; i++) {
    const a = TOP[i], b = TOP[(i + 1) % TOP.length], flip = triNorm(cp, a.p, b.p)[1] < 0;
    if (flip) { push(cp, N, uvc, white, 1); push(b.p, N, uvTop(b.s, b.t), white, 1); push(a.p, N, uvTop(a.s, a.t), white, 1); }
    else { push(cp, N, uvc, white, 1); push(a.p, N, uvTop(a.s, a.t), white, 1); push(b.p, N, uvTop(b.s, b.t), white, 1); }
  }
  const Cx = wx(s.x), Cz = wz(s.y);
  for (let i = 0; i < TOP.length; i++) {
    const j = (i + 1) % TOP.length, A = TOP[i].p, B = TOP[j].p, C = BOT[i].p, D = BOT[j].p;
    let n = triNorm(A, B, C); const ox = (A[0] + B[0]) / 2 - Cx, oz = (A[2] + B[2]) / 2 - Cz;
    if (n[0] * ox + n[2] * oz < 0) { n = [-n[0], -n[1], -n[2]]; push(A, n, solid, side, 0); push(C, n, solid, side, 0); push(B, n, solid, side, 0); push(B, n, solid, side, 0); push(C, n, solid, side, 0); push(D, n, solid, side, 0); }
    else { push(A, n, solid, side, 0); push(B, n, solid, side, 0); push(C, n, solid, side, 0); push(B, n, solid, side, 0); push(D, n, solid, side, 0); push(C, n, solid, side, 0); }
  }
}
function bufGeom(o) {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(o.pos, 3));
  if (o.nor) g.setAttribute('normal', new THREE.Float32BufferAttribute(o.nor, 3));
  if (o.uv) g.setAttribute('uv', new THREE.Float32BufferAttribute(o.uv, 2));
  if (o.col) g.setAttribute('color', new THREE.Float32BufferAttribute(o.col, 3));
  if (o.st) g.setAttribute('aS', new THREE.Float32BufferAttribute(o.st, 2));
  return g;
}
/** Вихід на екран у власному ShaderMaterial — те саме перетворення тону й кольору, що й у стандартних. */
const OUT_GLSL = `#include <tonemapping_fragment>\n#include <colorspace_fragment>`;
const WAVE_GLSL = `
float waveA(vec2 p, float t){
  p += 0.22*vec2(sin(p.y*1.9+t*0.31), cos(p.x*1.6-t*0.27));
  float a = sin(p.x*3.3 + p.y*1.2 + t*0.62)*0.5+0.5;
  float b = sin(p.x*-2.1 + p.y*4.4 - t*0.47)*0.5+0.5;
  float c = sin((p.x+p.y)*1.1 + t*0.2)*0.5+0.5;
  return a*b*(0.55+0.45*c);
}
`;

// ---------------------------------------------------------------------------------------------------------- один вигляд
/** shared — { time, uState (текстура стану), uKinds (кольори підсвіток), uPulse }. */
export async function buildWorld(base, look, hi, renderer, shared) {
  const { geo, mesh, routes, slots, cities } = base, name = look.name;
  const group = new THREE.Group(), textures = [];
  const col = (h) => new THREE.Color(h);
  const tex = (cv, opt = {}) => { const t = new THREE.CanvasTexture(cv); t.colorSpace = THREE.SRGBColorSpace; t.generateMipmaps = true; t.minFilter = THREE.LinearMipmapLinearFilter; t.anisotropy = Math.min(renderer.capabilities.getMaxAnisotropy(), opt.aniso || 4); textures.push(t); return t; };
  const data = { routes, cities };
  await pause();
  const map = paintMap(look, name, geo, data, hi);
  await pause();
  const mapTex = tex(map, { aniso: hi ? 12 : 4 });
  const { OFF, CW, CH } = CANVAS_DOMAIN;
  const uvOf = (x, y) => [(x + OFF) / CW, 1 - (y + OFF) / CH];

  // земля + поле — один меш
  {
    const P = Array.from(mesh.pos), Nn = Array.from(mesh.nor), UV = Array.from(mesh.uv), IDX = mesh.idx.slice();
    const paper = look.frame === 'paper';
    const a0 = -PAD, b0 = W + PAD, c0 = -PAD, d0 = H + PAD, a1 = -PAD - MAT, b1 = W + PAD + MAT, c1 = -PAD - MAT, d1 = H + PAD + MAT;
    const NP = paper ? 260 : 4, TT = 2 * (b0 - a0) + 2 * (d0 - c0), rows = paper ? 4 : 2, v0 = P.length / 3;
    const on = (u, a, b, c, d) => { const w = b - a, h = d - c, p = u * (2 * w + 2 * h); if (p < w) return [a + p, c]; if (p < w + h) return [b, c + p - w]; if (p < 2 * w + h) return [b - (p - w - h), d]; return [a, d - (p - 2 * w - h)]; };
    const idxAt = (i, j) => v0 + ((i + NP) % NP) * rows + j;
    for (let i = 0; i < NP; i++) {
      const u = paper ? i / NP : [0, (b0 - a0) / TT, ((b0 - a0) + (d0 - c0)) / TT, (2 * (b0 - a0) + (d0 - c0)) / TT][i];
      const [xi, yi] = on(u, a0, b0, c0, d0);
      for (let j = 0; j < rows; j++) {
        const f = j / (rows - 1);
        const [xo, yo] = paper ? on(u, a1, b1, c1, d1) : (i === 0 ? [a1, c1] : i === 1 ? [b1, c1] : i === 2 ? [b1, d1] : [a1, d1]);
        let x = xi + (xo - xi) * f, y = yi + (yo - yi) * f, yy = LV.mat;
        if (paper && j > 0) {
          // рваний край і підкручені кути
          const nn = (Math.sin(u * 91) + Math.sin(u * 213 + 1) * 0.6 + Math.sin(u * 511 + 3) * 0.3) * 13 * f * f;
          const ln = Math.hypot(xo - xi, yo - yi) || 1; x += (xo - xi) / ln * nn; y += (yo - yi) / ln * nn;
          const cd = Math.min(Math.hypot(x - a1, y - c1), Math.hypot(x - b1, y - c1), Math.hypot(x - b1, y - d1), Math.hypot(x - a1, y - d1));
          yy += 0.1 * Math.pow(smooth(420, 40, cd), 1.6) * f + 0.006 * Math.sin(u * 77) * f;
        }
        P.push(wx(x), yy, wz(y)); Nn.push(0, 1, 0); const q = uvOf(x, y); UV.push(q[0], q[1]);
      }
    }
    const stIdx = IDX.length;
    for (let i = 0; i < NP; i++) for (let j = 0; j < rows - 1; j++) { const a = idxAt(i, j), b = idxAt(i + 1, j), c = idxAt(i + 1, j + 1), d = idxAt(i, j + 1); IDX.push(a, b, c, a, c, d); }
    if (paper) {
      const t = new Float32Array(NP * rows * 3);
      for (let k = stIdx; k < IDX.length; k += 3) { const A = IDX[k], B = IDX[k + 1], C = IDX[k + 2]; const n = triNorm([P[3 * A], P[3 * A + 1], P[3 * A + 2]], [P[3 * B], P[3 * B + 1], P[3 * B + 2]], [P[3 * C], P[3 * C + 1], P[3 * C + 2]]); for (const V of [A, B, C]) { const o = V - v0; t[3 * o] += n[0]; t[3 * o + 1] += n[1]; t[3 * o + 2] += n[2]; } }
      for (let o = 0; o < NP * rows; o++) { const l = Math.hypot(t[3 * o], t[3 * o + 1], t[3 * o + 2]) || 1; Nn[3 * (v0 + o)] = t[3 * o] / l; Nn[3 * (v0 + o) + 1] = t[3 * o + 1] / l; Nn[3 * (v0 + o) + 2] = t[3 * o + 2] / l; }
    }
    // внутрішня стінка поля, щоб не було щілини
    const wall = [[a0, c0], [b0, c0], [b0, d0], [a0, d0]];
    for (let i = 0; i < 4; i++) {
      const p = wall[i], q = wall[(i + 1) % 4], v = P.length / 3, n = [[0, 0, 1], [-1, 0, 0], [0, 0, -1], [1, 0, 0]][i];
      for (const [x, y, yy] of [[p[0], p[1], LV.mat], [q[0], q[1], LV.mat], [q[0], q[1], LV.bed - 0.02], [p[0], p[1], LV.bed - 0.02]]) { P.push(wx(x), yy, wz(y)); Nn.push(n[0], n[1], n[2]); const u = uvOf(x + n[0] * 3, y + n[2] * 3); UV.push(u[0], u[1]); }
      IDX.push(v, v + 2, v + 1, v, v + 3, v + 2);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3)); g.setAttribute('normal', new THREE.Float32BufferAttribute(Nn, 3)); g.setAttribute('uv', new THREE.Float32BufferAttribute(UV, 2)); g.setIndex(IDX);
    const land = new THREE.Mesh(g, new THREE.MeshLambertMaterial({ map: mapTex })); land.receiveShadow = hi; group.add(land);
  }

  // море: площина з картини + м'яке мерехтіння (лише high)
  {
    const m = new THREE.MeshLambertMaterial({ map: mapTex });
    if (hi) {
      const gl = col(look.seaGlint);
      m.onBeforeCompile = (sh) => {
        sh.uniforms.uT = shared.time; sh.uniforms.uG = { value: gl }; sh.uniforms.uK = { value: look.seaGlintK }; sh.uniforms.uD = { value: look.seaDark };
        sh.vertexShader = 'varying vec2 vW;\n' + sh.vertexShader.replace('#include <begin_vertex>', '#include <begin_vertex>\n vW = position.xz;');
        sh.fragmentShader = 'uniform float uT,uK,uD; uniform vec3 uG; varying vec2 vW;\n' + WAVE_GLSL + sh.fragmentShader.replace('#include <map_fragment>', `#include <map_fragment>
          float w1 = waveA(vW*1.15, uT);
          float brk = smoothstep(0.42,0.82, waveA(vW*0.9+vec2(4.0,1.0), uT*0.8));
          float line = sin(vW.y*52. + 2.6*sin(vW.x*3.3+uT*0.33) + uT*0.75);
          float gl = smoothstep(0.86,1.0,line)*brk;
          diffuseColor.rgb *= 1.0 + uD*(w1*2.0-0.6);
          diffuseColor.rgb += uG * uK * gl;`);
      };
    }
    const a = -PAD, b = W + PAD, c = -PAD, d = H + PAD, g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute([wx(a), LV.sea, wz(c), wx(b), LV.sea, wz(c), wx(b), LV.sea, wz(d), wx(a), LV.sea, wz(d)], 3));
    g.setAttribute('normal', new THREE.Float32BufferAttribute([0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0], 3));
    g.setAttribute('uv', new THREE.Float32BufferAttribute([...uvOf(a, c), ...uvOf(b, c), ...uvOf(b, d), ...uvOf(a, d)], 2));
    g.setIndex([0, 2, 1, 0, 3, 2]);
    const sea = new THREE.Mesh(g, m); sea.receiveShadow = hi; group.add(sea);
  }

  // річки: стрічка в прорізаному руслі — вода й м'який схил берега; край гладкий, бо його задає стрічка, а не сітка землі
  {
    const P = [], A = [], I = [], k = 2, ROWS = [-1.4, -1, -0.45, 0.45, 1, 1.4], nr = ROWS.length;
    for (const rv of geo.rivers) {
      const Q = rv.pts; if (Q.length < 3) continue;
      let along = 0, prevIdx = -1, prevI = 0; const n = Q.length;
      for (let i = 0; i < n; i += k) {
        const a = Q[Math.max(0, i - 2)], b = Q[Math.min(n - 1, i + 2)], dx = b[0] - a[0], dy = b[1] - a[1], l = Math.hypot(dx, dy) || 1, nx = -dy / l, ny = dx / l;
        if (i > 0) along += Math.hypot(Q[i][0] - Q[prevI][0], Q[i][1] - Q[prevI][1]);
        prevI = i;
        const tap = Math.min(1, 0.42 + i / Math.max(1, n) * 3.2), hw = rv.w * 0.92 * tap;
        const hc = heightAt(geo, Q[i][0], Q[i][1]), dep = 0.013 + rv.w * 0.0003, yw = Math.max(LV.sea + 0.0015, hc - dep * 0.6), v = P.length / 3;
        for (const f of ROWS) {
          const af = Math.abs(f), X = Q[i][0] + nx * hw * f, Y = Q[i][1] + ny * hw * f, yy = af > 1.2 ? Math.max(yw, heightAt(geo, X, Y) + 0.0016) : yw;
          P.push(wx(X), yy, wz(Y)); A.push(along * U, f, rv.w);
        }
        if (prevIdx >= 0) for (let e = 0; e < nr - 1; e++) I.push(prevIdx + e, prevIdx + e + 1, v + e, prevIdx + e + 1, v + e + 1, v + e);
        prevIdx = v;
      }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3)); g.setAttribute('aS', new THREE.Float32BufferAttribute(A, 3)); g.setIndex(I);
    const m = new THREE.ShaderMaterial({
      transparent: true, side: THREE.DoubleSide, depthWrite: false,
      uniforms: { uT: shared.time, uA: { value: col(look.river.a) }, uB: { value: col(look.river.b) }, uG: { value: col(look.river.glint) }, uBank: { value: col(look.river.bank) }, uL: { value: look.tone === 'aces' ? 0.95 : 1.0 } },
      vertexShader: `attribute vec3 aS; varying vec3 vS; void main(){ vS=aS; gl_Position=projectionMatrix*modelViewMatrix*vec4(position,1.); }`,
      fragmentShader: `uniform float uT,uL; uniform vec3 uA,uB,uG,uBank; varying vec3 vS;
        void main(){ float c=abs(vS.y), s=vS.x;
          vec3 col=mix(uA,uB,smoothstep(0.15,1.0,c));
          float f=sin(s*34.-uT*1.15+sin(s*7.+vS.y*2.)*1.6)*0.5+0.5; float h=sin(s*13.-uT*0.7+vS.y*3.)*0.5+0.5;
          col+=uG*(smoothstep(0.76,0.97,f*h+0.12)*0.3*(1.0-c*0.7));
          float bank=smoothstep(0.95,1.03,c), side=vS.y>0.?0.72:1.0;
          col=mix(col,uBank*side,bank);
          col=mix(col,uBank*0.45,smoothstep(0.0,0.08,c-0.92)*(1.-smoothstep(0.08,0.2,c-0.92))*0.5);
          float a=1.0-smoothstep(1.03,1.4,c);
          gl_FragColor=vec4(col*uL,a);
          ${OUT_GLSL}
        }`,
    });
    const river = new THREE.Mesh(g, m); river.renderOrder = 1; group.add(river);
  }

  // стіл і рамка
  const woodTex = tex(paintWood(look, hi ? 1024 : 512)); woodTex.wrapS = woodTex.wrapT = THREE.MirroredRepeatWrapping;
  const tableY = look.frame === 'paper' ? -0.075 : -0.24;
  {
    const P = [], Nn = [], Cc = [], Uv = [], I = [], rings = 8, seg = 40, R = 42;
    for (let j = 0; j <= rings; j++) { const rr = R * Math.pow(j / rings, 1.5); for (let i = 0; i < seg; i++) { const a = i / seg * 6.2832, x = Math.cos(a) * rr * 1.25, z = Math.sin(a) * rr; P.push(x, tableY, z); Nn.push(0, 1, 0); Uv.push((x + 3.1) / 7.5, (z + 1.3) / 7.5); const dd = Math.hypot(x / 1.5, z), f = look.tableLight * (0.28 + 0.72 * Math.pow(smooth(18, 4, dd), 1.2)); Cc.push(f, f, f); } }
    for (let j = 0; j < rings; j++) for (let i = 0; i < seg; i++) { const a = j * seg + i, b = j * seg + (i + 1) % seg, c = (j + 1) * seg + i, d = (j + 1) * seg + (i + 1) % seg; I.push(a, b, c, b, d, c); }
    const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3)); g.setAttribute('normal', new THREE.Float32BufferAttribute(Nn, 3)); g.setAttribute('color', new THREE.Float32BufferAttribute(Cc, 3)); g.setAttribute('uv', new THREE.Float32BufferAttribute(Uv, 2)); g.setIndex(I);
    const table = new THREE.Mesh(g, new THREE.MeshLambertMaterial({ map: woodTex, vertexColors: true })); table.receiveShadow = hi; group.add(table);
  }
  {
    const OUTER = PAD + MAT, fx0 = wx(-OUTER), fx1 = wx(W + OUTER), fz0 = wz(-OUTER), fz1 = wz(H + OUTER);
    const o = { pos: [], nor: [], uv: [], col: [] }, white = col('#ffffff'), cA = col(look.frameCol), cB = col(look.frameCol2);
    const light = look.frame === 'lacquer' ? cA.clone() : cA.clone().lerp(white, 0.14), dark = cA.clone().multiplyScalar(0.72);
    const prof = look.frame === 'paper' ? [[-0.02, LV.mat - 0.012, cA], [0.0, LV.mat - 0.012, cA], [0.035, -0.002, cA], [0.055, tableY, dark]]
      : look.frame === 'lacquer' ? [[-0.01, LV.mat + 0.001, cA], [0.015, LV.mat + 0.06, cA], [0.04, LV.mat + 0.075, cB], [0.075, LV.mat + 0.075, cB], [0.09, LV.mat + 0.082, light], [0.21, LV.mat + 0.082, light], [0.245, LV.mat + 0.058, cA], [0.255, tableY, dark]]
        : [[-0.01, LV.mat + 0.001, dark], [0.02, LV.mat + 0.052, cA], [0.055, LV.mat + 0.062, cB], [0.085, LV.mat + 0.062, cB], [0.095, LV.mat + 0.082, white.clone().multiplyScalar(0.95)], [0.22, LV.mat + 0.088, white], [0.26, LV.mat + 0.056, white.clone().multiplyScalar(0.86)], [0.27, tableY, white.clone().multiplyScalar(0.62)]];
    const corners = [[fx0, fz1, -1, 1], [fx1, fz1, 1, 1], [fx1, fz0, 1, -1], [fx0, fz0, -1, -1]], uvS = 1 / 7.5;
    for (let s = 0; s < 4; s++) {
      const a = corners[s], b = corners[(s + 1) % 4], horizontal = s % 2 === 0;
      for (let k = 0; k < prof.length - 1; k++) {
        const [o0, y0, col0] = prof[k], [o1, y1, col1] = prof[k + 1];
        const pa0 = [a[0] + a[2] * o0, y0, a[1] + a[3] * o0], pb0 = [b[0] + b[2] * o0, y0, b[1] + b[3] * o0], pa1 = [a[0] + a[2] * o1, y1, a[1] + a[3] * o1], pb1 = [b[0] + b[2] * o1, y1, b[1] + b[3] * o1];
        const n = triNorm(pa0, pa1, pb0), up = (n[1] > 0 || (Math.abs(n[1]) < 1e-3 && (n[0] * pa1[0] + n[2] * pa1[2] > 0))), N = up ? n : [-n[0], -n[1], -n[2]];
        const tri = (A, B, C, cc) => { const m = triNorm(A, B, C), good = m[0] * N[0] + m[1] * N[1] + m[2] * N[2] > 0; for (const V of good ? [A, B, C] : [A, C, B]) { const q = cc[V === A ? 0 : V === B ? 1 : 2]; o.pos.push(V[0], V[1], V[2]); o.nor.push(N[0], N[1], N[2]); o.col.push(q.r, q.g, q.b); o.uv.push(horizontal ? V[0] * uvS : V[2] * uvS + 0.37, horizontal ? (V[2] + V[1]) * uvS + s * 0.2 : (V[0] + V[1]) * uvS + s * 0.2); } };
        tri(pa0, pb0, pa1, [col0, col0, col1]); tri(pb0, pb1, pa1, [col0, col1, col1]);
      }
    }
    const m = look.frame === 'lacquer' ? new THREE.MeshPhongMaterial({ vertexColors: true, specular: col('#6a5a44'), shininess: 60 }) : new THREE.MeshLambertMaterial({ vertexColors: true, map: look.frame === 'wood' ? woodTex : null });
    const fr = new THREE.Mesh(bufGeom(o), m); fr.receiveShadow = hi; fr.castShadow = hi; group.add(fr);
  }
  await pause();

  // плашки: один меш на всю мапу; колір власника, приглушення й підйом — із текстури стану
  const atlasTex = tex(paintAtlas(look, hi), { aniso: 8 });
  const rowOf = Object.fromEntries(ATLAS.order.map((c, i) => [c, i])), rows = ATLAS.order.length + 1;
  const plOut = { pos: [], nor: [], uv: [], col: [], st: [] };
  for (const r of routes) for (const s of r.slots) writePlate(plOut, r, s, look, hi, rowOf[r.cargo], rows);
  const plateMat = new THREE.MeshLambertMaterial({ map: atlasTex, vertexColors: true });
  plateMat.onBeforeCompile = (sh) => {
    sh.uniforms.uState = shared.uState;
    sh.vertexShader = `attribute vec2 aS; uniform sampler2D uState; varying vec4 vBase; varying float vDim, vTop, vFlash;\n` + sh.vertexShader.replace('#include <begin_vertex>', `#include <begin_vertex>
      vec4 st = texelFetch(uState, ivec2(int(aS.x), 0), 0);
      vBase = texelFetch(uState, ivec2(int(aS.x), 1), 0); vDim = st.a; vTop = aS.y;
      vFlash = abs(st.r*255.-8.) < 0.5 ? st.g : 0.;
      transformed.y += st.b * 0.05;`);
    sh.fragmentShader = `varying vec4 vBase; varying float vDim, vTop, vFlash;\n` + sh.fragmentShader.replace('#include <color_fragment>', `#include <color_fragment>
      if (vTop > 0.5) diffuseColor.rgb = mix(diffuseColor.rgb, vBase.rgb, vBase.a);
      { float g = dot(diffuseColor.rgb, vec3(0.3,0.59,0.11)); diffuseColor.rgb = mix(diffuseColor.rgb, vec3(g)*0.55 + vec3(0.06), vDim); }
      diffuseColor.rgb = mix(diffuseColor.rgb, vec3(1.,0.97,0.82), vFlash*0.75);`);
  };
  const plates = new THREE.Mesh(bufGeom(plOut), plateMat); plates.receiveShadow = hi; group.add(plates);

  // значки: квадрат поверх плашки, завжди рівно (північ — угору); зайнятий шлях їх ховає (вози зверху)
  const iconTex = tex(paintIcons(look, hi), { aniso: 8 });
  {
    const o = { pos: [], uv: [], st: [] };
    for (const r of routes) for (const s of r.slots) {
      const cell = rowOf[r.cargo], u0 = (cell % 3) / 3, v0 = 1 - ((cell / 3 | 0) + 1) / 3, d = 1 / 3, h = s.w * 0.5 * 1.04, y = Math.max(s.y0, s.y1) + 0.0155, x = wx(s.x), z = wz(s.y), q = h * U;
      for (const [dx, dz, a, b] of [[-1, 1, 0, 0], [1, 1, 1, 0], [1, -1, 1, 1], [-1, 1, 0, 0], [1, -1, 1, 1], [-1, -1, 0, 1]]) { o.pos.push(x + dx * q, y, z + dz * q); o.uv.push(u0 + a * d, v0 + b * d); o.st.push(s.i, 1); }
    }
    const m = new THREE.ShaderMaterial({
      transparent: true, depthWrite: false, toneMapped: false,
      uniforms: { uState: shared.uState, uMap: { value: iconTex }, uL: { value: look.tone === 'aces' ? 0.94 : 1 } },
      vertexShader: `attribute vec2 aS; uniform sampler2D uState; varying vec2 vU; varying float vDim;
        void main(){ vec4 st = texelFetch(uState, ivec2(int(aS.x),0), 0), bs = texelFetch(uState, ivec2(int(aS.x),1), 0);
          vU = uv; vDim = st.a; vec3 p = position; p.y += st.b*0.05;
          gl_Position = bs.a > 0.5 ? vec4(2.,2.,2.,1.) : projectionMatrix*modelViewMatrix*vec4(p,1.); }`,
      fragmentShader: `uniform sampler2D uMap; uniform float uL; varying vec2 vU; varying float vDim;
        void main(){ vec4 c = texture2D(uMap, vU); if (c.a < 0.02) discard; c.rgb *= uL; float g = dot(c.rgb, vec3(0.3,0.59,0.11)); c.rgb = mix(c.rgb, vec3(g)*0.55+vec3(0.06), vDim); c.a *= 1.0 - vDim*0.6; gl_FragColor = c;
          #include <colorspace_fragment>
        }`,
    });
    const icons = new THREE.Mesh(bufGeom(o), m); icons.renderOrder = 2; group.add(icons);
  }

  // підсвітка: стрічка навколо кожної плашки; показує лише те, що просить текстура стану
  const glow = (() => {
    const P = [], R = [], ST = [], I = [], m = 18, n = 4;
    for (const r of routes) for (const s of r.slots) {
      const hl = s.len / 2 + m, hw = s.w / 2 + m, v0 = P.length / 3, y = (s.y0 + s.y1) / 2 + 0.009;
      for (let i = 0; i <= n; i++) for (const sg of [-1, 1]) {
        const ss = -hl + 2 * hl * i / n, c = atS(r, s.m + ss), t = sg * hw;
        P.push(wx(c.x + c.nx * t), y, wz(c.y + c.ny * t)); R.push(ss, t, s.len / 2, s.w / 2); ST.push(s.i, 0);
      }
      for (let i = 0; i < n; i++) { const a = v0 + i * 2; I.push(a, a + 1, a + 2, a + 1, a + 3, a + 2); }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3)); g.setAttribute('aR', new THREE.Float32BufferAttribute(R, 4)); g.setAttribute('aS', new THREE.Float32BufferAttribute(ST, 2)); g.setIndex(I);
    const mat = new THREE.ShaderMaterial({
      transparent: true, depthWrite: false, side: THREE.DoubleSide, toneMapped: false,
      uniforms: { uState: shared.uState, uKinds: shared.uKinds, uPulse: shared.uPulse },
      vertexShader: `attribute vec4 aR; attribute vec2 aS; uniform sampler2D uState; uniform vec3 uKinds[10]; varying vec4 vR; varying vec3 vC; varying float vA, vKind;
        void main(){ vec4 st = texelFetch(uState, ivec2(int(aS.x),0), 0); vKind = floor(st.r*255.+.5); vC = uKinds[int(vKind)]; vA = st.g; vR = aR;
          vec3 p = position; p.y += st.b*0.05;
          gl_Position = vA < 0.004 ? vec4(2.,2.,2.,1.) : projectionMatrix*modelViewMatrix*vec4(p,1.); }`,
      fragmentShader: `uniform float uPulse; varying vec4 vR; varying vec3 vC; varying float vA, vKind;
        void main(){ vec2 d = abs(vR.xy)-vR.zw; float o = length(max(d,0.)), a = 1.-clamp(o/18.,0.,1.); a = a*a*0.85;
          float line = (1.-smoothstep(4.5,6.5,o));
          float dash = 1.;
          if (vKind > 2.5 && vKind < 3.5) { dash = step(0.46, fract(vR.x/26.+0.5)); a *= 0.2; }
          float pulse = (vKind > 0.5 && vKind < 2.5) || vKind > 8.5 ? 0.6+0.4*uPulse : 1.;
          gl_FragColor = vec4(vC, max(a*pulse, line*dash)*vA);
          #include <colorspace_fragment>
        }`,
    });
    const mesh2 = new THREE.Mesh(g, mat); mesh2.renderOrder = 3; mesh2.visible = false; group.add(mesh2);
    return mesh2;
  })();
  await pause();

  // поселення й декор — один меш
  const town = new M.Builder(21, 0.05), chimneys = [], TP = look.town, rndTown = rng(7001), HUT = 25 * U;
  for (const c of cities) {
    const x = wx(c.x), z = wz(c.y), y = c.h, R = c.r * 0.94 * U, big = c.lvl === 1, y1 = y + 0.012;
    const seg = hi ? 22 : 14;
    town.m.identity();
    if (look.cityMark === 'flower') {
      town.j = 0;
      M.flowerMark(town, x, y1, z, R, TP, seg);
      town.j = 0.05;
      continue;
    }
    const rimC = col(TP.plazaRim), plaza = col(big ? TP.plazaBig : TP.plaza);
    town.j = 0;
    town.frustum(x, y - 0.03, z, R * 1.04, y1 - 0.002, R, seg, rimC.clone().multiplyScalar(0.8));
    town.frustum(x, y1 - 0.002, z, R, y1, R * 0.88, seg, rimC);
    town.frustum(x, y1, z, R * 0.88, y1 - 0.0002, 0.0001, seg, plaza);
    town.j = 0.05;
    const hs0 = clamp(c.r * 0.42, 11, 21) * U, n = hi ? (c.r >= 52 ? 5 : c.r >= 36 ? 3 : 2) : (c.r >= 52 ? 3 : 2), rin = R * 0.6;
    for (let i = 0; i < n; i++) {
      const a = i / n * 6.283 + rndTown() * 0.6 + c.x, d = rin * (0.9 + rndTown() * 0.16), hs = hs0 * (0.88 + rndTown() * 0.22);
      const hx = x + Math.cos(a) * d, hz = z + Math.sin(a) * d, face = -a + Math.PI / 2 + (rndTown() - 0.5) * 0.4, chim = i % 2 === 0;
      M.hut(town, hx, y1 + 0.003, hz, Math.round(face / (Math.PI / 2)) * Math.PI / 2 + (rndTown() - 0.5) * 0.35, hs, TP, { chim, detail: hi });
      if (chim && (c.r >= 36 || i === 0)) chimneys.push([hx, y1 + hs, hz, rndTown()]);
    }
    const s = hs0 * 1.5, Y = y1 + 0.003;
    if (c.slug === 'kyiv') M.church(town, x, Y, z, s * 1.1, TP, { gold: true });
    else if (c.slug === 'lviv') M.tower(town, x, Y, z, s * 1.08, TP);
    else if (c.slug === 'odesa') M.lighthouse(town, x, Y, z, s * 1.05, TP);
    else if (c.slug === 'kamianets') M.fortress(town, x, Y, z, s * 0.86, TP);
    else if (c.slug === 'perekop') M.saltHeap(town, x, Y, z, s * 0.9, TP);
    else M.church(town, x, Y, z, s * (big ? 0.98 : 0.8), TP);
  }
  const dr = rng(8123);
  const freeForDecor = (mx, my, dR, dC) => { if (geo.routeDist[geo.pxOf(mx, my)] / geo.ms < dR) return false; for (const c of cities) if (Math.hypot(c.x - mx, c.y - my) < c.r + dC) return false; return true; };
  let mills = 0, kurg = 0, salt = 0;
  if (look.cityMark !== 'flower') {
    for (let i = 0; i < 2600; i++) {
      const mx = dr() * W, my = dr() * H; if (geo.maskAt(mx, my) !== 1 || geo.bil(geo.sU, mx, my) < 26) continue;
      const y = heightAt(geo, mx, my);
      if (my > 1000 && mills < (hi ? 16 : 8) && freeForDecor(mx, my, 56, 46)) { M.windmill(town, wx(mx), y, wz(my), HUT * 1.05, TP, 0.3 + dr() * 0.4 - 0.3); mills++; continue; }
      if (my > 1300 && kurg < (hi ? 8 : 4) && freeForDecor(mx, my, 56, 40)) { M.kurgan(town, wx(mx), y, wz(my), HUT * 0.82, look.kurgan, TP); kurg++; }
    }
    const pk = cities.find((q) => q.slug === 'perekop');
    for (let i = 0; i < 800 && salt < 7; i++) {
      const a = dr() * 6.28, rr = pk.r + 20 + dr() * 80, mx = pk.x + Math.cos(a) * rr, my = pk.y + Math.sin(a) * rr;
      if (geo.maskAt(mx, my) === 2 || !freeForDecor(mx, my, 40, 8)) continue;
      M.saltHeap(town, wx(mx), heightAt(geo, mx, my), wz(my), HUT * (0.42 + dr() * 0.22), TP); salt++;
    }
  }
  const townMesh = new THREE.Mesh(town.geometry(), new THREE.MeshLambertMaterial({ vertexColors: true })); townMesh.castShadow = hi; townMesh.receiveShadow = hi; group.add(townMesh);

  // дерева
  {
    const tg = M.treeGeometry(look.trees.kind, hi), cap = look.trees.cap[hi ? 0 : 1], pts = [], tr = rng(1234);
    const CA = col(look.trees.a), CB = col(look.trees.b), CC = col(look.trees.c), grid = new Map(), G = 18;
    for (let i = 0; i < cap * 40 && pts.length < cap; i++) {
      const mx = tr() * W, my = tr() * H, f = forestAt(geo, mx, my);
      if (f < 0.3 || tr() > f) continue;
      if (geo.routeDist[geo.pxOf(mx, my)] / geo.ms < 44 || geo.riverDist[geo.pxOf(mx, my)] / geo.ms < 16 || geo.bil(geo.sU, mx, my) < 16) continue;
      const k = (mx / G | 0) + ',' + (my / G | 0); if (grid.has(k)) continue; grid.set(k, 1);
      let ok = true; for (const c of cities) if (Math.hypot(c.x - mx, c.y - my) < c.r + 28) { ok = false; break; } if (!ok) continue;
      pts.push([mx, my]);
    }
    const inst = new THREE.InstancedMesh(tg, new THREE.MeshLambertMaterial({ vertexColors: true }), Math.max(1, pts.length));
    const m4 = new THREE.Matrix4(), q = new THREE.Quaternion(), UP = new THREE.Vector3(0, 1, 0);
    pts.forEach((p, i) => {
      const s = (19 + tr() * 13) * U * (name === 'board' ? 1 : 0.96), y = heightAt(geo, p[0], p[1]) - 0.003;
      m4.compose(new THREE.Vector3(wx(p[0]), y, wz(p[1])), q.setFromAxisAngle(UP, tr() * 6.28), new THREE.Vector3(s, s * (0.9 + tr() * 0.3), s));
      inst.setMatrixAt(i, m4);
      const t0 = tr(); inst.setColorAt(i, t0 < 0.5 ? CA.clone().lerp(CC, t0 * 2) : CA.clone().lerp(CB, (t0 - 0.5) * 2));
    });
    inst.count = pts.length; inst.castShadow = hi; inst.frustumCulled = false; group.add(inst);
  }

  // дим — лише фон high
  let smoke = null;
  if (hi && chimneys.length) {
    const puffs = 3, use = chimneys.slice(0, 46), n = use.length * puffs, g = new THREE.InstancedBufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0], 3)); g.setIndex([0, 1, 2, 0, 2, 3]);
    const at = new Float32Array(n * 4); let k = 0;
    for (const c of use) for (let i = 0; i < puffs; i++) { at[k++] = c[0]; at[k++] = c[1]; at[k++] = c[2]; at[k++] = (c[3] + i / puffs) % 1; }
    g.setAttribute('aP', new THREE.InstancedBufferAttribute(at, 4)); g.instanceCount = n;
    const m = new THREE.ShaderMaterial({
      transparent: true, depthWrite: false,
      uniforms: { uT: shared.time, uC: { value: col(look.smoke) } },
      vertexShader: `attribute vec4 aP; uniform float uT; varying vec2 vU; varying float vA;
        void main(){ float t=fract(aP.w+uT*0.075); float s=0.012+t*0.05;
          vec3 c=aP.xyz; c.y+=t*0.26; c.x+=sin(t*5.+aP.w*20.)*0.018*t+t*0.05; c.z+=cos(t*4.+aP.w*11.)*0.012*t;
          vec4 mv=modelViewMatrix*vec4(c,1.); mv.xy+=position.xy*s; vU=position.xy; vA=smoothstep(0.,0.12,t)*(1.-t)*(1.-t); gl_Position=projectionMatrix*mv; }`,
      fragmentShader: `uniform vec3 uC; varying vec2 vU; varying float vA; void main(){ float d=dot(vU,vU); if(d>1.)discard; float a=(1.-d); a*=a; gl_FragColor=vec4(uC,a*vA*0.55);
        ${OUT_GLSL}
      }`,
    });
    smoke = new THREE.Mesh(g, m); smoke.frustumCulled = false; smoke.renderOrder = 5; group.add(smoke);
  }
  await pause();

  // вози: близькі (нейтральна частина + кузов за місцем) і далекі (усе в одному меші, кузов фарбує атрибут aTint)
  const CAP = 270, CG = M.cartGeometries(hi, look.cart), lam = () => new THREE.MeshLambertMaterial({ vertexColors: true });
  const inst = (g, n, cast) => { const m = new THREE.InstancedMesh(g, lam(), n); m.count = 0; m.castShadow = cast; m.frustumCulled = false; group.add(m); return m; };
  // тінь кидає лише кузов: колеса й воли — це половина трикутників воза, а тіні від них зверху не видно
  const neutral = inst(CG.neutral, CAP, false), bodies = CG.bodies.map((g) => { const m = inst(g, 60, hi); m.setColorAt(0, col('#ffffff')); return m; });
  const farMat = lam();
  farMat.onBeforeCompile = (sh) => {
    // кузов — колір місця (instanceColor), колеса й воли — свої
    sh.vertexShader = 'attribute float aTint;\n' + sh.vertexShader.replace('#include <color_vertex>', `#include <color_vertex>
      #ifdef USE_INSTANCING_COLOR
      vColor.rgb = color.rgb * mix(vec3(1.), instanceColor.rgb, aTint);
      #endif`);
  };
  const far = new THREE.InstancedMesh(M.cartFar(look.cart), farMat, CAP); far.count = 0; far.castShadow = hi; far.frustumCulled = false; far.setColorAt(0, col('#ffffff')); group.add(far);

  // фігури місць над середнім возом шляху
  const badgeTex = tex(paintSeatBadges(look.players));
  const badgeG = new THREE.InstancedBufferGeometry();
  badgeG.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0], 3)); badgeG.setIndex([0, 1, 2, 0, 2, 3]);
  badgeG.setAttribute('aC', new THREE.InstancedBufferAttribute(new Float32Array(102 * 4), 4)); badgeG.instanceCount = 0;
  const badgeMat = new THREE.ShaderMaterial({
    transparent: true, depthTest: false, depthWrite: false, toneMapped: false,
    uniforms: { uMap: { value: badgeTex }, uPx: shared.uBadgePx, uRes: shared.uRes },
    vertexShader: `attribute vec4 aC; uniform float uPx; uniform vec2 uRes; varying vec2 vU; varying float vCell;
      void main(){ vec4 c = projectionMatrix*modelViewMatrix*vec4(aC.xyz,1.); c.xy += position.xy*uPx*2./uRes*c.w; gl_Position = c; vU = position.xy*.5+.5; vCell = aC.w; }`,
    fragmentShader: `uniform sampler2D uMap; varying vec2 vU; varying float vCell; void main(){ vec4 c = texture2D(uMap, vec2((vCell+vU.x)/5., vU.y)); if (c.a < 0.03) discard; gl_FragColor = c;
      #include <colorspace_fragment>
    }`,
  });
  const badges = new THREE.Mesh(badgeG, badgeMat); badges.frustumCulled = false; badges.renderOrder = 8; group.add(badges);

  // кільця міст: замовлення, дозволені міста, курсор
  const ringG = (() => {
    const P = [], Cc = [], I = [], seg = hi ? 40 : 24, band = (r0, r1, c) => { const v = P.length / 3; for (let i = 0; i <= seg; i++) { const a = i / seg * 6.2832; P.push(Math.cos(a) * r0, 0, Math.sin(a) * r0, Math.cos(a) * r1, 0, Math.sin(a) * r1); Cc.push(c, c, c, c, c, c); } for (let i = 0; i < seg; i++) { const q = v + i * 2; I.push(q, q + 2, q + 1, q + 1, q + 2, q + 3); } };
    band(0.84, 1, 1); band(1, 1.06, 0.12);
    const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3)); g.setAttribute('color', new THREE.Float32BufferAttribute(Cc, 3)); g.setIndex(I); return g;
  })();
  const rings = new THREE.InstancedMesh(ringG, new THREE.MeshBasicMaterial({ vertexColors: true, toneMapped: false, side: THREE.DoubleSide }), 64);
  rings.count = 0; rings.frustumCulled = false; rings.renderOrder = 4; rings.setColorAt(0, col('#ffffff')); group.add(rings);

  function dispose() {
    group.traverse((o) => {
      o.geometry?.dispose();
      if (o.material) o.material.dispose();
      if (o.isInstancedMesh) o.dispose();
    });
    textures.forEach((t) => t.dispose());
    map.width = map.height = 0;
  }
  return { group, look, plates, glow, far, neutral, bodies, badges, badgeG, rings, smoke, dispose };
}
