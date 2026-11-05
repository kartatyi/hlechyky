// «Стара мапа» — бібліотека малюнків: символи вантажів, ілюстровані карти, карти-значки, поселення, чудовиська, рожа вітрів.
export const INK = '#2b1a0d';
export const SEPIA = '#5a3a1e';
export const BONE = '#f7efd9';

export const CARGO = {
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
export function mark(kind, c) {
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
export const ILLO = {
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
    return `<clipPath id="wildc"><rect x="0" y="0" width="80" height="80"/></clipPath><g clip-path="url(#wildc)">${rays}</g>
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
export function card(kind, o = {}) {
  const c = CARGO[kind];
  const id = 'cg' + (++cardN);
  const label = o.label !== false;
  const short = kind === 'wild' ? 'віл' : c.name;
  const vb = label ? '0 0 80 104' : '0 0 80 84';
  const H = label ? 104 : 84;
  const onMark = kind === 'white' || kind === 'yellow' || kind === 'pink' ? INK : '#fff7e6';
  const frame = kind === 'wild' ? '#8a6614' : INK;
  const bandFill = kind === 'black' ? '#f0e2bd' : '#f8efd6';
  return `<svg class="cardart" viewBox="${vb}" aria-hidden="true" preserveAspectRatio="xMidYMid meet">
    <defs><linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${c.top}"/><stop offset="1" stop-color="${c.bot}"/></linearGradient></defs>
    <rect x=".8" y=".8" width="78.400" height="${H - 1.6}" rx="6" fill="url(#${id})" stroke="${frame}" stroke-width="1.600"/>
    <rect x="3.500" y="3.500" width="73" height="${H - 7}" rx="4" fill="none" stroke="${kind === 'white' ? '#a3967a' : '#fff'}" stroke-opacity=".55" stroke-width=".9"/>
    <g opacity=".14">${kind === 'white' ? '' : `<path d="M6 12 74 12M6 18 74 18M6 24 74 24" stroke="#000" stroke-width=".6"/>`}</g>
    <g transform="translate(0 ${label ? 3 : 3})">${ILLO[kind]}</g>
    ${label ? `<path d="M3 83Q40 79 77 83V96Q77 101 72 101H8Q3 101 3 96Z" fill="${bandFill}" stroke="${INK}" stroke-width="1.200"/>
    <text x="40" y="96" text-anchor="middle" font-family="Luminari,'Iowan Old Style',Georgia,serif" font-size="${short.length > 6 ? 12.5 : 13.5}" fill="${INK}">${short}</text>` : ''}
    ${kind === 'wild' ? '' : `<g transform="translate(13 13) scale(.62)"><circle r="13" fill="${kind === 'white' ? '#e9dfc4' : '#00000055'}"/>${mark(kind, onMark)}</g>`}
  </svg>`;
}

// ─── Поселення. Бокс приблизно ±40 у сітці мапи. ─────────────────────────────────────────────────────
const P = '#f4e9cb', R = '#6b4a2b';
const hs = (x, y, w, h, r = w * .75, roof = R) => `<path d="M${x - w} ${y}V${y - h}H${x + w}V${y}Z" fill="${P}" stroke="${INK}" stroke-width="2.200"/><path d="M${x - w - 2.500} ${y - h}L${x} ${y - h - r}L${x + w + 2.500} ${y - h}Z" fill="${roof}" stroke="${INK}" stroke-width="2.200" stroke-linejoin="round"/>`;
const church = (x, y, k = 1, gold = false) => {
  const d = gold ? '#e2b23a' : '#59703f';
  return `<g transform="translate(${x} ${y}) scale(${k})"><path d="M-12 0V-22H12V0Z" fill="${P}" stroke="${INK}" stroke-width="2.200"/>
    <path d="M-7-22V-34H7V-22Z" fill="${P}" stroke="${INK}" stroke-width="2.200"/>
    <path d="M-8.500-34C-9.500-42-2.500-44 0-50 2.500-44 9.500-42 8.500-34Z" fill="${d}" stroke="${INK}" stroke-width="2.200" stroke-linejoin="round"/>
    <path d="M0-50V-60M-3.500-56H3.500" stroke="${INK}" stroke-width="2.200"/>
    <path d="M-4 0V-9A4 4 0 0 1 4-9V0Z" fill="${INK}"/><path d="M-8-15V-19M8-15V-19M0-26V-31" stroke="${INK}" stroke-width="1.800"/></g>`;
};
const tower = (x, y, k = 1) => `<g transform="translate(${x} ${y}) scale(${k})"><path d="M-9 0V-34H9V0Z" fill="${P}" stroke="${INK}" stroke-width="2.200"/><path d="M-12-34V-42H-7V-38H-2.500V-42H2.500V-38H7V-42H12V-34Z" fill="${R}" stroke="${INK}" stroke-width="2"/><path d="M-3 0V-8A3 3 0 0 1 3-8V0Z" fill="${INK}"/><path d="M0-22V-28" stroke="${INK}" stroke-width="2.400"/></g>`;
const ground = (w) => `<path d="M${-w - 6} 1Q0 6 ${w + 6} 1" fill="none" stroke="${INK}" stroke-width="2.200" stroke-linecap="round"/>`;

export const TOWN = {
  // столиця: багато бань
  kyiv: () => `<g transform="translate(0 16)">${ground(36)}${hs(-30, 0, 9, 10)}${hs(31, 0, 9, 10)}${church(-13, 0, .85, true)}${church(14, 0, .85, true)}${church(0, 2, 1.12, true)}</g>`,
  major: () => `<g transform="translate(0 14)">${ground(26)}${hs(-21, 0, 8, 10)}${hs(21, 0, 8, 9)}${church(0, 1, .95)}</g>`,
  capital: () => `<g transform="translate(0 13)">${ground(20)}${hs(-15, 0, 7.500, 10)}${hs(16, 0, 7, 8)}${church(1, 1, .75)}</g>`,
  castle: () => `<g transform="translate(0 13)">${ground(24)}<path d="M-20 0V-16H20V0Z" fill="${P}" stroke="${INK}" stroke-width="2.200"/><path d="M-14 0V-4H14V0" fill="none" stroke="${INK}" stroke-width="1.400"/>${tower(-17, 0, .8)}${tower(17, 0, .8)}${tower(0, 0, 1.05)}</g>`,
  village: () => `<g transform="translate(0 11)">${ground(14)}${hs(-8, 0, 7, 9)}${hs(9, 1, 6.500, 8)}</g>`,
  // порт: маяк і щогла
  port: () => `<g transform="translate(0 13)">${ground(24)}${hs(-15, 0, 8, 9)}<path d="M6 0 9-30H17L20 0Z" fill="${P}" stroke="${INK}" stroke-width="2.200"/><path d="M8-30H18V-37H8Z" fill="#e2b23a" stroke="${INK}" stroke-width="2"/><path d="M6-37H20L13-45Z" fill="${R}" stroke="${INK}" stroke-width="2" stroke-linejoin="round"/><path d="M8-18H18M7-8H19" stroke="#b3261e" stroke-width="3"/></g>`,
  // Опішня: глечик! столиця гончарства
  pot: () => `<g transform="translate(0 14)">${ground(12)}<path d="M-5-33H5V-29C13-25 16-18 16-12 16-4 9 0 0 0-9 0-16-4-16-12-16-18-13-25-5-29Z" fill="#c5763a" stroke="${INK}" stroke-width="2.300"/><rect x="-7" y="-37" width="14" height="5" rx="2" fill="#d4884a" stroke="${INK}" stroke-width="2"/><path d="M12-24C24-26 24-10 14-8" fill="none" stroke="${INK}" stroke-width="2.200"/><path d="M-14-17H14M-14-13H14" stroke="#f4c542" stroke-width="2"/></g>`,
  // Перекоп: вал із брамою
  gate: () => `<g transform="translate(0 12)">${ground(24)}<path d="M-24 0V-14H24V0Z" fill="${P}" stroke="${INK}" stroke-width="2.200"/><path d="M-7 0V-10A7 7 0 0 1 7-10V0Z" fill="${INK}"/>${tower(0, -8, .7)}<path d="M-24-14V-19H-19V-14M-13-14V-19H-8V-14M8-14V-19H13V-14M19-14V-19H24V-14" fill="${R}" stroke="${INK}" stroke-width="1.600"/></g>`,
};

// ─── Віз на шляху (вид збоку, рух по +x). w — напівширина. pm — позначка гравця на будці. ──────────────
export function cart(w, color, pm) {
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
export const PMARK = [
  `<circle r="7.500" fill="#fff" stroke="${INK}" stroke-width="1.500"/>`,
  `<path d="M0-10 9.500 7H-9.500Z" fill="#fff" stroke="${INK}" stroke-width="1.500" stroke-linejoin="round"/>`,
  `<rect x="-7.500" y="-7.500" width="15" height="15" fill="#fff" stroke="${INK}" stroke-width="1.500"/>`,
  `<path d="M-3-9.500H3V-3H9.500V3H3V9.500H-3V3H-9.500V-3H-3Z" fill="#fff" stroke="${INK}" stroke-width="1.300" stroke-linejoin="round"/>`,
];

// ─── Морські дива ─────────────────────────────────────────────────────────────────────────────────────
const SEA_S = '#2c2a22';
export const serpent = `<g fill="none" stroke-linejoin="round" stroke-linecap="round">
  <g stroke="${SEA_S}" stroke-width="2.200">
    <path d="M58 104C50 72 36 62 46 40 54 22 82 16 98 28L128 36C134 38 134 46 126 47L120 48 128 56C118 58 104 54 96 50 86 52 84 60 90 76 94 88 98 96 100 104Z" fill="#a9b88e"/>
    <path d="M47 38 34 32 42 46 27 48 42 58 28 66 47 70 37 82 54 84" fill="#7e3b24"/>
    <path d="M128 37 118 40 122 45 112 44M98 50 106 49 104 43" fill="#f7efd9" stroke-width="1.300"/>
    <path d="M130 104C132 64 184 64 188 104Z" fill="#a9b88e"/><path d="M198 104C200 76 234 76 238 104Z" fill="#a9b88e"/>
    <path d="M250 104C254 94 264 84 280 66 276 80 288 76 302 70 298 88 284 96 276 104Z" fill="#7e3b24"/>
    <path d="M138 72 132 58 146 63 150 50 160 62 172 50 174 64 186 58 182 74" fill="#7e3b24" stroke-width="1.800"/>
    <path d="M204 84 200 74 210 78 216 68 222 77 232 72 232 86" fill="#7e3b24" stroke-width="1.800"/>
  </g>
  <circle cx="84" cy="33" r="6.500" fill="#f7efd9" stroke="${SEA_S}" stroke-width="2"/><circle cx="85.500" cy="33" r="2.600" fill="#111"/>
  <path d="M74 26Q84 19 94 28" stroke="${SEA_S}" stroke-width="3"/>
  <g stroke="#48553d" stroke-width="1.300"><path d="M58 52q6 4 12 0M56 62q6 4 12 0M60 72q6 4 12 0M64 82q6 4 12 0M68 92q6 4 12 0"/><path d="M146 92q4-8 12-8M158 96q4-8 12-8M170 98q2-5 8-6M210 94q4-6 8-6M222 98q2-4 6-5"/></g>
  <path d="M132 44q12 4 22-5q-4 9-16 12" stroke="#b3261e" stroke-width="2"/>
  <g stroke="${SEA_S}" stroke-width="1.700"><path d="M20 107q10-5 20 0t20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0"/><path d="M60 116q10-4 20 0t20 0 20 0 20 0 20 0 20 0 20 0 20 0"/><path d="M124 125q8-3 16 0t16 0 16 0 16 0"/></g>
</g>`;
export const whale = `<g fill="none" stroke-linejoin="round" stroke-linecap="round">
  <g stroke="${SEA_S}" stroke-width="2"><path d="M60 22C52 8 40 8 34 16M60 22C60 6 72 0 84 6M60 22C64 12 66 8 60 0" stroke-width="1.800"/></g>
  <path d="M12 80C22 40 96 18 150 42 170 52 190 54 208 34 206 54 210 66 226 82 204 76 186 78 164 88 110 118 32 116 12 80Z" fill="#9db3a4" stroke="${SEA_S}" stroke-width="2.300"/>
  <path d="M12 80C28 82 42 76 56 62 54 82 40 98 26 99" fill="#f7efd9" stroke="${SEA_S}" stroke-width="2"/>
  <path d="M18 82 21 90 26 80 30 91 35 76 40 86 44 70" fill="#fff" stroke="${SEA_S}" stroke-width="1.300"/>
  <circle cx="58" cy="50" r="7.500" fill="#f7efd9" stroke="${SEA_S}" stroke-width="2"/><circle cx="56" cy="51" r="3" fill="#111"/><path d="M46 44Q56 36 68 44" stroke="${SEA_S}" stroke-width="3"/>
  <path d="M84 36 88 22 98 32 106 16 116 32 128 22 130 40" fill="#7e3b24" stroke="${SEA_S}" stroke-width="1.800"/>
  <path d="M96 92C102 112 118 118 132 114 124 106 122 96 124 86" fill="#7e3b24" stroke="${SEA_S}" stroke-width="1.900"/>
  <g stroke="#3f5145" stroke-width="1.200"><path d="M80 52q6 5 12 0M100 50q6 5 12 0M120 54q6 5 12 0M140 58q6 5 12 0M90 66q6 5 12 0M110 66q6 5 12 0M130 70q6 5 12 0M154 72q6 5 12 0M70 70q6 5 12 0"/></g>
  <g stroke="${SEA_S}" stroke-width="1.700"><path d="M4 110q10-5 20 0t20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0"/><path d="M40 120q10-4 20 0t20 0 20 0 20 0 20 0"/></g>
</g>`;
export const chaika = `<g fill="none" stroke-linejoin="round" stroke-linecap="round" stroke="${SEA_S}" stroke-width="2.200">
  <path d="M4 42C20 52 90 52 110 40L102 56C76 62 32 62 14 56Z" fill="#7a5730"/>
  <path d="M56 46V4" stroke-width="2.800"/><path d="M34 12C50 8 62 8 80 12 74 22 74 30 78 38 62 34 50 34 36 38 40 30 40 22 34 12Z" fill="#f4e9cb"/>
  <path d="M48 20H66M46 28H68" stroke="#b3261e"/><path d="M56 4 70 8 56 11Z" fill="#b3261e" stroke-width="1.300"/>
  <path d="M24 50 16 66M42 54 36 68M72 54 76 68M90 50 96 66" stroke-width="1.700"/>
  <path d="M0 68q8-4 16 0t16 0 16 0 16 0 16 0 16 0 16 0" stroke-width="1.600"/>
</g>`;

// ─── Рожа вітрів ─────────────────────────────────────────────────────────────────────────────────────
export function compass(r) {
  let o = '';
  const pt = (a, R, w, dark, light) => {
    const A = a * Math.PI / 180, ca = Math.cos(A), sa = Math.sin(A);
    const tip = [R * sa, -R * ca], l = [-w * ca, -w * sa], rr = [w * ca, w * sa];
    return `<path d="M0 0L${l[0]} ${l[1]}L${tip[0]} ${tip[1]}Z" fill="${dark}" stroke="${INK}" stroke-width="1.300"/><path d="M0 0L${rr[0]} ${rr[1]}L${tip[0]} ${tip[1]}Z" fill="${light}" stroke="${INK}" stroke-width="1.300"/>`;
  };
  // кільця з поділками
  o += `<circle r="${r}" fill="#efe2bd" fill-opacity=".55" stroke="${INK}" stroke-width="2.600"/><circle r="${r * .92}" fill="none" stroke="${INK}" stroke-width="1.100"/>`;
  for (let i = 0; i < 72; i++) {
    const A = i * 5 * Math.PI / 180, a = r * .92, b = i % 9 === 0 ? r : r * .965;
    o += `<path d="M${a * Math.sin(A)} ${-a * Math.cos(A)}L${b * Math.sin(A)} ${-b * Math.cos(A)}" stroke="${INK}" stroke-width="${i % 9 === 0 ? 2 : 1}"/>`;
  }
  for (let i = 0; i < 16; i++) if (i % 2) o += pt(i * 22.5, r * .56, r * .045, '#e6d6ad', '#5a3a1e');
  for (let i = 0; i < 8; i++) if (i % 2) o += pt(i * 45, r * .74, r * .085, '#2c7a3a', '#efe2bd');
  for (let i = 0; i < 4; i++) o += pt(i * 90, r * 1.0, r * .12, '#b3261e', '#f7efd9');
  o += `<circle r="${r * .1}" fill="#e2b23a" stroke="${INK}" stroke-width="1.800"/><circle r="${r * .035}" fill="${INK}"/>`;
  const lab = (t, a) => {
    const A = a * Math.PI / 180, R = r * 1.2;
    return `<text x="${R * Math.sin(A)}" y="${-R * Math.cos(A) + r * .08}" text-anchor="middle" font-family="Luminari,Georgia,serif" font-size="${r * .24}" fill="${INK}">${t}</text>`;
  };
  o += lab('Пн', 0) + lab('Сх', 90) + lab('Пд', 180) + lab('Зх', 270);
  return `<g>${o}</g>`;
}
