// Малюнки: карти вантажів, гліфи для клітинок шляху, дрібниці для столу. Лише SVG-рядки.
export const INK = '#2a160b';
export const TYPES = {
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
export const ORDER = ['red','blue','green','black','yellow','white','orange','pink','wild'];

// ── крихітні гліфи для клітинок шляху (бокс -10..10): одна чітка форма, зрозуміла й без кольору ──
export const GLYPH = {
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
export function cardDefs() {
  let o = '';
  // патерн-тла: кожен тип має ще й власну фактуру — зрозуміло й без кольору
  const pat = {
    red: `<pattern id="cp-red" width="18" height="18" patternUnits="userSpaceOnUse"><path d="M4 4 l3 3 -3 3 -3 -3z M13 12 l3 3 -3 3 -3 -3z" fill="#fff" opacity=".2"/></pattern>`,
    blue: `<pattern id="cp-blue" width="24" height="12" patternUnits="userSpaceOnUse"><path d="M0 6 q6 -5 12 0 t12 0" stroke="#fff" stroke-width="1.6" fill="none" opacity=".18"/></pattern>`,
    green: `<pattern id="cp-green" width="11" height="11" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="5" height="11" fill="#fff" opacity=".07"/></pattern>`,
    black: `<pattern id="cp-black" width="12" height="12" patternUnits="userSpaceOnUse"><circle cx="3" cy="3" r="1.1" fill="#fff" opacity=".13"/><circle cx="9" cy="9" r="1.1" fill="#fff" opacity=".13"/></pattern>`,
    yellow: `<pattern id="cp-yellow" width="20" height="17.3" patternUnits="userSpaceOnUse"><path d="M5 0 h10 l5 8.66 -5 8.66 h-10 l-5 -8.66z" fill="none" stroke="#a86a00" stroke-width="1.4" opacity=".28"/></pattern>`,
    white: `<pattern id="cp-white" width="18" height="18" patternUnits="userSpaceOnUse"><path d="M3 9 c2 -5 7 -3 5 1 M12 16 c2 -5 7 -3 5 1" stroke="#a79c82" stroke-width="1.5" fill="none" opacity=".32"/></pattern>`,
    orange: `<pattern id="cp-orange" width="14" height="14" patternUnits="userSpaceOnUse"><path d="M2 7 h6" stroke="#fff" stroke-width="1.7" stroke-linecap="round" opacity=".2"/></pattern>`,
    pink: `<pattern id="cp-pink" width="12" height="12" patternUnits="userSpaceOnUse"><path d="M3 3 l4 4 M7 3 l-4 4" stroke="#fff" stroke-width="1.3" opacity=".3"/></pattern>`,
    wild: '',
  };
  o += Object.values(pat).join('');
  for (const k of ORDER) {
    const t = TYPES[k];
    o += `<linearGradient id="cg-${k}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${t.bg}"/><stop offset="1" stop-color="${t.bg2}"/></linearGradient>`;
    o += `<symbol id="card-${k}" viewBox="0 0 100 140">
      <rect x="1" y="1" width="98" height="138" rx="9" fill="#fbf4e4" stroke="#7a5a3a" stroke-width="1"/>
      <rect x="5" y="5" width="90" height="130" rx="6" fill="url(#cg-${k})"/>
      ${pat[k] ? `<rect x="5" y="5" width="90" height="130" rx="6" fill="url(#cp-${k})"/>` : ''}
      <clipPath id="cc-${k}"><rect x="5" y="5" width="90" height="130" rx="6"/></clipPath>
      <g clip-path="url(#cc-${k})">${CARD_ART[k]}</g>
      <rect x="5" y="108" width="90" height="27" fill="#fbf4e4"/><rect x="5" y="108" width="90" height="2" fill="${INK}" opacity=".5"/>
      <rect x="5" y="5" width="90" height="130" rx="6" fill="none" stroke="${INK}" stroke-width="1.6"/>
    </symbol>`;
  }
  for (const k of ORDER) o += `<symbol id="gl-${k}" viewBox="-10 -10 20 20" overflow="visible"><g fill="${TYPES[k].glyphC}">${GLYPH[k]}</g></symbol>`;
  return o;
}
export const card = (k, cls = '') => `<svg class="cardart ${cls}" viewBox="0 0 100 140"><use href="#card-${k}"/></svg>`;

// ── дрібниці столу ──
export function teaGlass(size = 150) {
  return `<svg viewBox="0 0 200 200" width="${size}" height="${size}" style="overflow:visible">
    <ellipse cx="108" cy="110" rx="76" ry="74" fill="#000" opacity=".38" filter="url(#blur6)"/>
    <ellipse cx="100" cy="100" rx="80" ry="80" fill="#f1ece0"/><ellipse cx="100" cy="100" rx="80" ry="80" fill="url(#saucer)"/>
    <circle cx="100" cy="100" r="74" fill="none" stroke="#2c6fbb" stroke-width="2.4"/><circle cx="100" cy="100" r="70" fill="none" stroke="#2c6fbb" stroke-width="1" stroke-dasharray="3 5"/>
    <path d="M152 124 C176 124 182 88 156 82" fill="none" stroke="#6c6358" stroke-width="10"/><path d="M152 124 C176 124 182 88 156 82" fill="none" stroke="#d9cfb9" stroke-width="5"/>
    <circle cx="100" cy="100" r="56" fill="#b8ad97"/><circle cx="100" cy="100" r="53" fill="url(#tea)"/><circle cx="100" cy="100" r="53" fill="none" stroke="#fff" stroke-width="1.5" opacity=".8"/>
    <g transform="translate(112 112) rotate(20)"><circle r="19" fill="#ffe34d" stroke="#d49e12" stroke-width="2"/><circle r="15" fill="#fff4a8"/>${[0,1,2,3,4,5,6,7].map(i=>`<path d="M0 0 L${(13*Math.cos(i*.785)).toFixed(1)} ${(13*Math.sin(i*.785)).toFixed(1)}" stroke="#e8c02a" stroke-width="1.6"/>`).join('')}</g>
    <path d="M58 76 A52 52 0 0 1 96 50" stroke="#fff" stroke-width="5" fill="none" opacity=".55" stroke-linecap="round"/>
    <path d="M92 104 L22 170" stroke="#3a3128" stroke-width="7" stroke-linecap="round" opacity=".35" transform="translate(4 5)"/>
    <path d="M92 104 L22 170" stroke="#cfd3d7" stroke-width="5.4" stroke-linecap="round"/><ellipse cx="92" cy="104" rx="9" ry="6" fill="#cfd3d7" transform="rotate(-44 92 104)"/>
  </svg>`;
}
function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
export { rng };
export function saltScatter(w, h, n, seed = 3, heap = false) {
  const R = rng(seed); let o = '';
  for (let i = 0; i < n; i++) {
    const g = () => (R() + R() + R()) / 3;
    const x = g() * w, y = g() * h, sz = 1 + R() * R() * 3.4, a = R() * 90;
    o += `<rect x="${(x+0.8).toFixed(1)}" y="${(y+1).toFixed(1)}" width="${sz.toFixed(1)}" height="${sz.toFixed(1)}" transform="rotate(${a.toFixed(0)} ${x.toFixed(1)} ${y.toFixed(1)})" fill="#000" opacity=".45"/><rect x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${sz.toFixed(1)}" height="${sz.toFixed(1)}" transform="rotate(${a.toFixed(0)} ${x.toFixed(1)} ${y.toFixed(1)})" fill="#fbfaf5"/>`;
  }
  return `<svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}" style="overflow:visible">${o}</svg>`;
}
