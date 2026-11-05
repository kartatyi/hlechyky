import { writeFileSync } from 'node:fs';
import { cardDefs, card, TYPES, INK, teaGlass, saltScatter, GLYPH, ORDER } from './art.mjs';
import { FX, GLEK, orderHint, landD, outlineD, seaD, proj, CLAIM, layoutAll, MARK } from './map.mjs';
import { mapDefs, buildMap, ORDER_CITIES } from './board.mjs';
const OUT = new URL('./', import.meta.url).pathname;
const M = FX.mock;
const glek = (sz, cls = '') => GLEK.replace('<svg ', `<svg class="${cls}" width="${sz}" height="${sz}" `).replace(/id="clay"/g, `id="clay${sz}"`).replace(/url\(#clay\)/g, `url(#clay${sz})`).replace(/<!--.*?-->/g, '');
const initial = (p) => p.name[0];
const markIcon = (i, c) => `<svg class="mrk" viewBox="-10 -10 20 20" style="--c:${c}"><circle r="9.6" fill="${c}" stroke="#2a160b" stroke-width="1.4"/><g fill="#fff">${MARK[i](4.6)}</g></svg>`;
const pts = FX.pointsByLen;
const SEL = FX.routes.find((r) => r.id === M.selectedRoute);

const ICON = {
  cart: (c = 'currentColor') => `<svg class="i" viewBox="0 0 20 20"><rect x="2" y="5.6" width="16" height="8.4" rx="2" fill="${c}"/><circle cx="6" cy="15" r="2.3" fill="${c}" stroke="#fff6e0" stroke-width="1"/><circle cx="14" cy="15" r="2.3" fill="${c}" stroke="#fff6e0" stroke-width="1"/></svg>`,
  cards: (c = 'currentColor') => `<svg class="i" viewBox="0 0 20 20"><rect x="3.6" y="3.2" width="9.6" height="13.4" rx="1.8" fill="none" stroke="${c}" stroke-width="1.7" transform="rotate(-9 8 10)"/><rect x="7.8" y="3.8" width="9.6" height="13.4" rx="1.8" fill="${c}" transform="rotate(7 12 10)"/></svg>`,
  scroll: (c = 'currentColor') => `<svg class="i" viewBox="0 0 20 20"><path d="M5 3 H15.4 A1.6 1.6 0 0 1 17 4.6 V6 H13.8 V15.4 A2 2 0 0 1 11.8 17.4 H4.4 A2 2 0 0 1 2.6 15.2 H5Z" fill="${c}"/><path d="M7.2 7 H11.6 M7.2 10 H11.6" stroke="#fff6e0" stroke-width="1.4" stroke-linecap="round"/></svg>`,
  lock: `<svg class="i" viewBox="0 0 20 20"><rect x="4" y="9" width="12" height="9" rx="2" fill="currentColor"/><path d="M6.6 9 V6.4 a3.4 3.4 0 0 1 6.8 0 V9" fill="none" stroke="currentColor" stroke-width="2"/></svg>`,
  hand: `<svg class="i" viewBox="0 0 20 20"><path d="M7 10 V3.6 a1.3 1.3 0 0 1 2.6 0 V9 l5 1.2 a2 2 0 0 1 1.5 2.4 l-.9 3.8 a2 2 0 0 1 -2 1.6 H9.4 a2.4 2.4 0 0 1 -1.9 -.9 L3.2 12 a1.3 1.3 0 0 1 1.9 -1.8Z" fill="currentColor"/></svg>`,
};
const hourglass = (s = 22) => `<svg width="${s}" height="${s}" viewBox="0 0 24 24"><path d="M6 2.5 h12 M6 21.5 h12" stroke="#3a1d0a" stroke-width="2.6" stroke-linecap="round"/><path d="M7.4 3 C7.4 8 11 10 11 12 C11 14 7.4 16 7.4 21 H16.6 C16.6 16 13 14 13 12 C13 10 16.6 8 16.6 3Z" fill="#fff8e6" stroke="#3a1d0a" stroke-width="1.5"/><path d="M9 6.4 H15 C14.4 8.6 12.6 9.8 12 11.2 C11.4 9.8 9.6 8.6 9 6.4Z" fill="#f2c063"/><path d="M8.6 20.4 C9.4 17.6 11 16.8 12 16 C13 16.8 14.6 17.6 15.4 20.4Z" fill="#e6a43a"/></svg>`;

const BASE_CSS = `
@font-face { font-family:'Onest'; font-weight:100 900; src:url('fonts/onest-cyrillic-wght-normal.woff2') format('woff2'); unicode-range:U+0301,U+0400-045F,U+0490-0491,U+04B0-04B1,U+2116; }
@font-face { font-family:'Onest'; font-weight:100 900; src:url('fonts/onest-latin-wght-normal.woff2') format('woff2'); unicode-range:U+0000-00FF,U+2000-206F,U+2190-2193,U+2212; }
*{box-sizing:border-box} html,body{margin:0;padding:0;overflow:hidden;background:#2c1a0f}
body{font-family:'Onest',system-ui,sans-serif;color:${INK};-webkit-font-smoothing:antialiased}
svg text{font-family:'Onest',system-ui,sans-serif}
button{font:inherit;color:inherit;border:0;cursor:pointer;background:none;padding:0}
.defs{position:absolute;width:0;height:0;overflow:hidden}
.i{width:1em;height:1em;display:inline-block;vertical-align:-.13em;flex:none}
.cardart{display:block;width:100%;height:100%}
.tbl{position:absolute;left:0;right:0;top:0;bottom:0;z-index:0}
.tbl svg{display:block;width:100%;height:100%}
/* шапка сайту — справжні токени style.css */
.site{position:absolute;left:0;right:0;top:0;height:56px;z-index:30;display:flex;align-items:center;gap:12px;padding:8px 16px;background:rgba(15,31,24,.97);border-bottom:1px solid #2f4d3d;color:#ecf1ea;font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif}
.site .brand{display:flex;align-items:center;gap:10px;font-weight:700;font-size:1.15em;letter-spacing:.3px}
.site .brand svg{width:30px;height:30px}
.site nav{display:flex;gap:4px;margin-left:10px}
.site nav span{padding:6px 12px;border-radius:10px;color:#9db3a5;font-size:14.5px}
.site nav span.on{background:#234032;color:#f4c542}
.site .sp{flex:1}
.site .me{display:flex;align-items:center;gap:8px;font-size:14px;background:#1c3328;border:1px solid #2f4d3d;border-radius:999px;padding:4px 12px 4px 4px}
.site .ava{width:28px;height:28px;border-radius:50%;background:hsl(4 42% 30%);color:hsl(4 85% 88%);display:grid;place-items:center;font-weight:700;font-size:.85em}
.chip{display:inline-flex;align-items:center;gap:4px;padding:2px 8px;border-radius:999px;background:#234032;font-size:12px;color:#9db3a5}
/* матеріали столу */
.paper{background:linear-gradient(180deg,#fdf7e8,#f1e4c8);box-shadow:0 1px 0 rgba(255,255,255,.75) inset,0 -1px 0 rgba(120,85,40,.18) inset,0 1px 1px rgba(30,12,2,.5),0 7px 13px rgba(20,8,2,.48);border-radius:8px}
.wood{background:linear-gradient(180deg,#d7a062 0%,#bb8346 55%,#a26c35 100%);box-shadow:inset 0 1.5px 0 rgba(255,235,195,.65),inset 0 -2px 0 rgba(70,34,8,.5),0 1px 1px rgba(20,8,2,.55),0 7px 12px rgba(15,6,1,.5);border-radius:10px}
.lbl{font-size:11.5px;font-weight:800;letter-spacing:.09em;text-transform:uppercase;color:#6e5440}
.lbl.onwood{color:#f8e3bb;text-shadow:0 1px 1px rgba(0,0,0,.65)}
.cnt{position:absolute;left:-7px;top:-7px;min-width:29px;height:29px;padding:0 4px;border-radius:15px;display:grid;place-items:center;font-weight:900;font-size:15px;color:#2a160b;background:radial-gradient(circle at 35% 28%,#ffe9bb,#e3b273 70%,#c48c4b);box-shadow:inset 0 0 0 1.5px #2a160b,0 2px 0 #3a1d0a,0 3px 5px rgba(0,0,0,.5)}
.cardbox{position:relative;border-radius:7px;filter:drop-shadow(0 1px 1px rgba(25,10,2,.6)) drop-shadow(0 5px 7px rgba(20,8,2,.42))}
.cname{position:absolute;left:0;right:0;bottom:0;text-align:center;font-weight:800;line-height:1;color:${INK};white-space:nowrap}
.y-btn{background:linear-gradient(180deg,#ffdc62,#f4c542 55%,#d9a92f);color:#2a1e03;font-weight:900;border-radius:10px;box-shadow:inset 0 1.5px 0 rgba(255,255,255,.7),0 0 0 1.5px #2a1e03,0 3px 0 #7d5a0e,0 7px 11px rgba(20,8,2,.45)}
.lk-btn{background:repeating-linear-gradient(-45deg,#e3d5b4 0 7px,#d9c9a4 7px 14px);color:#6e5440;font-weight:800;border-radius:10px;box-shadow:inset 0 0 0 1.5px #a58e66}
`;
function table(w, h, seed = 7) {
  return `<div class="tbl"><svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="none">
    <defs>
      <filter id="walnut" x="0" y="0" width="100%" height="100%" color-interpolation-filters="sRGB">
        <feTurbulence type="fractalNoise" baseFrequency=".0028 .085" numOctaves="5" seed="${seed}" result="n"/>
        <feColorMatrix in="n" type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  1.5 0 0 0 -.45" result="a"/>
        <feFlood flood-color="#1e0e05" result="d"/><feComposite in="d" in2="a" operator="in" result="dark"/>
        <feTurbulence type="fractalNoise" baseFrequency=".012 .34" numOctaves="3" seed="${seed + 3}" result="m"/>
        <feColorMatrix in="m" type="matrix" values="0 0 0 0 .86  0 0 0 0 .55  0 0 0 0 .3  .7 0 0 0 -.27" result="lit"/>
        <feFlood flood-color="#5d3820" result="b"/>
        <feMerge><feMergeNode in="b"/><feMergeNode in="lit"/><feMergeNode in="dark"/></feMerge>
      </filter>
      <radialGradient id="vign" cx=".42" cy=".36" r=".82"><stop offset="0" stop-color="#ffd9a0" stop-opacity=".13"/><stop offset=".55" stop-color="#000" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".46"/></radialGradient>
    </defs>
    <rect width="${w}" height="${h}" filter="url(#walnut)"/>
    ${[1, 2, 3, 4].map((i) => `<path d="M0 ${Math.round(i * h / 4.6)} H${w}" stroke="#160903" stroke-width="2" opacity=".6"/><path d="M0 ${Math.round(i * h / 4.6) + 2} H${w}" stroke="#a97b4f" stroke-width="1" opacity=".26"/>`).join('')}
    <rect width="${w}" height="${h}" fill="url(#vign)"/>
  </svg></div>`;
}
const siteIcon = `<svg viewBox="0 0 64 64"><circle cx="32" cy="32" r="31" fill="#173a2a"/><circle cx="32" cy="32" r="20" fill="none" stroke="#d9a92f" stroke-width="2.6" stroke-dasharray="3 3.4"/><path d="M26 18 h12 v4 c6 3 10 9 10 16 c0 9 -6 14 -16 14 c-10 0 -16 -5 -16 -14 c0 -7 4 -13 10 -16z" fill="#c5763a" stroke="#5a2f14" stroke-width="2"/></svg>`;

// ── надруковане на дошці ──
const cartVignette = (x, y, k) => `<g transform="translate(${x} ${y}) scale(${k})">
  <ellipse cx="0" cy="16" rx="62" ry="5" fill="#3b2a12" opacity=".2"/>
  <path d="M-46 8 L-8 8" stroke="${INK}" stroke-width="3" />
  <rect x="-6" y="-14" width="48" height="20" rx="3" fill="#b77a3f" stroke="${INK}" stroke-width="2.4"/>
  <path d="M-4 -14 C4 -30 30 -30 40 -14Z" fill="#fff8e6" stroke="${INK}" stroke-width="2.4"/>
  <circle cx="4" cy="10" r="8" fill="#7a4a25" stroke="${INK}" stroke-width="2.4"/><circle cx="32" cy="10" r="8" fill="#7a4a25" stroke="${INK}" stroke-width="2.4"/>
  <path d="M-64 -4 C-64 -12 -52 -14 -44 -12 L-30 -12 C-26 -12 -24 -8 -24 -4 L-24 12 M-58 12 L-58 -2 M-32 12 L-32 2" fill="none" stroke="${INK}" stroke-width="4.2" stroke-linecap="round"/>
  <path d="M-66 -9 C-64 -14 -52 -16 -44 -12 L-28 -11 C-24 -10 -23 -6 -24 -2 L-27 4 L-52 4 C-58 2 -64 -3 -66 -9Z" fill="#f3e7cc" stroke="${INK}" stroke-width="2.2"/>
  <path d="M-58 -13 C-62 -22 -70 -22 -74 -26 M-54 -14 C-50 -22 -42 -24 -38 -28" fill="none" stroke="${INK}" stroke-width="2.6" stroke-linecap="round"/>
</g>`;
const compass = (x, y, r) => {
  let s = `<g transform="translate(${x} ${y})"><circle r="${r}" fill="#ffffff" opacity=".13"/><circle r="${r * .92}" fill="none" stroke="#e8f8ff" stroke-width="${r * .025}" opacity=".85"/><circle r="${r * .78}" fill="none" stroke="#e8f8ff" stroke-width="${r * .012}" stroke-dasharray="${r * .03} ${r * .05}" opacity=".85"/>`;
  for (let i = 0; i < 8; i++) { const k = i % 2 ? .46 : .78, w = i % 2 ? .08 : .12, a = i * 45; s += `<g transform="rotate(${a})"><path d="M0 ${-r * k} L${r * w} 0 L0 0Z" fill="#e8f8ff"/><path d="M0 ${-r * k} L${-r * w} 0 L0 0Z" fill="#1766ad"/></g>`; }
  return s + `<text y="${-r * 1.0}" text-anchor="middle" font-size="${r * .24}" font-weight="900" fill="#fff">Пн</text></g>`;
};
const boat = (x, y, k) => `<g transform="translate(${x} ${y}) scale(${k})"><path d="M-22 4 H22 L14 15 H-14Z" fill="#8a5324" stroke="#123e6a" stroke-width="2.2" stroke-linejoin="round"/><path d="M-1 3 V-24" stroke="#123e6a" stroke-width="2.4"/><path d="M1 -22 L19 0 H1Z" fill="#fff8e6" stroke="#123e6a" stroke-width="2"/><path d="M-3 -18 L-16 0 H-3Z" fill="#f4c542" stroke="#123e6a" stroke-width="2"/></g>`;
const fish = (x, y, k, rot = 0) => `<g transform="translate(${x} ${y}) rotate(${rot}) scale(${k})" opacity=".8"><path d="M-14 0 C-8 -7 4 -7 8 -1 L15 -6 L15 6 L8 1 C4 7 -8 7 -14 0Z" fill="#e8f8ff"/><circle cx="-8" cy="-1" r="1.2" fill="#1766ad"/></g>`;
function riverLabel(t, x, y, rot, u, size = 10.5) {
  return `<text x="${x}" y="${y}" transform="rotate(${rot} ${x} ${y})" text-anchor="middle" font-size="${u(size)}" font-weight="700" font-style="italic" fill="#0d5d9e" stroke="#eaf8d2" stroke-width="${u(2.6)}" paint-order="stroke" stroke-linejoin="round" letter-spacing="${u(.3)}">${t}</text>`;
}

// ═════════ ПК ═════════
function buildPC() {
  const W = 1600, H = 900, top = 56;
  const pad = 12;
  const bH = H - top - pad * 2 + 4, track = 31, inner = 3;
  const mapH = bH - 2 * track - 2 * inner;
  const vb = { x: -10, y: -6, w: 4020, h: 2744 };
  const sc = mapH / vb.h, mapW = vb.w * sc;
  const bW = mapW + 2 * track + 2 * inner;
  const bX = 12, bY = top + pad - 2;
  const o = { sc, T: 13.8 / sc, node: 7.6 / sc, clear: 9.4 / sc, gap: 1.8 / sc, cap: 46 / sc, fs: 11, bounds: [vb.x, vb.y, vb.x + vb.w, vb.y + vb.h],
    fixed: { 'Ізмаїл': [1, .2], 'Івано-Франківськ': [-1, -.75], 'Коломия': [-1, .55], 'Опішня': [-1, -.2], 'Кременчук': [0, 1, 4 / sc] },
    force: { 6: -190, 25: 110 }, report: true, riverFs: 10.5, rivers: [['Дніпро', 'Дніпро', 2], ['Дністер', 'Дністер'], ['Південний Буг', 'Півд. Буг'], ['Десна', 'Десна'], ['Сіверський Донець', 'Сів. Донець'], ['Прут', 'Прут']],
    printed: (u) => `
      <text x="2010" y="2395" text-anchor="middle" font-size="${u(20)}" font-weight="800" font-style="italic" fill="#fff" opacity=".95" letter-spacing="${u(2.2)}">Чорне море</text>
      <text x="3190" y="2190" text-anchor="middle" font-size="${u(14.5)}" font-weight="800" font-style="italic" fill="#fff" opacity=".93" letter-spacing="${u(1.6)}">Азовське море</text>
      ${compass(2375, 2570, u(32))}
      ${boat(1700, 2520, u(1))}${fish(1500, 2660, u(1.1), 10)}${fish(3280, 2330, u(1), -14)}${fish(2120, 2640, u(1), 16)}
            
      ${cartVignette(1400, 1610, u(.82))}
      <g transform="translate(3240 54)">
        <rect x="${u(3)}" y="${u(4)}" width="745" height="${u(92)}" rx="${u(9)}" fill="#3a2410" opacity=".35"/>
        <rect width="745" height="${u(92)}" rx="${u(9)}" fill="#fff6da" stroke="${INK}" stroke-width="${u(1.6)}"/>
        <rect x="${u(3.5)}" y="${u(3.5)}" width="${745 - 2 * u(3.5)}" height="${u(85)}" rx="${u(6)}" fill="none" stroke="#c5392b" stroke-width="${u(1.2)}" stroke-dasharray="${u(3)} ${u(2.4)}"/>
        <text x="372" y="${u(34)}" text-anchor="middle" font-size="${u(23.5)}" font-weight="900" fill="#b3261e">Соляний шлях</text>
        <text x="372" y="${u(51)}" text-anchor="middle" font-size="${u(9)}" font-weight="700" fill="#6e5440">чумацька настілка Дядька Глека</text>
        <g transform="translate(${372 - u(87)} ${u(70)})">
          ${Object.entries(pts).filter(([k]) => +k !== 5).map(([k, v], i) => `<g transform="translate(${i * u(35.8)} 0)"><rect x="${-u(1)}" y="${-u(9.5)}" width="${u(14)}" height="${u(13.5)}" rx="${u(3)}" fill="#d9cfb6" stroke="${INK}" stroke-width="${u(.9)}"/><text x="${u(6)}" y="${u(1.4)}" text-anchor="middle" font-size="${u(10)}" font-weight="900" fill="${INK}">${k}</text><text x="${u(14.8)}" y="${u(1.4)}" font-size="${u(10)}" font-weight="700" fill="#6e5440">→</text><text x="${u(25)}" y="${u(1.4)}" font-size="${u(11)}" font-weight="900" fill="#b3261e">${v}</text></g>`).join('')}
        </g>
      </g>`,
    top: (u, SL) => {
      const mid = SL.slots[3];
      const cx = mid.x + u(48), cy = mid.y - u(50);
      const w = u(136), h = u(30);
      return `<g filter="url(#pieceShadow)"><path d="M${mid.x} ${mid.y - u(10)} L${mid.x - u(8)} ${cy + h / 2 - u(1)} L${mid.x + u(8)} ${cy + h / 2 - u(1)}Z" fill="#2a160b"/>
        <rect x="${cx - w / 2}" y="${cy - h / 2}" width="${w}" height="${h}" rx="${u(7)}" fill="#2a160b"/>
        <use href="#gl-green" x="${cx - w / 2 + u(6)}" y="${cy - u(9)}" width="${u(18)}" height="${u(18)}"/>
        <text x="${cx - w / 2 + u(28)}" y="${cy + u(5)}" font-size="${u(13.5)}" font-weight="800" fill="#fff6da">6 возів</text>
        <rect x="${cx + w / 2 - u(50)}" y="${cy - u(11)}" width="${u(44)}" height="${u(22)}" rx="${u(5)}" fill="#ffd35a"/>
        <text x="${cx + w / 2 - u(28)}" y="${cy + u(5.2)}" text-anchor="middle" font-size="${u(14.5)}" font-weight="900" fill="#2a1e03">+15</text></g>`;
    },
  };
  const { svg } = buildMap(o);
  // ── рахівниця навколо дошки ──
  const cells = [];
  const cw = bW / 31, ch = bH / 21; // кути входять у сторони
  for (let i = 0; i <= 30; i++) cells.push({ n: i, x: i * cw, y: 0, w: cw, h: track });
  for (let i = 1; i < 20; i++) cells.push({ n: 30 + i, x: bW - track, y: track + (i - 1) * ((bH - 2 * track) / 19), w: track, h: (bH - 2 * track) / 19 });
  for (let i = 0; i <= 30; i++) cells.push({ n: 49 + 1 + i, x: bW - (i + 1) * cw, y: bH - track, w: cw, h: track });
  for (let i = 1; i < 20; i++) cells.push({ n: 80 + i, x: 0, y: bH - track - i * ((bH - 2 * track) / 19), w: track, h: (bH - 2 * track) / 19 });
  const tr = cells.map((c) => {
    const five = c.n % 5 === 0, ten = c.n % 10 === 0;
    const fill = ten ? 'url(#tkD)' : five ? 'url(#tkM)' : c.n % 2 ? 'url(#tkL)' : 'url(#tkL2)';
    return `<rect x="${(c.x + 1).toFixed(1)}" y="${(c.y + 1).toFixed(1)}" width="${(c.w - 2).toFixed(1)}" height="${(c.h - 2).toFixed(1)}" rx="3.5" fill="${fill}" stroke="#2a160b" stroke-width="1"/>
      <path d="M${(c.x + 4).toFixed(1)} ${(c.y + 2.6).toFixed(1)} H${(c.x + c.w - 4).toFixed(1)}" stroke="#ffeec9" stroke-width="1" opacity=".55"/>
      <text x="${(c.x + c.w / 2).toFixed(1)}" y="${(c.y + c.h / 2 + 4.6).toFixed(1)}" text-anchor="middle" font-size="${five ? 13 : 11.5}" font-weight="${five ? 900 : 700}" fill="${ten ? '#fff0cf' : '#3a1d0a'}" ${ten ? '' : 'opacity=".85"'}>${c.n}</text>`;
  }).join('');
  const disc = (p, c) => {
    const x = c.x + c.w / 2, y = c.y + c.h / 2, r = 13.6;
    return `<g><ellipse cx="${x + 2}" cy="${y + 4.4}" rx="${r}" ry="${r}" fill="#1b0d03" opacity=".6"/><circle cx="${x}" cy="${y + 2}" r="${r}" fill="${shadeL(p.color, -.4)}"/><circle cx="${x}" cy="${y}" r="${r}" fill="${p.color}" stroke="${INK}" stroke-width="1.2"/><circle cx="${x}" cy="${y}" r="${r - 3}" fill="none" stroke="#fff" stroke-opacity=".42" stroke-width="1.3"/><text x="${x}" y="${y + 5}" text-anchor="middle" font-size="14" font-weight="900" fill="#fff">${p.points}</text></g>`;
  };
  const discs = M.players.map((p) => disc(p, cells.find((c) => c.n === p.points))).join('');

  // ── рука віялом: потрібні для обраного шляху — піднято ──
  const handOrder = ['red', 'blue', 'yellow', 'white', 'pink', 'green', 'wild'];
  const handItems = handOrder.filter((k) => M.myHand[k] > 0);
  const n = handItems.length, cardW = 86, cardH = 120, step = 50;
  const fan = handItems.map((k, i) => {
    const cnt = M.myHand[k], c = i - (n - 1) / 2, rot = c * 2.6, up = c * c * .9;
    const pay = k === 'green' || k === 'wild';
    const x = i * step, y = up - (pay ? 18 : 0);
    let stack = '';
    for (let j = Math.min(cnt, 4) - 1; j > 0; j--) stack += `<div class="stk" style="transform:translate(${j * 2.4}px,${-j * 2.6}px) rotate(${j * 1.4}deg)">${card(k)}</div>`;
    const name = k === 'wild' ? 'круторогий віл' : TYPES[k].name;
    return `<div class="fan ${pay ? 'pay' : ''}" style="left:${x}px;top:${y}px;transform:rotate(${rot}deg);z-index:${i + 1}">
      ${stack}<div class="cardbox top">${card(k)}<div class="cname" style="font-size:${k === 'wild' ? 9.6 : 11.5}px;bottom:${k === 'wild' ? 8 : 7}px">${name}</div></div>
      <div class="cnt">${cnt}</div>
    </div>`;
  }).join('');

  // ── гравці ──
  const mat = (p, i) => `<div class="mat ${p.me ? 'me turn' : ''}" style="--pc:${p.color}">
    <div class="band"></div>
    <div class="av">${p.name.startsWith('Глек') ? glek(40) : `<span style="background:${p.color}">${initial(p)}</span>`}${markIcon(i, p.color)}</div>
    <div class="nm">${p.name}${p.me ? ' <em>ти</em>' : ''}</div>
    <div class="sc"><b>${p.points}</b><i>очок</i></div>
    <div class="st"><span title="воз">${ICON.cart(p.color)} ${p.wagons}</span><span title="карти">${ICON.cards('#6e5440')} ${p.cards}</span><span title="замовлення">${ICON.scroll('#6e5440')} ${p.tickets}</span></div>
  </div>`;

  // ── вартість обраного шляху ──
  const cost = [];
  for (let i = 0; i < 6; i++) cost.push(i < M.myHand.green ? 'green' : i === M.myHand.green ? 'wild' : null);
  const costHtml = cost.map((k) => k ? `<div class="cc">${card(k)}</div>` : `<div class="cc miss"><span>?</span></div>`).join('');

  const orders = M.myTickets.map((t) => {
    const h = orderHint(t.a, t.b);
    let pips = '';
    if (!t.done) {
      for (const l of h.legs) { pips += `<span class="leg ${l.mine ? 'mine' : ''}">${'<i></i>'.repeat(l.R.len)}</span>`; }
    }
    const next = h.legs.find((l) => !l.mine);
    const hint = t.done ? 'виконано — вже в рахунку' : `ще ${h.leftRoutes} шляхи, ${h.leftWagons} возів`;
    return `<div class="tk paper ${t.done ? 'done' : ''}">
      <div class="seal">${t.done ? '✓' : ''}<b>${t.done ? '' : t.pts}</b></div>
      <div class="tb"><div class="tt">${t.a} <span>→</span> ${t.b}${t.done ? ` <em>+${t.pts}</em>` : ''}</div>
      <div class="ph">${t.done ? '' : `<span class="pips">${pips}</span>`}<span class="hn">${hint}</span></div></div>
    </div>`;
  }).join('');
  const nameDisc = (t) => { const p = M.players.find((q) => t.startsWith(q.name)); return p ? p.color : '#888'; };
  const log = M.log.map((t) => `<li><i style="background:${nameDisc(t)}"></i><span>${t}</span></li>`).join('');

  const html = `<!doctype html><html lang="uk"><head><meta charset="utf-8"><title>Соляний шлях — ПК</title><style>${BASE_CSS}
body{width:${W}px;height:${H}px;position:relative}
.board{position:absolute;left:${bX}px;top:${bY}px;width:${bW}px;height:${bH}px;z-index:2;border-radius:7px;background:#e9ddc0;box-shadow:0 1px 0 #fff6dd inset,0 3px 0 #cdbb93,0 5px 0 #a58f64,0 7px 0 #7d6a48,0 16px 26px rgba(10,4,0,.62),0 2px 4px rgba(10,4,0,.6)}
.board>svg{position:absolute;left:0;top:0}
.side{position:absolute;left:${bX + bW + 13}px;top:${top + 10}px;right:12px;bottom:12px;z-index:3;display:flex;flex-direction:column;gap:9px}
.turn{display:flex;align-items:center;gap:10px;padding:5px 11px 8px 8px;height:54px;flex:none;position:relative}
.turn .tdisc{width:36px;height:36px;border-radius:50%;background:#c0392b;color:#fff;display:grid;place-items:center;font-weight:900;font-size:18px;box-shadow:inset 0 0 0 2px rgba(255,255,255,.35),0 0 0 1.5px #2a160b,0 3px 0 #6b1a12}
.turn h2{margin:0;font-size:19.5px;font-weight:900;line-height:1.05;color:#2a160b;letter-spacing:-.01em}
.turn small{display:block;font-size:12px;font-weight:700;color:#4a2a10;margin-top:2px}
.timer{margin-left:auto;display:flex;align-items:center;gap:6px;background:#fbf4e4;border-radius:9px;padding:4px 10px 4px 7px;box-shadow:inset 0 0 0 1.5px #2a160b;font-weight:900;font-size:20px;font-variant-numeric:tabular-nums}
.tbar{position:absolute;left:10px;right:10px;bottom:3.5px;height:4px;border-radius:2px;background:rgba(58,29,10,.42)}
.tbar i{display:block;height:100%;width:62%;background:#ffe066;border-radius:2px}
.mats{display:grid;grid-template-columns:1fr 1fr;gap:7px}
.mat{position:relative;border-radius:8px;background:linear-gradient(180deg,#fdf7e8,#f1e4c8);padding:10px 8px 3px 7px;display:grid;grid-template-columns:40px 1fr auto;column-gap:7px;row-gap:1px;align-items:center;height:62px;box-shadow:0 1px 1px rgba(30,12,2,.5),0 6px 10px rgba(20,8,2,.42)}
.mat .band{position:absolute;left:0;right:0;top:0;height:7px;border-radius:8px 8px 0 0;background:var(--pc);box-shadow:inset 0 -1.5px 0 rgba(0,0,0,.25)}
.mat.turn{box-shadow:0 0 0 2.5px #ffd35a,0 0 0 4px #2a160b,0 6px 10px rgba(20,8,2,.42)}
.mat .av{position:relative;grid-row:1/3;width:40px;height:40px;display:grid;place-items:center}
.mrk{position:absolute;right:-3px;bottom:-3px;width:16px;height:16px}
.mat .av>span{width:38px;height:38px;border-radius:50%;display:grid;place-items:center;color:#fff;font-weight:900;font-size:18px;box-shadow:0 0 0 1.5px #2a160b,inset 0 0 0 2px rgba(255,255,255,.3)}
.mat .nm{font-weight:800;font-size:13px;white-space:nowrap;overflow:hidden;line-height:1.15}
.mat .nm em{font-style:normal;background:#b3261e;color:#fff;font-size:10px;padding:0 4px;border-radius:4px;font-weight:800;vertical-align:1px}
.mat .sc{grid-row:1/3;grid-column:3;text-align:center;line-height:.95;align-self:center}
.mat .sc b{font-size:25px;font-weight:900;letter-spacing:-.02em;display:block}
.mat .sc i{font-style:normal;font-size:9.5px;font-weight:700;color:#6e5440}
.mat .st{display:flex;gap:7px;font-size:12px;font-weight:800;color:#4a3524;white-space:nowrap}
.mat .st span{display:inline-flex;align-items:center;gap:2px}
.route{padding:9px 11px 10px;position:relative;border-top:6px solid #0f7f3f}
.route .rh{display:flex;align-items:baseline;gap:8px}
.route h3{margin:0;font-size:18.5px;font-weight:900;letter-spacing:-.01em}
.route .rm{margin-left:auto;font-size:12.5px;font-weight:800;color:#6e5440;white-space:nowrap}
.route .rm b{color:#b3261e;font-size:15px;font-weight:900}
.route .rw{display:flex;gap:4px;align-items:center;margin:7px 0 8px}
.cc{width:32px;height:44.8px;border-radius:4px;filter:drop-shadow(0 1px 1px rgba(25,10,2,.5))}
.cc.miss{display:grid;place-items:center;background:rgba(179,38,30,.08);border:2px dashed #b3261e;font-weight:900;color:#b3261e;font-size:19px;border-radius:5px}
.route .note{font-size:12.5px;font-weight:700;color:#4a3524;line-height:1.22;margin-left:7px}
.route .note b{color:#b3261e;font-weight:900;display:block;font-size:14px}
.btns{display:grid;grid-template-columns:1fr 1.25fr;gap:8px}
.btns>*{height:40px;display:flex;align-items:center;justify-content:center;gap:6px;font-size:14px;white-space:nowrap}
.mkt{position:relative;padding:0}
.hd{display:flex;align-items:center;justify-content:space-between;margin-bottom:6px}
.mk{display:grid;grid-template-columns:repeat(5,1fr) 1.02fr;gap:6px}
.mk .cardbox{height:84px}
.mk .cardbox .cname{font-size:9.6px;bottom:4.5px}
.mk .hot{outline:3px solid #ffe066;outline-offset:1px;border-radius:6px}
.deck{position:relative;height:84px}
.deck .bk{position:absolute;inset:0;border-radius:6px;background:linear-gradient(145deg,#8a5324,#5e3314);box-shadow:inset 0 0 0 3px #fbf4e4,inset 0 0 0 4px #2a160b}
.deck .fc{display:grid;place-items:center;align-content:center;color:#fff0cf;text-align:center;font-weight:900}
.deck .fc b{font-size:20px;line-height:1}.deck .fc small{font-size:9.5px;font-weight:700;opacity:.95}
.orders{display:flex;flex-direction:column;gap:5px}
.tk{display:flex;align-items:stretch;gap:0;min-height:42px;position:relative;border-radius:7px}
.seal{flex:none;width:38px;display:grid;place-items:center;background:#b3261e;color:#fff;font-weight:900;font-size:17px;border-radius:7px 0 0 7px;border-right:2px dashed #fdf7e8}
.tk.done{opacity:.97}.tk.done .seal{background:#2f7a4a;font-size:20px}
.tk .tb{padding:3px 8px 3px 9px;flex:1;min-width:0}
.tt{font-size:14px;font-weight:800;line-height:1.2;white-space:nowrap}
.tt span{color:#b3261e;font-weight:900}.tt em{font-style:normal;color:#2f7a4a;font-weight:900;margin-left:3px}
.ph{display:flex;align-items:center;gap:8px;margin-top:2px}
.pips{display:inline-flex;gap:3px;flex:none}.leg{display:inline-flex;gap:1px}
.leg i{display:block;width:6px;height:10px;border-radius:1.5px;background:#fff;box-shadow:inset 0 0 0 1px #8a7558}
.leg.mine i{background:#c0392b;box-shadow:inset 0 0 0 1px #5e150d}
.hn{font-size:11.5px;font-weight:600;color:#5d4631;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.bot{display:grid;grid-template-columns:1fr;gap:8px;flex:1;min-height:0}
.log{padding:6px 10px;background:linear-gradient(180deg,#fdf7e8,#f1e4c8);position:relative}
.log ul{list-style:none;margin:3px 0 0;padding:0;display:flex;flex-direction:column;gap:1.5px}
.log li{display:flex;align-items:baseline;gap:6px;font-size:12px;font-weight:600;line-height:1.28;white-space:nowrap}
.log li i{flex:none;width:8px;height:8px;border-radius:50%;box-shadow:0 0 0 1px #2a160b;align-self:center}
.tip{display:flex;gap:7px;align-items:flex-end;position:relative}
.tip .gl{flex:none;margin-bottom:-3px;filter:drop-shadow(0 4px 5px rgba(0,0,0,.5))}
.tip .sp{position:relative;padding:7px 10px;font-size:12.5px;font-weight:600;line-height:1.28;border-radius:11px;flex:1}
.tip .sp b{font-weight:900;color:#b3261e}
.tip .sp::before{content:'';position:absolute;left:-7px;bottom:13px;border:7px solid transparent;border-left:0;border-right-color:#f3e7cd}
.hand{position:absolute;left:28px;top:${H-146}px;z-index:6;width:${n * step + cardW}px;height:126px}
.fan{position:absolute;width:${cardW}px;height:${cardH}px;transform-origin:50% 130%}
.fan .stk{position:absolute;inset:0;filter:drop-shadow(0 1px 1px rgba(25,10,2,.5))}
.fan .top{width:100%;height:100%}
.fan .cname{text-align:left;padding-left:9px}.fan:last-of-type .cname{text-align:center;padding-left:0}
.fan.pay .top{outline:3px solid #ffe066;outline-offset:-1px;border-radius:7px}
.paytag{position:absolute;background:#2a160b;color:#ffe066;font-size:11.5px;font-weight:800;padding:2.5px 8px;border-radius:6px;white-space:nowrap;box-shadow:0 2px 5px rgba(20,8,2,.5)}
.handtag{position:absolute;left:33px;top:${H-173}px;z-index:6;padding:3px 9px;display:flex;gap:6px;align-items:baseline;font-size:12.5px;font-weight:900;color:#2a160b;border-radius:7px}
.handtag span{font-weight:600;color:#4a2a10;font-size:11.5px}
.dress{position:absolute;z-index:4;pointer-events:none}
</style></head><body>
<svg class="defs"><defs>${cardDefs()}${mapDefs()}
  <radialGradient id="saucer" cx=".4" cy=".35" r=".8"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#d9d0bb"/></radialGradient>
  <radialGradient id="tea" cx=".42" cy=".38" r=".75"><stop offset="0" stop-color="#d9782a"/><stop offset=".65" stop-color="#a64a12"/><stop offset="1" stop-color="#6e2c07"/></radialGradient>
  ${[['tkL', '#d6a163', '#bb8346'], ['tkL2', '#cb9558', '#ae773d'], ['tkM', '#b67a3d', '#94602c'], ['tkD', '#7a4a25', '#5a3519']].map(([id, a, b]) => `<linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${a}"/><stop offset="1" stop-color="${b}"/></linearGradient>`).join('')}
  <filter id="grain" x="0" y="0" width="100%" height="100%"><feTurbulence type="fractalNoise" baseFrequency=".75" numOctaves="2" seed="4"/><feColorMatrix values="0 0 0 0 .22  0 0 0 0 .14  0 0 0 0 .05  0 0 0 .14 0"/></filter>
</defs></svg>
${table(W, H)}
<header class="site"><div class="brand">${siteIcon}<span>Глечики</span></div><nav><span>📻 Ефір</span><span>🎵 Бібліотека</span><span class="on">🎮 Ігри</span><span>📊 Хто скільки</span><span>💬 Балачки</span></nav><div class="sp"></div><span class="chip">🧂 Соляний шлях · стіл на 4</span><div class="me"><span class="ava">О</span>Оля <span style="color:#f4c542">🏺 128</span></div></header>
<div class="board">
  <svg width="${bW}" height="${bH}" viewBox="0 0 ${bW} ${bH}">
    <rect x="${track + 1}" y="${track + 1}" width="${bW - 2 * track - 2}" height="${bH - 2 * track - 2}" fill="#2a160b"/>
    <svg x="${track + inner}" y="${track + inner}" width="${mapW}" height="${mapH}" viewBox="${vb.x} ${vb.y} ${vb.w} ${vb.h}">${svg}</svg>
    <rect x="${track + inner}" y="${track + inner}" width="${mapW}" height="${mapH}" filter="url(#grain)" style="mix-blend-mode:multiply"/>
    ${tr}
    ${discs}
  </svg>
</div>
<div class="dress" style="left:${bX + bW - 136}px;bottom:30px;">${teaGlass(138)}</div>
<div class="dress" style="left:${bX + bW - 232}px;bottom:34px;">${saltScatter(116, 74, 150, 5)}</div>
<div class="dress" style="left:${bX + bW - 190}px;bottom:4px;">${saltScatter(60, 26, 22, 9)}</div>

<div class="handtag wood">Твоя рука</div>
<div class="hand">${fan}<div class="paytag" style="left:${5 * step + 62}px;top:-41px;z-index:20;transform:none">піде на шлях ↓</div></div>

<aside class="side">
  <div class="turn wood"><div class="tdisc">О</div><div><h2>Твій хід, Оля</h2><small>займи шлях або візьми карти</small></div><div class="timer">${hourglass(22)}0:47</div><div class="tbar"><i></i></div></div>
  <div class="mats">${M.players.map(mat).join('')}</div>
  <section class="route paper">
    <div class="rh"><h3>${SEL.a} — ${SEL.b}</h3><div class="rm">${SEL.len} возів · <b>+${pts[SEL.len]}</b></div></div>
    <div class="rw">${costHtml}<div class="note"><b>Бракує 1 карти</b>є 5 із 6: збіжжя і віл</div></div>
    <div class="btns"><button class="lk-btn">${ICON.lock} Зайняти · 5/6</button><button class="y-btn">Узяти збіжжя з ринку</button></div>
  </section>
  <section class="mkt">
    <div class="hd"><span class="lbl onwood">Ринок</span><span class="lbl onwood" style="font-weight:600;letter-spacing:.02em;text-transform:none">дві карти або одного вола</span></div>
    <div class="mk">${M.market.map((k) => `<div class="cardbox ${k === 'green' ? 'hot' : ''}">${card(k)}<div class="cname" ">${k === 'wild' ? 'віл' : TYPES[k].name}</div></div>`).join('')}
      <div class="deck"><div class="bk" style="transform:translate(3px,-3px) rotate(1.5deg)"></div><div class="bk" style="transform:translate(1.5px,-1.5px) rotate(.6deg)"></div><div class="bk fc"><b>${M.deck}</b><small>колода</small></div></div>
    </div>
  </section>
  <section>
    <div class="hd"><span class="lbl onwood">Мої замовлення</span><span class="lbl onwood" style="font-weight:600;letter-spacing:.02em;text-transform:none">виконано 1 з 3</span></div>
    <div class="orders">${orders}</div>
  </section>
  <section class="log paper"><span class="lbl">Журнал</span><ul>${log}</ul></section>
  <div class="tip">${glek(58, 'gl')}<div class="sp paper">Оля, до Полтави бракує одного снопа. <b>Збіжжя лежить на ринку</b> — бери, поки Петро не вхопив!</div></div>
</aside>
</body></html>`;
  writeFileSync(OUT + 'pc.html', html);
}
const shadeL = (hex, k) => { const n = parseInt(hex.slice(1), 16); const r = n >> 16, g = (n >> 8) & 255, b = n & 255; const m = (c) => Math.round(k < 0 ? c * (1 + k) : c + (255 - c) * k); return '#' + ((1 << 24) | (m(r) << 16) | (m(g) << 8) | m(b)).toString(16).slice(1); };

// ═════════ ТЕЛЕФОН ═════════
function buildPhone() {
  const W = 390, H = 844, top = 56, tabs = 64;
  const stripH = 52, mapTop = top + stripH, mapH = 316, railH = 46;
  const sc = 0.4;
  const vbW = W / sc, vbH = mapH / sc, vbX = 1830, vbY = 386;
  const o = { sc, T: 18 / sc, node: 9.5 / sc, clear: 11.5 / sc, gap: 2.2 / sc, cap: 58 / sc, fs: 12.4, bounds: [vbX + 4, vbY + 4, vbX + vbW - 4, vbY + vbH - 4], view: [vbX, vbY, vbX + vbW, vbY + vbH], fixed: { 'Кременчук': [-1, .3], 'Полтава': [-.75, 1] }, force: { 6: -190, 31: 90 }, riverFs: 11.5, rivers: [['Дніпро', 'Дніпро'], ['Десна', 'Десна']],
    top: (u, SL) => {
      const mid = SL.slots[3]; const cx = mid.x + u(6), cy = mid.y - u(48), w = u(128), h = u(30);
      return `<g filter="url(#pieceShadow)"><path d="M${mid.x} ${mid.y - u(12)} L${mid.x - u(8)} ${cy + h / 2 - u(1)} L${mid.x + u(8)} ${cy + h / 2 - u(1)}Z" fill="#2a160b"/><rect x="${cx - w / 2}" y="${cy - h / 2}" width="${w}" height="${h}" rx="${u(7)}" fill="#2a160b"/>
      <use href="#gl-green" x="${cx - w / 2 + u(5)}" y="${cy - u(9)}" width="${u(18)}" height="${u(18)}"/>
      <text x="${cx - w / 2 + u(26)}" y="${cy + u(5)}" font-size="${u(13)}" font-weight="800" fill="#fff6da">6 возів</text>
      <rect x="${cx + w / 2 - u(46)}" y="${cy - u(11)}" width="${u(41)}" height="${u(22)}" rx="${u(5)}" fill="#ffd35a"/><text x="${cx + w / 2 - u(25.5)}" y="${cy + u(5.2)}" text-anchor="middle" font-size="${u(14)}" font-weight="900" fill="#2a1e03">+15</text></g>`;
    },
  };
  const { svg } = buildMap(o);
  const chip = (p, i) => `<div class="pc ${p.me ? 'on' : ''}" style="--pc:${p.color}"><span class="dsc">${p.name.startsWith('Глек') ? glek(26) : initial(p)}${markIcon(i, p.color)}</span><span class="pt"><b>${p.points}</b><small>${ICON.cart('#5d4631')}${p.wagons} ${ICON.cards('#5d4631')}${p.cards}</small></span></div>`;
  const handOrder = ['green', 'wild', 'red', 'yellow', 'pink', 'blue', 'white'];
  const hand = handOrder.filter((k) => M.myHand[k] > 0).map((k) => `<button class="hc ${k === 'green' || k === 'wild' ? 'pay' : ''}">${M.myHand[k] > 1 ? `<i class="bk2">${card(k)}</i>` : ''}<span class="cardbox">${card(k)}<em>${k === 'wild' ? 'віл' : TYPES[k].name}</em></span><b class="cnt">${M.myHand[k]}</b></button>`).join('');
  const market = M.market.map((k) => `<button class="mc ${k === 'green' ? 'hot' : ''}"><span class="cardbox">${card(k)}<em>${k === 'wild' ? 'віл' : TYPES[k].name}</em></span></button>`).join('');
  const costs = [];
  for (let i = 0; i < 6; i++) costs.push(i < M.myHand.green ? 'green' : i === M.myHand.green ? 'wild' : null);
  const costHtml = costs.map((k) => k ? `<i class="pp" style="background:${TYPES[k].slot}"><svg viewBox="-10 -10 20 20"><g fill="${TYPES[k].glyphC}">${GLYPH[k]}</g></svg></i>` : `<i class="pp miss">?</i>`).join('');
  // мінімапа
  const mmW = 86, mmH = mmW * 2730 / 4000, ms = mmW / 4000;
  const mini = `<svg width="${mmW}" height="${mmH.toFixed(0)}" viewBox="0 0 4000 2730">
    <rect x="-20" y="-20" width="4040" height="2770" fill="#efe3c0"/><path d="${seaD}" fill="#3aa8e4"/><path d="${landD}" fill="#a8d868"/><path d="${outlineD}" fill="none" stroke="#355a2d" stroke-width="20"/>
    ${FX.routes.filter((r) => CLAIM[r.id] != null).map((r) => { const a = FX.cities.find((c) => c.name === r.a), b = FX.cities.find((c) => c.name === r.b); return `<path d="M${a.x} ${a.y}L${b.x} ${b.y}" stroke="${M.players[CLAIM[r.id]].color}" stroke-width="34" stroke-linecap="round"/>`; }).join('')}
    ${Object.keys(ORDER_CITIES).map((n) => { const c = FX.cities.find((x) => x.name === n); return `<circle cx="${c.x}" cy="${c.y}" r="44" fill="#fff" stroke="#c0392b" stroke-width="22"/>`; }).join('')}
    <rect x="${vbX}" y="${vbY}" width="${vbW}" height="${vbH}" fill="#fff" fill-opacity=".35" stroke="#2a160b" stroke-width="42"/>
  </svg>`;
  const html = `<!doctype html><html lang="uk"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Соляний шлях — телефон</title><style>${BASE_CSS}
body{width:${W}px;height:${H}px;position:relative}
.site{padding:8px 10px;gap:8px;z-index:30}
.site .brand{font-size:1.02em;gap:7px}.site .brand svg{width:28px;height:28px}
.mtabs{position:absolute;left:0;right:0;bottom:0;height:${tabs}px;z-index:30;display:flex;background:rgba(15,31,24,.98);border-top:1px solid #2f4d3d;font-family:system-ui,-apple-system,sans-serif}
.mtabs button{flex:1;display:flex;flex-direction:column;gap:1px;align-items:center;justify-content:center;font-size:11.5px;color:#9db3a5;position:relative}
.mtabs .ico{font-size:1.5em;line-height:1}.mtabs .on{color:#f4c542}
.mtabs .badge{position:absolute;top:5px;left:calc(50% + 7px);background:#f4c542;color:#2a1e03;border-radius:999px;font-size:9.5px;font-weight:700;padding:0 5px}
.strip{position:absolute;left:0;right:0;top:${top}px;height:${stripH}px;z-index:9;display:flex;align-items:center;gap:4px;padding:4px 6px;background:linear-gradient(180deg,#a26c35,#82522a);box-shadow:inset 0 1.5px 0 rgba(255,235,195,.45),0 3px 6px rgba(0,0,0,.5)}
.pc{position:relative;flex:1;min-width:0;height:44px;display:flex;align-items:center;gap:3px;padding:0 3px 0 3px;border-radius:9px;background:linear-gradient(180deg,#fdf7e8,#efe0c2);box-shadow:inset 0 -3px 0 var(--pc),0 1px 2px rgba(20,8,2,.6)}
.pc.on{box-shadow:inset 0 -3px 0 var(--pc),0 0 0 2px #ffe066,0 0 0 3.2px #2a160b}
.dsc{position:relative;flex:none;width:26px;height:26px;border-radius:50%;background:var(--pc);display:grid;place-items:center;color:#fff;font-weight:900;font-size:13px;box-shadow:0 0 0 1px #2a160b}
.pc:nth-child(2) .dsc{background:#fbf4e4}
.mrk{position:absolute;right:-4px;bottom:-3px;width:13px;height:13px}
.pt{min-width:0;line-height:1}.pt b{display:block;font-size:17px;font-weight:900;letter-spacing:-.02em}
.pt small{display:flex;align-items:center;gap:0;font-size:9.5px;font-weight:800;color:#4a3524;white-space:nowrap;margin-top:1px}.pt small .i{width:9.5px;height:9.5px;margin-right:1px}
.tm{flex:none;width:52px;height:44px;border-radius:9px;display:flex;flex-direction:column;align-items:center;justify-content:center;background:#2a160b;color:#ffe066;font-weight:900;font-size:15.5px;line-height:1;font-variant-numeric:tabular-nums;box-shadow:inset 0 0 0 1.2px #ffe066}
.tm small{font-size:8.8px;font-weight:700;color:#fff0cf;margin-top:2px}
.vp{position:absolute;left:0;right:0;top:${mapTop}px;height:${mapH}px;overflow:hidden;z-index:2;background:#efe3c0;box-shadow:inset 0 0 0 1px #2a160b}
.vp>svg{position:absolute;left:0;top:0}
.vign{position:absolute;inset:0;pointer-events:none;box-shadow:inset 0 0 22px rgba(30,12,2,.5)}
.zm{position:absolute;right:7px;bottom:15px;display:flex;flex-direction:column;gap:6px;z-index:4}
.zb{width:44px;height:44px;display:grid;place-items:center;font-size:25px;font-weight:900;line-height:1;color:#2a160b}
.zb.ft svg{width:23px;height:23px}
.mm{position:absolute;left:7px;bottom:7px;z-index:4;padding:3px;border-radius:7px;background:#fbf4e4;box-shadow:0 0 0 1.5px #2a160b,0 4px 8px rgba(20,8,2,.5);line-height:0;display:block}
.mm span{display:flex;align-items:center;justify-content:center;gap:3px;height:15px;font-size:9.8px;font-weight:800;line-height:1;white-space:nowrap}
.rail{position:absolute;left:0;right:0;top:${mapTop + mapH}px;height:${railH}px;z-index:6;display:flex;align-items:center;gap:5px;padding:0 7px 0 8px;background:linear-gradient(180deg,#a26c35,#82522a);box-shadow:inset 0 1.5px 0 rgba(255,235,195,.45),0 3px 6px rgba(0,0,0,.5);font-size:15px}
.rail .off{height:44px;display:block}
.rl{font-size:11px;font-weight:800;color:#fff0cf;text-shadow:0 1px 1px rgba(0,0,0,.6);white-space:nowrap}
.gr{position:absolute;z-index:8;pointer-events:none}
.rail .zb{width:40px;height:40px;font-size:24px}
.mm svg{border-radius:4px}
.pan{position:absolute;right:7px;bottom:7px;z-index:4;height:32px;display:flex;align-items:center;gap:5px;padding:0 10px;border-radius:999px;background:rgba(42,22,11,.9);color:#fff0cf;font-size:12px;font-weight:700}
.off{font-size:11.5px;font-weight:800;white-space:nowrap}
.off::before{content:none}
.off{position:relative}
.off i{font-style:normal;color:#c0392b;font-weight:900}
.sheet{position:absolute;left:6px;right:6px;top:${mapTop + mapH + railH + 5}px;height:88px;z-index:7;padding:7px 9px 8px;border-top:5px solid #0f7f3f;border-radius:8px}
.sheet .r1{display:flex;align-items:center;gap:6px;height:22px}
.sheet h3{margin:0;font-size:15.5px;font-weight:900;white-space:nowrap;letter-spacing:-.01em}
.sheet .pl{color:#b3261e;font-weight:900;font-size:14px}
.sheet .pm{display:flex;gap:2px;margin-left:auto}
.pp{width:18px;height:18px;border-radius:4px;box-shadow:inset 0 0 0 1px #2a160b;display:grid;place-items:center;font-style:normal}
.pp svg{width:14px;height:14px}.pp.miss{background:#fff;box-shadow:none;border:1.6px dashed #b3261e;color:#b3261e;font-weight:900;font-size:12px}
.sheet .r2{display:grid;grid-template-columns:1fr 1.5fr;gap:7px;margin-top:6px}
.sheet .r2>*{height:44px;display:flex;align-items:center;justify-content:center;gap:5px;font-size:13.5px;white-space:nowrap}
.tray{position:absolute;left:0;right:0;top:${mapTop + mapH + railH + 5 + 88 + 7}px;bottom:${tabs}px;z-index:5;padding:0 6px;display:flex;flex-direction:column;gap:5px;justify-content:space-between}
.row{display:flex;gap:4px;align-items:stretch}
.off span{display:flex;align-items:center;gap:3px;height:28px;margin:6px 0;padding:0 8px;border-radius:999px;background:#fff8e6;box-shadow:0 0 0 1.3px #c0392b,0 2px 5px rgba(20,8,2,.5)}
.tag{flex:none;width:17px;border-radius:5px;writing-mode:vertical-rl;transform:rotate(180deg);display:grid;place-items:center;font-size:9.5px;font-weight:900;letter-spacing:.09em;text-transform:uppercase;color:#fff0cf;background:rgba(30,14,5,.72)}
.cols{flex:1;display:grid;gap:4px;min-width:0}
.mc,.hc{position:relative;display:block;min-width:0;height:72px}
.mc .cardbox,.hc .cardbox{display:block;width:100%;height:100%;overflow:hidden;border-radius:6px;background:#fbf4e4}
.cardbox svg.cardart{height:auto}
.cardbox em{position:absolute;left:0;right:0;bottom:0;height:16px;display:grid;place-items:center;background:#fbf4e4;font-style:normal;font-weight:800;font-size:10px;line-height:1;color:#2a160b;box-shadow:0 -1px 0 rgba(42,22,11,.6)}
.hc .cnt{left:-3px;top:-4px;min-width:22px;height:22px;font-size:13px}
.hc.pay .cardbox{outline:2.5px solid #ffe066;outline-offset:-.5px}
.hc .bk2{position:absolute;inset:0;transform:translate(2.2px,-2.4px) rotate(2deg);display:block;border-radius:6px;overflow:hidden;height:72px}
.mc.hot .cardbox{outline:3px solid #ffe066;outline-offset:0}
.deckm{position:relative;height:72px;display:flex;align-items:center;justify-content:center;flex-direction:column;border-radius:6px;background:linear-gradient(145deg,#8a5324,#5e3314);box-shadow:inset 0 0 0 2px #fbf4e4,0 0 0 1px #2a160b,2px -2px 0 #5e3314,3px -3px 0 #fbf4e4;color:#fff0cf;font-weight:900;font-size:17px;line-height:1}
.deckm small{font-size:9.5px;font-weight:700;margin-top:3px}
.seg{display:grid;grid-template-columns:1.32fr 1fr 44px;gap:5px;margin-bottom:5px}
.seg>*{height:42px;display:flex;align-items:center;justify-content:center;gap:5px;font-weight:800;font-size:12.5px;white-space:nowrap}
.seg .n{background:#b3261e;color:#fff;border-radius:6px;font-size:11px;padding:1px 5px;font-weight:900}
.seg .g{padding:0;background:linear-gradient(180deg,#fdf7e8,#f1e4c8);position:relative}
.seg .g i{position:absolute;right:3px;top:3px;width:9px;height:9px;border-radius:50%;background:#e2492f;box-shadow:0 0 0 1.4px #fff}
.bubble{position:absolute;left:8px;right:60px;top:8px;z-index:4;display:flex;gap:6px;align-items:center;padding:4px 9px 4px 4px;font-size:11.8px;font-weight:600;line-height:1.2}
.bubble b{color:#b3261e;font-weight:900}
</style></head><body>
<svg class="defs"><defs>${cardDefs()}${mapDefs()}<filter id="grain" x="0" y="0" width="100%" height="100%"><feTurbulence type="fractalNoise" baseFrequency=".75" numOctaves="2" seed="4"/><feColorMatrix values="0 0 0 0 .22  0 0 0 0 .14  0 0 0 0 .05  0 0 0 .13 0"/></filter></defs></svg>
${table(W, H, 12)}
<header class="site"><div class="brand">${siteIcon}<span>Глечики</span></div><div class="sp"></div><span class="chip">🧂 Соляний шлях</span><div class="me" style="padding:3px 9px 3px 3px;font-size:13px"><span class="ava">О</span><span style="color:#f4c542">🏺 128</span></div></header>
<div class="strip">${M.players.map(chip).join('')}<div class="tm">0:47<small>твій хід</small></div></div>
<div class="vp">
  <svg width="${W}" height="${mapH}" viewBox="${vbX} ${vbY} ${vbW} ${vbH}">${svg}</svg>
  <svg width="${W}" height="${mapH}" style="mix-blend-mode:multiply"><rect width="${W}" height="${mapH}" filter="url(#grain)"/></svg>
  <div class="vign"></div>
  <button class="mm">${mini}<span>${ICON.hand} тягни мапу</span></button>
</div>
<div class="rail"><span class="rl">Замовлення:</span><button class="off"><span><i>↓</i> Одеса</span></button><button class="off"><span><i>↓</i> Сімферополь</span></button><div style="flex:1"></div><button class="zb wood" aria-label="Віддалити">−</button><button class="zb wood" aria-label="Наблизити">+</button></div>
<div class="gr" style="left:259px;top:${mapTop + mapH + 6}px">${saltScatter(36, 34, 22, 21)}</div>
<section class="sheet paper">
  <div class="r1"><h3>${SEL.a} — ${SEL.b}</h3><b class="pl">+15</b><div class="pm">${costHtml}</div></div>
  <div class="r2"><button class="lk-btn">${ICON.lock} Зайняти 5/6</button><button class="y-btn">Узяти збіжжя з ринку</button></div>
</section>
<div class="tray">
  <div class="row"><div class="tag">ринок</div><div class="cols" style="grid-template-columns:repeat(6,1fr)">${market}<button class="deckm">${M.deck}<small>колода</small></button></div></div>
  <div class="row"><div class="tag">рука</div><div class="cols" style="grid-template-columns:repeat(7,1fr)">${hand}</div></div>
  <div class="seg"><button class="wood" style="color:#2a160b">${ICON.scroll('#2a160b')} Замовлення <span class="n">1/3</span></button><button class="wood" style="color:#2a160b">Журнал</button><button class="wood g" aria-label="Порада Глека">${glek(36)}<i></i></button></div>
</div>
<nav class="mtabs"><button><span class="ico">📻</span>Ефір</button><button><span class="ico">🎵</span>Бібліотека</button><button class="on"><span class="ico">🎮</span>Ігри<span class="badge">хід</span></button><button><span class="ico">📊</span>Хто скільки</button><button><span class="ico">💬</span>Балачки</button></nav>
</body></html>`;
  writeFileSync(OUT + 'phone.html', html);
}
buildPC(); buildPhone();
console.log('ok');
