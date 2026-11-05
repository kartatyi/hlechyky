// «Соляний шлях» — малюнки картки столу для трьох виглядів (§11.1, solyanyi-look.md): карти вантажів (колір + значок +
// фактура), сорочки, значки шляхів, печатки, жетони гравців, таймер-квітка, рахівниця, тло столу, прев'ю теми.
// Лише SVG-рядки, намальовані кодом: жодних файлів зображень і нових шрифтів. Символи карт — один раз на сторінку в defs(theme)
// (svg 0×0 у корені картки, не display:none), карти посилаються на них через <use>, тож рука й ринок лишаються легкими.
import { CARGO_NAME, SEAT_SHAPE, CARDS } from './consts.js';

/** Нік і все, що прийшло з сервера, — лише через це: малюнки йдуть у innerHTML. */
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

/** Вантаж у грі → ключ у вихідних малюнках дизайнерів. */
const KEY = { salt: 'red', fish: 'blue', grain: 'green', tar: 'black', honey: 'yellow', wool: 'white', hides: 'orange', linen: 'pink', wild: 'wild', grey: 'any' };

// ═══ «Стара мапа» (малюнки дизайнера d1) ═══════════════════════════════════════════════════════════
const P1 = (() => {
const INK = '#2b1a0d';
const SEPIA = '#5a3a1e';
const BONE = '#f7efd9';

const CARGO = {
  red:    { name: 'сіль',    ink: '#b3261e', soft: '#c8473a', on: '#fff7e6', top: '#d0483a', bot: '#8e1a14' },
  blue:   { name: 'риба',    ink: '#1c5a9e', soft: '#3c79b8', on: '#fff7e6', top: '#3b7dbf', bot: '#154476' },
  green:  { name: 'збіжжя',  ink: '#2c7a3a', soft: '#4a9455', on: '#fff7e6', top: '#4d9a57', bot: '#1f5c2c' },
  black:  { name: 'дьоготь', ink: '#231c17', soft: '#3a312a', on: '#f7efd9', top: '#3d332b', bot: '#17110d' },
  yellow: { name: 'мед',     ink: '#e6ad12', soft: '#ecc04a', on: '#2b1a0d', top: '#f3c63a', bot: '#c98e08' },
  white:  { name: 'вовна',   ink: '#f8f3e4', soft: '#f8f3e4', on: '#2b1a0d', top: '#fbf7ea', bot: '#ddd2b6' },
  orange: { name: 'шкіри',   ink: '#c95f12', soft: '#d97a30', on: '#fff7e6', top: '#de7d2c', bot: '#a24a0b' },
  pink:   { name: 'полотно', ink: '#d5689a', soft: '#e089b0', on: '#2b1a0d', top: '#e78fb6', bot: '#b84d80' },
  wild:   { name: 'круторогий віл', ink: '#6a4bb0', soft: '#6a4bb0', on: '#fff', top: '#ecc659', bot: '#b78a22' },
  any:    { name: 'будь-який', ink: '#d9c79c', soft: '#d9c79c', on: '#2b1a0d' },
};

// ─── Символ вантажу: один і той самий у слоті шляху, на кутику карти, в легенді. Бокс ±10. ─────────────
function mark(kind, c) {
  switch (kind) {
    case 'red':    return `<path d="M0-10 7.5 0 0 10-7.5 0Z" fill="${c}"/>`;
    case 'blue':   return `<path d="M-10.5 0C-6-7.5 3-7 6.5-.8L11-6V6L6.5 .8C3 7-6 7.5-10.5 0Z" fill="${c}"/>`;
    case 'green':  return `<path d="M0 10.5V-9M0-3.5-5.5-8.5M0-3.5 5.5-8.5M0 2.5-5.5-2.5M0 2.5 5.5-2.5M0 8.5-5.5 3.5M0 8.5 5.5 3.5" fill="none" stroke="${c}" stroke-width="2.7" stroke-linecap="round"/>`;
    case 'black':  return `<path d="M0-11C3.5-5 7.5-1.5 7.5 3.2A7.5 7.5 0 0 1-7.5 3.2C-7.5-1.5-3.5-5 0-11Z" fill="${c}"/>`;
    case 'yellow': return `<path d="M0-9.5 8.4-4.7V4.7L0 9.5-8.4 4.7V-4.7Z" fill="${c}"/>`;
    case 'white':  return `<g fill="${c}"><circle cx="-4.6" cy="2.6" r="4.9"/><circle cx="4.6" cy="2.6" r="4.9"/><circle cx="0" cy="-4" r="5.4"/></g>`;
    case 'orange': return `<path d="M-10.5-8.500Q0-3 10.5-8.500V8.500Q0 3-10.5 8.500Z" fill="${c}"/>`;
    case 'pink':   return `<path d="M-7.5-7.5 7.5 7.5M7.5-7.5-7.5 7.5" fill="none" stroke="${c}" stroke-width="3.8" stroke-linecap="round"/>`;
    case 'wild':   return `<path d="M-10.5-8C-9-1.5-5-.8-3.5-.2-3 5-1.5 9 0 9S3 5 3.5-.2C5-.8 9-1.5 10.5-8 8-3.5 5-3.5 3-3.6-1-5-1-5-3-3.6-5-3.5-8-3.5-10.5-8Z" fill="${c}"/>`;
  }
  return '';
}

// ─── Ілюстрації для карт: бокс 80×78 (верх карти). ───────────────────────────────────────────────────
const s = (w = 1.5) => `stroke="${INK}" stroke-width="${w}" stroke-linejoin="round" stroke-linecap="round"`;
const ILLO = {
  // сіль: купа кристалів на рядні
  red: `<path d="M8 62Q40 72 72 62L66 68Q40 76 14 68Z" fill="#7a5a33" ${s()}/>
    <path d="M12 63C20 48 26 38 40 22 54 38 60 48 68 63 50 69 30 69 12 63Z" fill="#fdf9ee" ${s(1.8)}/>
    <path d="M47 33 56 49 M52 42 61 58 M45 43 52 62 M38 52 42 65" stroke="#a79a7e" stroke-width="1.3" fill="none" stroke-linecap="round"/>
    <g fill="#fff" ${s(1.1)}><path d="M40 21 45 27 40 33 35 27Z"/><path d="M29 41 34 47 29 53 24 47Z"/><path d="M41 45 45 50 41 55 37 50Z"/><path d="M22 56 25.5 60 22 64 18.5 60Z"/><path d="M53 54 57 58.500 53 63 49 58.500Z"/></g>
    <path d="M40 21V33M29 41V53M41 45V55" stroke="#b9ad92" stroke-width=".8"/>
    <g fill="#fff"><path d="M66 18 68 23 73 25 68 27 66 32 64 27 59 25 64 23Z" opacity=".95"/><path d="M15 26 16 29 19 30 16 31 15 34 14 31 11 30 14 29Z" opacity=".85"/></g>`,
  // риба: в’ялена тараня
  blue: `<g transform="rotate(-18 40 42)"><path d="M63 40 76 26C74 36 74 46 77 56L63 44Z" fill="#e7d9b3" ${s(1.6)}/>
    <path d="M6 42C16 22 46 20 66 41 48 62 18 62 6 42Z" fill="#f4ead0" ${s(1.8)}/>
    <path d="M30 28 Q38 16 50 28" fill="#d8c79f" ${s(1.3)}/><path d="M34 56Q40 64 48 54" fill="#d8c79f" ${s(1.2)}/>
    <path d="M21 28Q27 42 20 56" fill="none" ${s(1.4)}/>
    <g fill="none" stroke="#7b6a48" stroke-width="1"><path d="M27 34q4 3 0 6M33 31q4 3 0 6M39 31q4 3 0 6M45 33q4 3 0 6M30 40q4 3 0 6M36 38q4 3 0 6M42 39q4 3 0 6M48 40q4 3 0 6M33 47q4 3 0 6M39 46q4 3 0 6M45 46q4 3 0 6"/></g>
    <circle cx="14" cy="38" r="3.2" fill="#fff" ${s(1.2)}/><circle cx="14" cy="38" r="1.3" fill="${INK}"/><path d="M6.500 43Q11 45 15 43" fill="none" ${s(1.1)}/></g>
    <path d="M6 70q5-3 10 0t10 0 10 0 10 0 10 0 10 0 10 0" fill="none" stroke="#fff" stroke-opacity=".7" stroke-width="1.5"/>`,
  // збіжжя: сніп
  green: (() => {
    const ear = (x, y, r) => `<g transform="translate(${x} ${y}) rotate(${r})"><path d="M0 36V0" stroke="#e3bf5a" stroke-width="2" /><path d="M0-14V-24" stroke="${INK}" stroke-width="1"/>${[-12, -6, 0, 6].map((d) => `<path d="M0 ${d + 4}C-6 ${d + 2}-7 ${d - 3}-5 ${d - 6}-1.500 ${d - 4}0 ${d}0 ${d + 4}Z" fill="#f2d370" ${s(1.1)}/><path d="M0 ${d + 4}C6 ${d + 2}7 ${d - 3}5 ${d - 6}1.500 ${d - 4}0 ${d}0 ${d + 4}Z" fill="#e8bf4f" ${s(1.1)}/>`).join('')}</g>`;
    return ear(19, 30, -24) + ear(61, 30, 24) + ear(30, 25, -11) + ear(50, 25, 11) + ear(40, 22, 0)
      + `<path d="M31 52 36 72M49 52 44 72M40 52V74M34 52 28 72M46 52 52 72" stroke="#c99e3c" stroke-width="2.2" stroke-linecap="round"/>
      <path d="M29 47Q40 53 51 47L52 54Q40 60 28 54Z" fill="#a65f22" ${s(1.5)}/><path d="M33 50 36 55M40 51 40 56.500M47 50 44 55" stroke="#ebc98a" stroke-width="1"/>`;
  })(),
  // дьоготь: мазниця — відерце під возом
  black: `<path d="M23 22Q40-4 57 22" fill="none" stroke="#e7d9b3" stroke-width="2.6"/>
    <path d="M19 26H61L56 66Q40 72 24 66Z" fill="#8a5a2e" stroke="#f0e2bd" stroke-width="1.9" stroke-linejoin="round"/>
    <path d="M28 27 31 68M37 27 38 70M46 27 45 70M54 27 50 68" stroke="#3f2711" stroke-width="1.1"/>
    <path d="M21 36H59M23 56H57" stroke="#d6c7a2" stroke-width="2.600"/>
    <ellipse cx="40" cy="26" rx="21" ry="5.500" fill="#0b0806" stroke="#f0e2bd" stroke-width="1.9"/>
    <path d="M33 26Q40 23 48 26" fill="none" stroke="#6b5f55" stroke-width="1.300"/>
    <path d="M24 29C23 40 23 44 22 48A3.400 3.400 0 0 0 28 48C27 42 28 36 29 30Z" fill="#0b0806" stroke="#f0e2bd" stroke-width="1.300"/>
    <path d="M61 57C65 63 67 66 67 69A3.600 3.600 0 0 1 60 69C60 66 60 62 61 57Z" fill="#0b0806" stroke="#f0e2bd" stroke-width="1.300"/>`,
  // мед: стільники й бджола
  yellow: (() => {
    const hex = (x, y, f, r = 10.5) => `<path d="M${x} ${y - r}L${x + r * .87} ${y - r / 2}V${y + r / 2}L${x} ${y + r}L${x - r * .87} ${y + r / 2}V${y - r / 2}Z" fill="${f}" ${s(1.7)}/>`;
    return hex(24, 49, '#fff0b3') + hex(42.300, 49, '#b56f07') + hex(60.600, 49, '#fff0b3') + hex(33.200, 65, '#b56f07') + hex(51.500, 65, '#fff0b3') + hex(33.200, 33, '#b56f07')
      + `<path d="M42 58C42 62 43 66 43 68A2.400 2.400 0 0 1 38.500 68C38.500 66 40 62 42 58Z" fill="#7a4904"/>
      <g transform="translate(55 22) rotate(-16)"><ellipse cx="-3" cy="-7.500" rx="7.500" ry="4.500" transform="rotate(-24 -3 -7.500)" fill="#fff" fill-opacity=".85" ${s(1.1)}/><ellipse cx="4" cy="-8" rx="6.500" ry="4" transform="rotate(16 4 -8)" fill="#fff" fill-opacity=".85" ${s(1.1)}/><ellipse rx="10" ry="6.500" fill="#f9d24a" ${s(1.5)}/><path d="M-4-6V6M1-6.500V6.500M6-5V5" stroke="${INK}" stroke-width="2.300"/><circle cx="-10" cy="-1" r="3.800" fill="${INK}"/><path d="M-12-4Q-15-8-17-6M-10-4.500Q-12-9-14-9" fill="none" stroke="${INK}" stroke-width=".9"/></g>`;
  })(),
  // вовна: вівця
  white: `<path d="M26 52V70M34 54V72M50 54V72M58 52V70" stroke="${INK}" stroke-width="3.200" stroke-linecap="round"/>
    <path d="M18 44C10 40 12 30 20 30 20 21 31 19 35 24 39 17 51 18 53 25 60 21 69 28 65 35 72 38 71 49 63 50 62 58 51 60 46 56 41 62 30 61 28 55 22 58 15 52 18 44Z" fill="#fffdf4" ${s(1.8)}/>
    <g fill="none" stroke="#9a8e74" stroke-width="1.100" stroke-linecap="round"><path d="M27 34q4-4 6 0t-4 3M42 29q4-4 6 0t-4 3M52 40q4-4 6 0t-4 3M36 44q4-4 6 0t-4 3M24 45q3-3 5 0M46 50q3-3 5 0"/></g>
    <path d="M9 33C4 32 3 41 8 44 11 49 18 48 20 42 22 36 15 31 9 33Z" fill="#33261b" ${s(1.3)}/>
    <path d="M12 33Q6 26 3 30 4 34 9 35" fill="#33261b" ${s(1)}/><circle cx="11" cy="38" r="1.800" fill="#fff"/><circle cx="10.600" cy="38.200" r=".8" fill="#111"/>
    <path d="M66 39q5 2 6 8" fill="none" ${s(2)}/>`,
  // шкіри: розп’ята шкіра на рамі
  orange: `<g stroke="#4d2c0f" stroke-width="3.400" stroke-linecap="round"><path d="M12 16H68M12 68H68M16 11V73M64 11V73"/></g>
    <path d="M12 16H68M12 68H68M16 11V73M64 11V73" stroke="#c99a5e" stroke-width="1.300" fill="none"/>
    <path d="M23 21C31 25 49 25 57 21 56 27 52 31 53 37 54 44 58 48 59 62 51 57 47 58 45 62 42 66 38 66 35 62 33 58 29 57 21 62 22 48 26 44 27 37 28 31 24 27 23 21Z" fill="#f1cf9b" ${s(1.8)}/>
    <path d="M40 27V57" stroke="#a0763f" stroke-width="1.300" stroke-dasharray="3 2"/>
    <g fill="#9c5a1e"><circle cx="33" cy="36" r="2.100"/><circle cx="47" cy="33" r="1.600"/><circle cx="46" cy="47" r="2.400"/><circle cx="33" cy="50" r="1.500"/></g>
    <path d="M16 16 23 21M64 16 57 21M16 68 21 62M64 68 59 62M40 16V24M40 68V63M16 42H27M64 42H53" stroke="${INK}" stroke-width="1"/>`,
  // полотно: сувій із вишитим кінцем
  pink: `<path d="M22 28H71V65L60 72 56 66 49 72 44 66 38 72 33 66H22Z" fill="#fff9ea" ${s(1.8)}/>
    <path d="M26 54H70" stroke="#b3261e" stroke-width="2.200"/><path d="M26 61H68" stroke="#2b1a0d" stroke-width="1.100"/>
    <g fill="#b3261e"><path d="M31 46 34.500 49.500 31 53 27.500 49.500Z"/><path d="M42 46 45.500 49.500 42 53 38.500 49.500Z"/><path d="M53 46 56.500 49.500 53 53 49.500 49.500Z"/><path d="M64 46 67.500 49.500 64 53 60.500 49.500Z"/></g>
    <g stroke="#cbbd9c" stroke-width=".8"><path d="M26 33H70M26 37H70M26 41H70M32 28V45M40 28V45M48 28V45M56 28V45M64 28V45"/></g>
    <rect x="9" y="16" width="20" height="50" rx="9.500" fill="#fff4d8" ${s(1.8)}/>
    <ellipse cx="19" cy="20" rx="9.500" ry="4.200" fill="#fff" ${s(1.4)}/><ellipse cx="19" cy="20" rx="5.200" ry="2.200" fill="none" ${s(1)}/><ellipse cx="19" cy="20" rx="1.800" ry=".9" fill="${INK}"/>
    <path d="M23 26V60M16 28V62" stroke="#c6b58f" stroke-width="1.100"/>`,
  // джокер: круторогий віл на сонці з усіх восьми вантажів
  wild: (() => {
    const cols = ['#b3261e', '#d96b16', '#e6ad12', '#2c7a3a', '#1c5a9e', '#d5689a', '#231c17', '#f8f3e4'];
    let rays = '';
    for (let i = 0; i < 24; i++) {
      const a = i / 24 * Math.PI * 2, b = a + Math.PI * 2 / 48;
      const R = 54, cx = 40, cy = 42;
      rays += `<path d="M${cx} ${cy}L${(cx + R * Math.cos(a)).toFixed(1)} ${(cy + R * Math.sin(a)).toFixed(1)}L${(cx + R * Math.cos(b)).toFixed(1)} ${(cy + R * Math.sin(b)).toFixed(1)}Z" fill="${cols[i % 8]}"/>`;
    }
    return `<clipPath id="sol-p-wildc"><rect x="0" y="0" width="80" height="80"/></clipPath><g clip-path="url(#sol-p-wildc)">${rays}</g>
    <circle cx="40" cy="44" r="25" fill="#f3d67e" ${s(1.3)}/>
    <path d="M26 33C13 33 3 24 3 8 9 17 18 22 30 24ZM54 33C67 33 77 24 77 8 71 17 62 22 50 24Z" fill="#fff4d0" ${s(2)}/>
    <path d="M9 19C14 25 21 29 29 30M71 19C66 25 59 29 51 30" fill="none" stroke="#8d7446" stroke-width="1.100"/>
    <path d="M28 25C33 19 47 19 52 25 56 30 55 38 53 43 52 52 49 62 46 66 43 70 37 70 34 66 31 62 28 52 27 43 25 38 24 30 28 25Z" fill="#5d3519" ${s(2)}/>
    <path d="M22 34C15 30 13 37 17 40 21 43 25 41 27 40ZM58 34C65 30 67 37 63 40 59 43 55 41 53 40Z" fill="#7b4a22" ${s(1.5)}/>
    <path d="M33 26C36 22 44 22 47 26 45 30 43 31 40 31 37 31 35 30 33 26Z" fill="#8e5a2c"/>
    <circle cx="33" cy="40" r="3.200" fill="#fff" ${s(1)}/><circle cx="47" cy="40" r="3.200" fill="#fff" ${s(1)}/><circle cx="33.500" cy="40.500" r="1.400" fill="${INK}"/><circle cx="46.500" cy="40.500" r="1.400" fill="${INK}"/>
    <path d="M33 58C33 53 47 53 47 58 47 67 33 67 33 58Z" fill="#d9ab7f" ${s(1.5)}/><ellipse cx="37" cy="59" rx="1.600" ry="1.300" fill="${INK}"/><ellipse cx="43" cy="59" rx="1.600" ry="1.300" fill="${INK}"/>
    <path d="M40 62 V66" stroke="${INK}" stroke-width="1"/><path d="M36 63 A4.500 4.500 0 1 0 44 63" fill="none" stroke="#e8c04f" stroke-width="2.200"/>`;
  })(),
};

/// Ілюстрована карта. opts: count, ghost, w, h (css px), label (true|false)
let cardN = 0;
function card(kind, o = {}) {
  const c = CARGO[kind];
  const id = 'sol-p-g' + (++cardN);
  const label = o.label !== false;
  const short = kind === 'wild' ? 'віл' : c.name;
  const vb = label ? '0 0 80 104' : '0 0 80 84';
  const H = label ? 104 : 84;
  const onMark = kind === 'white' || kind === 'yellow' || kind === 'pink' ? INK : '#fff7e6';
  const frame = kind === 'wild' ? '#8a6614' : INK;
  const bandFill = kind === 'black' ? '#f0e2bd' : '#f8efd6';
  return `<svg viewBox="${vb}" aria-hidden="true" preserveAspectRatio="xMidYMid meet">
    <defs><linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${c.top}"/><stop offset="1" stop-color="${c.bot}"/></linearGradient></defs>
    <rect x=".8" y=".8" width="78.400" height="${H - 1.6}" rx="6" fill="url(#${id})" stroke="${frame}" stroke-width="1.600"/>
    <rect x="3.500" y="3.500" width="73" height="${H - 7}" rx="4" fill="none" stroke="${kind === 'white' ? '#a3967a' : '#fff'}" stroke-opacity=".55" stroke-width=".9"/>
    <g opacity=".14">${kind === 'white' ? '' : `<path d="M6 12 74 12M6 18 74 18M6 24 74 24" stroke="#000" stroke-width=".6"/>`}</g>
    <g transform="translate(0 ${label ? 3 : 3})">${ILLO[kind]}</g>
    ${label ? `<path d="M3 83Q40 79 77 83V96Q77 101 72 101H8Q3 101 3 96Z" fill="${bandFill}" stroke="${INK}" stroke-width="1.200"/>
    <text x="40" y="96" text-anchor="middle" font-family="Georgia,'Noto Serif','Times New Roman',serif" font-size="${short.length > 6 ? 12.5 : 13.5}" fill="${INK}">${short}</text>` : ''}
    ${kind === 'wild' ? '' : `<g transform="translate(13 13) scale(.62)"><circle r="13" fill="${kind === 'white' ? '#e9dfc4' : '#00000055'}"/>${mark(kind, onMark)}</g>`}
  </svg>`;
}
// ─── Віз на шляху (вид збоку, рух по +x). w — напівширина. pm — позначка гравця на будці. ──────────────
function cart(w, color, pm) {
  const pole = w * .2;
  return `<g>
    <path d="M${w - 1} 12 L${w + pole + 6} 15" stroke="${INK}" stroke-width="3.600" stroke-linecap="round"/>
    <path d="M${-w} 4C${-w - 3}-30 ${w + 3}-30 ${w} 4Z" fill="${color}" stroke="${INK}" stroke-width="3" stroke-linejoin="round"/>
    <path d="M${-w * .55}-13V4M0-17V4M${w * .55}-13V4" stroke="#00000040" stroke-width="1.800"/>
    <rect x="${-w - 3}" y="3" width="${2 * w + 6}" height="8.500" rx="1.500" fill="#6e4722" stroke="${INK}" stroke-width="2.400"/>
    ${[-w * .58, w * .58].map((x) => `<g transform="translate(${x} 15)"><circle r="8.500" fill="#e5d3a6" stroke="${INK}" stroke-width="2.500"/><path d="M-8.500 0H8.500M0-8.500V8.500M-6-6 6 6M6-6-6 6" stroke="${INK}" stroke-width="1.300"/><circle r="2.200" fill="${INK}"/></g>`).join('')}
    ${pm ? `<g transform="translate(0 -6.500) scale(.62)">${pm}</g>` : ''}
  </g>`;
}
// позначки гравців: не лише колір, а й знак (дальтонікам)
const PMARK = [
  `<circle r="7.500" fill="#fff" stroke="${INK}" stroke-width="1.500"/>`,
  `<path d="M0-10 9.500 7H-9.500Z" fill="#fff" stroke="${INK}" stroke-width="1.500" stroke-linejoin="round"/>`,
  `<rect x="-7.500" y="-7.500" width="15" height="15" fill="#fff" stroke="${INK}" stroke-width="1.500"/>`,
  `<path d="M-3-9.500H3V-3H9.500V3H3V9.500H-3V3H-9.500V-3H-3Z" fill="#fff" stroke="${INK}" stroke-width="1.300" stroke-linejoin="round"/>`,
];
return { INK, SEPIA, BONE, CARGO, mark, ILLO, card, cart, PMARK };
})();

// ═══ «Настілка на столі» (малюнки дизайнера d2) ════════════════════════════════════════════════════
const P2 = (() => {
const INK = '#2a160b';
const TYPES = {
  red:    { name: 'сіль',    bg: '#cf2f26', bg2: '#94170f', slot: '#d8352b', glyphC: '#fff', dark:false },
  blue:   { name: 'риба',    bg: '#1f77cf', bg2: '#0f4d94', slot: '#1f73d1', glyphC: '#fff', dark:false },
  green:  { name: 'збіжжя',  bg: '#17894a', bg2: '#0b5a2e', slot: '#0f7f3f', glyphC: '#fff3b0', dark:false },
  black:  { name: 'дьоготь', bg: '#37302b', bg2: '#17120f', slot: '#2b2521', glyphC: '#fff', dark:false },
  yellow: { name: 'мед',     bg: '#f8c51f', bg2: '#d99405', slot: '#fbcb1e', glyphC: '#3a2500', dark:true },
  white:  { name: 'вовна',   bg: '#f6f1e3', bg2: '#d8cfb8', slot: '#fbf8ee', glyphC: '#2a160b', dark:true },
  orange: { name: 'шкіри',   bg: '#ee7f1c', bg2: '#b9520a', slot: '#f07f16', glyphC: '#3a1600', dark:true },
  pink:   { name: 'полотно', bg: '#f06aa6', bg2: '#c23a78', slot: '#f573ae', glyphC: '#3f0a26', dark:true },
  wild:   { name: 'круторогий віл', short: 'віл', bg: '#f4c542', bg2: '#b9791a', slot: '#f4c542', glyphC: '#2a160b', dark:true },
};
const ORDER = ['red','blue','green','black','yellow','white','orange','pink','wild'];

// ── крихітні гліфи для клітинок шляху (бокс -10..10): одна чітка форма, зрозуміла й без кольору ──
const GLYPH = {
  red:   '<path d="M-8 6.5 L-3.4 -1 L0 -7.5 L3.4 -1 L8 6.5 Z"/><path d="M-3.2 3.2 l1.6 -1.6 1.6 1.6 -1.6 1.6z M1.8 1.2 l1.2 -1.2 1.2 1.2 -1.2 1.2z" fill="#d8352b"/>',
  blue:  '<path d="M-8.4 0 C-5 -5.6 2 -5.6 4.6 -.9 L9 -4.6 L9 4.6 L4.6 .9 C2 5.6 -5 5.6 -8.4 0Z"/><circle cx="-4.6" cy="-1.1" r="1.1" fill="#1f73d1"/>',
  green: '<path d="M0 -9 C1.9 -7.5 1.9 -5.5 0 -4 C-1.9 -5.5 -1.9 -7.5 0 -9Z M-4.6 -5.2 C-2.6 -5 -1.2 -3.4 -.9 -1.4 C-2.9 -1.6 -4.3 -3.2 -4.6 -5.2Z M4.6 -5.2 C2.6 -5 1.2 -3.4 .9 -1.4 C2.9 -1.6 4.3 -3.2 4.6 -5.2Z M-4.6 -.4 C-2.6 -.2 -1.2 1.4 -.9 3.4 C-2.9 3.2 -4.3 1.6 -4.6 -.4Z M4.6 -.4 C2.6 -.2 1.2 1.4 .9 3.4 C2.9 3.2 4.3 1.6 4.6 -.4Z"/><rect x="-.7" y="-4" width="1.4" height="13.5" rx=".7"/>',
  black: '<path d="M0 -9 C4 -3.6 6.6 -.6 6.6 3 A6.6 6.6 0 0 1 -6.6 3 C-6.6 -.6 -4 -3.6 0 -9Z"/>',
  yellow:'<path d="M0 -8.6 L7.5 -4.3 V4.3 L0 8.6 L-7.5 4.3 V-4.3Z"/><path d="M0 -4.2 L3.7 -2.1 V2.1 L0 4.2 L-3.7 2.1 V-2.1Z" fill="#fbcb1e"/>',
  white: '<path d="M-6 6 C-9.4 6 -9.6 1 -6.8 .2 C-8 -3.4 -3.6 -5.4 -1.8 -3.4 C-.8 -7 4.6 -6.4 4.8 -2.6 C8.6 -2.8 9.8 3 6.6 4.6 C6.2 7 2.6 7.4 1.4 5.6 C-.2 7.4 -4.2 7.6 -6 6Z"/>',
  orange:'<path d="M-3.4 -8.4 L-.6 -6.4 L2.6 -8.6 L3.4 -4.8 L7.8 -3.2 L5.2 0 L8 3.4 L3.8 4.6 L3 8.6 L-.2 6.2 L-3.6 8.4 L-4 4.6 L-8 3.2 L-5.4 0 L-8 -3.4 L-3.8 -4.8Z"/>',
  pink:  '<path d="M-6.8 -6.8 L-3.4 -6.8 L0 -3.4 L3.4 -6.8 L6.8 -6.8 L6.8 -3.4 L3.4 0 L6.8 3.4 L6.8 6.8 L3.4 6.8 L0 3.4 L-3.4 6.8 L-6.8 6.8 L-6.8 3.4 L-3.4 0 L-6.8 -3.4Z"/>',
  wild:  '<path d="M-9.4 -7.4 C-8.6 -2.6 -6 -1 -3.4 -.8 L3.4 -.8 C6 -1 8.6 -2.6 9.4 -7.4 C10.6 -1.4 7.6 2 4.4 2 C4 5.8 2.6 8 0 8 C-2.6 8 -4 5.8 -4.4 2 C-7.6 2 -10.6 -1.4 -9.4 -7.4Z"/>',
};

// ── Великі малюнки на картах (бокс 100×140: картинка в зоні y 12..104) ──
const s = (d, f, sw = 3.2, stroke = INK) => `<path d="${d}" fill="${f}" stroke="${stroke}" stroke-width="${sw}" stroke-linejoin="round" stroke-linecap="round"/>`;
const CARD_ART = {
  red: `
    <ellipse cx="50" cy="96" rx="34" ry="5.5" fill="#000" opacity=".28"/>
    ${s('M22 94 C16 76 22 58 34 50 L66 50 C78 58 84 76 78 94 C66 99 34 99 22 94Z','#d9b47e')}
    <path d="M30 56 C26 68 27 82 31 92" stroke="#a97f47" stroke-width="2.4" fill="none"/><path d="M70 56 C74 68 73 82 69 92" stroke="#a97f47" stroke-width="2.4" fill="none"/>
    ${s('M27 53 C33 34 42 18 50 16 C58 18 67 34 73 53 C60 60 40 60 27 53Z','#ffffff')}
    <path d="M40 34 l5 -5 5 5 -5 5z M54 28 l4 -4 4 4 -4 4z M56 44 l5 -5 5 5 -5 5z M34 47 l4 -4 4 4 -4 4z M47 45 l3 -3 3 3 -3 3z" fill="#cfdfe9" stroke="#8ea6b6" stroke-width="1"/>
    <path d="M28 54 C40 62 60 62 72 54 L74 60 C60 67 40 67 26 60Z" fill="#a97f47" stroke="${INK}" stroke-width="2.6" stroke-linejoin="round"/>
    <path d="M13 92 l4 -4 4 4 -4 4z M85 86 l3.4 -3.4 3.4 3.4 -3.4 3.4z M84 98 l2.4 -2.4 2.4 2.4 -2.4 2.4z" fill="#fff" stroke="${INK}" stroke-width="1.4"/>`,
  blue: `
    <path d="M6 88 q11 -9 22 0 t22 0 t22 0 t22 0" stroke="#bfe6fb" stroke-width="4" fill="none" stroke-linecap="round"/>
    <path d="M6 99 q11 -9 22 0 t22 0 t22 0 t22 0" stroke="#7fc4ef" stroke-width="4" fill="none" stroke-linecap="round"/>
    <g transform="rotate(-14 50 54)">
      ${s('M8 54 C22 28 56 26 74 48 L94 32 C90 46 90 62 94 76 L74 60 C56 82 22 80 8 54Z','#f3e8c6')}
      ${s('M34 32 L48 18 L58 34Z','#d0b783',2.8)}
      ${s('M40 76 L50 88 L56 72Z','#d0b783',2.8)}
      <path d="M30 40 C26 48 26 60 30 68" stroke="${INK}" stroke-width="2.8" fill="none"/>
      <path d="M38 44 l6 5 -6 5 M38 56 l6 5 -6 5 M48 42 l6 5 -6 5 M48 54 l6 5 -6 5 M58 46 l6 5 -6 5" stroke="#9c8250" stroke-width="2.2" fill="none" stroke-linecap="round"/>
      <circle cx="19" cy="50" r="5" fill="#fff" stroke="${INK}" stroke-width="2.6"/><circle cx="18" cy="50" r="2" fill="${INK}"/>
    </g>`,
  green: `
    <ellipse cx="50" cy="98" rx="26" ry="4.5" fill="#000" opacity=".28"/>
    <g fill="#ffd556" stroke="${INK}" stroke-width="2.4" stroke-linejoin="round">
      <g transform="rotate(-24 50 74)"><path d="M50 74 V22" stroke-width="3.4" fill="none"/>${ear(50,16,.82)}</g>
      <g transform="rotate(24 50 74)"><path d="M50 74 V22" stroke-width="3.4" fill="none"/>${ear(50,16,.82)}</g>
      <g transform="rotate(-11 50 74)"><path d="M50 74 V18" stroke-width="3.4" fill="none"/>${ear(50,10,.9)}</g>
      <g transform="rotate(11 50 74)"><path d="M50 74 V18" stroke-width="3.4" fill="none"/>${ear(50,10,.9)}</g>
      <path d="M50 76 V14" stroke-width="3.6" fill="none"/>${ear(50,6,1)}
    </g>
    <path d="M38 96 C42 86 46 80 50 72 C54 80 58 86 62 96" fill="#e3b53a" stroke="${INK}" stroke-width="2.8"/>
    ${s('M36 68 C44 74 56 74 64 68 L66 77 C56 83 44 83 34 77Z','#b3261e',2.8)}`,
  black: `
    <ellipse cx="50" cy="98" rx="30" ry="5" fill="#000" opacity=".5"/>
    ${s('M26 34 C22 52 22 70 26 90 C40 97 60 97 74 90 C78 70 78 52 74 34 C60 28 40 28 26 34Z','#a26a35')}
    <path d="M38 30 C35 50 35 76 38 94 M50 28 V96 M62 30 C65 50 65 76 62 94" stroke="#6f4217" stroke-width="2.2" fill="none"/>
    <path d="M23.6 46 C40 53 60 53 76.4 46 M23.4 76 C40 83 60 83 76.6 76" stroke="${INK}" stroke-width="5.4" fill="none"/>
    <path d="M23.6 46 C40 53 60 53 76.4 46 M23.4 76 C40 83 60 83 76.6 76" stroke="#c8ccd1" stroke-width="2.4" fill="none"/>
    <ellipse cx="50" cy="34" rx="24" ry="7" fill="#111" stroke="${INK}" stroke-width="3"/>
    <ellipse cx="46" cy="33" rx="12" ry="2.6" fill="#55504c"/>
    ${s('M68 36 C72 44 77 50 77 58 A8 8 0 0 1 61 58 C61 50 66 44 68 36Z','#0d0b0a',2.8,'#f4efe6')}
    <path d="M65 57 a3.6 3.6 0 0 0 3 4" stroke="#fff" stroke-width="2" fill="none" stroke-linecap="round"/>`,
  yellow: `
    <ellipse cx="50" cy="98" rx="27" ry="4.5" fill="#000" opacity=".22"/>
    ${s('M30 50 C18 60 16 84 34 95 C44 99 56 99 66 95 C84 84 82 60 70 50Z','#c8743c')}
    <path d="M24 72 C40 80 60 80 76 72" stroke="#f7e1b4" stroke-width="3.2" fill="none"/>
    <path d="M34 78 l3 3 3 -3 3 3 3 -3 3 3 3 -3 3 3 3 -3 3 3 3 -3" stroke="#7a3b16" stroke-width="2" fill="none"/>
    ${s('M28 42 H72 L70 54 H30Z','#b5612e')}
    ${s('M34 44 C34 36 66 36 66 44 C68 58 62 60 60 66 C58 72 53 72 52 66 C52 60 46 60 44 56 C40 58 36 56 34 44Z','#ffbf14')}
    <path d="M42 45 C44 41 52 41 56 43" stroke="#ffe78a" stroke-width="2.8" fill="none" stroke-linecap="round"/>
    <g transform="translate(74 22)"><ellipse cx="-3" cy="-7" rx="6" ry="3.6" fill="#fff" stroke="${INK}" stroke-width="1.8" transform="rotate(-20 -3 -7)"/><ellipse cx="4" cy="-7" rx="6" ry="3.6" fill="#fff" stroke="${INK}" stroke-width="1.8" transform="rotate(16 4 -7)"/><ellipse cx="0" cy="0" rx="9" ry="6.4" fill="#ffb300" stroke="${INK}" stroke-width="2.2"/><path d="M-3 -6 V6 M2.6 -6 V6" stroke="${INK}" stroke-width="2.6"/></g>`,
  white: `
    <ellipse cx="50" cy="98" rx="32" ry="4.5" fill="#000" opacity=".16"/>
    <path d="M32 84 V96 M44 86 V97 M60 86 V97 M70 84 V96" stroke="${INK}" stroke-width="5.4" stroke-linecap="round"/>
    ${s('M22 80 C8 76 8 58 20 54 C14 40 32 28 42 36 C46 22 70 24 72 38 C86 34 96 54 84 62 C92 74 80 86 70 82 C62 92 44 92 38 84 C32 90 24 88 22 80Z','#ffffff')}
    <path d="M30 56 c4 -6 11 -3 9 3 M50 46 c4 -6 11 -3 9 3 M58 68 c4 -6 11 -3 9 3 M38 72 c4 -6 11 -3 9 3 M70 52 c4 -6 11 -3 9 3" stroke="#a79c82" stroke-width="2.4" fill="none" stroke-linecap="round"/>
    ${s('M10 44 C10 34 26 32 28 44 C30 56 22 64 18 64 C14 64 8 56 10 44Z','#3b2a20')}
    <path d="M9 42 C4 38 2 42 4 47 M28 42 C33 38 35 42 33 47" stroke="#3b2a20" stroke-width="4.6" fill="none" stroke-linecap="round"/>
    <circle cx="15" cy="47" r="2.6" fill="#fff"/><circle cx="23" cy="47" r="2.6" fill="#fff"/><circle cx="15.4" cy="47.4" r="1.2" fill="#000"/><circle cx="22.6" cy="47.4" r="1.2" fill="#000"/>`,
  orange: `
    <g stroke="${INK}" stroke-width="3.4" stroke-linecap="round"><path d="M14 18 V100 M86 18 V100 M8 24 H92 M8 94 H92" stroke="#6f3d17" stroke-width="5.4"/></g>
    <path d="M14 18 V100 M86 18 V100 M8 24 H92 M8 94 H92" stroke="${INK}" stroke-width="1.2" opacity=".55" fill="none"/>
    ${s('M36 26 L44 34 C48 32 52 32 56 34 L64 26 L66 40 C72 44 76 44 82 44 C80 52 78 54 74 58 C76 66 76 70 82 78 C74 80 70 80 66 80 L60 92 L50 86 L40 92 L34 80 C30 80 26 80 18 78 C24 70 24 66 26 58 C22 54 20 52 18 44 C24 44 28 44 34 40Z','#a65c24')}
    <path d="M40 44 C47 50 53 50 60 44 M36 58 C46 66 54 66 64 58 M42 72 C47 76 53 76 58 72" stroke="#e7a763" stroke-width="2.6" fill="none" stroke-linecap="round"/>
    <path d="M36 28 L14 22 M64 28 L86 22 M18 44 L14 44 M82 44 L86 44 M18 78 L14 78 M82 78 L86 78 M40 92 L30 94 M60 92 L70 94" stroke="#fff4d8" stroke-width="1.8"/>`,
  pink: `
    <ellipse cx="50" cy="98" rx="33" ry="4.5" fill="#000" opacity=".22"/>
    ${s('M36 28 H84 C92 28 92 62 84 62 H36Z','#fdf7e8')}
    ${s('M16 28 H36 C44 28 44 62 36 62 H16 C8 62 8 28 16 28Z','#fffdf4')}
    <ellipse cx="17" cy="45" rx="6" ry="16" fill="#ece1c6" stroke="${INK}" stroke-width="2.8"/>
    <path d="M16 36 C20 40 20 50 16 54" stroke="${INK}" stroke-width="1.8" fill="none"/>
    ${s('M36 62 C44 62 44 28 36 28 V 96 L62 92 V 62Z','#fdf7e8')}
    ${stitchBand(38,70,23,'#c5192b','#1b1b1b')}
    ${stitchBand(38,82,23,'#1b1b1b','#c5192b')}
    ${stitchBand(46,40,36,'#c5192b','#1b1b1b')}`,
  wild: `
    <g>
      ${[0,1,2,3,4,5,6,7].map(i=>{const c=['#cf2f26','#1f77cf','#17894a','#37302b','#f8c51f','#f6f1e3','#ee7f1c','#f06aa6'][i];const a0=-Math.PI/2+i*Math.PI/4-Math.PI/8,a1=a0+Math.PI/4;const R=80;return `<path d="M50 56 L${(50+R*Math.cos(a0)).toFixed(1)} ${(56+R*Math.sin(a0)).toFixed(1)} A${R} ${R} 0 0 1 ${(50+R*Math.cos(a1)).toFixed(1)} ${(56+R*Math.sin(a1)).toFixed(1)}Z" fill="${c}"/>`}).join('')}
    </g>
    <circle cx="50" cy="56" r="39" fill="#f4c542" stroke="${INK}" stroke-width="3"/>
    <circle cx="50" cy="56" r="34" fill="none" stroke="#b9791a" stroke-width="2" stroke-dasharray="3 4"/>
    ${s('M36 40 C24 40 12 34 10 14 C22 24 32 26 42 30Z','#fff3d0')}
    ${s('M64 40 C76 40 88 34 90 14 C78 24 68 26 58 30Z','#fff3d0')}
    <path d="M10 14 C12 20 14 24 17 27 C14 24 12 22 10 14Z M90 14 C88 20 86 24 83 27" stroke="${INK}" stroke-width="4.6" stroke-linecap="round"/>
    ${s('M30 40 C24 36 18 40 20 46 C24 50 28 50 33 47Z','#8a5324')}
    ${s('M70 40 C76 36 82 40 80 46 C76 50 72 50 67 47Z','#8a5324')}
    ${s('M32 34 C40 26 60 26 68 34 C70 48 66 60 62 70 C62 82 56 88 50 88 C44 88 38 82 38 70 C34 60 30 48 32 34Z','#a46a35')}
    <path d="M40 30 C46 36 54 36 60 30 C56 28 44 28 40 30Z" fill="#6f4217"/>
    ${s('M38 70 C42 66 58 66 62 70 C62 82 56 88 50 88 C44 88 38 82 38 70Z','#e8c79b')}
    <ellipse cx="44.6" cy="77" rx="2.4" ry="3.2" fill="${INK}"/><ellipse cx="55.4" cy="77" rx="2.4" ry="3.2" fill="${INK}"/>
    <circle cx="41" cy="50" r="5.6" fill="#fff" stroke="${INK}" stroke-width="2.4"/><circle cx="59" cy="50" r="5.6" fill="#fff" stroke="${INK}" stroke-width="2.4"/>
    <circle cx="42" cy="51" r="2.6" fill="${INK}"/><circle cx="58" cy="51" r="2.6" fill="${INK}"/>
    <path d="M34 42 l9 2.6 M66 42 l-9 2.6" stroke="${INK}" stroke-width="3.4" stroke-linecap="round"/>`,
};
function ear(x, y, k) {
  let o = '';
  for (let i = 0; i < 4; i++) {
    const yy = y + 4 + i * 8 * k;
    o += `<path d="M${x} ${yy+10*k} C${x-9*k} ${yy+8*k} ${x-10*k} ${yy-2*k} ${x-8*k} ${yy-3*k} C${x-3*k} ${yy-1*k} ${x} ${yy+4*k} ${x} ${yy+10*k}Z"/><path d="M${x} ${yy+10*k} C${x+9*k} ${yy+8*k} ${x+10*k} ${yy-2*k} ${x+8*k} ${yy-3*k} C${x+3*k} ${yy-1*k} ${x} ${yy+4*k} ${x} ${yy+10*k}Z"/>`;
  }
  o += `<path d="M${x} ${y+7*k} C${x-4*k} ${y+2*k} ${x-3*k} ${y-5*k} ${x} ${y-8*k} C${x+3*k} ${y-5*k} ${x+4*k} ${y+2*k} ${x} ${y+7*k}Z"/>`;
  return o;
}
function stitchBand(x0, y, w, c1, c2) {
  let o = ''; const st = 5;
  for (let i = 0; i < Math.floor(w / st); i++) {
    const x = x0 + i * st, c = i % 2 ? c2 : c1;
    o += `<path d="M${x} ${y} l3.6 3.6 M${x+3.6} ${y} l-3.6 3.6" stroke="${c}" stroke-width="1.8" stroke-linecap="round"/>`;
  }
  return o;
}
const CARD_PATTERN = {
  red: '<path d="M0 0 l4 4 4 -4 -4 -4z" fill="#fff" opacity=".12"/>',
};
function cardDefs() {
  let o = '';
  // патерн-тла: кожен тип має ще й власну фактуру — зрозуміло й без кольору
  const pat = {
    red: `<pattern id="sol-b-cp-red" width="18" height="18" patternUnits="userSpaceOnUse"><path d="M4 4 l3 3 -3 3 -3 -3z M13 12 l3 3 -3 3 -3 -3z" fill="#fff" opacity=".2"/></pattern>`,
    blue: `<pattern id="sol-b-cp-blue" width="24" height="12" patternUnits="userSpaceOnUse"><path d="M0 6 q6 -5 12 0 t12 0" stroke="#fff" stroke-width="1.6" fill="none" opacity=".18"/></pattern>`,
    green: `<pattern id="sol-b-cp-green" width="11" height="11" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="5" height="11" fill="#fff" opacity=".07"/></pattern>`,
    black: `<pattern id="sol-b-cp-black" width="12" height="12" patternUnits="userSpaceOnUse"><circle cx="3" cy="3" r="1.1" fill="#fff" opacity=".13"/><circle cx="9" cy="9" r="1.1" fill="#fff" opacity=".13"/></pattern>`,
    yellow: `<pattern id="sol-b-cp-yellow" width="20" height="17.3" patternUnits="userSpaceOnUse"><path d="M5 0 h10 l5 8.66 -5 8.66 h-10 l-5 -8.66z" fill="none" stroke="#a86a00" stroke-width="1.4" opacity=".28"/></pattern>`,
    white: `<pattern id="sol-b-cp-white" width="18" height="18" patternUnits="userSpaceOnUse"><path d="M3 9 c2 -5 7 -3 5 1 M12 16 c2 -5 7 -3 5 1" stroke="#a79c82" stroke-width="1.5" fill="none" opacity=".32"/></pattern>`,
    orange: `<pattern id="sol-b-cp-orange" width="14" height="14" patternUnits="userSpaceOnUse"><path d="M2 7 h6" stroke="#fff" stroke-width="1.7" stroke-linecap="round" opacity=".2"/></pattern>`,
    pink: `<pattern id="sol-b-cp-pink" width="12" height="12" patternUnits="userSpaceOnUse"><path d="M3 3 l4 4 M7 3 l-4 4" stroke="#fff" stroke-width="1.3" opacity=".3"/></pattern>`,
    wild: '',
  };
  o += Object.values(pat).join('');
  for (const k of ORDER) {
    const t = TYPES[k];
    o += `<linearGradient id="sol-b-cg-${k}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${t.bg}"/><stop offset="1" stop-color="${t.bg2}"/></linearGradient>`;
    o += `<symbol id="sol-b-card-${k}" viewBox="0 0 100 140">
      <rect x="1" y="1" width="98" height="138" rx="9" fill="#fbf4e4" stroke="#7a5a3a" stroke-width="1"/>
      <rect x="5" y="5" width="90" height="130" rx="6" fill="url(#sol-b-cg-${k})"/>
      ${pat[k] ? `<rect x="5" y="5" width="90" height="130" rx="6" fill="url(#sol-b-cp-${k})"/>` : ''}
      <clipPath id="sol-b-cc-${k}"><rect x="5" y="5" width="90" height="130" rx="6"/></clipPath>
      <g clip-path="url(#sol-b-cc-${k})">${CARD_ART[k]}</g>
      <rect x="5" y="108" width="90" height="27" fill="#fbf4e4"/><rect x="5" y="108" width="90" height="2" fill="${INK}" opacity=".5"/>
      <rect x="5" y="5" width="90" height="130" rx="6" fill="none" stroke="${INK}" stroke-width="1.6"/>
    </symbol>`;
  }
  for (const k of ORDER) o += `<symbol id="sol-b-gl-${k}" viewBox="-10 -10 20 20" overflow="visible"><g fill="${TYPES[k].glyphC}">${GLYPH[k]}</g></symbol>`;
  return o;
}
const card = (k, cls = '') => `<svg class="cardart ${cls}" viewBox="0 0 100 140"><use href="#sol-b-card-${k}"/></svg>`;
function teaGlass(size = 150) {
  return `<svg viewBox="0 0 200 200" width="${size}" height="${size}" style="overflow:visible">
    <ellipse cx="108" cy="110" rx="76" ry="74" fill="#000" opacity=".38" filter="url(#sol-b-blur6)"/>
    <ellipse cx="100" cy="100" rx="80" ry="80" fill="#f1ece0"/><ellipse cx="100" cy="100" rx="80" ry="80" fill="url(#sol-b-saucer)"/>
    <circle cx="100" cy="100" r="74" fill="none" stroke="#2c6fbb" stroke-width="2.4"/><circle cx="100" cy="100" r="70" fill="none" stroke="#2c6fbb" stroke-width="1" stroke-dasharray="3 5"/>
    <path d="M152 124 C176 124 182 88 156 82" fill="none" stroke="#6c6358" stroke-width="10"/><path d="M152 124 C176 124 182 88 156 82" fill="none" stroke="#d9cfb9" stroke-width="5"/>
    <circle cx="100" cy="100" r="56" fill="#b8ad97"/><circle cx="100" cy="100" r="53" fill="url(#sol-b-tea)"/><circle cx="100" cy="100" r="53" fill="none" stroke="#fff" stroke-width="1.5" opacity=".8"/>
    <g transform="translate(112 112) rotate(20)"><circle r="19" fill="#ffe34d" stroke="#d49e12" stroke-width="2"/><circle r="15" fill="#fff4a8"/>${[0,1,2,3,4,5,6,7].map(i=>`<path d="M0 0 L${(13*Math.cos(i*.785)).toFixed(1)} ${(13*Math.sin(i*.785)).toFixed(1)}" stroke="#e8c02a" stroke-width="1.6"/>`).join('')}</g>
    <path d="M58 76 A52 52 0 0 1 96 50" stroke="#fff" stroke-width="5" fill="none" opacity=".55" stroke-linecap="round"/>
    <path d="M92 104 L22 170" stroke="#3a3128" stroke-width="7" stroke-linecap="round" opacity=".35" transform="translate(4 5)"/>
    <path d="M92 104 L22 170" stroke="#cfd3d7" stroke-width="5.4" stroke-linecap="round"/><ellipse cx="92" cy="104" rx="9" ry="6" fill="#cfd3d7" transform="rotate(-44 92 104)"/>
  </svg>`;
}
function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
function saltScatter(w, h, n, seed = 3, heap = false) {
  const R = rng(seed); let o = '';
  for (let i = 0; i < n; i++) {
    const g = () => (R() + R() + R()) / 3;
    const x = g() * w, y = g() * h, sz = 1 + R() * R() * 3.4, a = R() * 90;
    o += `<rect x="${(x+0.8).toFixed(1)}" y="${(y+1).toFixed(1)}" width="${sz.toFixed(1)}" height="${sz.toFixed(1)}" transform="rotate(${a.toFixed(0)} ${x.toFixed(1)} ${y.toFixed(1)})" fill="#000" opacity=".45"/><rect x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${sz.toFixed(1)}" height="${sz.toFixed(1)}" transform="rotate(${a.toFixed(0)} ${x.toFixed(1)} ${y.toFixed(1)})" fill="#fbfaf5"/>`;
  }
  return `<svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}" style="overflow:visible">${o}</svg>`;
}
return { INK, TYPES, GLYPH, cardDefs, card, teaGlass, saltScatter };
})();

// ═══ «Петриківка» (малюнки дизайнера d3) ═══════════════════════════════════════════════════════════
const P3 = (() => {
const f1 = (v) => Math.round(v * 10) / 10;
function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
function mix(a, b, k) { const p = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16)); const A = p(a), B = p(b); return '#' + A.map((v, i) => Math.round(v + (B[i] - v) * k).toString(16).padStart(2, '0')).join(''); }

// мазок: тонкий початок, круглий кінець — основний жест петриківки
function petalD(L, W, bend = 0) {
  const b = bend * L;
  return `M0 0C${f1(L * .3)} ${f1(-W * .16 + b * .4)} ${f1(L * .55)} ${f1(-W * .62 + b)} ${f1(L * .8)} ${f1(-W * .5 + b)}C${f1(L * 1.03)} ${f1(-W * .36 + b)} ${f1(L * 1.03)} ${f1(W * .38 + b)} ${f1(L * .8)} ${f1(W * .5 + b)}C${f1(L * .55)} ${f1(W * .62 + b)} ${f1(L * .3)} ${f1(W * .16 + b * .4)} 0 0Z`;
}
function petal(x, y, deg, L, W, fill, opt = {}) {
  const { hi, bend = 0 } = opt;
  let s = `<g transform="translate(${f1(x)} ${f1(y)}) rotate(${f1(deg)})"><path d="${petalD(L, W, bend)}" fill="${fill}"/>`;
  if (hi) s += `<path d="M${f1(L * .28)} ${f1(-W * .02)}C${f1(L * .5)} ${f1(-W * .34)} ${f1(L * .72)} ${f1(-W * .38)} ${f1(L * .9)} ${f1(-W * .18)}C${f1(L * .72)} ${f1(-W * .18)} ${f1(L * .5)} ${f1(-W * .14)} ${f1(L * .28)} ${f1(-W * .02)}Z" fill="${hi}"/>`;
  return s + '</g>';
}
// пензлевий стебельний штрих: кубічна крива зі змінною шириною
function brush(p0, p1, p2, p3, w0, w1, fill) {
  const n = 28, L = [], Rr = [];
  for (let i = 0; i <= n; i++) {
    const t = i / n, u = 1 - t;
    const x = u * u * u * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t * t * t * p3[0];
    const y = u * u * u * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t * t * t * p3[1];
    const dx = 3 * u * u * (p1[0] - p0[0]) + 6 * u * t * (p2[0] - p1[0]) + 3 * t * t * (p3[0] - p2[0]);
    const dy = 3 * u * u * (p1[1] - p0[1]) + 6 * u * t * (p2[1] - p1[1]) + 3 * t * t * (p3[1] - p2[1]);
    const l = Math.hypot(dx, dy) || 1, w = (w0 + (w1 - w0) * t) / 2;
    L.push([x - dy / l * w, y + dx / l * w]); Rr.unshift([x + dy / l * w, y - dx / l * w]);
  }
  return `<path d="M${L.concat(Rr).map((q) => f1(q[0]) + ' ' + f1(q[1])).join('L')}Z" fill="${fill}"/>`;
}
function bezAt(p0, p1, p2, p3, t) { const u = 1 - t; const x = u * u * u * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t * t * t * p3[0], y = u * u * u * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t * t * t * p3[1]; const dx = 3 * u * u * (p1[0] - p0[0]) + 6 * u * t * (p2[0] - p1[0]) + 3 * t * t * (p3[0] - p2[0]), dy = 3 * u * u * (p1[1] - p0[1]) + 6 * u * t * (p2[1] - p1[1]) + 3 * t * t * (p3[1] - p2[1]); return [x, y, Math.atan2(dy, dx) * 180 / Math.PI]; }

const P = { red: '#d9301f', red2: '#ff6a3d', yel: '#f6bf26', yel2: '#ffe08a', org: '#ef7d1a', grn: '#2f9e4a', grn2: '#7ad36b', blu: '#2a6fd0', blu2: '#7cc0ff', cream: '#fbf1da', ink: '#0a0812', pink: '#ef6aa2' };
const PAL = P;

// квітка-«жоржина»: два кола мазків, «пальчикові» крапки
function flower(x, y, r, pal = {}) {
  const a = pal.a || P.red, ah = pal.ah || P.red2, b = pal.b || P.yel, bh = pal.bh || P.yel2, c = pal.c || P.ink, dot = pal.dot || P.cream;
  const n = pal.n || 11; let s = '';
  for (let i = 0; i < n; i++) s += petal(x + Math.cos(i / n * 6.283) * r * .2, y + Math.sin(i / n * 6.283) * r * .2, i / n * 360, r * .82, r * .46, a, { hi: ah });
  const m = Math.round(n * .75);
  for (let i = 0; i < m; i++) s += petal(x, y, (i + .5) / m * 360, r * .56, r * .26, b, { hi: bh });
  s += `<circle cx="${f1(x)}" cy="${f1(y)}" r="${f1(r * .19)}" fill="${c}"/>`;
  for (let i = 0; i < 7; i++) s += `<circle cx="${f1(x + Math.cos(i / 7 * 6.283) * r * .115)}" cy="${f1(y + Math.sin(i / 7 * 6.283) * r * .115)}" r="${f1(r * .03)}" fill="${dot}"/>`;
  return s;
}
// соняшник: загострені пелюстки, темний диск зі спіраллю насіння
function sunflower(x, y, r, pal = {}) {
  const a = pal.a || P.yel, ah = pal.ah || P.yel2, b = pal.b || P.org, disc = pal.disc || '#2a1408', seed = pal.seed || P.yel; let s = '';
  const n = pal.n || 19;
  for (let i = 0; i < n; i++) { const d = (i + .5) / n * 360; s += `<g transform="translate(${f1(x)} ${f1(y)}) rotate(${f1(d)})"><path d="M${f1(r * .42)} ${f1(-r * .13)}Q${f1(r * .8)} ${f1(-r * .19)} ${f1(r * 1.02)} 0Q${f1(r * .8)} ${f1(r * .19)} ${f1(r * .42)} ${f1(r * .13)}Z" fill="${b}"/></g>`; }
  for (let i = 0; i < n; i++) { const d = i / n * 360; s += `<g transform="translate(${f1(x)} ${f1(y)}) rotate(${f1(d)})"><path d="M${f1(r * .4)} ${f1(-r * .12)}Q${f1(r * .7)} ${f1(-r * .17)} ${f1(r * .88)} 0Q${f1(r * .7)} ${f1(r * .17)} ${f1(r * .4)} ${f1(r * .12)}Z" fill="${a}"/><path d="M${f1(r * .46)} ${f1(-r * .05)}Q${f1(r * .66)} ${f1(-r * .09)} ${f1(r * .78)} 0Q${f1(r * .66)} ${f1(-r * .03)} ${f1(r * .46)} ${f1(r * .02)}Z" fill="${ah}"/></g>`; }
  s += `<circle cx="${f1(x)}" cy="${f1(y)}" r="${f1(r * .46)}" fill="${disc}"/><circle cx="${f1(x)}" cy="${f1(y)}" r="${f1(r * .46)}" fill="none" stroke="${b}" stroke-width="${f1(r * .022)}"/>`;
  for (let i = 1; i < 90; i++) { const rr = r * .43 * Math.sqrt(i / 90), t = i * 2.39996; s += `<circle cx="${f1(x + Math.cos(t) * rr)}" cy="${f1(y + Math.sin(t) * rr)}" r="${f1(r * .012 + r * .012 * i / 90)}" fill="${i < 26 ? (pal.seed2 || P.org) : seed}"/>`; }
  return s;
}
// калина: гроно ягід з бликом і чорною цяткою, зубчасті листки
function kalyna(x, y, r, seed = 3, pal = {}) {
  const R = rng(seed), a = pal.a || P.red, hi = pal.hi || P.red2, d = pal.d || P.ink; let s = '';
  const pts = []; let tries = 0;
  while (pts.length < (pal.n || 13) && tries++ < 400) { const t = R() * 6.283, q = Math.sqrt(R()) * r * .78; const px = x + Math.cos(t) * q, py = y + Math.sin(t) * q * .86; if (pts.every((p) => Math.hypot(p[0] - px, p[1] - py) > r * .34)) pts.push([px, py]); }
  for (const [px, py] of pts) { const b = r * .19; s += `<circle cx="${f1(px)}" cy="${f1(py)}" r="${f1(b)}" fill="${a}"/><path d="M${f1(px - b * .6)} ${f1(py - b * .2)}A${f1(b * .7)} ${f1(b * .7)} 0 0 1 ${f1(px + b * .1)} ${f1(py - b * .65)}" fill="none" stroke="${hi}" stroke-width="${f1(b * .28)}" stroke-linecap="round"/><circle cx="${f1(px + b * .3)}" cy="${f1(py + b * .35)}" r="${f1(b * .15)}" fill="${d}"/>`; }
  return s;
}
// листок-гребінець: мазок із паралельними зубцями
function leaf(x, y, deg, L, W, a = P.grn, b = P.grn2) {
  let s = `<g transform="translate(${f1(x)} ${f1(y)}) rotate(${f1(deg)})"><path d="${petalD(L, W)}" fill="${a}"/>`;
  for (let i = 1; i <= 4; i++) { const t = .22 + i * .14; s += `<path d="M${f1(L * t)} 0L${f1(L * (t + .1))} ${f1(-W * .36)}" stroke="${b}" stroke-width="${f1(W * .07)}" stroke-linecap="round"/>`; }
  s += `<path d="M${f1(L * .1)} 0L${f1(L * .84)} 0" stroke="${b}" stroke-width="${f1(W * .05)}" stroke-linecap="round"/></g>`;
  return s;
}
// пагін: вигнуте стебло, мазки по обидва боки, закрут на кінці
function sprig(p0, p1, p2, p3, opt = {}) {
  const { w = 12, n = 9, L = 90, W = 34, a = P.grn, h = P.grn2, stem = P.grn, curl = true, skip = 0 } = opt; let s = '';
  s += brush(p0, p1, p2, p3, w, w * .25, stem);
  for (let i = skip; i < n; i++) {
    const t = (i + .6) / (n + .6), [x, y, d] = bezAt(p0, p1, p2, p3, t), k = 1 - t * .62;
    s += petal(x, y, d - 42, L * k, W * k, a, { hi: h });
    s += petal(x, y, d + 42, L * k * .92, W * k * .92, a, { hi: h });
  }
  const [x, y, d] = bezAt(p0, p1, p2, p3, 1);
  if (curl) s += petal(x, y, d, L * .34, W * .34, h);
  return s;
}

// ─── Піктограми вантажів: 64×64, плоскі, власний силует кожна ─────
const I = '#1b0f06';
const ICON = {
  red: () => `<path d="M21 23C15 33 10 43 12 51C14 60 46 60 48 51C50 43 45 33 39 23Z" fill="#fff4dc"/><path d="M12 51C14 60 46 60 48 51C49 47 48.6 43 47.4 39C42 46 22 47 12.6 40C11.5 44 11.2 48 12 51Z" fill="#e8d3a8"/><path d="M22 24L38 24L44 13L37 16L30 10L23 16L16 13Z" fill="#fff4dc"/><rect x="20" y="20.5" width="20" height="4.6" rx="2.3" fill="#f6bf26"/><g fill="#a51b10"><path d="M30 31l4.4 4.4L30 39.8l-4.4-4.4z"/><path d="M21 35.4l2.8 2.8L21 41l-2.8-2.8z"/><path d="M39 35.4l2.8 2.8L39 41l-2.8-2.8z"/></g><g fill="#fff"><path d="M53 41l4.5 4.5L53 50l-4.5-4.5z"/><path d="M47 51l3.6 3.6L47 58.2l-3.6-3.6z"/><path d="M56 52l3 3-3 3-3-3z"/></g>`,
  blue: () => `<path d="M5 33C15 14 38 12 49 29L60 19C58 28 58 37 60 46L49 36C38 53 15 52 5 33Z" fill="#fff4dc"/><path d="M5 33C15 44 36 44 49 33C38 53 15 52 5 33Z" fill="#f6bf26"/><g fill="none" stroke="#1a4f9e" stroke-width="2.2" stroke-linecap="round"><path d="M23 22q5 5 0 11"/><path d="M30 21q5 6 0 12"/><path d="M37 23q5 5 0 10"/></g><path d="M17 20q5 7 1 15" fill="none" stroke="#d9301f" stroke-width="3" stroke-linecap="round"/><circle cx="12" cy="29" r="3.2" fill="#1a1a2e"/><circle cx="11" cy="28" r="1" fill="#fff"/><path d="M52 22L60 19 59 25Z" fill="#f6bf26"/><path d="M52 43L60 46 59 40Z" fill="#f6bf26"/>`,
  green: () => { let s = '<path d="M32 62C31 44 31 26 32 10" stroke="#ffe08a" stroke-width="3" stroke-linecap="round" fill="none"/><path d="M16 62C18 50 20 40 24 32" stroke="#ffe08a" stroke-width="2.2" stroke-linecap="round" fill="none"/><path d="M48 62C46 50 44 40 40 32" stroke="#ffe08a" stroke-width="2.2" stroke-linecap="round" fill="none"/>'; for (let i = 0; i < 5; i++) { const y = 42 - i * 7; s += petal(32, y, -62, 13, 7, '#f6bf26', { hi: '#ffe08a' }) + petal(32, y, -118, 13, 7, '#f6bf26', { hi: '#ffe08a' }); } s += petal(32, 11, -90, 11, 6, '#ffe08a'); for (let i = 0; i < 3; i++) { const y = 46 - i * 6, x = 20.5 + i * 1.6; s += petal(x, y, -48, 10, 5.4, '#fff4dc') + petal(x, y, -128, 9, 5, '#fff4dc'); const xx = 43.5 - i * 1.6; s += petal(xx, y, -52, 9, 5, '#fff4dc') + petal(xx, y, -132, 10, 5.4, '#fff4dc'); } return s; },
  black: () => `<path d="M14 26H50L46 58C46 60 18 60 18 58Z" fill="#c9a26a"/><path d="M14 26H50L49 33H15Z" fill="#fff4dc"/><path d="M17 49H47L46 56H18Z" fill="#fff4dc"/><path d="M26 33L27 49M38 33L37 49" stroke="#7a5428" stroke-width="2"/><path d="M12 26C12 8 52 8 52 26" fill="none" stroke="#fff4dc" stroke-width="3.2"/><path d="M32 30C37 38 41 42 41 47C41 52 37 56 32 56C27 56 23 52 23 47C23 42 27 38 32 30Z" fill="#0a0812" stroke="#f6bf26" stroke-width="2.2"/><path d="M28 47q0 4 4 5" fill="none" stroke="#f6bf26" stroke-width="2" stroke-linecap="round"/>`,
  yellow: () => { const hx = (x, y, r, f) => { let d = ''; for (let i = 0; i < 6; i++) { const a = i / 6 * 6.283 + .5236; d += (i ? 'L' : 'M') + f1(x + Math.cos(a) * r) + ' ' + f1(y + Math.sin(a) * r); } return `<path d="${d}Z" fill="${f}" stroke="${I}" stroke-width="2.4" stroke-linejoin="round"/>`; }; return hx(22, 22, 11.5, '#fff4dc') + hx(42, 22, 11.5, '#e8840f') + hx(32, 39.5, 11.5, '#e8840f') + hx(12, 39.5, 11.5, '#e8840f') + hx(52, 39.5, 11.5, '#fff4dc') + `<g transform="translate(35 16) rotate(-14)"><ellipse cx="0" cy="0" rx="10" ry="7" fill="${I}"/><path d="M-4-6.4v12.8M2-6.6v13.2" stroke="#ffe08a" stroke-width="2.8"/><ellipse cx="-3" cy="-9" rx="5" ry="3.4" fill="#fff" transform="rotate(-24 -3 -9)"/><ellipse cx="4" cy="-9" rx="5" ry="3.4" fill="#fff" transform="rotate(20 4 -9)"/></g>`; },
  white: () => { const puff = [[20, 24, 10], [32, 18, 11], [45, 23, 10], [53, 33, 9], [47, 44, 10], [33, 48, 11], [20, 44, 10], [14, 34, 9], [31, 33, 15]]; let s = puff.map(([x, y, r]) => `<circle cx="${x}" cy="${y}" r="${r + 2.6}" fill="${I}"/>`).join('') + puff.map(([x, y, r]) => `<circle cx="${x}" cy="${y}" r="${r}" fill="#fff"/>`).join(''); s += '<g fill="none" stroke="#b9a988" stroke-width="2" stroke-linecap="round"><path d="M24 26q4-4 7 0"/><path d="M36 22q4-3 6 1"/><path d="M42 40q4-3 6 1"/><path d="M28 42q4-3 6 1"/></g>'; s += `<path d="M6 30C4 42 14 50 22 42C26 36 22 26 14 25C10 25 7 27 6 30Z" fill="${I}"/><circle cx="10" cy="33" r="1.9" fill="#fff"/><circle cx="17" cy="32" r="1.9" fill="#fff"/><path d="M6 27C0 24 0 17 6 16C9 16 11 19 9 21" fill="none" stroke="${I}" stroke-width="3.2" stroke-linecap="round"/><path d="M20 26C24 20 22 14 17 14C14 14 13 17 15 19" fill="none" stroke="${I}" stroke-width="3.2" stroke-linecap="round"/>`; s += `<path d="M22 58v4M42 58v4" stroke="${I}" stroke-width="4" stroke-linecap="round"/>`; return s; },
  orange: () => `<path d="M6 8L18 14M58 8L46 14M6 58L18 50M58 58L46 50" stroke="#fff4dc" stroke-width="3" stroke-linecap="round"/><path d="M18 12C22 16 42 16 46 12C50 14 52 18 50 22C46 26 46 30 50 34C53 40 50 48 46 52C42 48 22 48 18 52C14 48 11 40 14 34C18 30 18 26 14 22C12 18 14 14 18 12Z" fill="#5a2208"/><path d="M21 17C25 20 39 20 43 17C45 18 46 20 45 22C41 26 41 32 45 36C47 40 45 44 43 47C38 44 26 44 21 47C19 44 17 40 19 36C23 32 23 26 19 22C18 20 19 18 21 17Z" fill="#b5581c"/><g fill="none" stroke="#fff4dc" stroke-width="2" stroke-linecap="round" stroke-dasharray="3.4 3.8"><path d="M20 19.5C25 23 39 23 44 19.5"/><path d="M20 44.5C25 41 39 41 44 44.5"/></g><path d="M32 25l5 7-5 7-5-7z" fill="#fff4dc"/>`,
  pink: () => `<path d="M12 14H42V42H12Z" fill="#fff4dc"/><path d="M12 42H42V58L37 54L32 58L27 54L22 58L17 54L12 58Z" fill="#fff4dc"/><path d="M12 42H42V45H12Z" fill="#e8d3a8"/><g fill="#b3132e"><rect x="12" y="20" width="30" height="2.8"/><rect x="12" y="35" width="30" height="2.8"/><path d="M18 24.8l4 3.7-4 3.7-4-3.7z"/><path d="M27 24.8l4 3.7-4 3.7-4-3.7z"/><path d="M36 24.8l4 3.7-4 3.7-4-3.7z"/></g><g fill="#1b0f06"><path d="M18 26.8l1.8 1.7-1.8 1.7-1.8-1.7z"/><path d="M27 26.8l1.8 1.7-1.8 1.7-1.8-1.7z"/><path d="M36 26.8l1.8 1.7-1.8 1.7-1.8-1.7z"/></g><rect x="40" y="11" width="13" height="40" rx="6.5" fill="#fff4dc" stroke="${I}" stroke-width="2.2"/><ellipse cx="46.5" cy="13.4" rx="6" ry="2.6" fill="#e8d3a8" stroke="${I}" stroke-width="1.8"/><circle cx="46.5" cy="13.4" r="1.2" fill="${I}"/>`,
  wild: () => `<path d="M21 26C8 26 1 15 6 3C9 12 15 16 25 17Z" fill="#ffe08a"/><path d="M43 26C56 26 63 15 58 3C55 12 49 16 39 17Z" fill="#ffe08a"/><path d="M12 25C7 22 4 16 5 9C7 14 12 19 20 20Z" fill="#c98a1d"/><path d="M52 25C57 22 60 16 59 9C57 14 52 19 44 20Z" fill="#c98a1d"/><path d="M9 26C13 21 19 22 22 26L20 31C15 32 11 30 9 26Z" fill="#7a3a12"/><path d="M55 26C51 21 45 22 42 26L44 31C49 32 53 30 55 26Z" fill="#7a3a12"/><path d="M20 19C26 15 38 15 44 19C47 26 45 34 43 40C42 47 40 54 32 54C24 54 22 47 21 40C19 34 17 26 20 19Z" fill="#a65226"/><path d="M26 16C29 15 35 15 38 16C37 22 35 28 32 32C29 28 27 22 26 16Z" fill="#fff4dc"/><path d="M24 42C24 38 40 38 40 42C42 50 38 57 32 57C26 57 22 50 24 42Z" fill="#fbdcc0"/><ellipse cx="28" cy="47" rx="1.9" ry="2.6" fill="#1b0f06"/><ellipse cx="36" cy="47" rx="1.9" ry="2.6" fill="#1b0f06"/><path d="M28 52a4 4 0 0 0 8 0" fill="none" stroke="#f6bf26" stroke-width="2.4" stroke-linecap="round"/><circle cx="24" cy="30" r="3.2" fill="#fff"/><circle cx="40" cy="30" r="3.2" fill="#fff"/><circle cx="24.6" cy="30.5" r="1.9" fill="#1b0f06"/><circle cx="39.4" cy="30.5" r="1.9" fill="#1b0f06"/><path d="M32 12l1.4 2.6 2.9.2-2.2 1.9.7 2.8-2.8-1.5-2.8 1.5.7-2.8-2.2-1.9 2.9-.2z" fill="#d9301f"/>`,
};
// кольори поля й тексту карт + власний візерунок (для дальтоніків — колір І малюнок І піктограма)
const CARD = {
  red: { f: '#c9281b', d: '#a31d12', t: '#fff4dc', n: 'Сіль' },
  blue: { f: '#1f5fbd', d: '#174c9c', t: '#fff4dc', n: 'Риба' },
  green: { f: '#14865a', d: '#0f7049', t: '#fff4dc', n: 'Збіжжя' },
  black: { f: '#17141d', d: '#2b2734', t: '#f6bf26', n: 'Дьоготь' },
  yellow: { f: '#f6bf26', d: '#e0a511', t: '#1b0f06', n: 'Мед' },
  white: { f: '#f3e9d0', d: '#dfd1ae', t: '#1b0f06', n: 'Вовна' },
  orange: { f: '#e8741a', d: '#c95f0e', t: '#1b0f06', n: 'Шкіри' },
  pink: { f: '#e8609c', d: '#cc4882', t: '#1b0f06', n: 'Полотно' },
  wild: { f: '#2a1450', d: '#3a2068', t: '#ffe08a', n: 'Круторогий віл' },
};
const ORDER = ['red', 'blue', 'green', 'black', 'yellow', 'white', 'orange', 'pink', 'wild'];
// поле карти 120×84: тло, власний візерунок, куточки-мазки, смуга-вишивка, велика піктограма
function pat(c) {
  const d = CARD[c].d;
  switch (c) {
    case 'red': { let s = ''; for (let y = 8; y < 84; y += 14) for (let x = (y % 28 ? 4 : 11); x < 120; x += 14) s += `<path d="M${x} ${y - 3}l3 3-3 3-3-3z"/>`; return `<g fill="${d}">${s}</g>`; }
    case 'blue': { let s = ''; for (let y = 8; y < 84; y += 11) { let p = `M-6 ${y}`; for (let x = -6; x < 126; x += 12) p += `q3 -6 6 0t6 0`; s += `<path d="${p}"/>`; } return `<g fill="none" stroke="${d}" stroke-width="2">${s}</g>`; }
    case 'green': { let s = ''; for (let x = -84; x < 120; x += 13) s += `<path d="M${x} 84L${x + 84} 0"/>`; return `<g stroke="${d}" stroke-width="2.4">${s}</g>`; }
    case 'black': { let s = ''; for (let y = 8; y < 84; y += 12) for (let x = (y % 24 ? 6 : 12); x < 120; x += 12) s += `<circle cx="${x}" cy="${y}" r="1.3"/>`; return `<g fill="${d}">${s}</g>`; }
    case 'yellow': { let s = ''; for (let y = 0; y < 90; y += 16) for (let x = (y % 32 ? 9 : 0); x < 124; x += 18) s += `<path d="M${x} ${y - 7}l6 3.5v7l-6 3.5-6-3.5v-7z"/>`; return `<g fill="none" stroke="${d}" stroke-width="1.8">${s}</g>`; }
    case 'white': { let s = ''; for (let y = 10; y < 84; y += 15) for (let x = (y % 30 ? 6 : 14); x < 120; x += 16) s += `<path d="M${x - 4} ${y}a4 4 0 1 1 4 4"/>`; return `<g fill="none" stroke="${d}" stroke-width="1.8" stroke-linecap="round">${s}</g>`; }
    case 'orange': { let s = ''; for (let y = 8; y < 84; y += 12) s += `<path d="M0 ${y}H120"/>`; return `<g stroke="${d}" stroke-width="1.8" stroke-dasharray="6 5">${s}</g>`; }
    case 'pink': { let s = ''; for (let x = 6; x < 120; x += 13) s += `<rect x="${x}" width="4.4" height="84"/>`; return `<g fill="${d}">${s}</g>`; }
    case 'wild': { let s = ''; const cc = ['#d9301f', '#2a6fd0', '#2f9e4a', '#fff4dc', '#f6bf26', '#ef7d1a', '#ef6aa2', '#8b7bd8']; for (let i = 0; i < 26; i++) { const a = i / 26 * 360; s += petal(60, 26, a, 78, 13, cc[i % 8]); } return `<g opacity=".95">${s}</g><circle cx="60" cy="26" r="27" fill="#2a1450"/>`; }
  }
}
function cardSymbol(c) {
  const k = CARD[c];
  const icon = ICON[c]();
  const dark = c === 'black';
  return `<symbol id="sol-k-card-${c}" viewBox="0 0 120 84"><rect width="120" height="84" fill="${k.f}"/>${pat(c)}<g transform="translate(35.7 2) scale(.76)">${icon}</g>${dark ? '<rect x="1.5" y="1.5" width="117" height="81" rx="6" fill="none" stroke="#f6bf26" stroke-width="2"/>' : ''}</symbol>`;
}
// мікрогліфи слотів — ті самі, що й у кутку карт: 20×20 з центром у 0,0
const GLYPH = {
  red: `<path d="M0-8.5L8.5 0 0 8.5-8.5 0Z" fill="#fff"/>`,
  blue: `<path d="M-10 0C-5-7 3-7 6-1L10-5V5L6 1C3 7-5 7-10 0Z" fill="#fff"/>`,
  green: `<path d="M-7 5L0-2 7 5M-7-1L0-8 7-1" fill="none" stroke="#fff" stroke-width="3.4" stroke-linecap="round" stroke-linejoin="round"/>`,
  black: `<path d="M0-9.5C4-3.5 6.5-1 6.5 2.5A6.5 6.5 0 0 1-6.5 2.5C-6.5-1-4-3.5 0-9.5Z" fill="#f6bf26"/>`,
  yellow: `<path d="M0-9L8-4.5V4.5L0 9-8 4.5V-4.5Z" fill="none" stroke="#1b0f06" stroke-width="3.4" stroke-linejoin="round"/>`,
  white: `<path d="M-5 1a5 5 0 1 1 5 5" fill="none" stroke="#1b0f06" stroke-width="3.6" stroke-linecap="round"/>`,
  orange: `<path d="M-7-7L-3-4H3L7-7 5 0 7 7 3 4H-3L-7 7-5 0Z" fill="#1b0f06"/>`,
  pink: `<path d="M-4-8V8M4-8V8" stroke="#1b0f06" stroke-width="3.6" stroke-linecap="round"/>`,
  wild: `<path d="M-9-7C-9-1-5 1 0 1S9-1 9-7M0 1V8" fill="none" stroke="#ffe08a" stroke-width="3.2" stroke-linecap="round"/>`,
  any: `<circle r="4" fill="#3a352c"/>`,
};
const SLOT = { red: '#e8341f', blue: '#2474e8', green: '#12a862', black: '#121019', yellow: '#fbc924', white: '#f6eeda', orange: '#f5851c', pink: '#f46fab', any: '#9b917d' };
// стрічка-вишивка: повторюваний ромбовий орнамент
function stitchBand(w, h, opts = {}) {
  const s = h / 7, a = opts.a || '#d9301f', b = opts.b || '#f6bf26', c = opts.c || '#fbf1da';
  const rows = ['...a...', '..aba..', '.abcba.', 'abc.cba', '.abcba.', '..aba..', '...a...'];
  const col = { a, b, c }; let out = '';
  for (let X = 0; X < w; X += s * 7) rows.forEach((r, j) => { for (let i = 0; i < 7; i++) { const k = col[r[i]]; if (!k) continue; const x = X + i * s, y = j * s; out += `<path d="M${f1(x + s * .15)} ${f1(y + s * .15)}l${f1(s * .7)} ${f1(s * .7)}m${f1(-s * .7)} 0l${f1(s * .7)} ${f1(-s * .7)}" stroke="${k}" stroke-width="${f1(s * .34)}" stroke-linecap="round"/>`; } });
  return out;
}

// віл із возом: лого-ілюстрація, вісь X вправо, ~220×90
function oxCart(x, y, s = 1) {
  const c = '#fbf1da', d = '#d9301f', g = '#f6bf26', ink = '#0a0812';
  return `<g transform="translate(${x} ${y}) scale(${s})"><g fill="${c}"><path d="M104 22C120 14 150 14 165 22C172 26 176 32 176 40C176 48 172 52 168 54L170 78H160L156 56C144 60 128 60 118 57L114 78H104L104 52C100 48 100 30 104 22Z"/><path d="M170 26C178 22 188 24 196 30C200 34 201 42 198 46C192 50 184 48 176 44Z"/></g><path d="M188 26C196 24 204 16 204 6C198 12 192 15 182 17ZM178 24C176 15 178 8 186 2C186 10 188 15 192 20Z" fill="${g}"/><circle cx="189" cy="33" r="2.2" fill="${ink}"/><g fill="${d}"><path d="M125 26l8 8-8 8-8-8z"/><path d="M146 26l8 8-8 8-8-8z"/></g><g fill="${g}"><path d="M136 41l5 5-5 5-5-5z"/></g><path d="M170 20V52" stroke="${g}" stroke-width="5" stroke-linecap="round"/><path d="M92 50L170 34" stroke="${c}" stroke-width="4"/><g stroke="${ink}" stroke-width="3" stroke-linejoin="round"><path d="M4 40C4 2 90 2 90 40Z" fill="#d9301f"/><path d="M0 40H94L88 54H6Z" fill="#f6bf26"/></g><path d="M24 12V40M47 7V40M70 12V40" stroke="${c}" stroke-width="3"/><g><circle cx="24" cy="62" r="17" fill="${c}" stroke="${ink}" stroke-width="3"/><circle cx="70" cy="62" r="17" fill="${c}" stroke="${ink}" stroke-width="3"/><path d="M24 47V77M9 62H39M13 51L35 73M35 51L13 73M70 47V77M55 62H85M59 51L81 73M81 51L59 73" stroke="${ink}" stroke-width="2.4"/><circle cx="24" cy="62" r="5" fill="${d}"/><circle cx="70" cy="62" r="5" fill="${d}"/></g></g>`;
}
return { f1, rng, mix, petal, brush, flower, sunflower, kalyna, leaf, sprig, ICON, CARD, ORDER, cardSymbol, GLYPH, SLOT, stitchBand, oxCart, PAL };
})();

const GLEK = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128"><defs><linearGradient id="clay" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#d4884a"/><stop offset="1" stop-color="#a65a2a"/></linearGradient></defs><path d="M92 44 C118 44 120 88 94 96" fill="none" stroke="#5a2f14" stroke-width="14" stroke-linecap="round"/><path d="M92 44 C118 44 120 88 94 96" fill="none" stroke="#c5763a" stroke-width="8" stroke-linecap="round"/><path d="M46 22 h36 v10 c14 6 24 22 24 42 c0 22 -14 40 -42 40 c-28 0 -42 -18 -42 -40 c0 -20 10 -36 24 -42 z"fill="url(#clay)" stroke="#5a2f14" stroke-width="3" stroke-linejoin="round"/><rect x="42" y="16" width="44" height="12" rx="5" fill="#c5763a" stroke="#5a2f14" stroke-width="3"/><ellipse cx="64" cy="19" rx="16" ry="3" fill="#3a1c0a" opacity=".55"/><path d="M34 62 c0 -12 6 -22 14 -27" fill="none" stroke="#f1b57a" stroke-width="4" stroke-linecap="round" opacity=".55"/><path d="M28 44 h72" stroke="#f4c542" stroke-width="3"/><path d="M28 50 h72" stroke="#2f6b3a" stroke-width="3"/><g fill="#e2492f"><path d="M40 43 l4 4 l-4 4 l-4 -4 z"/><path d="M52 43 l4 4 l-4 4 l-4 -4 z"/><path d="M64 43 l4 4 l-4 4 l-4 -4 z"/><path d="M76 43 l4 4 l-4 4 l-4 -4 z"/><path d="M88 43 l4 4 l-4 4 l-4 -4 z"/></g><path d="M60 17 c-4 -10 -18 -12 -26 -4 c8 -4 16 -2 20 6" fill="#2b1a0e"/><path d="M26 74 C22 40 106 40 102 74" fill="none" stroke="#1f2a24" stroke-width="6" stroke-linecap="round"/><rect x="16" y="66" width="16" height="26" rx="6" fill="#1f2a24" stroke="#f4c542" stroke-width="2.5"/><rect x="96" y="66" width="16" height="26" rx="6" fill="#1f2a24" stroke="#f4c542" stroke-width="2.5"/><ellipse cx="52" cy="70" rx="8" ry="8.5" fill="#fff8ec"/><ellipse cx="76" cy="70" rx="8" ry="8.5" fill="#fff8ec"/><circle cx="54" cy="71" r="4" fill="#1a1a1a"/><circle cx="78" cy="71" r="4" fill="#1a1a1a"/><circle cx="55.5" cy="69.5" r="1.3" fill="#fff"/><circle cx="79.5" cy="69.5" r="1.3" fill="#fff"/><path d="M43 58 l16 3" stroke="#2b1a0e" stroke-width="4" stroke-linecap="round"/><path d="M85 58 l-16 3" stroke="#2b1a0e" stroke-width="4" stroke-linecap="round"/><circle cx="42" cy="84" r="5" fill="#f08a6a" opacity=".55"/><circle cx="86" cy="84" r="5" fill="#f08a6a" opacity=".55"/><ellipse cx="64" cy="82" rx="4.5" ry="3.5" fill="#8d4a22"/><path d="M64 88 c-6 -6 -18 -4 -24 4 c-4 6 -8 10 -12 8 c4 -2 6 -8 10 -12 c8 -8 22 -8 26 0 c4 -8 18 -8 26 0 c4 4 6 10 10 12 c-4 2 -8 -2 -12 -8 c-6 -8 -18 -10 -24 -4 z" fill="#2b1a0e"/><path d="M56 100 q8 6 16 0" fill="none" stroke="#5a2f14" stroke-width="2.5" stroke-linecap="round"/></svg>';

// ═══ Спільний шар: те, що кличе картка столу ═══════════════════════════════════════════════════════
export const SERIF = "Georgia, 'Noto Serif', 'Times New Roman', serif";
const svgInner = (s) => s.replace(/^\s*<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '');
const r1 = (v) => Math.round(v * 10) / 10;
const PET = { salt: 'Сіль', fish: 'Риба', grain: 'Збіжжя', tar: 'Дьоготь', honey: 'Мед', wool: 'Вовна', hides: 'Шкіри', linen: 'Полотно', wild: 'Віл · будь-що' };

let defsCache = {};
/** Символи карт і значків цього вигляду. Вставити один раз у корінь картки. */
export function defs(theme) {
  if (defsCache[theme]) return defsCache[theme];
  let o = '';
  if (theme === 'parchment') {
    for (const k of CARDS) {
      const key = KEY[k];
      o += '<symbol id="sol-p-' + k + '" viewBox="0 0 80 104">' + svgInner(P1.card(key)) + '</symbol>';
      o += '<symbol id="sol-pc-' + k + '" viewBox="0 0 80 84">' + svgInner(P1.card(key, { label: false })) + '</symbol>';
    }
  } else if (theme === 'board') {
    o += P2.cardDefs()
      + '<radialGradient id="sol-b-saucer" cx=".4" cy=".35" r=".8"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#d9d0bb"/></radialGradient>'
      + '<radialGradient id="sol-b-tea" cx=".42" cy=".38" r=".75"><stop offset="0" stop-color="#d9782a"/><stop offset=".65" stop-color="#a64a12"/><stop offset="1" stop-color="#6e2c07"/></radialGradient>'
      + '<filter id="sol-b-blur6"><feGaussianBlur stdDeviation="6"/></filter>';
    for (const [id, a, b] of [['tkL', '#d6a163', '#bb8346'], ['tkL2', '#cb9558', '#ae773d'], ['tkM', '#b67a3d', '#94602c'], ['tkD', '#7a4a25', '#5a3519']]) {
      o += '<linearGradient id="sol-b-' + id + '" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="' + a + '"/><stop offset="1" stop-color="' + b + '"/></linearGradient>';
    }
  } else {
    o += P3.ORDER.map(P3.cardSymbol).join('');
  }
  return (defsCache[theme] = '<svg class="sol-defs" width="0" height="0" aria-hidden="true"><defs>' + o + '</defs></svg>');
}

/** Карта вантажу: svg + назва. compact — без підпису (телефон, вузькі місця). */
export function cardHtml(theme, kind, opt = {}) {
  const key = KEY[kind];
  const compact = !!opt.compact;
  if (theme === 'parchment') {
    const vb = compact ? '0 0 80 84' : '0 0 80 104';
    return '<svg class="sol-art" viewBox="' + vb + '"' + (opt.slice ? ' preserveAspectRatio="xMidYMid slice"' : '') + ' aria-hidden="true"><use href="#' + (compact ? 'sol-pc-' : 'sol-p-') + kind + '"/></svg>';
  }
  if (theme === 'board') {
    const name = kind === 'wild' ? (compact ? 'віл' : 'круторогий віл') : CARGO_NAME[kind];
    return '<svg class="sol-art" viewBox="0 0 100 140" preserveAspectRatio="xMidYMin slice" aria-hidden="true"><use href="#sol-b-card-' + key + '"/></svg><em class="sol-cn">' + name + '</em>';
  }
  const vb = compact ? '34 0 52 58' : '0 0 120 84';
  const name = compact ? '' : '<em class="sol-cn" style="color:' + P3.CARD[key].t + ';background:' + P3.CARD[key].f + '">' + PET[kind] + '</em>';
  return '<svg class="sol-art" viewBox="' + vb + '" preserveAspectRatio="xMidYMid slice" aria-hidden="true"><use href="#sol-k-card-' + key + '" width="120" height="84"/></svg>' + name;
}

/** Сорочка колоди. */
export function backHtml(theme) {
  if (theme === 'parchment') {
    let petals = '';
    for (let i = 0; i < 8; i++) petals += '<path transform="rotate(' + i * 45 + ')" d="M0-5 5-18 0-30-5-18Z"/>';
    return '<svg class="sol-art" viewBox="0 0 80 104" aria-hidden="true"><rect x=".8" y=".8" width="78.4" height="102.4" rx="6" fill="#5a3418" stroke="#d8b77a" stroke-width="1.4"/><rect x="5" y="5" width="70" height="94" rx="3.5" fill="none" stroke="#d8b77a" stroke-width=".8"/><g transform="translate(40 50)" fill="none" stroke="#d8b77a" stroke-width="1.3">' + petals + '<circle r="5"/></g></svg>';
  }
  if (theme === 'board') return '<i class="sol-bk"></i><i class="sol-bk"></i><i class="sol-bk top"></i>';
  return '<svg class="sol-art" viewBox="-60 -40 120 80" preserveAspectRatio="xMidYMid slice" aria-hidden="true">' + P3.flower(0, 0, 44, {}) + '</svg>';
}

/** Плашка вантажу (значок на кольорі) — у вартості шляху, кнопках оплати, рядках 🧭. */
export function tile(theme, kind, missing) {
  const key = KEY[kind];
  if (missing) return '<i class="sol-tile miss" aria-hidden="true">?</i>';
  if (theme === 'parchment') {
    const C = P1.CARGO[key], wild = kind === 'wild';
    return '<svg class="sol-tile" viewBox="-13 -13 26 26" aria-hidden="true"><rect x="-12.3" y="-12.3" width="24.6" height="24.6" rx="4" fill="' + (wild ? '#ecc659' : C.soft) + '" stroke="' + P1.INK + '" stroke-width="1.3"/><g transform="scale(.82)">' + P1.mark(key, wild ? P1.INK : C.on) + '</g></svg>';
  }
  if (theme === 'board') {
    if (kind === 'grey') return '<svg class="sol-tile" viewBox="-13 -13 26 26" aria-hidden="true"><rect x="-12" y="-12" width="24" height="24" rx="4" fill="#d9cfb6" stroke="#2a160b" stroke-width="1.3"/><circle r="3.6" fill="#2a160b"/></svg>';
    const T = P2.TYPES[key];
    return '<svg class="sol-tile" viewBox="-13 -13 26 26" aria-hidden="true"><rect x="-12" y="-12" width="24" height="24" rx="4" fill="' + T.slot + '" stroke="#2a160b" stroke-width="1.3"/><g fill="' + T.glyphC + '">' + P2.GLYPH[key] + '</g></svg>';
  }
  const fill = kind === 'wild' ? '#2a1450' : P3.SLOT[key];
  return '<svg class="sol-tile" viewBox="-13 -13 26 26" aria-hidden="true"><rect x="-12" y="-12" width="24" height="24" rx="5" fill="' + fill + '" stroke="' + (kind === 'wild' ? '#ffe08a' : '#0a0812') + '" stroke-width="1.6"/><g transform="scale(.9)">' + (P3.GLYPH[key] || '') + '</g></svg>';
}

/** Рядок плашок: «🧵2 🐂1» значками. */
export const comboHtml = (theme, combo) => (combo.n ? tile(theme, combo.c) + '<b>' + combo.n + '</b>' : '') + (combo.w ? tile(theme, 'wild') + '<b>' + combo.w + '</b>' : '');

/** Фігура гравця (§ SEAT_SHAPE): колір завжди разом із нею. */
export function shapeSvg(i, fg = '#fff', bg = '#20140b') {
  const s = {
    0: '<circle r="6.5" fill="' + fg + '"/>',
    1: '<path d="M0-8 7.5 5.5H-7.5Z" fill="' + fg + '"/>',
    2: '<rect x="-6" y="-6" width="12" height="12" fill="' + fg + '"/>',
    3: '<path d="M0-8.5 8.5 0 0 8.5-8.5 0Z" fill="' + fg + '"/>',
    4: '<path d="M0-9 2.6-2.8 9.2-2.3 4.2 2 5.8 8.5 0 5 -5.8 8.5-4.2 2-9.2-2.3-2.6-2.8Z" fill="' + fg + '"/>',
  }[i] || '';
  return '<svg class="sol-emb" viewBox="-13 -13 26 26" aria-hidden="true"><circle r="12.5" fill="' + bg + '"/>' + s + '</svg>';
}

/** Дядько Глек. */
export const glekSvg = (cls = '') => GLEK.replace('<svg ', '<svg class="sol-gks ' + cls + '" aria-hidden="true" ').replace(/id="clay"/g, 'id="sol-clay"').replace(/url\(#clay\)/g, 'url(#sol-clay)');

/** Жетон гравця: воскова печатка / паперовий кружок / лакований перстень. */
export function avatarHtml(theme, p, color) {
  const nick = String(p.nick || '').replace(/\s*🤖\s*/g, '').trim();
  const isGlek = !!p.bot;
  const inner = isGlek ? '<span class="sol-avg">' + glekSvg() + '</span>' : '<b>' + (esc(nick.charAt(0).toUpperCase()) || '?') + '</b>';
  const wax = theme === 'parchment'
    ? '<svg class="sol-wax" viewBox="0 0 60 60" aria-hidden="true"><path d="M30 2C36 1 40 5 45 7 50 10 53 13 55 18 58 23 59 27 57 33 58 38 55 43 52 47 49 51 45 54 40 56 35 58 30 59 25 57 20 58 15 55 11 52 7 48 4 44 3 39 1 34 1 29 3 24 3 18 7 14 11 10 15 6 20 3 25 3 27 2 28 2 30 2Z" fill="' + color + '" stroke="rgba(20,8,0,.7)" stroke-width="1.3"/><circle cx="30" cy="30" r="21" fill="rgba(0,0,0,.14)" stroke="rgba(255,235,210,.4)" stroke-width="1"/></svg>'
    : '';
  return '<span class="sol-av" style="--c:' + color + '">' + wax + inner + shapeSvg(p.color, '#fff', theme === 'petryk' ? '#0a0812' : '#20140b') + '</span>';
}

/** Воскова печатка замовлення (очки на воску). */
export function sealSvg(txt, wax = '#a51d16') {
  return '<svg class="sol-sealart" viewBox="0 0 60 60" aria-hidden="true"><path d="M30 3C35 2 37 6 42 7 47 9 49 12 51 15 55 19 58 24 56 30 57 35 56 40 53 44 50 49 46 51 42 54 37 56 33 58 28 57 23 58 19 55 15 53 10 51 8 47 6 43 3 38 2 34 3 29 2 23 5 19 8 15 11 11 15 9 19 7 23 5 26 3 30 3Z" fill="' + wax + '" stroke="rgba(30,5,0,.65)" stroke-width="1.2"/><circle cx="30" cy="30" r="19.5" fill="rgba(0,0,0,.16)" stroke="rgba(255,220,190,.35)" stroke-width="1.1"/><path d="M14 22C18 13 26 9 34 10" fill="none" stroke="rgba(255,240,220,.55)" stroke-width="2" stroke-linecap="round"/><text x="30" y="38.5" text-anchor="middle" font-size="' + (String(txt).length > 2 ? 19 : 25) + '" font-family="' + SERIF.replace(/"/g, '') + '" fill="#fff3df">' + txt + '</text></svg>';
}

/** Значки смуг: віз, карти, замовлення. */
export const IC = {
  cart: '<svg viewBox="0 0 24 18" class="sol-ic" aria-hidden="true"><path d="M3 10C2 1 22 1 21 10Z" fill="currentColor"/><rect x="1.5" y="10" width="21" height="3" rx="1" fill="currentColor"/><circle cx="7" cy="14.5" r="3" fill="none" stroke="currentColor" stroke-width="1.7"/><circle cx="17" cy="14.5" r="3" fill="none" stroke="currentColor" stroke-width="1.7"/></svg>',
  cards: '<svg viewBox="0 0 22 20" class="sol-ic" aria-hidden="true"><rect x="2.5" y="3" width="10" height="14" rx="1.5" transform="rotate(-12 7 10)" fill="none" stroke="currentColor" stroke-width="1.7"/><rect x="8" y="3" width="11" height="15" rx="1.5" transform="rotate(8 13 10)" fill="currentColor"/></svg>',
  scroll: '<svg viewBox="0 0 20 20" class="sol-ic" aria-hidden="true"><path d="M5 3H15.4A1.6 1.6 0 0 1 17 4.6V6H13.8V15.4A2 2 0 0 1 11.8 17.4H4.4A2 2 0 0 1 2.6 15.2H5Z" fill="currentColor"/><path d="M7.2 7H11.6M7.2 10H11.6" stroke="rgba(255,246,224,.75)" stroke-width="1.4" stroke-linecap="round"/></svg>',
};
/** Пісковий годинник (Стара мапа, Настілка). */
export const hourglass = (cls = '') => '<svg class="sol-hg ' + cls + '" viewBox="0 0 30 40" aria-hidden="true"><path d="M5 3H25M5 37H25" stroke="#3b2412" stroke-width="3.6" stroke-linecap="round"/><path d="M8 4C8 15 13 16 13.5 20 13 24 8 25 8 36H22C22 25 17 24 16.5 20 17 16 22 15 22 4Z" fill="#fff8e3" fill-opacity=".85" stroke="#3b2412" stroke-width="1.8"/><path d="M10.5 11C12 15 14 17 15 20 16 17 18 15 19.5 11Z" fill="#c98e2c"/><path d="M15 21V30" stroke="#c98e2c" stroke-width="1.2"/><path d="M10 35C10.5 30 13 28.5 15 28.5S19.5 30 20 35Z" fill="#c98e2c"/></svg>';

/** Таймер-квітка (Петриківка): 24 пелюстки, гасне по колу; кожна з data-i, картка гасить їх класом .off. */
export function timerRose() {
  let s = '';
  for (let i = 0; i < 24; i++) {
    const a = i / 24 * 360 - 90, rad = a * Math.PI / 180;
    s += '<g class="sol-pt" data-i="' + i + '" style="--pt:' + (i % 2 ? '#ffd34d' : '#ef7d1a') + '">' + P3.petal(Math.cos(rad) * 26, Math.sin(rad) * 26, a, 22, 8.5, 'currentColor') + '</g>';
  }
  return '<svg class="sol-rose" viewBox="-50 -50 100 100" aria-hidden="true">' + s + '<circle r="25" fill="#0a0812" stroke="#ffd34d" stroke-width="1.6"/></svg>';
}

/** Плитка вишивки (Петриківка) — data:url на CSS-змінну. */
export function stitchUrl(h = 10) {
  const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="' + h + '" height="' + h + '" viewBox="0 0 ' + h + ' ' + h + '">' + P3.stitchBand(h, h) + '</svg>';
  return 'url("data:image/svg+xml,' + encodeURIComponent(svg) + '")';
}

/** Шкіра панелей «Старої мапи». */
export const LEATHER = 'url("data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="220" height="220"><filter id="l"><feTurbulence type="fractalNoise" baseFrequency=".55" numOctaves="3" seed="3"/><feColorMatrix values="0 0 0 0 .07 0 0 0 0 .03 0 0 0 0 .01 0 0 0 .8 -.2"/></filter><rect width="220" height="220" filter="url(#l)"/></svg>') + '")';

/** Горіхова стільниця «Настілки». Малюємо фільтром один раз на вікно. */
export function walnut(w, h, seed = 7) {
  w = Math.round(w); h = Math.round(h);
  return '<svg class="sol-table" viewBox="0 0 ' + w + ' ' + h + '" preserveAspectRatio="none" aria-hidden="true"><defs>'
    + '<filter id="sol-walnut" x="0" y="0" width="100%" height="100%" color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency=".0028 .085" numOctaves="5" seed="' + seed + '" result="n"/><feColorMatrix in="n" type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  1.5 0 0 0 -.45" result="a"/><feFlood flood-color="#1e0e05" result="d"/><feComposite in="d" in2="a" operator="in" result="dark"/><feTurbulence type="fractalNoise" baseFrequency=".012 .34" numOctaves="3" seed="' + (seed + 3) + '" result="m"/><feColorMatrix in="m" type="matrix" values="0 0 0 0 .86  0 0 0 0 .55  0 0 0 0 .3  .7 0 0 0 -.27" result="lit"/><feFlood flood-color="#5d3820" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="lit"/><feMergeNode in="dark"/></feMerge></filter>'
    + '<radialGradient id="sol-vign" cx=".42" cy=".36" r=".82"><stop offset="0" stop-color="#ffd9a0" stop-opacity=".13"/><stop offset=".55" stop-color="#000" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".46"/></radialGradient></defs>'
    + '<rect width="' + w + '" height="' + h + '" filter="url(#sol-walnut)"/>'
    + [1, 2, 3, 4].map((i) => '<path d="M0 ' + Math.round(i * h / 4.6) + 'H' + w + '" stroke="#160903" stroke-width="2" opacity=".6"/>').join('')
    + '<rect width="' + w + '" height="' + h + '" fill="url(#sol-vign)"/></svg>';
}

/** Склянка чаю й дрібка солі: нерухома оздоба столу. */
export const teaHtml = (size) => P2.teaGlass(size);
export const saltHtml = (w, h, n, seed) => P2.saltScatter(w, h, n, seed);

/**
 * Рахівниця довкола мапи (Настілка): 100 клітинок (0…99), диски гравців за очками шляхів; понад 99 — по колу зі «+100».
 * w, h — розмір дошки, t — товщина смуги; scores — [{ seat, color, shape, pts }].
 */
export function railSvg(w, h, t, scores) {
  const cw = w / 31, vh = (h - 2 * t) / 19, cells = [];
  for (let i = 0; i <= 30; i++) cells.push({ n: i, x: i * cw, y: 0, w: cw, h: t });
  for (let i = 1; i < 20; i++) cells.push({ n: 30 + i, x: w - t, y: t + (i - 1) * vh, w: t, h: vh });
  for (let i = 0; i <= 30; i++) cells.push({ n: 50 + i, x: w - (i + 1) * cw, y: h - t, w: cw, h: t });
  for (let i = 1; i < 20; i++) cells.push({ n: 80 + i, x: 0, y: h - t - i * vh, w: t, h: vh });
  let o = '';
  for (const c of cells) {
    const five = c.n % 5 === 0, ten = c.n % 10 === 0;
    const fill = ten ? 'tkD' : five ? 'tkM' : c.n % 2 ? 'tkL' : 'tkL2';
    o += '<rect x="' + r1(c.x + 1) + '" y="' + r1(c.y + 1) + '" width="' + r1(c.w - 2) + '" height="' + r1(c.h - 2) + '" rx="3.5" fill="url(#sol-b-' + fill + ')" stroke="#2a160b"/>'
      + '<text x="' + r1(c.x + c.w / 2) + '" y="' + r1(c.y + c.h / 2 + t * .15) + '" text-anchor="middle" font-size="' + r1(t * (five ? .42 : .37)) + '" font-weight="' + (five ? 900 : 700) + '" fill="' + (ten ? '#fff0cf' : '#3a1d0a') + '"' + (ten ? '' : ' opacity=".85"') + '>' + c.n + '</text>';
  }
  const at = {};
  for (const s of scores) {
    const n = ((s.pts % 100) + 100) % 100, c = cells.find((x) => x.n === n);
    const k = at[n] = (at[n] || 0) + 1;
    const x = c.x + c.w / 2 + (k - 1) * t * .24, y = c.y + c.h / 2 - (k - 1) * t * .24, r = t * .44;
    o += '<g><circle cx="' + r1(x + 1.5) + '" cy="' + r1(y + 3) + '" r="' + r1(r) + '" fill="#1b0d03" opacity=".6"/><circle cx="' + r1(x) + '" cy="' + r1(y) + '" r="' + r1(r) + '" fill="' + s.color + '" stroke="#2a160b" stroke-width="1.2"/><circle cx="' + r1(x) + '" cy="' + r1(y) + '" r="' + r1(r - 2.6) + '" fill="none" stroke="#fff" stroke-opacity=".42" stroke-width="1.2"/>'
      + '<text x="' + r1(x) + '" y="' + r1(y + r * .36) + '" text-anchor="middle" font-size="' + r1(r * 1.02) + '" font-weight="900" fill="#fff"><title>' + esc(s.nick) + ': ' + s.pts + ' ⭐ ' + SEAT_SHAPE[s.shape] + '</title>' + s.pts + '</text></g>';
  }
  return '<svg class="sol-railsvg" width="' + Math.round(w) + '" height="' + Math.round(h) + '" viewBox="0 0 ' + Math.round(w) + ' ' + Math.round(h) + '" aria-hidden="true">' + o + '</svg>';
}

/** Парадна назва вигляду (картуш, табличка, напис із волом). */
export function titleHtml(theme) {
  if (theme === 'parchment') {
    const stars = [[14, 26, 1.3], [30, 18, .9], [46, 16, 1.4], [62, 10, 1], [80, 13, .8], [96, 6, 1.5], [112, 8, 1], [130, 5, 1.2], [150, 7, .9], [168, 6, 1.5], [186, 10, 1], [204, 12, 1.2], [222, 16, .9], [238, 22, 1.3]]
      .map(([x, y, r]) => '<path d="M' + x + ' ' + (y - r * 2.2) + 'l' + r * .5 + ' ' + r * 1.7 + ' ' + r * 1.7 + ' ' + r * .5 + ' -' + r * 1.7 + ' ' + r * .5 + ' -' + r * .5 + ' ' + r * 1.7 + ' -' + r * .5 + ' -' + r * 1.7 + ' -' + r * 1.7 + ' -' + r * .5 + ' ' + r * 1.7 + ' -' + r * .5 + 'z"/>').join('');
    const ox = '<g transform="translate(46 2)"><path d="M-14-10C-8-14 6-13 11-10L13 1 14 14H10L9 4C4 7-4 7-9 4L-10 14H-14Z" fill="#5d3519" stroke="#2b1a0d" stroke-width="1.4"/><path d="M11-10 18-10 22-16M14-10 17-6" fill="none" stroke="#2b1a0d" stroke-width="1.6"/><path d="M11-12C13-16 19-16 21-10 20-6 16-5 13-6Z" fill="#5d3519" stroke="#2b1a0d" stroke-width="1.4"/></g>';
    return '<svg class="sol-stars" viewBox="0 0 250 34" aria-hidden="true"><path d="M6 28C70 4 170 2 244 22" fill="none" stroke="#7a5a33" stroke-width=".6" stroke-dasharray="1 3"/><g fill="#6d4a1e">' + stars + '</g></svg>'
      + '<h1>Соляний шлях</h1><p>мапа чумацьких доріг, бродів і торгів,<br>накреслена зі слів бувалих людей</p>'
      + '<svg class="sol-cartoon" viewBox="-70 -30 140 54" aria-hidden="true">' + P1.cart(26, '#c8a86a', '') + ox + '</svg>';
  }
  if (theme === 'board') {
    const row = [[1, 1], [2, 2], [3, 4], [4, 7], [6, 15]].map(([a, b]) => '<span><i>' + a + '</i>→<b>' + b + '</b></span>').join('');
    return '<h1>Соляний шлях</h1><small>чумацька настілка Дядька Глека</small><div class="sol-ptrow">' + row + '</div>';
  }
  return '<h1>Соляний шлях</h1>';
}

/** Міні-мапа замовлення: контур і дві точки. land — d контуру з hud-calc.parseGeo. */
export function miniMap(land, A, B, cls = 'sol-mm') {
  const p = (c, f) => '<circle cx="' + c.x + '" cy="' + c.y + '" r="90" fill="' + f + '" stroke="#fff" stroke-width="26"/>';
  return '<svg class="' + cls + '" viewBox="-40 -40 4080 2810" aria-hidden="true"><path d="' + (land || '') + '" class="sol-mml"/><path d="M' + A.x + ' ' + A.y + 'L' + B.x + ' ' + B.y + '" class="sol-mmp"/>' + p(A, '#b3261e') + p(B, '#b3261e') + '</svg>';
}

/** Прев'ю вигляду для «🎨 Вигляд»: крихітний знімок розкладки — видно, де колонка, рука й мапа. */
export function previewSvg(theme) {
  const land = 'M18 26 34 15 52 18 66 12 86 16 100 26 106 36 90 40 84 50 70 46 60 54 50 44 34 42 22 40Z';
  const routes = (c1, c2, c3) => '<path d="M34 25 58 30 84 24" fill="none" stroke="' + c1 + '" stroke-width="2.4" stroke-dasharray="5 1.2"/><path d="M42 36 58 30 66 42" fill="none" stroke="' + c2 + '" stroke-width="2.4" stroke-dasharray="5 1.2"/><path d="M84 24 92 36" fill="none" stroke="' + c3 + '" stroke-width="2.4" stroke-dasharray="4 1.2"/>';
  if (theme === 'parchment') {
    return '<svg class="sol-pv" viewBox="0 0 120 70" aria-hidden="true"><rect width="120" height="70" fill="#a9c2b4"/><path d="' + land + '" transform="translate(16 2) scale(.88)" fill="#ead9ad" stroke="#5a3a1e"/><g transform="translate(16 2) scale(.88)">' + routes('#b3261e', '#1c5a9e', '#e6ad12') + '</g>'
      + '<rect width="30" height="70" fill="#2a190e"/><rect x="2" y="2" width="26" height="12" rx="1" fill="#efdba4"/><rect x="3" y="18" width="24" height="3" fill="#e0bc6e"/><rect x="3" y="24" width="24" height="3" fill="#e0bc6e" opacity=".6"/><rect x="3" y="34" width="24" height="6" fill="#efdba4"/><rect x="3" y="42" width="24" height="6" fill="#efdba4"/><circle cx="6" cy="37" r="3" fill="#a51d16"/><circle cx="6" cy="45" r="3" fill="#a51d16"/>'
      + '<rect x="30" y="50" width="34" height="20" fill="#2a190e"/>' + [0, 1, 2, 3].map((i) => '<rect x="' + (33 + i * 7.4) + '" y="54" width="6" height="12" rx="1" fill="' + ['#b3261e', '#1c5a9e', '#2c7a3a', '#e6ad12'][i] + '"/>').join('')
      + [0, 1].map((i) => '<rect x="' + (3 + i * 8) + '" y="56" width="7" height="11" rx="1" fill="' + ['#2c7a3a', '#d5689a'][i] + '"/>').join('') + '</svg>';
  }
  if (theme === 'board') {
    return '<svg class="sol-pv" viewBox="0 0 120 70" aria-hidden="true"><rect width="120" height="70" fill="#5d3820"/><rect x="3" y="3" width="80" height="64" rx="1.5" fill="#c79a5e" stroke="#2a160b" stroke-width=".6"/><rect x="8" y="8" width="70" height="54" fill="#3aa8e4"/>'
      + '<path d="' + land + '" transform="translate(-4 2) scale(.76)" fill="#a8d868" stroke="#355a2d"/><g transform="translate(-4 2) scale(.76)">' + routes('#d8352b', '#fbcb1e', '#0f7f3f') + '</g>'
      + [0, 1, 2].map((i) => '<rect x="' + (10 + i * 6) + '" y="' + (46 - (i === 1 ? 2 : 0)) + '" width="9" height="14" rx="1" transform="rotate(' + ((i - 1) * 6) + ' ' + (14 + i * 6) + ' 52)" fill="' + ['#d8352b', '#fbcb1e', '#0f7f3f'][i] + '" stroke="#fbf4e4" stroke-width=".8"/>').join('')
      + '<rect x="87" y="5" width="30" height="8" rx="1.5" fill="#bb8346"/><rect x="87" y="15" width="14" height="8" rx="1" fill="#f1e4c8"/><rect x="103" y="15" width="14" height="8" rx="1" fill="#f1e4c8"/><rect x="87" y="26" width="30" height="12" rx="1" fill="#f1e4c8"/>'
      + [0, 1, 2, 3].map((i) => '<rect x="' + (87 + i * 7.6) + '" y="41" width="6.4" height="9" rx="1" fill="' + ['#0f7f3f', '#f4c542', '#d8352b', '#1f73d1'][i] + '"/>').join('') + '<rect x="87" y="53" width="30" height="12" rx="1" fill="#f1e4c8"/></svg>';
  }
  return '<svg class="sol-pv" viewBox="0 0 120 70" aria-hidden="true"><rect width="120" height="70" fill="#08070f"/><rect y="0" width="88" height="70" fill="#121a58"/><path d="' + land + '" transform="translate(-4 3) scale(.8)" fill="#3b2a66" stroke="#f6bf26" stroke-dasharray="1.4 1"/><g transform="translate(-4 3) scale(.8)">' + routes('#e8341f', '#fbc924', '#12a862') + '</g>'
    + '<rect x="92" width="28" height="70" fill="#100d1e"/><rect x="88" width="4" height="70" fill="#ef7d1a"/><rect x="95" y="4" width="22" height="4" fill="#fbf1da"/><rect x="95" y="11" width="22" height="4" fill="#ffd34d"/><rect x="95" y="22" width="10" height="7" fill="#c9281b"/><rect x="107" y="22" width="10" height="7" fill="#f6bf26"/><rect x="95" y="31" width="10" height="7" fill="#1f5fbd"/><rect x="107" y="31" width="10" height="7" fill="#14865a"/><rect x="95" y="44" width="22" height="3" fill="#fbf1da"/><rect x="95" y="50" width="22" height="3" fill="#fbf1da"/>'
    + P3.flower(14, 12, 9, {}) + [0, 1, 2].map((i) => '<rect x="' + (5 + i * 13) + '" y="52" width="11" height="14" rx="1.5" fill="' + ['#c9281b', '#14865a', '#f6bf26'][i] + '" stroke="#0a0812"/>').join('') + '</svg>';
}
