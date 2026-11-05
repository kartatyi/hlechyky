// Петриківка: мазок-«зернятко», квітка, соняшник, калина, пагін; піктограми вантажів і карти.
export const f1 = (v) => Math.round(v * 10) / 10;
export function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
export function mix(a, b, k) { const p = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16)); const A = p(a), B = p(b); return '#' + A.map((v, i) => Math.round(v + (B[i] - v) * k).toString(16).padStart(2, '0')).join(''); }

// мазок: тонкий початок, круглий кінець — основний жест петриківки
export function petalD(L, W, bend = 0) {
  const b = bend * L;
  return `M0 0C${f1(L * .3)} ${f1(-W * .16 + b * .4)} ${f1(L * .55)} ${f1(-W * .62 + b)} ${f1(L * .8)} ${f1(-W * .5 + b)}C${f1(L * 1.03)} ${f1(-W * .36 + b)} ${f1(L * 1.03)} ${f1(W * .38 + b)} ${f1(L * .8)} ${f1(W * .5 + b)}C${f1(L * .55)} ${f1(W * .62 + b)} ${f1(L * .3)} ${f1(W * .16 + b * .4)} 0 0Z`;
}
export function petal(x, y, deg, L, W, fill, opt = {}) {
  const { hi, bend = 0 } = opt;
  let s = `<g transform="translate(${f1(x)} ${f1(y)}) rotate(${f1(deg)})"><path d="${petalD(L, W, bend)}" fill="${fill}"/>`;
  if (hi) s += `<path d="M${f1(L * .28)} ${f1(-W * .02)}C${f1(L * .5)} ${f1(-W * .34)} ${f1(L * .72)} ${f1(-W * .38)} ${f1(L * .9)} ${f1(-W * .18)}C${f1(L * .72)} ${f1(-W * .18)} ${f1(L * .5)} ${f1(-W * .14)} ${f1(L * .28)} ${f1(-W * .02)}Z" fill="${hi}"/>`;
  return s + '</g>';
}
// пензлевий стебельний штрих: кубічна крива зі змінною шириною
export function brush(p0, p1, p2, p3, w0, w1, fill) {
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
export function bezAt(p0, p1, p2, p3, t) { const u = 1 - t; const x = u * u * u * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t * t * t * p3[0], y = u * u * u * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t * t * t * p3[1]; const dx = 3 * u * u * (p1[0] - p0[0]) + 6 * u * t * (p2[0] - p1[0]) + 3 * t * t * (p3[0] - p2[0]), dy = 3 * u * u * (p1[1] - p0[1]) + 6 * u * t * (p2[1] - p1[1]) + 3 * t * t * (p3[1] - p2[1]); return [x, y, Math.atan2(dy, dx) * 180 / Math.PI]; }

const P = { red: '#d9301f', red2: '#ff6a3d', yel: '#f6bf26', yel2: '#ffe08a', org: '#ef7d1a', grn: '#2f9e4a', grn2: '#7ad36b', blu: '#2a6fd0', blu2: '#7cc0ff', cream: '#fbf1da', ink: '#0a0812', pink: '#ef6aa2' };
export const PAL = P;

// квітка-«жоржина»: два кола мазків, «пальчикові» крапки
export function flower(x, y, r, pal = {}) {
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
export function sunflower(x, y, r, pal = {}) {
  const a = pal.a || P.yel, ah = pal.ah || P.yel2, b = pal.b || P.org, disc = pal.disc || '#2a1408', seed = pal.seed || P.yel; let s = '';
  const n = pal.n || 19;
  for (let i = 0; i < n; i++) { const d = (i + .5) / n * 360; s += `<g transform="translate(${f1(x)} ${f1(y)}) rotate(${f1(d)})"><path d="M${f1(r * .42)} ${f1(-r * .13)}Q${f1(r * .8)} ${f1(-r * .19)} ${f1(r * 1.02)} 0Q${f1(r * .8)} ${f1(r * .19)} ${f1(r * .42)} ${f1(r * .13)}Z" fill="${b}"/></g>`; }
  for (let i = 0; i < n; i++) { const d = i / n * 360; s += `<g transform="translate(${f1(x)} ${f1(y)}) rotate(${f1(d)})"><path d="M${f1(r * .4)} ${f1(-r * .12)}Q${f1(r * .7)} ${f1(-r * .17)} ${f1(r * .88)} 0Q${f1(r * .7)} ${f1(r * .17)} ${f1(r * .4)} ${f1(r * .12)}Z" fill="${a}"/><path d="M${f1(r * .46)} ${f1(-r * .05)}Q${f1(r * .66)} ${f1(-r * .09)} ${f1(r * .78)} 0Q${f1(r * .66)} ${f1(-r * .03)} ${f1(r * .46)} ${f1(r * .02)}Z" fill="${ah}"/></g>`; }
  s += `<circle cx="${f1(x)}" cy="${f1(y)}" r="${f1(r * .46)}" fill="${disc}"/><circle cx="${f1(x)}" cy="${f1(y)}" r="${f1(r * .46)}" fill="none" stroke="${b}" stroke-width="${f1(r * .022)}"/>`;
  for (let i = 1; i < 90; i++) { const rr = r * .43 * Math.sqrt(i / 90), t = i * 2.39996; s += `<circle cx="${f1(x + Math.cos(t) * rr)}" cy="${f1(y + Math.sin(t) * rr)}" r="${f1(r * .012 + r * .012 * i / 90)}" fill="${i < 26 ? (pal.seed2 || P.org) : seed}"/>`; }
  return s;
}
// калина: гроно ягід з бликом і чорною цяткою, зубчасті листки
export function kalyna(x, y, r, seed = 3, pal = {}) {
  const R = rng(seed), a = pal.a || P.red, hi = pal.hi || P.red2, d = pal.d || P.ink; let s = '';
  const pts = []; let tries = 0;
  while (pts.length < (pal.n || 13) && tries++ < 400) { const t = R() * 6.283, q = Math.sqrt(R()) * r * .78; const px = x + Math.cos(t) * q, py = y + Math.sin(t) * q * .86; if (pts.every((p) => Math.hypot(p[0] - px, p[1] - py) > r * .34)) pts.push([px, py]); }
  for (const [px, py] of pts) { const b = r * .19; s += `<circle cx="${f1(px)}" cy="${f1(py)}" r="${f1(b)}" fill="${a}"/><path d="M${f1(px - b * .6)} ${f1(py - b * .2)}A${f1(b * .7)} ${f1(b * .7)} 0 0 1 ${f1(px + b * .1)} ${f1(py - b * .65)}" fill="none" stroke="${hi}" stroke-width="${f1(b * .28)}" stroke-linecap="round"/><circle cx="${f1(px + b * .3)}" cy="${f1(py + b * .35)}" r="${f1(b * .15)}" fill="${d}"/>`; }
  return s;
}
// листок-гребінець: мазок із паралельними зубцями
export function leaf(x, y, deg, L, W, a = P.grn, b = P.grn2) {
  let s = `<g transform="translate(${f1(x)} ${f1(y)}) rotate(${f1(deg)})"><path d="${petalD(L, W)}" fill="${a}"/>`;
  for (let i = 1; i <= 4; i++) { const t = .22 + i * .14; s += `<path d="M${f1(L * t)} 0L${f1(L * (t + .1))} ${f1(-W * .36)}" stroke="${b}" stroke-width="${f1(W * .07)}" stroke-linecap="round"/>`; }
  s += `<path d="M${f1(L * .1)} 0L${f1(L * .84)} 0" stroke="${b}" stroke-width="${f1(W * .05)}" stroke-linecap="round"/></g>`;
  return s;
}
// пагін: вигнуте стебло, мазки по обидва боки, закрут на кінці
export function sprig(p0, p1, p2, p3, opt = {}) {
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
export const ICON = {
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
export const CARD = {
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
export const ORDER = ['red', 'blue', 'green', 'black', 'yellow', 'white', 'orange', 'pink', 'wild'];
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
export function cardSymbol(c) {
  const k = CARD[c];
  const icon = ICON[c]();
  const dark = c === 'black';
  return `<symbol id="card-${c}" viewBox="0 0 120 84"><rect width="120" height="84" fill="${k.f}"/>${pat(c)}<g transform="translate(35.7 2) scale(.76)">${icon}</g>${dark ? '<rect x="1.5" y="1.5" width="117" height="81" rx="6" fill="none" stroke="#f6bf26" stroke-width="2"/>' : ''}</symbol>`;
}
// мікрогліфи слотів — ті самі, що й у кутку карт: 20×20 з центром у 0,0
export const GLYPH = {
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
export const SLOT = { red: '#e8341f', blue: '#2474e8', green: '#12a862', black: '#121019', yellow: '#fbc924', white: '#f6eeda', orange: '#f5851c', pink: '#f46fab', any: '#9b917d' };
// стрічка-вишивка: повторюваний ромбовий орнамент
export function stitchBand(w, h, opts = {}) {
  const s = h / 7, a = opts.a || '#d9301f', b = opts.b || '#f6bf26', c = opts.c || '#fbf1da';
  const rows = ['...a...', '..aba..', '.abcba.', 'abc.cba', '.abcba.', '..aba..', '...a...'];
  const col = { a, b, c }; let out = '';
  for (let X = 0; X < w; X += s * 7) rows.forEach((r, j) => { for (let i = 0; i < 7; i++) { const k = col[r[i]]; if (!k) continue; const x = X + i * s, y = j * s; out += `<path d="M${f1(x + s * .15)} ${f1(y + s * .15)}l${f1(s * .7)} ${f1(s * .7)}m${f1(-s * .7)} 0l${f1(s * .7)} ${f1(-s * .7)}" stroke="${k}" stroke-width="${f1(s * .34)}" stroke-linecap="round"/>`; } });
  return out;
}

// віл із возом: лого-ілюстрація, вісь X вправо, ~220×90
export function oxCart(x, y, s = 1) {
  const c = '#fbf1da', d = '#d9301f', g = '#f6bf26', ink = '#0a0812';
  return `<g transform="translate(${x} ${y}) scale(${s})"><g fill="${c}"><path d="M104 22C120 14 150 14 165 22C172 26 176 32 176 40C176 48 172 52 168 54L170 78H160L156 56C144 60 128 60 118 57L114 78H104L104 52C100 48 100 30 104 22Z"/><path d="M170 26C178 22 188 24 196 30C200 34 201 42 198 46C192 50 184 48 176 44Z"/></g><path d="M188 26C196 24 204 16 204 6C198 12 192 15 182 17ZM178 24C176 15 178 8 186 2C186 10 188 15 192 20Z" fill="${g}"/><circle cx="189" cy="33" r="2.2" fill="${ink}"/><g fill="${d}"><path d="M125 26l8 8-8 8-8-8z"/><path d="M146 26l8 8-8 8-8-8z"/></g><g fill="${g}"><path d="M136 41l5 5-5 5-5-5z"/></g><path d="M170 20V52" stroke="${g}" stroke-width="5" stroke-linecap="round"/><path d="M92 50L170 34" stroke="${c}" stroke-width="4"/><g stroke="${ink}" stroke-width="3" stroke-linejoin="round"><path d="M4 40C4 2 90 2 90 40Z" fill="#d9301f"/><path d="M0 40H94L88 54H6Z" fill="#f6bf26"/></g><path d="M24 12V40M47 7V40M70 12V40" stroke="${c}" stroke-width="3"/><g><circle cx="24" cy="62" r="17" fill="${c}" stroke="${ink}" stroke-width="3"/><circle cx="70" cy="62" r="17" fill="${c}" stroke="${ink}" stroke-width="3"/><path d="M24 47V77M9 62H39M13 51L35 73M35 51L13 73M70 47V77M55 62H85M59 51L81 73M81 51L59 73" stroke="${ink}" stroke-width="2.4"/><circle cx="24" cy="62" r="5" fill="${d}"/><circle cx="70" cy="62" r="5" fill="${d}"/></g></g>`;
}
