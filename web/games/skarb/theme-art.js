// «Скарб Полуботка» — малюнки картки столу для трьох виглядів (§12 «Вигляд»): іконки інструментів, Дядько Глек,
// портрети копачів, «тунель у віконці», ілюстровані карти руки, ремінець-логотип, тло столу. Перенесено з дизайн-макетів
// (c1 «Казкова ніч», c2 «Дерев'яна настолка», c3 «Ніч копачів»). Лише SVG-рядки, без зовнішніх файлів.
//
// Градієнти й маски — один раз на сторінку в `defs()` (svg 0×0 у корені картки, НЕ display:none — інакше Chrome не
// малює посилання на них); карти й портрети посилаються на сталі id `skb-…`, тож той самий HTML не перемальовується.

import { SHAPES, BIT, DIRS, TOOLS } from './consts.js';

export const THEMES = ['fairy', 'wood', 'night'];
export const THEME_NAME = { fairy: 'Казкова ніч', wood: "Дерев'яна настолка", night: 'Ніч копачів' };
export const THEME_HINT = { fairy: 'фіолетове небо, золоті рамки, пергамент', wood: 'горіховий стіл, паперові мати, вишивка', night: 'кіно: поле на весь екран, ліхтарі в темряві' };
export const THEME_STYLE = { fairy: 'toon', wood: 'warm', night: 'dramatic' };   // createScene({ style })

const INK = '#2a160b';
const r1 = (v) => Math.round(v * 10) / 10;
function rng(seed) {
  let a = seed >>> 0;
  return () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
export function shade(hex, k) {   // k < 0 — темніше, k > 0 — світліше
  const n = parseInt(hex.slice(1), 16); const r = n >> 16, g = (n >> 8) & 255, b = n & 255;
  const f = (c) => Math.round(k < 0 ? c * (1 + k) : c + (255 - c) * k);
  return '#' + ((1 << 24) | (f(r) << 16) | (f(g) << 8) | f(b)).toString(16).slice(1);
}

// ─── Хрестик-вишивка ──────────────────────────────────────────────────────
export function stitch(x0, y0, s, rows, cols) {
  const by = {};
  rows.forEach((row, j) => {
    for (let i = 0; i < row.length; i++) {
      const c = cols[row[i]];
      if (!c) continue;
      const x = x0 + i * s, y = y0 + j * s, a = s * 0.16, b = s * 0.68;
      (by[c] = by[c] || []).push('M' + r1(x + a) + ' ' + r1(y + a) + 'l' + r1(b) + ' ' + r1(b) + 'M' + r1(x + a + b) + ' ' + r1(y + a) + 'l' + r1(-b) + ' ' + r1(b));
    }
  });
  return Object.keys(by).map((c) => '<path d="' + by[c].join('') + '" stroke="' + c + '" stroke-width="' + r1(s * 0.34) + '" stroke-linecap="round" fill="none"/>').join('');
}

// ─── Іконки (бокс 100×100) ────────────────────────────────────────────────
export const ico = {
  pick(o) {
    o = o || {}; const ink = o.ink || INK, br = o.broken;
    const head = '<path d="M22 30 Q50 8 82 26 L79 33 Q52 19 27 38 Z" fill="' + (o.iron || '#9aa6b2') + '" stroke="' + ink + '" stroke-width="5" stroke-linejoin="round"/>'
      + '<path d="M30 30 Q50 18 74 26" fill="none" stroke="#e6eef4" stroke-width="3" stroke-linecap="round" opacity=".7"/>'
      + '<rect x="46" y="18" width="12" height="18" rx="3" transform="rotate(38 52 27)" fill="' + (o.iron2 || '#6c7884') + '" stroke="' + ink + '" stroke-width="4.5"/>';
    if (!br) return '<path d="M54 30 L24 86" stroke="' + ink + '" stroke-width="15" stroke-linecap="round"/><path d="M54 30 L24 86" stroke="' + (o.wood || '#c98a4b') + '" stroke-width="8" stroke-linecap="round"/><path d="M50 40 L32 74" stroke="#f0c48c" stroke-width="2.5" stroke-linecap="round" opacity=".7"/>' + head;
    return '<g transform="rotate(-8 50 50)"><path d="M54 30 L44 49" stroke="' + ink + '" stroke-width="15" stroke-linecap="round"/><path d="M54 30 L44 49" stroke="' + (o.wood || '#c98a4b') + '" stroke-width="8" stroke-linecap="round"/>'
      + '<path d="M40 50 l6 2 l-3 4 l5 1 l-4 4" fill="none" stroke="' + ink + '" stroke-width="3" stroke-linejoin="round"/>' + head + '</g>'
      + '<g transform="rotate(14 40 74)"><path d="M38 62 L22 90" stroke="' + ink + '" stroke-width="15" stroke-linecap="round"/><path d="M38 62 L22 90" stroke="' + (o.wood || '#c98a4b') + '" stroke-width="8" stroke-linecap="round"/><path d="M33 60 l5 6 l3 -5 l3 5" fill="none" stroke="' + ink + '" stroke-width="3" stroke-linejoin="round"/></g>'
      + '<path d="M62 58 l10 -6 M64 68 l12 0 M60 77 l9 6" stroke="' + (o.red || '#e3122a') + '" stroke-width="4.5" stroke-linecap="round"/>';
  },
  lamp(o) {
    o = o || {}; const ink = o.ink || INK, br = o.broken, lit = !br;
    return (lit ? '<circle cx="50" cy="56" r="30" fill="' + (o.glow || '#ffcf6b') + '" opacity=".35"/>' : '')
      + '<path d="M36 22 Q50 4 64 22" fill="none" stroke="' + ink + '" stroke-width="5" stroke-linecap="round"/>'
      + '<path d="M32 26 h36 l-4 9 h-28 z" fill="' + (o.metal || '#b5532a') + '" stroke="' + ink + '" stroke-width="4.5" stroke-linejoin="round"/>'
      + '<path d="M35 36 h30 q6 16 0 38 h-30 q-6 -22 0 -38z" fill="' + (lit ? '#fff1b8' : '#7d8a92') + '" stroke="' + ink + '" stroke-width="4.5" stroke-linejoin="round"/>'
      + (lit ? '<path d="M50 44 q8 10 0 20 q-8 -10 0 -20z" fill="#ff9a2e" stroke="#e0531a" stroke-width="2"/><path d="M50 52 q3 4 0 8 q-3 -4 0 -8z" fill="#fff6c8"/>'
        : '<path d="M42 40 l6 12 l-5 6 l8 12 M56 42 l-4 10 l7 4" fill="none" stroke="' + ink + '" stroke-width="2.6" stroke-linejoin="round"/>')
      + '<path d="M38 40 v30 M62 40 v30" stroke="' + ink + '" stroke-width="3"/>'
      + '<path d="M30 74 h40 l-3 9 h-34 z" fill="' + (o.metal || '#b5532a') + '" stroke="' + ink + '" stroke-width="4.5" stroke-linejoin="round"/>'
      + (br ? '<path d="M72 30 l10 -6 M74 44 l12 0 M18 40 l-8 -6" stroke="' + (o.red || '#e3122a') + '" stroke-width="4.5" stroke-linecap="round"/>' : '');
  },
  cart(o) {
    o = o || {}; const ink = o.ink || INK, br = o.broken;
    const wheel = (cx, cy, r) => '<circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="' + (o.wood2 || '#7a4a25') + '" stroke="' + ink + '" stroke-width="4.5"/>'
      + '<circle cx="' + cx + '" cy="' + cy + '" r="' + r1(r * 0.55) + '" fill="none" stroke="' + (o.wood || '#c98a4b') + '" stroke-width="3"/>'
      + [0, 60, 120].map((a) => { const c = Math.cos(a * Math.PI / 180) * r * 0.8, s = Math.sin(a * Math.PI / 180) * r * 0.8; return '<path d="M' + r1(cx + c) + ' ' + r1(cy + s) + 'L' + r1(cx - c) + ' ' + r1(cy - s) + '" stroke="' + ink + '" stroke-width="3"/>'; }).join('')
      + '<circle cx="' + cx + '" cy="' + cy + '" r="' + r1(r * 0.18) + '" fill="' + (o.iron || '#9aa6b2') + '" stroke="' + ink + '" stroke-width="2.5"/>';
    if (o.wheel) return '<circle cx="50" cy="52" r="34" fill="' + (o.glow || '#9be07a') + '" opacity=".28"/>' + wheel(50, 52, 28)
      + '<path d="M78 18 l4 9 l9 4 l-9 4 l-4 9 l-4 -9 l-9 -4 l9 -4z" fill="#fff6c8" stroke="' + ink + '" stroke-width="2.5" stroke-linejoin="round"/>';
    return '<path d="M8 40 L26 46" stroke="' + ink + '" stroke-width="9" stroke-linecap="round"/><path d="M8 40 L26 46" stroke="' + (o.wood || '#c98a4b') + '" stroke-width="4" stroke-linecap="round"/>'
      + '<path d="M20 34 h66 l-10 30 h-46 z" fill="' + (o.body || '#8f9aa6') + '" stroke="' + ink + '" stroke-width="5" stroke-linejoin="round"/>'
      + '<path d="M28 40 h50" stroke="#e6eef4" stroke-width="3" opacity=".6" stroke-linecap="round"/>'
      + '<path d="M36 64 l-4 14 M62 64 l4 14" stroke="' + ink + '" stroke-width="5" stroke-linecap="round"/>'
      + (br ? '<path d="M64 74 l12 8 M70 66 l14 0" stroke="' + (o.red || '#e3122a') + '" stroke-width="4.5" stroke-linecap="round"/><circle cx="82" cy="86" r="9" fill="none" stroke="' + ink + '" stroke-width="3" stroke-dasharray="5 4" opacity=".7"/>' : wheel(48, 78, 14));
  },
  map(o) {
    o = o || {}; const ink = o.ink || INK;
    return '<path d="M14 22 l22 -8 l26 8 l24 -8 v62 l-24 8 l-26 -8 l-22 8 z" fill="' + (o.paper || '#efdcaa') + '" stroke="' + ink + '" stroke-width="4.5" stroke-linejoin="round"/>'
      + '<path d="M36 14 v62 M62 22 v62" stroke="' + ink + '" stroke-width="2.5" opacity=".35"/>'
      + '<path d="M14 22 l22 -8 v62 l-22 8z" fill="#000" opacity=".07"/><path d="M62 22 l24 -8 v62 l-24 8z" fill="#000" opacity=".05"/>'
      + '<path d="M22 66 q10 -18 22 -10 t20 -14 t14 -18" fill="none" stroke="' + (o.red || '#c8202a') + '" stroke-width="3.5" stroke-dasharray="1 6.5" stroke-linecap="round"/>'
      + '<path d="M70 18 l10 10 M80 18 l-10 10" stroke="' + (o.red || '#c8202a') + '" stroke-width="5" stroke-linecap="round"/>'
      + '<circle cx="24" cy="38" r="7" fill="none" stroke="' + ink + '" stroke-width="2.2" opacity=".7"/><path d="M24 31 v14 M17 38 h14" stroke="' + ink + '" stroke-width="1.6" opacity=".7"/>';
  },
  rock(o) {
    o = o || {}; const ink = o.ink || INK, st = o.stone || '#9a9ca2', st2 = o.stone2 || '#767880';
    const s = (d, f) => '<path d="' + d + '" fill="' + f + '" stroke="' + ink + '" stroke-width="4" stroke-linejoin="round"/>';
    return '<path d="M30 8 l2 8 M50 4 v9 M68 9 l-3 8" stroke="' + ink + '" stroke-width="3" stroke-linecap="round" opacity=".55"/>'
      + s('M40 18 l10 -4 l8 6 l-4 9 l-11 1 z', st) + s('M64 24 l7 -2 l4 6 l-5 5 l-6 -3 z', st2)
      + s('M10 88 l6 -18 l16 -8 l14 6 l2 20 z', st2) + s('M40 88 l4 -22 l18 -10 l18 8 l6 24 z', st)
      + s('M24 88 l6 -14 l14 0 l6 14 z', '#b4b6bb')
      + '<path d="M52 66 l8 -4 M66 70 l6 4" stroke="#e7e8ea" stroke-width="3" stroke-linecap="round" opacity=".6"/>'
      + '<path d="M6 92 h88" stroke="' + ink + '" stroke-width="4" stroke-linecap="round" opacity=".6"/>'
      + '<g fill="' + ink + '" opacity=".35"><circle cx="20" cy="60" r="2.5"/><circle cx="84" cy="62" r="2"/><circle cx="30" cy="52" r="1.8"/><circle cx="76" cy="50" r="1.6"/></g>';
  },
  coin() {
    return '<ellipse cx="50" cy="56" rx="40" ry="38" fill="#9a6512"/><circle cx="50" cy="50" r="40" fill="url(#skb-coin)" stroke="#6b4208" stroke-width="5"/>'
      + '<circle cx="50" cy="50" r="29" fill="none" stroke="#fff3c0" stroke-width="3" opacity=".55"/>'
      + '<path d="M50 30 l5 12 l13 1 l-10 8 l4 13 l-12 -7 l-12 7 l4 -13 l-10 -8 l13 -1z" fill="#fff3c0" opacity=".8"/>';
  },
  chest() {
    return '<path d="M14 50 q36 -34 72 0 v8 h-72z" fill="url(#skb-chw)" stroke="#2a160b" stroke-width="4"/><rect x="14" y="56" width="72" height="32" rx="4" fill="url(#skb-chw)" stroke="#2a160b" stroke-width="4"/>'
      + '<path d="M14 62 h72 M30 50 v38 M70 50 v38" stroke="#e0a030" stroke-width="5"/><rect x="43" y="60" width="14" height="14" rx="3" fill="#ffd35a" stroke="#2a160b" stroke-width="3"/>';
  },
};
export const icoSvg = (name, o, cls) => '<svg class="' + (cls || '') + '" viewBox="0 0 100 100" aria-hidden="true">' + ico[name](o) + '</svg>';
export const coinSvg = (cls) => '<svg class="skb-coinic ' + (cls || '') + '" viewBox="0 0 100 100" aria-hidden="true">' + ico.coin() + '</svg>';
export const chestSvg = (cls) => '<svg class="' + (cls || '') + '" viewBox="0 0 100 100" aria-hidden="true"><circle cx="50" cy="44" r="40" fill="#ffd35a" opacity=".22"/>'
  + '<g transform="translate(26 6) scale(.48)">' + ico.coin() + '</g>' + ico.chest() + '</svg>';

// ─── Дядько Глек (128×128) ───────────────────────────────────────────────
function glekBody(o) {
  o = o || {}; const I = INK;
  return '<path d="M92 44C118 44 120 88 94 96" fill="none" stroke="' + I + '" stroke-width="15" stroke-linecap="round"/>'
    + '<path d="M92 44C118 44 120 88 94 96" fill="none" stroke="url(#skb-clayl)" stroke-width="8.5" stroke-linecap="round"/>'
    + '<path d="M46 22h36v10c14 6 24 22 24 42c0 22-14 40-42 40c-28 0-42-18-42-40c0-20 10-36 24-42z" fill="url(#skb-clay)" stroke="' + I + '" stroke-width="4.5" stroke-linejoin="round"/>'
    + '<path d="M96 56c6 10 6 26 0 36c-5 9-14 16-26 18c16-8 28-26 26-54z" fill="#6e2c10" opacity=".28"/>'
    + '<path d="M34.7 40H93.3Q96.5 45 98.6 50.5H29.4Q31.5 45 34.7 40Z" fill="#f6ead0" stroke="' + I + '" stroke-width="2.2" stroke-linejoin="round"/>'
    + stitch(35, 41, 3, ['.x...x...x...x...x..', 'xox.xox.xox.xox.xox.', '.x...x...x...x...x..'], { x: '#e3122a', o: I })
    + '<rect x="41" y="14" width="46" height="14" rx="6" fill="url(#skb-clayl)" stroke="' + I + '" stroke-width="4.5"/>'
    + '<ellipse cx="64" cy="19.5" rx="16" ry="3.4" fill="#4a1e0c" opacity=".75"/>'
    + '<path d="M60 16c-4-11-19-14-28-5c9-4 17-2 21 7z" fill="#2b1a0e" stroke="' + I + '" stroke-width="2" stroke-linejoin="round"/>'
    + '<path d="M33 64c0-12 6-22 14-27" fill="none" stroke="#ffe0c0" stroke-width="4.5" stroke-linecap="round" opacity=".65"/>'
    + (o.phones === false ? '' : '<path d="M25 73C21 37 107 37 103 73" fill="none" stroke="' + I + '" stroke-width="9" stroke-linecap="round"/>'
      + '<path d="M25 73C21 37 107 37 103 73" fill="none" stroke="#33463e" stroke-width="5" stroke-linecap="round"/>'
      + '<rect x="13" y="64" width="18" height="29" rx="7" fill="#2a3a33" stroke="' + I + '" stroke-width="3"/><rect x="17" y="68" width="10" height="21" rx="4" fill="none" stroke="#f4c542" stroke-width="2"/>'
      + '<rect x="97" y="64" width="18" height="29" rx="7" fill="#2a3a33" stroke="' + I + '" stroke-width="3"/><rect x="101" y="68" width="10" height="21" rx="4" fill="none" stroke="#f4c542" stroke-width="2"/>')
    + '<ellipse cx="52" cy="70" rx="8.6" ry="9.2" fill="#fffaf0" stroke="' + I + '" stroke-width="2.4"/><circle cx="53.4" cy="70" r="4.6" fill="#1a0e12"/><circle cx="55.2" cy="68" r="1.7" fill="#fff"/>'
    + (o.wink ? '<path d="M68 71 q8 -6 16 0" fill="none" stroke="' + I + '" stroke-width="3.6" stroke-linecap="round"/>' : '<ellipse cx="76" cy="70" rx="8.6" ry="9.2" fill="#fffaf0" stroke="' + I + '" stroke-width="2.4"/><circle cx="74.6" cy="70" r="4.6" fill="#1a0e12"/><circle cx="76.4" cy="68" r="1.7" fill="#fff"/>')
    + '<path d="M42 59Q49 52 59 56M86 59Q79 52 69 56" fill="none" stroke="#2b1a0e" stroke-width="4.6" stroke-linecap="round"/>'
    + '<circle cx="41" cy="85" r="5.5" fill="#ff7a5a" opacity=".55"/><circle cx="87" cy="85" r="5.5" fill="#ff7a5a" opacity=".55"/>'
    + '<path d="M51 99Q64 116 77 99Q64 103 51 99Z" fill="#6a1a10" stroke="' + I + '" stroke-width="2.4" stroke-linejoin="round"/><path d="M58 106Q64 112 70 106Q64 104 58 106Z" fill="#ff6a5a"/>'
    + '<path d="M64 88c-6-6-18-4-24 4c-4 6-8 10-12 8c4-2 6-8 10-12c8-8 22-8 26 0c4-8 18-8 26 0c4 4 6 10 10 12c-4 2-8-2-12-8c-6-8-18-10-24-4z" fill="#2b1a0e" stroke="' + I + '" stroke-width="1.6" stroke-linejoin="round"/>'
    + '<ellipse cx="64" cy="82" rx="5.4" ry="4.2" fill="#a34e22" stroke="' + I + '" stroke-width="1.8"/><circle cx="62.4" cy="80.6" r="1.4" fill="#ffd0a8"/>'
    + (o.hat ? '<path d="M20 22 q44 -16 88 0 q-6 6 -44 6 q-38 0 -44 -6z" fill="#e9c46a" stroke="' + I + '" stroke-width="3.6" stroke-linejoin="round"/><path d="M42 18 q22 -22 44 0" fill="#f1d27e" stroke="' + I + '" stroke-width="3.6"/><path d="M44 16 q20 -6 40 0" stroke="#c8202a" stroke-width="4" fill="none"/>' : '');
}
export const glekSvg = (o, cls) => '<svg class="skb-glekic ' + (cls || '') + '" viewBox="0 0 128 128" aria-hidden="true">' + glekBody(o) + '</svg>';

// ─── Портрети (бокс 100×100) ─────────────────────────────────────────────
function face(kind, color) {
  const I = INK, skin = '#f2c39a', skinD = '#d99a70', shirtD = shade(color, -0.3);
  let hair = '', hat = '', extra = '';
  const body = '<path d="M12 104 q4 -30 38 -32 q34 2 38 32z" fill="' + color + '" stroke="' + I + '" stroke-width="3.5"/>'
    + '<path d="M38 74 q12 10 24 0 l-2 8 q-10 6 -20 0z" fill="#f6ead0" stroke="' + I + '" stroke-width="2.4"/>'
    + '<path d="M50 80 v24" stroke="' + shirtD + '" stroke-width="3"/>'
    + '<g transform="translate(37 84)">' + stitch(0, 0, 2.2, ['x.x.x.x.x.x', '.o.o.o.o.o.'], { x: '#f6ead0', o: '#1a0e0a' }) + '</g>';
  const head = '<ellipse cx="50" cy="50" rx="21" ry="23" fill="' + skin + '" stroke="' + I + '" stroke-width="3.5"/>'
    + '<ellipse cx="29" cy="53" rx="4" ry="6" fill="' + skin + '" stroke="' + I + '" stroke-width="3"/><ellipse cx="71" cy="53" rx="4" ry="6" fill="' + skin + '" stroke="' + I + '" stroke-width="3"/>';
  const eyes = '<ellipse cx="42" cy="52" rx="2.8" ry="3.4" fill="' + I + '"/><ellipse cx="58" cy="52" rx="2.8" ry="3.4" fill="' + I + '"/><circle cx="43" cy="51" r="1" fill="#fff"/><circle cx="59" cy="51" r="1" fill="#fff"/>'
    + '<circle cx="36" cy="60" r="4" fill="#ff7a5a" opacity=".45"/><circle cx="64" cy="60" r="4" fill="#ff7a5a" opacity=".45"/>';
  if (kind === 'man') {
    hat = '<path d="M14 40 q36 -12 72 0 q-6 6 -36 6 q-30 0 -36 -6z" fill="#e2b85c" stroke="' + I + '" stroke-width="3.5" stroke-linejoin="round"/><path d="M32 38 q2 -20 18 -20 q16 0 18 20z" fill="#efcd78" stroke="' + I + '" stroke-width="3.5"/><path d="M33 33 q17 -5 34 0" stroke="#c8202a" stroke-width="4" fill="none"/>';
    extra = '<path d="M50 63 c-4 -3 -12 -3 -16 3 c4 -1 6 0 8 2 c3 -1 6 -2 8 -5 c2 3 5 4 8 5 c2 -2 4 -3 8 -2 c-4 -6 -12 -6 -16 -3z" fill="#5a3419" stroke="' + I + '" stroke-width="1.8"/><path d="M46 70 q4 3 8 0" stroke="' + I + '" stroke-width="2.4" fill="none" stroke-linecap="round"/>';
  } else if (kind === 'man2') {
    hair = '<path d="M30 44 q0 -22 20 -22 q20 0 20 22 q-6 -10 -20 -10 q-14 0 -20 10z" fill="#3a2414" stroke="' + I + '" stroke-width="3"/>';
    hat = '<path d="M28 34 q22 -26 44 0 q-22 6 -44 0z" fill="#2b2b33" stroke="' + I + '" stroke-width="3.5"/><path d="M26 34 q24 8 48 0" stroke="' + I + '" stroke-width="3.5" fill="#3d3d48"/>';
    extra = '<path d="M38 66 q12 14 24 0 q-2 12 -12 12 q-10 0 -12 -12z" fill="#3a2414" stroke="' + I + '" stroke-width="2"/><path d="M45 66 q5 3 10 0" stroke="#f2c39a" stroke-width="2.4" fill="none"/>';
  } else if (kind === 'woman') {
    hair = '<path d="M28 54 q-4 -32 22 -32 q26 0 22 32 q-4 -18 -22 -20 q-18 2 -22 20z" fill="#7a3f1d" stroke="' + I + '" stroke-width="3"/>'
      + '<path d="M70 56 q10 14 4 34" stroke="' + I + '" stroke-width="9" fill="none" stroke-linecap="round"/><path d="M70 56 q10 14 4 34" stroke="#7a3f1d" stroke-width="5" fill="none" stroke-linecap="round"/>';
    hat = '<g stroke="' + I + '" stroke-width="2.4">' + ['#e3122a', '#f4c542', '#3c78d8', '#e3122a', '#f4c542'].map((c, i) => '<circle cx="' + (32 + i * 9) + '" cy="' + r1(30 - Math.sin((i / 4) * Math.PI) * 6) + '" r="5.5" fill="' + c + '"/>').join('') + '</g>'
      + '<path d="M28 34 q8 -2 10 4 M72 34 q-8 -2 -10 4" stroke="#2f6b3a" stroke-width="3" fill="none"/>';
    extra = '<path d="M44 68 q6 4 12 0" stroke="' + I + '" stroke-width="2.4" fill="none" stroke-linecap="round"/>';
  } else {   // woman2 — хустка
    hat = '<path d="M24 56 q-2 -36 26 -36 q28 0 26 36 q-6 -8 -8 -20 q-18 -8 -36 0 q-2 12 -8 20z" fill="' + shade(color, -0.1) + '" stroke="' + I + '" stroke-width="3.2"/>'
      + '<g fill="#fff6e0" opacity=".9"><circle cx="38" cy="28" r="2"/><circle cx="50" cy="24" r="2"/><circle cx="62" cy="28" r="2"/><circle cx="30" cy="40" r="2"/><circle cx="70" cy="40" r="2"/></g>'
      + '<path d="M30 36 q20 -10 40 0" stroke="' + I + '" stroke-width="2.4" fill="none"/>';
    extra = '<path d="M44 68 q6 4 12 0" stroke="' + I + '" stroke-width="2.4" fill="none" stroke-linecap="round"/>';
  }
  return '<path d="M50 70 v6" stroke="' + skinD + '" stroke-width="10"/>' + body + hair + head + eyes + extra + hat;
}
const hashStr = (s) => { let h = 2166136261; for (const ch of String(s || '')) h = Math.imul(h ^ ch.codePointAt(0), 16777619); return h >>> 0; };
/** Обличчя за ніком: Глек-бот — глечик у брилі; жіночі імена (на -а/-я/-і) — дівчата; решта — хлопці. */
export function faceOf(p) {
  if (p && p.bot) return 'glek';
  const n = String((p && p.nick) || '').trim().toLowerCase().replace(/[^a-zа-яіїєґ']/g, '');
  const h = hashStr(n);
  const fem = /[аяі]$/.test(n) && !['микола', 'сава', 'фома', 'кузьма', 'ілля', 'лука', 'никита', 'макітра'].includes(n);
  return fem ? (h & 1 ? 'woman2' : 'woman') : (h & 1 ? 'man2' : 'man');
}
export function avatarSvg(p, color) {
  const k = faceOf(p);
  if (k === 'glek') return '<svg class="skb-face" viewBox="0 0 128 128" aria-hidden="true"><g transform="translate(6 12) scale(.92)">' + glekBody({ hat: true }) + '</g></svg>';
  return '<svg class="skb-face" viewBox="0 0 100 100" aria-hidden="true">' + face(k, color) + '</svg>';
}

// ─── «Тунель у віконці» (бокс 100×100) ───────────────────────────────────
const TILE = {
  fairy: { soil: ['#c27f40', '#8c4f24'], tunnel: '#1c0f0a', wood: '#c48a4e', lamp: '#ffc062' },
  wood: { soil: ['#b88452', '#94663a'], tunnel: '#2e1e13', wood: '#c08850', lamp: '#ffd27a' },
  night: { soil: ['#4a3118', '#2a1a0c'], tunnel: '#0a0604', wood: '#9a6a3a', lamp: '#ffb04a' },
};
export function tileArt(theme, m, c) {
  const t = TILE[theme] || TILE.fairy, W = 34, has = (d) => !!(m & BIT[d]);
  const lines = [];
  for (const d of DIRS) {
    if (!has(d)) continue;
    lines.push(c ? { N: 'M50 50 V-4', E: 'M50 50 H104', S: 'M50 50 V104', W: 'M50 50 H-4' }[d] : { N: 'M50 -4 V26', E: 'M104 50 H74', S: 'M50 104 V74', W: 'M-4 50 H26' }[d]);
  }
  const p = lines.join(' ');
  let s = '<g clip-path="url(#skb-tclip)"><rect width="100" height="100" fill="url(#skb-ts-' + theme + ')"/>';
  const R = rng(m * 31 + (c ? 7 : 3));
  for (let i = 0; i < 14; i++) s += '<ellipse cx="' + r1(R() * 100) + '" cy="' + r1(R() * 100) + '" rx="' + r1(2 + R() * 4) + '" ry="' + r1(1.5 + R() * 2.5) + '" fill="' + (R() < 0.5 ? shade(t.soil[1], -0.25) : shade(t.soil[0], 0.15)) + '" opacity=".6"/>';
  if (p) s += '<path d="' + p + '" stroke="#000" stroke-opacity=".38" stroke-width="' + (W + 10) + '" stroke-linecap="square" fill="none"/>'
    + '<path d="' + p + '" stroke="' + t.tunnel + '" stroke-width="' + W + '" stroke-linecap="square" fill="none"/>';
  for (const d of DIRS) {
    if (!has(d)) continue;
    const k = c ? 14 : 18;
    if (d === 'E' || d === 'W') { const x = d === 'E' ? 100 - k : k; s += '<rect x="' + (x - 3.5) + '" y="' + (50 - W / 2 - 3) + '" width="7" height="' + (W + 6) + '" rx="1.5" fill="' + t.wood + '" stroke="' + INK + '" stroke-width="2"/>'; }
    else { const y = d === 'S' ? 100 - k : k; s += '<rect y="' + (y - 3.5) + '" x="' + (50 - W / 2 - 3) + '" height="7" width="' + (W + 6) + '" rx="1.5" fill="' + t.wood + '" stroke="' + INK + '" stroke-width="2"/>'; }
  }
  if (c) s += '<circle cx="50" cy="50" r="40" fill="url(#skb-tg-' + theme + ')"/><path d="M50 ' + (50 - W / 2) + ' v6" stroke="' + INK + '" stroke-width="2"/><rect x="45" y="' + (56 - W / 2) + '" width="10" height="12" rx="2.5" fill="#fff1b8" stroke="' + INK + '" stroke-width="2.4"/>';
  else s += '<path d="M36 62 q-2 -16 14 -18 q14 0 16 14 q0 8 -14 10 q-12 0 -16 -6z" fill="#8d8f94" stroke="' + INK + '" stroke-width="3"/><path d="M44 52 q6 -4 12 0" stroke="#c9cbd0" stroke-width="3" fill="none"/>';
  return s + '</g>';
}

// ─── Карти руки (бокс 100×140) ──────────────────────────────────────────
/** Малюнок дії (бокс 100×100): злам — розбитий інструмент, ремонт — цілий з сяйвом, мапа, завал. */
function actionArt(card) {
  if (card.k === 'break') return ico[card.tool] ? ico[card.tool]({ broken: true }) : ico.pick({ broken: true });
  if (card.k === 'map') return ico.map();
  if (card.k === 'rockfall') return ico.rock();
  if (card.k === 'fix') {
    const t = (card.tools || []).filter((x) => TOOLS.includes(x));
    const one = (x) => (x === 'cart' ? ico.cart({ wheel: true }) : '<circle cx="50" cy="52" r="34" fill="#9be07a" opacity=".28"/>' + ico[x]({}));
    if (t.length < 2) return one(t[0] || 'pick');
    return '<g transform="translate(-2 14) scale(.58)">' + one(t[0]) + '</g><g transform="translate(44 14) scale(.58)">' + one(t[1]) + '</g>'
      + '<path d="M50 10 l3 7 l7 3 l-7 3 l-3 7 l-3 -7 l-7 -3 l7 -3z" fill="#fff6c8" stroke="' + INK + '" stroke-width="2"/>';
  }
  return '';
}
function nameLines(name, x, y, fs, fill, weight) {
  const nm = String(name || '').split(' ');
  const two = nm.length > 1 && name.length > 10;
  const T = (yy, t) => '<text x="' + x + '" y="' + r1(yy) + '" text-anchor="middle" font-family="Onest, system-ui, sans-serif" font-weight="' + weight + '" font-size="' + fs + '" fill="' + fill + '">' + t + '</text>';
  if (!two) return T(y + 5, name);
  const cut = nm.length > 2 && nm[0].length < 5 ? 2 : 1;
  return T(y, nm.slice(0, cut).join(' ')) + T(y + fs * 1.15, nm.slice(cut).join(' '));
}
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const CAT = {
  fairy: { path: ['ХІД', '#a3642f', '#6e3a18'], dead: ['ГЛУХИЙ', '#6a5a4a', '#3a2a1a'], break: ['ЗЛАМАТИ', '#c8202a', '#5a0a14'], fix: ['ПОЛАГОДИТИ', '#2e9e6a', '#0f4a32'], map: ['ПІДГЛЯНУТИ', '#3c78d8', '#16306a'], rockfall: ['ЗАСИПАТИ', '#7a6aa0', '#2e2450'] },
  wood: { path: ['Хід', '#9a6634', '#f1dfbf'], dead: ['Глухий', '#6e5440', '#e6dccb'], break: ['Злам', '#b3261e', '#f6d2c8'], fix: ['Ремонт', '#2f7a4a', '#d6ead2'], map: ['Мапа', '#2f5f9a', '#d4e0ee'], rockfall: ['Завал', '#5a5470', '#e0dce8'] },
  night: { path: ['ХІД', '#e9b65a'], dead: ['ГЛУХИЙ', '#a8998a'], break: ['ЗЛАМАТИ', '#ff6a50'], fix: ['ПОЛАГОДИТИ', '#7bd389'], map: ['ПІДГЛЯНУТИ', '#7fb0ff'], rockfall: ['ЗАСИПАТИ', '#c0b0e0'] },
};
const WOOD_BADGE = { path: '⛏', dead: '●', break: '✕', fix: '+', map: '?', rockfall: '▲' };

/**
 * Лице карти: theme — fairy | wood | night; card — { k, s?, tool?, tools? } з виду; m — маска (вже з поворотом) для ходу;
 * name — підпис; cmp — стисла (телефон: більший шрифт, без оздоб).
 */
export function cardSvg(theme, card, m, name, cmp) {
  const dead = card.k === 'path' && SHAPES[card.s] && !SHAPES[card.s].c;
  const kind = dead ? 'dead' : card.k;
  const nm = esc(name);
  const open = '<svg class="skb-cardart" viewBox="0 0 100 140" aria-hidden="true">';
  if (theme === 'wood') {
    const cat = CAT.wood[kind] || CAT.wood.path;
    const art = card.k === 'path'
      ? '<g transform="translate(7 7) scale(.86)">' + tileArt('wood', m, !dead) + '</g>'
      : '<rect x="7" y="7" width="86" height="86" fill="' + cat[2] + '"/>'
        + '<g opacity=".22">' + stitch(7, 7, 4.3, ['x...x...x...x...x...', '.o...o...o...o...o..', '..x...x...x...x...x.', '', '', '', '', '', '', '', '', '', '', '', '', '', '', 'x...x...x...x...x...', '.o...o...o...o...o..', '..x...x...x...x...x.'], { x: cat[1], o: INK }) + '</g>'
        + '<circle cx="50" cy="52" r="34" fill="#fff" opacity=".55"/><g transform="translate(15 17) scale(.7)">' + actionArt(card) + '</g>';
    return open + '<rect x="1" y="1" width="98" height="138" rx="9" fill="url(#skb-w-cs)" stroke="' + INK + '" stroke-width="1.8"/>'
      + '<g clip-path="url(#skb-w-win)">' + art + '</g><rect x="7" y="7" width="86" height="86" rx="6" fill="none" stroke="' + INK + '" stroke-width="2"/>'
      + '<g transform="translate(16 93)"><circle r="11" fill="' + cat[1] + '" stroke="#fffaf0" stroke-width="2.5"/><circle r="11" fill="none" stroke="' + INK + '" stroke-width="1.4"/><text y="3.8" text-anchor="middle" font-family="Onest, system-ui, sans-serif" font-weight="900" font-size="10" fill="#fff">' + WOOD_BADGE[kind] + '</text></g>'
      + '<text x="92" y="102" text-anchor="end" font-family="Onest, system-ui, sans-serif" font-weight="800" font-size="7.5" letter-spacing=".8" fill="' + cat[1] + '">' + cat[0].toUpperCase() + '</text>'
      + nameLines(nm, 50, cmp ? 111 : 112, cmp ? 13.5 : 11.5, INK, 900)
      + (cmp ? '' : '<g transform="translate(26 131)">' + stitch(0, 0, 2.4, ['x.x.x.x.x.x.x.x.x.x.'], { x: '#b3261e' }) + '</g>')
      + '</svg>';
  }
  if (theme === 'night') {
    const cat = CAT.night[kind] || CAT.night.path;
    const art = card.k === 'path'
      ? '<g transform="translate(10 18) scale(.8)">' + tileArt('night', m, !dead) + '</g>'
      : '<rect x="10" y="18" width="80" height="80" fill="url(#skb-n-sp-' + kind + ')"/><g transform="translate(16 24) scale(.68)">' + actionArt(card) + '</g>';
    return open + '<rect x="1" y="1" width="98" height="138" rx="8" fill="url(#skb-n-b)" stroke="url(#skb-n-g)" stroke-width="2"/>'
      + '<rect x="5" y="5" width="90" height="130" rx="5" fill="none" stroke="#e9b65a" stroke-opacity=".35" stroke-width="1"/>'
      + '<text x="50" y="13.5" text-anchor="middle" font-family="Onest, system-ui, sans-serif" font-weight="700" font-size="6" letter-spacing="1.6" fill="' + cat[1] + '">' + cat[0] + '</text>'
      + '<g clip-path="url(#skb-n-win)">' + art + '</g><rect x="10" y="18" width="80" height="80" rx="3" fill="none" stroke="#e9b65a" stroke-opacity=".6" stroke-width="1"/>'
      + '<path d="M30 103 h40" stroke="#e9b65a" stroke-opacity=".5" stroke-width=".8"/><path d="M50 100.5 l2.5 2.5 l-2.5 2.5 l-2.5 -2.5z" fill="#e9b65a"/>'
      + nameLines(nm, 50, cmp ? 115 : 114, cmp ? 13 : 11, '#f3e9d8', 700) + '</svg>';
  }
  // fairy
  const cat = CAT.fairy[kind] || CAT.fairy.path;
  const wx = cmp ? 9 : 14, wy = cmp ? 20 : 22, ww = cmp ? 82 : 72, cy = wy + ww / 2;
  const art = card.k === 'path'
    ? '<g transform="translate(' + wx + ' ' + wy + ') scale(' + ww / 100 + ')">' + tileArt('fairy', m, !dead) + '</g>'
    : '<rect x="' + wx + '" y="' + wy + '" width="' + ww + '" height="' + ww + '" rx="9" fill="url(#skb-f-bg-' + kind + ')"/>'
      + '<g opacity=".16" fill="#fff">' + [0, 1, 2, 3, 4, 5, 6, 7].map((k) => '<path transform="rotate(' + (k * 45 + 22) + ' 50 ' + cy + ')" d="M50 ' + cy + ' l-6 -48 h12z"/>').join('') + '</g>'
      + '<g transform="translate(' + r1(wx + ww * 0.08) + ' ' + r1(wy + ww * 0.08) + ') scale(' + r1(ww * 0.0084 * 100) / 100 + ')">' + actionArt(card) + '</g>';
  const rw = Math.max(30, cat[0].length * 4.9 + 10);
  return open + '<rect x="1" y="1" width="98" height="138" rx="11" fill="url(#skb-f-fr)" stroke="#3a1d04" stroke-width="2"/>'
    + '<rect x="5" y="5" width="90" height="130" rx="8" fill="url(#skb-f-pa)" stroke="#8a4a0c" stroke-width="1"/>'
    + (cmp ? '' : '<path d="M9 14 q6 -6 12 0 M79 14 q6 -6 12 0" stroke="#c8202a" stroke-width="1.6" fill="none" opacity=".7"/>')
    + '<rect x="' + r1(50 - rw / 2) + '" y="' + (cmp ? 6 : 8) + '" width="' + r1(rw) + '" height="11" rx="5.5" fill="' + cat[1] + '"/><text x="50" y="' + (cmp ? 14.2 : 16.2) + '" text-anchor="middle" font-family="Onest, system-ui, sans-serif" font-weight="900" font-size="6.6" letter-spacing=".5" fill="#fff">' + cat[0] + '</text>'
    + '<g clip-path="url(#skb-f-win' + (cmp ? 'c' : '') + ')">' + art + '</g>'
    + '<rect x="' + wx + '" y="' + wy + '" width="' + ww + '" height="' + ww + '" rx="9" fill="none" stroke="#3a1d04" stroke-width="2.2"/>'
    + '<rect x="' + (wx + 2) + '" y="' + (wy + 2) + '" width="' + (ww - 4) + '" height="' + (ww - 4) + '" rx="7.5" fill="none" stroke="#fff" stroke-opacity=".35" stroke-width="1"/>'
    + nameLines(nm, 50, cmp ? 116 : 110, cmp ? 13.5 : 10.5, '#3a1d04', 800)
    + (cmp ? '' : '<g transform="translate(34 128)" opacity=".75">' + stitch(0, 0, 2.4, ['x.x.x.x.x.x.x', '.o.o.o.o.o.o.'], { x: '#c8202a', o: '#2a160b' }) + '</g>')
    + '</svg>';
}

/** Сорочка карти (колода, скид пасом). */
export function backSvg(theme) {
  if (theme === 'wood') return '<svg class="skb-cardart" viewBox="0 0 100 140" aria-hidden="true"><rect x="1" y="1" width="98" height="138" rx="9" fill="url(#skb-w-back)" stroke="#f7eedb" stroke-width="6"/><rect x="1" y="1" width="98" height="138" rx="9" fill="none" stroke="' + INK + '" stroke-width="2"/>'
    + '<circle cx="50" cy="70" r="24" fill="#f7eedb" stroke="' + INK + '" stroke-width="2"/><g transform="translate(32 52) scale(.36)">' + ico.lamp() + '</g></svg>';
  if (theme === 'night') return '<svg class="skb-cardart" viewBox="0 0 100 140" aria-hidden="true"><rect x="1" y="1" width="98" height="138" rx="8" fill="url(#skb-n-b)" stroke="url(#skb-n-g)" stroke-width="2"/>'
    + '<path d="M50 40 l22 30 l-22 30 l-22 -30z" fill="none" stroke="#8a6420" stroke-width="2"/><path d="M50 58 l9 12 l-9 12 l-9 -12z" fill="#e9b65a" opacity=".6"/></svg>';
  return '<svg class="skb-cardart" viewBox="0 0 100 140" aria-hidden="true"><rect x="1" y="1" width="98" height="138" rx="11" fill="url(#skb-f-back)" stroke="#ffd88a" stroke-width="3"/>'
    + '<path d="M50 30 l24 40 l-24 40 l-24 -40z" fill="none" stroke="#ffd88a" stroke-width="3"/><g transform="translate(30 50) scale(.4)">' + ico.lamp() + '</g></svg>';
}

// ─── Оздоби ─────────────────────────────────────────────────────────────
/** Петриківський кутик рамки поля (Казкова ніч). */
export const ORN = '<svg class="skb-orn" viewBox="0 0 60 60" aria-hidden="true"><path d="M4 4 C4 30 14 40 40 42" fill="none" stroke="#ffe08a" stroke-width="3"/><path d="M4 4 C26 4 36 14 38 38" fill="none" stroke="#e0a030" stroke-width="2.4"/>'
  + '<path d="M22 12 q10 -8 16 2 q-10 6 -16 -2z" fill="#e3122a" stroke="#2a160b" stroke-width="1.2"/><path d="M12 22 q-8 10 2 16 q6 -10 -2 -16z" fill="#e3122a" stroke="#2a160b" stroke-width="1.2"/>'
  + '<circle cx="8" cy="8" r="5" fill="#ffe08a" stroke="#2a160b" stroke-width="1.4"/><circle cx="42" cy="44" r="3" fill="#ffe08a"/><circle cx="28" cy="28" r="2.4" fill="#ffe08a"/></svg>';

/** Плитка вишивки 24×24 для рамки поля (Дерев'яна настолка) — data-URL. */
export function stitchTileUrl() {
  const t = '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"><rect width="24" height="24" fill="#f1e6cc"/>'
    + stitch(0, 0, 3, ['...x....', '..xox...', '.xo.ox..', 'xo.r.ox.', '.xo.ox..', '..xox...', '...x....', '........'], { x: '#b3261e', o: '#2a160b', r: '#b3261e' }) + '</svg>';
  return 'url("data:image/svg+xml,' + encodeURIComponent(t) + '")';
}

/** Горіхова стільниця (Дерев'яна настолка) — повнорозмірне тло. */
export const WOOD_TABLE = '<svg class="skb-table" preserveAspectRatio="none" viewBox="0 0 1600 900" aria-hidden="true"><defs>'
  + '<filter id="skb-grain" x="0" y="0" width="100%" height="100%"><feTurbulence type="fractalNoise" baseFrequency="0.0035 0.09" numOctaves="4" seed="4"/><feColorMatrix values="0 0 0 0 .36  0 0 0 0 .2  0 0 0 0 .09  0 0 0 1.1 -.25"/></filter>'
  + '<linearGradient id="skb-tb" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#7a4a26"/><stop offset="1" stop-color="#5a3519"/></linearGradient></defs>'
  + '<rect width="1600" height="900" fill="url(#skb-tb)"/><rect width="1600" height="900" filter="url(#skb-grain)" opacity=".85"/>'
  + [150, 300, 450, 600, 750].map((y) => '<path d="M0 ' + y + 'H1600" stroke="#2a1408" stroke-width="3" opacity=".55"/><path d="M0 ' + (y + 2) + 'H1600" stroke="#a06a3a" stroke-width="1" opacity=".4"/>').join('') + '</svg>';

/** Невеличке прев'ю вигляду для вибору «🎨 Вигляд» (SVG 72×44). */
export function previewSvg(theme) {
  const P = {
    fairy: ['#2b1a4d', '#1d1236', '#f5c33b', '#c27a14', '#8a522a', '#fff7e2'],
    wood: ['#7a4a26', '#5a3519', '#f3e8d0', '#b3261e', '#b97c45', '#fbf4e4'],
    night: ['#07060a', '#0a0f22', '#e9b65a', '#8a6420', '#33200f', '#211912'],
  }[theme] || [];
  const [bg1, bg2, acc, acc2, soil, card] = P;
  let s = '<svg class="skb-prev" viewBox="0 0 72 44" aria-hidden="true"><defs><linearGradient id="skb-pv-' + theme + '" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="' + bg1 + '"/><stop offset="1" stop-color="' + bg2 + '"/></linearGradient></defs>'
    + '<rect width="72" height="44" rx="6" fill="url(#skb-pv-' + theme + ')"/>';
  if (theme === 'night') {
    s += '<rect x="0" y="6" width="72" height="26" fill="' + soil + '"/><circle cx="36" cy="19" r="11" fill="#ffb04a" opacity=".35"/><path d="M22 19 h28" stroke="#0a0604" stroke-width="4"/>'
      + '<rect x="5" y="9" width="9" height="2" fill="' + acc + '"/><rect x="5" y="14" width="7" height="2" fill="' + acc2 + '"/>';
  } else if (theme === 'wood') {
    s += '<rect x="5" y="5" width="11" height="9" rx="1.5" fill="' + card + '"/><rect x="5" y="16" width="11" height="9" rx="1.5" fill="' + card + '"/><rect x="56" y="5" width="11" height="9" rx="1.5" fill="' + card + '"/>'
      + '<rect x="19" y="4" width="34" height="25" rx="2" fill="' + acc + '"/><rect x="21.5" y="6.5" width="29" height="20" fill="' + soil + '"/><path d="M24 16 h22" stroke="#2e1e13" stroke-width="3"/>'
      + '<path d="M20 4.5 h32" stroke="' + acc2 + '" stroke-width="1" stroke-dasharray="1.5 1.5"/>';
  } else {
    s += '<rect x="4" y="4" width="11" height="26" rx="2" fill="#3a2262"/><rect x="57" y="4" width="11" height="26" rx="2" fill="#3a2262"/>'
      + '<rect x="18" y="4" width="36" height="26" rx="4" fill="' + soil + '" stroke="' + acc + '" stroke-width="1.5"/><path d="M22 17 h28" stroke="#1c0f0a" stroke-width="3.5"/>'
      + '<rect x="27" y="2.5" width="18" height="5" rx="2.5" fill="' + acc + '"/>';
  }
  for (let i = 0; i < 5; i++) s += '<rect x="' + (20 + i * 7) + '" y="' + (33 - (i === 0 ? 2 : 0)) + '" width="6" height="9" rx="1" fill="' + card + '" stroke="' + acc + '" stroke-width=".6"/>';
  return s + '</svg>';
}

/** Спільні градієнти й маски (один svg 0×0 у корені картки). */
export function defs() {
  const lg = (id, a, b, x2, y2) => '<linearGradient id="' + id + '" x1="0" y1="0" x2="' + (x2 == null ? 0 : x2) + '" y2="' + (y2 == null ? 1 : y2) + '"><stop offset="0" stop-color="' + a + '"/><stop offset="1" stop-color="' + b + '"/></linearGradient>';
  let s = '<radialGradient id="skb-coin" cx=".35" cy=".3" r=".8"><stop offset="0" stop-color="#fff6c4"/><stop offset=".45" stop-color="#f4c542"/><stop offset="1" stop-color="#b07b14"/></radialGradient>'
    + '<linearGradient id="skb-clay" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#e59a5a"/><stop offset=".55" stop-color="#c06a32"/><stop offset="1" stop-color="#8a4420"/></linearGradient>'
    + lg('skb-clayl', '#f0ad6c', '#b8622e') + lg('skb-chw', '#b06a32', '#6a3414')
    + '<clipPath id="skb-tclip"><rect width="100" height="100"/></clipPath>';
  for (const th of Object.keys(TILE)) {
    const t = TILE[th];
    s += lg('skb-ts-' + th, t.soil[0], t.soil[1])
      + '<radialGradient id="skb-tg-' + th + '"><stop offset="0" stop-color="' + t.lamp + '" stop-opacity=".9"/><stop offset=".5" stop-color="' + t.lamp + '" stop-opacity=".25"/><stop offset="1" stop-color="' + t.lamp + '" stop-opacity="0"/></radialGradient>';
  }
  // Казкова ніч
  s += lg('skb-f-pa', '#fff7e2', '#ecd09a')
    + '<linearGradient id="skb-f-fr" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ffe9a0"/><stop offset=".45" stop-color="#e0a030"/><stop offset=".7" stop-color="#a8640e"/><stop offset="1" stop-color="#ffd870"/></linearGradient>'
    + lg('skb-f-back', '#6a3cc8', '#2a1556', 1, 1)
    + '<clipPath id="skb-f-win"><rect x="14" y="22" width="72" height="72" rx="9"/></clipPath><clipPath id="skb-f-winc"><rect x="9" y="20" width="82" height="82" rx="9"/></clipPath>';
  for (const k of Object.keys(CAT.fairy)) s += '<radialGradient id="skb-f-bg-' + k + '" cx=".5" cy=".4"><stop offset="0" stop-color="' + shade(CAT.fairy[k][1], 0.25) + '"/><stop offset="1" stop-color="' + CAT.fairy[k][2] + '"/></radialGradient>';
  // Дерев'яна настолка
  s += lg('skb-w-cs', '#fffaf0', '#efe3c8') + '<clipPath id="skb-w-win"><rect x="7" y="7" width="86" height="86" rx="6"/></clipPath>'
    + '<pattern id="skb-w-back" width="12" height="12" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="12" height="12" fill="#8a1a14"/><rect width="6" height="12" fill="#b3261e"/></pattern>';
  // Ніч копачів
  s += lg('skb-n-b', '#211912', '#0d0a08')
    + '<linearGradient id="skb-n-g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ffe2a0"/><stop offset=".5" stop-color="#9a6a22"/><stop offset="1" stop-color="#e9b65a"/></linearGradient>'
    + '<clipPath id="skb-n-win"><rect x="10" y="18" width="80" height="80" rx="3"/></clipPath>';
  for (const k of Object.keys(CAT.night)) s += '<radialGradient id="skb-n-sp-' + k + '" cx=".5" cy=".45" r=".7"><stop offset="0" stop-color="' + CAT.night[k][1] + '" stop-opacity=".55"/><stop offset=".6" stop-color="' + CAT.night[k][1] + '" stop-opacity=".12"/><stop offset="1" stop-color="#000" stop-opacity="0"/></radialGradient>';
  return '<svg class="skb-defs" width="0" height="0" aria-hidden="true" focusable="false"><defs>' + s + '</defs></svg>';
}
