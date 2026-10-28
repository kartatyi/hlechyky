// «Хутори» — процедурні low-poly моделі для 3D-сцени (scene.js). Лише геометрія three.js, жодних файлів:
// плитки земель із «жителями», жетони чисел, пристані, фігури гравців, Кармелюк, кубики, море, рамка, стільниця.
//
// Стиль — «дерев'яна настолка»: пласке затінення, кольори вершин із легким розкидом яскравості по гранях (так low-poly
// виглядає живим, а не пластмасовим), тепла палітра. Майже все статичне зливається в ОДИН меш (Builder), однакові
// рухомі речі (дерева, вівці, колоски, дим, фігури) — InstancedMesh у scene.js. Тут лише «з чого зліплено».
//
// Система координат: поле в площині XZ, x — праворуч, z — «униз екрана» (y контракту), висота — Y.

import * as THREE from '/static/vendor/three-0.186.1/three.module.js';
import { TERRAIN_COLOR, RES_COLOR, SEA_COLOR, PIPS, RES_NAME } from './consts.js';

export const TILE_H = 0.18;        // висота плитки гекса
export const SEA_Y = 0.06;         // рівень моря
export const SEA_R = 5.55;         // радіус шестикутника моря (плаский верх)
export const FRAME_W = 0.45;       // ширина дерев'яної рамки довкола моря
export const ROBBER_SPOT = [0, -0.42];   // де на гексі стоїть Кармелюк (за жетоном, щоб число було видно)

const V3 = THREE.Vector3;
const S3 = Math.sqrt(3);
const UP = new V3(0, 1, 0);

// ---------------------------------------------------------------------------------------------------------- кольори

const colCache = new Map();
/** Колір із hex-рядка (sRGB → лінійний робочий простір three.js). Кешується — не мутувати! */
export function col(hex) {
  let c = colCache.get(hex);
  if (!c) { c = new THREE.Color(hex); colCache.set(hex, c); }
  return c;
}
function mix(a, b, k) { return col(a).clone().lerp(col(b), k); }

/** Детермінований генератор (mulberry32): та сама карта — ті самі дерева. */
export function rngOf(seed) {
  let s = (seed >>> 0) || 1;
  return () => {
    s = (s + 0x6D2B79F5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
export function hashStr(s) { let h = 2166136261; for (const ch of String(s)) h = Math.imul(h ^ ch.charCodeAt(0), 16777619); return h >>> 0; }

// ---------------------------------------------------------------------------------------------------------- будівник

const _m = new THREE.Matrix4(), _q = new THREE.Quaternion(), _e = new THREE.Euler(), _s = new V3(), _p = new V3();
/** Матриця: зсув, поворот (Y, потім X/Z), масштаб. */
export function mat(x = 0, y = 0, z = 0, ry = 0, sx = 1, sy = sx, sz = sx, rx = 0, rz = 0) {
  _e.set(rx, ry, rz, 'YXZ');
  return new THREE.Matrix4().compose(_p.set(x, y, z), _q.setFromEuler(_e), _s.set(sx, sy, sz));
}

/**
 * Зливає купу геометрій в одну неіндексовану з кольорами вершин (і uv за потреби).
 * add(geo, matrix, color | (localCentroid, rng) => Color, { jitter, rng, uv: [u0, v0, su, sv], keep })
 */
export class Builder {
  constructor(withUv = false) { this.p = []; this.c = []; this.uv = withUv ? [] : null; }
  add(g, m = null, color = '#ffffff', opt = {}) {
    const src = g.index ? g.toNonIndexed() : g;
    const pos = src.attributes.position, uv = src.attributes.uv;
    const jit = opt.jitter ?? 0.06, rng = opt.rng ?? Math.random;
    const a = new V3(), b = new V3(), c = new V3(), cen = new V3();
    const fixed = typeof color === 'function' ? null : (color instanceof THREE.Color ? color : col(color));
    for (let i = 0; i < pos.count; i += 3) {
      a.fromBufferAttribute(pos, i); b.fromBufferAttribute(pos, i + 1); c.fromBufferAttribute(pos, i + 2);
      const cc = fixed ?? color(cen.copy(a).add(b).add(c).divideScalar(3), rng);
      if (m) { a.applyMatrix4(m); b.applyMatrix4(m); c.applyMatrix4(m); }
      const j = 1 + (rng() - 0.5) * 2 * jit;
      this.p.push(a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z);
      for (let k = 0; k < 3; k++) this.c.push(cc.r * j, cc.g * j, cc.b * j);
      if (this.uv) {
        const [u0, v0, su, sv] = opt.uv ?? [0, 0, 0, 0];
        for (let k = 0; k < 3; k++) {
          const u = uv ? uv.getX(i + k) : 0, v = uv ? uv.getY(i + k) : 0;
          this.uv.push(u0 + u * su, v0 + v * sv);
        }
      }
    }
    if (src !== g) src.dispose();
    if (!opt.keep) g.dispose();
    return this;
  }
  get empty() { return this.p.length === 0; }
  geometry() {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(this.p, 3));
    g.setAttribute('color', new THREE.Float32BufferAttribute(this.c, 3));
    if (this.uv) g.setAttribute('uv', new THREE.Float32BufferAttribute(this.uv, 2));
    g.computeVertexNormals();
    g.computeBoundingSphere();
    return g;
  }
}

/** Геометрія з плаского масиву трикутників. */
function rawGeo(arr) {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(arr, 3));
  return g;
}
/** Трикутник (масиви [x,y,z]) з нормаллю в бік dir (V3). */
function tri(arr, a, b, c, dir) {
  const ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
  const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
  if (nx * dir.x + ny * dir.y + nz * dir.z < 0) arr.push(...a, ...c, ...b); else arr.push(...a, ...b, ...c);
}
function quad(arr, a, b, c, d, dir) { tri(arr, a, b, c, dir); tri(arr, a, c, d, dir); }

/** Брусок між двома точками (балка, дошка містка). */
function beam(b, p1, p2, w, h, color, rng, jitter = 0.08) {
  const dx = p2[0] - p1[0], dz = p2[2] - p1[2], dy = p2[1] - p1[1];
  const len = Math.hypot(dx, dy, dz);
  const g = new THREE.BoxGeometry(len, h, w);
  const m = new THREE.Matrix4().compose(
    new V3((p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2, (p1[2] + p2[2]) / 2),
    new THREE.Quaternion().setFromUnitVectors(new V3(1, 0, 0), new V3(dx, dy, dz).normalize()),
    new V3(1, 1, 1));
  b.add(g, m, color, { rng, jitter });
}

// ---------------------------------------------------------------------------------------------------------- плитка

const TOP_TINT = {   // верх плитки трохи відрізняється від «пласкої» барви, щоб жителі на ньому читались
  forest: mix(TERRAIN_COLOR.forest, '#6d5b32', 0.32),
  pit: mix(TERRAIN_COLOR.pit, '#d9945c', 0.25),
  pasture: mix(TERRAIN_COLOR.pasture, '#5f9440', 0.22),
  field: mix(TERRAIN_COLOR.field, '#b58a3a', 0.28),
  mountain: mix(TERRAIN_COLOR.mountain, '#7b8461', 0.3),
  steppe: col(TERRAIN_COLOR.steppe),
};
const SIDE = '#7a5a3a';

/** Кут гекса (вершиною догори): i = 0 — верх, далі за годинниковою (z униз). */
function corner(cx, cz, r, i, y) {
  const a = (-90 + 60 * i) * Math.PI / 180;
  return [cx + r * Math.cos(a), y, cz + r * Math.sin(a)];
}

/** Товста плитка з фаскою. Верх — 24 трикутники з розкидом барви. */
export function addTile(b, cx, cz, t, rng) {
  const H = TILE_H, rt = 0.9, rc = 0.985, yc = H - 0.05, yb = -0.04;
  const top = [], cham = [], side = [];
  const C = [cx, H, cz];
  for (let i = 0; i < 6; i++) {
    const P = corner(cx, cz, rt, i, H), Q = corner(cx, cz, rt, i + 1, H);
    const m0 = [(C[0] + P[0]) / 2, H, (C[2] + P[2]) / 2], m1 = [(P[0] + Q[0]) / 2, H, (P[2] + Q[2]) / 2],
      m2 = [(Q[0] + C[0]) / 2, H, (Q[2] + C[2]) / 2];
    tri(top, C, m0, m2, UP); tri(top, m0, P, m1, UP); tri(top, m0, m1, m2, UP); tri(top, m2, m1, Q, UP);
    const P2 = corner(cx, cz, rc, i, yc), Q2 = corner(cx, cz, rc, i + 1, yc);
    const P3 = corner(cx, cz, rc, i, yb), Q3 = corner(cx, cz, rc, i + 1, yb);
    const out = new V3((P[0] + Q[0]) / 2 - cx, 0.6, (P[2] + Q[2]) / 2 - cz);
    quad(cham, P, Q, Q2, P2, out);
    quad(side, P2, Q2, Q3, P3, new V3(out.x, 0, out.z));
  }
  const tc = TOP_TINT[t] ?? col('#999999');
  b.add(rawGeo(top), null, tc, { rng, jitter: 0.07 });
  b.add(rawGeo(cham), null, tc.clone().multiplyScalar(1.12), { rng, jitter: 0.04 });
  b.add(rawGeo(side), null, mix(SIDE, TERRAIN_COLOR[t] ?? SIDE, 0.25), { rng, jitter: 0.05 });
}

/** Пісочна мілина під плиткою — береги острова. */
export function addShore(b, cx, cz, rng) {
  const arr = [], y = SEA_Y + 0.026;
  for (let i = 0; i < 6; i++) tri(arr, [cx, y, cz], corner(cx, cz, 1.16, i, y), corner(cx, cz, 1.16, i + 1, y), UP);
  b.add(rawGeo(arr), null, '#e2cf9c', { rng, jitter: 0 });
}

// ---------------------------------------------------------------------------------------------------------- жителі

/** Точки в кільці гекса (локально), не ближче sep одна до одної й не на місці Кармелюка. */
function spots(rng, n, rMin, rMax, sep, clear = 0.2, taken = [], tall = 0) {
  const out = [];
  for (let tries = 0; out.length < n && tries < 400; tries++) {
    const a = rng() * Math.PI * 2, r = rMin + (rMax - rMin) * Math.sqrt(rng());
    const x = r * Math.cos(a), z = r * Math.sin(a);
    if (!inHex(x, z, 0.8)) continue;
    if (Math.hypot(x - ROBBER_SPOT[0], z - ROBBER_SPOT[1]) < clear) continue;
    if (tall && Math.abs(x) < 0.3 + tall && z > -0.05) continue;   // високе не стає перед жетоном (камера — з півдня)
    if ([...out, ...taken].some(([u, w, s]) => Math.hypot(u - x, w - z) < Math.max(sep, s ?? 0))) continue;
    out.push([x, z, sep]);
  }
  return out;
}
/** Чи точка всередині гекса вершиною догори з апофемою ap (локально). */
function inHex(x, z, ap) {
  for (let i = 0; i < 6; i++) {
    const a = (-60 + 60 * i) * Math.PI / 180;   // нормалі сторін: 30° від кутів
    if (x * Math.cos(a) + z * Math.sin(a) > ap) return false;
  }
  return true;
}

const LEAF = ['#3f6d27', '#4d7f2c', '#5a8a33', '#466f2a'];
const NEEDLE = ['#2f5b31', '#355f2f', '#2a5230'];

/** Дуб: стовбур і крона з трьох «клубків». Основа в (0,0,0). */
export function oakGeo() {
  const b = new Builder(), r = rngOf(7);
  b.add(new THREE.CylinderGeometry(0.026, 0.036, 0.16, 5), mat(0, 0.08, 0), '#6b4a2e', { rng: r });
  const blobs = [[0, 0.24, 0, 0.13], [0.07, 0.2, 0.04, 0.095], [-0.065, 0.21, -0.03, 0.1], [0.01, 0.31, -0.02, 0.09]];
  blobs.forEach(([x, y, z, s], i) => b.add(new THREE.IcosahedronGeometry(s, 0), mat(x, y, z, i), LEAF[i % 4], { rng: r, jitter: 0.1 }));
  return b.geometry();
}
/** Смерека: три яруси конусів. */
export function spruceGeo() {
  const b = new Builder(), r = rngOf(11);
  b.add(new THREE.CylinderGeometry(0.022, 0.03, 0.1, 5), mat(0, 0.05, 0), '#5e3f26', { rng: r });
  [[0.15, 0.17, 0.15], [0.12, 0.15, 0.25], [0.085, 0.14, 0.34]].forEach(([rad, h, y], i) =>
    b.add(new THREE.ConeGeometry(rad, h, 7), mat(0, y, 0, i * 0.4), NEEDLE[i % 3], { rng: r, jitter: 0.08 }));
  return b.geometry();
}
/** Вівця: кудлатий тулуб, темна голова й ноги. Дивиться в +x. */
export function sheepGeo() {
  const b = new Builder(), r = rngOf(5);
  [[0, 0.075, 0, 0.055], [0.035, 0.08, 0.012, 0.045], [-0.035, 0.078, -0.01, 0.047], [0, 0.1, 0, 0.042]].forEach(([x, y, z, s], i) =>
    b.add(new THREE.IcosahedronGeometry(s, 0), mat(x, y, z, i * 0.7, 1.15, 0.9, 1), '#f3efe4', { rng: r, jitter: 0.05 }));
  b.add(new THREE.BoxGeometry(0.045, 0.04, 0.035), mat(0.075, 0.095, 0, 0, 1, 1, 1, 0, -0.35), '#3a322e', { rng: r });
  b.add(new THREE.BoxGeometry(0.012, 0.02, 0.022), mat(0.07, 0.115, 0.022), '#3a322e', { rng: r });
  b.add(new THREE.BoxGeometry(0.012, 0.02, 0.022), mat(0.07, 0.115, -0.022), '#3a322e', { rng: r });
  for (const [x, z] of [[0.035, 0.022], [0.035, -0.022], [-0.035, 0.022], [-0.035, -0.022]])
    b.add(new THREE.BoxGeometry(0.014, 0.05, 0.014), mat(x, 0.025, z), '#3a322e', { rng: r, jitter: 0 });
  return b.geometry();
}
/** Жмуток із трьох колосків; основа в нулі (гойдається навколо неї). */
export function wheatGeo() {
  const b = new Builder(), r = rngOf(3);
  [[0, 0, 0.15], [0.025, 0.015, 0.13], [-0.02, 0.02, 0.14]].forEach(([x, z, h], i) => {
    b.add(new THREE.BoxGeometry(0.008, h, 0.008), mat(x, h / 2, z), '#b89a3e', { rng: r, jitter: 0.05 });
    b.add(new THREE.OctahedronGeometry(0.02, 0), mat(x, h + 0.02, z, i, 0.7, 1.8, 0.7), i === 1 ? '#e2b850' : '#f0c862', { rng: r, jitter: 0.08 });
  });
  return b.geometry();
}
/** Клубок диму (одиничний, розмір — масштабом екземпляра). */
export function puffGeo() {
  const b = new Builder(), r = rngOf(9);
  b.add(new THREE.IcosahedronGeometry(1, 0), null, '#ece8e0', { rng: r, jitter: 0.06 });
  return b.geometry();
}

/** Глечик (точіння). */
function potGeo(h = 1) {
  const pts = [[0, 0], [0.028, 0], [0.042, 0.022], [0.044, 0.045], [0.03, 0.07], [0.02, 0.082], [0.026, 0.094], [0, 0.094]]
    .map(([x, y]) => new THREE.Vector2(x * h, y * h));
  return new THREE.LatheGeometry(pts, 8);
}

/**
 * Статичні й рухомі «жителі» гекса. b — статичний будівник; out — списки екземплярів:
 * out.oak/spruce/sheep/wheat: [{x, z, ry, s, ...}], out.smoke: [{x, y, z}].
 */
export function decorate(b, t, cx, cz, rng, out) {
  const P = (x, z) => [cx + x, cz + z];
  const H = TILE_H;
  if (t === 'forest') {
    const pts = spots(rng, 6, 0.42, 0.74, 0.25, 0.2, [], 0.06);
    pts.forEach(([x, z], i) => {
      const [wx, wz] = P(x, z);
      (rng() < 0.45 ? out.oak : out.spruce).push({ x: wx, z: wz, ry: rng() * 6.3, s: 0.85 + rng() * 0.35, ph: rng() * 6.3 });
    });
    for (const [x, z] of spots(rng, 3, 0.35, 0.7, 0.14, 0.2, pts)) {
      const [wx, wz] = P(x, z);
      if (rng() < 0.5) b.add(new THREE.IcosahedronGeometry(0.05, 0), mat(wx, H + 0.03, wz, rng() * 3, 1, 0.7, 1), '#3b6a2a', { rng });
      else {   // пеньок із грибом
        b.add(new THREE.CylinderGeometry(0.03, 0.035, 0.04, 6), mat(wx, H + 0.02, wz), '#7a5634', { rng });
        b.add(new THREE.SphereGeometry(0.018, 6, 3, 0, Math.PI * 2, 0, Math.PI / 2), mat(wx + 0.04, H + 0.012, wz), '#c0392b', { rng, jitter: 0 });
      }
    }
  } else if (t === 'pasture') {
    // Тин — дуга з кілків і плетива.
    const a0 = Math.PI * (0.15 + rng() * 0.7), n = 6, R = 0.66;
    const posts = [];
    for (let i = 0; i < n; i++) {
      const a = a0 + (i - (n - 1) / 2) * 0.22;
      posts.push([cx + R * Math.cos(a), cz + R * Math.sin(a)]);
    }
    posts.forEach(([x, z]) => b.add(new THREE.CylinderGeometry(0.011, 0.013, 0.12, 5), mat(x, H + 0.06, z), '#6e5032', { rng }));
    for (let i = 0; i + 1 < n; i++)
      for (const y of [0.04, 0.075]) beam(b, [posts[i][0], H + y, posts[i][1]], [posts[i + 1][0], H + y + 0.004, posts[i + 1][1]], 0.016, 0.022, '#9a7448', rng, 0.12);
    const sh = spots(rng, 3, 0.42, 0.56, 0.26);
    sh.forEach(([x, z]) => { const [wx, wz] = P(x, z); out.sheep.push({ x: wx, z: wz, ph: rng() * 6.3, rx: 0.05 + rng() * 0.05, rz: 0.04 + rng() * 0.04, sp: 0.12 + rng() * 0.1 }); });
    for (const [x, z] of spots(rng, 8, 0.3, 0.76, 0.1, 0.18, sh)) {
      const [wx, wz] = P(x, z);
      if (rng() < 0.6) b.add(new THREE.ConeGeometry(0.022, 0.05, 3), mat(wx, H + 0.025, wz, rng() * 3), '#5f9a3a', { rng });
      else b.add(new THREE.IcosahedronGeometry(0.012, 0), mat(wx, H + 0.012, wz), rng() < 0.5 ? '#fbf6e8' : '#f2cf4a', { rng, jitter: 0 });
    }
  } else if (t === 'field') {
    for (let k = 0; k < 10; k++) {
      const z = -0.68 + k * 0.152;
      const row = [];
      for (let x = -0.78; x <= 0.78; x += 0.105) {
        const xx = x + (k % 2) * 0.05;
        if (!inHex(xx, z, 0.74)) continue;
        if (Math.hypot(xx, z) < 0.37) continue;
        if (Math.hypot(xx - ROBBER_SPOT[0], z - ROBBER_SPOT[1]) < 0.2) continue;
        row.push(xx);
        out.wheat.push({ x: cx + xx + (rng() - 0.5) * 0.02, z: cz + z + (rng() - 0.5) * 0.02, ry: rng() * 6.3, s: 0.85 + rng() * 0.3 });
      }
      // Борозна під рядком — темніша смужка землі.
      let i = 0;
      while (i < row.length) {
        let j = i;
        while (j + 1 < row.length && row[j + 1] - row[j] < 0.16) j++;
        if (j > i || row.length === 1) {
          const x1 = row[i] - 0.05, x2 = row[j] + 0.05;
          b.add(new THREE.BoxGeometry(x2 - x1, 0.006, 0.05), mat(cx + (x1 + x2) / 2, H + 0.003, cz + z), '#9a7234', { rng, jitter: 0.05 });
        }
        i = j + 1;
      }
    }
  } else if (t === 'pit') {
    const [pitP, kilnP, potP] = spots(rng, 3, 0.44, 0.62, 0.34);
    if (pitP) {   // яма з глиною й купи довкола
      const [x, z] = P(pitP[0], pitP[1]);
      b.add(new THREE.CylinderGeometry(0.17, 0.15, 0.012, 8), mat(x, H + 0.004, z, rng()), '#7c3a22', { rng, jitter: 0.08 });
      b.add(new THREE.CylinderGeometry(0.11, 0.12, 0.016, 7), mat(x + 0.01, H + 0.008, z, rng()), '#a24a2a', { rng, jitter: 0.08 });
      for (let i = 0; i < 4; i++) {
        const a = i * 1.6 + rng();
        b.add(new THREE.IcosahedronGeometry(0.05, 0), mat(x + 0.19 * Math.cos(a), H + 0.012, z + 0.19 * Math.sin(a), a, 1, 0.55, 1), '#b9582f', { rng, jitter: 0.1 });
      }
    }
    if (kilnP) {  // піч-гончарня з комином і димком
      const [x, z] = P(kilnP[0], kilnP[1]);
      b.add(new THREE.CylinderGeometry(0.14, 0.15, 0.03, 8), mat(x, H + 0.015, z), '#8f5b3c', { rng });
      b.add(new THREE.SphereGeometry(0.13, 8, 4, 0, Math.PI * 2, 0, Math.PI / 2), mat(x, H + 0.03, z, 0.3, 1, 0.95, 1), '#b8643f', { rng, jitter: 0.1 });
      b.add(new THREE.BoxGeometry(0.07, 0.06, 0.04), mat(x, H + 0.06, z + 0.115), '#2a1810', { rng, jitter: 0 });
      b.add(new THREE.BoxGeometry(0.09, 0.02, 0.05), mat(x, H + 0.1, z + 0.11), '#9a4e30', { rng });
      b.add(new THREE.CylinderGeometry(0.026, 0.03, 0.11, 6), mat(x - 0.03, H + 0.17, z - 0.03), '#8a4630', { rng });
      out.smoke.push({ x: x - 0.03, y: H + 0.23, z: z - 0.03 });
      // дрова
      for (let i = 0; i < 3; i++) b.add(new THREE.CylinderGeometry(0.012, 0.012, 0.09, 5), mat(x + 0.14, H + 0.013 + (i === 2 ? 0.02 : 0), z + 0.06 + (i % 2) * 0.026 - (i === 2 ? 0.013 : 0), 0, 1, 1, 1, Math.PI / 2), '#7a5232', { rng });
    }
    if (potP) {   // глечики й цегла
      const [x, z] = P(potP[0], potP[1]);
      b.add(potGeo(1.25), mat(x, H, z), '#c8703e', { rng, jitter: 0.06 });
      b.add(potGeo(0.95), mat(x + 0.09, H, z + 0.03), '#a85a33', { rng, jitter: 0.06 });
      for (let l = 0; l < 3; l++)
        for (let i = 0; i < 3 - l; i++)
          b.add(new THREE.BoxGeometry(0.05, 0.022, 0.028), mat(x - 0.08 + i * 0.054 + l * 0.027, H + 0.011 + l * 0.023, z + 0.08), '#b5502d', { rng, jitter: 0.1 });
    }
  } else if (t === 'mountain') {
    const side = rng() < 0.5 ? -1 : 1;
    const main = [0.38 * side, -0.08];
    const peaks = [[main[0], main[1], 0.36, 0.86]];
    for (const [x, z] of spots(rng, 2, 0.45, 0.66, 0.42, 0.34, [[main[0], main[1], 0.5]], 0.14)) peaks.push([x, z, 0.24 + rng() * 0.06, 0.5 + rng() * 0.18]);
    peaks.forEach(([x, z, rad, h]) => {
      const seg = rng() < 0.5 ? 5 : 6;
      const [wx, wz] = P(x, z);
      b.add(new THREE.ConeGeometry(rad, h, seg, 3), mat(wx, H + h / 2 - 0.01, wz, rng() * 6.3),
        (c, r) => c.y > h * 0.14 ? col('#f3f5f7') : (r() < 0.5 ? col('#80858d') : col('#6c7179')), { rng, jitter: 0.07 });
    });
    // Копальня біля підніжжя головної вершини: вхід, підпори, рейки, вагонетка з рудою.
    const [mx, mz] = P(main[0] - 0.02 * side, main[1] + 0.3);
    b.add(new THREE.BoxGeometry(0.12, 0.11, 0.06), mat(mx, H + 0.055, mz - 0.02), '#1d1b1a', { rng, jitter: 0 });
    for (const dx of [-0.065, 0.065]) b.add(new THREE.BoxGeometry(0.022, 0.13, 0.022), mat(mx + dx, H + 0.065, mz + 0.012), '#6b4a2b', { rng });
    b.add(new THREE.BoxGeometry(0.17, 0.024, 0.03), mat(mx, H + 0.135, mz + 0.012), '#7a5532', { rng });
    for (const dx of [-0.025, 0.025]) b.add(new THREE.BoxGeometry(0.008, 0.008, 0.2), mat(mx + dx, H + 0.004, mz + 0.11), '#4a4a4f', { rng, jitter: 0 });
    b.add(new THREE.BoxGeometry(0.07, 0.04, 0.05), mat(mx, H + 0.03, mz + 0.16), '#5b4a3a', { rng });
    for (let i = 0; i < 3; i++) b.add(new THREE.IcosahedronGeometry(0.018, 0), mat(mx - 0.018 + i * 0.018, H + 0.055, mz + 0.16 + (i % 2) * 0.01), '#4a4f58', { rng, jitter: 0.1 });
    for (const [x, z] of spots(rng, 3, 0.3, 0.72, 0.16, 0.2, peaks.map(([x, z, r]) => [x, z, r + 0.08]).concat([[main[0], main[1] + 0.32, 0.2]]))) {
      const [wx, wz] = P(x, z);
      b.add(new THREE.DodecahedronGeometry(0.04 + rng() * 0.03, 0), mat(wx, H + 0.015, wz, rng() * 3, 1, 0.7, 1), '#7a7f87', { rng, jitter: 0.1 });
    }
  } else if (t === 'steppe') {
    const a = rng() < 0.5 ? 0.25 : Math.PI - 0.25;
    const kx = 0.45 * Math.cos(a), kz = 0.45 * Math.sin(a) - 0.05;
    const [wx, wz] = P(kx, kz);
    b.add(new THREE.SphereGeometry(0.3, 8, 4, 0, Math.PI * 2, 0, Math.PI / 2), mat(wx, H - 0.005, wz, 0.2, 1, 0.42, 1), '#b9b06a', { rng, jitter: 0.07 });
    // Кам'яна баба на кургані.
    const by = H + 0.12;
    b.add(new THREE.CylinderGeometry(0.035, 0.045, 0.13, 6), mat(wx, by + 0.065, wz), '#9a958c', { rng, jitter: 0.06 });
    b.add(new THREE.BoxGeometry(0.07, 0.018, 0.05), mat(wx, by + 0.085, wz + 0.006), '#8d887f', { rng });
    b.add(new THREE.DodecahedronGeometry(0.034, 0), mat(wx, by + 0.155, wz), '#a39e95', { rng, jitter: 0.06 });
    b.add(new THREE.CylinderGeometry(0.03, 0.036, 0.022, 6), mat(wx, by + 0.185, wz), '#8a857c', { rng });
    // Криниця з журавлем — по другий бік від кургану.
    const sd = kx > 0 ? -1 : 1, [qx, qz] = P(sd * 0.47, 0.27);
    b.add(new THREE.CylinderGeometry(0.075, 0.08, 0.065, 8), mat(qx, H + 0.032, qz), '#8d8576', { rng, jitter: 0.1 });
    b.add(new THREE.CylinderGeometry(0.055, 0.055, 0.004, 8), mat(qx, H + 0.066, qz), '#2c3c48', { rng, jitter: 0 });
    b.add(new THREE.CylinderGeometry(0.012, 0.015, 0.29, 5), mat(qx + sd * 0.13, H + 0.145, qz), '#6b4a2b', { rng });
    beam(b, [qx - sd * 0.02, H + 0.37, qz], [qx + sd * 0.32, H + 0.2, qz], 0.014, 0.014, '#7a5532', rng, 0.05);
    b.add(new THREE.BoxGeometry(0.004, 0.25, 0.004), mat(qx - sd * 0.02, H + 0.245, qz), '#4a3a2a', { rng, jitter: 0 });
    b.add(new THREE.CylinderGeometry(0.018, 0.014, 0.03, 6), mat(qx - sd * 0.02, H + 0.12, qz), '#6b4a2b', { rng });
    b.add(new THREE.BoxGeometry(0.04, 0.03, 0.03), mat(qx + sd * 0.31, H + 0.19, qz), '#7d776c', { rng });
    // Ковила, каміння.
    for (const [x, z] of spots(rng, 9, 0.32, 0.76, 0.1, 0.18, [[kx, kz, 0.38], [sd * 0.47, 0.27, 0.16], [sd * 0.62, 0.27, 0.12]])) {
      const [px, pz] = P(x, z);
      if (rng() < 0.75) b.add(new THREE.ConeGeometry(0.02, 0.08, 3), mat(px, H + 0.04, pz, rng() * 3, 1, 1, 1, (rng() - 0.5) * 0.4), rng() < 0.5 ? '#e4dcb6' : '#c9bf86', { rng });
      else b.add(new THREE.DodecahedronGeometry(0.03, 0), mat(px, H + 0.01, pz, rng() * 3, 1, 0.6, 1), '#a9a294', { rng });
    }
  }
}

// ---------------------------------------------------------------------------------------------------------- атлас

// 4×4 клітинки по 256 px: числа 2–6, 8–12 (0–9), пристані: any, wood, clay, wool, grain, ore (10–15).
const NUMS = [2, 3, 4, 5, 6, 8, 9, 10, 11, 12];
export const PORT_KINDS = ['any', 'wood', 'clay', 'wool', 'grain', 'ore'];
export function atlasCell(i) { return [(i % 4) / 4, 1 - (Math.floor(i / 4) + 1) / 4, 0.25, 0.25]; }
export function numCell(n) { return NUMS.indexOf(n); }
export function portCell(kind) { return 10 + Math.max(0, PORT_KINDS.indexOf(kind)); }

export function makeAtlas() {
  const S = 256, cv = document.createElement('canvas');
  cv.width = cv.height = S * 4;
  const g = cv.getContext('2d');
  const serif = 'Georgia, "Times New Roman", serif';
  NUMS.forEach((n, i) => {
    const x = (i % 4) * S + S / 2, y = Math.floor(i / 4) * S + S / 2;
    const red = n === 6 || n === 8;
    g.fillStyle = '#f2e3c0'; g.beginPath(); g.arc(x, y, 124, 0, Math.PI * 2); g.fill();
    g.strokeStyle = '#c9a46a'; g.lineWidth = 7; g.beginPath(); g.arc(x, y, 112, 0, Math.PI * 2); g.stroke();
    const p = PIPS[n];
    g.fillStyle = red ? '#b3261e' : '#3a2718';
    g.font = `bold ${92 + p * 9}px ${serif}`; g.textAlign = 'center'; g.textBaseline = 'middle';
    g.fillText(String(n), x, y - 14);
    for (let k = 0; k < p; k++) { g.beginPath(); g.arc(x + (k - (p - 1) / 2) * 24, y + 72, 8.5, 0, Math.PI * 2); g.fill(); }
  });
  PORT_KINDS.forEach((k, j) => {
    const i = 10 + j, x = (i % 4) * S + S / 2, y = Math.floor(i / 4) * S + S / 2;
    g.fillStyle = '#7a5130'; roundRect(g, x - 120, y - 92, 240, 184, 34); g.fill();
    g.fillStyle = '#a8763f'; roundRect(g, x - 110, y - 82, 220, 164, 26); g.fill();
    g.fillStyle = k === 'any' ? '#f2e3c0' : RES_COLOR[k];
    roundRect(g, x - 96, y - 68, 192, 136, 20); g.fill();
    const dark = k === 'any' || k === 'grain' || k === 'wool';
    g.fillStyle = dark ? '#3a2718' : '#fff8ea';
    g.textAlign = 'center'; g.textBaseline = 'middle';
    g.font = `bold ${k === 'any' ? 92 : 78}px ${serif}`;
    g.fillText(k === 'any' ? '3:1' : '2:1', x, k === 'any' ? y + 4 : y - 16);
    if (k !== 'any') { g.font = `bold 40px ${serif}`; g.fillText(RES_NAME[k], x, y + 42); }
  });
  const tex = new THREE.CanvasTexture(cv);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.anisotropy = 4;
  return tex;
}
function roundRect(g, x, y, w, h, r) {
  g.beginPath(); g.moveTo(x + r, y); g.arcTo(x + w, y, x + w, y + h, r); g.arcTo(x + w, y + h, x, y + h, r);
  g.arcTo(x, y + h, x, y, r); g.arcTo(x, y, x + w, y, r); g.closePath();
}

/** Жетон числа: дерев'яне кружальце (у статичний меш) і верх із числом (у меш з атласом). */
export const TOKEN_R = 0.28;
export function addTokenBody(b, x, z, rng) {
  b.add(new THREE.CylinderGeometry(TOKEN_R + 0.015, TOKEN_R + 0.03, 0.05, 20), mat(x, TILE_H + 0.025, z), '#b98a52', { rng, jitter: 0.05 });
}
export function addTokenTop(bu, x, z, n) {
  const g = new THREE.CircleGeometry(TOKEN_R, 24);
  bu.add(g, mat(x, TILE_H + 0.051, z, 0, 1, 1, 1, -Math.PI / 2), '#ffffff', { jitter: 0, uv: atlasCell(numCell(n)) });
}

// ---------------------------------------------------------------------------------------------------------- пристань

/** Місток із човником і стовпом під табличку. Повертає точку таблички (для спрайта). */
export function addPort(b, A, B, rng) {
  const mx = (A[0] + B[0]) / 2, mz = (A[1] + B[1]) / 2;
  const ol = Math.hypot(mx, mz), ox = mx / ol, oz = mz / ol;        // назовні від центру поля
  let tx = B[0] - A[0], tz = B[1] - A[1]; const tl = Math.hypot(tx, tz); tx /= tl; tz /= tl;
  const px = mx + ox * 0.52, pz = mz + oz * 0.52, py = SEA_Y + 0.08;
  const ry = -Math.atan2(tz, tx);
  // Поміст із трьох дощок і палі.
  for (let i = -1; i <= 1; i++)
    b.add(new THREE.BoxGeometry(0.42, 0.03, 0.1), mat(px + ox * i * 0.105, py, pz + oz * i * 0.105, ry + (rng() - 0.5) * 0.04), '#9b6b3c', { rng, jitter: 0.1 });
  for (const [s, u] of [[-1, -1], [-1, 1], [1, -1], [1, 1]])
    b.add(new THREE.CylinderGeometry(0.018, 0.02, 0.16, 5), mat(px + tx * s * 0.18 + ox * u * 0.13, py - 0.06, pz + tz * s * 0.18 + oz * u * 0.13), '#5e3f26', { rng });
  // Кладки від двох вершин до помосту.
  for (const [V, s] of [[A, -1], [B, 1]]) {
    const vx = V[0] + ox * 0.02, vz = V[1] + oz * 0.02;
    beam(b, [vx, TILE_H - 0.01, vz], [px + tx * s * 0.13 - ox * 0.12, py + 0.01, pz + tz * s * 0.13 - oz * 0.12], 0.075, 0.022, '#a8763f', rng, 0.1);
  }
  // Човник біля помосту.
  const bx = px + ox * 0.3 + tx * 0.08, bz = pz + oz * 0.3 + tz * 0.08;
  b.add(new THREE.SphereGeometry(0.1, 8, 3, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2), mat(bx, SEA_Y + 0.045, bz, ry, 1.9, 0.6, 0.75), '#7b4f2c', { rng, jitter: 0.08 });
  b.add(new THREE.CylinderGeometry(0.1, 0.1, 0.006, 8), mat(bx, SEA_Y + 0.044, bz, ry, 1.75, 1, 0.65), '#4a2f1b', { rng, jitter: 0 });
  b.add(new THREE.BoxGeometry(0.012, 0.012, 0.13), mat(bx, SEA_Y + 0.05, bz, ry), '#a8763f', { rng });
  // Стовп таблички.
  const sx = px - tx * 0.17, sz = pz - tz * 0.17;
  b.add(new THREE.CylinderGeometry(0.014, 0.016, 0.26, 5), mat(sx, py + 0.13, sz), '#5e3f26', { rng });
  return [sx, py + 0.38, sz];
}

// ---------------------------------------------------------------------------------------------------------- море, рамка, стіл

/** Трикутна сітка в шестикутнику (плаский верх) — щоб хвилі мали грані. Кольори: мілина біля землі світліша. */
export function seaGeo(landCenters) {
  const N = 20, s = SEA_R / N, arr = [], cols = [];
  const deep = col(SEA_COLOR).clone().multiplyScalar(0.78), shallow = mix(SEA_COLOR, '#8fd0d8', 0.55);
  const P = (i, j) => [s * (i + j / 2), SEA_Y, s * (j * S3 / 2)];
  const inside = (i, j) => Math.abs(i) <= N && Math.abs(j) <= N && Math.abs(i + j) <= N;
  const shade = (p) => {
    let d = 99;
    for (const [x, z] of landCenters) d = Math.min(d, Math.hypot(p[0] - x, p[2] - z));
    const k = Math.min(1, Math.max(0, (d - 1.05) / 1.3));
    return shallow.clone().lerp(deep, k);
  };
  const rng = rngOf(77);
  const push = (a, b, c) => {
    arr.push(...a, ...b, ...c);
    const cen = [(a[0] + b[0] + c[0]) / 3, 0, (a[2] + b[2] + c[2]) / 3];
    const cc = shade(cen), j = 1 + (rng() - 0.5) * 0.08;
    for (let k = 0; k < 3; k++) cols.push(cc.r * j, cc.g * j, cc.b * j);
  };
  for (let i = -N; i <= N; i++)
    for (let j = -N; j <= N; j++) {
      if (inside(i, j) && inside(i + 1, j) && inside(i, j + 1)) push(P(i, j), P(i, j + 1), P(i + 1, j));
      if (inside(i + 1, j) && inside(i, j + 1) && inside(i + 1, j + 1)) push(P(i + 1, j), P(i, j + 1), P(i + 1, j + 1));
    }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(arr, 3));
  g.setAttribute('color', new THREE.Float32BufferAttribute(cols, 3));
  g.computeVertexNormals();
  return g;
}

/** Дерев'яна рамка довкола моря (6 брусів зі скошеним верхом). */
export function addFrame(b) {
  const rng = rngOf(21), r0 = SEA_R - 0.06, r1 = SEA_R + FRAME_W, y0 = -0.01, y1 = 0.2, yb = 0.24;
  for (let i = 0; i < 6; i++) {
    const a0 = i * Math.PI / 3, a1 = (i + 1) * Math.PI / 3;
    const pt = (r, a, y) => [r * Math.cos(a), y, r * Math.sin(a)];
    const am = (a0 + a1) / 2, out = new V3(Math.cos(am), 0, Math.sin(am)), inn = out.clone().negate();
    const arr = [], rm = (r0 + r1) / 2;
    quad(arr, pt(r0, a0, y1), pt(r0, a1, y1), pt(rm, a1, yb), pt(rm, a0, yb), new V3(inn.x, 1, inn.z));
    quad(arr, pt(rm, a0, yb), pt(rm, a1, yb), pt(r1, a1, y1), pt(r1, a0, y1), new V3(out.x, 1, out.z));
    quad(arr, pt(r0, a0, y0), pt(r0, a1, y0), pt(r0, a1, y1), pt(r0, a0, y1), inn);
    quad(arr, pt(r1, a0, y0), pt(r1, a1, y0), pt(r1, a1, y1), pt(r1, a0, y1), out);
    b.add(rawGeo(arr), null, i % 2 ? '#8a5a30' : '#7e522c', { rng, jitter: 0.05 });
    // Дерев'яний кілочок на куті рамки.
    const [cx, , cz] = pt(rm, a0, 0);
    b.add(new THREE.CylinderGeometry(0.07, 0.07, 0.03, 8), mat(cx, yb, cz), '#5e3c20', { rng, jitter: 0 });
  }
}

/** Стільниця з дощок. */
export function tableGeo() {
  const b = new Builder(), rng = rngOf(31), W = 0.95, L = 40;
  const tones = ['#9c6a3c', '#a6743f', '#94623a', '#a06c3a', '#8f5f36'];
  for (let i = -22; i <= 22; i++) {
    const z = i * W;
    let x = -L / 2 + (i % 3) * 3.1;
    while (x < L / 2) {
      const len = 6 + rng() * 7, x2 = Math.min(L / 2, x + len);
      b.add(new THREE.PlaneGeometry(x2 - x - 0.02, W - 0.025), mat((x + x2) / 2, 0, z, 0, 1, 1, 1, -Math.PI / 2), tones[Math.floor(rng() * tones.length)], { rng, jitter: 0.03 });
      x = x2;
    }
  }
  return b.geometry();
}

// ---------------------------------------------------------------------------------------------------------- фігури

/** Хата: білені стіни, призьба, вікна, двері, комин. Дах — окремо (у кольорі гравця). Фасад — +z. */
export function houseWallsGeo(big = false) {
  const b = new Builder(), r = rngOf(big ? 41 : 43);
  const w = big ? 0.32 : 0.24, d = big ? 0.22 : 0.18, h = big ? 0.15 : 0.13;
  b.add(new THREE.BoxGeometry(w + 0.03, 0.03, d + 0.03), mat(0, 0.015, 0), '#8a4a35', { rng: r, jitter: 0.04 });
  b.add(new THREE.BoxGeometry(w, h, d), mat(0, h / 2 + 0.01, 0), '#f6f1e6', { rng: r, jitter: 0.03 });
  b.add(new THREE.BoxGeometry(0.045, 0.08, 0.01), mat(-w * 0.18, 0.05, d / 2 + 0.004), '#6a4426', { rng: r, jitter: 0 });
  for (const x of big ? [w * 0.12, w * 0.34] : [w * 0.22]) {
    b.add(new THREE.BoxGeometry(0.05, 0.05, 0.008), mat(x, h * 0.62, d / 2 + 0.004), '#3f6fa6', { rng: r, jitter: 0 });
    b.add(new THREE.BoxGeometry(0.062, 0.012, 0.012), mat(x, h * 0.62 - 0.03, d / 2 + 0.006), '#6a4426', { rng: r, jitter: 0 });
  }
  b.add(new THREE.BoxGeometry(0.035, 0.09, 0.035), mat(w * 0.25, h + 0.1, -d * 0.12), '#ece4d4', { rng: r, jitter: 0.02 });
  return b.geometry();
}
/** Чотирисхилий дах (вальмовий) — білий, колір дає екземпляр. */
export function roofGeo(big = false) {
  const w = big ? 0.32 : 0.24, d = big ? 0.22 : 0.18, h = big ? 0.15 : 0.13;
  const ex = w / 2 + 0.035, ez = d / 2 + 0.035, y0 = h + 0.005, y1 = h + (big ? 0.16 : 0.14), rx = w * 0.22;
  const arr = [];
  const A = [-ex, y0, ez], B = [ex, y0, ez], C = [ex, y0, -ez], D = [-ex, y0, -ez], R1 = [-rx, y1, 0], R2 = [rx, y1, 0];
  quad(arr, A, B, R2, R1, new V3(0, 1, 1));
  quad(arr, C, D, R1, R2, new V3(0, 1, -1));
  tri(arr, B, C, R2, new V3(1, 1, 0));
  tri(arr, D, A, R1, new V3(-1, 1, 0));
  quad(arr, A, B, C, D, new V3(0, -1, 0));
  const b = new Builder();
  b.add(rawGeo(arr), null, (c) => c.y > y0 + 0.001 ? col('#ffffff') : col('#b0b0b0'), { rng: rngOf(1), jitter: 0.06 });
  // гребінь
  b.add(new THREE.BoxGeometry(rx * 2 + 0.02, 0.016, 0.022), mat(0, y1, 0), '#d8d8d8', { rng: rngOf(2), jitter: 0 });
  // Темний кант по стрісі (не тонується кольором гравця): білий/кремовий дах інакше зливався з біленими стінами й ланом.
  const k = '#2a1d12', t = 0.016;
  for (const [x, z, lx, lz] of [[0, ez, ex * 2 + t, t], [0, -ez, ex * 2 + t, t], [ex, 0, t, ez * 2], [-ex, 0, t, ez * 2]])
    b.add(new THREE.BoxGeometry(lx, 0.014, lz), mat(x, y0 + 0.002, z), k, { rng: rngOf(3), jitter: 0 });
  return b.geometry();
}

/** Містечко: велика хата + церква з банею. Повертає { walls, color } — біле й «у кольорі гравця». */
export function cityGeo() {
  const wb = new Builder(), cb = new Builder(), r = rngOf(51);
  copyColored(wb, houseWallsGeo(true), mat(-0.07, 0, 0.03));   // велика хата з власними кольорами вершин
  copyColored(cb, roofGeo(true), mat(-0.07, 0, 0.03));
  // Церква: четверик, восьмерик, баня з хрестом.
  const tx = 0.17, tz = -0.06;
  wb.add(new THREE.BoxGeometry(0.03 + 0.12, 0.03, 0.15), mat(tx, 0.015, tz), '#8a4a35', { rng: r, jitter: 0.04 });
  wb.add(new THREE.BoxGeometry(0.13, 0.2, 0.13), mat(tx, 0.11, tz), '#f6f1e6', { rng: r, jitter: 0.03 });
  wb.add(new THREE.BoxGeometry(0.035, 0.06, 0.008), mat(tx, 0.14, tz + 0.067), '#3f6fa6', { rng: r, jitter: 0 });
  wb.add(new THREE.CylinderGeometry(0.055, 0.06, 0.07, 8), mat(tx, 0.245, tz), '#f1ebdf', { rng: r, jitter: 0.03 });
  const onion = [[0, 0], [0.058, 0], [0.072, 0.03], [0.062, 0.062], [0.032, 0.092], [0.008, 0.118], [0, 0.124]].map(([x, y]) => new THREE.Vector2(x, y));
  cb.add(new THREE.LatheGeometry(onion, 8), mat(tx, 0.28, tz), '#ffffff', { rng: r, jitter: 0.05 });
  wb.add(new THREE.BoxGeometry(0.008, 0.07, 0.008), mat(tx, 0.43, tz), '#e2b443', { rng: r, jitter: 0 });
  wb.add(new THREE.BoxGeometry(0.04, 0.008, 0.008), mat(tx, 0.445, tz), '#e2b443', { rng: r, jitter: 0 });
  return { walls: wb.geometry(), color: cb.geometry() };
}
/** Скопіювати готову геометрію з її власними кольорами вершин. */
function copyColored(b, g, m) {
  const p = g.attributes.position, c = g.attributes.color, v = new V3();
  for (let i = 0; i < p.count; i++) {
    v.fromBufferAttribute(p, i).applyMatrix4(m);
    b.p.push(v.x, v.y, v.z); b.c.push(c.getX(i), c.getY(i), c.getZ(i));
  }
  g.dispose();
}

/** Шлях: три дошки вздовж ребра (вісь x, довжина ~0.62). Колір — екземпляра. */
export function roadGeo() {
  const b = new Builder(), r = rngOf(61);
  [-0.205, 0, 0.205].forEach((x, i) =>
    b.add(new THREE.BoxGeometry(0.19, 0.045, 0.105), mat(x, 0.0225 + (i === 1 ? 0.004 : 0), (i - 1) * 0.006, (i - 1) * 0.05), '#ffffff', { rng: r, jitter: 0.07 }));
  return b.geometry();
}

/** Кармелюк: темна свита, червоний пояс, смушева шапка зі шликом, на дерев'яній підставці. */
export function robberGeo() {
  const b = new Builder(), r = rngOf(71);
  b.add(new THREE.CylinderGeometry(0.11, 0.12, 0.035, 10), mat(0, 0.0175, 0), '#4e3a28', { rng: r, jitter: 0.04 });
  b.add(new THREE.CylinderGeometry(0.07, 0.11, 0.3, 8), mat(0, 0.185, 0), '#2a2830', { rng: r, jitter: 0.06 });
  b.add(new THREE.CylinderGeometry(0.075, 0.08, 0.035, 8), mat(0, 0.2, 0), '#a8302a', { rng: r, jitter: 0.04 });
  b.add(new THREE.IcosahedronGeometry(0.075, 0), mat(0, 0.33, 0, 0, 1.2, 0.8, 1.1), '#2e2c35', { rng: r, jitter: 0.06 });
  b.add(new THREE.IcosahedronGeometry(0.055, 1), mat(0, 0.41, 0), '#d6a47c', { rng: r, jitter: 0.04 });
  b.add(new THREE.BoxGeometry(0.06, 0.012, 0.012), mat(0, 0.395, 0.05), '#2a1d14', { rng: r, jitter: 0 });
  b.add(new THREE.CylinderGeometry(0.06, 0.066, 0.085, 8), mat(0, 0.485, 0), '#1c1918', { rng: r, jitter: 0.08 });
  b.add(new THREE.ConeGeometry(0.035, 0.08, 6), mat(0.035, 0.53, -0.01, 0, 1, 1, 1, 0, -1.1), '#a8302a', { rng: r, jitter: 0.04 });
  return b.geometry();
}

/** Кубик: коробка з uv на текстуру 3×2 (грані 1–6). Грань n дивиться: 1 +y, 6 −y, 2 +x, 5 −x, 3 +z, 4 −z. */
export const DIE_NORMAL = { 1: [0, 1, 0], 6: [0, -1, 0], 2: [1, 0, 0], 5: [-1, 0, 0], 3: [0, 0, 1], 4: [0, 0, -1] };
export function dieGeo(size = 0.36) {
  const g = new THREE.BoxGeometry(size, size, size);
  const order = [2, 5, 1, 6, 3, 4];   // групи BoxGeometry: +x −x +y −y +z −z
  const uv = g.attributes.uv;
  for (let f = 0; f < 6; f++) {
    const n = order[f] - 1, cx = n % 3, cy = Math.floor(n / 3);
    for (let k = 0; k < 4; k++) {
      const i = f * 4 + k;
      uv.setXY(i, (cx + uv.getX(i)) / 3, 1 - (cy + 1 - uv.getY(i)) / 2);
    }
  }
  return g;
}
export function dieTexture() {
  const S = 128, cv = document.createElement('canvas'); cv.width = S * 3; cv.height = S * 2;
  const g = cv.getContext('2d');
  const P = { 1: [[0, 0]], 2: [[-1, -1], [1, 1]], 3: [[-1, -1], [0, 0], [1, 1]], 4: [[-1, -1], [1, -1], [-1, 1], [1, 1]],
    5: [[-1, -1], [1, -1], [0, 0], [-1, 1], [1, 1]], 6: [[-1, -1], [1, -1], [-1, 0], [1, 0], [-1, 1], [1, 1]] };
  for (let n = 1; n <= 6; n++) {
    const x0 = ((n - 1) % 3) * S, y0 = Math.floor((n - 1) / 3) * S;
    g.fillStyle = '#d9c7a0'; g.fillRect(x0, y0, S, S);
    g.fillStyle = '#f4ead2'; roundRect(g, x0 + 6, y0 + 6, S - 12, S - 12, 22); g.fill();
    g.fillStyle = n === 1 ? '#b3261e' : '#3a2416';
    for (const [dx, dy] of P[n]) { g.beginPath(); g.arc(x0 + S / 2 + dx * 32, y0 + S / 2 + dy * 32, n === 1 ? 17 : 12, 0, Math.PI * 2); g.fill(); }
  }
  const t = new THREE.CanvasTexture(cv); t.colorSpace = THREE.SRGBColorSpace; t.anisotropy = 4;
  return t;
}

/** Жетон ресурсу для «роздачі»: шестигранна монетка з обідком (колір — екземпляра). */
export function chipGeo() {
  const b = new Builder(), r = rngOf(81);
  // Кольори > 1 навмисно: множаться на колір ресурсу — світла серединка, темніший обідок.
  b.add(new THREE.CylinderGeometry(0.11, 0.11, 0.03, 6), null, new THREE.Color(0.72, 0.72, 0.72), { rng: r, jitter: 0.03 });
  b.add(new THREE.CylinderGeometry(0.082, 0.082, 0.04, 6), null, new THREE.Color(1.3, 1.3, 1.3), { rng: r, jitter: 0 });
  b.add(new THREE.CylinderGeometry(0.03, 0.03, 0.046, 6), null, new THREE.Color(1.9, 1.9, 1.9), { rng: r, jitter: 0 });
  return b.geometry();
}

// ---------------------------------------------------------------------------------------------------------- знак гравця

/** Прапорець на даху: держак і табличка-фігура гравця (0 ● коло, 1 ▲ трикутник, 2 ■ квадрат, 3 ◆ ромб) — ті самі
 *  SEAT_SHAPE, що на аватарках і бейджах. Табличка біла (колір дає екземпляр) з темним кантом, що не тонується, —
 *  щоб читалась і біла на світлому полі. Початок — низ держака; табличка дивиться на +z (до камери). */
export const FLAG_H = 0.15;
export function flagGeo(shape) {
  const b = new Builder(), r = rngOf(91 + shape), K = '#22170d';
  b.add(new THREE.BoxGeometry(0.012, FLAG_H + 0.04, 0.012), mat(0, (FLAG_H + 0.04) / 2, 0), K, { rng: r, jitter: 0 });
  const y = FLAG_H, front = (g, m, c, d) => b.add(g, m, c, { rng: r, jitter: d });
  if (shape === 0) {
    front(new THREE.CylinderGeometry(0.064, 0.064, 0.012, 18), mat(0, y, 0, 0, 1, 1, 1, Math.PI / 2), K, 0);
    front(new THREE.CylinderGeometry(0.05, 0.05, 0.02, 18), mat(0, y, 0, 0, 1, 1, 1, Math.PI / 2), '#ffffff', 0.03);
  } else if (shape === 1) {
    front(new THREE.CylinderGeometry(0.082, 0.082, 0.012, 3), mat(0, y - 0.006, 0, 0, 1, 1, 1, -Math.PI / 2), K, 0);
    front(new THREE.CylinderGeometry(0.06, 0.06, 0.02, 3), mat(0, y - 0.006, 0, 0, 1, 1, 1, -Math.PI / 2), '#ffffff', 0.03);
  } else if (shape === 2) {
    front(new THREE.BoxGeometry(0.11, 0.11, 0.012), mat(0, y, 0), K, 0);
    front(new THREE.BoxGeometry(0.085, 0.085, 0.02), mat(0, y, 0), '#ffffff', 0.03);
  } else {
    front(new THREE.BoxGeometry(0.088, 0.088, 0.012), mat(0, y, 0, 0, 1, 1, 1, 0, Math.PI / 4), K, 0);
    front(new THREE.BoxGeometry(0.066, 0.066, 0.02), mat(0, y, 0, 0, 1, 1, 1, 0, Math.PI / 4), '#ffffff', 0.03);
  }
  return b.geometry();
}
/** Де на хаті стоїть прапорець (локально, до масштабу фігури): лівий край гребеня; у містечка — гребінь великої хати. */
export const FLAG_AT = { town: [-0.053, 0.27, 0], city: [-0.14, 0.31, 0.03] };

// ---------------------------------------------------------------------------------------------------------- підсвітка
// Мова підсвітки (khutory-look.md §3): золото #ffd23f, якого нема на полі, з темним кантом #2a1a00 (читається і на
// світлому лані, і на білих дахах), м'яке світіння під ним і білі «мурашки» вздовж шляхів.

export const GOLD = '#ffd23f';
const KANT = '#2a1a00';

/** Кільце для вершини: темний кант, золоте кільце, світла серединка. Лежить у площині XZ. */
export function ringGeo() {
  const b = new Builder();
  b.add(new THREE.RingGeometry(0.15, 0.215, 28), mat(0, 0, 0, 0, 1, 1, 1, -Math.PI / 2), KANT, { jitter: 0 });
  b.add(new THREE.RingGeometry(0.1, 0.163, 28), mat(0, 0.002, 0, 0, 1, 1, 1, -Math.PI / 2), GOLD, { jitter: 0 });
  b.add(new THREE.CircleGeometry(0.1, 28), mat(0, 0.001, 0, 0, 1, 1, 1, -Math.PI / 2), '#fff4c2', { jitter: 0 });
  return b.geometry();
}
/** Стрічка ребра (вісь x): темний кант і золота серединка, піднята над плиткою. */
export function stripGeo() {
  const b = new Builder();
  b.add(new THREE.BoxGeometry(0.66, 0.02, 0.17), mat(0, 0.01, 0), KANT, { jitter: 0 });
  b.add(new THREE.BoxGeometry(0.62, 0.028, 0.105), mat(0, 0.016, 0), GOLD, { jitter: 0 });
  return b.geometry();
}
/** «Мурашки» поверх стрічки — тонка смужка з uv уздовж x (текстура штрихів зсувається в часі). */
export function antsGeo() {
  const g = new THREE.PlaneGeometry(0.56, 0.032);
  g.applyMatrix4(mat(0, 0.032, 0, 0, 1, 1, 1, -Math.PI / 2));
  return g;
}
/** Площинка світіння (1×1 у XZ) під текстуру glowTexture; масштаб екземпляра робить коло чи овал. */
export function glowGeo() {
  const g = new THREE.PlaneGeometry(1, 1);
  g.applyMatrix4(mat(0, 0, 0, 0, 1, 1, 1, -Math.PI / 2));
  return g;
}
/** Обвідка гекса (Кармелюк): темний кант і золота смуга. */
export function hexRingGeo() {
  const band = (r0, r1, y) => {
    const arr = [];
    for (let i = 0; i < 6; i++) quad(arr, corner(0, 0, r1, i, y), corner(0, 0, r1, i + 1, y), corner(0, 0, r0, i + 1, y), corner(0, 0, r0, i, y), UP);
    return rawGeo(arr);
  };
  const b = new Builder();
  b.add(band(0.64, 0.97, 0), null, KANT, { jitter: 0 });
  b.add(band(0.7, 0.91, 0.003), null, GOLD, { jitter: 0 });
  return b.geometry();
}
/** М'яке світіння гекса (glowHexes — «показ», без клікабельності): заливка, що світлішає до краю. Для адитивного
 *  змішування — темніший колір додає менше світла. */
export function hexGlowGeo() {
  const arr = [], cs = [];
  const ring = (r0, r1, c0, c1) => {
    for (let i = 0; i < 6; i++) {
      const a = corner(0, 0, r1, i, 0), b2 = corner(0, 0, r1, i + 1, 0), c = corner(0, 0, r0, i + 1, 0), d = corner(0, 0, r0, i, 0);
      arr.push(...a, ...c, ...b2, ...a, ...d, ...c);
      cs.push(...c1, ...c0, ...c1, ...c1, ...c0, ...c0);
    }
  };
  const k = (hex, m) => { const c = col(hex); return [c.r * m, c.g * m, c.b * m]; };
  ring(0, 0.62, k(GOLD, 0.12), k(GOLD, 0.2));
  ring(0.62, 0.86, k(GOLD, 0.2), k(GOLD, 0.75));
  ring(0.86, 0.96, k(GOLD, 0.75), k(GOLD, 0.15));
  const g = rawGeo(arr);
  g.setAttribute('color', new THREE.Float32BufferAttribute(cs, 3));
  return g;
}

/** Радіальне золоте світіння (центр яскравий, край прозорий). */
export function glowTexture() {
  const S = 64, cv = document.createElement('canvas'); cv.width = cv.height = S;
  const g = cv.getContext('2d');
  const gr = g.createRadialGradient(S / 2, S / 2, 0, S / 2, S / 2, S / 2);
  gr.addColorStop(0, 'rgba(255,224,110,1)');
  gr.addColorStop(0.4, 'rgba(255,210,63,0.7)');
  gr.addColorStop(1, 'rgba(255,210,63,0)');
  g.fillStyle = gr; g.fillRect(0, 0, S, S);
  const t = new THREE.CanvasTexture(cv); t.colorSpace = THREE.SRGBColorSpace;
  return t;
}
/** Штрихи «мурашок»: білий штрих і прозорий проміжок; повтор по u, зсув offset.x рухає їх уздовж стрічки. */
export function antsTexture() {
  const cv = document.createElement('canvas'); cv.width = 32; cv.height = 4;
  const g = cv.getContext('2d');
  g.fillStyle = '#ffffff'; g.fillRect(0, 0, 14, 4);
  const t = new THREE.CanvasTexture(cv);
  t.wrapS = THREE.RepeatWrapping; t.repeat.set(6, 1);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}
