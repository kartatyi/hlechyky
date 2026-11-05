// Сама мапа-розпис: землі, моря, річки, шляхи, вози, міста. Один код на обидва екрани — різний лише viewBox і кегль.
import { regions, outlineD, bordersD, rivers, adjacency, blackSea, azovSea, solveRoutes, placeLabels, fx, geo, city, prj } from './geom.mjs';
import { petal, flower, sunflower, kalyna, leaf, sprig, brush, mix, rng, f1, GLYPH, SLOT, ICON, PAL } from './art.mjs';

export const R = solveRoutes({ 6: .46 });
const FIELDS = ['#561a2c', '#14463a', '#3c1f5c', '#5a3c12', '#114a57', '#622914'];
const adj = adjacency();
const fieldIdx = (() => {
  // зафарбування: сусіди різні, кольори рівномірно; сусідні ті самі — вручну допилено порядком обходу
  const order = regions.map((_, i) => i).sort((a, b) => adj[b].size - adj[a].size), out = {}, use = FIELDS.map(() => 0);
  for (const i of order) { const bad = new Set([...adj[i]].map((j) => out[j])); let best = -1; for (let c = 0; c < FIELDS.length; c++) if (!bad.has(c) && (best < 0 || use[c] < use[best])) best = c; out[i] = best; use[best]++; }
  return out;
})();
const SEA = '#10255f', GROUND = '#08070f';
export const PL = fx.mock.players.map((p, i) => ({ ...p, i }));
const EMB = [`<path d="M0-9L9 0 0 9-9 0Z"/>`, `<circle r="8"/>`, `<path d="M0-9L9.5 7H-9.5Z"/>`, `<rect x="-7.5" y="-7.5" width="15" height="15" rx="1"/>`];
export const emblem = (i, fill = '#fff4dc') => `<g fill="${fill}">${EMB[i]}</g>`;

function defs() {
  let s = '<defs>';
  for (const k in GLYPH) s += `<g id="g-${k}">${GLYPH[k]}</g>`;
  PL.forEach((p, i) => {
    const c = p.color, lt = mix(c, '#ffffff', .16), dk = mix(c, '#ffffff', .05);
    s += `<g id="cart-${i}"><g stroke="#0a0812" stroke-width="3.4" stroke-linejoin="round"><circle cx="-25" cy="17" r="11.5" fill="#fff4dc"/><circle cx="25" cy="17" r="11.5" fill="#fff4dc"/><path d="M-45 2H45L39 14H-39Z" fill="${dk}"/><path d="M-39 3C-39-34 39-34 39 3Z" fill="${lt}"/></g><path d="M-21-20V3M21-20V3" stroke="#fff4dc" stroke-width="2.4" opacity=".75"/><g transform="translate(0 -9) scale(1.05)">${emblem(i)}</g><circle cx="-25" cy="17" r="3.2" fill="#0a0812"/><circle cx="25" cy="17" r="3.2" fill="#0a0812"/></g>`;
  });
  s += `<g id="ros"><circle r="38" fill="#0a0812"/>`;
  for (let i = 0; i < 8; i++) s += petal(0, 0, i * 45 + 22.5, 34, 19, '#fbf1da');
  s += `<circle r="10" fill="#d9301f"/></g>`;
  regions.forEach((r, i) => { s += `<clipPath id="rc${i}"><path d="${r.d}"/></clipPath>`; });
  s += `<clipPath id="seaclip"><path d="${blackSea}${azovSea}"/></clipPath>`;
  s += `<filter id="goldglow" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="16"/></filter>`;
  return s + '</defs>';
}

// тон-у-тон розпис області: ледь світліше за поле — живе, але не бореться зі шляхами
function regionOrnament() {
  let s = '';
  regions.forEach((r, i) => {
    const f = FIELDS[fieldIdx[i]], t = mix(f, '#ffffff', .085), t2 = mix(f, '#ffffff', .15);
    const w = r.bb[2] - r.bb[0], h = r.bb[3] - r.bb[1], m = Math.min(w, h), cx = r.cx, cy = r.cy;
    let g = '';
    if (m < 90) { s += ''; return; }
    const mono = { a: t, ah: t2, b: t2, bh: t, c: f, dot: t2 };
    const K = i % 4;
    if (K === 0) { g += sunflower(cx, cy, m * .44, { a: t, ah: t2, b: t, disc: f, seed: t2, seed2: t2 }); }
    else if (K === 1) { g += flower(cx, cy, m * .42, mono); g += sprig([cx - m * .55, cy + m * .5], [cx - m * .2, cy + m * .4], [cx, cy + m * .55], [cx + m * .55, cy + m * .35], { L: m * .17, W: m * .07, w: m * .02, n: 6, a: t, h: t2, stem: t }); }
    else if (K === 2) { g += kalyna(cx, cy, m * .34, i + 5, { a: t2, hi: f, d: f, n: 14 }); g += leaf(cx - m * .25, cy - m * .25, -135, m * .4, m * .22, t, t2) + leaf(cx + m * .25, cy - m * .2, -48, m * .4, m * .22, t, t2) + leaf(cx + m * .1, cy + m * .3, 70, m * .38, m * .2, t, t2); }
    else { g += sprig([cx - m * .6, cy + m * .3], [cx - m * .2, cy - m * .3], [cx + m * .2, cy + m * .3], [cx + m * .6, cy - m * .3], { L: m * .24, W: m * .09, w: m * .03, n: 8, a: t, h: t2, stem: t }); }
    s += `<g clip-path="url(#rc${i})">${g}</g>`;
  });
  return s;
}

function seaWaves() {
  const Rn = rng(11); let s = '';
  const t = mix(SEA, '#6f9bff', .22), t2 = mix(SEA, '#9fc0ff', .32);
  for (let y = 1900; y < 2760; y += 44) {
    const off = (y / 44 % 2) * 60;
    for (let x = 1300 + off; x < 4050; x += 120) {
      const L = 52 + Rn() * 26, dy = (Rn() - .5) * 8;
      if (Rn() < .22) continue;
      s += `<path d="M${f1(x)} ${f1(y + dy)}q${f1(L * .5)} -15 ${f1(L)} 0q${f1(-L * .5)} -6 ${f1(-L)} 0Z" fill="${Rn() < .25 ? t2 : t}"/>`;
    }
  }
  return `<g clip-path="url(#seaclip)">${s}</g>`;
}

const upright = (ang) => { let a = ang * 180 / Math.PI; if (a > 90) a -= 180; if (a < -90) a += 180; return a; };
function slotTile(c, s, wide, sel) {
  const len = Math.max(s.l - 11, 18), col = SLOT[c], a = s.ang * 180 / Math.PI;
  const stroke = c === 'black' ? '#fbf1da' : '#0a0812';
  let o = `<g transform="translate(${f1(s.x)} ${f1(s.y)})"><g transform="rotate(${f1(a)})"><rect x="${f1(-len / 2)}" y="${-wide / 2}" width="${f1(len)}" height="${wide}" rx="9" fill="${col}" stroke="${stroke}" stroke-width="${c === 'black' ? 3.6 : 3}"/></g>`;
  if (GLYPH[c]) o += `<use href="#g-${c}" transform="scale(${f1(wide / 26)})"/>`;
  return o + '</g>';
}
export function routesLayer(sel, wide = 34) {
  const owner = {}; for (const p in fx.mock.claimed) for (const id of fx.mock.claimed[p]) owner[id] = +p;
  let base = '', claim = '';
  const ordered = [...R].sort((a, b) => (a.color === 'any' ? -1 : 0) - (b.color === 'any' ? -1 : 0));
  for (const X of ordered) {
    if (X.id === sel || owner[X.id] != null) continue;
    for (const s of X.slots) base += slotTile(X.color, s, wide);
  }
  for (const X of R) {
    const o = owner[X.id]; if (o == null) continue;
    const c = PL[o].color;
    claim += `<path d="${X.d}" fill="none" stroke="#0a0812" stroke-width="72" stroke-linecap="round"/><path d="${X.d}" fill="none" stroke="${mix(c, '#ffffff', .12)}" stroke-width="62" stroke-dasharray="${['none', 'none', '36 13', '11 11'][o]}"/><path d="${X.d}" fill="none" stroke="${mix(c, '#000000', .62)}" stroke-width="46" stroke-linecap="round"/>`;
    for (const s of X.slots) { const a = upright(s.ang); const sx = Math.min(1, (s.l - 8) / 96), sc = Math.min(1, Math.max(.62, sx)); claim += `<use href="#cart-${o}" transform="translate(${f1(s.x)} ${f1(s.y)}) rotate(${f1(a)}) scale(${f1(sc)})"/>`; }
  }
  let selS = '';
  const X = R[sel];
  if (X) {
    selS += `<path d="${X.d}" fill="none" stroke="#ffd34d" stroke-width="96" stroke-linecap="round" opacity=".42" filter="url(#goldglow)"/>`;
    selS += `<path d="${X.d}" fill="none" stroke="#0a0812" stroke-width="82" stroke-linecap="round"/><path d="${X.d}" fill="none" stroke="#ffd34d" stroke-width="72" stroke-linecap="round"/><path d="${X.d}" fill="none" stroke="#0a0812" stroke-width="58" stroke-linecap="round"/>`;
    for (const s of X.slots) selS += slotTile(X.color, s, 46);
  }
  return base + claim + selS;
}
export function citiesLayer(lab, fu, seals = {}, hi = []) {
  let s = '';
  for (const C of fx.cities) {
    const seal = seals[C.name];
    if (seal) {
      s += `<g transform="translate(${C.x} ${C.y})"><circle r="${f1(fu * 1.04)}" fill="#0a0812"/><circle r="${f1(fu * .88)}" fill="#ffd34d"/><circle r="${f1(fu * .68)}" fill="none" stroke="#7a4d08" stroke-width="${f1(fu*.05)}" stroke-dasharray="${f1(fu*.1)} ${f1(fu*.1)}"/><text y="${f1(fu * .34)}" text-anchor="middle" font-size="${f1(fu * .94)}" font-weight="900" fill="#1b0f06">${seal}</text></g>`;
    } else {
      const big = hi.includes(C.name);
      s += `<use href="#ros" transform="translate(${C.x} ${C.y}) scale(${big ? 1.25 : .94})"/>`;
    }
  }
  for (const C of fx.cities) {
    const L = lab[C.name], on = hi.includes(C.name) || !!seals[C.name];
    L.ls.forEach((t, i) => {
      const x = L.x + L.w / 2, y = L.y + L.f * .86 + i * L.f * 1.04;
      s += `<text x="${f1(x)}" y="${f1(y)}" text-anchor="middle" font-size="${f1(L.f)}" font-weight="${on ? 800 : 700}" fill="${on ? '#ffe08a' : '#fbf1da'}" stroke="#08070f" stroke-width="${f1(L.f * .3)}" stroke-linejoin="round" paint-order="stroke">${t}</text>`;
    });
  }
  return s;
}
export function baseLayer() {
  let s = `<rect x="-400" y="-400" width="4800" height="3600" fill="${GROUND}"/><path d="${blackSea}" fill="${SEA}"/><path d="${azovSea}" fill="${SEA}"/>` + seaWaves();
  // поля
  regions.forEach((r, i) => { s += `<path d="${r.d}" fill="${FIELDS[fieldIdx[i]]}"/>`; });
  s += regionOrnament();
  // межі: щілина кольору ґрунту, як у ліногравюрі
  s += `<path d="${bordersD}" fill="none" stroke="#0a0812" stroke-width="7" stroke-linejoin="round"/>`;
  // річки
  for (const rv of rivers) s += `<path d="${rv.d}" fill="none" stroke="#0a0812" stroke-width="${rv.big ? 22 : 13}" stroke-linecap="round" stroke-linejoin="round" opacity=".85"/>`;
  for (const rv of rivers) s += `<path d="${rv.d}" fill="none" stroke="#63d4e6" stroke-width="${rv.big ? 13 : 6.5}" stroke-linecap="round" stroke-linejoin="round"/>`;
  // контур: вишита «шнурівка» червоно-золота
  s += `<path d="${outlineD}" fill="none" stroke="#0a0812" stroke-width="20" stroke-linejoin="round"/><path d="${outlineD}" fill="none" stroke="#f6bf26" stroke-width="9" stroke-linejoin="round"/><path d="${outlineD}" fill="none" stroke="#d9301f" stroke-width="9" stroke-dasharray="17 17"/>`;
  return s;
}
export function riverLabels(fu, list) {
  let s = '';
  for (const [t, x, y, rot] of list) s += `<text transform="translate(${x} ${y}) rotate(${rot})" text-anchor="middle" font-size="${f1(fu * .82)}" font-weight="600" font-style="italic" fill="#8fe4f2" stroke="#08070f" stroke-width="${f1(fu * .22)}" paint-order="stroke" stroke-linejoin="round">${t}</text>`;
  return s;
}
export { defs, GROUND, SEA, FIELDS };
