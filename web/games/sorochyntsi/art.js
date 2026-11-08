// «Сорочинський ярмарок» — арт крамниць (опитування 09.11): на кожній із 90 крамниць своя сценка кодом, у стилі значків.
// Сцена — SVG-рядок у полі 100 × 102 (тіло крамниці під смугою товару). Вантажиться ліниво (import()) — і 2D-полем,
// і карткою (велика картка «розглядати»), і 3D (лиця крамниць). Легенда — рядок про крамницю для великої картки.
import { S3 } from './art/t3.js';
import { S2 } from './art/t2.js';
import { S1 } from './art/t1.js';
import { LORE } from './art/lore.js';

const ALL = { ...S1, ...S2, ...S3 };
const cache = new Map();

/** Внутрішність сцени (без <svg>) або '' — якщо сцени ще нема. id градієнтів — з префіксом `sa<id>`. */
export function artInner(id) {
  if (cache.has(id)) return cache.get(id);
  const fn = ALL[id];
  let s = '';
  try { s = fn ? fn('sa' + id) : ''; } catch (e) { console.warn('Сорочинці: сцена', id, e); s = ''; }
  cache.set(id, s);
  return s;
}
/** Повний <svg> сцени — для HTML (велика картка) і canvas (лиця 3D). crop — [y0, h], щоб показати частину по висоті. */
export function artSvg(id, { crop = null, cls = '', w = null } = {}) {
  const inner = artInner(id);
  if (!inner) return '';
  const vb = crop ? `0 ${crop[0]} 100 ${crop[1]}` : '0 0 100 102';
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${vb}"${w ? ` width="${w}" height="${Math.round(w * (crop ? crop[1] : 102) / 100)}"` : ''}${cls ? ` class="${cls}"` : ''} preserveAspectRatio="xMidYMid slice" aria-hidden="true"><rect width="100" height="102" fill="#f2e7d0"/>${inner}</svg>`;
}
export const hasArt = (id) => !!ALL[id];
export const lore = (id) => LORE[id] || '';
