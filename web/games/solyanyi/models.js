// «Соляний шлях» — процедурні low-poly моделі: хати, церкви, вітряки, вози, воли, дерева, мітки міст.
// Усе нерухоме зливається в один меш (Builder) із пласким затіненням і кольором вершин; вози й дерева — InstancedMesh.
// Віз має два рівні: близький (cartGeometries, п'ять різних кузовів) і далекий (cartFar, ~50 трикутників: 180 возів — 9 тис.).
import * as THREE from '/static/vendor/three-0.186.1/three.module.js';

const C = (h) => new THREE.Color(h);
const _c = new THREE.Color();
export class Builder {
  constructor(seed = 1, jitter = 0.04) { this.p = []; this.n = []; this.c = []; this.uv = null; this.r = seed >>> 0; this.j = jitter; this.m = new THREE.Matrix4(); this._v = new THREE.Vector3(); }
  rnd() { this.r = (this.r + 0x6D2B79F5) >>> 0; let t = this.r; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }
  at(x, y, z, ry = 0, s = 1) { this.m.compose(new THREE.Vector3(x, y, z), new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 1, 0), ry), new THREE.Vector3(s, s, s)); return this; }
  xf(a) { this._v.set(a[0], a[1], a[2]).applyMatrix4(this.m); return [this._v.x, this._v.y, this._v.z]; }
  /** Трикутник у локальних координатах; пласка нормаль; a,b,c проти годинникової, якщо дивитись ззовні. */
  tri(a, b, c, col, noJit) {
    const A = this.xf(a), B = this.xf(b), D = this.xf(c);
    const ux = B[0] - A[0], uy = B[1] - A[1], uz = B[2] - A[2], vx = D[0] - A[0], vy = D[1] - A[1], vz = D[2] - A[2];
    let nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx; const l = Math.hypot(nx, ny, nz) || 1; nx /= l; ny /= l; nz /= l;
    this.p.push(...A, ...B, ...D); this.n.push(nx, ny, nz, nx, ny, nz, nx, ny, nz);
    _c.copy(col); if (!noJit && this.j) _c.multiplyScalar(1 + (this.rnd() - 0.5) * 2 * this.j);
    this.c.push(_c.r, _c.g, _c.b, _c.r, _c.g, _c.b, _c.r, _c.g, _c.b);
  }
  quad(a, b, c, d, col) { const r = this.r; this.tri(a, b, c, col); this.r = r; this.tri(a, c, d, col); }
  /** Бокс із центром знизу (x,y,z), розміри sx sy sz. top — звуження верху. */
  box(x, y, z, sx, sy, sz, col, opt = {}) {
    const a = sx / 2, b = sz / 2, ta = a * (opt.top ?? 1), tb = b * (opt.topz ?? opt.top ?? 1), Y = y + sy;
    const q = (A, B, CC, D, c) => this.quad(A, B, CC, D, c);
    const side = opt.side || col;
    q([x - a, y, z + b], [x + a, y, z + b], [x + ta, Y, z + tb], [x - ta, Y, z + tb], side);
    q([x + a, y, z + b], [x + a, y, z - b], [x + ta, Y, z - tb], [x + ta, Y, z + tb], side);
    q([x + a, y, z - b], [x - a, y, z - b], [x - ta, Y, z - tb], [x + ta, Y, z - tb], side);
    q([x - a, y, z - b], [x - a, y, z + b], [x - ta, Y, z + tb], [x - ta, Y, z - tb], side);
    if (!opt.noTop) q([x - ta, Y, z + tb], [x + ta, Y, z + tb], [x + ta, Y, z - tb], [x - ta, Y, z - tb], col);
    if (opt.bottom) q([x - a, y, z - b], [x + a, y, z - b], [x + a, y, z + b], [x - a, y, z + b], col);
  }
  /** Зрізаний конус (seg граней). r1 = 0 — конус. */
  frustum(x, y0, z, r0, y1, r1, seg, col, opt = {}) {
    const ph = opt.ph || 0; const pt = (r, y, i) => { const a = ph + i / seg * Math.PI * 2; return [x + Math.cos(a) * r, y, z + Math.sin(a) * r]; };
    for (let i = 0; i < seg; i++) {
      const a = pt(r0, y0, i), b = pt(r0, y0, i + 1);
      if (r1 > 0) { const c = pt(r1, y1, i + 1), d = pt(r1, y1, i); this.quad(b, a, d, c, col); if (opt.cap) this.tri([x, y1, z], c, d, opt.capCol || col); } else this.tri(b, a, [x, y1, z], col);
    }
  }
  /** Диск-колесо, вісь — Z локальна. */
  wheel(x, y, z, r, th, seg, col) {
    const pt = (i, zz, k = 1) => { const a = i / seg * Math.PI * 2; return [x + Math.cos(a) * r * k, y + Math.sin(a) * r * k, z + zz]; };
    for (let i = 0; i < seg; i++) {
      this.tri([x, y, z + th / 2], pt(i, th / 2), pt(i + 1, th / 2), col);
      this.tri([x, y, z - th / 2], pt(i + 1, -th / 2), pt(i, -th / 2), col);
      this.quad(pt(i, -th / 2), pt(i + 1, -th / 2), pt(i + 1, th / 2), pt(i, th / 2), col);
    }
  }
  geometry() {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(this.p, 3));
    g.setAttribute('normal', new THREE.Float32BufferAttribute(this.n, 3));
    g.setAttribute('color', new THREE.Float32BufferAttribute(this.c, 3));
    return g;
  }
}

// ---------------------------------------------------------------------------------------------------------- хата
export function hut(b, x, y, z, ry, s, pal, opt = {}) {
  b.at(x, y, z, ry, s);
  const wall = C(pal.wall), roof = C(pal.roof), base = C(pal.plinth);
  const w = 1, d = 0.72, h = 0.5;
  b.box(0, -0.15, 0, w * 1.03, 0.2, d * 1.03, base, { noTop: true });
  b.box(0, 0, 0, w, h, d, wall, { noTop: true });
  // чотирисхилий стріховий дах із напуском
  const o = 0.14, ex = w / 2 + o, ez = d / 2 + o, ry0 = h - 0.03, rt = h + 0.5, rid = w * 0.2;
  const R = roof, RL = roof.clone().multiplyScalar(1.12), RD = roof.clone().multiplyScalar(0.78);
  b.quad([-ex, ry0, ez], [ex, ry0, ez], [rid, rt, 0], [-rid, rt, 0], RL);
  b.quad([ex, ry0, -ez], [-ex, ry0, -ez], [-rid, rt, 0], [rid, rt, 0], RD);
  b.tri([ex, ry0, ez], [ex, ry0, -ez], [rid, rt, 0], R);
  b.tri([-ex, ry0, -ez], [-ex, ry0, ez], [-rid, rt, 0], R);
  // двері й вікно
  if (opt.detail !== false) {
    const dc = C(pal.door), wc = C(pal.win), zf = d / 2 + 0.004;
    b.quad([-0.3, 0, zf], [-0.12, 0, zf], [-0.12, 0.34, zf], [-0.3, 0.34, zf], dc);
    b.quad([0.08, 0.17, zf], [0.28, 0.17, zf], [0.28, 0.34, zf], [0.08, 0.34, zf], wc);
  }
  if (opt.chim) b.box(0.26, rt - 0.3, -0.1, 0.16, 0.42, 0.16, C(pal.chim));
}

/** Дерев'яна триверха церква: зруби, що сходяться шатрами, маківка. gold — золота баня. */
export function church(b, x, y, z, s, pal, opt = {}) {
  b.at(x, y, z, opt.ry || 0, s);
  const wall = C(pal.churchWall), roof = C(pal.churchRoof), dome = C(opt.gold ? pal.gold : pal.dome);
  b.frustum(0, -0.2, 0, 0.62, 0.62, 0.56, 8, wall, { ph: Math.PI / 8 });
  b.frustum(0, 0.62, 0, 0.72, 0.98, 0.34, 8, roof, { ph: Math.PI / 8 });
  b.frustum(0, 0.98, 0, 0.34, 1.34, 0.3, 8, wall, { ph: Math.PI / 8 });
  b.frustum(0, 1.34, 0, 0.42, 1.62, 0.16, 8, roof, { ph: Math.PI / 8 });
  b.frustum(0, 1.62, 0, 0.16, 1.8, 0.14, 6, wall);
  b.frustum(0, 1.8, 0, 0.2, 2.0, 0.17, 6, dome);
  b.frustum(0, 2.0, 0, 0.17, 2.36, 0, 6, dome);
  b.box(0, 2.3, 0, 0.03, 0.34, 0.03, C(pal.gold));
  b.box(0, 2.5, 0, 0.17, 0.03, 0.03, C(pal.gold));
  if (opt.gold) for (const [dx, dz] of [[0.72, 0.5], [-0.72, 0.5], [0.72, -0.5], [-0.72, -0.5]]) {
    b.frustum(dx, -0.2, dz, 0.3, 0.7, 0.26, 8, wall);
    b.frustum(dx, 0.7, dz, 0.3, 0.92, 0.26, 8, dome);
    b.frustum(dx, 0.92, dz, 0.26, 1.34, 0, 8, dome);
    b.box(dx, 1.26, dz, 0.025, 0.2, 0.025, C(pal.gold));
  }
}
/** Ратуша/вежа Львова чи маяк: висока вузька вежа. */
export function tower(b, x, y, z, s, pal, opt = {}) {
  b.at(x, y, z, 0.2, s);
  const wall = C(opt.wall || pal.towerWall), roof = C(opt.roof || pal.towerRoof);
  b.box(0, -0.2, 0, 1.0, 0.75, 0.8, wall);
  b.box(0, 0.55, 0, 0.5, 1.15, 0.5, wall);
  b.frustum(0, 1.7, 0, 0.37, 2.0, 0.3, 4, roof, { ph: Math.PI / 4 });
  b.frustum(0, 2.0, 0, 0.3, 2.55, 0, 4, roof, { ph: Math.PI / 4 });
}
export function lighthouse(b, x, y, z, s, pal) {
  b.at(x, y, z, 0, s);
  const a = C('#f4efe4'), d = C(pal.towerRoof);
  b.frustum(0, -0.2, 0, 0.42, 0.6, 0.32, 8, a);
  b.frustum(0, 0.6, 0, 0.32, 1.3, 0.24, 8, C(pal.lightBand || '#c0392b'));
  b.frustum(0, 1.3, 0, 0.24, 1.9, 0.2, 8, a);
  b.frustum(0, 1.9, 0, 0.27, 2.08, 0.27, 8, C(pal.gold));
  b.frustum(0, 2.08, 0, 0.28, 2.4, 0, 8, d);
}
export function fortress(b, x, y, z, s, pal) {
  b.at(x, y, z, 0.5, s);
  const w = C(pal.towerWall), r = C(pal.towerRoof);
  for (const [dx, dz] of [[-0.7, -0.5], [0.7, -0.5], [0.7, 0.5], [-0.7, 0.5]]) { b.frustum(dx, -0.2, dz, 0.28, 0.82, 0.25, 8, w); b.frustum(dx, 0.82, dz, 0.33, 1.3, 0, 8, r); }
  b.box(0, -0.2, -0.5, 1.4, 0.68, 0.14, w); b.box(0, -0.2, 0.5, 1.4, 0.68, 0.14, w); b.box(-0.7, -0.2, 0, 0.14, 0.68, 1, w); b.box(0.7, -0.2, 0, 0.14, 0.68, 1, w);
}
export function windmill(b, x, y, z, s, pal, ry = 0) {
  b.at(x, y, z, ry, s);
  const w = C(pal.mill), r = C(pal.roof), bl = C(pal.millBlade);
  b.frustum(0, -0.1, 0, 0.42, 0.95, 0.3, 6, w);
  b.frustum(0, 0.95, 0, 0.38, 1.42, 0, 6, r);
  const hub = [0, 1.02, 0.4];
  for (let i = 0; i < 4; i++) {
    const a = i * Math.PI / 2 + 0.5, ca = Math.cos(a), sa = Math.sin(a), L = 0.92, Wd = 0.17;
    const px = (t, o) => [hub[0] + ca * t - sa * o, hub[1] + sa * t + ca * o, hub[2]];
    b.quad(px(0.14, 0), px(L, 0), px(L, Wd), px(0.14, Wd), bl);
    b.quad(px(0.14, Wd), px(L, Wd), px(L, 0), px(0.14, 0), bl);
    b.quad(px(0, -0.022), px(L, -0.022), px(L, 0.022), px(0, 0.022), r);
  }
}
export function saltHeap(b, x, y, z, s, pal) {
  b.at(x, y, z, b.rnd() * 6, s);
  const c = C('#f6f1e6');
  b.frustum(0, -0.1, 0, 0.8, 0.28, 0.45, 7, c);
  b.frustum(0, 0.28, 0, 0.45, 0.72, 0, 7, c);
}
export function kurgan(b, x, y, z, s, col, pal) {
  b.at(x, y, z, b.rnd() * 6, s);
  const c = C(col), seg = 12;
  b.frustum(0, -0.1, 0, 1.06, 0.1, 0.8, seg, c);
  b.frustum(0, 0.1, 0, 0.8, 0.26, 0.5, seg, c);
  b.frustum(0, 0.26, 0, 0.5, 0.34, 0.24, seg, c);
  b.frustum(0, 0.34, 0, 0.24, 0.36, 0.0, seg, c);
  // кам'яна баба на вершечку
  const st = C(pal ? pal.towerWall : '#ccc');
  b.box(0, 0.33, 0, 0.12, 0.2, 0.1, st, { top: 0.8 });
  b.frustum(0, 0.53, 0, 0.055, 0.6, 0.04, 5, st, { cap: true });
}

// ---------------------------------------------------------------------------------------------------------- віз із волами
// Довжина всієї фішки = 1, вісь +X — вперед (до волів), Y угору; низ коліс на y = 0.
function ox(b, x, z, col, dark, hi, horn) {
  const leg = dark;
  const LY = 0.062;
  for (const [lx, lz] of [[0.072, 0.024], [0.072, -0.024], [-0.078, 0.024], [-0.078, -0.024]]) b.box(x + lx, 0, z + lz, 0.024, LY + 0.006, 0.024, leg, { noTop: true, top: 1.2 });
  // тулуб: важка призма, холка вища за круп
  const X0 = x - 0.108, X1 = x + 0.1, Y0 = 0.058, Y1 = 0.142, Y2 = 0.122, W0 = 0.036, W1 = 0.046, W2 = 0.04;
  const q = (A, B, CC, D, c) => b.quad(A, B, CC, D, c);
  const px = (X, Y, Z) => [X, Y, z + Z];
  const lite = col.clone().multiplyScalar(1.12), sh = col.clone().multiplyScalar(0.82);
  q(px(X1, Y0, W0), px(X1, Y0, -W0), px(X1, Y1, -W1), px(X1, Y1, W1), sh);
  q(px(X0, Y0, -W0), px(X0, Y0, W0), px(X0, Y2, W2), px(X0, Y2, -W2), sh);
  q(px(X0, Y0, W0), px(X1, Y0, W0), px(X1, Y1, W1), px(X0, Y2, W2), col);
  q(px(X1, Y0, -W0), px(X0, Y0, -W0), px(X0, Y2, -W2), px(X1, Y1, -W1), col);
  q(px(X0, Y2, W2), px(X1, Y1, W1), px(X1, Y1, -W1), px(X0, Y2, -W2), lite);
  q(px(X1, Y0, -W0), px(X1, Y0, W0), px(X0, Y0, W0), px(X0, Y0, -W0), dark);
  // голова опущена вперед, морда темна
  b.box(x + 0.132, 0.086, z, 0.074, 0.054, 0.05, col, { top: 0.84 });
  b.box(x + 0.178, 0.082, z, 0.032, 0.036, 0.036, dark, { top: 0.9 });
  if (hi) {
    for (const sgn of [1, -1]) {
      const A = [x + 0.128, 0.138, z + sgn * 0.02], B = [x + 0.146, 0.136, z + sgn * 0.02], T = [x + 0.14, 0.2, z + sgn * 0.072];
      b.tri(A, B, T, horn); b.tri(B, A, T, horn);
    }
    b.tri(px(X0, Y2, 0.006), px(X0, Y2, -0.006), px(X0 - 0.02, 0.07, 0), dark);
    b.tri(px(X0, Y2, -0.006), px(X0, Y2, 0.006), px(X0 - 0.02, 0.07, 0), dark);
  } else {
    for (const sgn of [1, -1]) b.tri([x + 0.128, 0.138, z + sgn * 0.02], [x + 0.146, 0.136, z + sgn * 0.02], [x + 0.14, 0.195, z + sgn * 0.066], horn);
  }
}
/** neutral — колеса, дишель, воли: фарбуємо власними кольорами. body — сам віз і вантаж: множиться на колір гравця. */
export function cartGeometries(hi, pal) {
  const nb = new Builder(5, 0.03);
  const wheelC = C(pal.cartWheel), wood = C(pal.cartWood), oxA = C(pal.oxA), oxB = C(pal.oxB), oxD = C(pal.oxD), horn = C('#f4ecd8');
  const seg = hi ? 7 : 5;
  // великі колеса мажі
  for (const [wx, wz, r] of [[-0.42, 0.11, 0.092], [-0.42, -0.11, 0.092], [-0.13, 0.11, 0.076], [-0.13, -0.11, 0.076]]) {
    nb.wheel(wx, r, wz, r, 0.016, seg, wheelC);
    if (hi) nb.wheel(wx, r, wz + Math.sign(wz) * 0.012, r * 0.42, 0.008, 3, wood);
  }
  // осі, дишель, ярмо
  nb.box(-0.42, 0.086, 0, 0.018, 0.016, 0.22, wood); nb.box(-0.13, 0.07, 0, 0.018, 0.016, 0.22, wood);
  nb.box(-0.02, 0.07, 0, 0.42, 0.014, 0.016, wood);
  nb.box(0.205, 0.125, 0, 0.022, 0.018, 0.2, wood);
  // пара сірих волів
  ox(nb, 0.285, 0.062, oxA, oxD, hi, horn);
  ox(nb, 0.285, -0.062, hi ? oxB : oxA, oxD, hi, horn);
  const neutral = nb.geometry();

  const bodies = [];
  for (let k = 0; k < 5; k++) {
    const b = new Builder(11 + k, 0.03), W_ = C('#ffffff'), D = C('#8c8c8c'), DD = C('#6a6a6a'), RM = C('#b8b8b8');
    // кузов мажі: довга лозяна скриня, що розширюється догори
    const X0 = -0.5, X1 = -0.045, Y0 = 0.1, Y1 = 0.175, a = 0.064, c = 0.09;
    const q = (A, B, CC, E, col) => b.quad(A, B, CC, E, col);
    q([X0, Y0, a], [X1, Y0, a], [X1 + 0.018, Y1, c], [X0 - 0.018, Y1, c], D);
    q([X1, Y0, -a], [X0, Y0, -a], [X0 - 0.018, Y1, -c], [X1 + 0.018, Y1, -c], D);
    q([X1, Y0, a], [X1, Y0, -a], [X1 + 0.018, Y1, -c], [X1 + 0.018, Y1, c], DD);
    q([X0, Y0, -a], [X0, Y0, a], [X0 - 0.018, Y1, c], [X0 - 0.018, Y1, -c], DD);
    if (hi) {
      // обвідна планка і лозові стояки — видно зблизька
      for (const sg of [1, -1]) {
        q([X0 - 0.02, Y1, sg * c], [X1 + 0.02, Y1, sg * c], [X1 + 0.02, Y1 + 0.012, sg * (c + 0.002)], [X0 - 0.02, Y1 + 0.012, sg * (c + 0.002)], RM);
        for (let i = 0; i < 4; i++) { const t = X0 + (i + 0.5) / 4 * (X1 - X0); const y0 = Y0 + 0.004, y1 = Y1 - 0.002, zz0 = a + 0.002, zz1 = c + 0.0015, w = 0.006; if (sg > 0) q([t - w, y0, zz0], [t + w, y0, zz0], [t + w, y1, zz1], [t - w, y1, zz1], RM); else q([t + w, y0, -zz0], [t - w, y0, -zz0], [t - w, y1, -zz1], [t + w, y1, -zz1], RM); }
      }
    }
    const lx = (X1 - X0) / 2, mx = (X0 + X1) / 2;
    if (k === 0) {
      // шатро: п'ять обручів полотна — вантаж «під рядном»
      const sg = hi ? 6 : 4, rr = 0.09, bo = Y1 + 0.006;
      const pt = (x, an) => [x, bo + Math.sin(an) * rr * 1.08, Math.cos(an) * rr];
      for (let i = 0; i < sg; i++) {
        const a0 = i / sg * Math.PI, a1 = (i + 1) / sg * Math.PI;
        const lit = 0.74 + 0.34 * Math.sin((a0 + a1) / 2) + 0.06 * Math.cos((a0 + a1) / 2);
        q(pt(X0 + 0.018, a0), pt(X1 - 0.018, a0), pt(X1 - 0.018, a1), pt(X0 + 0.018, a1), W_.clone().multiplyScalar(lit));
        b.tri(pt(X1 - 0.018, a0), [X1 - 0.018, bo, 0], pt(X1 - 0.018, a1), W_.clone().multiplyScalar(0.6));
        b.tri(pt(X0 + 0.018, a1), [X0 + 0.018, bo, 0], pt(X0 + 0.018, a0), W_.clone().multiplyScalar(0.6));
      }
      if (hi) for (const f of [0.25, 0.5, 0.75]) { const xx = X0 + (X1 - X0) * f; for (let i = 0; i < sg; i++) { const a0 = i / sg * Math.PI, a1 = (i + 1) / sg * Math.PI; const s = 1.012; const P = (x, an) => [x, bo + Math.sin(an) * rr * 1.08 * s, Math.cos(an) * rr * s]; q(P(xx - 0.007, a0), P(xx + 0.007, a0), P(xx + 0.007, a1), P(xx - 0.007, a1), D); } }
    } else if (k === 1) {
      // лантухи в два яруси
      const n = 3;
      for (let i = 0; i < n; i++) { const x = X0 + (i + 0.5) * (X1 - X0) / n; for (const z of [0.044, -0.044]) { b.at(x, Y1 - 0.012, z, 0.18 * (i - 1)); b.box(0, 0, 0, 0.135, 0.078, 0.078, W_, { top: 0.72, side: W_.clone().multiplyScalar(0.84) }); } b.at(x, Y1 + 0.058, 0, -0.22 * (i - 1)); b.box(0, 0, 0, 0.125, 0.06, 0.075, W_.clone().multiplyScalar(0.95), { top: 0.68, side: W_.clone().multiplyScalar(0.78) }); }
      b.m.identity();
    } else if (k === 2) {
      // сіль навалом: гостра довга купа
      b.at(mx, Y1 - 0.012, 0);
      const sq = hi ? 10 : 6, r0 = 0.084, h = 0.17;
      for (let i = 0; i < sq; i++) {
        const a0 = i / sq * Math.PI * 2, a1 = (i + 1) / sq * Math.PI * 2;
        const e = (an, f) => [Math.cos(an) * lx * 0.98 * f, 0, Math.sin(an) * r0 * f];
        b.tri(e(a1, 1), e(a0, 1), [0, h, 0], W_.clone().multiplyScalar(0.78 + 0.28 * Math.sin((a0 + a1) / 2 + 0.6)));
      }
      b.m.identity();
    } else if (k === 4) {
      // сіно під мотузкою: заокруглена скирта
      const sg = hi ? 5 : 3, rr = 0.1, bo = Y1 - 0.006;
      const pt = (x, an) => [x, bo + Math.sin(an) * rr * 1.25, Math.cos(an) * rr];
      for (let i = 0; i < sg; i++) {
        const a0 = i / sg * Math.PI, a1 = (i + 1) / sg * Math.PI;
        const lit = 0.72 + 0.34 * Math.sin((a0 + a1) / 2);
        q(pt(X0 - 0.01, a0), pt(X1 + 0.01, a0), pt(X1 + 0.01, a1), pt(X0 - 0.01, a1), W_.clone().multiplyScalar(lit));
        b.tri(pt(X1 + 0.01, a0), [X1 + 0.01, bo, 0], pt(X1 + 0.01, a1), W_.clone().multiplyScalar(0.6));
        b.tri(pt(X0 - 0.01, a1), [X0 - 0.01, bo, 0], pt(X0 - 0.01, a0), W_.clone().multiplyScalar(0.6));
      }
    } else {
      // бочки дьогтю стоячи
      const n = 3;
      for (let i = 0; i < n; i++) { const x = X0 + (i + 0.5) * (X1 - X0) / n; b.at(x, Y1 - 0.016, 0); const sg = hi ? 8 : 6; b.frustum(0, 0, 0, 0.058, 0.055, 0.074, sg, W_.clone().multiplyScalar(0.9)); b.frustum(0, 0.055, 0, 0.074, 0.11, 0.058, sg, W_.clone().multiplyScalar(0.8), { cap: true, capCol: D }); }
      b.m.identity();
    }
    bodies.push(b.geometry());
  }
  return { neutral, bodies };
}

// ---------------------------------------------------------------------------------------------------------- дерева
export function treeGeometry(kind, hi) {
  const b = new Builder(3, 0);
  const seg = hi ? 6 : 4;
  const tr = C('#7a5a3a'), L = C('#ffffff'), D = C('#b8b8b8');
  if (kind === 'round') {
    b.frustum(0, 0, 0, 0.11, 0.42, 0.08, 4, tr);
    b.frustum(0, 0.28, 0, 0.34, 0.62, 0.52, seg, D);
    b.frustum(0, 0.62, 0, 0.52, 1.0, 0.32, seg, L);
    b.frustum(0, 1.0, 0, 0.32, 1.24, 0, seg, L);
  } else {
    b.frustum(0, 0, 0, 0.08, 0.26, 0.06, 4, tr);
    b.frustum(0, 0.2, 0, 0.46, 0.78, 0.0, seg, D);
    b.frustum(0, 0.58, 0, 0.36, 1.14, 0.0, seg, L, { ph: 0.5 });
    b.frustum(0, 0.92, 0, 0.24, 1.42, 0.0, seg, L);
  }
  const g = b.geometry(); return g;
}

/** Далекий віз: кузов-шатро завширшки з плашку (фарбується кольором гравця — атрибут aTint = 1), темні колеса, світла пара волів.
 *  Така ж система координат, як у близького; низ на y = 0. */
export function cartFar(pal) {
  const ox = C(pal.oxA), oxD = C(pal.oxD), wheel = C(pal.cartWheel), tintW = C('#ffffff');
  const P = [], N = [], Cc = [], T = [];
  const tri = (a, b, c, col, tint) => {
    const ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
    let nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx; const l = Math.hypot(nx, ny, nz) || 1; nx /= l; ny /= l; nz /= l;
    for (const v of [a, b, c]) { P.push(v[0], v[1], v[2]); N.push(nx, ny, nz); Cc.push(col.r, col.g, col.b); T.push(tint); }
  };
  const quad = (a, b, c, d, col, tint = 0) => { tri(a, b, c, col, tint); tri(a, c, d, col, tint); };
  const box = (x0, x1, y0, y1, z0, z1, col, tint = 0) => {
    quad([x0, y1, z1], [x1, y1, z1], [x1, y1, z0], [x0, y1, z0], col, tint);
    quad([x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1], col.clone().multiplyScalar(0.8), tint);
    quad([x1, y0, z0], [x0, y0, z0], [x0, y1, z0], [x1, y1, z0], col.clone().multiplyScalar(0.8), tint);
    quad([x1, y0, z1], [x1, y0, z0], [x1, y1, z0], [x1, y1, z1], col.clone().multiplyScalar(0.7), tint);
    quad([x0, y0, z0], [x0, y0, z1], [x0, y1, z1], [x0, y1, z0], col.clone().multiplyScalar(0.7), tint);
  };
  // колеса — темні «бруски» по боках
  for (const [x, z] of [[-0.36, 1], [-0.36, -1], [-0.04, 1], [-0.04, -1]]) box(x - 0.075, x + 0.075, 0, 0.13, z > 0 ? 0.105 : -0.125, z > 0 ? 0.125 : -0.105, wheel);
  // кузов-шатро: три грані дуги, торці
  const X0 = -0.47, X1 = 0.06, bo = 0.08, r = 0.108, an = [0, 0.95, 2.19, Math.PI], lit = [0.86, 1.08, 0.86];
  const pt = (x, a) => [x, bo + Math.sin(a) * r * 1.2, Math.cos(a) * r];
  for (let i = 0; i < 3; i++) {
    quad(pt(X0, an[i]), pt(X1, an[i]), pt(X1, an[i + 1]), pt(X0, an[i + 1]), tintW.clone().multiplyScalar(lit[i]), 1);
    tri(pt(X1, an[i]), [X1, bo, 0], pt(X1, an[i + 1]), tintW.clone().multiplyScalar(0.7), 1);
    tri(pt(X0, an[i + 1]), [X0, bo, 0], pt(X0, an[i]), tintW.clone().multiplyScalar(0.62), 1);
  }
  // дишель і пара волів — дві смужки з темними мордами, а не «кузов»
  const oxDark = ox.clone().multiplyScalar(0.62);
  box(0.05, 0.22, 0.07, 0.085, -0.014, 0.014, wheel);
  for (const z of [-1, 1]) {
    box(0.2, 0.41, 0.05, 0.135, z > 0 ? 0.02 : -0.096, z > 0 ? 0.096 : -0.02, oxDark);
    box(0.41, 0.49, 0.06, 0.11, z > 0 ? 0.036 : -0.08, z > 0 ? 0.08 : -0.036, oxD);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(P, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(N, 3));
  g.setAttribute('color', new THREE.Float32BufferAttribute(Cc, 3));
  g.setAttribute('aTint', new THREE.Float32BufferAttribute(T, 1));
  return g;
}

/** Мітка міста в петриківці: біла квітка з червоним серцем. */
export function flowerMark(b, x, y, z, R, pal, seg) {
  b.m.identity();
  const petal = C(pal.petal), heart = C(pal.heart), dark = C(pal.plazaRim), n = 8;
  b.frustum(x, y - 0.03, z, R * 1.02, y + 0.004, R * 0.98, seg, dark);
  for (let i = 0; i < n; i++) {
    const a = i / n * Math.PI * 2, d = R * 0.6, px = x + Math.cos(a) * d, pz = z + Math.sin(a) * d;
    b.frustum(px, y + 0.005, pz, R * 0.36, y + 0.0075, 0.0001, 8, petal);
  }
  b.frustum(x, y + 0.006, z, R * 0.42, y + 0.009, 0.0001, 10, heart);
}
