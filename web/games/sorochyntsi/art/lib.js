// «Сорочинський ярмарок» — «декорації» для арту крамниць (опитування 09.11: сценки кодом у стилі значків icons.js).
// Кожна функція повертає рядок SVG. Сцена — поле 100 × 102 (тіло крамниці під смугою товару); у крамниці на полі ліворуч
// унизу її накриває стовпчик ціни, праворуч унизу — стрічка з назвою, тож головне — у центрі й угорі праворуч.
// Стиль: пласкі фігури, тепла палітра, без контурів (лише темне дерево там, де треба форму).
import { ICONS, ICON_FILL, ICON_DETAIL } from '../icons.js';

export const C = {
  wall: '#f2e7d0', wallSh: '#e2d3b4', thatch: '#c9a86a', thatchD: '#8a6a3a', wood: '#7a4f2c', woodD: '#4a2e1a', woodL: '#a7744a',
  ground: '#a7744a', grass: '#5e7d43', grassD: '#3f5f2e', grassL: '#86a35a', water: '#3d7cc9', waterL: '#9cc4ea',
  red: '#b3262e', redL: '#d64541', black: '#2a1d17', skin: '#e8c4a0', hair: '#3a2414', linen: '#efe6cf', gold: '#e2b23a',
  sky: ['#8fc1ea', '#e6f1f7'], eve: ['#f6b178', '#fbe2b8'], night: ['#151a3a', '#2b3163'], dusk: ['#5b4a8a', '#f0a070'],
  clay: '#c4512f', clayL: '#d98a4e', clayD: '#8a3a1e', blue: '#3c78d8', green: '#4f9a3a', purple: '#9a5cf0', yellow: '#f2c94c',
};
const f = (n) => Math.round(n * 100) / 100;
/** Обгортка: зсув і масштаб. */
export const at = (x, y, s, inner, extra = '') => `<g transform="translate(${f(x)},${f(y)})${s !== 1 ? ` scale(${f(s)})` : ''}"${extra}>${inner}</g>`;
/** Значок з icons.js як частина сцени. */
export function ic(key, x, y, s, { fill, detail, lw = 1.9, op } = {}) {
  const I = ICONS[key], fl = fill || ICON_FILL[key] || '#fff', d = detail || ICON_DETAIL[key] || '#000';
  let o = '';
  for (const p of I.shape || []) o += `<path d="${p}" fill="${fl}"/>`;
  for (const p of I.sline || []) o += `<path d="${p}" fill="none" stroke="${fl}" stroke-width="${lw * 1.25}" stroke-linecap="round"/>`;
  for (const p of I.line || []) o += `<path d="${p}" fill="none" stroke="${d}" stroke-width="${lw}" stroke-linecap="round" stroke-linejoin="round"/>`;
  for (const [cx, cy, r] of I.dot || []) o += `<circle cx="${cx}" cy="${cy}" r="${r}" fill="${d}"/>`;
  return at(x - s / 2, y - s / 2, s / 32, o, op != null ? ` opacity="${op}"` : '');
}
/** Псевдовипадкові числа для зірок, трави тощо (детерміновано за seed). */
export function rnd(seed) {
  let s = (seed * 9301 + 49297) % 233280;
  return () => { s = (s * 9301 + 49297) % 233280; return s / 233280; };
}

// ── тло ─────────────────────────────────────────────────────────────────────
export function sky(id, [a, b], h = 102) {
  return `<defs><linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${a}"/><stop offset="1" stop-color="${b}"/></linearGradient></defs><rect width="100" height="${h}" fill="url(#${id})"/>`;
}
export const fill = (c, y = 0, h = 102) => `<rect y="${y}" width="100" height="${h}" fill="${c}"/>`;
export const sun = (x, y, r = 10, c = '#ffe39a') => `<circle cx="${x}" cy="${y}" r="${r * 1.6}" fill="${c}" opacity=".25"/><circle cx="${x}" cy="${y}" r="${r}" fill="${c}"/>`;
export const moon = (x, y, r = 9, bg = '#151a3a') => `<circle cx="${x}" cy="${y}" r="${r}" fill="#fff3c4"/><circle cx="${x + r * 0.45}" cy="${y - r * 0.3}" r="${r * 0.9}" fill="${bg}"/>`;
export function stars(seed, n = 10, y1 = 45) {
  const r = rnd(seed); let o = '';
  for (let i = 0; i < n; i++) o += `<circle cx="${f(r() * 100)}" cy="${f(r() * y1)}" r="${f(0.5 + r() * 0.7)}" fill="#fff3c4" opacity="${f(0.5 + r() * 0.5)}"/>`;
  return o;
}
/** Пагорби на обрії: хвиляста смуга від y донизу. */
export function hills(y, c = C.grass, amp = 6, seed = 1) {
  const r = rnd(seed); let d = `M0 ${y + r() * amp}`;
  for (let x = 0; x < 100; x += 25) d += ` Q${x + 12.5} ${f(y - amp + r() * amp * 2)} ${x + 25} ${f(y + r() * amp - amp / 2)}`;
  return `<path d="${d} V102 H0z" fill="${c}"/>`;
}
export const floor = (y, c = C.ground, line = '#7d5233') => `<rect y="${y}" width="100" height="${102 - y}" fill="${c}"/><rect y="${y}" width="100" height="2.5" fill="${line}"/>`;
/** Стіна хати з синьою призьбою. */
export const wall = (y = 74, c = C.wall) => `<rect width="100" height="102" fill="${c}"/>` + floor(y) + `<path d="M0 ${y}h100" stroke="${C.blue}" stroke-width="2.4"/>`;
export function river(y, h = 30, seed = 2) {
  const r = rnd(seed); let o = `<rect y="${y}" width="100" height="${h}" fill="${C.water}"/>`;
  for (let i = 0; i < 5; i++) { const x = r() * 85, yy = y + 4 + r() * (h - 8); o += `<path d="M${f(x)} ${f(yy)}q4-2 8 0t8 0" stroke="${C.waterL}" stroke-width="1.3" fill="none"/>`; }
  return o;
}
export function grass(seed, y = 102, n = 8, c = C.grassD, h = 10) {
  const r = rnd(seed); let d = '';
  for (let i = 0; i < n; i++) { const x = r() * 100; d += `M${f(x)} ${y}l${f((r() - 0.5) * 5)} ${f(-h * (0.5 + r() * 0.6))}`; }
  return `<path d="${d}" stroke="${c}" stroke-width="1.8" stroke-linecap="round" fill="none"/>`;
}
export function flowers(seed, y0, y1, n = 8, cols = [C.red, C.yellow, '#fff', C.purple]) {
  const r = rnd(seed); let o = '';
  for (let i = 0; i < n; i++) { const x = r() * 100, y = y0 + r() * (y1 - y0), c = cols[i % cols.length]; o += `<circle cx="${f(x)}" cy="${f(y)}" r="1.6" fill="${c}"/><circle cx="${f(x)}" cy="${f(y)}" r=".6" fill="#f2c94c"/>`; }
  return o;
}

// ── будівлі й речі ──────────────────────────────────────────────────────────
/** Біла хата зі стріхою: x — лівий край, y — низ стіни, w — ширина. lit — світло у вікнах. */
export function hata(x, y, w = 40, { lit = false, door = true, win = 2, wall = C.wall } = {}) {
  const h = w * 0.5, rh = w * 0.42;
  let o = `<path d="M${x - w * 0.08} ${y - h} L${x + w / 2} ${y - h - rh} L${x + w * 1.08} ${y - h}z" fill="${C.thatch}"/>`;
  o += `<path d="M${x - w * 0.08} ${y - h} L${x + w / 2} ${y - h - rh} L${x + w * 1.08} ${y - h}" stroke="${C.thatchD}" stroke-width="1.4" fill="none"/>`;
  o += `<rect x="${x}" y="${y - h}" width="${w}" height="${h}" fill="${wall}"/><rect x="${x}" y="${y - 2.5}" width="${w}" height="2.5" fill="${C.blue}"/>`;
  const wc = lit ? '#ffcf6b' : '#7fb2df', ww = w * 0.16, wy = y - h * 0.72;
  const xs = win === 1 ? [x + w * 0.62] : [x + w * 0.14, x + w * 0.66];
  for (const wx of xs) o += `<rect x="${f(wx)}" y="${f(wy)}" width="${f(ww)}" height="${f(ww)}" fill="${wc}" stroke="${C.wood}" stroke-width="1"/><path d="M${f(wx + ww / 2)} ${f(wy)}v${f(ww)}" stroke="${C.wood}" stroke-width=".8"/>`;
  if (door) o += `<rect x="${f(x + w * 0.4)}" y="${f(y - h * 0.82)}" width="${f(w * 0.17)}" height="${f(h * 0.82)}" fill="${C.wood}"/>`;
  return o;
}
/** Ятка: стовпчики, смугаста стріха, прилавок. x — лівий край, y — низ, w — ширина. */
export function yatka(x, y, w = 40, c = C.red, { h = 30, counter = C.woodL } = {}) {
  const n = 6, sw = w / n;
  let o = `<rect x="${x + 1}" y="${y - h}" width="2.2" height="${h}" fill="${C.woodD}"/><rect x="${x + w - 3.2}" y="${y - h}" width="2.2" height="${h}" fill="${C.woodD}"/>`;
  for (let i = 0; i < n; i++) o += `<path d="M${f(x + i * sw)} ${y - h}h${f(sw)}v7q${f(-sw / 2)} 3 ${f(-sw)} 0z" fill="${i % 2 ? '#fbf3e1' : c}"/>`;
  o += `<rect x="${x - 1}" y="${y - h - 2}" width="${w + 2}" height="3" fill="${C.woodD}"/>`;
  o += `<rect x="${x}" y="${y - h * 0.38}" width="${w}" height="${h * 0.38}" fill="${counter}"/><rect x="${x}" y="${y - h * 0.38}" width="${w}" height="2" fill="${C.woodD}"/>`;
  return o;
}
export function tree(x, y, s = 1, c = C.grassD) {
  return at(x, y, s, `<rect x="-1.2" y="-10" width="2.4" height="10" fill="${C.woodD}"/><ellipse cx="0" cy="-17" rx="9" ry="10" fill="${c}"/><ellipse cx="-3" cy="-20" rx="4" ry="4" fill="#fff" opacity=".08"/>`);
}
export function poplar(x, y, s = 1, c = '#3f6b38') {
  return at(x, y, s, `<rect x="-.8" y="-6" width="1.6" height="6" fill="${C.woodD}"/><ellipse cx="0" cy="-22" rx="4" ry="18" fill="${c}"/>`);
}
export function willow(x, y, s = 1) {
  return at(x, y, s, `<rect x="-1.5" y="-14" width="3" height="14" fill="${C.woodD}"/><path d="M-12 -2c0-14 4-22 12-22s12 8 12 22c-2-6-4-9-6-10 0 5 0 8-1 11-1-6-3-9-5-10-2 1-4 4-5 10-1-3-1-6-1-11-2 1-4 4-6 10z" fill="#4f7a3a"/>`);
}
/** Тин з лози від x0 до x1, низ y. */
export function tyn(x0, x1, y, h = 12) {
  let o = '';
  for (let x = x0; x <= x1; x += 6) o += `<rect x="${x}" y="${y - h - 2}" width="1.6" height="${h + 2}" fill="${C.woodD}"/>`;
  for (let k = 0; k < 3; k++) { const yy = y - h + 2 + k * 3.6; o += `<path d="M${x0} ${f(yy)}h${x1 - x0 + 2}" stroke="${C.wood}" stroke-width="2.2"/>`; }
  return o;
}
/** Глечик-горщик значком clay. */
export const jug = (x, y, s = 14, fl = C.clay, d = '#6b2614') => ic('clay', x, y - s / 2, s, { fill: fl, detail: d });
/** Макітра: широка миска з поливою. */
export const makitra = (x, y, w = 16, c = C.clayL, inner = '#f2e2c0') =>
  `<path d="M${x - w / 2} ${y - w * 0.5}h${w}l${-w * 0.16} ${w * 0.5}h${-w * 0.68}z" fill="${c}"/><ellipse cx="${x}" cy="${y - w * 0.5}" rx="${w / 2}" ry="${w * 0.1}" fill="${inner}"/><path d="M${x - w * 0.4} ${y - w * 0.28}h${w * 0.8}" stroke="${C.red}" stroke-width="1"/>`;
/** Миска з розписом (півколо + орнамент). */
export const bowl = (x, y, w = 16, c = '#f7ecd2', o = C.red) =>
  `<path d="M${x - w / 2} ${y - w * 0.32}a${w / 2} ${w * 0.32} 0 0 0 ${w} 0z" fill="${c}" stroke="${C.clayD}" stroke-width=".9"/><path d="M${x - w * 0.36} ${y - w * 0.2}q${w * 0.09} -${w * 0.08} ${w * 0.18} 0t${w * 0.18} 0 ${w * 0.18} 0 ${w * 0.18} 0" stroke="${o}" stroke-width="1" fill="none"/><path d="M${x - w / 2} ${y - w * 0.32}h${w}" stroke="${C.clayD}" stroke-width="1"/>`;
export const shelf = (x, y, w, c = C.wood) => `<rect x="${x}" y="${y}" width="${w}" height="3" fill="${c}"/><rect x="${x + 3}" y="${y + 3}" width="2" height="4" fill="${C.woodD}"/><rect x="${x + w - 5}" y="${y + 3}" width="2" height="4" fill="${C.woodD}"/>`;
export const basket = (x, y, w = 22, c = '#c8955a') =>
  `<path d="M${x} ${y - w * 0.5}h${w}l${-w * 0.1} ${w * 0.5}h${-w * 0.8}z" fill="${c}"/><path d="M${x + 2} ${y - w * 0.32}h${w - 4}M${x + 3} ${y - w * 0.16}h${w - 6}" stroke="#8a5a35" stroke-width="1"/><path d="M${x + w * 0.15} ${y - w * 0.5}q${w * 0.35} -${w * 0.5} ${w * 0.7} 0" stroke="#8a5a35" stroke-width="1.6" fill="none"/>`;
export const sack = (x, y, s = 1, c = '#d9c49a') =>
  at(x, y, s, `<path d="M-7 0c-2-6-1-12 3-15l-1-3h10l-1 3c4 3 5 9 3 15z" fill="${c}"/><path d="M-4 -15h8" stroke="#8a6a3a" stroke-width="1.4"/>`);
/** Рушник: прямокутник з червоною вишивкою на кінцях. */
export function rushnyk(x, y, w, h, { c = C.linen, e = C.red } = {}) {
  let o = `<rect x="${x}" y="${y}" width="${w}" height="${h}" fill="${c}"/>`;
  const band = (yy) => { let d = `M${x + 1} ${yy}`; for (let xx = x + 1; xx < x + w - 2; xx += 3) d += `l1.5 -1.5l1.5 1.5`; return `<path d="${d}" stroke="${e}" stroke-width="1" fill="none"/>`; };
  o += band(y + h - 4) + band(y + h - 7.5);
  o += `<path d="M${x} ${y + h}h${w}" stroke="${e}" stroke-width="1" stroke-dasharray="1 1.4"/>`;
  return o;
}
/** Мотузка-провис між x0 і x1 на висоті y. */
export const rope = (x0, x1, y, sag = 4) => `<path d="M${x0} ${y}Q${(x0 + x1) / 2} ${y + sag * 2} ${x1} ${y}" stroke="${C.woodD}" stroke-width="1" fill="none"/>`;
/** Сорочка-вишиванка (силует) на висоті: x — центр, y — верх. */
export function shirt(x, y, s = 1, c = '#fbf6ea', e = C.red) {
  return at(x, y, s, `<path d="M-5 0h10l7 5-3 4-4-2v15h-10v-15l-4 2-3-4z" fill="${c}"/><path d="M-1.5 0v6M1.5 0v6" stroke="${e}" stroke-width="1.2"/><path d="M8 4l3 3M-8 4l-3 3" stroke="${e}" stroke-width="1.6"/><path d="M-5 20h10" stroke="${e}" stroke-width="1.2" stroke-dasharray="1.2 1"/>`);
}
/** Свита / кожух: довгий одяг. */
export function svyta(x, y, s = 1, c = '#8a5a35', trim = '#f3ead7') {
  return at(x, y, s, `<path d="M-6 0h12l8 6-3 5-4-3v22h-14v-22l-4 3-3-5z" fill="${c}"/><path d="M0 0v30" stroke="${trim}" stroke-width="1.6"/><path d="M-6 0h12" stroke="${trim}" stroke-width="2.4"/>`);
}
/** Килим з ромбами. */
export function kilim(x, y, w, h, [a, b, c] = [C.red, C.black, C.yellow]) {
  let o = `<rect x="${x}" y="${y}" width="${w}" height="${h}" fill="${a}"/><rect x="${x + 2}" y="${y + 2}" width="${w - 4}" height="${h - 4}" fill="none" stroke="${c}" stroke-width="1.4"/>`;
  const n = Math.max(1, Math.round(w / 12)), sx = (w - 4) / n;
  for (let i = 0; i < n; i++) { const cx = x + 2 + sx * (i + 0.5), cy = y + h / 2, r = Math.min(sx, h) * 0.32; o += `<path d="M${f(cx)} ${f(cy - r)}l${f(r)} ${f(r)}-${f(r)} ${f(r)}-${f(r)}-${f(r)}z" fill="${b}"/><path d="M${f(cx)} ${f(cy - r / 2)}l${f(r / 2)} ${f(r / 2)}-${f(r / 2)} ${f(r / 2)}-${f(r / 2)}-${f(r / 2)}z" fill="${c}"/>`; }
  return o;
}
/** Віз: x — центр, y — низ коліс. */
export function cart(x, y, s = 1, load = '') {
  return at(x, y, s, `${load}<rect x="-16" y="-12" width="32" height="5" fill="${C.wood}"/><path d="M-16 -12l-2-6h36l-2 6" fill="none" stroke="${C.woodD}" stroke-width="1.6"/><path d="M16 -9.5h12" stroke="${C.woodD}" stroke-width="1.8"/>`
    + [-10, 10].map((cx) => `<circle cx="${cx}" cy="-5" r="5.5" fill="none" stroke="${C.woodD}" stroke-width="1.6"/><path d="M${cx} -10.5v11M${cx - 5.5} -5h11" stroke="${C.woodD}" stroke-width="1"/>`).join(''));
}
export function ox(x, y, s = 1, c = '#9a8a76') {
  return at(x, y, s, `<rect x="-11" y="-14" width="20" height="9" rx="4" fill="${c}"/><path d="M-9 -6v6M-5 -6v6M3 -6v6M7 -6v6" stroke="${c}" stroke-width="2.4"/><ellipse cx="11" cy="-13" rx="4" ry="3.4" fill="${c}"/><path d="M9 -16q-2-4 1-5M13 -16q2-4-1-5" stroke="#f3ead7" stroke-width="1.2" fill="none"/>`);
}
export function sheep(x, y, s = 1, c = '#f1ece0', head = '#3a3330') {
  return at(x, y, s, `<path d="M-1 -1v1M5 -1v1" stroke="${head}" stroke-width="1.6"/><ellipse cx="2" cy="-6" rx="7.5" ry="5" fill="${c}"/><circle cx="-2" cy="-8.5" r="3" fill="${c}"/><circle cx="5" cy="-9" r="3" fill="${c}"/><ellipse cx="9.5" cy="-7" rx="2.6" ry="2" fill="${head}"/>`);
}
export function boat(x, y, s = 1, c = C.wood) { return at(x, y, s, `<path d="M-18 0h36l-6 7h-24z" fill="${c}"/><path d="M-18 0h36" stroke="${C.woodD}" stroke-width="1.4"/>`); }
/** Багаття: x, y — низ. */
export function fire(x, y, s = 1) {
  return at(x, y, s, `<ellipse cx="0" cy="-6" rx="12" ry="10" fill="#ffb347" opacity=".25"/><path d="M-8 0l16-4M-8 -4l16 4" stroke="${C.woodD}" stroke-width="2.4"/><path d="M-5 -2c-2-6 2-8 1-13 4 3 5 6 4 9 2-3 2-5 1-8 4 4 5 9 2 12z" fill="#ff7a2f"/><path d="M-2 -2c-1-4 1-5 1-8 2 2 3 4 2 8z" fill="#ffd27a"/>`);
}
/** Піч (з жаром): x — лівий край, y — низ. */
export function pich(x, y, w = 30, h = 40, { glow = true, c = '#e9dcc4', tiles = false } = {}) {
  let o = `<rect x="${x}" y="${y - h}" width="${w}" height="${h}" fill="${c}"/>`;
  if (tiles) for (let yy = y - h + 3; yy < y - 4; yy += 7) for (let xx = x + 3; xx < x + w - 4; xx += 7) o += `<rect x="${xx}" y="${yy}" width="5.5" height="5.5" fill="#fbf3e1" stroke="${C.blue}" stroke-width=".7"/><circle cx="${xx + 2.75}" cy="${yy + 2.75}" r="1.2" fill="${C.blue}"/>`;
  o += `<path d="M${x + w * 0.25} ${y}v${-h * 0.3}a${w * 0.25} ${w * 0.25} 0 0 1 ${w * 0.5} 0v${h * 0.3}z" fill="${glow ? '#ff9a3c' : C.woodD}"/>`;
  if (glow) o += `<path d="M${x + w * 0.32} ${y}v${-h * 0.2}a${w * 0.18} ${w * 0.18} 0 0 1 ${w * 0.36} 0v${h * 0.2}z" fill="#ffd27a"/>`;
  return o;
}
export function skrynia(x, y, w = 24, c = '#8e2a24') {
  const h = w * 0.55;
  return `<rect x="${x}" y="${y - h}" width="${w}" height="${h}" rx="1.5" fill="${c}"/><path d="M${x} ${y - h * 0.62}h${w}" stroke="${C.gold}" stroke-width="1.4"/><rect x="${x + w / 2 - 2}" y="${y - h * 0.7}" width="4" height="4" fill="${C.gold}"/>`
    + `<path d="M${x + 3} ${y - h * 0.3}l2 -2 2 2 2 -2 2 2M${x + w - 13} ${y - h * 0.3}l2 -2 2 2 2 -2 2 2" stroke="${C.yellow}" stroke-width=".9" fill="none"/>`;
}
/** Церковка з банями: x — центр, y — низ. */
export function church(x, y, s = 1, c = '#efe6cf', dome = '#3f8a5a') {
  return at(x, y, s, `<rect x="-10" y="-18" width="20" height="18" fill="${c}"/><rect x="-4" y="-28" width="8" height="10" fill="${c}"/><path d="M-5 -28q5-10 10 0z" fill="${dome}"/><path d="M0 -38v-4M-1.5 -40h3" stroke="${C.gold}" stroke-width="1"/><path d="M-10 -18q3-7 6 0M4 -18q3-7 6 0" fill="${dome}"/><rect x="-2" y="-9" width="4" height="9" fill="${C.wood}"/>`);
}
export function mountains(y, c1 = '#6f7f9a', c2 = '#4f5f7a') {
  return `<path d="M0 ${y}l18-22 12 12 16-24 18 22 12-12 24 24V102H0z" fill="${c1}"/><path d="M46 ${y - 34}l-5 6 4-1 3 3 4-3z" fill="#f3f0e8"/><path d="M0 ${y + 8}l22-14 20 12 22-16 36 20V102H0z" fill="${c2}"/>`;
}
export function sunflower(x, y, s = 1) {
  return at(x, y, s, `<path d="M0 0v-22" stroke="#3f6b38" stroke-width="1.6"/><path d="M0 -10q-6-2-7 2 4 2 7-2z" fill="#4f7a3a"/>` + [0, 45, 90, 135, 180, 225, 270, 315].map((a) => `<ellipse cx="0" cy="-27.5" rx="1.8" ry="3.6" fill="${C.yellow}" transform="rotate(${a} 0 -24)"/>`).join('') + `<circle cx="0" cy="-24" r="3" fill="#5a3a1a"/>`);
}
export function pumpkin(x, y, s = 1) { return at(x, y, s, `<ellipse cx="0" cy="-5" rx="8" ry="5.5" fill="#e08a2e"/><path d="M-3 -10v10M3 -10v10" stroke="#b86a1e" stroke-width="1"/><path d="M0 -10q1-3 3-3" stroke="#3f6b38" stroke-width="1.6" fill="none"/>`); }
export function bottle(x, y, s = 1, c = '#5aa070') {
  return at(x, y, s, `<rect x="-3" y="-11" width="6" height="11" rx="1.5" fill="${c}"/><rect x="-1.2" y="-14.5" width="2.4" height="3.5" fill="${c}"/><rect x="-1.4" y="-16" width="2.8" height="1.6" fill="${C.wood}"/><rect x="-2" y="-7" width="4" height="3" fill="#f3ead7"/>`);
}
/** Пучок трав (висить донизу): x — точка підвісу. */
export function bunch(x, y, s = 1, c = C.green) {
  return at(x, y, s, `<path d="M0 0v4" stroke="${C.woodD}" stroke-width="1"/><path d="M-1 4h2" stroke="${C.red}" stroke-width="2"/><path d="M0 5l-5 12M0 5l-2 13M0 5l1 13M0 5l4 12M0 5l6 10" stroke="${c}" stroke-width="1.6" stroke-linecap="round"/><path d="M-5 17l-1 2M4 17l1 2" stroke="${C.purple}" stroke-width="1.6" stroke-linecap="round"/>`);
}
/** Вінок з квітів (коло). */
export function vinok(x, y, r = 6, ribbons = true) {
  let o = '';
  for (let i = 0; i < 10; i++) { const a = i / 10 * Math.PI * 2; o += `<circle cx="${f(x + Math.cos(a) * r)}" cy="${f(y + Math.sin(a) * r * 0.55)}" r="${f(r * 0.32)}" fill="${[C.red, C.yellow, '#fff', C.blue, C.redL][i % 5]}"/>`; }
  if (ribbons) o += `<path d="M${x - 2} ${y + r * 0.5}q-2 8 -4 14M${x} ${y + r * 0.5}q0 8 1 15M${x + 2} ${y + r * 0.5}q3 7 5 13" stroke-width="1.4" fill="none" stroke="${C.red}"/><path d="M${x} ${y + r * 0.5}q0 8 1 15" stroke="${C.blue}" stroke-width="1.4" fill="none"/>`;
  return o;
}

// ── люди ────────────────────────────────────────────────────────────────────
/**
 * Людина (стоїть): x — центр, y — ступні, s — масштаб (s = 1 — ~40 заввишки).
 * o: w (жінка) | m; shirt, skirt (плахта), apron (запаска), pants, boots, coat (свита поверх), hat: brylj|shapka|ochipok|
 * vinok|khustka|kapelyukh|none; arms: down|up|work|hold; beard; mustache (вуса); hair (колір); hold (SVG у руках, у локальних).
 */
export function person(x, y, s = 1, o = {}) {
  const w = o.w, skin = o.skin || C.skin, shirt = o.shirt || '#fbf6ea', hair = o.hair || C.hair;
  let g = '';
  if (w) {
    g += `<path d="M-7 0l2-15h10l2 15z" fill="${o.skirt || C.red}"/>` + (o.apron !== false ? `<path d="M-3 0l1-14h4l1 14z" fill="${o.apron || C.black}"/>` : '');
    g += `<path d="M-1.4 0v2.5M2.6 0v2.5" stroke="${o.boots || C.redL}" stroke-width="2.4"/>`;
  } else {
    g += `<path d="M-5 0l1-14h8l1 14h-4l-1-9-1 9z" fill="${o.pants || '#3c5a8a'}"/><path d="M-5.4 0v-4M4 0v-4" stroke="${o.boots || C.black}" stroke-width="3"/>`;
  }
  g += `<path d="M-5 -14l1-14h8l1 14z" fill="${shirt}"/><path d="M-5 -15h10" stroke="${C.red}" stroke-width="1.6"/><path d="M-1 -28v4" stroke="${C.red}" stroke-width="1"/>`;
  if (o.coat) g += `<path d="M-6 ${w ? -15 : -2}l1-13h3l1 ${w ? 13 : 26}zM6 ${w ? -15 : -2}l-1-13h-3l-1 ${w ? 13 : 26}z" fill="${o.coat}"/>`;
  const arms = o.arms || 'down', armC = o.coat || shirt;
  if (arms === 'down') g += `<path d="M-5 -27l-2.5 12M5 -27l2.5 12" stroke="${armC}" stroke-width="2.8" stroke-linecap="round"/><circle cx="-7.6" cy="-14" r="1.4" fill="${skin}"/><circle cx="7.6" cy="-14" r="1.4" fill="${skin}"/>`;
  else if (arms === 'up') g += `<path d="M-5 -27l-5-10M5 -27l5-10" stroke="${armC}" stroke-width="2.8" stroke-linecap="round"/><circle cx="-10.4" cy="-38" r="1.4" fill="${skin}"/><circle cx="10.4" cy="-38" r="1.4" fill="${skin}"/>`;
  else g += `<path d="M-5 -27l3 8 7-1M5 -27l3 8 3-1" stroke="${armC}" stroke-width="2.8" stroke-linecap="round" stroke-linejoin="round" fill="none"/><circle cx="6" cy="-20" r="1.4" fill="${skin}"/><circle cx="11.4" cy="-20" r="1.4" fill="${skin}"/>`;
  if (o.hold) g += o.hold;
  g += `<rect x="-1.4" y="-30.5" width="2.8" height="3" fill="${skin}"/><circle cx="0" cy="-34" r="4.4" fill="${skin}"/>`;
  if (w && o.hat !== 'khustka' && o.hat !== 'ochipok') g += `<path d="M-4.4 -35c0-5 8.8-5 8.8 0l.6 6h-1.6l-.4-4c-1 1-5 1-6 0l-.4 4h-1.6z" fill="${hair}"/>` + (o.braid ? `<path d="M3.6 -32q2 6 0 12" stroke="${hair}" stroke-width="2" fill="none"/>` : '');
  if (!w && o.hat !== 'shapka' && o.hat !== 'brylj' && o.hat !== 'kapelyukh') g += `<path d="M-4.4 -35c0-5 8.8-5 8.8 0z" fill="${hair}"/>`;
  if (o.mustache !== false && !w) g += `<path d="M-2.6 -31.4q2.6-1.6 5.2 0" stroke="${hair}" stroke-width="1.1" fill="none"/>`;
  if (o.beard) g += `<path d="M-3.6 -32q3.6 7 7.2 0" fill="${o.beard}"/>`;
  const hat = o.hat || 'none';
  if (hat === 'brylj') g += `<ellipse cx="0" cy="-37.4" rx="8" ry="1.8" fill="#e8c97a"/><path d="M-4 -37.6c0-4 8-4 8 0z" fill="#e8c97a"/><path d="M-4 -38.4h8" stroke="${C.red}" stroke-width=".9"/>`;
  else if (hat === 'shapka') g += `<path d="M-4.8 -36.4h9.6l-1-7.6c-2-1.6-5.6-1.6-7.6 0z" fill="${o.hatC || C.black}"/>` + (o.shlyk ? `<path d="M3.8 -43q4 0 5 5" stroke="${C.redL}" stroke-width="2.4" fill="none"/>` : '');
  else if (hat === 'kapelyukh') g += `<ellipse cx="0" cy="-37" rx="7" ry="1.6" fill="${C.black}"/><rect x="-4" y="-42" width="8" height="5" rx="1" fill="${C.black}"/>`;
  else if (hat === 'ochipok') g += `<path d="M-4.8 -34c0-6 9.6-6 9.6 0z" fill="${o.hatC || C.redL}"/><path d="M-4.6 -35h9.2" stroke="${C.gold}" stroke-width="1"/>`;
  else if (hat === 'khustka') g += `<path d="M-5.2 -33c0-7 10.4-7 10.4 0l1.4 6-3-2.4c-.6-3-1-4-3.8-4.2-2.8.2-3.2 1.2-3.8 4.2l-3 2.4z" fill="${o.hatC || C.redL}"/><circle cx="-1.6" cy="-37" r=".7" fill="#fff"/><circle cx="1.8" cy="-35.4" r=".7" fill="#fff"/>`;
  else if (hat === 'vinok') g += `<path d="M3 -35q3 6 2 14M4.4 -35.4q4 5 5 12" stroke="${C.red}" stroke-width="1.3" fill="none"/><path d="M3.8 -35q4 6 3.6 13" stroke="${C.blue}" stroke-width="1.3" fill="none"/>` + vinok(0, -37.6, 4.6, false);
  return at(x, y, s, g, o.flip ? '' : '').replace(/^<g transform="([^"]*)"/, o.flip ? '<g transform="$1 scale(-1,1)"' : '<g transform="$1"');
}
/** Чорт: маленький, рогатий, з хвостом. x — центр, y — низ. */
export function chort(x, y, s = 1) {
  return at(x, y, s, `<path d="M-4 0l1-10h6l1 10z" fill="${C.black}"/><circle cx="0" cy="-13" r="4.4" fill="${C.black}"/><path d="M-3.4 -16l-1.6-4 2.6 2.4M3.4 -16l1.6-4-2.6 2.4" fill="${C.black}"/><circle cx="-1.6" cy="-13.4" r=".9" fill="#ff5a3c"/><circle cx="1.6" cy="-13.4" r=".9" fill="#ff5a3c"/><path d="M4 -3q8 2 6-8" stroke="${C.black}" stroke-width="1.4" fill="none"/><path d="M9 -11l2-2 1 3z" fill="${C.black}"/><path d="M-4 -8l-4 3M4 -8l3-5" stroke="${C.black}" stroke-width="2" stroke-linecap="round"/>`);
}
/** Сяйво цвіту папороті. */
export function fernGlow(id, x, y, r = 16) {
  return `<defs><radialGradient id="${id}"><stop offset="0" stop-color="#ffd86b" stop-opacity=".9"/><stop offset="1" stop-color="#9a5cf0" stop-opacity="0"/></radialGradient></defs><circle cx="${x}" cy="${y}" r="${r}" fill="url(#${id})"/>` + ic('fern', x, y, r * 1.1);
}
/** Монети-дукати купкою. */
export function coins(x, y, n = 5) {
  let o = '';
  for (let i = 0; i < n; i++) o += `<ellipse cx="${f(x + (i % 3) * 5 - 5)}" cy="${f(y - Math.floor(i / 3) * 3)}" rx="3.4" ry="1.6" fill="${C.gold}" stroke="#9a6a00" stroke-width=".6"/>`;
  return o;
}
