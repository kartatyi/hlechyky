// «Скарб Полуботка» — процедурні low-poly моделі для 3D-сцени (scene.js). Лише геометрія three.js, жодних файлів:
// грудки ґрунту, валуни, кріплення, ліхтарі, дошки міток, скриня, монети, фігурки копачів, кроти, мапа, підсвітка,
// і все нерухоме (поверхня з хатою й журавлем, шахта, задня стінка, декоративні стовпці, небо).
//
// Стиль — «розріз землі під селом»: пласке затінення, кольори вершин із легким розкидом яскравості по гранях.
// Нерухоме зливається в ОДИН меш (Builder), однакові рухомі речі — InstancedMesh у scene.js. Тут лише «з чого зліплено».
//
// Система координат (контракт §4.2): клітинка (x, y) → центр X = x, Y = −y, лицьова площина розрізу Z = 0, камера з +Z.
// Грудки займають Z ∈ [−0.6, 0] (передня грань — на Z = 0), задня стінка тунелю — Z = −0.45.

import * as THREE from '/static/vendor/three-0.186.1/three.module.js';
import { STRATA, PAL } from './consts.js';

const V3 = THREE.Vector3;
export const L = 1 / 3;            // грудка — третина клітинки
export const LUMP_D = 0.6;         // глибина грудки
export const BACK_Z = -0.45;       // задня стінка тунелю
export const TUN_Z = -0.22;        // середина глибини тунелю (ліхтарі)
export const FIG_H = 0.44;         // висота фігурки

// ---------------------------------------------------------------------------------------------------------- кольори

const colCache = new Map();
/** Колір із hex-рядка (sRGB → лінійний робочий простір three.js). Кешується — не мутувати! */
export function col(hex) {
  let c = colCache.get(hex);
  if (!c) { c = new THREE.Color(hex); colCache.set(hex, c); }
  return c;
}
export function mix(a, b, k) { return col(a).clone().lerp(col(b), k); }

/** Колір шару ґрунту за рядком y (STRATA, межі включно). */
export function strataHex(y) {
  for (const s of STRATA) if (y >= s.y0 && y <= s.y1) return s.color;
  return y < STRATA[0].y0 ? STRATA[0].color : STRATA[STRATA.length - 1].color;
}

/** Детермінований генератор (mulberry32). */
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

// ---------------------------------------------------------------------------------------------------------- будівник

const _p = new V3(), _q = new THREE.Quaternion(), _e = new THREE.Euler(), _s = new V3();
/** Матриця: зсув, поворот (порядок YXZ), масштаб. */
export function mat(x = 0, y = 0, z = 0, ry = 0, sx = 1, sy = sx, sz = sx, rx = 0, rz = 0) {
  _e.set(rx, ry, rz, 'YXZ');
  return new THREE.Matrix4().compose(_p.set(x, y, z), _q.setFromEuler(_e), _s.set(sx, sy, sz));
}

/** Зливає геометрії в одну неіндексовану з кольорами вершин. add(geo, matrix, color | (centroid, rng) => Color, opt). */
export class Builder {
  constructor() { this.p = []; this.c = []; }
  add(g, m = null, color = '#ffffff', opt = {}) {
    const src = g.index ? g.toNonIndexed() : g;
    const pos = src.attributes.position;
    const jit = opt.jitter ?? 0.06, rng = opt.rng ?? Math.random;
    const a = new V3(), b = new V3(), c = new V3(), cen = new V3();
    const vc = opt.vertexColor;   // (v: V3 локальна) => Color — колір на вершину (градієнти)
    const fixed = vc || typeof color === 'function' ? null : (color instanceof THREE.Color ? color : col(color));
    for (let i = 0; i < pos.count; i += 3) {
      a.fromBufferAttribute(pos, i); b.fromBufferAttribute(pos, i + 1); c.fromBufferAttribute(pos, i + 2);
      const cc = vc ? null : (fixed ?? color(cen.copy(a).add(b).add(c).divideScalar(3), rng));
      const ca = vc ? vc(a) : cc, cb = vc ? vc(b) : cc, cd = vc ? vc(c) : cc;
      if (m) { a.applyMatrix4(m); b.applyMatrix4(m); c.applyMatrix4(m); }
      const j = 1 + (rng() - 0.5) * 2 * jit;
      this.p.push(a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z);
      this.c.push(ca.r * j, ca.g * j, ca.b * j, cb.r * j, cb.g * j, cb.b * j, cd.r * j, cd.g * j, cd.b * j);
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
    g.computeVertexNormals();
    g.computeBoundingSphere();
    return g;
  }
}

function rawGeo(arr) {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(arr, 3));
  return g;
}
/** Трикутник з нормаллю в бік dir. */
function tri(arr, a, b, c, dir) {
  const ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
  const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
  if (nx * dir.x + ny * dir.y + nz * dir.z < 0) arr.push(...a, ...c, ...b); else arr.push(...a, ...b, ...c);
}
function quad(arr, a, b, c, d, dir) { tri(arr, a, b, c, dir); tri(arr, a, c, d, dir); }

/** Брусок між двома точками. */
function beam(b, p1, p2, w, h, color, rng, jitter = 0.08) {
  const dx = p2[0] - p1[0], dy = p2[1] - p1[1], dz = p2[2] - p1[2];
  const len = Math.hypot(dx, dy, dz);
  const m = new THREE.Matrix4().compose(
    new V3((p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2, (p1[2] + p2[2]) / 2),
    new THREE.Quaternion().setFromUnitVectors(new V3(1, 0, 0), new V3(dx, dy, dz).normalize()), new V3(1, 1, 1));
  b.add(new THREE.BoxGeometry(len, h, w), m, color, { rng, jitter });
}
/** Колода (циліндр) між двома точками. */
function log(b, p1, p2, r, color, rng, seg = 6, jitter = 0.08) {
  const dx = p2[0] - p1[0], dy = p2[1] - p1[1], dz = p2[2] - p1[2];
  const len = Math.hypot(dx, dy, dz);
  const m = new THREE.Matrix4().compose(
    new V3((p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2, (p1[2] + p2[2]) / 2),
    new THREE.Quaternion().setFromUnitVectors(new V3(0, 1, 0), new V3(dx, dy, dz).normalize()), new V3(1, 1, 1));
  b.add(new THREE.CylinderGeometry(r, r * 1.05, len, seg), m, color, { rng, jitter });
}

const ZP = new V3(0, 0, 1);

// ---------------------------------------------------------------------------------------------------------- грудка

/** Грудка ґрунту: кубик 1/3 × 1/3 × 0.6 з фаскою спереду й легенькою «опуклістю» лиця. Біла — колір дає екземпляр.
 *  Центр — (0, 0, −0.3): передня грань на z = 0. Задньої грані нема (її ніколи не видно). */
export function lumpTris() {
  const w = L / 2, c = 0.05, bul = 0.024, fz = 0, bz = -c, zb = -LUMP_D;
  const arr = [];
  const A = [-w + c, w - c, fz], B = [w - c, w - c, fz], C = [w - c, -w + c, fz], D = [-w + c, -w + c, fz], O = [0.01, 0.006, fz + bul];
  const A2 = [-w, w, bz], B2 = [w, w, bz], C2 = [w, -w, bz], D2 = [-w, -w, bz];
  const A3 = [-w, w, zb], B3 = [w, w, zb], C3 = [w, -w, zb], D3 = [-w, -w, zb];
  tri(arr, O, A, B, ZP); tri(arr, O, B, C, ZP); tri(arr, O, C, D, ZP); tri(arr, O, D, A, ZP);
  quad(arr, A, B, B2, A2, new V3(0, 1, 1)); quad(arr, B, C, C2, B2, new V3(1, 0, 1));
  quad(arr, C, D, D2, C2, new V3(0, -1, 1)); quad(arr, D, A, A2, D2, new V3(-1, 0, 1));
  quad(arr, A2, B2, B3, A3, new V3(0, 1, 0)); quad(arr, B2, C2, C3, B3, new V3(1, 0, 0));
  quad(arr, C2, D2, D3, C3, new V3(0, -1, 0)); quad(arr, D2, A2, A3, D3, new V3(-1, 0, 0));
  return arr;
}
export function lumpGeo() {
  const b = new Builder();
  // Лице світліше, боки темніші (у тунелі видно саме боки) — так грудки читаються навіть без тіней.
  b.add(rawGeo(lumpTris()), null, (c) => c.z > -0.01 ? col('#ffffff') : c.z > -0.06 ? col('#d9d1c8') : col('#a39a92'),
    { rng: rngOf(3), jitter: 0.05 });
  return b.geometry();
}

/** Валун глухої карти. */
export function boulderGeo() {
  const b = new Builder(), r = rngOf(17);
  const tones = ['#8d8f94', '#7c7e84', '#9a9ca1', '#6f7177'];
  b.add(new THREE.IcosahedronGeometry(0.15, 0), mat(0, 0, 0, 0.4, 1.1, 0.95, 0.8, 0.3), () => col(tones[Math.floor(r() * 4)]), { rng: r, jitter: 0.06 });
  b.add(new THREE.IcosahedronGeometry(0.07, 0), mat(0.09, -0.08, 0.05, 1), '#6f7177', { rng: r, jitter: 0.06 });
  return b.geometry();
}

/** Кріплення відкритого краю: два стовпчики й перекладина біля задньої стінки + дві балки стелі. Вісь тунелю — +X. */
export function supportGeo() {
  const b = new Builder(), r = rngOf(13), h = L;
  const dark = mix(PAL.wood, '#2a1a0e', 0.35);
  for (const x of [-0.115, 0.115]) b.add(new THREE.BoxGeometry(0.05, h, 0.05), mat(x, 0, -0.395, 0, 1, 1, 1, 0, x * 0.1), PAL.wood, { rng: r, jitter: 0.1 });
  b.add(new THREE.BoxGeometry(0.31, 0.05, 0.06), mat(0, h / 2 - 0.027, -0.39), dark, { rng: r, jitter: 0.08 });
  for (const x of [-0.115, 0.115]) b.add(new THREE.BoxGeometry(0.045, 0.04, 0.4), mat(x, h / 2 - 0.022, -0.2), dark, { rng: r, jitter: 0.08 });
  return b.geometry();
}

/** Ліхтар: гачок, дашок, денце (темне залізо). Скло — окремо (емісив). Початок — точка кріплення до стелі. */
export function lanternFrameGeo() {
  const b = new Builder(), r = rngOf(19), iron = '#3a3028';
  b.add(new THREE.BoxGeometry(0.008, 0.05, 0.008), mat(0, -0.025, 0), iron, { rng: r, jitter: 0 });
  b.add(new THREE.TorusGeometry(0.012, 0.004, 3, 6), mat(0, -0.055, 0), iron, { rng: r, jitter: 0 });
  b.add(new THREE.ConeGeometry(0.052, 0.04, 6), mat(0, -0.083, 0), iron, { rng: r, jitter: 0.05 });
  b.add(new THREE.CylinderGeometry(0.044, 0.044, 0.012, 6), mat(0, -0.162, 0), iron, { rng: r, jitter: 0.05 });
  for (let i = 0; i < 3; i++) {
    const a = i * Math.PI * 2 / 3 + 0.5;
    b.add(new THREE.BoxGeometry(0.006, 0.07, 0.006), mat(Math.cos(a) * 0.036, -0.123, Math.sin(a) * 0.036), iron, { rng: r, jitter: 0 });
  }
  return b.geometry();
}
export function lanternGlassGeo() {
  const b = new Builder();
  b.add(new THREE.CylinderGeometry(0.034, 0.034, 0.068, 6), mat(0, -0.122, 0), '#ffffff', { jitter: 0 });
  return b.geometry();
}

/** Пляма світла: диск, яскравий у центрі й чорний по краю (для адитивного змішування). Лежить у площині XY. */
export function glowGeo(seg = 20) {
  const arr = [], cols = [];
  for (let i = 0; i < seg; i++) {
    const a0 = i / seg * Math.PI * 2, a1 = (i + 1) / seg * Math.PI * 2;
    arr.push(0, 0, 0, Math.cos(a0), Math.sin(a0), 0, Math.cos(a1), Math.sin(a1), 0);
    cols.push(1, 1, 1, 0, 0, 0, 0, 0, 0);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(arr, 3));
  g.setAttribute('color', new THREE.Float32BufferAttribute(cols, 3));
  return g;
}

// ---------------------------------------------------------------------------------------------------------- мітки

/** Дошка закритої мітки: три дошки, випалений «✕», три цвяхи. Центр у (0,0,0), лице на z = +0.035. */
export function goalBoardGeo() {
  const b = new Builder(), r = rngOf(23);
  const tones = ['#9a6b3c', '#8c5f34', '#a5753f'];
  for (let i = -1; i <= 1; i++)
    b.add(new THREE.BoxGeometry(0.285, 0.86, 0.06), mat(i * 0.29, (r() - 0.5) * 0.02, 0, 0, 1, 1, 1, 0, (r() - 0.5) * 0.02), tones[i + 1], { rng: r, jitter: 0.07 });
  b.add(new THREE.BoxGeometry(0.84, 0.07, 0.03), mat(0, 0.28, 0.035), '#7a4f2a', { rng: r, jitter: 0.06 });
  b.add(new THREE.BoxGeometry(0.84, 0.07, 0.03), mat(0, -0.28, 0.035), '#7a4f2a', { rng: r, jitter: 0.06 });
  // Випалений хрест: обвуглена облямівка і чорна серединка.
  for (const a of [Math.PI / 4, -Math.PI / 4]) {
    b.add(new THREE.BoxGeometry(0.66, 0.11, 0.01), mat(0, 0, 0.052, 0, 1, 1, 1, 0, a), '#5a3418', { rng: r, jitter: 0.04 });
    b.add(new THREE.BoxGeometry(0.6, 0.06, 0.01), mat(0, 0, 0.058, 0, 1, 1, 1, 0, a), '#1c120b', { rng: r, jitter: 0.03 });
  }
  for (const [x, y] of [[-0.33, 0.33], [0.33, 0.33], [0, -0.36]])
    b.add(new THREE.CylinderGeometry(0.022, 0.022, 0.02, 6), mat(x, y, 0.045, 0, 1, 1, 1, Math.PI / 2), '#5d6168', { rng: r, jitter: 0.05 });
  return b.geometry();
}

/** Значок підглянутого: золота монета (дивиться на +z). */
export function peekCoinGeo() {
  const b = new Builder(), r = rngOf(29);
  b.add(new THREE.CylinderGeometry(0.1, 0.1, 0.024, 14), mat(0, 0, 0, 0, 1, 1, 1, Math.PI / 2), '#e8b23a', { rng: r, jitter: 0.03 });
  b.add(new THREE.CylinderGeometry(0.07, 0.07, 0.03, 14), mat(0, 0, 0, 0, 1, 1, 1, Math.PI / 2), PAL.gold, { rng: r, jitter: 0.02 });
  b.add(new THREE.BoxGeometry(0.025, 0.07, 0.036), mat(0, 0, 0), '#b8862a', { rng: r, jitter: 0 });
  return b.geometry();
}
/** Значок підглянутого: сірий камінь. */
export function peekStoneGeo() {
  const b = new Builder(), r = rngOf(31);
  b.add(new THREE.DodecahedronGeometry(0.085, 0), mat(0, 0, 0, 0.5, 1.1, 0.85, 0.8), PAL.stone, { rng: r, jitter: 0.1 });
  b.add(new THREE.DodecahedronGeometry(0.045, 0), mat(0.07, -0.04, 0.03, 1), '#6f7177', { rng: r, jitter: 0.1 });
  return b.geometry();
}

/** Камінь (одиничний; розмір і колір — екземпляра). */
export function rockGeo() {
  const b = new Builder(), r = rngOf(37);
  b.add(new THREE.DodecahedronGeometry(1, 0), mat(0, 0, 0, 0, 1, 0.8, 0.9), '#ffffff', { rng: r, jitter: 0.12 });
  return b.geometry();
}

/** Кована скриня: тіло з окуттям, золото всередині. Основа в (0,0,0), фасад — +z. Кришка — окремо. */
export const CHEST = { w: 0.46, h: 0.22, d: 0.28 };
export function chestBodyGeo() {
  const b = new Builder(), r = rngOf(41), { w, h, d } = CHEST, iron = '#3b3634';
  b.add(new THREE.BoxGeometry(w, h, d), mat(0, h / 2, 0), '#6b4423', { rng: r, jitter: 0.08 });
  for (const x of [-w * 0.36, w * 0.36]) b.add(new THREE.BoxGeometry(0.04, h + 0.01, d + 0.012), mat(x, h / 2, 0), iron, { rng: r, jitter: 0.05 });
  b.add(new THREE.BoxGeometry(w + 0.012, 0.03, d + 0.012), mat(0, 0.015, 0), iron, { rng: r, jitter: 0.05 });
  b.add(new THREE.BoxGeometry(w + 0.012, 0.025, d + 0.012), mat(0, h - 0.012, 0), iron, { rng: r, jitter: 0.05 });
  b.add(new THREE.BoxGeometry(0.07, 0.08, 0.02), mat(0, h - 0.05, d / 2 + 0.01), '#d4a537', { rng: r, jitter: 0 });
  b.add(new THREE.BoxGeometry(0.018, 0.03, 0.01), mat(0, h - 0.055, d / 2 + 0.022), '#2a1d10', { rng: r, jitter: 0 });
  // Золото всередині — горбок над краєм.
  b.add(new THREE.SphereGeometry(w * 0.46, 8, 3, 0, Math.PI * 2, 0, Math.PI / 2), mat(0, h - 0.02, 0, 0, 1, 0.32, d / w * 0.95), PAL.gold, { rng: r, jitter: 0.12 });
  return b.geometry();
}
/** Кришка: півциліндр уздовж X; початок — на завісі (задній верхній край), кришка лежить у +z. */
export function chestLidGeo() {
  const b = new Builder(), r = rngOf(43), { w, d } = CHEST, iron = '#3b3634';
  const R = d / 2;
  b.add(new THREE.CylinderGeometry(R, R, w, 10, 1, false, 0, Math.PI), mat(0, 0, R, 0, 0.7, 1, 1, 0, Math.PI / 2), '#7a4e28', { rng: r, jitter: 0.08 });
  // Споднє кришки — дошка (відкрита кришка дивиться саме нею), з окуттям по краю.
  b.add(new THREE.BoxGeometry(w, 0.022, d), mat(0, 0.011, R), '#5a3a1e', { rng: r, jitter: 0.08 });
  b.add(new THREE.BoxGeometry(w + 0.01, 0.026, 0.03), mat(0, 0.012, d - 0.015), iron, { rng: r, jitter: 0.04 });
  for (const x of [-w * 0.36, 0, w * 0.36])
    b.add(new THREE.CylinderGeometry(R + 0.008, R + 0.008, 0.035, 10, 1, true, 0, Math.PI), mat(x, 0, R, 0, 0.7, 1, 1, 0, Math.PI / 2), iron, { rng: r, jitter: 0.04 });
  return b.geometry();
}
/** Монета (вісь Y). */
export function coinGeo() {
  const b = new Builder(), r = rngOf(47);
  b.add(new THREE.CylinderGeometry(0.045, 0.045, 0.012, 9), null, (c) => Math.abs(c.y) > 0.005 ? col(PAL.gold) : col('#c9952e'), { rng: r, jitter: 0.04 });
  return b.geometry();
}
/** Клубок пилу. */
export function dustGeo() {
  const b = new Builder(), r = rngOf(53);
  b.add(new THREE.IcosahedronGeometry(1, 0), null, '#ffffff', { rng: r, jitter: 0.08 });
  return b.geometry();
}

// ---------------------------------------------------------------------------------------------------------- фігурки

/** Сорочка й підставка (біле — колір гравця з екземпляра; підставка темніша). Основа в нулі, лицем до +z. */
export function figShirtGeo() {
  const b = new Builder(), r = rngOf(59);
  b.add(new THREE.CylinderGeometry(0.085, 0.095, 0.03, 12), mat(0, 0.015, 0), new THREE.Color(0.5, 0.5, 0.5), { rng: r, jitter: 0.03 });
  b.add(new THREE.CylinderGeometry(0.048, 0.074, 0.15, 7), mat(0, 0.205, 0), '#ffffff', { rng: r, jitter: 0.05 });
  for (const s of [-1, 1])
    b.add(new THREE.CylinderGeometry(0.017, 0.021, 0.12, 5), mat(s * 0.064, 0.205, 0.005, 0, 1, 1, 1, 0, s * 0.3), '#ffffff', { rng: r, jitter: 0.05 });
  return b.geometry();
}
/** Решта фігурки: штани, чоботи, пояс, руки, голова з вусами, бриль. */
export function figBodyGeo() {
  const b = new Builder(), r = rngOf(61);
  for (const s of [-1, 1]) {
    b.add(new THREE.BoxGeometry(0.036, 0.1, 0.042), mat(s * 0.024, 0.08, 0), '#3a3430', { rng: r, jitter: 0.05 });
    b.add(new THREE.BoxGeometry(0.042, 0.03, 0.06), mat(s * 0.024, 0.045, 0.008), '#241a14', { rng: r, jitter: 0.04 });
    b.add(new THREE.IcosahedronGeometry(0.019, 0), mat(s * 0.082, 0.145, 0.012), '#e3b38a', { rng: r, jitter: 0.03 });
  }
  b.add(new THREE.CylinderGeometry(0.069, 0.071, 0.022, 7), mat(0, 0.142, 0), '#b3302a', { rng: r, jitter: 0.04 });
  b.add(new THREE.IcosahedronGeometry(0.05, 1), mat(0, 0.33, 0), '#e3b38a', { rng: r, jitter: 0.03 });
  b.add(new THREE.BoxGeometry(0.06, 0.012, 0.012), mat(0, 0.316, 0.047, 0, 1, 1, 1, 0, 0), '#3a2a1a', { rng: r, jitter: 0 });
  for (const s of [-1, 1]) b.add(new THREE.BoxGeometry(0.01, 0.01, 0.006), mat(s * 0.018, 0.338, 0.047), '#1a120c', { rng: r, jitter: 0 });
  b.add(new THREE.CylinderGeometry(0.092, 0.092, 0.012, 12), mat(0, 0.366, 0), '#e2c46c', { rng: r, jitter: 0.05 });
  b.add(new THREE.CylinderGeometry(0.044, 0.05, 0.05, 10), mat(0, 0.395, 0), '#d8b85e', { rng: r, jitter: 0.05 });
  b.add(new THREE.CylinderGeometry(0.051, 0.051, 0.013, 10), mat(0, 0.378, 0), '#4a3020', { rng: r, jitter: 0 });
  return b.geometry();
}
/** Кирка: держак уздовж Y (центр у нулі), залізо вгорі. */
export function pickGeo() {
  const b = new Builder(), r = rngOf(67);
  b.add(new THREE.BoxGeometry(0.018, 0.32, 0.018), mat(0, 0, 0), '#9a6b3c', { rng: r, jitter: 0.06 });
  for (const s of [-1, 1])
    b.add(new THREE.BoxGeometry(0.11, 0.024, 0.022), mat(s * 0.05, 0.15 - 0.012, 0, 0, 1, 1, 1, 0, -s * 0.2), '#8a9099', { rng: r, jitter: 0.05 });
  return b.geometry();
}

/** Кріт: сіро-бурий еліпсоїд, рожевий ніс і лапки. Визирає в +z; центр тулуба в нулі. */
export function moleGeo() {
  const b = new Builder(), r = rngOf(71);
  b.add(new THREE.SphereGeometry(0.14, 8, 6), mat(0, 0, 0, 0, 1, 0.82, 1.15), '#857262', { rng: r, jitter: 0.06 });
  b.add(new THREE.ConeGeometry(0.045, 0.1, 7), mat(0, -0.01, 0.17, 0, 1, 1, 1, Math.PI / 2), '#cf8a95', { rng: r, jitter: 0.03 });
  b.add(new THREE.IcosahedronGeometry(0.026, 0), mat(0, -0.01, 0.22), '#ee9aa8', { rng: r, jitter: 0 });
  for (const s of [-1, 1]) {
    b.add(new THREE.IcosahedronGeometry(0.014, 0), mat(s * 0.055, 0.05, 0.12), '#0d0a08', { rng: r, jitter: 0 });
    b.add(new THREE.BoxGeometry(0.07, 0.022, 0.06), mat(s * 0.11, -0.08, 0.1, s * 0.4), '#e29aa5', { rng: r, jitter: 0.04 });
  }
  return b.geometry();
}

// ---------------------------------------------------------------------------------------------------------- мапа

/** Пергамент Старої мапи: x ∈ [0, 1] (розгортається масштабом x), висота 0.62; червоний пунктир і хрестик. */
export function mapSheetGeo() {
  const b = new Builder(), r = rngOf(73), H = 0.62;
  b.add(new THREE.PlaneGeometry(1, H, 6, 2), mat(0.5, 0, 0), (c) => c.x > 0.2 && c.x < 0.3 ? mix(PAL.map, '#c9a86a', 0.3) : col(PAL.map), { rng: r, jitter: 0.06 });
  const path = [];
  for (let i = 0; i <= 8; i++) { const t = i / 8; path.push([0.1 + t * 0.72, -0.2 + t * 0.32 + Math.sin(t * 7) * 0.07]); }
  for (let i = 0; i < path.length - 1; i++) {
    const [x1, y1] = path[i], [x2, y2] = path[i + 1];
    const mx = (x1 + x2) / 2, my = (y1 + y2) / 2, a = Math.atan2(y2 - y1, x2 - x1);
    b.add(new THREE.BoxGeometry(0.045, 0.014, 0.004), mat(mx, my, 0.004, 0, 1, 1, 1, 0, a), '#b8302a', { rng: r, jitter: 0 });
  }
  for (const a of [Math.PI / 4, -Math.PI / 4]) b.add(new THREE.BoxGeometry(0.1, 0.022, 0.004), mat(0.86, 0.15, 0.005, 0, 1, 1, 1, 0, a), '#b8302a', { rng: r, jitter: 0 });
  // крихітна хатка на початку пунктиру
  b.add(new THREE.BoxGeometry(0.05, 0.035, 0.003), mat(0.08, -0.23, 0.004), '#6b4a2b', { rng: r, jitter: 0 });
  return b.geometry();
}
/** Валик мапи: циліндр уздовж Y. */
export function mapRollGeo() {
  const b = new Builder(), r = rngOf(79);
  b.add(new THREE.CylinderGeometry(0.035, 0.035, 0.66, 8), null, mix(PAL.map, '#8a6a3a', 0.25), { rng: r, jitter: 0.05 });
  for (const s of [-1, 1]) b.add(new THREE.CylinderGeometry(0.02, 0.02, 0.05, 6), mat(0, s * 0.35, 0), '#5a3a20', { rng: r, jitter: 0 });
  return b.geometry();
}
/** Промінь ліхтаря: відкритий конус, вершина вгорі (y = 0), розкривається донизу на довжину 1. */
export function beamGeo() {
  const g = new THREE.ConeGeometry(0.45, 1, 14, 1, true);
  g.translate(0, -0.5, 0);
  const src = g.toNonIndexed(); g.dispose();
  const p = src.attributes.position, cols = [];
  for (let i = 0; i < p.count; i++) { const k = Math.max(0, Math.min(1, 1 + p.getY(i))); const v = 0.03 + 0.22 * k; cols.push(v, v * 0.88, v * 0.6); }
  src.setAttribute('color', new THREE.Float32BufferAttribute(cols, 3));
  return src;
}

// ---------------------------------------------------------------------------------------------------------- підсвітка

/** Рамка законної клітинки: темна облямівка + світла смуга. Біле — колір екземпляра. Площина XY. */
export function frameGeo() {
  const b = new Builder();
  const band = (o, i, z, color) => {
    for (const [x, y, w, h] of [[0, (o + i) / 2, 2 * o, o - i], [0, -(o + i) / 2, 2 * o, o - i], [(o + i) / 2, 0, o - i, 2 * i], [-(o + i) / 2, 0, o - i, 2 * i]])
      b.add(new THREE.PlaneGeometry(w, h), mat(x, y, z), color, { jitter: 0 });
  };
  band(0.49, 0.37, 0, new THREE.Color(0.12, 0.1, 0.08));
  band(0.465, 0.395, 0.004, '#ffffff');
  return b.geometry();
}
/** Значок «⟲» (законно лише в іншому повороті). */
export function altIconGeo() {
  const b = new Builder();
  const R = 0.12;
  b.add(new THREE.TorusGeometry(R, 0.036, 3, 18, Math.PI * 1.5), mat(0, 0, -0.004, 0, 1, 1, 1, 0, Math.PI * 0.6), new THREE.Color(0.12, 0.1, 0.08), { jitter: 0 });
  b.add(new THREE.TorusGeometry(R, 0.022, 3, 18, Math.PI * 1.5), mat(0, 0, 0, 0, 1, 1, 1, 0, Math.PI * 0.6), '#ffffff', { jitter: 0 });
  const a = Math.PI * 0.6, ex = R * Math.cos(a), ey = R * Math.sin(a);
  b.add(new THREE.ConeGeometry(0.06, 0.09, 3), mat(ex, ey + 0.02, 0, 0, 1, 1, 0.3, 0, a + Math.PI * 0.05), '#ffffff', { jitter: 0 });
  return b.geometry();
}
/** Курсор клавіатури/джойстика: чотири кутики. */
export function cursorGeo() {
  const b = new Builder(), o = 0.55, t = 0.05, l = 0.2;
  for (const [sx, sy] of [[-1, -1], [1, -1], [1, 1], [-1, 1]]) {
    b.add(new THREE.PlaneGeometry(l + 0.04, t + 0.04), mat(sx * (o - l / 2), sy * o, 0), new THREE.Color(0.08, 0.06, 0.05), { jitter: 0 });
    b.add(new THREE.PlaneGeometry(t + 0.04, l + 0.04), mat(sx * o, sy * (o - l / 2), 0), new THREE.Color(0.08, 0.06, 0.05), { jitter: 0 });
    b.add(new THREE.PlaneGeometry(l, t), mat(sx * (o - l / 2), sy * o, 0.004), '#ffffff', { jitter: 0 });
    b.add(new THREE.PlaneGeometry(t, l), mat(sx * o, sy * (o - l / 2), 0.004), '#ffffff', { jitter: 0 });
  }
  return b.geometry();
}

// ---------------------------------------------------------------------------------------------------------- нерухоме

/**
 * Усе нерухоме для поля grid: { lit, glow, sky, craneArm, craneRope, pivot, tip, surfY }.
 *   lit — освітлений меш (поверхня, хата, тин, вишні, соняшники, копиця, зруб шахти, задня стінка, декоративні стовпці,
 *         скеля внизу); glow — світлі вікна (без світла); sky — небо, пагорби, місяць, зорі (без туману й світла).
 *   craneArm / craneRope — рухомі частини журавля (жердина з противагою відносно pivot; мотузка з відром відносно tip).
 */
export function buildStatic(grid, bgHex) {
  const { x0, x1, y0, y1 } = grid;
  const sx = grid.shaft?.length ? grid.shaft[0][0] : (grid.start?.[0] ?? 0);
  const surfY = -y0 + 0.5, botY = -y1 - 0.5;
  const XL = x0 - 0.5, XR = x1 + 0.5;
  const lit = new Builder(), glow = new Builder(), sky = new Builder();
  const r = rngOf(101);
  const bg = col(bgHex);

  // Задня стінка тунелів — темна, з ледь помітними плямами.
  const back = new THREE.PlaneGeometry(XR - XL, surfY - botY, Math.round(XR - XL), Math.round(surfY - botY));
  lit.add(back, mat((XL + XR) / 2, (surfY + botY) / 2, BACK_Z), () => col(PAL.tunnel).clone().multiplyScalar(0.85 + r() * 0.3), { rng: r, jitter: 0.04 });

  // Декоративні стовпці землі ліворуч і праворуч — темнішають до кольору тьми («тонуть у тумані»).
  const lump = rawGeo(lumpTris());
  for (const side of [-1, 1]) {
    for (let k = 1; k <= 3; k++) {
      const x = side < 0 ? x0 - k : x1 + k;
      const fade = k === 1 ? 0.38 : k === 2 ? 0.66 : 0.86;
      for (let y = y0; y <= y1; y++) for (let j = 0; j < 9; j++) {
        const cx = (j % 3) - 1, cy = Math.floor(j / 3) - 1;
        const c = col(strataHex(y)).clone().multiplyScalar(0.92 + r() * 0.16).lerp(bg, fade);
        lit.add(lump, mat(x + cx * L, -y - cy * L, (r() - 0.5) * 0.03, 0, 0.98 + r() * 0.04, 0.98 + r() * 0.04, 1, 0, Math.floor(r() * 4) * Math.PI / 2),
          (cc) => cc.z > -0.01 ? c : c.clone().multiplyScalar(0.8), { rng: r, jitter: 0.05, keep: true });
      }
    }
  }
  lump.dispose();

  // Скеля під полем.
  for (let x = XL - 3; x < XR + 3; x += 0.5) {
    for (const [y, hh] of [[botY - 0.2, 0.4], [botY - 0.62, 0.44]]) {
      const out = Math.max(0, Math.max(XL - x, x - XR)) / 3;
      const c = col('#5d5a57').clone().multiplyScalar(0.75 + r() * 0.3).lerp(bg, Math.min(0.9, 0.25 + out * 0.7 + (y < botY - 0.5 ? 0.25 : 0)));
      lit.add(new THREE.BoxGeometry(0.5, hh, 0.6), mat(x + 0.25, y, -0.3 + (r() - 0.5) * 0.06, 0, 1, 1, 1, 0, (r() - 0.5) * 0.08), c, { rng: r, jitter: 0.06 });
    }
  }

  // ---- поверхня: трава з глибиною, переднє «дерно» й корінці.
  const GZ0 = 0.02, GZ1 = -9, GX0 = XL - 3.5, GX1 = XR + 3.5, step = 0.5;
  for (let x = GX0; x < GX1; x += step) for (let z = GZ0; z > GZ1; z -= step) {
    const z2 = Math.max(GZ1, z - step), x2 = Math.min(GX1, x + step);
    const out = Math.max(0, Math.max(XL - x, x - XR)) / 3.5;
    const far = Math.min(1, -z / 9);
    const arr = [];
    const h = (xx, zz) => surfY + (zz < -1 ? Math.sin(xx * 0.7 + zz * 0.5) * 0.04 * Math.min(1, -zz / 3) : 0);
    const A = [x, h(x, z), z], B = [x2, h(x2, z), z], C = [x2, h(x2, z2), z2], D = [x, h(x, z2), z2];
    quad(arr, A, B, C, D, new V3(0, 1, 0));
    const base = col(PAL.grass).clone().multiplyScalar(0.82 + r() * 0.2).lerp(col('#1c2a22'), far * 0.55).lerp(bg, Math.min(0.85, out * 0.8));
    lit.add(rawGeo(arr), null, base, { rng: r, jitter: 0.07 });
  }
  // дерно по передньому краю (над розрізом)
  lit.add(new THREE.BoxGeometry(XR - XL + 1, 0.07, 0.06), mat((XL + XR) / 2, surfY - 0.03, 0.0), '#2c3b1e', { rng: r, jitter: 0.08 });
  for (let i = 0; i < 70; i++) {
    const x = XL + r() * (XR - XL);
    if (Math.abs(x - sx) < 0.4) continue;
    lit.add(new THREE.ConeGeometry(0.03 + r() * 0.025, 0.09 + r() * 0.08, 3), mat(x, surfY + 0.05, 0.01 - r() * 0.12, r() * 3, 1, 1, 1, 0.25 + r() * 0.3, (r() - 0.5) * 0.5),
      r() < 0.5 ? '#5f8f3a' : '#4d7a2e', { rng: r, jitter: 0.08 });
  }
  // корінці, що звисають у верхній шар
  for (let i = 0; i < 16; i++) {
    const x = XL + 0.3 + r() * (XR - XL - 0.6);
    if (Math.abs(x - sx) < 0.5) continue;
    const len = 0.12 + r() * 0.22;
    lit.add(new THREE.CylinderGeometry(0.006, 0.012, len, 4), mat(x, surfY - 0.06 - len / 2, 0.035, 0, 1, 1, 1, 0, (r() - 0.5) * 0.6), '#6b4a30', { rng: r, jitter: 0.05 });
  }

  // ---- шахта над колодязем: зруб із колод, драбина, мотузка.
  const shaftTop = surfY, shaftBot = (grid.shaft?.length ? -Math.max(...grid.shaft.map((s) => s[1])) - 0.5 : surfY - 4);
  const logC = ['#7a5232', '#6b4528', '#84593a'];
  for (let y = shaftBot + 0.05; y < shaftTop; y += 0.1) {
    for (const s of [-1, 1]) log(lit, [sx + s * (L / 2 - 0.045), y, -0.58], [sx + s * (L / 2 - 0.045), y, 0.01], 0.048, logC[Math.floor(r() * 3)], r, 6);
    log(lit, [sx - L / 2, y + 0.05, -0.41], [sx + L / 2, y + 0.05, -0.41], 0.046, logC[Math.floor(r() * 3)], r, 6);
  }
  for (const s of [-1, 1]) lit.add(new THREE.BoxGeometry(0.022, shaftTop - shaftBot + 0.25, 0.022), mat(sx + s * 0.07, (shaftTop + shaftBot) / 2 - 0.1, -0.32), '#a57848', { rng: r, jitter: 0.05 });
  for (let y = shaftBot; y < shaftTop; y += 0.13) lit.add(new THREE.BoxGeometry(0.15, 0.016, 0.018), mat(sx, y, -0.32), '#9a6b3c', { rng: r, jitter: 0.08 });
  lit.add(new THREE.BoxGeometry(0.008, shaftTop - shaftBot + 0.3, 0.008), mat(sx + 0.035, (shaftTop + shaftBot) / 2 - 0.12, -0.12), '#c8b48a', { rng: r, jitter: 0 });
  // на дні колодязя — калюжа й відро
  const wb = (grid.start ? -grid.start[1] : 0) - L / 2;
  lit.add(new THREE.CylinderGeometry(0.15, 0.15, 0.006, 10), mat(sx, wb + 0.004, -0.2, 0, 1.2, 1, 1), '#1e3448', { rng: r, jitter: 0.05 });
  lit.add(new THREE.CylinderGeometry(0.05, 0.04, 0.08, 8), mat(sx + 0.035, wb + 0.04 + 0.12, -0.12), '#7a5232', { rng: r, jitter: 0.06 });
  lit.add(new THREE.TorusGeometry(0.05, 0.006, 3, 8), mat(sx + 0.035, wb + 0.13 + 0.12, -0.12, 0, 1, 1, 1, 0), '#3b3634', { rng: r, jitter: 0 });

  // ---- зруб колодязя на поверхні (розрізаний разом із землею: видно лише задню половину)
  for (let k = 0; k < 4; k++) {
    const y = surfY + 0.04 + k * 0.085;
    log(lit, [sx - 0.36, y, -0.62], [sx + 0.36, y, -0.62], 0.045, logC[k % 3], r, 6);
    for (const s of [-1, 1]) log(lit, [sx + s * 0.34, y + 0.04, -0.66], [sx + s * 0.34, y + 0.04, -0.02], 0.045, logC[(k + 1) % 3], r, 6);
  }
  lit.add(new THREE.PlaneGeometry(0.6, 0.55), mat(sx, surfY + 0.012, -0.33, 0, 1, 1, 1, -Math.PI / 2), '#0e0906', { rng: r, jitter: 0 });

  // ---- журавель: стовп-сошка (нерухомий); жердина з противагою та мотузка з відром — рухомі.
  const pivot = new V3(sx - 1.35, surfY + 1.5, -0.2);
  log(lit, [pivot.x, surfY - 0.05, pivot.z], [pivot.x, pivot.y - 0.02, pivot.z], 0.06, '#6b4a2b', r, 6);
  for (const s of [-1, 1]) log(lit, [pivot.x, pivot.y - 0.05, pivot.z], [pivot.x + s * 0.06, pivot.y + 0.14, pivot.z], 0.025, '#6b4a2b', r, 5);
  lit.add(new THREE.CylinderGeometry(0.012, 0.012, 0.16, 5), mat(pivot.x, pivot.y, pivot.z, 0, 1, 1, 1, Math.PI / 2), '#3b3634', { rng: r, jitter: 0 });
  const tipL = new V3(1.38, 0.45, 0), tailL = new V3(-0.95, -0.31, 0);
  const arm = new Builder();
  log(arm, [tailL.x, tailL.y, 0], [tipL.x, tipL.y, 0], 0.028, '#7a5532', r, 6);
  arm.add(new THREE.DodecahedronGeometry(0.11, 0), mat(tailL.x + 0.05, tailL.y - 0.06, 0), '#7c7e84', { rng: r, jitter: 0.1 });
  arm.add(new THREE.BoxGeometry(0.12, 0.08, 0.1), mat(tailL.x + 0.2, tailL.y + 0.02, 0, 0, 1, 1, 1, 0, 0.3), '#5e3f26', { rng: r, jitter: 0.1 });
  const ropeLen = (pivot.y + tipL.y) - (surfY + 0.62);
  const rope = new Builder();
  rope.add(new THREE.BoxGeometry(0.008, ropeLen, 0.008), mat(0, -ropeLen / 2, 0), '#c8b48a', { rng: r, jitter: 0 });
  rope.add(new THREE.CylinderGeometry(0.085, 0.07, 0.13, 8), mat(0, -ropeLen - 0.05, 0), '#7a5232', { rng: r, jitter: 0.06 });
  for (const y of [-ropeLen - 0.01, -ropeLen - 0.09]) rope.add(new THREE.CylinderGeometry(0.088, 0.088, 0.016, 8, 1, true), mat(0, y, 0), '#3b3634', { rng: r, jitter: 0 });
  rope.add(new THREE.TorusGeometry(0.075, 0.006, 3, 10, Math.PI), mat(0, -ropeLen + 0.02, 0), '#3b3634', { rng: r, jitter: 0 });

  // ---- хата: призьба, білені стіни, віконця з теплим світлом, двері, стріха.
  const hx = sx + 3.4, hz = -1.75, hw = 1.75, hd = 1.05, hh = 0.78;
  lit.add(new THREE.BoxGeometry(hw + 0.14, 0.13, hd + 0.14), mat(hx, surfY + 0.065, hz), '#8a4f33', { rng: r, jitter: 0.05 });
  lit.add(new THREE.BoxGeometry(hw, hh, hd), mat(hx, surfY + 0.13 + hh / 2, hz), PAL.wall, { rng: r, jitter: 0.03 });
  lit.add(new THREE.BoxGeometry(hw + 0.01, 0.06, hd + 0.01), mat(hx, surfY + 0.16, hz), '#c9b8a2', { rng: r, jitter: 0.03 });
  const fz = hz + hd / 2;
  lit.add(new THREE.BoxGeometry(0.3, 0.55, 0.03), mat(hx - 0.52, surfY + 0.13 + 0.275, fz + 0.01), '#6a4426', { rng: r, jitter: 0.06 });
  lit.add(new THREE.BoxGeometry(0.36, 0.05, 0.04), mat(hx - 0.52, surfY + 0.13 + 0.57, fz + 0.012), '#3f6fa6', { rng: r, jitter: 0 });
  for (const wx of [hx + 0.05, hx + 0.55]) {
    const wy = surfY + 0.13 + hh * 0.55;
    lit.add(new THREE.BoxGeometry(0.32, 0.32, 0.03), mat(wx, wy, fz + 0.008), '#3f6fa6', { rng: r, jitter: 0.04 });
    glow.add(new THREE.PlaneGeometry(0.11, 0.11), mat(wx - 0.062, wy + 0.062, fz + 0.026), PAL.lamp, { jitter: 0 });
    glow.add(new THREE.PlaneGeometry(0.11, 0.11), mat(wx + 0.062, wy + 0.062, fz + 0.026), PAL.lamp, { jitter: 0 });
    glow.add(new THREE.PlaneGeometry(0.11, 0.11), mat(wx - 0.062, wy - 0.062, fz + 0.026), '#f2b04e', { jitter: 0 });
    glow.add(new THREE.PlaneGeometry(0.11, 0.11), mat(wx + 0.062, wy - 0.062, fz + 0.026), '#f2b04e', { jitter: 0 });
    lit.add(new THREE.BoxGeometry(0.4, 0.035, 0.07), mat(wx, wy - 0.18, fz + 0.03), '#3f6fa6', { rng: r, jitter: 0 });
  }
  // бічне віконце (видно при повороті камери)
  glow.add(new THREE.PlaneGeometry(0.16, 0.16), mat(hx - hw / 2 - 0.004, surfY + 0.13 + hh * 0.55, hz, -Math.PI / 2), '#f2b04e', { jitter: 0 });
  // стріха — чотирисхила, з навісом
  {
    const ex = hw / 2 + 0.2, ez = hd / 2 + 0.2, ya = surfY + 0.13 + hh - 0.02, yb = ya + 0.78, rx = hw * 0.18;
    const arr = [];
    const A = [hx - ex, ya, hz + ez], B = [hx + ex, ya, hz + ez], C = [hx + ex, ya, hz - ez], D = [hx - ex, ya, hz - ez];
    const R1 = [hx - rx, yb, hz], R2 = [hx + rx, yb, hz];
    quad(arr, A, B, R2, R1, new V3(0, 1, 1)); quad(arr, C, D, R1, R2, new V3(0, 1, -1));
    tri(arr, B, C, R2, new V3(1, 1, 0)); tri(arr, D, A, R1, new V3(-1, 1, 0)); quad(arr, A, B, C, D, new V3(0, -1, 0));
    lit.add(rawGeo(arr), null, (c) => c.y < ya + 0.01 ? mix(PAL.thatch, '#5a4020', 0.5) : col(PAL.thatch), { rng: r, jitter: 0.06 });
    // пасма соломи — горизонтальні валики по схилу
    for (let k = 1; k <= 3; k++) {
      const t = k / 4, y = ya + (yb - ya) * t, zz = ez * (1 - t), xx = ex - (ex - rx) * t;
      lit.add(new THREE.BoxGeometry(2 * xx, 0.03, 0.04), mat(hx, y, hz + zz + 0.012, 0, 1, 1, 1, -0.75), mix(PAL.thatch, '#8a6a30', 0.35), { rng: r, jitter: 0.05 });
    }
    lit.add(new THREE.BoxGeometry(2 * rx + 0.1, 0.07, 0.12), mat(hx, yb, hz), '#9a7a3a', { rng: r, jitter: 0.05 });
    lit.add(new THREE.BoxGeometry(0.14, 0.34, 0.14), mat(hx + 0.35, yb - 0.08, hz - 0.15), PAL.wall, { rng: r, jitter: 0.03 });
  }

  // ---- тин позаду, з глечиками на кілках
  const fenceZ = -3.3;
  const posts = [];
  for (let x = XL - 2; x <= XR + 2; x += 0.55) posts.push(x + (r() - 0.5) * 0.08);
  for (const x of posts) {
    const out = Math.max(0, Math.max(XL - x, x - XR)) / 3;
    lit.add(new THREE.CylinderGeometry(0.025, 0.03, 0.62, 5), mat(x, surfY + 0.29, fenceZ, 0, 1, 1, 1, (r() - 0.5) * 0.08, (r() - 0.5) * 0.08), mix('#6e5032', bgHex, out * 0.7), { rng: r });
  }
  for (let i = 0; i + 1 < posts.length; i++)
    for (const y of [0.16, 0.3, 0.44]) beam(lit, [posts[i], surfY + y + (r() - 0.5) * 0.03, fenceZ], [posts[i + 1], surfY + y + (r() - 0.5) * 0.03, fenceZ], 0.04, 0.06, '#8a6a42', r, 0.14);
  for (const k of [Math.floor(posts.length * 0.35), Math.floor(posts.length * 0.62)]) {
    const x = posts[k];
    const pts = [[0, 0], [0.04, 0], [0.065, 0.04], [0.068, 0.08], [0.045, 0.12], [0.03, 0.135], [0.036, 0.15], [0, 0.15]].map(([a, b]) => new THREE.Vector2(a, b));
    lit.add(new THREE.LatheGeometry(pts, 8), mat(x, surfY + 0.8, fenceZ, 0, 1.2, 1.2, 1.2, Math.PI), '#b8643f', { rng: r, jitter: 0.06 });
  }

  // ---- вишні
  for (const [tx, tz, s] of [[sx + 6.2, -1.3, 1], [sx - 3.3, -2.2, 0.9]]) {
    log(lit, [tx, surfY, tz], [tx + 0.04, surfY + 0.75 * s, tz], 0.05 * s, '#4a3020', r, 6);
    log(lit, [tx + 0.02, surfY + 0.55 * s, tz], [tx + 0.3 * s, surfY + 0.85 * s, tz + 0.05], 0.03 * s, '#4a3020', r, 5);
    const blobs = [[0, 1.05, 0, 0.42], [0.3, 0.95, 0.08, 0.32], [-0.28, 0.92, -0.05, 0.33], [0.05, 1.3, -0.04, 0.3], [0.12, 0.85, 0.25, 0.26]];
    blobs.forEach(([x, y, z, rr], i) => lit.add(new THREE.IcosahedronGeometry(rr * s, 0), mat(tx + x * s, surfY + y * s, tz + z * s, i), ['#2f5a2a', '#3a6a2e', '#2a4f26'][i % 3], { rng: r, jitter: 0.1 }));
    for (let i = 0; i < 12; i++) {
      const a = r() * Math.PI * 2, e = r() * 0.9;
      lit.add(new THREE.IcosahedronGeometry(0.035 * s, 0), mat(tx + Math.cos(a) * 0.38 * s, surfY + (0.95 + Math.sin(e) * 0.3) * s, tz + Math.sin(a) * 0.3 * s + 0.12), '#b3202a', { rng: r, jitter: 0.05 });
    }
  }

  // ---- соняшники (лицем до камери)
  [[sx + 6.95, -0.45, 1.05], [sx + 7.35, -0.62, 1.2], [sx + 7.72, -0.42, 0.95]].forEach(([fx, fzz, h], i) => {
    log(lit, [fx, surfY, fzz], [fx + 0.02, surfY + h, fzz], 0.018, '#4d7a2e', r, 5);
    for (const s of [-1, 1]) lit.add(new THREE.ConeGeometry(0.06, 0.18, 3), mat(fx + s * 0.07, surfY + h * (0.45 + i * 0.05 + (s > 0 ? 0.1 : 0)), fzz, 0, 1, 1, 0.3, 0, -s * 1.2), '#4f8a30', { rng: r, jitter: 0.06 });
    const hy = surfY + h + 0.05;
    lit.add(new THREE.CylinderGeometry(0.16, 0.16, 0.025, 12), mat(fx + 0.02, hy, fzz + 0.03, 0, 1, 1, 1, Math.PI / 2 - 0.25), '#f2c230', { rng: r, jitter: 0.06 });
    for (let k = 0; k < 10; k++) {
      const a = k / 10 * Math.PI * 2;
      lit.add(new THREE.ConeGeometry(0.035, 0.09, 3), mat(fx + 0.02 + Math.cos(a) * 0.17, hy + Math.sin(a) * 0.17 * Math.cos(0.25), fzz + 0.03 + Math.sin(a) * 0.04, 0, 1, 1, 0.35, 0, a - Math.PI / 2), k % 2 ? '#f5c22e' : '#e8a823', { rng: r, jitter: 0.05 });
    }
    lit.add(new THREE.CylinderGeometry(0.09, 0.09, 0.035, 10), mat(fx + 0.02, hy, fzz + 0.045, 0, 1, 1, 1, Math.PI / 2 - 0.25), '#4a2e18', { rng: r, jitter: 0.06 });
  });

  // ---- копиця
  {
    const kx = sx + 9.7, kz = -1.4;
    lit.add(new THREE.SphereGeometry(0.55, 9, 5, 0, Math.PI * 2, 0, Math.PI / 2), mat(kx, surfY - 0.02, kz, 0.3, 1, 1.35, 1), '#cfa84a', { rng: r, jitter: 0.1 });
    lit.add(new THREE.ConeGeometry(0.22, 0.3, 8), mat(kx, surfY + 0.8, kz), '#b89238', { rng: r, jitter: 0.1 });
    log(lit, [kx, surfY + 0.85, kz], [kx + 0.03, surfY + 1.15, kz], 0.015, '#5e3f26', r, 4);
  }

  // ---- небо: градієнт skyTop → skyLow, пагорби, вогники хуторів, місяць, зорі.
  const SZ = -18, SX0 = -70, SX1 = 80, SY0 = surfY - 4, SY1 = surfY + 60;
  const skyAt = (y) => {
    const k = Math.max(0, Math.min(1, (y - (surfY - 0.3)) / 3.2));
    return mix(PAL.skyLow, PAL.skyTop, Math.pow(k, 0.45));
  };
  sky.add(new THREE.PlaneGeometry(SX1 - SX0, SY1 - SY0, 1, 24), mat((SX0 + SX1) / 2, (SY0 + SY1) / 2, SZ), null, { jitter: 0, vertexColor: (v) => skyAt(v.y + (SY0 + SY1) / 2) });
  // Місяць з ореолом.
  const mx = sx + 16.5, my = surfY + 2.6;
  for (const [rr, kk] of [[1.5, 0.18], [1.0, 0.3]]) sky.add(new THREE.CircleGeometry(rr, 28), mat(mx, my, SZ + 0.03 + kk * 0.1), null, { jitter: 0, vertexColor: (v) => (Math.hypot(v.x, v.y) < 0.1 ? skyAt(my).clone().lerp(col('#c8d0ee'), kk) : skyAt(my + v.y)) });
  sky.add(new THREE.CircleGeometry(0.62, 20), mat(mx, my, SZ + 0.1), '#f4f1dc', { jitter: 0 });
  sky.add(new THREE.CircleGeometry(0.15, 10), mat(mx + 0.2, my + 0.14, SZ + 0.12), '#dcd8c0', { jitter: 0 });
  sky.add(new THREE.CircleGeometry(0.1, 8), mat(mx - 0.18, my - 0.2, SZ + 0.12), '#d6d2ba', { jitter: 0 });
  for (let i = 0; i < 70; i++) {
    const x = -16 + r() * 40, y = surfY + 1.2 + r() * (i < 45 ? 3 : 14), s = 0.025 + r() * 0.05;
    if (Math.hypot(x - mx, y - my) < 1.6) continue;
    const c = mix('#fff6dc', '#cfe0ff', r());
    sky.add(new THREE.CircleGeometry(s, 4), mat(x, y, SZ + 0.15, 0, 1, 1, 1, 0, r()), c, { jitter: 0 });
  }
  // Пагорби: дальні (синюваті) і ближчі (темні), вогники хуторів.
  for (const [z, base, amp, c, seed] of [[SZ + 1, surfY - 0.1, 1.1, '#2b3552', 1.3], [SZ + 4, surfY - 0.5, 0.8, '#1d2a2a', 2.1]]) {
    const arr = [];
    for (let x = SX0; x < SX1; x += 1.5) {
      const hgt = (xx) => base + amp * (0.5 + 0.35 * Math.sin(xx * 0.13 + seed) + 0.25 * Math.sin(xx * 0.31 + seed * 3));
      quad(arr, [x, SY0, z], [x + 1.5, SY0, z], [x + 1.5, hgt(x + 1.5), z], [x, hgt(x), z], ZP);
    }
    sky.add(rawGeo(arr), null, c, { rng: r, jitter: 0.03 });
  }
  for (let i = 0; i < 14; i++) {
    const x = -20 + r() * 50, y = surfY + 0.5 + r() * 0.8;
    sky.add(new THREE.PlaneGeometry(0.07, 0.06), mat(x, y, SZ + 1.2), r() < 0.5 ? '#ffcf6b' : '#f2a04e', { jitter: 0 });
  }

  return { lit: lit.geometry(), glow: glow.geometry(), sky: sky.geometry(), craneArm: arm.geometry(), craneRope: rope.geometry(),
    pivot, tip: tipL, surfY, botY };
}
