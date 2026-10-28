// «Скарб Полуботка» — малюнок пласкої мапи (SVG-рядки) для board2d.js: три теми з концептів художника
// («Казкова ніч», «Дерев'яна настолка», «Ніч копачів»). Тут лише чисті функції «дані → розмітка»; стан, указівник і
// анімації — у board2d.js. Світові координати: центр клітинки (x, y) — (x·U, y·U), U = 100; поверхня — верхній край
// рядка y0. Усе, що поза полем (небо, боки, підошва), домальовується під будь-яке співвідношення сторін хоста.

import { BIT, DIRS, STEP, STRATA, PAL, SEAT_COLOR } from './consts.js';

export const U = 100;
export const INK = '#2a160b';

/** Теми: id → параметри. Порядок — як у перемикачі. */
export const THEME_LIST = [
  { id: 'fairy', name: 'Казкова ніч' },
  { id: 'wood', name: 'Дерев’яна настолка' },
  { id: 'night', name: 'Ніч копачів' },
];
export const DEFAULT_THEME = 'wood';

const BASE = {
  surf: 1.85, tw: 52, craneS: 0.88, moonX: 10.9, moonY: 0.32, moonR: 30, starC: '#fff8e0', moonGlow: '#fff6d8',
  thatch: '#d9b45a', wall: '#f4efe4', woodC: '#8a5a32', priz: '#b4532a', shutter: '#3c78d8', fenceC: '#8a6a46', fenceC2: '#a5835a',
  poleC: '#7a5232', poleC2: '#a5835a', bucket: '#7a5232', smokeC: '#c8c3d8', rootC: '#1c120a', boulder: '#8d8f94',
  rimOuter: 'rgba(0,0,0,.35)', seamC: 'rgba(0,0,0,.28)', seamW: 5, waveAmp: 16, blockInk: 'rgba(0,0,0,.35)',
  plank: ['#c58a4e', '#8a5a2e'], goalX: '#2a1408', goalX2: '#6b3a18', peekBg: '#2a2f3a', peekRing: '#f4c542', goalR: 8,
  hutX: -2.95, sunX: 2.3, hayX: 4.7, farX: 8.9, fenceA: -1.05, fenceB: -0.15, pawn: 'digger', pawnS: 1.3,
  starD: 2.4e-4, pebD: 1.7e-4, fogA: 1, vignette: 0,
};

export const THEMES = {
  // c1 — «Казкова ніч»: фіолетові сутінки, світлячки, хрестики-кутики сітки, кругле сяйво над мітками.
  fairy: { ...BASE, id: 'fairy', seed: 5, surf: 2.0, bg: '#140b08',
    sky: ['#120c33', '#2a1d5e', '#5b3478', '#b05a74', '#f09a64'], moonX: 10.9, moonY: 0.3, moonR: 34, starD: 2.7e-4, craneS: 0.9,
    hill1: '#3a2a66', hill2: '#26385a', grass: '#3f7a3a', grass2: '#5d9a44', leaf: '#2c5a3c', leaf2: '#3f7a4c',
    strata: [['#35200f', '#24140a'], ['#5e3620', '#4c2a17'], ['#8a522a', '#74421f'], ['#b4814a', '#9a6a38'], ['#5f5a70', '#48435c']],
    tunnel: '#1c0f0a', rim: '#4a2410', wood: '#c48a4e', lamp: '#ffc062', glowA: 0.7, glowR: 120, glowSA: 0.4, innerR: 62, floor: '#5a3018',
    grid: 'corners', gridC: 'rgba(255,236,200,.15)', waveAmp: 18, pebD: 1.9e-4,
    legal: { fill: 'rgba(255,211,90,.24)', stroke: '#ffd35a', w: 5, r: 14, glow: true, inner: 'rgba(255,240,190,.7)', altFill: 'rgba(255,233,168,.10)',
      altStroke: '#ffe9a8', altBadge: 'rgba(20,12,6,.75)', ghostT: 'rgba(255,214,120,.42)' },
    goalAura: true, auraC: '#ffe28a', plank: ['#d4975a', '#8a5424'], fog: '#140b08', vignette: 0.3, fireflies: true },
  // c2 — «Дерев'яна настолка»: теплі шари з чорнильним контуром, сітка лініями, фігурки-мипли.
  wood: { ...BASE, id: 'wood', seed: 3, surf: 1.8, bg: '#36241a',
    sky: ['#24344f', '#3c4c6e', '#8a6c80', '#d6a07a'], moonX: 10.9, moonY: 0.32, moonR: 30, starD: 1.1e-4, starC: '#fff4d6', craneS: 0.85,
    hill1: '#3c4866', hill2: '#3d5a48', grass: '#6a9440', grass2: '#86ad52', leaf: '#3d6a40', leaf2: '#5a8a50', thatch: '#e0bc66',
    strata: [['#45301f', '#36241a'], ['#8a5536', '#764629'], ['#b97c45', '#a56a37'], ['#dfb571', '#cca05c'], ['#8f9096', '#7a7b82']],
    tunnel: '#2e1e13', rim: INK, rimOuter: 'rgba(0,0,0,0)', wood: '#c08850', lamp: '#ffd27a', glowA: 0.5, glowR: 80, glowSA: 0.2, innerR: 55, floor: '#5e3c22',
    grid: 'lines', gridC: 'rgba(42,22,11,.2)', gridW: 2.2, waveAmp: 14, seamC: 'rgba(42,22,11,.55)', seamW: 3, pebbleInk: 'rgba(42,22,11,.5)', pebD: 1.7e-4, blockInk: INK,
    legal: { fill: 'rgba(255,226,110,.5)', stroke: '#ffd35a', w: 6, r: 10, glow: true, inner: INK, altFill: 'rgba(255,248,220,.22)',
      altStroke: '#fff4c8', altBadge: 'rgba(42,22,11,.8)', ghostT: 'rgba(255,240,190,.55)' },
    plank: ['#d09a5e', '#8e5a2c'], pawn: 'meeple', pawnS: 1.05, smokeC: '#e8e0d0', fog: '#2a160b', fogA: 0.45 },
  // c3 — «Ніч копачів»: майже чорна земля, ліхтарі розливають великі теплі плями, крапки сітки, кутики-приціли.
  night: { ...BASE, id: 'night', seed: 9, surf: 1.9, bg: '#07060a',
    sky: ['#03040a', '#0a0f22', '#1a1a33', '#3a2420', '#6a3a1c'], moonY: 0.32, moonR: 30, moonGlow: '#cfe0ff', starD: 3e-4, starC: '#dfe8ff', craneS: 0.9,
    hill1: '#0c0f1c', hill2: '#0b120f', grass: '#16241a', grass2: '#22382a', leaf: '#101a14', leaf2: '#18261e', thatch: '#5a4628', wall: '#5a5660',
    woodC: '#2a1c12', priz: '#3a1a10', shutter: '#1c2a44', fenceC: '#2a1e14', fenceC2: '#3a2a1c', poleC: '#2e2014', poleC2: '#3e2c1c', bucket: '#2e2014', smokeC: '#55607a',
    strata: [['#17100b', '#120c08'], ['#24170e', '#1e130b'], ['#33200f', '#2b1a0d'], ['#3e2a16', '#352312'], ['#1c1c22', '#141419']],
    tunnel: '#0a0604', rim: '#050302', rimOuter: 'rgba(0,0,0,.6)', wood: '#9a6a3a', lamp: '#ffb04a', glowA: 1, glowR: 200, glowSA: 0.6, innerR: 75, floor: '#3a2412',
    grid: 'dots', gridC: 'rgba(255,220,160,.12)', waveAmp: 14, seamC: 'rgba(0,0,0,.5)', seamW: 4, pebD: 1.45e-4, rootC: '#000', boulder: '#6d6f76', blockInk: 'rgba(0,0,0,.6)',
    legal: { fill: 'rgba(255,190,80,.14)', stroke: '#ffcf6b', w: 2, r: 4, glow: true, altFill: 'rgba(255,220,160,.05)', altStroke: 'rgba(255,226,170,.75)',
      altBadge: 'rgba(0,0,0,.7)', ghostT: 'rgba(255,200,110,.32)', brackets: '#ffe2a0' },
    goalAura: true, auraC: '#ffcf6b', plank: ['#5a3a1e', '#2a1a0c'], goalX: '#0e0804', goalX2: '#b8862a', peekBg: '#0e0d10', peekRing: '#e9b65a',
    sunX: 7.25, hayX: 8.7, farX: 10.9, moonX: 11.2, fog: '#07060a', vignette: 0.25, motes: true },
};
export const themeOf = (id) => THEMES[id] || THEMES[DEFAULT_THEME];

// ─── Утиліти ────────────────────────────────────────────────────────────────
export const r1 = (v) => Math.round(v * 10) / 10;
export function rng(seed) {
  let a = seed >>> 0;
  return function () { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
/** k < 0 — темніше, k > 0 — світліше. */
export function shade(hex, k) {
  const n = parseInt(hex.slice(1), 16), r = n >> 16, g = (n >> 8) & 255, b = n & 255;
  const f = (c) => Math.round(k < 0 ? c * (1 + k) : c + (255 - c) * k);
  return '#' + ((1 << 24) | (f(r) << 16) | (f(g) << 8) | f(b)).toString(16).slice(1);
}
const has = (m, d) => !!(m & BIT[d]);
/** Ліві/верхні краї «клітинки» в координатах художника (дробові x теж). */
const X = (x) => (x - 0.5) * U, Y = (y) => (y - 0.5) * U;

// ─── Визначення (градієнти, фільтри) — на тему ──────────────────────────────
export function defs(t, p) {
  const L = t.legal;
  let d = '';
  d += `<linearGradient id="${p}sky" x1="0" y1="0" x2="0" y2="1">${t.sky.map((c, i) => `<stop offset="${r1(i / (t.sky.length - 1))}" stop-color="${c}"/>`).join('')}</linearGradient>`;
  d += `<radialGradient id="${p}moonG"><stop offset="0" stop-color="${t.moonGlow}" stop-opacity=".55"/><stop offset=".35" stop-color="${t.moonGlow}" stop-opacity=".14"/><stop offset="1" stop-color="${t.moonGlow}" stop-opacity="0"/></radialGradient>`;
  d += `<radialGradient id="${p}moon" cx=".38" cy=".35"><stop offset="0" stop-color="#fffbe8"/><stop offset=".7" stop-color="#efe6c6"/><stop offset="1" stop-color="#c9bf98"/></radialGradient>`;
  d += `<radialGradient id="${p}glow"><stop offset="0" stop-color="${t.lamp}" stop-opacity="${t.glowA}"/><stop offset=".35" stop-color="${t.lamp}" stop-opacity="${r1(t.glowA * 0.33 * 100) / 100}"/><stop offset="1" stop-color="${t.lamp}" stop-opacity="0"/></radialGradient>`;
  d += `<radialGradient id="${p}glowS"><stop offset="0" stop-color="${t.lamp}" stop-opacity="${t.glowSA}"/><stop offset="1" stop-color="${t.lamp}" stop-opacity="0"/></radialGradient>`;
  d += `<radialGradient id="${p}gold"><stop offset="0" stop-color="#ffe28a" stop-opacity=".75"/><stop offset=".45" stop-color="#f2c14e" stop-opacity=".25"/><stop offset="1" stop-color="#f2c14e" stop-opacity="0"/></radialGradient>`;
  d += `<radialGradient id="${p}win"><stop offset="0" stop-color="#ffd77a" stop-opacity=".7"/><stop offset="1" stop-color="#ffb347" stop-opacity="0"/></radialGradient>`;
  d += `<linearGradient id="${p}thatch" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${t.thatch}"/><stop offset="1" stop-color="${shade(t.thatch, -0.35)}"/></linearGradient>`;
  d += `<linearGradient id="${p}wall" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${t.wall}"/><stop offset="1" stop-color="${shade(t.wall, -0.28)}"/></linearGradient>`;
  t.strata.forEach((s, i) => { d += `<linearGradient id="${p}st${i}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${s[0]}"/><stop offset="1" stop-color="${s[1]}"/></linearGradient>`; });
  d += `<radialGradient id="${p}coin" cx=".35" cy=".3" r=".8"><stop offset="0" stop-color="#fff6c4"/><stop offset=".45" stop-color="#f4c542"/><stop offset="1" stop-color="#b07b14"/></radialGradient>`;
  d += `<linearGradient id="${p}shaft" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#000" stop-opacity=".5"/><stop offset=".5" stop-color="#000" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".5"/></linearGradient>`;
  d += `<linearGradient id="${p}plank" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${t.plank[0]}"/><stop offset="1" stop-color="${t.plank[1]}"/></linearGradient>`;
  d += `<radialGradient id="${p}aura"><stop offset="0" stop-color="${t.auraC || '#ffe28a'}" stop-opacity=".35"/><stop offset="1" stop-color="${t.auraC || '#ffe28a'}" stop-opacity="0"/></radialGradient>`;
  d += `<filter id="${p}drop" x="-20%" y="-20%" width="140%" height="150%"><feDropShadow dx="0" dy="6" stdDeviation="5" flood-color="#000" flood-opacity=".5"/></filter>`;
  d += `<filter id="${p}lglow" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur in="SourceAlpha" stdDeviation="6" result="b"/><feFlood flood-color="${L.stroke}" flood-opacity=".9"/><feComposite in2="b" operator="in" result="g"/><feMerge><feMergeNode in="g"/><feMergeNode in="SourceGraphic"/></feMerge></filter>`;
  d += `<filter id="${p}rglow" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur in="SourceAlpha" stdDeviation="6" result="b"/><feFlood flood-color="${PAL.bad}" flood-opacity=".9"/><feComposite in2="b" operator="in" result="g"/><feMerge><feMergeNode in="g"/><feMergeNode in="SourceGraphic"/></feMerge></filter>`;
  if (t.fog) d += `<linearGradient id="${p}fogL" x1="0" x2="1"><stop offset="0" stop-color="${t.fog}" stop-opacity="${t.fogA}"/><stop offset="1" stop-color="${t.fog}" stop-opacity="0"/></linearGradient>`
    + `<linearGradient id="${p}fogR" x1="1" x2="0"><stop offset="0" stop-color="${t.fog}" stop-opacity="${t.fogA}"/><stop offset="1" stop-color="${t.fog}" stop-opacity="0"/></linearGradient>`;
  if (t.vignette) d += `<radialGradient id="${p}vig" cx=".5" cy=".55" r=".75"><stop offset=".55" stop-color="#000" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity="${t.vignette}"/></radialGradient>`;
  return d;
}

// ─── Тло: небо, село, шари ґрунту, фактура, сітка (статичне, під розмір вікна) ──
/** box — видима частина світу (viewBox), яку тло має покрити повністю. lite — менше дрібниць (слабкі пристрої). */
export function backdrop(t, p, G, box, lite) {
  const R = rng(t.seed);
  const left = Math.floor(box.x - 6), right = Math.ceil(box.x + box.w + 6), top = Math.floor(box.y - 6), bot = Math.ceil(box.y + box.h + 6);
  const W = right - left;
  const ground = (G.y0 - 0.5) * U;               // лінія землі
  const surfU = t.surf * U;
  const sx0 = G.start[0] * U;                     // вісь колодязя
  const k = lite ? 0.5 : 1;
  let s = '';

  // Небо, зорі, місяць, пагорби.
  s += `<rect x="${left}" y="${top}" width="${W}" height="${r1(ground + 40 - top)}" fill="url(#${p}sky)"/>`;
  const skyH = ground - top;
  const nStars = Math.min(220, Math.round(W * skyH * t.starD * k));
  let stars = '', crosses = '';
  for (let i = 0; i < nStars; i++) {
    const x = left + R() * W, y = top + R() * skyH * 0.8, big = R() < 0.12, r = big ? 3.2 : 1 + R() * 1.6;
    stars += `<circle cx="${r1(x)}" cy="${r1(y)}" r="${r1(r)}" opacity="${r1(0.35 + R() * 0.6)}"/>`;
    if (big) crosses += `M${r1(x - 8)} ${r1(y)}h16M${r1(x)} ${r1(y - 8)}v16`;
  }
  s += `<g fill="${t.starC}">${stars}</g>`;
  if (crosses) s += `<path d="${crosses}" stroke="${t.starC}" stroke-width="1.4" opacity=".6"/>`;
  const mx = X(t.moonX), my = ground - surfU * (1 - t.moonY), mr = t.moonR;
  s += `<circle cx="${mx}" cy="${r1(my)}" r="${mr * 6}" fill="url(#${p}moonG)"/><circle cx="${mx}" cy="${r1(my)}" r="${mr}" fill="url(#${p}moon)"/>`
    + `<circle cx="${r1(mx - mr * 0.3)}" cy="${r1(my - mr * 0.1)}" r="${r1(mr * 0.18)}" fill="#d8cfa8" opacity=".7"/><circle cx="${r1(mx + mr * 0.25)}" cy="${r1(my + mr * 0.3)}" r="${r1(mr * 0.12)}" fill="#d8cfa8" opacity=".6"/>`;
  const hill = (yb, amp, col, seed) => {
    const r = rng(seed); let d = `M${left} ${yb}`;
    for (let x = left; x <= right + 200; x += 200) d += ` Q${x + 100} ${r1(yb - amp * (0.4 + r()))} ${x + 200} ${r1(yb - amp * r() * 0.4)}`;
    return `<path d="${d} V${ground + 20} H${left}z" fill="${col}"/>`;
  };
  s += hill(ground - 34, 60, t.hill1, 11) + hill(ground - 10, 44, t.hill2, 5);

  // Село: хата, вишні, тин із глечиками, журавель, зруб, соняшники, копиця, далека хатка.
  const hx = X(t.hutX), hw = 190, hh = 78, hy = ground - 4;
  s += `<circle cx="${hx + hw * 0.5}" cy="${hy - 40}" r="150" fill="url(#${p}win)" opacity=".55"/>`
    + `<rect x="${hx}" y="${hy - hh}" width="${hw}" height="${hh}" rx="6" fill="url(#${p}wall)" stroke="${INK}" stroke-width="4"/>`
    + `<rect x="${hx}" y="${hy - 16}" width="${hw}" height="16" fill="${t.priz}" stroke="${INK}" stroke-width="3"/>`
    + `<path d="M${hx - 26} ${hy - hh + 6} Q${hx + hw / 2} ${hy - hh - 120} ${hx + hw + 26} ${hy - hh + 6} Q${hx + hw / 2} ${hy - hh - 10} ${hx - 26} ${hy - hh + 6}z" fill="url(#${p}thatch)" stroke="${INK}" stroke-width="4" stroke-linejoin="round"/>`;
  let straw = '';
  for (let i = 0; i < 9; i++) straw += `M${r1(hx - 10 + i * 26)} ${hy - hh + 2} q${6 - i} -26 ${12 - i * 2} -46`;
  s += `<path d="${straw}" stroke="${shade(t.thatch, -0.45)}" stroke-width="2.2" fill="none" opacity=".55"/>`;
  s += `<rect x="${hx + 100}" y="${hy - hh - 70}" width="18" height="36" fill="${shade(t.wall, -0.2)}" stroke="${INK}" stroke-width="3.5"/>`
    + `<path d="M${hx + 109} ${hy - hh - 76} q-16 -20 4 -38 q18 -18 0 -40" stroke="${t.smokeC}" stroke-width="9" fill="none" stroke-linecap="round" opacity=".35"/>`;
  for (const dx of [28, 124]) {
    s += `<rect x="${hx + dx}" y="${hy - hh + 22}" width="34" height="30" rx="3" fill="#ffd36a" stroke="${INK}" stroke-width="3.5"/>`
      + `<path d="M${hx + dx + 17} ${hy - hh + 22}v30M${hx + dx} ${hy - hh + 37}h34" stroke="${INK}" stroke-width="2.5"/>`
      + `<rect x="${hx + dx - 7}" y="${hy - hh + 20}" width="7" height="34" fill="${t.shutter}" stroke="${INK}" stroke-width="2.4"/><rect x="${hx + dx + 34}" y="${hy - hh + 20}" width="7" height="34" fill="${t.shutter}" stroke="${INK}" stroke-width="2.4"/>`;
  }
  s += `<rect x="${hx + 78}" y="${hy - hh + 22}" width="30" height="${hh - 22}" rx="3" fill="${shade(t.woodC, -0.1)}" stroke="${INK}" stroke-width="3.5"/>`;
  const tree = (x, sc) => {
    const c = (dx, dy) => `cx="${r1(x + dx * sc)}" cy="${r1(ground + dy * sc)}"`;
    return `<path d="M${x} ${ground} q-4 -40 4 -70" stroke="${INK}" stroke-width="${r1(14 * sc)}" fill="none" stroke-linecap="round"/><path d="M${x} ${ground} q-4 -40 4 -70" stroke="#5a3419" stroke-width="${r1(8 * sc)}" fill="none" stroke-linecap="round"/>`
      + `<g stroke="${INK}" stroke-width="4" fill="${t.leaf}">${[[0, -110, 46], [-34, -84, 34], [32, -86, 36], [-8, -70, 30]].map(([dx, dy, rr]) => `<circle ${c(dx, dy)} r="${r1(rr * sc)}"/>`).join('')}</g>`
      + `<g fill="${t.leaf2}">${[[-10, -122, 22], [22, -96, 16], [-36, -94, 14]].map(([dx, dy, rr]) => `<circle ${c(dx, dy)} r="${r1(rr * sc)}"/>`).join('')}</g>`
      + `<g fill="#e3122a" stroke="${INK}" stroke-width="1.5">${[[-20, -100], [14, -118], [30, -78], [-30, -74], [2, -84], [-6, -132]].map(([dx, dy]) => `<circle ${c(dx, dy)} r="${r1(5 * sc)}"/>`).join('')}</g>`;
  };
  s += tree(X(-3.6), 0.9);
  // Тин.
  const fx0 = X(t.fenceA), fx1 = X(t.fenceB);
  let posts = '', rails = '';
  for (let x = fx0; x <= fx1; x += 22) posts += `M${r1(x)} ${ground + 4}v-58`;
  for (let i = 0; i < 3; i++) rails += `M${fx0 - 10} ${ground - 14 - i * 14} Q${(fx0 + fx1) / 2} ${ground - 8 - i * 14} ${fx1 + 10} ${ground - 14 - i * 14}`;
  s += `<path d="${posts}" stroke="${INK}" stroke-width="8" stroke-linecap="round"/><path d="${posts}" stroke="${t.fenceC}" stroke-width="4" stroke-linecap="round"/>`
    + `<path d="${rails}" stroke="${t.fenceC2}" stroke-width="6" fill="none" opacity=".9"/>`;
  [fx0 + 22, fx0 + 66].forEach((x, i) => { s += `<g transform="translate(${r1(x - 13)} ${ground - 86}) scale(.21)"><path d="M46 22h36v10c14 6 24 22 24 42c0 22-14 40-42 40c-28 0-42-18-42-40c0-20 10-36 24-42z" fill="${i ? '#c5763a' : '#a8602c'}" stroke="${INK}" stroke-width="10"/><path d="M30 60 h68" stroke="#f6ead0" stroke-width="8"/></g>`; });
  // Журавель над шахтою.
  const pole = sx0 + 64;
  s += `<g transform="translate(${sx0} ${ground}) scale(${t.craneS}) translate(${-sx0} ${-ground})">`
    + `<path d="M${pole} ${ground + 2}V${ground - 150}" stroke="${INK}" stroke-width="15" stroke-linecap="round"/><path d="M${pole} ${ground + 2}V${ground - 150}" stroke="${t.poleC}" stroke-width="9" stroke-linecap="round"/>`
    + `<path d="M${pole - 10} ${ground - 150} l10 -14 l10 14" stroke="${INK}" stroke-width="5" fill="none"/>`
    + `<path d="M${sx0 - 16} ${ground - 200}L${pole + 120} ${ground - 92}" stroke="${INK}" stroke-width="11" stroke-linecap="round"/><path d="M${sx0 - 16} ${ground - 200}L${pole + 120} ${ground - 92}" stroke="${t.poleC2}" stroke-width="6" stroke-linecap="round"/>`
    + `<rect x="${pole + 104}" y="${ground - 104}" width="34" height="26" rx="4" fill="#6d6a66" stroke="${INK}" stroke-width="3.5"/>`
    + `<path d="M${sx0 - 14} ${ground - 198}V${ground - 66}" stroke="${INK}" stroke-width="2.5"/>`
    + `<path d="M${sx0 - 26} ${ground - 66} h24 l-4 22 h-16z" fill="${t.bucket}" stroke="${INK}" stroke-width="3.5" stroke-linejoin="round"/></g>`;
  s += [0, 1, 2].map((i) => `<rect x="${sx0 - 50}" y="${ground - 18 - i * 15}" width="100" height="16" rx="7" fill="${i % 2 ? '#8a5a32' : '#a06a3a'}" stroke="${INK}" stroke-width="3.5"/>`).join('')
    + `<path d="M${sx0 - 56} ${ground - 60} l56 -30 l56 30z" fill="${shade(t.thatch, -0.15)}" stroke="${INK}" stroke-width="3.5" stroke-linejoin="round"/>`;
  // Соняшники.
  const sun = (x, hgt, sc) => {
    let g = `<path d="M${x} ${ground} q6 -${hgt / 2} 0 -${hgt}" stroke="${INK}" stroke-width="7" fill="none"/><path d="M${x} ${ground} q6 -${hgt / 2} 0 -${hgt}" stroke="#4f7d2e" stroke-width="3.5" fill="none"/>`
      + `<path d="M${x + 3} ${r1(ground - hgt * 0.45)} q20 -14 30 0 q-14 8 -30 0z" fill="#4f7d2e" stroke="${INK}" stroke-width="2.5"/><path d="M${x + 2} ${r1(ground - hgt * 0.65)} q-20 -14 -30 0 q14 8 30 0z" fill="#4f7d2e" stroke="${INK}" stroke-width="2.5"/>`
      + `<g transform="translate(${x} ${ground - hgt}) scale(${sc})" stroke="${INK}" stroke-width="2">`;
    for (let i = 0; i < 14; i++) g += `<ellipse cy="-19" rx="6" ry="12" fill="${i % 2 ? '#f4c542' : '#f7b733'}" transform="rotate(${r1(i * 360 / 14)})"/>`;
    return g + `<circle r="13" fill="#5a3419" stroke-width="2.5"/></g>`;
  };
  const sfx = X(t.sunX);
  s += sun(sfx, 108, 1) + sun(sfx + 46, 132, 1.1) + sun(sfx + 92, 96, 0.9);
  const kx = X(t.hayX);
  s += `<path d="M${kx} ${ground} q6 -96 70 -104 q64 8 70 104z" fill="url(#${p}thatch)" stroke="${INK}" stroke-width="4" stroke-linejoin="round"/><path d="M${kx + 70} ${ground - 104} v-24" stroke="${INK}" stroke-width="5"/>`
    + `<path d="M${kx + 24} ${ground - 40} q46 -16 92 0 M${kx + 14} ${ground - 14} q56 -16 112 0" stroke="${shade(t.thatch, -0.45)}" stroke-width="3" fill="none" opacity=".6"/>`;
  const fh = X(t.farX);
  s += `<g opacity=".85"><rect x="${fh}" y="${ground - 44}" width="100" height="40" rx="4" fill="${shade(t.wall, -0.35)}" stroke="${INK}" stroke-width="3"/><path d="M${fh - 14} ${ground - 40} Q${fh + 50} ${ground - 108} ${fh + 114} ${ground - 40}z" fill="${shade(t.thatch, -0.4)}" stroke="${INK}" stroke-width="3"/><rect x="${fh + 38}" y="${ground - 34}" width="20" height="16" fill="#ffd36a" stroke="${INK}" stroke-width="2.4"/></g>`;
  s += tree(X(11.3), 0.7);
  // На широкому екрані — ще кілька дерев по краях села.
  for (const [tx, sc] of [[-6.4, 0.8], [-9.6, 0.65], [14.4, 0.85], [17.6, 0.7]]) if (X(tx) > left + 40 && X(tx) < right - 40) s += tree(X(tx), sc);
  // Трава.
  let tufts = '';
  for (let x = left; x < right; x += 14 + R() * 18) { const h = 8 + R() * 14; tufts += `M${r1(x)} ${ground - 4} l${r1(3 + R() * 3)} -${r1(h)} l${r1(3 + R() * 3)} ${r1(h)}`; }
  s += `<path d="M${left} ${ground - 6}H${right}V${ground + 14}H${left}z" fill="${t.grass}"/><path d="${tufts}" fill="${t.grass2}"/>`
    + `<path d="M${left} ${ground + 12}H${right}" stroke="${INK}" stroke-width="3" opacity=".5"/>`;

  // Шари ґрунту з хвилястими межами (останній — до самого низу).
  const wave = (yy, amp, seed) => { const r = rng(seed); let d = `M${left} ${yy}`; for (let x = left; x <= right + 160; x += 160) d += ` Q${x + 80} ${r1(yy + (r() - 0.5) * amp * 2)} ${x + 160} ${r1(yy + (r() - 0.5) * amp)}`; return d; };
  const bounds = STRATA.map((q) => q.y0);
  const topY = (i) => (i === 0 ? ground + 12 : Y(bounds[i]));
  for (let i = 0; i < 5; i++) {
    const y0 = topY(i), w = wave(y0, t.waveAmp, 30 + i);
    s += `<path d="${i === 0 ? `M${left} ${y0}H${right}` : w} V${bot} H${left}z" fill="url(#${p}st${i})"/>`;
    if (i > 0) s += `<path d="${w}" stroke="${t.seamC}" stroke-width="${t.seamW}" fill="none"/>`;
  }
  // Камінці.
  const layerAt = (py) => bounds.reduce((a, b, j) => (py >= Y(b) ? j : a), 0);
  const nPeb = Math.min(650, Math.round(W * (bot - ground - 20) * t.pebD * k));
  let peb = '';
  for (let i = 0; i < nPeb; i++) {
    const px = left + R() * W, py = ground + 20 + R() * (bot - ground - 20);
    const base = t.strata[layerAt(py)][1];
    const big = R() < 0.15, rx = big ? 8 + R() * 10 : 2 + R() * 5, ry = rx * (0.55 + R() * 0.3);
    peb += `<ellipse cx="${r1(px)}" cy="${r1(py)}" rx="${r1(rx)}" ry="${r1(ry)}" transform="rotate(${r1((R() - 0.5) * 40)} ${r1(px)} ${r1(py)})" fill="${R() < 0.55 ? shade(base, -0.28) : shade(base, 0.22)}" opacity="${big ? 0.8 : 0.55}"${big && t.pebbleInk ? ` stroke="${t.pebbleInk}" stroke-width="2"` : ''}/>`;
  }
  s += `<g>${peb}</g>`;
  // Корінці під травою.
  let roots = '';
  const nRoots = Math.round(W / 70);
  for (let i = 0; i < nRoots; i++) { const rx = left + R() * W, len = 30 + R() * 60; roots += `M${r1(rx)} ${ground + 12} q${r1((R() - 0.5) * 30)} ${r1(len * 0.5)} ${r1((R() - 0.5) * 24)} ${r1(len)}`; }
  s += `<path d="${roots}" stroke="${t.rootC}" stroke-width="2.6" fill="none" opacity=".55" stroke-linecap="round"/>`;
  // Кам'яна підошва — брили, ряд за рядом до низу.
  let blocks = '';
  for (let ry0 = Y(G.y1) + 10, row = 0; ry0 < Math.min(bot, Y(G.y1 + 1) + 340) && row < 12; ry0 += 62, row++) {
    for (let x = left - 20 - (row % 2) * 40; x < right;) {
      const w2 = 60 + R() * 70;
      blocks += `<path d="M${r1(x + 4)} ${r1(ry0 + 10 + R() * 14)} l${r1(w2 * 0.3)} -${r1(8 + R() * 6)} l${r1(w2 * 0.6)} 4 l${r1(w2 * 0.1)} ${r1(30 + R() * 20)} l-${r1(w2 * 0.2)} 40 l-${r1(w2 * 0.8)} 0z" fill="${shade(t.strata[4][0], (R() - 0.5) * 0.25)}" stroke="${t.blockInk}" stroke-width="3" stroke-linejoin="round"/>`;
      x += w2;
    }
  }
  s += `<g>${blocks}</g>`;
  // Знахідки: черепки, підкова, кістка, монета, глечик.
  for (const [kind, fx, fy] of [['shard', 5.6, 2.55], ['shoe', -2.6, -2.4], ['bone', 10.5, 3.3], ['coin', 11.3, -0.6], ['shard', -3.3, 3.2], ['pot', 6.4, -3.3]]) {
    const x = X(fx), y = Y(fy);
    if (kind === 'shard') s += `<path d="M${x} ${y} l26 -6 l8 14 l-18 10 z" fill="#b8642e" stroke="${INK}" stroke-width="2.5" opacity=".9"/><path d="M${x + 6} ${y + 2} l20 -4" stroke="#f6ead0" stroke-width="3" opacity=".8"/>`;
    if (kind === 'pot') s += `<g transform="translate(${x} ${y - 6}) scale(.32) rotate(-24 64 64)" opacity=".92"><path d="M46 22h36v10c14 6 24 22 24 42c0 22-14 40-42 40c-28 0-42-18-42-40c0-20 10-36 24-42z" fill="#a8602c" stroke="${INK}" stroke-width="7"/><path d="M30 56 h68" stroke="#f6ead0" stroke-width="7"/><path d="M40 70 l14 10 l-6 14" stroke="${INK}" stroke-width="5" fill="none"/></g>`;
    if (kind === 'shoe') s += `<path d="M${x} ${y} a16 16 0 1 1 28 0" stroke="${INK}" stroke-width="11" fill="none" stroke-linecap="round"/><path d="M${x} ${y} a16 16 0 1 1 28 0" stroke="#8d8f94" stroke-width="6" fill="none" stroke-linecap="round"/>`;
    if (kind === 'bone') s += `<g transform="rotate(-18 ${x} ${y})" fill="#e8dfc8" stroke="${INK}" stroke-width="2.5"><rect x="${x}" y="${y - 4}" width="40" height="8"/>${[[0, -5], [0, 5], [40, -5], [40, 5]].map(([a, b]) => `<circle cx="${x + a}" cy="${y + b}" r="6"/>`).join('')}<rect x="${x + 1}" y="${y - 3}" width="38" height="6" stroke="none"/></g>`;
    if (kind === 'coin') s += `<g transform="translate(${x} ${y}) scale(.22)">${coinIcon(p)}</g>`;
  }
  // Сітка поля.
  const gx0 = X(G.x0), gx1 = X(G.x1 + 1);
  let grid = '';
  if (t.grid === 'corners') {
    for (let x = G.x0; x <= G.x1 + 1; x++) for (let y = G.y0 + 1; y <= G.y1 + 1; y++) grid += `M${X(x) - 7} ${Y(y)}h14M${X(x)} ${Y(y) - 7}v14`;
    s += `<path d="${grid}" stroke="${t.gridC}" stroke-width="2.4" stroke-linecap="round"/>`;
  } else if (t.grid === 'lines') {
    for (let x = G.x0; x <= G.x1 + 1; x++) grid += `M${X(x)} ${Y(G.y0) + 14}V${Y(G.y1 + 1)}`;
    for (let y = G.y0 + 1; y <= G.y1 + 1; y++) grid += `M${gx0} ${Y(y)}H${gx1}`;
    s += `<path d="${grid}" stroke="${t.gridC}" stroke-width="${t.gridW || 2}"/>`;
  } else if (t.grid === 'dots') {
    for (let x = G.x0; x <= G.x1 + 1; x++) for (let y = G.y0 + 1; y <= G.y1 + 1; y++) grid += `M${X(x)} ${Y(y)}h0.01`;
    s += `<path d="${grid}" stroke="${t.gridC}" stroke-width="6.4" stroke-linecap="round"/>`;
  }
  // Бічні тумани: поза полем земля тоне в темряві.
  if (t.fog) {
    if (gx0 - left > 30) s += `<rect x="${left}" y="${ground}" width="${gx0 - left}" height="${bot - ground}" fill="url(#${p}fogL)"/>`;
    if (right - gx1 > 30) s += `<rect x="${gx1}" y="${ground}" width="${right - gx1}" height="${bot - ground}" fill="url(#${p}fogR)"/>`;
  }
  // Глибше за поле (високий хост) — підошва поволі тоне в темряві, щоб не муляла.
  const gb = Y(G.y1 + 1) + 30;
  if (bot - gb > 60) {
    const deep = t.fog || t.bg;
    s += `<defs><linearGradient id="${p}deep" gradientUnits="userSpaceOnUse" x1="0" y1="${gb}" x2="0" y2="${gb + 260}"><stop offset="0" stop-color="${deep}" stop-opacity="0"/><stop offset="1" stop-color="${deep}" stop-opacity=".88"/></linearGradient></defs>`
      + `<rect x="${left}" y="${gb}" width="${W}" height="${bot - gb}" fill="url(#${p}deep)"/>`;
  }
  return s;
}

/** Верхній декор (над фігурками, під підсвіткою): світлячки, порошинки в світлі, віньєтка. */
export function deco(t, p, G, box, lite) {
  const ground = (G.y0 - 0.5) * U;
  let s = '';
  if (t.fireflies && !lite) {
    const R = rng(77);
    let a = '', b = '';
    for (let i = 0; i < 22; i++) { const x = r1(X(-1.5 + R() * 13)), y = r1(ground - 30 - R() * 120); a += `<circle cx="${x}" cy="${y}" r="${r1(2 + R() * 2.5)}" opacity="${r1(0.5 + R() * 0.5)}"/>`; b += `<circle cx="${x}" cy="${y}" r="10"/>`; }
    s += `<g fill="#fff3a0">${a}<g opacity=".12">${b}</g></g>`;
  }
  if (t.motes && !lite) {
    const R = rng(13);
    let a = '';
    for (let i = 0; i < 70; i++) a += `<circle cx="${r1(X(-1.5 + R() * 9))}" cy="${r1(Y(-1.6 + R() * 4.2))}" r="${r1(1 + R() * 2)}" opacity="${r1(0.15 + R() * 0.5)}"/>`;
    s += `<g fill="#ffe6b0">${a}</g>`;
  }
  if (t.vignette) s += `<rect x="${r1(box.x - 6)}" y="${r1(box.y - 6)}" width="${r1(box.w + 12)}" height="${r1(box.h + 12)}" fill="url(#${p}vig)"/>`;
  return s;
}

// ─── Ходи: один спільний вигризений тунель + кріплення, ліхтарі, валуни ─────
function cellPath(x, y, m, c) {
  const cx = x * U, cy = y * U, e = U / 2 + 3;
  let d = '';
  for (const dir of DIRS) {
    if (!has(m, dir)) continue;
    const [dx, dy] = STEP[dir];
    if (c) d += `M${cx} ${cy}L${cx + dx * e} ${cy + dy * e}`;
    else d += `M${cx + dx * e} ${cy + dy * e}L${cx + dx * 22} ${cy + dy * 22}`;
  }
  return d;
}
function beam(t, x, y, dir, k) {
  const cx = x * U, cy = y * U, off = U / 2 - k, TW = t.tw;
  if (dir === 'E' || dir === 'W') {
    const bx = cx + (dir === 'E' ? off : -off);
    return `<rect x="${bx - 5}" y="${cy - TW / 2 - 5}" width="10" height="${TW + 10}" rx="2.5" fill="${t.wood}" stroke="${INK}" stroke-width="2.6"/><rect x="${bx - 9}" y="${cy - TW / 2 - 8}" width="18" height="7" rx="2" fill="${shade(t.wood, -0.15)}" stroke="${INK}" stroke-width="2.2"/>`;
  }
  const by = cy + (dir === 'S' ? off : -off);
  return `<rect y="${by - 5}" x="${cx - TW / 2 - 5}" height="10" width="${TW + 10}" rx="2.5" fill="${t.wood}" stroke="${INK}" stroke-width="2.6"/>`;
}
export function boulder(t, cx, cy) {
  return `<path d="M${cx - 26} ${cy + 14} q-6 -26 16 -32 q26 -6 34 14 q6 18 -10 24 q-22 8 -40 -6z" fill="${t.boulder}" stroke="${INK}" stroke-width="3.5"/><path d="M${cx - 12} ${cy - 10} q10 -6 20 0" stroke="#d4d6da" stroke-width="4" fill="none" opacity=".7" stroke-linecap="round"/><path d="M${cx + 4} ${cy + 2} l6 6 l-4 6" stroke="${INK}" stroke-width="2" fill="none" opacity=".6"/>`;
}
function lamp(t, x, y, on) {
  const cx = x * U, cy = y * U - t.tw / 2;
  return `<path d="M${cx} ${cy - 2}v8" stroke="${INK}" stroke-width="2.5"/><path d="M${cx - 7} ${cy + 6}h14l-2 4h-10z" fill="#5a3419" stroke="${INK}" stroke-width="2"/>`
    + `<rect x="${cx - 7}" y="${cy + 10}" width="14" height="16" rx="3" fill="${on ? '#fff1b8' : '#6a6050'}" stroke="${INK}" stroke-width="2.4"/>`
    + (on ? `<path d="M${cx} ${cy + 13}q4 5 0 10q-4 -5 0 -10z" fill="#ff9a2e"/>` : '')
    + `<path d="M${cx - 8} ${cy + 26}h16" stroke="${INK}" stroke-width="3" stroke-linecap="round"/>`;
}
function rubble(t, x, y) {
  const cx = x * U, cy = y * U, R = rng(99 + x * 7 + y * 131);
  let s = '';
  for (let i = 0; i < 9; i++) {
    const a = cx - 34 + R() * 68, b = cy + 6 + R() * 30, z = 7 + R() * 9;
    s += `<path d="M${r1(a - z)} ${r1(b)} l${r1(z * 0.6)} -${r1(z)} l${r1(z * 1.1)} ${r1(z * 0.2)} l${r1(z * 0.4)} ${r1(z * 0.8)}z" fill="${shade(t.boulder, (R() - 0.5) * 0.3)}" stroke="${INK}" stroke-width="2"/>`;
  }
  return `<g opacity=".9">${s}</g>`;
}

/**
 * Усі ходи одним шаром. cards: [{x, y, s, m, c}] (з колодязем), opened: відкриті мітки [{x, y, m, c, kind}],
 * linked: Set('x,y'), rubbleAt: ['x,y']. Повертає розмітку; кожна карта — <g data-c="x,y"> (для «появи»).
 */
export function tunnels(t, p, G, cards, opened, linked, rubbleAt) {
  const TW = t.tw, ground = (G.y0 - 0.5) * U;
  const [sx, sy] = G.start;
  const wcx = sx * U, wcy = sy * U;
  const shTop = ground + 10, shBot = (sy - 0.5) * U + 20;
  let d = '';
  for (const b of cards) d += cellPath(b.x, b.y, b.m, b.c);
  for (const g of opened) if (g.m) d += cellPath(g.x, g.y, g.m, true);
  const mx = X(G.x0 - 1), my = ground - U, mw = (G.x1 - G.x0 + 3) * U, mh = (G.y1 - G.y0 + 3) * U;
  let s = `<defs><mask id="${p}tm" maskUnits="userSpaceOnUse" x="${mx}" y="${my}" width="${mw}" height="${mh}"><rect x="${mx}" y="${my}" width="${mw}" height="${mh}" fill="#000"/>`
    + (d ? `<path d="${d}" stroke="#fff" stroke-width="${TW}" stroke-linecap="round" fill="none"/>` : '')
    + `<circle cx="${wcx}" cy="${wcy}" r="${r1(TW * 0.9)}" fill="#fff"/><rect x="${wcx - 20}" y="${shTop}" width="40" height="${shBot - shTop}" fill="#fff"/></mask></defs>`;
  // Ліхтарі: колодязь завжди світить; наскрізні карти — яскраво в linked, тьмяно поза ним.
  const lamps = [];
  for (const b of cards) if (b.c && b.s !== 'start') lamps.push([b.x, b.y, linked.has(b.x + ',' + b.y)]);
  let soil = `<circle cx="${wcx}" cy="${wcy}" r="${t.glowR * 1.4}" fill="url(#${p}glowS)"/>`;
  for (const [x, y, on] of lamps) soil += `<circle cx="${x * U}" cy="${y * U - TW / 2 + 18}" r="${t.glowR}" fill="url(#${p}glowS)" opacity="${on ? 1 : 0.2}"/>`;
  s += `<g style="mix-blend-mode:screen">${soil}</g>`;
  // Обідок ходу.
  if (d) {
    s += `<path d="${d}" stroke="${t.rimOuter}" stroke-width="${TW + 18}" stroke-linecap="round" fill="none"/>`;
    s += `<path d="${d}" stroke="${t.rim}" stroke-width="${TW + 8}" stroke-linecap="round" fill="none"/>`;
  }
  s += `<circle cx="${wcx}" cy="${wcy}" r="${r1(TW * 0.9 + 4)}" fill="${t.rim}"/><rect x="${wcx - 24}" y="${shTop}" width="48" height="${shBot - shTop}" fill="${t.rim}"/>`;
  // Нутро: темрява, світліша підлога, світло ліхтарів — усе в межах вигризеного.
  let floor = '';
  const fh = 9;
  for (const b of cards) {
    const cx = b.x * U, cy = b.y * U, fy = cy + TW / 2 - fh;
    if (b.c && (has(b.m, 'E') || has(b.m, 'W'))) {
      const l = has(b.m, 'W') ? X(b.x) - 3 : cx - TW / 2, r = has(b.m, 'E') ? X(b.x) + U + 3 : cx + TW / 2;
      floor += `<rect x="${l}" y="${fy}" width="${r - l}" height="${TW}"/>`;
    } else if (!b.c) {
      if (has(b.m, 'W')) floor += `<rect x="${X(b.x) - 3}" y="${fy}" width="31" height="${TW}"/>`;
      if (has(b.m, 'E')) floor += `<rect x="${X(b.x) + U - 28}" y="${fy}" width="31" height="${TW}"/>`;
    }
  }
  let inner = `<circle cx="${wcx}" cy="${wcy - 4}" r="${t.innerR * 2}" fill="url(#${p}glow)"/>`;
  for (const [x, y, on] of lamps) inner += `<circle cx="${x * U}" cy="${y * U - 4}" r="${on ? t.innerR * 2 : 70}" fill="url(#${p}glow)" opacity="${on ? 1 : 0.3}"/>`;
  for (const g of opened) if (g.kind === 'gold') inner += `<circle cx="${g.x * U}" cy="${g.y * U}" r="110" fill="url(#${p}gold)"/>`;
  s += `<g mask="url(#${p}tm)"><rect x="${mx}" y="${my}" width="${mw}" height="${mh}" fill="${t.tunnel}"/><g fill="${t.floor}">${floor}</g>${inner}</g>`;
  // Шахта: тінь, драбина, мотузка.
  let sh = `<rect x="${wcx - 20}" y="${shTop}" width="40" height="${shBot - shTop}" fill="url(#${p}shaft)"/>`;
  for (let y = shTop + 6; y < shBot - 30; y += 34) sh += `<rect x="${wcx - 28}" y="${y}" width="56" height="12" rx="5" fill="${t.wood}" stroke="${INK}" stroke-width="2.6"/>`;
  let rungs = '';
  for (let y = shTop + 22; y < shBot - 16; y += 17) rungs += `M${wcx - 10} ${y}h20`;
  sh += `<path d="M${wcx - 10} ${shTop}V${shBot - 10}M${wcx + 10} ${shTop}V${shBot - 10}${rungs}" stroke="${shade(t.wood, 0.15)}" stroke-width="3.5"/>`;
  sh += `<path d="M${wcx - 14} ${ground - 44}V${wcy - 20}" stroke="#c9a26a" stroke-width="2" opacity=".8"/>`;
  s += sh;
  // Кам'яне кільце колодязя.
  s += `<circle cx="${wcx}" cy="${wcy}" r="${r1(TW * 0.9)}" fill="none" stroke="${t.wellRing || '#8d8f94'}" stroke-width="9" stroke-dasharray="16 6"/><circle cx="${wcx}" cy="${wcy}" r="${r1(TW * 0.9)}" fill="none" stroke="${INK}" stroke-width="2" opacity=".5"/>`;
  // Кожна карта: кріплення, валун (глуха), ліхтар.
  for (const b of cards) {
    if (b.s === 'start') continue;
    let g = '';
    for (const dir of DIRS) if (has(b.m, dir)) g += beam(t, b.x, b.y, dir, b.c ? 12 : 30);
    if (!b.c) g += boulder(t, b.x * U, b.y * U);
    else g += lamp(t, b.x, b.y, linked.has(b.x + ',' + b.y));
    s += `<g data-c="${b.x},${b.y}">${g}</g>`;
  }
  for (const k of rubbleAt) { const [x, y] = k.split(',').map(Number); s += rubble(t, x, y); }
  return s;
}

// ─── Мітки ──────────────────────────────────────────────────────────────────
export function coinIcon(p) {
  return `<ellipse cx="50" cy="56" rx="40" ry="38" fill="#9a6512"/><circle cx="50" cy="50" r="40" fill="url(#${p}coin)" stroke="#6b4208" stroke-width="5"/>`
    + `<circle cx="50" cy="50" r="29" fill="none" stroke="#fff3c0" stroke-width="3" opacity=".55"/><path d="M50 30 l5 12 l13 1 l-10 8 l4 13 l-12 -7 l-12 7 l4 -13 l-10 -8 l13 -1z" fill="#fff3c0" opacity=".8"/>`;
}
const stoneIcon = '<path d="M-11 6 q-2 -12 10 -14 q12 0 12 10 q0 8 -10 8 q-8 0 -12 -4z" fill="#9a9ca2" stroke="' + INK + '" stroke-width="2.4"/><path d="M-4 -4 q5 -3 9 0" stroke="#dadcdf" stroke-width="2.4" fill="none"/>';
function chest(p, cx, cy, sc) {
  const coins = [[-16, -20], [-4, -24], [9, -21], [19, -16], [-22, -12], [3, -14], [14, -10]].map(([a, b]) => `<circle cx="${a}" cy="${b}" r="8" fill="url(#${p}coin)" stroke="#6b4208" stroke-width="2"/>`).join('');
  return `<g transform="translate(${cx} ${cy}) scale(${sc})">`
    + `<path d="M-30 -14 L-27 -46 Q0 -58 27 -46 L30 -14z" fill="#5e3216" stroke="${INK}" stroke-width="3.5" stroke-linejoin="round"/><path d="M-27 -40 Q0 -52 27 -40" stroke="#c9a24a" stroke-width="4" fill="none"/>`
    + coins
    + `<rect x="-33" y="-16" width="66" height="38" rx="5" fill="#8a5226" stroke="${INK}" stroke-width="3.5"/>`
    + `<path d="M-33 -4H33M-21 -16V22M21 -16V22" stroke="#c9a24a" stroke-width="5"/><path d="M-33 -4H33" stroke="${INK}" stroke-width="1.2" opacity=".5"/>`
    + `<rect x="-7" y="-9" width="14" height="14" rx="2.5" fill="#f2c14e" stroke="${INK}" stroke-width="2.4"/><circle cx="0" cy="-3" r="2.2" fill="${INK}"/></g>`;
}
function stones(t, cx, cy, sc) {
  let s = `<g transform="translate(${cx} ${cy}) scale(${sc})">`;
  for (const [a, b, z] of [[-18, 12, 0.7], [16, 14, 0.75], [0, -4, 0.85], [-22, -8, 0.5], [22, -6, 0.5]]) s += `<g transform="translate(${a} ${b}) scale(${z})">${boulder(t, 0, 0)}</g>`;
  return s + '</g>';
}
/** Одна мітка: закрита дошка (з моїм значком «бачив»), або те, що під нею. */
export function goal(t, p, gl, peek) {
  const cx = gl.x * U, cy = gl.y * U;
  if (gl.open) {
    if (gl.kind === 'gold') return `<circle cx="${cx}" cy="${cy}" r="90" fill="url(#${p}gold)"/>` + chest(p, cx, cy + 8, 1.15);
    return stones(t, cx, cy + 4, 0.95);
  }
  const gx = X(gl.x) + 8, gy = Y(gl.y) + 8, gw = U - 16;
  let s = t.goalAura ? `<circle cx="${cx}" cy="${cy}" r="80" fill="url(#${p}aura)"/>` : '';
  s += `<g filter="url(#${p}drop)"><rect x="${gx}" y="${gy}" width="${gw}" height="${gw}" rx="${t.goalR}" fill="url(#${p}plank)" stroke="${INK}" stroke-width="4"/>`
    + `<path d="M${gx + 4} ${r1(gy + gw * 0.36)}H${gx + gw - 4}M${gx + 4} ${r1(gy + gw * 0.7)}H${gx + gw - 4}" stroke="${INK}" stroke-width="2" opacity=".35"/>`;
  if (gl.kind) {
    // Кінець раунду: видно, що там було, хоч мітку й не відкопали.
    s += `<rect x="${gx}" y="${gy}" width="${gw}" height="${gw}" rx="${t.goalR}" fill="rgba(0,0,0,.4)"/>`;
    s += gl.kind === 'gold' ? chest(p, cx, cy + 10, 0.85) : stones(t, cx, cy + 2, 0.7);
  } else {
    const xd = `M${gx + 24} ${gy + 24}L${gx + gw - 24} ${gy + gw - 24}M${gx + gw - 24} ${gy + 24}L${gx + 24} ${gy + gw - 24}`;
    s += `<path d="${xd}" stroke="${t.goalX}" stroke-width="10" stroke-linecap="round"/><path d="${xd}" stroke="${t.goalX2}" stroke-width="4" stroke-linecap="round" opacity=".8"/>`;
  }
  s += [[10, 10], [gw - 10, 10], [10, gw - 10], [gw - 10, gw - 10]].map(([a, b]) => `<circle cx="${gx + a}" cy="${gy + b}" r="3.6" fill="#c9cbd0" stroke="${INK}" stroke-width="1.6"/>`).join('') + '</g>';
  if (peek) {
    s += `<g transform="translate(${gx + gw - 18} ${gy - 14})"><circle r="19" fill="${t.peekBg}" stroke="${t.peekRing}" stroke-width="3.5"/>`
      + (peek === 'gold' ? `<g transform="translate(-13 -13) scale(.26)">${coinIcon(p)}</g>` : stoneIcon)
      + `<title>${peek === 'gold' ? 'Ти бачив: тут скарб' : 'Ти бачив: тут каміння'}</title></g>`;
  }
  return s;
}

// ─── Фігурки ────────────────────────────────────────────────────────────────
/** Копачик (бриль, кирка): x, y — ноги. sit — зламаний інструмент: сидить, кирка поруч, «!». */
function digger(x, y, sc, color, sit) {
  const I = INK;
  return `<g transform="translate(${r1(x)} ${r1(y)}) scale(${sc})"><ellipse rx="16" ry="4" fill="#000" opacity=".35"/>`
    + (sit
      ? `<path d="M-10 0 q0 -16 10 -18 q10 2 10 18z" fill="${color}" stroke="${I}" stroke-width="2.6"/><circle cy="-24" r="7" fill="#f2c39a" stroke="${I}" stroke-width="2.4"/><path d="M-11 -27 q11 -5 22 0 q-3 3 -11 3 q-8 0 -11 -3z" fill="#e2b85c" stroke="${I}" stroke-width="2"/>`
        + `<g transform="translate(16 -2) rotate(70)"><path d="M0 0 v-18" stroke="${I}" stroke-width="4" stroke-linecap="round"/><path d="M0 0 v-8" stroke="#c98a4b" stroke-width="2"/><path d="M-6 -16 q6 -6 12 0" stroke="${I}" stroke-width="3" fill="none"/></g>`
        + `<circle cy="-42" r="8" fill="${PAL.bad}" stroke="${I}" stroke-width="2"/><path d="M0 -47v6M0 -38.5v.5" stroke="#fff" stroke-width="2.6" stroke-linecap="round"/>`
      : `<path d="M-6 0 v-10 M6 0 v-10" stroke="${I}" stroke-width="4.5" stroke-linecap="round"/><path d="M-10 -9 q0 -18 10 -20 q10 2 10 20z" fill="${color}" stroke="${I}" stroke-width="2.6"/>`
        + `<circle cy="-35" r="7.5" fill="#f2c39a" stroke="${I}" stroke-width="2.4"/><path d="M-12 -38 q12 -5 24 0 q-3 3 -12 3 q-9 0 -12 -3z" fill="#e2b85c" stroke="${I}" stroke-width="2"/><path d="M-5 -40 q5 -8 10 0" fill="#efcd78" stroke="${I}" stroke-width="2"/>`
        + `<g transform="translate(9 -24) rotate(-30)"><path d="M0 0 v-20" stroke="${I}" stroke-width="4" stroke-linecap="round"/><path d="M0 0 v-20" stroke="#c98a4b" stroke-width="2" stroke-linecap="round"/><path d="M-8 -18 q8 -7 16 0" stroke="${I}" stroke-width="3.2" fill="none" stroke-linecap="round"/></g>`)
    + '</g>';
}
/** Мипл із настолки. sit — лежить, над ним червоний ✕. */
function meeple(x, y, sc, color, sit) {
  const d = 'M-15 0 L-9 -15 L-19 -21 Q-23 -31 -12 -31 L-6 -29 Q-11 -46 0 -47 Q11 -46 6 -29 L12 -31 Q23 -31 19 -21 L9 -15 L15 0 Q0 3 -15 0Z';
  return `<g transform="translate(${r1(x)} ${r1(y)}) scale(${sc})${sit ? ' translate(0 -2) rotate(-90 0 -8) translate(-6 6)' : ''}">`
    + `<ellipse cy="1" rx="18" ry="4" fill="#000" opacity=".35"${sit ? ' transform="rotate(90 0 -8)"' : ''}/>`
    + `<path d="${d}" fill="${color}" stroke="${INK}" stroke-width="3" stroke-linejoin="round"/>`
    + `<path d="M-4 -42 Q2 -45 4 -38" stroke="#fff" stroke-width="3" fill="none" opacity=".55" stroke-linecap="round"/><path d="M-13 -26 L-7 -25" stroke="#fff" stroke-width="2.4" opacity=".4" stroke-linecap="round"/></g>`
    + (sit ? `<g transform="translate(${r1(x + 2)} ${r1(y - 34 * sc)})"><circle r="11" fill="#b3261e" stroke="${INK}" stroke-width="2.4"/><path d="M-4 -4 l8 8 M4 -4 l-8 8" stroke="#fff" stroke-width="3" stroke-linecap="round"/></g>` : '');
}
export function figure(t, x, y, sc, colorIdx, sit) {
  const col = SEAT_COLOR[colorIdx] || '#9aa3ad';
  return t.pawn === 'meeple' ? meeple(x, y, t.pawnS * sc, col, sit) : digger(x, y, t.pawnS * sc, col, sit);
}

// ─── Підсвітка й привид ─────────────────────────────────────────────────────
const rotIcon = (col) => `<path d="M-7 2 a8 8 0 1 0 2 -8" stroke="${col}" stroke-width="3.2" fill="none" stroke-linecap="round"/><path d="M-9 -10 l4 5 l-6 1z" fill="${col}"/>`;
/** cls: '' | 'alt' | 'rock' | 'goal'. Обгортка <g class="s2-leg …" data-t> — у board2d.js. */
export function legalCell(t, p, x, y, cls, lite) {
  const L = t.legal;
  const glow = lite ? '' : ` filter="url(#${p}${cls === 'rock' ? 'r' : 'l'}glow)"`;
  if (cls === 'alt') {
    const gx = X(x) + 10, gy = Y(y) + 10, gw = U - 20;
    return `<rect class="pl" x="${gx}" y="${gy}" width="${gw}" height="${gw}" rx="${L.r}" fill="${L.altFill}" stroke="${L.altStroke}" stroke-width="3.5" stroke-dasharray="12 8"/>`
      + `<g transform="translate(${gx + gw - 16} ${gy + 16})"><circle r="15" fill="${L.altBadge}"/>${rotIcon(L.altStroke)}</g>`;
  }
  const gx = X(x) + 6, gy = Y(y) + 6, gw = U - 12;
  if (cls === 'rock') {
    return `<rect x="${gx}" y="${gy}" width="${gw}" height="${gw}" rx="${L.r}" fill="rgba(214,69,65,.16)" stroke="${PAL.bad}" stroke-width="4"${glow}/>`
      + `<rect class="pl" x="${gx + 7}" y="${gy + 7}" width="${gw - 14}" height="${gw - 14}" rx="${Math.max(2, L.r - 5)}" fill="none" stroke="#ffb0a8" stroke-width="2.5" stroke-dasharray="10 6"/>`;
  }
  const w = cls === 'goal' ? L.w + 1 : L.w;
  let s = `<rect x="${gx}" y="${gy}" width="${gw}" height="${gw}" rx="${L.r}" fill="${L.fill}" stroke="${L.stroke}" stroke-width="${w}"${glow}/>`;
  s += `<rect class="pl" x="${gx + 9}" y="${gy + 9}" width="${gw - 18}" height="${gw - 18}" rx="${Math.max(2, L.r - 6)}" fill="none" stroke="${L.inner || L.stroke}" stroke-width="2" stroke-dasharray="6 6"/>`;
  if (L.brackets) {
    const a = X(x) + 8, b = Y(y) + 8, z = U - 16, k = 18;
    s += `<path d="M${a} ${b + k}V${b}H${a + k}M${a + z - k} ${b}H${a + z}V${b + k}M${a + z} ${b + z - k}V${b + z}H${a + z - k}M${a + k} ${b + z}H${a}V${b + z - k}" stroke="${L.brackets}" stroke-width="5" fill="none" stroke-linecap="round"/>`;
  }
  return s;
}
/** Привид вибраної карти на клітинці (m, c — уже повернута маска). */
export function ghost(t, x, y, m, c) {
  const L = t.legal, cx = x * U, cy = y * U, e = U / 2;
  let d = '';
  for (const dir of DIRS) {
    if (!has(m, dir)) continue;
    const [dx, dy] = STEP[dir];
    d += c ? `M${cx} ${cy}L${cx + dx * e} ${cy + dy * e}` : `M${cx + dx * e} ${cy + dy * e}L${cx + dx * 24} ${cy + dy * 24}`;
  }
  let s = `<path d="${d}" stroke="${L.ghostT}" stroke-width="${t.tw}" stroke-linecap="round" fill="none"/><path d="${d}" stroke="${L.stroke}" stroke-width="3" stroke-dasharray="8 6" fill="none"/>`;
  s += c ? `<circle cx="${cx}" cy="${cy - 18}" r="6" fill="#fff1b8" stroke="${INK}" stroke-width="2"/>` : `<g opacity=".8">${boulder(t, cx, cy)}</g>`;
  return s;
}
