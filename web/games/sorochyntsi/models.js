// «Сорочинський ярмарок» — процедурні low-poly моделі для 3D-сцени (scene.js). Лише геометрія three.js і текстури,
// намальовані в canvas під час роботи, жодних файлів: стільниця й скатертина, плашки крамниць і атлас їхніх лиць,
// 18 емблем (6 товарів × 3 рівні), жетони-кружальця, плитки гостей, гості, козак старшини, булава з подушкою, прилавки,
// підсвітка, тло (ятки, вози, мішки, бочки, тополі, баня церкви, гірлянда ліхтарів), стрічки фіналу.
//
// Стиль — як у «Хуторах»: пласке затінення, кольори вершин із легким розкидом яскравості по гранях, тепла палітра.
// Нерухоме зливається в ОДИН меш (Builder), однакові рухомі речі — InstancedMesh у scene.js. Тут лише «з чого зліплено».
//
// Система координат: стільниця — площина XZ (Y = 0 — її верх), X праворуч, Z до камери (мій край). Контракт §11.

import * as THREE from '/static/vendor/three-0.186.1/three.module.js';
import { GOODS, BONUSES, TOKENS, GOOD_COLOR, GOOD_INK, GOOD_ICON, DECK, CARD_NAME, GUESTS, TIER_ROMAN, TIER_NAME, PAL,
  STARSHYNA, GUEST_POINTS } from './consts.js';

const V3 = THREE.Vector3;
const UP = new V3(0, 1, 0);

// ---------------------------------------------------------------------------------------------------------- розмітка
/** Плашка крамниці (контракт §11): 1,0 × 1,4, товщина 0,08. */
export const PLAQUE = { w: 1.0, d: 1.4, h: 0.08 };
/** Жетон: r = 0,28, h = 0,08. */
export const TOKEN = { r: 0.28, h: 0.08 };
export const ROW_Z = { 1: 1.0, 2: -0.6, 3: -2.2 };
export const SLOT_X = [-1.9, -0.65, 0.6, 1.85];
export const DECK_X = -3.3;
export const BANK_X = 3.4;
export const bankZ = (i) => -2.4 + i * 0.8;          // TOKENS[i]: −2,4 … 1,6
export const GUEST_Z = -3.8;
export const GUEST_STEP = 1.05;
export const TILE = 0.9;
export const AWARD = { x: 3.4, z: -3.6 };            // підставка з подушкою: булава й старшина
export const PED_H = 0.3;                            // висота підставки
export const TABLE = { x0: -7.5, x1: 7.5, z0: -6.5, z1: 4.9 };
/** Прилавки: центр смуги й поворот (local x — праворуч від власника, local z — до власника). */
export const COUNTER = {
  bottom: { x: 0, z: 4.0, ry: 0 },
  top: { x: 0, z: -5.6, ry: Math.PI },
  left: { x: -6.4, z: -0.8, ry: -Math.PI / 2 },
  right: { x: 6.4, z: -0.8, ry: Math.PI / 2 },
};
export const COUNTER_W = 7, COUNTER_D = 1.6, BOARD_H = 0.06;
/** Масштаб речей на прилавку. */
export const CS = 0.5, TS = 0.7, GS = 0.5;

// ---------------------------------------------------------------------------------------------------------- кольори
const colCache = new Map();
/** Колір із hex-рядка (sRGB → лінійний робочий простір three.js). Кешується — не мутувати! */
export function col(hex) {
  let c = colCache.get(hex);
  if (!c) { c = new THREE.Color(hex); colCache.set(hex, c); }
  return c;
}
export function mix(a, b, k) { return col(a).clone().lerp(col(b), k); }
function dark(hex, k) { return mix(hex, '#000000', k); }
function light(hex, k) { return mix(hex, '#ffffff', k); }

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
const _q = new THREE.Quaternion(), _e = new THREE.Euler(), _s = new V3(), _p = new V3();
/** Матриця: зсув, поворот (Y, потім X/Z), масштаб. */
export function mat(x = 0, y = 0, z = 0, ry = 0, sx = 1, sy = sx, sz = sx, rx = 0, rz = 0) {
  _e.set(rx, ry, rz, 'YXZ');
  return new THREE.Matrix4().compose(_p.set(x, y, z), _q.setFromEuler(_e), _s.set(sx, sy, sz));
}

/**
 * Зливає купу геометрій в одну неіндексовану з кольорами вершин.
 * add(geo, matrix, color | null (узяти кольори вершин geo) | (centroid, rng) => Color, { jitter, rng, keep })
 */
export class Builder {
  constructor() { this.p = []; this.c = []; }
  add(g, m = null, color = '#ffffff', opt = {}) {
    const src = g.index ? g.toNonIndexed() : g;
    const pos = src.attributes.position, vc = src.attributes.color;
    const jit = opt.jitter ?? 0.06, rng = opt.rng ?? Math.random;
    const a = new V3(), b = new V3(), c = new V3(), cen = new V3();
    const fixed = color == null ? null : typeof color === 'function' ? null : (color instanceof THREE.Color ? color : col(color));
    for (let i = 0; i < pos.count; i += 3) {
      a.fromBufferAttribute(pos, i); b.fromBufferAttribute(pos, i + 1); c.fromBufferAttribute(pos, i + 2);
      const fn = typeof color === 'function' ? color(cen.copy(a).add(b).add(c).divideScalar(3), rng) : null;
      if (m) { a.applyMatrix4(m); b.applyMatrix4(m); c.applyMatrix4(m); }
      const j = 1 + (rng() - 0.5) * 2 * jit;
      this.p.push(a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z);
      for (let k = 0; k < 3; k++) {
        if (color == null && vc) this.c.push(vc.getX(i + k) * j, vc.getY(i + k) * j, vc.getZ(i + k) * j);
        else { const cc = fixed ?? fn ?? col('#ffffff'); this.c.push(cc.r * j, cc.g * j, cc.b * j); }
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
    g.computeVertexNormals();
    g.computeBoundingSphere();
    return g;
  }
}

/** Брусок між двома точками. */
function beam(b, p1, p2, w, h, color, rng, jitter = 0.06) {
  const dx = p2[0] - p1[0], dy = p2[1] - p1[1], dz = p2[2] - p1[2];
  const len = Math.hypot(dx, dy, dz) || 1e-4;
  const m = new THREE.Matrix4().compose(
    new V3((p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2, (p1[2] + p2[2]) / 2),
    new THREE.Quaternion().setFromUnitVectors(new V3(1, 0, 0), new V3(dx, dy, dz).normalize()),
    new V3(1, 1, 1));
  b.add(new THREE.BoxGeometry(len, h, w), m, color, { rng, jitter });
}
/** Циліндр між двома точками. */
function rod(b, p1, p2, r, color, rng, seg = 6, jitter = 0.05, r2 = r) {
  const d = new V3(p2[0] - p1[0], p2[1] - p1[1], p2[2] - p1[2]);
  const len = d.length() || 1e-4;
  const m = new THREE.Matrix4().compose(new V3((p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2, (p1[2] + p2[2]) / 2),
    new THREE.Quaternion().setFromUnitVectors(UP, d.normalize()), new V3(1, 1, 1));
  b.add(new THREE.CylinderGeometry(r2, r, len, seg), m, color, { rng, jitter });
}

// ---------------------------------------------------------------------------------------------------------- плашка
/** Дерев'яна плашка з фаскою: низ — Y = 0, верх — Y = PLAQUE.h. Верх світлий (PAL.plaque), боки — кант (PAL.plaqueEdge). */
export function plaqueGeo() {
  const w = PLAQUE.w / 2 - 0.015, d = PLAQUE.d / 2 - 0.015, c = 0.05;
  const sh = new THREE.Shape();
  sh.moveTo(-w + c, -d); sh.lineTo(w - c, -d); sh.lineTo(w, -d + c); sh.lineTo(w, d - c); sh.lineTo(w - c, d);
  sh.lineTo(-w + c, d); sh.lineTo(-w, d - c); sh.lineTo(-w, -d + c); sh.closePath();
  const g = new THREE.ExtrudeGeometry(sh, { depth: PLAQUE.h - 0.03, bevelEnabled: true, bevelThickness: 0.015, bevelSize: 0.015, bevelSegments: 1, curveSegments: 1 });
  g.applyMatrix4(new THREE.Matrix4().makeRotationX(-Math.PI / 2));
  g.applyMatrix4(new THREE.Matrix4().makeTranslation(0, 0.015, 0));
  const b = new Builder(), r = rngOf(7);
  b.add(g, null, (cen) => cen.y > PLAQUE.h - 0.004 ? col(PAL.plaque) : cen.y > PLAQUE.h - 0.02 ? mix(PAL.plaque, PAL.plaqueEdge, 0.45) : col(PAL.plaqueEdge), { rng: r, jitter: 0.04 });
  return b.geometry();
}
/** Розмір лиця на плашці (трохи менший за верх). */
export const FACE = { w: 0.9, d: 1.3 };

// ---------------------------------------------------------------------------------------------------------- атлас лиць
// 90 лиць + 3 сорочки, клітинка 170 × 238 (high, 2048²) або 85 × 119 (low, 1024²): 12 стовпців × 8 рядків.
export const ATLAS = { cols: 12, rows: 8, cw: 170, ch: 238 };
export const BACK_CELL = (tier) => 89 + tier;   // 90, 91, 92
export function atlasUv(cell, size) {
  const k = size / 2048;
  const cw = ATLAS.cw * k, ch = ATLAS.ch * k;
  const cx = cell % ATLAS.cols, cy = Math.floor(cell / ATLAS.cols);
  const pad = 1.5 * k;
  return [(cx * cw + pad) / size, 1 - ((cy + 1) * ch - pad) / size, (cw - 2 * pad) / size, (ch - 2 * pad) / size];
}
const SERIF = 'Georgia, "Times New Roman", serif';
const SANS = '"Helvetica Neue", Arial, sans-serif';
const EMOJI = '"Apple Color Emoji", "Segoe UI Emoji", "Noto Color Emoji", sans-serif';
function rr(g, x, y, w, h, r) {
  g.beginPath(); g.moveTo(x + r, y); g.arcTo(x + w, y, x + w, y + h, r); g.arcTo(x + w, y + h, x, y + h, r);
  g.arcTo(x, y + h, x, y, r); g.arcTo(x, y, x + w, y, r); g.closePath();
}
function wrapText(g, text, maxW) {
  const words = text.split(' '), lines = [];
  let cur = '';
  for (const w of words) {
    const t = cur ? cur + ' ' + w : w;
    if (g.measureText(t).width <= maxW || !cur) cur = t; else { lines.push(cur); cur = w; }
  }
  if (cur) lines.push(cur);
  return lines;
}
function coin(g, x, y, r, good, n) {
  g.fillStyle = 'rgba(0,0,0,.28)'; g.beginPath(); g.arc(x + 1.5, y + 2, r, 0, Math.PI * 2); g.fill();
  g.fillStyle = GOOD_COLOR[good]; g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill();
  g.lineWidth = Math.max(2, r * 0.14); g.strokeStyle = good === 'linen' ? '#8a6a3a' : 'rgba(20,10,0,.55)'; g.stroke();
  g.fillStyle = GOOD_INK[good];
  g.font = `bold ${Math.round(r * 1.35)}px ${SANS}`; g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(String(n), x, y + r * 0.07);
}
function drawFace(g, id) {
  const [, tier, bonus, pts, k, cost] = DECK[id];
  const W = ATLAS.cw, H = ATLAS.ch;
  // тло — світле дерево з канвою
  g.fillStyle = '#f4e6c6'; g.fillRect(0, 0, W, H);
  g.fillStyle = 'rgba(160,110,60,.07)';
  for (let y = 4; y < H; y += 7) g.fillRect(0, y, W, 2);
  // смуга бонуса
  const band = 58;
  g.fillStyle = GOOD_COLOR[bonus]; g.fillRect(0, 0, W, band);
  g.fillStyle = 'rgba(255,255,255,.14)'; g.fillRect(0, 0, W, 8);
  g.fillStyle = 'rgba(0,0,0,.18)'; g.fillRect(0, band - 4, W, 4);
  // вишитий «рушниковий» поясок під смугою
  for (let x = 4; x < W; x += 12) {
    g.fillStyle = (x / 12) % 2 < 1 ? PAL.embroideryRed : PAL.embroideryBlack;
    g.beginPath(); g.moveTo(x, band + 5); g.lineTo(x + 5, band + 1); g.lineTo(x + 10, band + 5); g.lineTo(x + 5, band + 9); g.closePath(); g.fill();
  }
  // значок товару в смузі
  g.font = `38px ${EMOJI}`; g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(GOOD_ICON[bonus], W / 2 + 12, band / 2 + 2);
  // ⭐ — велике число ліворуч угорі
  if (pts > 0) {
    g.font = `bold 50px ${SERIF}`; g.textAlign = 'left'; g.textBaseline = 'middle';
    g.lineWidth = 6; g.strokeStyle = bonus === 'linen' ? '#fff6e0' : 'rgba(0,0,0,.55)'; g.strokeText(String(pts), 9, band / 2 + 3);
    g.fillStyle = bonus === 'linen' ? '#3a2a1a' : '#ffffff'; g.fillText(String(pts), 9, band / 2 + 3);
    g.font = `17px ${EMOJI}`; g.fillText('⭐', 38, band / 2 + 15);
  }
  // ⚔ праворуч угорі
  if (k) {
    g.fillStyle = '#2a1d17'; g.beginPath(); g.arc(W - 22, 22, 17, 0, Math.PI * 2); g.fill();
    g.strokeStyle = PAL.gild; g.lineWidth = 2.5; g.stroke();
    g.fillStyle = '#ffe7a8'; g.font = `bold 22px ${SANS}`; g.textAlign = 'center'; g.textBaseline = 'middle';
    g.fillText('⚔', W - 22, 23);
  }
  // назва
  g.fillStyle = '#3a2414'; g.font = `bold 15px ${SANS}`; g.textAlign = 'center'; g.textBaseline = 'top';
  const lines = wrapText(g, CARD_NAME[id], W - 16).slice(0, 2);
  lines.forEach((l, i) => g.fillText(l, W / 2, band + 14 + i * 17));
  // ціна — стовпчик кружечків унизу ліворуч (лише ненульові)
  const nz = GOODS.map((gd, i) => [gd, cost[i]]).filter(([, n]) => n > 0);
  nz.forEach(([gd, n], i) => coin(g, 25, H - 24 - (nz.length - 1 - i) * 37, 16.5, gd, n));
  // рівень — римська цифра внизу праворуч
  g.fillStyle = 'rgba(110,69,35,.75)'; g.font = `bold 15px ${SERIF}`; g.textAlign = 'right'; g.textBaseline = 'bottom';
  g.fillText(TIER_ROMAN[tier], W - 8, H - 6);
  // рамка
  g.lineWidth = 6; g.strokeStyle = PAL.plaqueEdge; rr(g, 3, 3, W - 6, H - 6, 12); g.stroke();
}
function drawBack(g, tier) {
  const W = ATLAS.cw, H = ATLAS.ch;
  const base = ['#5e3b21', '#4b2f1a', '#3a2414'][tier - 1];
  g.fillStyle = base; g.fillRect(0, 0, W, H);
  g.fillStyle = 'rgba(255,255,255,.04)';
  for (let y = 0; y < H; y += 9) g.fillRect(0, y, W, 3);
  g.strokeStyle = PAL.gild; g.lineWidth = 3; rr(g, 12, 12, W - 24, H - 24, 10); g.stroke();
  // вишиті ромби по колу
  for (let i = 0; i < 9; i++) {
    for (const [x, y] of [[20 + i * 16.2, 22], [20 + i * 16.2, H - 22]]) {
      g.fillStyle = i % 2 ? PAL.embroideryRed : PAL.gild;
      g.beginPath(); g.moveTo(x, y - 5); g.lineTo(x + 5, y); g.lineTo(x, y + 5); g.lineTo(x - 5, y); g.closePath(); g.fill();
    }
  }
  // «баранячі ріжки» над і під числом
  g.strokeStyle = PAL.embroideryRed; g.lineWidth = 4;
  for (const y of [72, H - 72]) {
    for (const s of [-1, 1]) {
      g.beginPath(); g.arc(W / 2 + s * 18, y, 12, s > 0 ? Math.PI : 0, s > 0 ? Math.PI * 2.6 : Math.PI * 1.6, s < 0); g.stroke();
    }
  }
  g.fillStyle = PAL.gild; g.font = `bold ${tier === 3 ? 58 : 66}px ${SERIF}`; g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(TIER_ROMAN[tier], W / 2, H / 2 - 4);
  g.font = `bold 15px ${SANS}`; g.fillStyle = '#f3dfb0';
  g.fillText(TIER_NAME[tier], W / 2, H / 2 + 40);
  g.lineWidth = 6; g.strokeStyle = '#2a1a0e'; rr(g, 3, 3, W - 6, H - 6, 12); g.stroke();
}
/** Атлас: одна текстура на всі 90 лиць + 3 сорочки. */
export function makeCardAtlas(hi) {
  const size = hi ? 2048 : 1024, k = size / 2048;
  const cv = document.createElement('canvas'); cv.width = cv.height = size;
  const g = cv.getContext('2d');
  g.fillStyle = PAL.plaque; g.fillRect(0, 0, size, size);
  for (let cell = 0; cell < 93; cell++) {
    const cx = cell % ATLAS.cols, cy = Math.floor(cell / ATLAS.cols);
    g.save();
    g.translate(cx * ATLAS.cw * k, cy * ATLAS.ch * k); g.scale(k, k);
    g.beginPath(); g.rect(0, 0, ATLAS.cw, ATLAS.ch); g.clip();
    if (cell < 90) drawFace(g, cell); else drawBack(g, cell - 89);
    g.restore();
  }
  const tex = new THREE.CanvasTexture(cv);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.anisotropy = hi ? 8 : 2;
  tex.generateMipmaps = true;
  tex.minFilter = THREE.LinearMipmapLinearFilter;
  return { tex, size };
}

// ---------------------------------------------------------------------------------------------------------- атлас плиток
// Плитки гостей (0–9) і Січової старшини (10): 4 × 4 клітинки по 128 (512²). Вимога — кружечки вздовж ближнього краю.
export const TILE_ATLAS = { cols: 4, rows: 4 };
export const STARSHYNA_CELL = 10;
export function makeTileAtlas() {
  const S = 128, cv = document.createElement('canvas'); cv.width = cv.height = S * 4;
  const g = cv.getContext('2d');
  for (let i = 0; i < 11; i++) {
    g.save(); g.translate((i % 4) * S, Math.floor(i / 4) * S);
    if (i < 10) {
      g.fillStyle = '#ead7ad'; g.fillRect(0, 0, S, S);
      g.fillStyle = 'rgba(120,80,40,.08)'; for (let y = 3; y < S; y += 6) g.fillRect(0, y, S, 2);
      // вишитий поясок угорі
      for (let x = 2; x < S; x += 10) {
        g.fillStyle = (x / 10) % 2 < 1 ? PAL.embroideryRed : PAL.embroideryBlack;
        g.beginPath(); g.moveTo(x, 10); g.lineTo(x + 4, 6); g.lineTo(x + 8, 10); g.lineTo(x + 4, 14); g.closePath(); g.fill();
      }
      const req = GUESTS[i][2].map((n, j) => [GOODS[j], n]).filter(([, n]) => n > 0);
      const r = req.length === 2 ? 22 : 18, step = req.length === 2 ? 50 : 40;
      req.forEach(([gd, n], j) => coin(g, S / 2 + (j - (req.length - 1) / 2) * step, S - 27, r, gd, n));
      g.fillStyle = '#6e4523'; g.font = `bold 17px ${SANS}`; g.textAlign = 'left'; g.textBaseline = 'middle';
      g.fillText('+' + GUEST_POINTS, 8, 30); g.font = `13px ${EMOJI}`; g.fillText('⭐', 30, 31);
      g.lineWidth = 5; g.strokeStyle = PAL.plaqueEdge; rr(g, 2.5, 2.5, S - 5, S - 5, 10); g.stroke();
    } else {
      g.fillStyle = '#9e1f28'; g.fillRect(0, 0, S, S);
      g.strokeStyle = PAL.gild; g.lineWidth = 4; rr(g, 8, 8, S - 16, S - 16, 8); g.stroke();
      for (let x = 14; x < S - 10; x += 12) {
        g.fillStyle = PAL.gild;
        g.beginPath(); g.moveTo(x, S - 16); g.lineTo(x + 4, S - 20); g.lineTo(x + 8, S - 16); g.lineTo(x + 4, S - 12); g.closePath(); g.fill();
      }
      g.fillStyle = '#ffe7a8'; g.font = `bold 30px ${SERIF}`; g.textAlign = 'left'; g.textBaseline = 'middle';
      g.fillText(String(STARSHYNA.points), 16, 34); g.font = `22px ${EMOJI}`; g.fillText('⭐', 36, 36);
      g.fillStyle = '#ffe7a8'; g.font = `bold 13px ${SANS}`; g.textAlign = 'center';
      g.fillText('СТАРШИНА', S / 2, S - 32);
    }
    g.restore();
  }
  const tex = new THREE.CanvasTexture(cv);
  tex.colorSpace = THREE.SRGBColorSpace; tex.anisotropy = 4;
  return tex;
}

// ---------------------------------------------------------------------------------------------------------- значки
// Позначки на плашках (setMarks) і бронь наосліп: 0 — ✓ «можу купити», 1 — 🪙 «лише з дукатами», 2 — 🂠 «бачиш лише ти».
export const ICON_CELLS = 4;
export function makeIconAtlas() {
  const S = 128, cv = document.createElement('canvas'); cv.width = S * 4; cv.height = S;
  const g = cv.getContext('2d');
  const disc = (i, fill, ring) => {
    const x = i * S + S / 2, y = S / 2;
    g.fillStyle = 'rgba(0,0,0,.35)'; g.beginPath(); g.arc(x + 3, y + 4, 54, 0, Math.PI * 2); g.fill();
    g.fillStyle = ring; g.beginPath(); g.arc(x, y, 56, 0, Math.PI * 2); g.fill();
    g.fillStyle = fill; g.beginPath(); g.arc(x, y, 47, 0, Math.PI * 2); g.fill();
    return [x, y];
  };
  let [x, y] = disc(0, PAL.buy, '#0f3a1a');
  g.strokeStyle = '#ffffff'; g.lineWidth = 15; g.lineCap = 'round'; g.lineJoin = 'round';
  g.beginPath(); g.moveTo(x - 24, y + 2); g.lineTo(x - 6, y + 20); g.lineTo(x + 26, y - 18); g.stroke();
  [x, y] = disc(1, GOOD_COLOR.gold, PAL.legalEdge);
  g.fillStyle = '#7a5200'; g.beginPath(); g.arc(x, y, 34, 0, Math.PI * 2); g.fill();
  g.fillStyle = '#ffe58a'; g.beginPath(); g.arc(x, y, 28, 0, Math.PI * 2); g.fill();
  g.fillStyle = '#7a5200'; g.font = `bold 40px ${SERIF}`; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText('⚜', x, y + 2);
  [x, y] = disc(2, '#3a2414', PAL.gild);
  g.fillStyle = '#5e3b21'; rr(g, x - 20, y - 28, 40, 56, 6); g.fill();
  g.strokeStyle = PAL.gild; g.lineWidth = 4; rr(g, x - 20, y - 28, 40, 56, 6); g.stroke();
  g.strokeStyle = '#f3dfb0'; g.lineWidth = 3; g.beginPath(); g.moveTo(x - 10, y - 14); g.lineTo(x + 10, y + 14); g.moveTo(x + 10, y - 14); g.lineTo(x - 10, y + 14); g.stroke();
  [x, y] = disc(3, PAL.gild, PAL.legalEdge);
  g.fillStyle = '#fff'; g.font = `bold 56px ${SERIF}`; g.fillText('★', x, y + 3);
  const tex = new THREE.CanvasTexture(cv);
  tex.colorSpace = THREE.SRGBColorSpace;
  return tex;
}

// ---------------------------------------------------------------------------------------------------------- скатертина
/** Полотняна скатертина з вишитою червоно-чорною каймою (ромби й «баранячі ріжки»). */
export const CLOTH = { x0: -4.25, x1: 4.25, z0: -4.6, z1: 2.25 };
export function clothTexture(hi) {
  const W = hi ? 1024 : 512, H = Math.round(W * (CLOTH.z1 - CLOTH.z0) / (CLOTH.x1 - CLOTH.x0));
  const cv = document.createElement('canvas'); cv.width = W; cv.height = H;
  const g = cv.getContext('2d'), k = W / 1024;
  g.fillStyle = PAL.cloth; g.fillRect(0, 0, W, H);
  const r = rngOf(5);
  // полотно: м'які плями й рідкі нитки (без дрібної сітки — на відстані вона мерехтить муаром)
  for (let i = 0; i < 900 * k; i++) { g.fillStyle = `rgba(${r() > 0.5 ? '120,90,50' : '255,250,235'},${0.025 + r() * 0.03})`; g.fillRect(r() * W, r() * H, (8 + r() * 30) * k, (2 + r() * 4) * k); }
  for (let y = 0; y < H; y += (14 + r() * 20) * k) { g.fillStyle = `rgba(120,90,50,${0.03 + r() * 0.03})`; g.fillRect(0, y, W, 2 * k); }
  const B = 64 * k;   // ширина кайми
  // дві тонкі смуги вздовж кайми
  g.strokeStyle = PAL.embroideryRed; g.lineWidth = 4 * k;
  for (const d of [12 * k, B - 8 * k]) { g.strokeRect(d, d, W - 2 * d, H - 2 * d); }
  g.strokeStyle = PAL.embroideryBlack; g.lineWidth = 2 * k;
  for (const d of [18 * k, B - 14 * k]) { g.strokeRect(d, d, W - 2 * d, H - 2 * d); }
  // ромби з «ріжками» по колу
  const motif = (x, y, s, rot) => {
    g.save(); g.translate(x, y); g.rotate(rot);
    g.fillStyle = PAL.embroideryRed;
    g.beginPath(); g.moveTo(0, -s); g.lineTo(s * 0.8, 0); g.lineTo(0, s); g.lineTo(-s * 0.8, 0); g.closePath(); g.fill();
    g.fillStyle = PAL.embroideryBlack;
    g.beginPath(); g.moveTo(0, -s * 0.45); g.lineTo(s * 0.36, 0); g.lineTo(0, s * 0.45); g.lineTo(-s * 0.36, 0); g.closePath(); g.fill();
    g.strokeStyle = PAL.embroideryBlack; g.lineWidth = 2.6 * k;
    for (const sx of [-1, 1]) {   // баранячі ріжки — завитки обабіч
      g.beginPath(); g.arc(sx * s * 1.35, -s * 0.15, s * 0.42, sx > 0 ? Math.PI * 0.9 : Math.PI * 0.1, sx > 0 ? Math.PI * 2.4 : -Math.PI * 1.4, sx < 0); g.stroke();
    }
    g.restore();
  };
  const step = 46 * k, s = 11 * k, c = B / 2 + 2 * k;
  for (let x = c + step / 2; x < W - c; x += step) { motif(x, c, s, 0); motif(x, H - c, s, 0); }
  for (let y = c + step; y < H - c - step / 2; y += step) { motif(c, y, s, Math.PI / 2); motif(W - c, y, s, Math.PI / 2); }
  // кутові «зірки»
  for (const [x, y] of [[c, c], [W - c, c], [c, H - c], [W - c, H - c]]) {
    for (let i = 0; i < 8; i++) {
      g.save(); g.translate(x, y); g.rotate(i * Math.PI / 4);
      g.fillStyle = i % 2 ? PAL.embroideryBlack : PAL.embroideryRed;
      g.beginPath(); g.moveTo(0, 0); g.lineTo(5 * k, -16 * k); g.lineTo(0, -24 * k); g.lineTo(-5 * k, -16 * k); g.closePath(); g.fill();
      g.restore();
    }
  }
  const tex = new THREE.CanvasTexture(cv);
  tex.colorSpace = THREE.SRGBColorSpace; tex.anisotropy = hi ? 8 : 2;
  return tex;
}
export function clothGeo() {
  const w = CLOTH.x1 - CLOTH.x0, d = CLOTH.z1 - CLOTH.z0;
  const g = new THREE.PlaneGeometry(w, d, 1, 1);
  g.applyMatrix4(mat((CLOTH.x0 + CLOTH.x1) / 2, 0.004, (CLOTH.z0 + CLOTH.z1) / 2, 0, 1, 1, 1, -Math.PI / 2));
  return g;
}

// ---------------------------------------------------------------------------------------------------------- емблеми
// Маленька фігурка бонус-товару на плашці. Початок — точка верху плашки, де стоїть емблема; масштаб — для плашки 1 × 1,4.
/** Сама емблема товару (вигляд I). */
function addBare(b, good, r, relief = false) {
  const C = GOOD_COLOR;
  if (good === 'linen') {
    // сувій полотна з червоною смугою і звислим краєм
    const roll = new THREE.CylinderGeometry(0.085, 0.085, 0.36, 10);
    b.add(roll, mat(0, 0.2, 0, 0, 1, 1, 1, 0, Math.PI / 2), C.linen, { rng: r, jitter: 0.03 });
    b.add(new THREE.CylinderGeometry(0.089, 0.089, 0.05, 10), mat(0.07, 0.2, 0, 0, 1, 1, 1, 0, Math.PI / 2), PAL.embroideryRed, { rng: r, jitter: 0 });
    b.add(new THREE.CylinderGeometry(0.089, 0.089, 0.025, 10), mat(-0.1, 0.2, 0, 0, 1, 1, 1, 0, Math.PI / 2), PAL.embroideryBlack, { rng: r, jitter: 0 });
    b.add(new THREE.BoxGeometry(0.3, 0.2, 0.014), mat(0, 0.1, 0.082, 0, 1, 1, 1, -0.15), light(C.linen, 0.2), { rng: r, jitter: 0.02 });
    b.add(new THREE.BoxGeometry(0.3, 0.03, 0.016), mat(0, 0.045, 0.098, 0, 1, 1, 1, -0.15), PAL.embroideryRed, { rng: r, jitter: 0 });
    if (!relief) b.add(new THREE.BoxGeometry(0.24, 0.05, 0.16), mat(0, 0.025, -0.01), '#8a5a35', { rng: r, jitter: 0.04 });
  } else if (good === 'fish') {
    // рибина: витягнутий октаедр і хвіст, на кілочку
    const body = new THREE.OctahedronGeometry(0.1, 0);
    b.add(body, mat(0, 0.2, 0, 0, 2.0, 0.85, 0.5), C.fish, { rng: r, jitter: 0.08 });
    b.add(new THREE.OctahedronGeometry(0.1, 0), mat(0.02, 0.17, 0, 0, 1.4, 0.4, 0.42), light(C.fish, 0.55), { rng: r, jitter: 0.04 });
    const tail = new THREE.ConeGeometry(0.075, 0.1, 4);
    b.add(tail, mat(-0.235, 0.2, 0, 0, 1, 1, 0.35, 0, Math.PI / 2), dark(C.fish, 0.2), { rng: r, jitter: 0.05 });
    b.add(new THREE.SphereGeometry(0.016, 5, 4), mat(0.13, 0.225, 0.04), '#101010', { rng: r, jitter: 0 });
    b.add(new THREE.SphereGeometry(0.016, 5, 4), mat(0.13, 0.225, -0.04), '#101010', { rng: r, jitter: 0 });
    b.add(new THREE.ConeGeometry(0.03, 0.07, 3), mat(-0.02, 0.29, 0, 0, 1, 1, 0.35), dark(C.fish, 0.25), { rng: r, jitter: 0 });
    if (!relief) {
      b.add(new THREE.CylinderGeometry(0.012, 0.012, 0.13, 5), mat(0, 0.065, 0), '#6e4523', { rng: r, jitter: 0 });
      b.add(new THREE.CylinderGeometry(0.06, 0.07, 0.025, 8), mat(0, 0.012, 0), '#6e4523', { rng: r, jitter: 0.03 });
    }
  } else if (good === 'herbs') {
    // пучок 5 стебел, перев'язаний
    for (let i = 0; i < 5; i++) {
      const a = (i - 2) * 0.2;
      const tx = Math.sin(a) * 0.17, ty = 0.36 - Math.abs(i - 2) * 0.02;
      rod(b, [0, 0.0, 0], [tx, ty, (i % 2 ? 0.03 : -0.03)], 0.012, '#3c6b2a', r, 4, 0.05);
      b.add(new THREE.IcosahedronGeometry(0.055, 0), mat(tx, ty + 0.02, (i % 2 ? 0.03 : -0.03), a, 0.9, 1.4, 0.9), i % 2 ? C.herbs : light(C.herbs, 0.18), { rng: r, jitter: 0.1 });
      if (i === 2) b.add(new THREE.IcosahedronGeometry(0.04, 0), mat(tx, ty + 0.08, 0), '#f2e96b', { rng: r, jitter: 0.05 });
    }
    b.add(new THREE.CylinderGeometry(0.04, 0.04, 0.04, 8), mat(0, 0.12, 0), PAL.embroideryRed, { rng: r, jitter: 0 });
  } else if (good === 'clay') {
    // глечик (LatheGeometry) з вушком
    const pts = [[0, 0], [0.07, 0], [0.105, 0.06], [0.12, 0.13], [0.1, 0.2], [0.055, 0.25], [0.05, 0.3], [0.068, 0.33], [0.06, 0.34]].map(([x, y]) => new THREE.Vector2(x, y));
    b.add(new THREE.LatheGeometry(pts, 10), null, (cen) => cen.y > 0.12 && cen.y < 0.15 ? col('#f3ead7') : col(C.clay), { rng: r, jitter: 0.05 });
    const h = new THREE.TorusGeometry(0.06, 0.014, 4, 8, Math.PI * 1.1);
    b.add(h, mat(0.085, 0.24, 0, 0, 1, 1, 1, 0, -Math.PI * 0.55), dark(C.clay, 0.12), { rng: r, jitter: 0.03 });
  } else if (good === 'wool') {
    // клубок вовни з веретеном
    b.add(new THREE.IcosahedronGeometry(0.13, 1), mat(0, 0.13, 0), light(C.wool, 0.12), { rng: r, jitter: 0.14 });
    b.add(new THREE.TorusGeometry(0.13, 0.012, 3, 12), mat(0, 0.13, 0, 0.5, 1, 1, 1, 0.9), light(C.wool, 0.45), { rng: r, jitter: 0 });
    b.add(new THREE.TorusGeometry(0.128, 0.012, 3, 12), mat(0, 0.13, 0, -0.4, 1, 1, 1, 1.9), light(C.wool, 0.4), { rng: r, jitter: 0 });
    rod(b, [-0.02, 0.02, 0.05], [0.12, 0.36, -0.06], 0.012, '#b98a52', r, 5, 0, 0.006);
    b.add(new THREE.CylinderGeometry(0.035, 0.035, 0.02, 8), mat(0.085, 0.27, -0.035, 0, 1, 1, 1, 0.3, -0.38), '#8a5a35', { rng: r, jitter: 0 });
  } else if (good === 'fern') {
    // квітка з 6 пелюсток на стеблі (світиться)
    rod(b, [0, 0, 0], [0, 0.26, 0], 0.012, '#2f5a22', r, 4, 0);
    for (const s of [-1, 1]) b.add(new THREE.ConeGeometry(0.04, 0.14, 3), mat(s * 0.06, 0.1, 0, 0, 1, 1, 0.3, 0, -s * 1.0), '#3c7a2a', { rng: r, jitter: 0.05 });
    for (let i = 0; i < 6; i++) {
      const a = i * Math.PI / 3;
      b.add(new THREE.ConeGeometry(0.05, 0.16, 4), mat(Math.cos(a) * 0.075, 0.3, Math.sin(a) * 0.075, -a, 1, 1, 0.45, 0, -Math.PI / 2 + 0.35)
, C.fern, { rng: r, jitter: 0.06 });
    }
    b.add(new THREE.IcosahedronGeometry(0.045, 0), mat(0, 0.31, 0), '#ffe06b', { rng: r, jitter: 0 });
  } else if (good === 'gold') {
    addBulava(b, r, 0.55, true);
  }
}
/** Емблема рівня: I — сама; II — під наметом-яткою (два стовпчики, смугаста стріха); III — під рушником-аркою з позолоченим кантом. */
export function emblemGeo(good, tier) {
  const b = new Builder(), r = rngOf(100 + BONUSES.indexOf(good) * 7 + tier);
  addBare(b, good, r);
  if (tier === 2) {
    const C = GOOD_COLOR[good];
    for (const s of [-1, 1]) b.add(new THREE.BoxGeometry(0.028, 0.4, 0.028), mat(s * 0.24, 0.2, -0.13), '#6e4523', { rng: r, jitter: 0.05 });
    // стріха: 6 смуг від стовпчиків уперед і вниз
    const n = 6, w = 0.54 / n;
    for (let i = 0; i < n; i++) {
      const x = -0.27 + w * (i + 0.5);
      b.add(new THREE.BoxGeometry(w, 0.018, 0.3), mat(x, 0.35, 0.0, 0, 1, 1, 1, 0.42), i % 2 ? '#f3ead7' : (good === 'linen' ? PAL.embroideryRed : C), { rng: r, jitter: 0.03 });
      b.add(new THREE.ConeGeometry(w * 0.5, 0.05, 3), mat(x, 0.27, 0.15, 0, 1, 1, 0.3, Math.PI), i % 2 ? (good === 'linen' ? PAL.embroideryRed : C) : '#f3ead7', { rng: r, jitter: 0 });
    }
  } else if (tier === 3) {
    // рушник-арка: 9 сегментів півкола + дві «ноги», білий з червоним узором, позолочений кант
    const R = 0.26, segs = 9, cy = 0.17;
    const piece = (x, y, ang, len, red) => {
      b.add(new THREE.BoxGeometry(len, 0.065, 0.03), mat(x, y, -0.1, 0, 1, 1, 1, 0, ang), red ? PAL.embroideryRed : '#f6f0e2', { rng: r, jitter: 0.02 });
      for (const s of [-1, 1]) b.add(new THREE.BoxGeometry(len, 0.015, 0.04), mat(x - Math.sin(ang) * s * 0.04, y + Math.cos(ang) * s * 0.04, -0.1, 0, 1, 1, 1, 0, ang), PAL.gild, { rng: r, jitter: 0 });
    };
    for (let i = 0; i < segs; i++) {
      const a = Math.PI * (i + 0.5) / segs;
      piece(Math.cos(a) * R, cy + Math.sin(a) * R, a - Math.PI / 2, Math.PI * R / segs + 0.012, i % 2 === 0);
    }
    for (const s of [-1, 1]) {
      b.add(new THREE.BoxGeometry(0.07, cy, 0.03), mat(s * R, cy / 2, -0.1), '#f6f0e2', { rng: r, jitter: 0.02 });
      b.add(new THREE.BoxGeometry(0.075, 0.035, 0.035), mat(s * R, 0.06, -0.1), PAL.embroideryRed, { rng: r, jitter: 0 });
      b.add(new THREE.BoxGeometry(0.075, 0.018, 0.04), mat(s * R, 0.11, -0.1), PAL.embroideryBlack, { rng: r, jitter: 0 });
      for (const t of [-1, 1]) b.add(new THREE.BoxGeometry(0.012, cy, 0.04), mat(s * R + t * 0.04, cy / 2, -0.1), PAL.gild, { rng: r, jitter: 0 });
    }
    b.add(new THREE.OctahedronGeometry(0.04, 0), mat(0, cy + R + 0.05, -0.1), PAL.gild, { rng: r, jitter: 0 });
  }
  return b.geometry();
}
/** Де на плашці стоїть емблема (local x, z для плашки масштабу 1): праворуч унизу, вільне від ціни й назви. */
export const EMBLEM_AT = [0.17, 0.34];

// ---------------------------------------------------------------------------------------------------------- жетони
/** Кружальце: темніший обідок, світлий верх, рельєф-емблема (та сама, пласка). Низ — Y = 0. */
export function tokenGeo(good) {
  const b = new Builder(), r = rngOf(300 + TOKENS.indexOf(good));
  const R = TOKEN.r, H = TOKEN.h, gold = good === 'gold';
  const base = GOOD_COLOR[good];
  const rim = gold ? '#a6780e' : good === 'linen' ? '#b9a27a' : dark(base, 0.38);
  b.add(new THREE.CylinderGeometry(R, R, H, 20), mat(0, H / 2, 0), (cen) => cen.y > H / 2 - 0.001 ? col(base) : (rim instanceof THREE.Color ? rim : col(rim)), { rng: r, jitter: 0.02 });
  b.add(new THREE.RingGeometry(R * 0.76, R * 0.86, 20), mat(0, H + 0.001, 0, 0, 1, 1, 1, -Math.PI / 2), rim, { rng: r, jitter: 0 });
  // рельєф: емблема, покладена лицем угору й сплющена
  const rb = new Builder();
  addBare(rb, good, rngOf(1), true);
  const rg = rb.geometry();
  rg.computeBoundingBox();
  const bb = rg.boundingBox, cx = (bb.min.x + bb.max.x) / 2, cy = (bb.min.y + bb.max.y) / 2;
  const span = Math.max(bb.max.x - bb.min.x, bb.max.y - bb.min.y), s = (R * (good === 'linen' ? 0.95 : 1.15)) / span;
  const m = new THREE.Matrix4().makeTranslation(0, H + 0.002, 0)
    .multiply(new THREE.Matrix4().makeScale(s, 0.14 * s, s))
    .multiply(new THREE.Matrix4().makeRotationX(-Math.PI / 2))
    .multiply(new THREE.Matrix4().makeTranslation(-cx, -cy, -bb.min.z));
  const ink = gold ? '#8a5e00' : GOOD_INK[good];
  b.add(rg, m, ink, { rng: r, jitter: 0.06 });
  return b.geometry();
}

// ---------------------------------------------------------------------------------------------------------- плитки
/** Дерев'яна плитка гостя 0,9 × 0,9 (лице — окремо, атлас плиток). Низ — Y = 0. */
export const TILE_H = 0.07;
export function tileGeo() {
  const b = new Builder(), r = rngOf(11);
  const g = new THREE.BoxGeometry(TILE, TILE_H, TILE, 1, 1, 1);
  b.add(g, mat(0, TILE_H / 2, 0), (cen) => cen.y > TILE_H / 2 - 0.001 ? col('#c8955a') : col('#6e4523'), { rng: r, jitter: 0.03 });
  return b.geometry();
}
export function tileFaceGeo() {
  const g = new THREE.PlaneGeometry(TILE - 0.06, TILE - 0.06);
  g.applyMatrix4(mat(0, TILE_H + 0.002, 0, 0, 1, 1, 1, -Math.PI / 2));
  return g;
}

// ---------------------------------------------------------------------------------------------------------- гості
const SKIN = '#e8bf95';
function person(b, r, { coat, coatLen = 0.42, coatR = 0.17, shirt = '#f3ead7', pants = null, arms = coat, beard = null, mustache = '#3a2a1a', hairCol = '#3a2a1a', hand = 0.0 }) {
  // ноги/чоботи
  for (const s of [-1, 1]) b.add(new THREE.CylinderGeometry(0.035, 0.04, 0.12, 6), mat(s * 0.06, 0.06, 0), pants || '#2a1d17', { rng: r, jitter: 0.04 });
  // тулуб — конус
  b.add(new THREE.ConeGeometry(coatR, coatLen, 8, 1, true), mat(0, 0.1 + coatLen / 2, 0), coat, { rng: r, jitter: 0.08 });
  b.add(new THREE.CylinderGeometry(0.075, coatR * 0.98, 0.05, 8), mat(0, 0.12, 0), coat, { rng: r, jitter: 0.05 });
  b.add(new THREE.CylinderGeometry(0.05, 0.06, 0.06, 8), mat(0, 0.1 + coatLen - 0.01, 0), shirt, { rng: r, jitter: 0.03 });
  // голова
  const hy = 0.1 + coatLen + 0.08;
  b.add(new THREE.IcosahedronGeometry(0.085, 1), mat(0, hy, 0, 0, 1, 1.08, 1), SKIN, { rng: r, jitter: 0.04 });
  b.add(new THREE.ConeGeometry(0.018, 0.04, 4), mat(0, hy, 0.085, 0, 1, 1, 1, Math.PI / 2), mix(SKIN, '#b86b50', 0.3), { rng: r, jitter: 0 });
  for (const s of [-1, 1]) b.add(new THREE.SphereGeometry(0.012, 4, 3), mat(s * 0.032, hy + 0.02, 0.075), '#1a120c', { rng: r, jitter: 0 });
  if (mustache) b.add(new THREE.BoxGeometry(0.1, 0.018, 0.02), mat(0, hy - 0.03, 0.078, 0, 1, 1, 1, 0, 0), mustache, { rng: r, jitter: 0 });
  if (beard) b.add(new THREE.ConeGeometry(0.06, 0.1, 6), mat(0, hy - 0.08, 0.05, 0, 1, 1, 0.7, Math.PI), beard, { rng: r, jitter: 0.05 });
  // руки
  const sy = 0.1 + coatLen - 0.04;
  for (const s of [-1, 1]) {
    const hx = s * (0.17 + hand * (s > 0 ? 1 : 0)), hz = s > 0 ? 0.08 + hand : 0.04;
    rod(b, [s * 0.1, sy, 0], [hx, sy - 0.19, hz], 0.03, arms, r, 5, 0.05, 0.026);
    b.add(new THREE.IcosahedronGeometry(0.03, 0), mat(hx, sy - 0.21, hz), SKIN, { rng: r, jitter: 0.03 });
  }
  return { hy, sy };
}
/** Лo-poly постать гостя (висота ~0,7). Початок — між ступнями; дивиться на +z. */
export function guestGeo(id) {
  const b = new Builder(), r = rngOf(500 + id);
  const RED = '#b3262e';
  if (id === 0) {   // Солопій Черевик — сіра шапка й батіг
    const { hy } = person(b, r, { coat: '#7a5a3a', pants: '#3a2c22', mustache: '#8a8a88' });
    b.add(new THREE.CylinderGeometry(0.1, 0.1, 0.012, 10), mat(0, hy + 0.06, 0), '#8a8a88', { rng: r, jitter: 0 });
    b.add(new THREE.CylinderGeometry(0.07, 0.085, 0.09, 8), mat(0, hy + 0.105, 0), '#9a9a96', { rng: r, jitter: 0.06 });
    rod(b, [0.17, 0.28, 0.08], [0.24, 0.6, 0.12], 0.008, '#6e4523', r, 4, 0);
    for (let i = 0; i < 5; i++) rod(b, [0.24 + i * 0.03, 0.6 - i * i * 0.012, 0.12], [0.27 + i * 0.03, 0.6 - (i + 1) * (i + 1) * 0.012, 0.12], 0.005, '#2a1d17', r, 3, 0);
  } else if (id === 1) {   // Хівря — очіпок і червона запаска
    const { hy } = person(b, r, { coat: '#3b4a6b', coatR: 0.19, mustache: null, arms: '#f3ead7' });
    b.add(new THREE.SphereGeometry(0.095, 8, 5, 0, Math.PI * 2, 0, Math.PI / 2), mat(0, hy + 0.01, -0.01, 0, 1, 1.05, 1.08), '#c9a227', { rng: r, jitter: 0.06 });
    b.add(new THREE.BoxGeometry(0.2, 0.32, 0.02), mat(0, 0.3, 0.13, 0, 1, 1, 1, -0.32), RED, { rng: r, jitter: 0.04 });
    b.add(new THREE.BoxGeometry(0.2, 0.03, 0.025), mat(0, 0.18, 0.168, 0, 1, 1, 1, -0.32), PAL.embroideryBlack, { rng: r, jitter: 0 });
  } else if (id === 2) {   // Параска — вінок зі стрічками
    const { hy } = person(b, r, { coat: RED, coatR: 0.18, shirt: '#ffffff', mustache: null, arms: '#ffffff' });
    b.add(new THREE.IcosahedronGeometry(0.088, 1), mat(0, hy + 0.01, -0.02, 0, 1, 1, 1), '#c9a066', { rng: r, jitter: 0.04 });
    const fl = ['#e8483b', '#ffd23f', '#3d7cc9', '#ffffff', '#e8483b', '#4f9a3a', '#ffd23f'];
    for (let i = 0; i < 7; i++) { const a = i / 7 * Math.PI * 2; b.add(new THREE.IcosahedronGeometry(0.028, 0), mat(Math.cos(a) * 0.08, hy + 0.07, Math.sin(a) * 0.08 - 0.01), fl[i], { rng: r, jitter: 0 }); }
    ['#e8483b', '#ffd23f', '#3d7cc9', '#4f9a3a'].forEach((c, i) => b.add(new THREE.BoxGeometry(0.025, 0.3, 0.008), mat(-0.045 + i * 0.03, hy - 0.11, -0.1, 0, 1, 1, 1, 0.18), c, { rng: r, jitter: 0 }));
  } else if (id === 3) {   // Грицько — смушкова шапка
    const { hy } = person(b, r, { coat: '#f3ead7', pants: '#2f4f8f', mustache: '#3a2a1a' });
    b.add(new THREE.CylinderGeometry(0.085, 0.092, 0.15, 9), mat(0, hy + 0.12, 0, 0, 1, 1, 1, -0.12), '#1f1a18', { rng: r, jitter: 0.12 });
    b.add(new THREE.CylinderGeometry(0.1, 0.1, 0.03, 9), mat(0, 0.44, 0), RED, { rng: r, jitter: 0 });
  } else if (id === 4) {   // Кум Цибуля — кожух
    const { hy } = person(b, r, { coat: '#c8a46a', coatR: 0.2, mustache: '#5a4030' });
    b.add(new THREE.TorusGeometry(0.19, 0.03, 4, 10), mat(0, 0.13, 0, 0, 1, 1, 1, Math.PI / 2), '#f6f0e2', { rng: r, jitter: 0.08 });
    b.add(new THREE.TorusGeometry(0.07, 0.03, 4, 8), mat(0, 0.5, 0, 0, 1, 1, 1, Math.PI / 2), '#f6f0e2', { rng: r, jitter: 0.08 });
    b.add(new THREE.CylinderGeometry(0.075, 0.085, 0.07, 8), mat(0, hy + 0.08, 0), '#5e3b21', { rng: r, jitter: 0.05 });
  } else if (id === 5) {   // Попович — капелюх і довга свита
    const { hy } = person(b, r, { coat: '#2c2a3a', coatLen: 0.5, coatR: 0.16, mustache: null, beard: null });
    b.add(new THREE.CylinderGeometry(0.15, 0.15, 0.012, 12), mat(0, hy + 0.065, 0), '#151218', { rng: r, jitter: 0 });
    b.add(new THREE.CylinderGeometry(0.075, 0.08, 0.08, 10), mat(0, hy + 0.11, 0), '#151218', { rng: r, jitter: 0.05 });
  } else if (id === 6) {   // Циган-міняйло — червона хустка на шиї й вуздечка
    const { hy } = person(b, r, { coat: '#2f5a3a', pants: '#3a2c22', mustache: '#1a120c', hairCol: '#1a120c' });
    b.add(new THREE.TorusGeometry(0.06, 0.025, 4, 10), mat(0, hy - 0.08, 0, 0, 1, 1, 1, Math.PI / 2), RED, { rng: r, jitter: 0.04 });
    b.add(new THREE.ConeGeometry(0.04, 0.07, 3), mat(0, hy - 0.13, 0.06, 0, 1, 1, 0.4, Math.PI), RED, { rng: r, jitter: 0 });
    b.add(new THREE.TorusGeometry(0.07, 0.01, 3, 10), mat(0.2, 0.25, 0.1, 0, 1, 1.3, 1), '#6e4523', { rng: r, jitter: 0 });
    b.add(new THREE.BoxGeometry(0.03, 0.04, 0.01), mat(0.2, 0.16, 0.1), '#c9a227', { rng: r, jitter: 0 });
    b.add(new THREE.SphereGeometry(0.09, 8, 4, 0, Math.PI * 2, 0, Math.PI / 2), mat(0, hy + 0.02, 0), '#1a120c', { rng: r, jitter: 0.04 });
  } else if (id === 7) {   // Пан сотник — шапка з червоним шликом і шабля
    const { hy } = person(b, r, { coat: '#2f4f8f', coatR: 0.18, pants: RED, mustache: '#2a1d17' });
    b.add(new THREE.CylinderGeometry(0.088, 0.095, 0.1, 9), mat(0, hy + 0.09, 0), '#3a2a20', { rng: r, jitter: 0.08 });
    b.add(new THREE.ConeGeometry(0.06, 0.16, 6), mat(0.02, hy + 0.13, -0.07, 0, 1, 1, 1, -1.6), RED, { rng: r, jitter: 0.03 });
    b.add(new THREE.SphereGeometry(0.02, 4, 3), mat(0.03, hy + 0.1, -0.15), PAL.gild, { rng: r, jitter: 0 });
    b.add(new THREE.CylinderGeometry(0.1, 0.12, 0.03, 8), mat(0, 0.36, 0), PAL.gild, { rng: r, jitter: 0 });
    for (let i = 0; i < 4; i++) rod(b, [-0.19 + i * 0.005, 0.36 - i * 0.075, 0.02 + i * i * 0.012], [-0.19 + (i + 1) * 0.005, 0.36 - (i + 1) * 0.075, 0.02 + (i + 1) * (i + 1) * 0.012], 0.012, '#c9ccd2', r, 4, 0);
    b.add(new THREE.BoxGeometry(0.06, 0.012, 0.03), mat(-0.19, 0.37, 0.02), PAL.gild, { rng: r, jitter: 0 });
  } else if (id === 8) {   // Кобзар — кобза
    person(b, r, { coat: '#6b6355', mustache: '#d8d4cc', beard: '#d8d4cc', hand: 0.05 });
    b.add(new THREE.SphereGeometry(0.11, 9, 6), mat(0.02, 0.34, 0.16, 0.3, 1, 1.2, 0.45), '#8a5a35', { rng: r, jitter: 0.05 });
    b.add(new THREE.CircleGeometry(0.03, 8), mat(0.025, 0.36, 0.212, 0.3), '#2a1d17', { rng: r, jitter: 0 });
    b.add(new THREE.BoxGeometry(0.035, 0.24, 0.025), mat(-0.04, 0.52, 0.16, 0.3, 1, 1, 1, 0, 0.35), '#5e3b21', { rng: r, jitter: 0.03 });
  } else {   // Писар — перо за вухом і сувій
    const { hy } = person(b, r, { coat: '#3a3330', coatR: 0.16, mustache: '#5a4030' });
    b.add(new THREE.ConeGeometry(0.018, 0.2, 4), mat(0.09, hy + 0.04, -0.02, 0, 1, 1, 1, 0, -0.5), '#ffffff', { rng: r, jitter: 0 });
    b.add(new THREE.CylinderGeometry(0.03, 0.03, 0.22, 8), mat(0.21, 0.3, 0.12, 0, 1, 1, 1, 0, Math.PI / 2), '#f3ead7', { rng: r, jitter: 0.03 });
    b.add(new THREE.CylinderGeometry(0.033, 0.033, 0.03, 8), mat(0.3, 0.3, 0.12, 0, 1, 1, 1, 0, Math.PI / 2), PAL.embroideryRed, { rng: r, jitter: 0 });
    b.add(new THREE.SphereGeometry(0.088, 8, 4, 0, Math.PI * 2, 0, Math.PI / 2), mat(0, hy + 0.02, 0), '#5a4030', { rng: r, jitter: 0.04 });
  }
  return b.geometry();
}

/** Козак старшини: оселедець, червоний жупан, шабля. Стоїть на плитці (початок — між ступнями). */
export function cossackGeo() {
  const b = new Builder(), r = rngOf(77);
  const { hy } = person(b, r, { coat: '#b3262e', coatR: 0.18, pants: '#2f4f8f', mustache: '#1a120c', arms: '#b3262e' });
  // поголена голова й оселедець
  b.add(new THREE.ConeGeometry(0.025, 0.16, 4), mat(-0.02, hy + 0.06, -0.04, 0, 1, 1, 1, -0.9, 0.3), '#1a120c', { rng: r, jitter: 0 });
  b.add(new THREE.BoxGeometry(0.12, 0.02, 0.02), mat(0, hy - 0.032, 0.078, 0, 1, 1, 1, 0, 0), '#1a120c', { rng: r, jitter: 0 });
  for (const s of [-1, 1]) b.add(new THREE.ConeGeometry(0.01, 0.06, 3), mat(s * 0.06, hy - 0.06, 0.075, 0, 1, 1, 1, 0, s * 0.3), '#1a120c', { rng: r, jitter: 0 });
  b.add(new THREE.CylinderGeometry(0.11, 0.13, 0.035, 8), mat(0, 0.36, 0), PAL.gild, { rng: r, jitter: 0 });
  for (let i = 0; i < 4; i++) rod(b, [0.2 - i * 0.004, 0.37 - i * 0.075, 0.03 + i * i * 0.012], [0.2 - (i + 1) * 0.004, 0.37 - (i + 1) * 0.075, 0.03 + (i + 1) * (i + 1) * 0.012], 0.013, '#d2d5da', r, 4, 0);
  b.add(new THREE.BoxGeometry(0.07, 0.014, 0.035), mat(0.2, 0.38, 0.03), PAL.gild, { rng: r, jitter: 0 });
  return b.geometry();
}

// ---------------------------------------------------------------------------------------------------------- булава
/** Булава: держак, куля з ребрами-пір'ями. Лежить уздовж X, середина — у початку. */
function addBulava(b, r, s = 1, relief = false) {
  const G = PAL.gild, Gd = '#a67c1c';
  rod(b, [-0.3 * s, 0, 0], [0.16 * s, 0, 0], 0.022 * s, G, r, 8, 0.03);
  b.add(new THREE.SphereGeometry(0.035 * s, 8, 5), mat(-0.32 * s, 0, 0), Gd, { rng: r, jitter: 0.03 });
  for (const x of [-0.18, 0.02]) b.add(new THREE.CylinderGeometry(0.03 * s, 0.03 * s, 0.03 * s, 8), mat(x * s, 0, 0, 0, 1, 1, 1, 0, Math.PI / 2), Gd, { rng: r, jitter: 0 });
  b.add(new THREE.IcosahedronGeometry(0.07 * s, 1), mat(0.24 * s, 0, 0), G, { rng: r, jitter: 0.05 });
  const n = relief ? 4 : 8;
  for (let i = 0; i < n; i++) {
    const a = i / n * Math.PI * 2;
    b.add(new THREE.BoxGeometry(0.13 * s, 0.075 * s, 0.018 * s), mat(0.24 * s, Math.cos(a) * 0.065 * s, Math.sin(a) * 0.065 * s, 0, 1, 1, 1, -a + Math.PI / 2), Gd, { rng: r, jitter: 0.04 });
  }
  b.add(new THREE.OctahedronGeometry(0.03 * s, 0), mat(0.34 * s, 0, 0), '#b3262e', { rng: r, jitter: 0 });
}
export function bulavaGeo() {
  const b = new Builder();
  addBulava(b, rngOf(88), 1.7);
  return b.geometry();
}
/** Підставка з червоною подушкою й китицями (у статичний меш). Верх подушки — PED_H + 0,1. */
export function addPedestal(b) {
  const r = rngOf(91), { x, z } = AWARD;
  b.add(new THREE.BoxGeometry(1.7, PED_H, 0.95), mat(x, PED_H / 2, z), '#6b4426', { rng: r, jitter: 0.04 });
  b.add(new THREE.BoxGeometry(1.78, 0.04, 1.03), mat(x, PED_H - 0.02, z), '#5a3820', { rng: r, jitter: 0.03 });
  b.add(new THREE.BoxGeometry(1.78, 0.05, 1.03), mat(x, 0.025, z), '#4a2e1a', { rng: r, jitter: 0.03 });
  // подушка під булаву (ліва половина)
  const cx = x - 0.4;
  const cu = new THREE.BoxGeometry(0.8, 0.12, 0.62, 2, 1, 2);
  const p = cu.attributes.position;
  for (let i = 0; i < p.count; i++) { const yy = p.getY(i); if (yy > 0) p.setY(i, yy + (Math.abs(p.getX(i)) < 0.1 && Math.abs(p.getZ(i)) < 0.1 ? 0.03 : 0)); }
  b.add(cu, mat(cx, PED_H + 0.06, z), '#a8202a', { rng: r, jitter: 0.05 });
  for (const [sx, sz] of [[-1, -1], [1, -1], [-1, 1], [1, 1]]) {
    b.add(new THREE.SphereGeometry(0.03, 5, 4), mat(cx + sx * 0.41, PED_H + 0.06, z + sz * 0.32), PAL.gild, { rng: r, jitter: 0 });
    b.add(new THREE.ConeGeometry(0.035, 0.12, 6), mat(cx + sx * 0.42, PED_H + 0.0, z + sz * 0.33), PAL.gild, { rng: r, jitter: 0.04 });
  }
}
export const BULAVA_HOME = { x: AWARD.x - 0.4, y: PED_H + 0.2, z: AWARD.z, ry: 0.35 };
export const STAR_HOME = { x: AWARD.x + 0.42, y: PED_H, z: AWARD.z };

// ---------------------------------------------------------------------------------------------------------- прилавок
/** Дошка-прилавок 7 × 1,6 (біла — колір дає місце) з кантом і прапорцем-фігурою. Початок — центр смуги на столі. */
export function addCounter(b, P, colorHex, shape, light) {
  const r = rngOf(600 + shape);
  const M0 = mat(P.x, 0, P.z, P.ry);
  const base = col(colorHex), kant = light ? col('#8a7a5a') : dark(colorHex, 0.45);
  const top = light ? col(colorHex) : mix(colorHex, '#f4e6c6', 0.18);
  const add = (g, m, c, j = 0.04) => b.add(g, M0.clone().multiply(m), c, { rng: r, jitter: j });
  add(new THREE.BoxGeometry(COUNTER_W, BOARD_H, COUNTER_D), mat(0, BOARD_H / 2, 0), top);
  add(new THREE.BoxGeometry(COUNTER_W + 0.08, 0.035, 0.08), mat(0, BOARD_H + 0.012, COUNTER_D / 2), kant);
  add(new THREE.BoxGeometry(COUNTER_W + 0.08, 0.035, 0.08), mat(0, BOARD_H + 0.012, -COUNTER_D / 2), kant);
  add(new THREE.BoxGeometry(0.08, 0.035, COUNTER_D), mat(COUNTER_W / 2, BOARD_H + 0.012, 0), kant);
  add(new THREE.BoxGeometry(0.08, 0.035, COUNTER_D), mat(-COUNTER_W / 2, BOARD_H + 0.012, 0), kant);
  // прапорець: держак і табличка-фігура (●▲■◆) у кольорі місця з темним кантом
  const fx = -COUNTER_W / 2 - 0.25, fz = 0.45, fy = 0.75;
  add(new THREE.CylinderGeometry(0.03, 0.035, fy + 0.15, 6), mat(fx, (fy + 0.15) / 2, fz), '#4a2e1a');
  add(new THREE.CylinderGeometry(0.12, 0.14, 0.05, 8), mat(fx, 0.025, fz), '#4a2e1a');
  const K = '#22170d', sz = 0.32;
  const face = (g, gIn, m) => { add(g, m, K, 0); add(gIn, m.clone().multiply(mat(0, 0, 0, 0, 1, 1, 1.6)), base, 0.02); };
  const fm = mat(fx + 0.0, fy, fz, Math.PI / 2 * 0);
  if (shape === 0) face(new THREE.CylinderGeometry(sz / 2, sz / 2, 0.03, 18), new THREE.CylinderGeometry(sz / 2 - 0.035, sz / 2 - 0.035, 0.034, 18), fm.clone().multiply(mat(0, 0, 0, 0, 1, 1, 1, Math.PI / 2)));
  else if (shape === 1) face(new THREE.CylinderGeometry(sz * 0.62, sz * 0.62, 0.03, 3), new THREE.CylinderGeometry(sz * 0.62 - 0.07, sz * 0.62 - 0.07, 0.034, 3), fm.clone().multiply(mat(0, -0.02, 0, 0, 1, 1, 1, -Math.PI / 2)));
  else if (shape === 2) face(new THREE.BoxGeometry(sz, sz, 0.03), new THREE.BoxGeometry(sz - 0.07, sz - 0.07, 0.034), fm);
  else face(new THREE.BoxGeometry(sz * 0.75, sz * 0.75, 0.03), new THREE.BoxGeometry(sz * 0.75 - 0.07, sz * 0.75 - 0.07, 0.034), fm.clone().multiply(mat(0, 0, 0, 0, 1, 1, 1, 0, Math.PI / 4)));
}
/** Де на прилавку що лежить (local): стовпці 0–5 (BONUSES / TOKENS), бронь 0–2, гості, старшина, булава. */
export const SPOT = {
  colX: (i) => -3.05 + i * 0.6,
  cardZ: 0.02,          // верхня (найновіша) плашка стосу; старші — далі від власника на 0,08
  tokZ: 0.56,
  resX: (i) => 0.72 + i * 0.6,
  resZ: -0.12,
  guestX: (k) => 2.55 + (k % 3) * 0.36,
  guestZ: (k) => -0.42 + Math.floor(k / 3) * 0.32,
  starX: 3.05, starZ: 0.44,
  bulavaX: 2.55, bulavaZ: 0.5,
};

// ---------------------------------------------------------------------------------------------------------- підсвітка
/** Рамка довкола плашки (кант PAL.legalEdge і золото PAL.legal) — у площині XZ, w × d — внутрішній розмір. */
export function frameGeo(w, d, t = 0.07) {
  const b = new Builder();
  const ring = (ww, dd, tt, y, c) => {
    for (const [x, z, sx, sz] of [[0, -dd / 2 - tt / 2, ww + 2 * tt, tt], [0, dd / 2 + tt / 2, ww + 2 * tt, tt], [-ww / 2 - tt / 2, 0, tt, dd], [ww / 2 + tt / 2, 0, tt, dd]])
      b.add(new THREE.BoxGeometry(sx, 0.012, sz), mat(x, y, z), c, { jitter: 0 });
  };
  ring(w + 0.02, d + 0.02, t + 0.04, 0.004, PAL.legalEdge);
  ring(w + 0.04, d + 0.04, t, 0.01, PAL.legal);
  return b.geometry();
}
/** Пунктирна рамка (позначка near). */
export function dashedFrameGeo(w, d) {
  const b = new Builder();
  const dash = 0.12, gap = 0.08, t = 0.045;
  const side = (x0, z0, x1, z1) => {
    const L = Math.hypot(x1 - x0, z1 - z0), n = Math.floor((L + gap) / (dash + gap));
    const used = n * dash + (n - 1) * gap, off = (L - used) / 2;
    for (let i = 0; i < n; i++) {
      const s = off + i * (dash + gap) + dash / 2, k = s / L;
      const x = x0 + (x1 - x0) * k, z = z0 + (z1 - z0) * k;
      const horiz = Math.abs(z1 - z0) < 1e-6;
      b.add(new THREE.BoxGeometry(horiz ? dash : t + 0.03, 0.01, horiz ? t + 0.03 : dash), mat(x, 0.004, z), PAL.legalEdge, { jitter: 0 });
      b.add(new THREE.BoxGeometry(horiz ? dash - 0.02 : t, 0.012, horiz ? t : dash - 0.02), mat(x, 0.008, z), PAL.near, { jitter: 0 });
    }
  };
  const W = w / 2 + 0.05, D = d / 2 + 0.05;
  side(-W, -D, W, -D); side(-W, D, W, D); side(-W, -D, -W, D); side(W, -D, W, D);
  return b.geometry();
}
/** Кільце довкола купи жетонів. */
export function pileRingGeo() {
  const b = new Builder();
  b.add(new THREE.RingGeometry(0.33, 0.43, 32), mat(0, 0.003, 0, 0, 1, 1, 1, -Math.PI / 2), PAL.legalEdge, { jitter: 0 });
  b.add(new THREE.RingGeometry(0.345, 0.41, 32), mat(0, 0.006, 0, 0, 1, 1, 1, -Math.PI / 2), PAL.legal, { jitter: 0 });
  return b.geometry();
}
/** Площинка 1×1 у XZ під текстуру світіння. */
export function flatGeo() {
  const g = new THREE.PlaneGeometry(1, 1);
  g.applyMatrix4(mat(0, 0, 0, 0, 1, 1, 1, -Math.PI / 2));
  return g;
}
/** Радіальне світіння (центр яскравий, край прозорий). */
export function glowTexture(rgb = '255,210,63') {
  const S = 64, cv = document.createElement('canvas'); cv.width = cv.height = S;
  const g = cv.getContext('2d');
  const gr = g.createRadialGradient(S / 2, S / 2, 0, S / 2, S / 2, S / 2);
  gr.addColorStop(0, `rgba(${rgb},1)`); gr.addColorStop(0.45, `rgba(${rgb},0.55)`); gr.addColorStop(1, `rgba(${rgb},0)`);
  g.fillStyle = gr; g.fillRect(0, 0, S, S);
  const t = new THREE.CanvasTexture(cv); t.colorSpace = THREE.SRGBColorSpace;
  return t;
}
/** Прямокутне світіння під плашкою (м'які краї). */
export function rectGlowTexture() {
  const S = 64, cv = document.createElement('canvas'); cv.width = cv.height = S;
  const g = cv.getContext('2d');
  const img = g.createImageData(S, S);
  for (let y = 0; y < S; y++) for (let x = 0; x < S; x++) {
    const dx = Math.max(0, Math.abs(x - S / 2 + 0.5) / (S / 2) - 0.55) / 0.45, dy = Math.max(0, Math.abs(y - S / 2 + 0.5) / (S / 2) - 0.55) / 0.45;
    const d = Math.min(1, Math.hypot(dx, dy));
    const a = Math.pow(1 - d, 1.6);
    const i = (y * S + x) * 4;
    img.data[i] = 255; img.data[i + 1] = 214; img.data[i + 2] = 80; img.data[i + 3] = Math.round(a * 255);
  }
  g.putImageData(img, 0, 0);
  const t = new THREE.CanvasTexture(cv); t.colorSpace = THREE.SRGBColorSpace;
  return t;
}
/** Стрічка фіналу (двобічна смужка). */
export function ribbonGeo() {
  const g = new THREE.PlaneGeometry(0.06, 0.26, 1, 2);
  const p = g.attributes.position;
  for (let i = 0; i < p.count; i++) p.setZ(i, Math.sin(p.getY(i) * 12) * 0.03);
  return g;
}
/** Ліхтар гірлянди: каркас і скло (скло — яскраве, біле — колір дає матеріал). */
export function lampGeo() {
  const b = new Builder(), r = rngOf(41);
  b.add(new THREE.CylinderGeometry(0.11, 0.14, 0.24, 6), mat(0, -0.16, 0), '#ffd98a', { rng: r, jitter: 0.04 });
  b.add(new THREE.ConeGeometry(0.15, 0.1, 6), mat(0, -0.02, 0), '#3a2414', { rng: r, jitter: 0 });
  b.add(new THREE.CylinderGeometry(0.15, 0.13, 0.04, 6), mat(0, -0.29, 0), '#3a2414', { rng: r, jitter: 0 });
  return b.geometry();
}

// ---------------------------------------------------------------------------------------------------------- тло
/** Гірлянда: мотузка між двома стовпами, ліхтарі на ній (точки для InstancedMesh). */
export const GARLAND = { x0: -9.2, x1: 9.2, y: 3.3, z: -7.0, sag: 0.9, n: 13 };
export function garlandPoint(t) {
  const { x0, x1, y, z, sag } = GARLAND;
  return [x0 + (x1 - x0) * t, y - sag * 4 * t * (1 - t), z];
}
function addStall(b, x, z, w, good, r, facing = 1) {
  const post = '#5a3820', h = 2.1 + r() * 0.4, d = 1.3;
  for (const sx of [-1, 1]) for (const sz of [-1, 1]) b.add(new THREE.BoxGeometry(0.1, h + (sz < 0 ? 0.35 : 0), 0.1), mat(x + sx * w / 2, (h + (sz < 0 ? 0.35 : 0)) / 2 - 0.9, z + sz * d / 2), post, { rng: r, jitter: 0.06 });
  // прилавок ятки з товаром
  b.add(new THREE.BoxGeometry(w - 0.1, 0.85, 0.7), mat(x, -0.9 + 0.425, z + d / 2 - 0.3), '#7a5130', { rng: r, jitter: 0.06 });
  b.add(new THREE.BoxGeometry(w + 0.05, 0.06, 0.8), mat(x, -0.9 + 0.86, z + d / 2 - 0.3), '#8a5a35', { rng: r, jitter: 0.04 });
  const top = -0.9 + 0.89;
  for (let i = 0; i < 4; i++) {
    const gx = x - w / 2 + 0.35 + i * (w - 0.7) / 3, gz = z + d / 2 - 0.3 + (r() - 0.5) * 0.2;
    const kind = Math.floor(r() * 4);
    if (kind === 0) b.add(new THREE.LatheGeometry([[0, 0], [0.12, 0.02], [0.16, 0.15], [0.1, 0.3], [0.08, 0.36]].map(([a, c]) => new THREE.Vector2(a, c)), 7), mat(gx, top, gz), mix(GOOD_COLOR.clay, '#000', r() * 0.2), { rng: r, jitter: 0.08 });
    else if (kind === 1) b.add(new THREE.IcosahedronGeometry(0.2, 0), mat(gx, top + 0.16, gz, r(), 1, 0.8, 1), mix('#c8b48a', '#8a7a5a', r()), { rng: r, jitter: 0.1 });
    else if (kind === 2) b.add(new THREE.CylinderGeometry(0.1, 0.1, 0.4, 7), mat(gx, top + 0.1, gz, 0, 1, 1, 1, 0, Math.PI / 2), r() > 0.5 ? GOOD_COLOR.linen : PAL.embroideryRed, { rng: r, jitter: 0.05 });
    else b.add(new THREE.BoxGeometry(0.34, 0.2, 0.26), mat(gx, top + 0.1, gz, r()), GOOD_COLOR[GOODS[Math.floor(r() * 5)]], { rng: r, jitter: 0.08 });
  }
  // смугаста стріха кольору товару (нахилена до столу)
  const n = Math.max(5, Math.round(w / 0.32)), sw = (w + 0.3) / n;
  const C = good === 'linen' ? PAL.embroideryRed : GOOD_COLOR[good];
  for (let i = 0; i < n; i++) {
    const sx = x - (w + 0.3) / 2 + sw * (i + 0.5);
    b.add(new THREE.BoxGeometry(sw, 0.04, d + 0.5), mat(sx, h - 0.9 + 0.17, z + 0.05 * facing, 0, 1, 1, 1, 0.26 * facing), i % 2 ? '#efe2c6' : C, { rng: r, jitter: 0.05 });
    b.add(new THREE.ConeGeometry(sw * 0.5, 0.16, 3), mat(sx, h - 0.9 - 0.08, z + (d / 2 + 0.24) * facing, 0, 1, 1, 0.3, Math.PI), i % 2 ? C : '#efe2c6', { rng: r, jitter: 0.03 });
  }
}
function addCart(b, x, z, ry, r) {
  const M0 = mat(x, -0.9, z, ry);
  const add = (g, m, c, j = 0.06) => b.add(g, M0.clone().multiply(m), c, { rng: r, jitter: j });
  const wood = '#7a5130', dk = '#4a2e1a';
  add(new THREE.BoxGeometry(2.4, 0.12, 1.2), mat(0, 0.85, 0), wood);
  for (const s of [-1, 1]) add(new THREE.BoxGeometry(2.4, 0.4, 0.06), mat(0, 1.1, s * 0.6, 0, 1, 1, 1, s * 0.12), wood);
  add(new THREE.BoxGeometry(0.06, 0.4, 1.2), mat(1.2, 1.1, 0), wood);
  add(new THREE.BoxGeometry(0.06, 0.4, 1.2), mat(-1.2, 1.1, 0), wood);
  // колеса зі спицями
  for (const wx of [-0.8, 0.8]) for (const s of [-1, 1]) {
    add(new THREE.TorusGeometry(0.5, 0.05, 4, 14), mat(wx, 0.52, s * 0.72), dk);
    for (let k = 0; k < 6; k++) add(new THREE.BoxGeometry(0.95, 0.04, 0.04), mat(wx, 0.52, s * 0.72, 0, 1, 1, 1, 0, k * Math.PI / 6), wood);
    add(new THREE.CylinderGeometry(0.09, 0.09, 0.14, 8), mat(wx, 0.52, s * 0.72, 0, 1, 1, 1, Math.PI / 2), dk);
  }
  // дишло
  add(new THREE.BoxGeometry(2.2, 0.08, 0.08), mat(2.1, 0.45, 0, 0, 1, 1, 1, 0, -0.32), wood);
  // поклажа: мішки
  for (let i = 0; i < 4; i++) add(new THREE.IcosahedronGeometry(0.32, 0), mat(-0.7 + i * 0.45, 1.2, (r() - 0.5) * 0.4, r(), 1, 0.75, 1), mix('#c8b48a', '#a08a60', r()), 0.1);
}
function addPoplar(b, x, z, h, r) {
  b.add(new THREE.CylinderGeometry(0.12, 0.18, h * 0.25, 5), mat(x, -0.9 + h * 0.12, z), '#1c1626', { rng: r, jitter: 0.04 });
  b.add(new THREE.ConeGeometry(0.9 + r() * 0.3, h, 6), mat(x, -0.9 + h * 0.2 + h / 2, z, r()), mix('#1e2a2a', '#26223a', r()), { rng: r, jitter: 0.08 });
}
function addChurch(b, x, z, r) {
  const c = '#1f1a30', c2 = '#2a2340';
  b.add(new THREE.BoxGeometry(4.5, 4, 3.5), mat(x, 1.1, z), c, { rng: r, jitter: 0.02 });
  b.add(new THREE.CylinderGeometry(1.2, 1.2, 1.8, 8), mat(x, 4.0, z), c2, { rng: r, jitter: 0.03 });
  b.add(new THREE.SphereGeometry(1.25, 8, 5, 0, Math.PI * 2, 0, Math.PI / 2), mat(x, 4.9, z, 0, 1, 1.3, 1), c, { rng: r, jitter: 0.03 });
  b.add(new THREE.CylinderGeometry(0.1, 0.2, 0.7, 6), mat(x, 6.7, z), c2, { rng: r, jitter: 0 });
  b.add(new THREE.BoxGeometry(0.08, 0.9, 0.08), mat(x, 7.4, z), '#c9a227', { rng: r, jitter: 0 });
  b.add(new THREE.BoxGeometry(0.5, 0.07, 0.08), mat(x, 7.55, z), '#c9a227', { rng: r, jitter: 0 });
  for (const s of [-1, 1]) {
    b.add(new THREE.CylinderGeometry(0.7, 0.7, 1.0, 8), mat(x + s * 2.0, 3.6, z), c2, { rng: r, jitter: 0.03 });
    b.add(new THREE.SphereGeometry(0.72, 8, 4, 0, Math.PI * 2, 0, Math.PI / 2), mat(x + s * 2.0, 4.1, z, 0, 1, 1.3, 1), c, { rng: r, jitter: 0.03 });
  }
}
/**
 * Усе нерухоме: земля, стіл з дощок і ніжками, підставка з подушкою, ятки, вози, мішки, бочки, тополі, церква, стовпи й
 * мотузка гірлянди. Повертає { geo, spots } — spots: адитивні плями світла (окремий меш).
 */
export function staticGeo(hi) {
  const b = new Builder(), r = rngOf(2026);
  // земля ярмарку
  b.add(new THREE.PlaneGeometry(90, 70, 18, 14), mat(0, -0.9, -10, 0, 1, 1, 1, -Math.PI / 2), (cen) => mix('#3a2a1c', '#4a3524', rngOf(Math.floor((cen.x + 99) * 13 + (cen.y + 99) * 7))()), { rng: r, jitter: 0.08 });
  // стільниця з дощок уздовж X
  const { x0, x1, z0, z1 } = TABLE;
  const tones = ['#8a5a35', '#93613a', '#845432', '#8e5d36', '#7f5030'];
  const W = 0.76;
  for (let z = z0; z < z1 - 0.01; z += W) {
    const zz = Math.min(z1, z + W);
    let x = x0 + (Math.round(z * 7) % 3) * 1.7 - 3;
    while (x < x1) {
      const xa = Math.max(x0, x), xb = Math.min(x1, x + 5 + r() * 5);
      if (xb - xa > 0.05) b.add(new THREE.BoxGeometry(xb - xa - 0.015, 0.1, zz - z - 0.02), mat((xa + xb) / 2, -0.05, (z + zz) / 2), tones[Math.floor(r() * tones.length)], { rng: r, jitter: 0.025 });
      x = xb;
    }
  }
  b.add(new THREE.BoxGeometry(x1 - x0 + 0.1, 0.16, z1 - z0 + 0.1), mat(0, -0.17, (z0 + z1) / 2), PAL.tableDark, { rng: r, jitter: 0.03 });
  for (const sx of [x0 + 0.5, x1 - 0.5]) for (const sz of [z0 + 0.5, z1 - 0.5]) b.add(new THREE.BoxGeometry(0.35, 0.7, 0.35), mat(sx, -0.6, sz), PAL.tableDark, { rng: r, jitter: 0.04 });
  addPedestal(b);
  // два ряди яток за дальнім краєм
  const goods = [...GOODS, 'fern'];
  let gi = 0;
  for (const [z, xs, f] of [[-8.6, [-8.4, -5.1, -1.7, 1.7, 5.1, 8.4], 1], [-11.6, [-10, -6.6, -3.2, 0.2, 3.6, 7, 10.4], 1]]) {
    for (const x of xs) { addStall(b, x + (r() - 0.5) * 0.4, z + (r() - 0.5) * 0.4, 2.6 + r() * 0.5, goods[gi++ % goods.length], r, f); }
  }
  // вози
  addCart(b, -10.6, -6.0, 0.5, r);
  addCart(b, 10.8, -6.6, Math.PI - 0.4, r);
  // мішки й бочки між ятками
  const barrel = (x, z) => {
    b.add(new THREE.CylinderGeometry(0.34, 0.34, 0.85, 10), mat(x, -0.9 + 0.425, z), '#6e4523', { rng: r, jitter: 0.06 });
    b.add(new THREE.CylinderGeometry(0.42, 0.38, 0.12, 10), mat(x, -0.9 + 0.2, z), '#3a2a20', { rng: r, jitter: 0 });
    b.add(new THREE.CylinderGeometry(0.42, 0.42, 0.12, 10), mat(x, -0.9 + 0.65, z), '#3a2a20', { rng: r, jitter: 0 });
    b.add(new THREE.CylinderGeometry(0.42, 0.42, 0.03, 10), mat(x, -0.9 + 0.84, z), '#8a5a35', { rng: r, jitter: 0 });
  };
  for (const [x, z] of [[-9.6, -7.6], [-3.4, -7.5], [3.5, -7.4], [9.8, -8.0], [-12.3, -9.5], [12.2, -9.6]]) barrel(x, z);
  for (const [x, z] of [[-8.9, -7.3], [-0.2, -7.2], [6.8, -7.3], [-6.6, -10.1], [4.9, -10.0], [11.4, -7.9]]) {
    b.add(new THREE.IcosahedronGeometry(0.42, 0), mat(x, -0.9 + 0.32, z, r(), 1, 0.78, 0.95), mix('#c8b48a', '#a58c62', r()), { rng: r, jitter: 0.1 });
    b.add(new THREE.ConeGeometry(0.14, 0.2, 5), mat(x, -0.9 + 0.7, z), '#a58c62', { rng: r, jitter: 0.05 });
  }
  // тополі й церква силуетом
  for (let i = 0; i < 16; i++) {
    const x = -26 + i * 3.4 + (r() - 0.5) * 1.6, z = -17 - r() * 5;
    if (Math.abs(x + 6) < 3.5) continue;
    addPoplar(b, x, z, 7 + r() * 4, r);
  }
  addChurch(b, -6.5, -21, r);
  // стовпи й мотузка гірлянди
  for (const x of [GARLAND.x0, GARLAND.x1]) {
    b.add(new THREE.BoxGeometry(0.16, GARLAND.y + 1.2, 0.16), mat(x, (GARLAND.y + 1.2) / 2 - 0.9, GARLAND.z), '#4a2e1a', { rng: r, jitter: 0.05 });
  }
  const N = 28;
  for (let i = 0; i < N; i++) {
    const a = garlandPoint(i / N), c = garlandPoint((i + 1) / N);
    beam(b, a, c, 0.03, 0.03, '#2a1d17', r, 0);
  }
  const geo = b.geometry();
  // адитивні плями світла ліхтарів на землі й прилавках яток
  const sb = new Builder();
  for (let i = 0; i < GARLAND.n; i++) {
    const [x, , z] = garlandPoint((i + 0.5) / GARLAND.n);
    const g = new THREE.CircleGeometry(1.5 + (i % 3) * 0.3, 14);
    const pos = g.attributes.position, cs = [];
    for (let k = 0; k < pos.count; k++) {
      const d = Math.hypot(pos.getX(k), pos.getY(k)) / (1.5 + (i % 3) * 0.3);
      const a = (1 - d) * 0.22;
      const cc = col(PAL.lantern);
      cs.push(cc.r * a, cc.g * a, cc.b * a);
    }
    g.setAttribute('color', new THREE.Float32BufferAttribute(cs, 3));
    sb.add(g, mat(x, -0.88, z - 0.8, 0, 1, 1, 1, -Math.PI / 2), null, { jitter: 0 });
  }
  const spots = sb.geometry();
  return { geo, spots };
}
/** Небо — градієнт PAL.skyTop → PAL.skyLow на половині циліндра за ярмарком. */
export function skyGeo() {
  const g = new THREE.CylinderGeometry(60, 60, 50, 24, 8, true, Math.PI - 1.4, 2.8);
  g.applyMatrix4(mat(0, 18, 6));
  const p = g.attributes.position, cs = [];
  const top = col(PAL.skyTop), low = col(PAL.skyLow), mid = mix(PAL.skyLow, '#8a4a6a', 0.6);
  for (let i = 0; i < p.count; i++) {
    const k = Math.min(1, Math.max(0, (p.getY(i) + 3) / 30));
    const c = k < 0.25 ? low.clone().lerp(mid, k / 0.25) : mid.clone().lerp(top, Math.min(1, (k - 0.25) / 0.55));
    cs.push(c.r, c.g, c.b);
  }
  g.setAttribute('color', new THREE.Float32BufferAttribute(cs, 3));
  return g;
}
