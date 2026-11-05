// «Соляний шлях» — малюнки плаского поля для трьох тем: перенесено з макетів дизайнерів
// (docs/games/dev/solyanyi-look/{parchment,board,petryk}), але на правдивій геометрії consts.js і без дорогих фільтрів:
// замість feTurbulence — хвиля в самих координатах, замість розмиття — вкладені штрихи й радіальні градієнти, а дрібне
// (ліс, трава, орнамент, хвилі) злито в один <path> на колір. Лише рядки SVG, жодного DOM. Рушій — board2d.js.

// ── дрібні помічники ──
export const f1 = (v) => Math.round(v * 10) / 10;
export function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
export function mix(a, b, k) { const p = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16)); const A = p(a), B = p(b); return '#' + A.map((v, i) => Math.round(v + (B[i] - v) * k).toString(16).padStart(2, '0')).join(''); }
const shade = (hex, k) => mix(hex, k < 0 ? '#000000' : '#ffffff', Math.abs(k));
const P = (pts) => pts.length ? 'M' + pts.map((p) => f1(p[0]) + ' ' + f1(p[1])).join('L') : '';
const circ = (x, y, r) => `M${f1(x - r)} ${f1(y)}a${f1(r)} ${f1(r)} 0 1 0 ${f1(2 * r)} 0a${f1(r)} ${f1(r)} 0 1 0 ${f1(-2 * r)} 0Z`;
const chaikin = (pts) => {
  if (pts.length < 3) return pts;
  const o = [pts[0]];
  for (let i = 0; i < pts.length - 1; i++) {
    const a = pts[i], b = pts[i + 1];
    if (i > 0) o.push([a[0] * .75 + b[0] * .25, a[1] * .75 + b[1] * .25]);
    if (i < pts.length - 2) o.push([a[0] * .25 + b[0] * .75, a[1] * .25 + b[1] * .75]);
  }
  o.push(pts[pts.length - 1]);
  return o;
};
const flat = (l) => { const p = []; for (let i = 0; i < l.length; i += 2) p.push([l[i], l[i + 1]]); return p; };

/** Переносить рядок шляху (абсолютні й відносні команди) у нове місце: зсув, масштаб і, для кривих без H/V/A, поворот. */
export function xf(d, tx, ty, k = 1, deg = 0) {
  const c = Math.cos(deg * Math.PI / 180), s = Math.sin(deg * Math.PI / 180), rot = deg !== 0;
  const X = (x, y) => f1(tx + (rot ? c * x - s * y : x) * k), Y = (x, y) => f1(ty + (rot ? s * x + c * y : y) * k);
  const rx = (x, y) => f1((rot ? c * x - s * y : x) * k), ry = (x, y) => f1((rot ? s * x + c * y : y) * k);
  let out = '', cx = 0, cy = 0;
  for (const m of d.matchAll(/([MLHVCQTSAZmlhvcqtsaz])([^MLHVCQTSAZmlhvcqtsaz]*)/g)) {
    const cmd = m[1], n = (m[2].match(/-?\d*\.?\d+/g) || []).map(Number);
    if (cmd === 'Z' || cmd === 'z') { out += 'Z'; continue; }
    if (cmd === 'H') { for (const v of n) { out += 'H' + f1(tx + v * k); cx = v; } continue; }
    if (cmd === 'V') { for (const v of n) { out += 'V' + f1(ty + v * k); cy = v; } continue; }
    if (cmd === 'h' || cmd === 'v') { for (const v of n) out += cmd + f1(v * k); continue; }
    if (cmd === 'A' || cmd === 'a') {
      for (let i = 0; i + 6 < n.length + 0.5 && i + 7 <= n.length; i += 7) {
        const e = cmd === 'A' ? `${X(n[i + 5], n[i + 6])} ${Y(n[i + 5], n[i + 6])}` : `${f1(n[i + 5] * k)} ${f1(n[i + 6] * k)}`;
        out += `${cmd}${f1(n[i] * k)} ${f1(n[i + 1] * k)} ${n[i + 2]} ${n[i + 3]} ${n[i + 4]} ${e}`;
      }
      continue;
    }
    out += cmd;
    const abs = cmd === cmd.toUpperCase();
    for (let i = 0; i + 1 < n.length; i += 2) out += (i ? ' ' : '') + (abs ? `${X(n[i], n[i + 1])} ${Y(n[i], n[i + 1])}` : `${rx(n[i], n[i + 1])} ${ry(n[i], n[i + 1])}`);
  }
  return out;
}
/** Шар, що зливає дрібні фігури одного вигляду в один <path>: ключ — рядок атрибутів. */
class Bucket {
  constructor() { this.m = new Map(); }
  add(attrs, d) { this.m.set(attrs, (this.m.get(attrs) || '') + d); return this; }
  fill(c, d) { return this.add(`fill="${c}"`, d); }
  line(c, w, d, extra = '') { return this.add(`fill="none" stroke="${c}" stroke-width="${f1(w)}" stroke-linecap="round" stroke-linejoin="round"${extra}`, d); }
  svg() { let s = ''; for (const [a, d] of this.m) s += `<path ${a} d="${d}"/>`; return s; }
}
/** Прямокутник із заокругленням, повернутий навколо центру, абсолютними координатами. */
export function rrectD(x, y, a, L, W, r) {
  const c = Math.cos(a), s = Math.sin(a), h = L / 2, v = W / 2; r = Math.min(r, h, v);
  const pt = (u, w) => f1(x + c * u - s * w) + ' ' + f1(y + s * u + c * w);
  const arc = (u, w) => `A${f1(r)} ${f1(r)} 0 0 1 ${pt(u, w)}`;
  return `M${pt(-h + r, -v)}L${pt(h - r, -v)}${arc(h, -v + r)}L${pt(h, v - r)}${arc(h - r, v)}L${pt(-h + r, v)}${arc(-h, v - r)}L${pt(-h, -v + r)}${arc(-h + r, -v)}Z`;
}
/** Слот, вигнутий за кривою шляху: для «Старої мапи», де плашки йдуть уздовж дороги. */
export function curvedSlotD(R, sl, hw) {
  const c = Math.cos(sl.a), s = Math.sin(sl.a), pts = R.curve.pts, L = [], Q = [];
  for (let i = 0; i <= 4; i++) {
    const t = (i / 4 - .5) * sl.len, x = sl.x + c * t, y = sl.y + s * t;
    let best = 1e12, nx = -s, ny = c, bx = x, by = y;
    for (let j = 1; j < pts.length; j++) {
      const a = pts[j - 1], b = pts[j], dx = b[0] - a[0], dy = b[1] - a[1], ll = dx * dx + dy * dy || 1, q = Math.max(0, Math.min(1, ((x - a[0]) * dx + (y - a[1]) * dy) / ll));
      const px = a[0] + dx * q, py = a[1] + dy * q, d = (px - x) ** 2 + (py - y) ** 2;
      if (d < best) { best = d; const l = Math.sqrt(ll); nx = -dy / l; ny = dx / l; bx = px; by = py; }
    }
    L.push([bx + nx * hw, by + ny * hw]); Q.unshift([bx - nx * hw, by - ny * hw]);
  }
  return P(L.concat(Q)) + 'Z';
}

// ── геометрія країни ──
const memoGeo = new Map();
/** Розбирає geo-map.json (§7.4 geo.md): контур — дуги, на які посилається рівно одне кільце. */
export function buildGeo(raw, opt = {}) {
  const key = (opt.smooth ? 's' : '') + (opt.wobble ? 'w' : '');
  if (memoGeo.has(key) && memoGeo.get(key).raw === raw) return memoGeo.get(key);
  const wob = opt.wobble ? (p) => [p[0] + 2.4 * Math.sin(p[1] * .035 + 1.3 * Math.sin(p[0] * .021)) + 1.2 * Math.sin(p[0] * .09 + p[1] * .05), p[1] + 2.4 * Math.sin(p[0] * .033 + 1.1 * Math.sin(p[1] * .019)) + 1.2 * Math.sin(p[1] * .085 - p[0] * .04)] : (p) => p;
  const conv = (l) => { let p = flat(l); if (opt.smooth) p = chaikin(p); return p.map(wob); };
  const arcsRaw = raw?.arcs || [], regsRaw = raw?.regions || [], rivRaw = raw?.rivers || [];
  const arcs = arcsRaw.map(conv), refs = new Uint8Array(arcs.length);
  for (const r of regsRaw) for (const ring of r.rings) for (const id of ring) refs[Math.abs(id) - 1]++;
  const regions = regsRaw.map((r) => {
    const rings = r.rings.map((ring) => { const o = []; ring.forEach((id, n) => { const a = arcs[Math.abs(id) - 1], seq = id > 0 ? a : [...a].reverse(); for (let k = n ? 1 : 0; k < seq.length; k++) o.push(seq[k]); }); return o; });
    const bb = [1e9, 1e9, -1e9, -1e9]; let area = 0, cx = 0, cy = 0;
    for (const pts of rings) for (let i = 0; i < pts.length; i++) {
      const [x0, y0] = pts[i], [x1, y1] = pts[(i + 1) % pts.length], c = x0 * y1 - x1 * y0;
      area += c; cx += (x0 + x1) * c; cy += (y0 + y1) * c;
      if (x0 < bb[0]) bb[0] = x0; if (y0 < bb[1]) bb[1] = y0; if (x0 > bb[2]) bb[2] = x0; if (y0 > bb[3]) bb[3] = y0;
    }
    return { name: r.name, rings, bb, cx: cx / (3 * area || 1), cy: cy / (3 * area || 1), d: rings.map((x) => P(x) + 'Z').join('') };
  });
  const inRing = (x, y, ring) => { let c = false; for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) { const a = ring[i], b = ring[j]; if ((a[1] > y) !== (b[1] > y) && x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]) c = !c; } return c; };
  const inLand = (x, y) => { for (const r of regions) { if (x < r.bb[0] || x > r.bb[2] || y < r.bb[1] || y > r.bb[3]) continue; for (const ring of r.rings) if (inRing(x, y, ring)) return true; } return false; };
  // суміжність областей — для «Петриківки»
  const byArc = new Map(); regsRaw.forEach((r, i) => { for (const ring of r.rings) for (const id of ring) { const k = Math.abs(id); if (!byArc.has(k)) byArc.set(k, []); byArc.get(k).push(i); } });
  const adj = regsRaw.map(() => new Set()); for (const l of byArc.values()) if (l.length > 1) for (const a of l) for (const b of l) if (a !== b) adj[a].add(b);
  // проєкція Альберса (data/geo/build-map.py) — щоб дорисувати чужі береги морів
  const pr = raw?.proj || { phi1: 46, phi2: 51, phi0: 48.5, lam0: 31.5, x0: -0.11, y0: 0.07, k: 19000 }, r_ = (d) => d * Math.PI / 180;
  const N = (Math.sin(r_(pr.phi1)) + Math.sin(r_(pr.phi2))) / 2, C = Math.cos(r_(pr.phi1)) ** 2 + 2 * N * Math.sin(r_(pr.phi1)), RHO0 = Math.sqrt(C - 2 * N * Math.sin(r_(pr.phi0))) / N;
  const proj = (lat, lon) => { const rho = Math.sqrt(C - 2 * N * Math.sin(r_(lat))) / N, th = N * r_(lon - pr.lam0); return [(rho * Math.sin(th) - pr.x0) * pr.k, (pr.y0 - (RHO0 - rho * Math.cos(th))) * pr.k]; };
  const geo = {
    raw, regions, inLand, adj, proj,
    landD: regions.map((r) => r.d).join(''),
    outlineD: arcs.filter((_, i) => refs[i] === 1).map(P).join(''),
    bordersD: arcs.filter((_, i) => refs[i] > 1).map(P).join(''),
    coastD: arcs.filter((a, i) => refs[i] === 1 && a.some((p) => p[1] > 1500)).map(P).join(''),
    rivers: rivRaw.map((rv) => ({ name: rv.name, big: !!rv.big, d: rv.lines.map((l) => P(conv(l))).join('') })),
  };
  memoGeo.set(key, geo);
  return geo;
}

/** Чи вільне місце від шляхів і міст — щоб декор не ліз під плашки (сітка, бо перевірок тисячі). */
function clearance(ctx) {
  if (ctx._clear) return ctx._clear;
  const C = 80, grid = new Map(), add = (x, y) => { const k = (x / C | 0) + ',' + (y / C | 0); (grid.get(k) || grid.set(k, []).get(k)).push(x, y); };
  for (const R of ctx.routes) for (let i = 0; i < R.curve.pts.length; i += 2) add(R.curve.pts[i][0], R.curve.pts[i][1]);
  const near = (x, y, d) => {
    for (const c of ctx.cities) if (Math.hypot(c.x - x, c.y - y) < d + c.r + 6) return true;
    const dd = d + 24, x0 = (x - dd) / C | 0, x1 = (x + dd) / C | 0, y0 = (y - dd) / C | 0, y1 = (y + dd) / C | 0;
    for (let i = x0; i <= x1; i++) for (let j = y0; j <= y1; j++) { const l = grid.get(i + ',' + j); if (l) for (let n = 0; n < l.length; n += 2) if (Math.abs(l[n] - x) < dd && Math.abs(l[n + 1] - y) < dd) return true; }
    return false;
  };
  return (ctx._clear = near);
}

// ── фактури вантажів: третя ознака поруч із кольором і значком (дальтоніки) ──
const TEX = {
  salt: { s: 15, rot: 45, d: 'M0 4H15', w: 2.2 },
  fish: { s: 22, d: 'M0 8q5.500-6 11 0t11 0M0 19q5.500-6 11 0t11 0', w: 1.8 },
  grain: { s: 12, rot: 90, d: 'M0 3H12M0 9H12', w: 1.3 },
  tar: { s: 13, d: 'M3 3h.1M9.500 9.500h.1', w: 3 },
  honey: { s: 24, d: 'M0 7L6 3.500 12 7V14L6 17.500 0 14ZM12 7L18 3.500 24 7M12 14L18 17.500 24 14', w: 1.4 },
  wool: { s: 18, d: 'M4 6a3 3 0 1 1 3 3M13 15a3 3 0 1 1 3 3', w: 1.5 },
  hides: { s: 15, rot: 45, d: 'M0 7.500H15M7.500 0V15', w: 1.5 },
  linen: { s: 9, d: 'M0 2.200H9M2.200 0V9M0 6.700H9M6.700 0V9', w: 1 },
};
function texDefs(p, fills, inks, a = .24) {
  let s = '';
  for (const k in TEX) {
    const t = TEX[k];
    s += `<pattern id="${p}pt-${k}" width="${t.s}" height="${t.s}" patternUnits="userSpaceOnUse"${t.rot ? ` patternTransform="rotate(${t.rot})"` : ''}><rect width="${t.s}" height="${t.s}" fill="${fills[k]}"/><path d="${t.d}" fill="none" stroke="${inks[k]}" stroke-opacity="${a}" stroke-width="${t.w}" stroke-linecap="round"/></pattern>`;
  }
  return s;
}
const SEAT_MARK = [
  (r) => `<circle r="${f1(r * .85)}"/>`,
  (r) => `<path d="M0 ${f1(-r)}L${f1(r)} ${f1(r * .78)}H${f1(-r)}Z"/>`,
  (r) => `<rect x="${f1(-r * .8)}" y="${f1(-r * .8)}" width="${f1(r * 1.6)}" height="${f1(r * 1.6)}"/>`,
  (r) => `<path d="M0 ${f1(-r * 1.1)}L${f1(r * 1.1)} 0L0 ${f1(r * 1.1)}L${f1(-r * 1.1)} 0Z"/>`,
  (r) => { let d = ''; for (let i = 0; i < 10; i++) { const R = (i % 2 ? .46 : 1.18) * r, A = -Math.PI / 2 + i * Math.PI / 5; d += (i ? 'L' : 'M') + f1(R * Math.cos(A)) + ' ' + f1(R * Math.sin(A)); } return `<path d="${d}Z"/>`; },
];
const upright = (a) => { let d = a * 180 / Math.PI; if (d > 90) d -= 180; if (d < -90) d += 180; return d; };
const SERIF = `Georgia, 'Noto Serif', 'Times New Roman', serif`, SANS = `Onest, system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif`;
const BREAK = { "Кам'янець-Подільський": ["Кам'янець-", 'Подільський'], 'Івано-Франківськ': ['Івано-', 'Франківськ'] };
const nice = (t) => t.replace(/'/g, '’');
const P_TOWN = {
  kyiv: `<g transform="translate(0 16)"><path d="M-42 1Q0 6 42 1" fill="none" stroke="#2b1a0d" stroke-width="2.2" stroke-linecap="round"/><path d="M-39 0V-10H-21V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-41.5 -10L-30 -16.75L-18.5 -10Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><path d="M22 0V-10H40V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M19.5 -10L31 -16.75L42.5 -10Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><g transform="translate(-13 0) scale(0.85)"><path d="M-12 0V-22H12V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-7-22V-34H7V-22Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-8.5-34C-9.5-42-2.5-44 0-50 2.5-44 9.5-42 8.5-34Z" fill="#e2b23a" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><path d="M0-50V-60M-3.5-56H3.5" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-4 0V-9A4 4 0 0 1 4-9V0Z" fill="#2b1a0d"/><path d="M-8-15V-19M8-15V-19M0-26V-31" stroke="#2b1a0d" stroke-width="1.8"/></g><g transform="translate(14 0) scale(0.85)"><path d="M-12 0V-22H12V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-7-22V-34H7V-22Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-8.5-34C-9.5-42-2.5-44 0-50 2.5-44 9.5-42 8.5-34Z" fill="#e2b23a" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><path d="M0-50V-60M-3.5-56H3.5" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-4 0V-9A4 4 0 0 1 4-9V0Z" fill="#2b1a0d"/><path d="M-8-15V-19M8-15V-19M0-26V-31" stroke="#2b1a0d" stroke-width="1.8"/></g><g transform="translate(0 2) scale(1.12)"><path d="M-12 0V-22H12V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-7-22V-34H7V-22Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-8.5-34C-9.5-42-2.5-44 0-50 2.5-44 9.5-42 8.5-34Z" fill="#e2b23a" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><path d="M0-50V-60M-3.5-56H3.5" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-4 0V-9A4 4 0 0 1 4-9V0Z" fill="#2b1a0d"/><path d="M-8-15V-19M8-15V-19M0-26V-31" stroke="#2b1a0d" stroke-width="1.8"/></g></g>`,
  major: `<g transform="translate(0 14)"><path d="M-32 1Q0 6 32 1" fill="none" stroke="#2b1a0d" stroke-width="2.2" stroke-linecap="round"/><path d="M-29 0V-10H-13V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-31.5 -10L-21 -16L-10.5 -10Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><path d="M13 0V-9H29V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M10.5 -9L21 -15L31.5 -9Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><g transform="translate(0 1) scale(0.95)"><path d="M-12 0V-22H12V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-7-22V-34H7V-22Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-8.5-34C-9.5-42-2.5-44 0-50 2.5-44 9.5-42 8.5-34Z" fill="#59703f" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><path d="M0-50V-60M-3.5-56H3.5" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-4 0V-9A4 4 0 0 1 4-9V0Z" fill="#2b1a0d"/><path d="M-8-15V-19M8-15V-19M0-26V-31" stroke="#2b1a0d" stroke-width="1.8"/></g></g>`,
  capital: `<g transform="translate(0 13)"><path d="M-26 1Q0 6 26 1" fill="none" stroke="#2b1a0d" stroke-width="2.2" stroke-linecap="round"/><path d="M-22.5 0V-10H-7.5V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-25 -10L-15 -15.625L-5 -10Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><path d="M9 0V-8H23V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M6.5 -8L16 -13.25L25.5 -8Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><g transform="translate(1 1) scale(0.75)"><path d="M-12 0V-22H12V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-7-22V-34H7V-22Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-8.5-34C-9.5-42-2.5-44 0-50 2.5-44 9.5-42 8.5-34Z" fill="#59703f" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><path d="M0-50V-60M-3.5-56H3.5" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-4 0V-9A4 4 0 0 1 4-9V0Z" fill="#2b1a0d"/><path d="M-8-15V-19M8-15V-19M0-26V-31" stroke="#2b1a0d" stroke-width="1.8"/></g></g>`,
  castle: `<g transform="translate(0 13)"><path d="M-30 1Q0 6 30 1" fill="none" stroke="#2b1a0d" stroke-width="2.2" stroke-linecap="round"/><path d="M-20 0V-16H20V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-14 0V-4H14V0" fill="none" stroke="#2b1a0d" stroke-width="1.4"/><g transform="translate(-17 0) scale(0.8)"><path d="M-9 0V-34H9V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-12-34V-42H-7V-38H-2.5V-42H2.5V-38H7V-42H12V-34Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2"/><path d="M-3 0V-8A3 3 0 0 1 3-8V0Z" fill="#2b1a0d"/><path d="M0-22V-28" stroke="#2b1a0d" stroke-width="2.4"/></g><g transform="translate(17 0) scale(0.8)"><path d="M-9 0V-34H9V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-12-34V-42H-7V-38H-2.5V-42H2.5V-38H7V-42H12V-34Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2"/><path d="M-3 0V-8A3 3 0 0 1 3-8V0Z" fill="#2b1a0d"/><path d="M0-22V-28" stroke="#2b1a0d" stroke-width="2.4"/></g><g transform="translate(0 0) scale(1.05)"><path d="M-9 0V-34H9V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-12-34V-42H-7V-38H-2.5V-42H2.5V-38H7V-42H12V-34Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2"/><path d="M-3 0V-8A3 3 0 0 1 3-8V0Z" fill="#2b1a0d"/><path d="M0-22V-28" stroke="#2b1a0d" stroke-width="2.4"/></g></g>`,
  village: `<g transform="translate(0 11)"><path d="M-20 1Q0 6 20 1" fill="none" stroke="#2b1a0d" stroke-width="2.2" stroke-linecap="round"/><path d="M-15 0V-9H-1V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-17.5 -9L-8 -14.25L1.5 -9Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><path d="M2.5 1V-7H15.5V1Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M0 -7L9 -11.875L18 -7Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/></g>`,
  port: `<g transform="translate(0 13)"><path d="M-30 1Q0 6 30 1" fill="none" stroke="#2b1a0d" stroke-width="2.2" stroke-linecap="round"/><path d="M-23 0V-9H-7V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-25.5 -9L-15 -15L-4.5 -9Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2.2" stroke-linejoin="round"/><path d="M6 0 9-30H17L20 0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M8-30H18V-37H8Z" fill="#e2b23a" stroke="#2b1a0d" stroke-width="2"/><path d="M6-37H20L13-45Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2" stroke-linejoin="round"/><path d="M8-18H18M7-8H19" stroke="#b3261e" stroke-width="3"/></g>`,
  pot: `<g transform="translate(0 14)"><path d="M-18 1Q0 6 18 1" fill="none" stroke="#2b1a0d" stroke-width="2.2" stroke-linecap="round"/><path d="M-5-33H5V-29C13-25 16-18 16-12 16-4 9 0 0 0-9 0-16-4-16-12-16-18-13-25-5-29Z" fill="#c5763a" stroke="#2b1a0d" stroke-width="2.3"/><rect x="-7" y="-37" width="14" height="5" rx="2" fill="#d4884a" stroke="#2b1a0d" stroke-width="2"/><path d="M12-24C24-26 24-10 14-8" fill="none" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-14-17H14M-14-13H14" stroke="#f4c542" stroke-width="2"/></g>`,
  gate: `<g transform="translate(0 12)"><path d="M-30 1Q0 6 30 1" fill="none" stroke="#2b1a0d" stroke-width="2.2" stroke-linecap="round"/><path d="M-24 0V-14H24V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-7 0V-10A7 7 0 0 1 7-10V0Z" fill="#2b1a0d"/><g transform="translate(0 -8) scale(0.7)"><path d="M-9 0V-34H9V0Z" fill="#f4e9cb" stroke="#2b1a0d" stroke-width="2.2"/><path d="M-12-34V-42H-7V-38H-2.5V-42H2.5V-38H7V-42H12V-34Z" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="2"/><path d="M-3 0V-8A3 3 0 0 1 3-8V0Z" fill="#2b1a0d"/><path d="M0-22V-28" stroke="#2b1a0d" stroke-width="2.4"/></g><path d="M-24-14V-19H-19V-14M-13-14V-19H-8V-14M8-14V-19H13V-14M19-14V-19H24V-14" fill="#6b4a2b" stroke="#2b1a0d" stroke-width="1.6"/></g>`,
};
const P_SERPENT = `<g fill="none" stroke-linejoin="round" stroke-linecap="round"><g stroke="#2c2a22" stroke-width="2.2"><path d="M58 104C50 72 36 62 46 40 54 22 82 16 98 28L128 36C134 38 134 46 126 47L120 48 128 56C118 58 104 54 96 50 86 52 84 60 90 76 94 88 98 96 100 104Z" fill="#a9b88e"/><path d="M47 38 34 32 42 46 27 48 42 58 28 66 47 70 37 82 54 84" fill="#7e3b24"/><path d="M128 37 118 40 122 45 112 44M98 50 106 49 104 43" fill="#f7efd9" stroke-width="1.3"/><path d="M130 104C132 64 184 64 188 104Z" fill="#a9b88e"/><path d="M198 104C200 76 234 76 238 104Z" fill="#a9b88e"/><path d="M250 104C254 94 264 84 280 66 276 80 288 76 302 70 298 88 284 96 276 104Z" fill="#7e3b24"/><path d="M138 72 132 58 146 63 150 50 160 62 172 50 174 64 186 58 182 74" fill="#7e3b24" stroke-width="1.8"/><path d="M204 84 200 74 210 78 216 68 222 77 232 72 232 86" fill="#7e3b24" stroke-width="1.8"/></g><circle cx="84" cy="33" r="6.5" fill="#f7efd9" stroke="#2c2a22" stroke-width="2"/><circle cx="85.5" cy="33" r="2.6" fill="#111"/><path d="M74 26Q84 19 94 28" stroke="#2c2a22" stroke-width="3"/><g stroke="#48553d" stroke-width="1.3"><path d="M58 52q6 4 12 0M56 62q6 4 12 0M60 72q6 4 12 0M64 82q6 4 12 0M68 92q6 4 12 0"/><path d="M146 92q4-8 12-8M158 96q4-8 12-8M170 98q2-5 8-6M210 94q4-6 8-6M222 98q2-4 6-5"/></g><path d="M132 44q12 4 22-5q-4 9-16 12" stroke="#b3261e" stroke-width="2"/><g stroke="#2c2a22" stroke-width="1.7"><path d="M20 107q10-5 20 0t20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0"/><path d="M60 116q10-4 20 0t20 0 20 0 20 0 20 0 20 0 20 0 20 0"/><path d="M124 125q8-3 16 0t16 0 16 0 16 0"/></g></g>`;
const P_WHALE = `<g fill="none" stroke-linejoin="round" stroke-linecap="round"><g stroke="#2c2a22" stroke-width="2"><path d="M60 22C52 8 40 8 34 16M60 22C60 6 72 0 84 6M60 22C64 12 66 8 60 0" stroke-width="1.8"/></g><path d="M12 80C22 40 96 18 150 42 170 52 190 54 208 34 206 54 210 66 226 82 204 76 186 78 164 88 110 118 32 116 12 80Z" fill="#9db3a4" stroke="#2c2a22" stroke-width="2.3"/><path d="M12 80C28 82 42 76 56 62 54 82 40 98 26 99" fill="#f7efd9" stroke="#2c2a22" stroke-width="2"/><path d="M18 82 21 90 26 80 30 91 35 76 40 86 44 70" fill="#fff" stroke="#2c2a22" stroke-width="1.3"/><circle cx="58" cy="50" r="7.5" fill="#f7efd9" stroke="#2c2a22" stroke-width="2"/><circle cx="56" cy="51" r="3" fill="#111"/><path d="M46 44Q56 36 68 44" stroke="#2c2a22" stroke-width="3"/><path d="M84 36 88 22 98 32 106 16 116 32 128 22 130 40" fill="#7e3b24" stroke="#2c2a22" stroke-width="1.8"/><path d="M96 92C102 112 118 118 132 114 124 106 122 96 124 86" fill="#7e3b24" stroke="#2c2a22" stroke-width="1.9"/><g stroke="#3f5145" stroke-width="1.2"><path d="M80 52q6 5 12 0M100 50q6 5 12 0M120 54q6 5 12 0M140 58q6 5 12 0M90 66q6 5 12 0M110 66q6 5 12 0M130 70q6 5 12 0M154 72q6 5 12 0M70 70q6 5 12 0"/></g><g stroke="#2c2a22" stroke-width="1.7"><path d="M4 110q10-5 20 0t20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0 20 0"/><path d="M40 120q10-4 20 0t20 0 20 0 20 0 20 0"/></g></g>`;
const P_CHAIKA = `<g fill="none" stroke-linejoin="round" stroke-linecap="round" stroke="#2c2a22" stroke-width="2.2"><path d="M4 42C20 52 90 52 110 40L102 56C76 62 32 62 14 56Z" fill="#7a5730"/><path d="M56 46V4" stroke-width="2.8"/><path d="M34 12C50 8 62 8 80 12 74 22 74 30 78 38 62 34 50 34 36 38 40 30 40 22 34 12Z" fill="#f4e9cb"/><path d="M48 20H66M46 28H68" stroke="#b3261e"/><path d="M56 4 70 8 56 11Z" fill="#b3261e" stroke-width="1.3"/><path d="M24 50 16 66M42 54 36 68M72 54 76 68M90 50 96 66" stroke-width="1.7"/><path d="M0 68q8-4 16 0t16 0 16 0 16 0 16 0 16 0 16 0" stroke-width="1.6"/></g>`;
const P_MARK = {
  salt: `<path d="M0-10 7.5 0 0 10-7.5 0Z" fill="#fff7e6"/>`,
  fish: `<path d="M-10.5 0C-6-7.5 3-7 6.5-.8L11-6V6L6.5 .8C3 7-6 7.5-10.5 0Z" fill="#fff7e6"/>`,
  grain: `<path d="M0 10.5V-9M0-3.5-5.5-8.5M0-3.5 5.5-8.5M0 2.5-5.5-2.5M0 2.5 5.5-2.5M0 8.5-5.5 3.5M0 8.5 5.5 3.5" fill="none" stroke="#fff7e6" stroke-width="2.7" stroke-linecap="round"/>`,
  tar: `<path d="M0-11C3.5-5 7.5-1.5 7.5 3.2A7.5 7.5 0 0 1-7.5 3.2C-7.5-1.5-3.5-5 0-11Z" fill="#f7efd9"/>`,
  honey: `<path d="M0-9.5 8.4-4.7V4.7L0 9.5-8.4 4.7V-4.7Z" fill="#2b1a0d"/>`,
  wool: `<g fill="#2b1a0d"><circle cx="-4.6" cy="2.6" r="4.9"/><circle cx="4.6" cy="2.6" r="4.9"/><circle cx="0" cy="-4" r="5.4"/></g>`,
  hides: `<path d="M-10.5-8.5Q0-3 10.5-8.5V8.5Q0 3-10.5 8.5Z" fill="#fff7e6"/>`,
  linen: `<path d="M-7.5-7.5 7.5 7.5M7.5-7.5-7.5 7.5" fill="none" stroke="#2b1a0d" stroke-width="3.8" stroke-linecap="round"/>`,
};
const B_GLYPH = {
  salt: `<g fill="#fff"><path d="M-8 6.5 L-3.4 -1 L0 -7.5 L3.4 -1 L8 6.5 Z"/><path d="M-3.2 3.2 l1.6 -1.6 1.6 1.6 -1.6 1.6z M1.8 1.2 l1.2 -1.2 1.2 1.2 -1.2 1.2z" fill="#d8352b"/></g>`,
  fish: `<g fill="#fff"><path d="M-8.4 0 C-5 -5.6 2 -5.6 4.6 -.9 L9 -4.6 L9 4.6 L4.6 .9 C2 5.6 -5 5.6 -8.4 0Z"/><circle cx="-4.6" cy="-1.1" r="1.1" fill="#1f73d1"/></g>`,
  grain: `<g fill="#fff3b0"><path d="M0 -9 C1.9 -7.5 1.9 -5.5 0 -4 C-1.9 -5.5 -1.9 -7.5 0 -9Z M-4.6 -5.2 C-2.6 -5 -1.2 -3.4 -.9 -1.4 C-2.9 -1.6 -4.3 -3.2 -4.6 -5.2Z M4.6 -5.2 C2.6 -5 1.2 -3.4 .9 -1.4 C2.9 -1.6 4.3 -3.2 4.6 -5.2Z M-4.6 -.4 C-2.6 -.2 -1.2 1.4 -.9 3.4 C-2.9 3.2 -4.3 1.6 -4.6 -.4Z M4.6 -.4 C2.6 -.2 1.2 1.4 .9 3.4 C2.9 3.2 4.3 1.6 4.6 -.4Z"/><rect x="-.7" y="-4" width="1.4" height="13.5" rx=".7"/></g>`,
  tar: `<g fill="#fff"><path d="M0 -9 C4 -3.6 6.6 -.6 6.6 3 A6.6 6.6 0 0 1 -6.6 3 C-6.6 -.6 -4 -3.6 0 -9Z"/></g>`,
  honey: `<g fill="#3a2500"><path d="M0 -8.6 L7.5 -4.3 V4.3 L0 8.6 L-7.5 4.3 V-4.3Z"/><path d="M0 -4.2 L3.7 -2.1 V2.1 L0 4.2 L-3.7 2.1 V-2.1Z" fill="#fbcb1e"/></g>`,
  wool: `<g fill="#2a160b"><path d="M-6 6 C-9.4 6 -9.6 1 -6.8 .2 C-8 -3.4 -3.6 -5.4 -1.8 -3.4 C-.8 -7 4.6 -6.4 4.8 -2.6 C8.6 -2.8 9.8 3 6.6 4.6 C6.2 7 2.6 7.4 1.4 5.6 C-.2 7.4 -4.2 7.6 -6 6Z"/></g>`,
  hides: `<g fill="#3a1600"><path d="M-3.4 -8.4 L-.6 -6.4 L2.6 -8.6 L3.4 -4.8 L7.8 -3.2 L5.2 0 L8 3.4 L3.8 4.6 L3 8.6 L-.2 6.2 L-3.6 8.4 L-4 4.6 L-8 3.2 L-5.4 0 L-8 -3.4 L-3.8 -4.8Z"/></g>`,
  linen: `<g fill="#3f0a26"><path d="M-6.8 -6.8 L-3.4 -6.8 L0 -3.4 L3.4 -6.8 L6.8 -6.8 L6.8 -3.4 L3.4 0 L6.8 3.4 L6.8 6.8 L3.4 6.8 L0 3.4 L-3.4 6.8 L-6.8 6.8 L-6.8 3.4 L-3.4 0 L-6.8 -3.4Z"/></g>`,
};
const K_GLYPH = {
  salt: `<path d="M0-8.5L8.5 0 0 8.5-8.5 0Z" fill="#fff"/>`,
  fish: `<path d="M-10 0C-5-7 3-7 6-1L10-5V5L6 1C3 7-5 7-10 0Z" fill="#fff"/>`,
  grain: `<path d="M-7 5L0-2 7 5M-7-1L0-8 7-1" fill="none" stroke="#fff" stroke-width="3.4" stroke-linecap="round" stroke-linejoin="round"/>`,
  tar: `<path d="M0-9.5C4-3.5 6.5-1 6.5 2.5A6.5 6.5 0 0 1-6.5 2.5C-6.5-1-4-3.5 0-9.5Z" fill="#f6bf26"/>`,
  honey: `<path d="M0-9L8-4.5V4.5L0 9-8 4.5V-4.5Z" fill="none" stroke="#1b0f06" stroke-width="3.4" stroke-linejoin="round"/>`,
  wool: `<path d="M-5 1a5 5 0 1 1 5 5" fill="none" stroke="#1b0f06" stroke-width="3.6" stroke-linecap="round"/>`,
  hides: `<path d="M-7-7L-3-4H3L7-7 5 0 7 7 3 4H-3L-7 7-5 0Z" fill="#1b0f06"/>`,
  linen: `<path d="M-4-8V8M4-8V8" stroke="#1b0f06" stroke-width="3.6" stroke-linecap="round"/>`,
};
const K_OXCART = `<g transform="translate(0 0) scale(1)"><g fill="#fbf1da"><path d="M104 22C120 14 150 14 165 22C172 26 176 32 176 40C176 48 172 52 168 54L170 78H160L156 56C144 60 128 60 118 57L114 78H104L104 52C100 48 100 30 104 22Z"/><path d="M170 26C178 22 188 24 196 30C200 34 201 42 198 46C192 50 184 48 176 44Z"/></g><path d="M188 26C196 24 204 16 204 6C198 12 192 15 182 17ZM178 24C176 15 178 8 186 2C186 10 188 15 192 20Z" fill="#f6bf26"/><circle cx="189" cy="33" r="2.2" fill="#0a0812"/><g fill="#d9301f"><path d="M125 26l8 8-8 8-8-8z"/><path d="M146 26l8 8-8 8-8-8z"/></g><g fill="#f6bf26"><path d="M136 41l5 5-5 5-5-5z"/></g><path d="M170 20V52" stroke="#f6bf26" stroke-width="5" stroke-linecap="round"/><path d="M92 50L170 34" stroke="#fbf1da" stroke-width="4"/><g stroke="#0a0812" stroke-width="3" stroke-linejoin="round"><path d="M4 40C4 2 90 2 90 40Z" fill="#d9301f"/><path d="M0 40H94L88 54H6Z" fill="#f6bf26"/></g><path d="M24 12V40M47 7V40M70 12V40" stroke="#fbf1da" stroke-width="3"/><g><circle cx="24" cy="62" r="17" fill="#fbf1da" stroke="#0a0812" stroke-width="3"/><circle cx="70" cy="62" r="17" fill="#fbf1da" stroke="#0a0812" stroke-width="3"/><path d="M24 47V77M9 62H39M13 51L35 73M35 51L13 73M70 47V77M55 62H85M59 51L81 73M81 51L59 73" stroke="#0a0812" stroke-width="2.4"/><circle cx="24" cy="62" r="5" fill="#d9301f"/><circle cx="70" cy="62" r="5" fill="#d9301f"/></g></g>`;


// ═════════════════════════════ «Стара мапа» ═════════════════════════════
const INK = '#2b1a0d', SEPIA = '#5a3a1e';
const P_SOFT = { salt: '#c8473a', fish: '#3c79b8', grain: '#4a9455', tar: '#3a312a', honey: '#ecc04a', wool: '#f8f3e4', hides: '#d97a30', linen: '#e089b0', grey: '#f8efd6' };
const P_ON = { salt: '#fff7e6', fish: '#fff7e6', grain: '#fff7e6', tar: '#f7efd9', honey: '#2b1a0d', wool: '#2b1a0d', hides: '#fff7e6', linen: '#2b1a0d', grey: '#5a3a1e' };
const BIG = new Set(['Харків', 'Одеса', 'Львів', 'Дніпро']);
const CAP = new Set(['Чернігів', 'Суми', 'Полтава', 'Луганськ', 'Донецьк', 'Запоріжжя', 'Черкаси', 'Кропивницький', 'Миколаїв', 'Херсон', 'Сімферополь', 'Вінниця', 'Житомир', 'Рівне', 'Хмельницький', 'Чернівці', 'Івано-Франківськ', 'Тернопіль', 'Луцьк', 'Ужгород', "Кам'янець-Подільський", 'Ізмаїл', 'Севастополь', 'Маріуполь', 'Перекоп', 'Опішня']);
const TIER = (n) => (n === 'Київ' ? 0 : BIG.has(n) ? 1 : CAP.has(n) ? 2 : 3);
const TOWN_KIND = { 'Київ': 'kyiv', 'Харків': 'major', 'Львів': 'castle', 'Одеса': 'port', 'Дніпро': 'major', "Кам'янець-Подільський": 'castle', 'Ізмаїл': 'castle', 'Луцьк': 'castle', 'Ужгород': 'castle', 'Перекоп': 'gate', 'Опішня': 'pot', 'Севастополь': 'port', 'Маріуполь': 'port', 'Бердянськ': 'port', 'Миколаїв': 'port', 'Херсон': 'port' };
const kindOf = (n) => TOWN_KIND[n] || (CAP.has(n) ? 'capital' : 'village');

function pPeak(k = 1) { const w = 34 * k, h = 42 * k; return `<path d="M${-w} 0C${-w * .6} ${-h * .3} ${-w * .3} ${-h * .8} 0 ${-h}C${w * .25} ${-h * .8} ${w * .55} ${-h * .3} ${w} 0" fill="#e8d7ac" stroke="#3a2412" stroke-width="2.8" stroke-linejoin="round"/><path d="M0 ${-h}C${w * .25} ${-h * .8} ${w * .55} ${-h * .3} ${w} 0H${w * .1}C${w * .15} ${-h * .4} ${w * .06} ${-h * .7} 0 ${-h}Z" fill="#8f6d3f" fill-opacity=".5"/><path d="M${w * .2} ${-h * .55}L${w * .05} -3M${w * .38} ${-h * .32}L${w * .25} -2M${w * .56} ${-h * .18}L${w * .45} -2" stroke="#3a2412" stroke-width="1.7"/>`; }
const pHill = `<path d="M-34 0Q0 -34 34 0" fill="#e6d4a6" stroke="#4a3018" stroke-width="2.5"/><path d="M8 -14l-5 12M18 -8l-4 8" stroke="#4a3018" stroke-width="1.7"/>`;
const pKurgan = `<path d="M-30 0Q0 -22 30 0" fill="#e6d4a6" stroke="#4a3018" stroke-width="2.3"/><path d="M-3.5 -13V-27A3.5 3.5 0 0 1 3.5 -27V-13Z" fill="#7a5a33" stroke="#2b1a0d" stroke-width="1.7"/><path d="M10 -7l-4 6M18 -4l-3 4" stroke="#4a3018" stroke-width="1.5"/>`;
const TREE_C = 'M-1.5 0V-5H-9L-4 -13H-7.5L-3 -21H-5L0 -30L5 -21H3L7.5 -13H4L9 -5H1.5V0Z';
const TREE_D = 'M-1.8 0V-7C-11 -7 -13 -16 -8 -20C-9 -27 2 -30 6 -24C13 -23 13 -11 7 -8.5L1.8 -7V0Z';

function pCompass(r) {
  const b = new Bucket();
  let ticks = '', o = `<circle r="${r}" fill="#efe2bd" fill-opacity=".55" stroke="${INK}" stroke-width="2.6"/><circle r="${r * .92}" fill="none" stroke="${INK}" stroke-width="1.1"/>`;
  for (let i = 0; i < 72; i++) { const A = i * 5 * Math.PI / 180, a = r * .92, e = i % 9 === 0 ? r : r * .965; ticks += `M${f1(a * Math.sin(A))} ${f1(-a * Math.cos(A))}L${f1(e * Math.sin(A))} ${f1(-e * Math.cos(A))}`; }
  o += `<path d="${ticks}" stroke="${INK}" stroke-width="1.2" fill="none"/>`;
  const pt = (deg, R, w, dark, light) => {
    const A = deg * Math.PI / 180, ca = Math.cos(A), sa = Math.sin(A), t = `${f1(R * sa)} ${f1(-R * ca)}`;
    b.add(`fill="${dark}" stroke="${INK}" stroke-width="1.3"`, `M0 0L${f1(-w * ca)} ${f1(-w * sa)}L${t}Z`);
    b.add(`fill="${light}" stroke="${INK}" stroke-width="1.3"`, `M0 0L${f1(w * ca)} ${f1(w * sa)}L${t}Z`);
  };
  for (let i = 0; i < 16; i++) if (i % 2) pt(i * 22.5, r * .56, r * .045, '#e6d6ad', '#5a3a1e');
  for (let i = 0; i < 8; i++) if (i % 2) pt(i * 45, r * .74, r * .085, '#2c7a3a', '#efe2bd');
  for (let i = 0; i < 4; i++) pt(i * 90, r, r * .12, '#b3261e', '#f7efd9');
  o += b.svg() + `<circle r="${r * .1}" fill="#e2b23a" stroke="${INK}" stroke-width="1.8"/><circle r="${r * .035}" fill="${INK}"/>`;
  const lab = (t, a) => { const A = a * Math.PI / 180, R = r * 1.2; return `<text x="${f1(R * Math.sin(A))}" y="${f1(-R * Math.cos(A) + r * .08)}" text-anchor="middle" font-family="${SERIF}" font-size="${f1(r * .24)}" fill="${INK}">${t}</text>`; };
  return o + lab('Пн', 0) + lab('Сх', 90) + lab('Пд', 180) + lab('Зх', 270);
}

const parchment = {
  bg: '#d9c493', slotW: 31, selW: 37,
  geo: { smooth: true, wobble: true },
  base(ctx) {
    const g = ctx.geo, p = ctx.p, near = clearance(ctx), r = rng(2024);
    const romania = [[42.4, 27.7], [43.2, 27.95], [43.8, 28.6], [44.17, 28.66], [44.68, 29.0], [44.89, 29.6], [45.16, 29.75], [45.42, 29.72]].map(([a, b]) => g.proj(a, b));
    const russia = [[47.1, 38.2], [47.2, 38.9], [47.15, 39.3], [46.95, 39.0], [46.7, 38.3], [46.35, 37.9], [46.05, 38.15], [45.7, 37.7], [45.35, 37.4], [45.36, 36.95], [45.2, 36.65], [44.95, 37.25], [44.65, 37.85], [44.1, 39.05], [43.6, 39.7]].map(([a, b]) => g.proj(a, b));
    const inland = [[45.42, 29.72], [45.9, 30.2], [46.6, 30.6], [47.0, 31.3], [47.0, 32.4], [46.8, 33.4], [46.7, 34.6], [47.1, 35.6], [47.4, 36.6], [47.4, 37.7], [47.1, 38.2]].map(([a, b]) => g.proj(a, b));
    const rS = chaikin(chaikin(romania)), uS = chaikin(chaikin(russia));
    const seaD = P([...rS, ...inland, ...uS, [6500, 4400], [-1800, 4400]]) + 'Z', foreignD = P(rS) + P(uS);
    // чи море — точка під береговим багатокутником
    const seaPoly = [...rS, ...inland, ...uS, [6500, 4400], [-1800, 4400]];
    const inSea = (x, y) => { let c = false; for (let i = 0, j = seaPoly.length - 1; i < seaPoly.length; j = i++) { const a = seaPoly[i], b = seaPoly[j]; if ((a[1] > y) !== (b[1] > y) && x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]) c = !c; } return c; };

    const fills = {}, inks = {}; for (const k in TEX) { fills[k] = P_SOFT[k]; inks[k] = P_ON[k]; }
    let defs = `<pattern id="${p}hatch" width="20" height="20" patternUnits="userSpaceOnUse" patternTransform="rotate(38)"><path d="M0 4H20" stroke="#6d4f2a" stroke-width="1.9" stroke-opacity=".3"/></pattern>
      <pattern id="${p}hatch2" width="13" height="13" patternUnits="userSpaceOnUse" patternTransform="rotate(-48)"><path d="M0 3H13" stroke="#6d4f2a" stroke-width="2" stroke-opacity=".36"/></pattern>
      <clipPath id="${p}land"><path d="${g.landD}"/></clipPath><clipPath id="${p}sea"><path d="${seaD}" clip-rule="evenodd"/></clipPath>
      <g id="${p}pk">${pPeak()}</g><g id="${p}hl">${pHill}</g><g id="${p}kr">${pKurgan}</g>` + texDefs(p, fills, inks);
    for (const k in P_MARK) defs += `<g id="${p}ic-${k}">${P_MARK[k]}</g>`;
    defs += `<g id="${p}ic-grey"><circle r="5" fill="none" stroke="${SEPIA}" stroke-width="2.4"/></g>`;
    for (const k in P_TOWN) defs += `<g id="${p}t-${k}">${P_TOWN[k]}</g>`;
    ctx.pal.seat.forEach((col, i) => {
      const w = 30;
      defs += `<g id="${p}cart-${i}"><path d="M${w - 1} 12L${w + 12} 15" stroke="${INK}" stroke-width="3.6" stroke-linecap="round"/><path d="M${-w} 4C${-w - 3} -30 ${w + 3} -30 ${w} 4Z" fill="${col}" stroke="${INK}" stroke-width="3" stroke-linejoin="round"/><path d="M${-w * .55} -13V4M${w * .55} -13V4" stroke="#00000040" stroke-width="1.8"/><rect x="${-w - 3}" y="3" width="${2 * w + 6}" height="8.5" rx="1.5" fill="#6e4722" stroke="${INK}" stroke-width="2.4"/>`
        + [-w * .58, w * .58].map((x) => `<g transform="translate(${x} 15)"><circle r="8.5" fill="#e5d3a6" stroke="${INK}" stroke-width="2.5"/><path d="M-8.5 0H8.5M0 -8.5V8.5M-6 -6L6 6M6 -6L-6 6" stroke="${INK}" stroke-width="1.3"/><circle r="2.2" fill="${INK}"/></g>`).join('')
        + `<g transform="translate(0 -9)" fill="#fff" stroke="${INK}" stroke-width="1.7" stroke-linejoin="round">${SEAT_MARK[i](9.5)}</g></g>`;
    });

    // ── тло ──
    let s = `<rect x="-1800" y="-1200" width="8300" height="5600" fill="#d9c493"/><rect x="-1800" y="-1200" width="8300" height="5600" fill="url(#${p}hatch)"/>`;
    s += `<path d="${seaD}" fill="#bdcab2"/><g clip-path="url(#${p}sea)">`;
    for (const d of [130, 96, 68, 44, 25, 10]) {
      s += `<path d="${g.coastD}${foreignD}" fill="none" stroke="#54665a" stroke-opacity="${((.24 + (130 - d) / 130 * .42)).toFixed(2)}" stroke-width="${2 * d + 3.2}" stroke-linejoin="round"/><path d="${g.coastD}${foreignD}" fill="none" stroke="#bdcab2" stroke-width="${2 * d - 1}" stroke-linejoin="round"/>`;
    }
    const rr = rng(99); let ripples = '';
    for (let i = 0; i < 110; i++) { const x = 1400 + rr() * 2800, y = 2120 + rr() * 650; if (g.inLand(x, y) || !(x < 2400 || y > 2300 || x > 3000)) continue; ripples += `M${f1(x)} ${f1(y)}q9-7 18 0t18 0`; }
    s += `<path d="${ripples}" fill="none" stroke="#54665a" stroke-opacity=".5" stroke-width="2.2" stroke-linecap="round"/></g>`;
    let rhumbs = ''; for (let i = 0; i < 16; i++) { const A = i * 22.5 * Math.PI / 180; rhumbs += `M3610 2480L${f1(3610 + 4200 * Math.cos(A))} ${f1(2480 + 4200 * Math.sin(A))}`; }
    s += `<path d="${rhumbs}" fill="none" stroke="#7a3322" stroke-opacity=".2" stroke-width="2"/>`;
    s += `<path d="${foreignD}" fill="none" stroke="${INK}" stroke-width="3.4"/><path d="${g.outlineD}" fill="none" stroke="url(#${p}hatch2)" stroke-width="64"/>`;
    // ── суходіл ──
    s += `<path d="${g.landD}" fill="#f1e3bd"/>`;
    const tints = ['#c9a24a', '#8fa35a', '#c77e55', '#7f9a8a', '#b98c6a'], tb = new Bucket();
    g.regions.forEach((rg, i) => tb.add(`fill="${tints[(i * 7 + 2) % 5]}" fill-opacity=".17"`, rg.d));
    s += tb.svg();
    s += `<g clip-path="url(#${p}land)" fill="none" stroke="#b56a35" stroke-linejoin="round"><path d="${g.outlineD}" stroke-opacity=".11" stroke-width="86"/><path d="${g.outlineD}" stroke-opacity=".14" stroke-width="56"/><path d="${g.outlineD}" stroke-opacity=".18" stroke-width="30"/></g>`;
    s += `<path d="${g.bordersD}" fill="none" stroke="#6d4f2a" stroke-opacity=".5" stroke-width="2.4" stroke-dasharray="9 7 2 7"/>`;
    const small = g.rivers.filter((x) => !x.big).map((x) => x.d).join(''), big = g.rivers.filter((x) => x.big).map((x) => x.d).join('');
    s += `<g fill="none" stroke-linecap="round" stroke-linejoin="round"><path d="${small}" stroke="#3d6d7e" stroke-width="5.5" stroke-opacity=".75"/><path d="${big}" stroke="#2f5a68" stroke-width="15" stroke-opacity=".9"/><path d="${big}" stroke="#bdd0c6" stroke-width="8"/><path d="${g.outlineD}" stroke="${INK}" stroke-width="4.6"/></g>`;
    // ── рельєф: Карпати, Крим, кряж, Полісся, степ ──
    const arcC = [[110, 1040], [260, 1120], [420, 1240], [570, 1370], [720, 1490], [860, 1540]], pk = [];
    for (let i = 0; i < 260; i++) {
      const t = r() * (arcC.length - 1), a = Math.floor(t), q = t - a, x = arcC[a][0] + (arcC[a + 1][0] - arcC[a][0]) * q + (r() - .5) * 200, y = arcC[a][1] + (arcC[a + 1][1] - arcC[a][1]) * q + (r() - .5) * 200, k = .75 + r() * .55;
      if (!g.inLand(x, y) || near(x, y, 30 * k)) continue;
      if (pk.some((v) => Math.hypot(v[0] - x, (v[1] - y) * 1.5) < 52)) continue;
      pk.push([x, y, k]);
    }
    for (const v of [[2640, 2610, .72], [2720, 2590, .81], [2800, 2570, .85], [2880, 2540, .76], [2960, 2510, .72], [2770, 2525, .63], [2860, 2490, .63]]) if (!near(v[0], v[1], 26)) pk.push(v);
    pk.sort((a, b) => a[1] - b[1]);
    const trees = new Bucket(); let grass = '', hills = '';
    for (let i = 0; i < 620; i++) {
      const x = 500 + r() * 2300, y = 120 + r() * 560, kk = .85 + r() * .5, con = r() < .62;
      if (y > 280 + 330 * Math.sin((x / 2800) * 3.1) + 100 || (x > 2200 && y > 420) || !g.inLand(x, y) || near(x, y, 28)) continue;
      trees.add('fill="#3f2a14" fill-opacity=".62"', xf(con ? TREE_C : TREE_D, x, y, kk));
    }
    for (let i = 0; i < 480; i++) {
      const x = 1500 + r() * 2300, y = 1450 + r() * 760;
      if (!g.inLand(x, y) || near(x, y, 22)) continue;
      grass += `M${f1(x)} ${f1(y)}q-6-9-11-11M${f1(x)} ${f1(y)}q-1-11-1-16M${f1(x)} ${f1(y)}q5-9 10-12`;
    }
    for (const [x, y] of [[3560, 1330], [3640, 1400], [3700, 1330], [3590, 1500], [3300, 1560]]) if (!near(x, y, 26)) hills += `<use href="#${p}hl" transform="translate(${x} ${y})"/>`;
    for (const [x, y] of [[3120, 1680], [2750, 1990], [2120, 1520], [3220, 1100], [3030, 1980]]) if (!near(x, y, 32)) hills += `<use href="#${p}kr" transform="translate(${x} ${y})"/>`;
    s += trees.svg() + `<path d="${grass}" fill="none" stroke="#6b4f27" stroke-width="2.2" stroke-linecap="round" opacity=".55"/>` + hills;
    s += pk.map(([x, y, k]) => `<use href="#${p}pk" transform="translate(${f1(x)} ${f1(y)}) scale(${f1(k)})"/>`).join('');
    // ── написи ──
    const LAND = [['ПОЛІССЯ', 1140, 280, 2, 38], ['ГАЛИЧИНА', 330, 950, -28, 34], ['ПОДІЛЛЯ', 1210, 1270, 20, 34], ['ГЕТЬМАНЩИНА', 2130, 590, 15, 30], ['СЛОБОЖАНЩИНА', 2765, 735, 12, 22], ['ЗАПОРОЖЖЯ', 2600, 1720, 8, 30], ['ДИКЕ ПОЛЕ', 3090, 1610, 4, 30], ['БУДЖАК', 1520, 2240, -38, 30], ['ТАВРІЯ', 2590, 1965, 0, 26]];
    const FOREIGN = [['Литва', 1290, 118, -3, 52], ['Польща', 45, 395, -8, 52], ['Волощина', 1000, 1660, 18, 50], ['Московія', 3370, 640, 8, 54], ['Дон', 3880, 1660, 80, 46], ['Кубань', 3770, 2390, 18, 46]];
    const SEAS = [['МОРЕ ЧОРНЕ', 2860, 2672, -3, 58], ['Азовське', 3180, 2115, 8, 46], ['море', 3275, 2170, 8, 46]];
    const RIV = [['Дніпро', 2740, 1630, -12, 36], ['Дністер', 1450, 1640, 40, 34], ['Півд. Буг', 1660, 1460, 60, 32], ['Десна', 2230, 280, 18, 34], ['Сів. Донець', 3560, 1040, 18, 30], ['Прип’ять', 1010, 215, 6, 30], ['Дунай', 1160, 2510, 8, 34]];
    const jit = rng(5), txt = (t, x, y, rot, attrs) => `<text transform="translate(${x} ${y}) rotate(${rot})" font-family="${SERIF}" ${attrs}>${t}</text>`;
    s += LAND.map(([t, x, y, rot, z]) => txt(t, x, y, rot, `font-size="${z}" letter-spacing="${f1(z * .28)}" fill="#7a4e22" fill-opacity=".55"`)).join('');
    s += FOREIGN.map(([t, x, y, rot, z]) => txt(t, x, y, rot, `font-size="${z}" letter-spacing="${f1(z * .22)}" font-style="italic" fill="#4e3518" fill-opacity=".68"`)).join('');
    s += SEAS.map(([t, x, y, rot, z]) => txt(t, x, y, rot, `font-size="${z}" letter-spacing="${f1(z * .3)}" fill="#2f4238" fill-opacity=".72" rotate="${Array.from(t).map(() => f1((jit() - .5) * 3.2)).join(' ')}"`)).join('');
    s += RIV.map(([t, x, y, rot, z]) => txt(t, x, y, rot, `font-size="${z}" font-style="italic" fill="#23505e" stroke="#efe3bf" stroke-opacity=".7" stroke-width="${f1(z * .22)}" paint-order="stroke"`)).join('');
    s += `<g transform="translate(1872 2214) scale(1.6)">${P_SERPENT}</g><g transform="translate(3120 2446) scale(1.3)">${P_WHALE}</g><g transform="translate(2130 2440) scale(1.2)">${P_CHAIKA}</g><g transform="translate(3610 2480)">${pCompass(150)}</g>`;
    return { defs, body: s, inSea };
  },
  halos(free) { return free.length ? `<path d="${free.map((R) => R.slots.map((sl) => curvedSlotD(R, sl, 15.5)).join('')).join('')}" fill="#f6ebca" stroke="#f6ebca" stroke-width="9" stroke-opacity=".9" stroke-linejoin="round"/>` : ''; },
  slotsD(R, W = this.slotW) { return R.slots.map((sl) => curvedSlotD(R, sl, W / 2)).join(''); },
  route(R, ctx) {
    return `<path d="${R.curveD}" fill="none" stroke="${SEPIA}" stroke-opacity=".5" stroke-width="2.6" stroke-dasharray="3 9" stroke-linecap="round"/>`
      + this.tiles(R, ctx, this.slotW, 1.26);
  },
  tiles(R, ctx, W, ks) {
    const grey = R.cargo === 'grey';
    return `<path d="${this.slotsD(R, W)}" fill="${grey ? P_SOFT.grey : `url(#${ctx.p}pt-${R.cargo})`}" stroke="${INK}" stroke-width="2.6" stroke-linejoin="round"${grey ? ' stroke-dasharray="8 5"' : ''}/>`
      + R.slots.map((sl) => `<use href="#${ctx.p}ic-${R.cargo}" transform="translate(${f1(sl.x)} ${f1(sl.y)}) scale(${ks})"/>`).join('');
  },
  claimed(R, seat, ctx) {
    const col = ctx.pal.seat[seat] || '#888';
    let s = `<path d="${R.spanD}" fill="none" stroke="${col}" stroke-opacity=".55" stroke-width="14"/>`;
    for (const sl of R.slots) s += this.cartOne(R, sl, seat, ctx);
    return s;
  },
  cartOne(R, sl, seat, ctx) {
    const hw = Math.min(sl.len * .46, 62), sx = hw / 30, sy = Math.max(.95, Math.min(1.25, sx));
    return `<use href="#${ctx.p}cart-${seat}" transform="translate(${f1(sl.x)} ${f1(sl.y)}) rotate(${f1(upright(sl.a))}) scale(${f1((R.id % 2 ? -1 : 1) * sx)} ${f1(sy)})"/>`;
  },
  sel(R, ctx, claimedHtml) {
    const d = this.slotsD(R, this.selW);
    return `<path d="${d}" fill="#ffe28a" stroke="#ffcf3f" stroke-width="22" stroke-linejoin="round"/><path d="${d}" fill="#fff6cf" stroke="#fff6cf" stroke-width="9" stroke-linejoin="round"/>`
      + (claimedHtml || this.tiles(R, ctx, this.selW, 1.42));
  },
  nodeR: (c) => Math.min(c.r * .92, [62, 50, 42, 36][TIER(c.name)]),
  city(c, ctx) {
    const r = this.nodeR(c), k = kindOf(c.name);
    return `<g transform="translate(${c.x} ${c.y})"><circle r="${f1(r)}" fill="#f6ebc9" stroke="${INK}" stroke-width="2.8"/><circle r="${f1(r - 6)}" fill="none" stroke="${INK}" stroke-opacity=".35" stroke-width="1.1"/><use href="#${ctx.p}t-${k}" transform="scale(${(r / 44).toFixed(2)})"/></g>`;
  },
  rim(c, col) { return `<circle cx="${c.x}" cy="${c.y}" r="${f1(this.nodeR(c) + 13)}" fill="${col || '#ffd95c'}" stroke="#ffcf3f" stroke-width="6"/>`; },
  ring(c) {
    const r = this.nodeR(c) + 16;
    return `<path transform="translate(${c.x} ${c.y}) rotate(${(c.x % 90) - 45})" d="M${f1(-r)} 2C${f1(-r)} ${f1(-r * .62)} ${f1(-r * .6)} ${f1(-r)} 2 ${f1(-r)}C${f1(r * .66)} ${f1(-r)} ${f1(r + 2)} ${f1(-r * .55)} ${f1(r)} 3C${f1(r - 2)} ${f1(r * .64)} ${f1(r * .6)} ${f1(r + 2)} -3 ${f1(r)}C${f1(-r * .7)} ${f1(r)} ${f1(-r - 3)} ${f1(r * .5)} ${f1(-r - 6)} -6" fill="none" stroke="#b3261e" stroke-width="5" stroke-linecap="round"/>`;
  },
  ringExtra: 18,
  font: { family: SERIF, weight: () => 400 },
  labelPx(c, phone) { const t = [17.5, 15, 13.5, 12.5][TIER(c.name)]; return phone ? t - 2 : t; },
  label(L) {
    const t0 = L.c.name === 'Київ';
    return L.lines.map((t, i) => `<text x="${f1(L.cx)}" y="${f1(L.y + L.lh * (i + .82))}" text-anchor="middle" font-family="${SERIF}" font-size="${f1(L.fs)}" font-weight="${L.hot ? 700 : 400}" fill="${t0 || L.hot ? '#5a0f0a' : '#24140a'}" stroke="#f6ebca" stroke-opacity=".92" stroke-width="${f1(L.fs * .3)}" paint-order="stroke" stroke-linejoin="round">${nice(t)}</text>`).join('');
  },
};

// ═════════════════════════════ «Настілка» ═════════════════════════════
const BI = '#2a160b';
const SEA_LL = [[45.16, 29.72], [44.9, 29.62], [44.55, 28.95], [44.17, 28.66], [43.8, 28.6], [43.2, 28.0], [41.5, 28.0], [41.5, 42.0], [43.6, 39.7], [44.1, 39.07], [44.56, 38.07], [44.72, 37.8], [44.89, 37.31], [45.08, 36.75], [45.11, 36.62], [45.22, 36.65], [45.33, 36.9], [45.33, 37.38], [45.62, 37.62], [46.05, 38.12], [46.35, 37.85], [46.68, 38.24], [46.92, 38.42], [47.1, 38.6], [47.2, 38.93], [47.26, 38.3], [47.25, 38.0], [47.2, 37.5], [46.95, 36.6], [47.0, 35.4], [46.95, 34.5], [46.85, 33.4], [47.05, 32.2], [46.75, 31.1], [46.45, 30.5], [46.0, 30.1], [45.7, 29.9], [45.45, 29.65]];
const inPoly = (x, y, poly) => { let c = false; for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) { const a = poly[i], b = poly[j]; if ((a[1] > y) !== (b[1] > y) && x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]) c = !c; } return c; };
const CARP = [[49.05, 22.75], [48.86, 23.05], [48.92, 23.5], [48.68, 23.25], [48.72, 23.75], [48.45, 23.45], [48.48, 23.95], [48.28, 23.72], [48.27, 24.22], [48.08, 24.0], [48.14, 24.52], [47.98, 24.55], [48.0, 24.92], [47.92, 25.22], [48.2, 24.95], [48.42, 24.55], [48.08, 25.35], [48.6, 24.2]];
const CRM = [[44.56, 33.92], [44.64, 33.78], [44.66, 34.12], [44.76, 34.28], [44.84, 34.58], [44.9, 34.88], [44.97, 35.14], [44.76, 34.02], [44.88, 34.34]];
const bPeak = `<path d="M-1 0L0 -1.15L1 0Z" fill="#7d9a6a"/><path d="M0 -1.15L1 0L.15 0L-.12 -.58Z" fill="#587a52"/><path d="M0 -1.15L.34 -.72L.12 -.83L-.06 -.67L-.36 -.71Z" fill="#fff"/><path d="M-1 0L0 -1.15L1 0" fill="none" stroke="#2f4a33" stroke-width=".075" stroke-linejoin="round" stroke-linecap="round"/>`;
const bBoat = `<path d="M-22 4H22L14 15H-14Z" fill="#8a5324" stroke="#123e6a" stroke-width="2.2" stroke-linejoin="round"/><path d="M-1 3V-24" stroke="#123e6a" stroke-width="2.4"/><path d="M1 -22L19 0H1Z" fill="#fff8e6" stroke="#123e6a" stroke-width="2"/><path d="M-3 -18L-16 0H-3Z" fill="#f4c542" stroke="#123e6a" stroke-width="2"/>`;
const bFish = `<g opacity=".8"><path d="M-14 0C-8 -7 4 -7 8 -1L15 -6V6L8 1C4 7 -8 7 -14 0Z" fill="#e8f8ff"/><circle cx="-8" cy="-1" r="1.2" fill="#1766ad"/></g>`;
const bVignette = `<ellipse cx="0" cy="16" rx="62" ry="5" fill="#3b2a12" opacity=".2"/><path d="M-46 8L-8 8" stroke="${BI}" stroke-width="3"/><rect x="-6" y="-14" width="48" height="20" rx="3" fill="#b77a3f" stroke="${BI}" stroke-width="2.4"/><path d="M-4 -14C4 -30 30 -30 40 -14Z" fill="#fff8e6" stroke="${BI}" stroke-width="2.4"/><circle cx="4" cy="10" r="8" fill="#7a4a25" stroke="${BI}" stroke-width="2.4"/><circle cx="32" cy="10" r="8" fill="#7a4a25" stroke="${BI}" stroke-width="2.4"/><path d="M-64 -4C-64 -12 -52 -14 -44 -12L-30 -12C-26 -12 -24 -8 -24 -4L-24 12M-58 12L-58 -2M-32 12L-32 2" fill="none" stroke="${BI}" stroke-width="4.2" stroke-linecap="round"/><path d="M-66 -9C-64 -14 -52 -16 -44 -12L-28 -11C-24 -10 -23 -6 -24 -2L-27 4L-52 4C-58 2 -64 -3 -66 -9Z" fill="#f3e7cc" stroke="${BI}" stroke-width="2.2"/><path d="M-58 -13C-62 -22 -70 -22 -74 -26M-54 -14C-50 -22 -42 -24 -38 -28" fill="none" stroke="${BI}" stroke-width="2.6" stroke-linecap="round"/>`;
function bCompass(x, y, r) {
  const b = new Bucket();
  for (let i = 0; i < 8; i++) { const k = i % 2 ? .46 : .78, w = i % 2 ? .08 : .12, a = i * 45; b.fill('#e8f8ff', xf(`M0 ${-r * k}L${r * w} 0L0 0Z`, 0, 0, 1, a)); b.fill('#1766ad', xf(`M0 ${-r * k}L${-r * w} 0L0 0Z`, 0, 0, 1, a)); }
  return `<g transform="translate(${x} ${y})"><circle r="${r}" fill="#fff" opacity=".13"/><circle r="${r * .92}" fill="none" stroke="#e8f8ff" stroke-width="${f1(r * .025)}" opacity=".85"/><circle r="${r * .78}" fill="none" stroke="#e8f8ff" stroke-width="${f1(r * .012)}" stroke-dasharray="${f1(r * .03)} ${f1(r * .05)}" opacity=".85"/>${b.svg()}<text y="${f1(-r)}" text-anchor="middle" font-family="${SANS}" font-size="${f1(r * .24)}" font-weight="900" fill="#fff">Пн</text></g>`;
}

const board = {
  bg: '#efe3c0', slotW: 33, selW: 40,
  geo: {},
  base(ctx) {
    const g = ctx.geo, p = ctx.p, u = (px) => f1(px / .3), v = (px) => px / .3, near = clearance(ctx), R = rng(77);
    const seaPts = SEA_LL.map(([a, b]) => g.proj(a, b)), seaD = P(seaPts) + 'Z';
    const fills = {}, inks = {}; for (const k in TEX) { fills[k] = ctx.pal.cargo[k]; inks[k] = ctx.pal.cargoInk[k]; }
    let defs = `<linearGradient id="${p}landG" x1="0" y1="0" x2="0" y2="2730" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#8fca5f"/><stop offset=".3" stop-color="#a8d868"/><stop offset=".58" stop-color="#c8de70"/><stop offset=".8" stop-color="#e3dc7a"/><stop offset="1" stop-color="#e8d98a"/></linearGradient>
      <linearGradient id="${p}seaG" x1="0" y1="1700" x2="0" y2="2760" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#54c0ec"/><stop offset="1" stop-color="#2d97de"/></linearGradient>
      <pattern id="${p}hatchN" width="52" height="52" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><path d="M0 0V52" stroke="#c9b98c" stroke-width="6" opacity=".55"/></pattern>
      <clipPath id="${p}clipSea"><path d="${seaD}"/></clipPath><clipPath id="${p}clipLand"><path d="${g.landD}"/></clipPath>
      <g id="${p}pk">${bPeak}</g>` + texDefs(p, fills, inks, .2);
    const blobs = [[420, 1290, 430, 290, '#5fa24d', .75], [1100, 330, 1100, 260, '#5ea850', .5], [2830, 2600, 330, 140, '#6fa64e', .85], [3500, 1400, 380, 360, '#b4b562', .55], [2100, 1000, 700, 330, '#b8e070', .6]];
    blobs.forEach((b, i) => { defs += `<radialGradient id="${p}bl${i}"><stop offset="0" stop-color="${b[4]}" stop-opacity="${b[5]}"/><stop offset=".55" stop-color="${b[4]}" stop-opacity="${b[5] * .75}"/><stop offset="1" stop-color="${b[4]}" stop-opacity="0"/></radialGradient>`; });
    for (const k in B_GLYPH) defs += `<g id="${p}ic-${k}">${B_GLYPH[k]}</g>`;
    defs += `<g id="${p}ic-grey"><circle r="3.4" fill="#8a7a58"/></g>`;
    ctx.pal.seat.forEach((col, i) => {
      const L = 100, T = 36, d = shade(col, -.42), lit = shade(col, .22), hi = shade(col, .42);
      defs += `<g id="${p}cart-${i}"><rect x="-45" y="-13" width="30" height="${T + 8}" fill="none"/><rect x="${-L / 2 + 5}" y="${-T / 2 + 7}" width="${L}" height="${T}" rx="9" fill="${d}"/>`
        + [[-28, -1], [28, -1], [-28, 1], [28, 1]].map(([x, s]) => `<rect x="${x - 12}" y="${s < 0 ? -T / 2 - 7 : T / 2 - 5}" width="24" height="12" rx="4.8" fill="#20130a"/>`).join('')
        + `<rect x="${-L / 2}" y="${-T / 2}" width="${L}" height="${T}" rx="9" fill="${col}" stroke="${BI}" stroke-width="3.3"/><rect x="${-L / 2 + 6}" y="${-T * .3}" width="${L - 12}" height="${T * .6}" rx="7" fill="${lit}"/><path d="M${-L / 2 + 11} ${-T * .18}H${L / 2 - 11}" stroke="${hi}" stroke-width="4.6" stroke-linecap="round"/><path d="M-30 ${-T * .28}V${T * .28}M30 ${-T * .28}V${T * .28}" stroke="${d}" stroke-width="3.6" opacity=".7"/></g>`;
      defs += `<g id="${p}mk-${i}" fill="#fff" stroke="${d}" stroke-width="2.6" stroke-linejoin="round">${SEAT_MARK[i](9.5)}</g>`;
    });

    let s = `<rect x="-1800" y="-1200" width="8300" height="5600" fill="#efe3c0"/><rect x="-1800" y="-1200" width="8300" height="5600" fill="url(#${p}hatchN)"/>`;
    s += `<path d="${seaD}" fill="url(#${p}seaG)"/><g clip-path="url(#${p}clipSea)" fill="none" stroke-linejoin="round"><path d="${seaD}" stroke="#7fd2f2" stroke-width="${u(26)}"/><path d="${seaD}" stroke="#b8ebf8" stroke-width="${u(11)}"/><path d="${g.coastD}" stroke="#7fd2f2" stroke-width="${u(26)}"/><path d="${g.coastD}" stroke="#c6f0fa" stroke-width="${u(11)}"/>`;
    let waves = ''; const placed = [];
    const spaced = (x, y, d) => placed.every((q) => Math.hypot(q[0] - x, q[1] - y) > d);
    const reserved = [[1720, 2388, 2300, 2388], [2930, 2184, 3450, 2184]];
    const inRes = (x, y) => reserved.some(([a, b, c]) => x > a - 40 && x < c + 40 && Math.abs(y - b) < 50) || Math.hypot(x - 2375, y - 2570) < 130 || Math.hypot(x - 1700, y - 2510) < 90;
    for (let i = 0; i < 900; i++) { const x = R() * 4000, y = 1950 + R() * 800; if (!inPoly(x, y, seaPts) || g.inLand(x, y) || inRes(x, y) || !spaced(x, y, v(44)) || near(x, y, v(14))) continue; placed.push([x, y]); const q = v(8); waves += `M${f1(x)} ${f1(y)}q${f1(q * .5)} ${f1(-q * .5)} ${f1(q)} 0t${f1(q)} 0`; }
    s += `<path d="${waves}" stroke="#e8f8ff" stroke-width="${f1(v(8) * .13)}" stroke-linecap="round" opacity=".55"/></g>`;
    s += `<path d="${seaD}" fill="none" stroke="#8d7a4a" stroke-width="${u(1.4)}" stroke-linejoin="round"/><path d="${g.outlineD}" fill="none" stroke="#fff6da" stroke-width="${u(7)}" stroke-linejoin="round"/><path d="${g.landD}" fill="url(#${p}landG)"/>`;
    s += `<g clip-path="url(#${p}clipLand)">${blobs.map((b, i) => `<ellipse cx="${b[0]}" cy="${b[1]}" rx="${f1(b[2] * 1.25)}" ry="${f1(b[3] * 1.25)}" fill="url(#${p}bl${i})"/>`).join('')}</g>`;
    s += `<path d="${g.bordersD}" fill="none" stroke="#5f8a3e" stroke-width="${u(1.1)}" stroke-dasharray="${u(1.5)} ${u(3.2)}" stroke-linecap="round" opacity=".85"/>`;
    // ── флора ──
    const fl = new Bucket(), tuft = new Bucket(), tr = new Bucket(), P2 = [];
    const sp2 = (x, y, d) => P2.every((q) => Math.hypot(q[0] - x, q[1] - y) > d);
    for (let i = 0; i < 2600; i++) {
      const x = R() * 4000, y = R() * 2730, a = R(), b = R();
      if (!g.inLand(x, y) || near(x, y, v(15))) continue;
      const pole = y < 620 - (x - 2000) * .05 && x < 2700, sw = y > 1450;
      if (pole) { if (!sp2(x, y, v(19))) continue; P2.push([x, y]); const q = v(9 + a * 3); tr.fill('#6b4a25', xf(`M${-q * .1} ${-q * .3}H${q * .1}V${q * .1}H${-q * .1}Z`, x, y)); tr.add(`fill="#3f8a4a" stroke="#28663a" stroke-width="${f1(v(10) * .1)}" stroke-linejoin="round"`, xf(`M0 ${-q * 1.5}L${q * .62} ${-q * .2}L${-q * .62} ${-q * .2}Z`, x, y)); }
      else if (sw || (y > 650 && y < 1300 && x > 1100 && x < 3100)) {
        if (!sp2(x, y, sw ? v(34) : v(44))) continue; P2.push([x, y]);
        if (!sw && a < .4) { const q = v(5); let d = ''; for (let n = 0; n < 8; n++) { const A = n * .785; d += circ(x + Math.cos(A) * q * .62, y + Math.sin(A) * q * .62, q * .3); } fl.fill('#ffcb2e', d); fl.fill('#6b3e16', circ(x, y, q * .36)); }
        else { const q = v(sw ? 9 : 8); tuft.line('#9aa84e', q * .16, `M${f1(x - q * .5)} ${f1(y)}q${f1(q * .1)} ${f1(-q * .5)} ${f1(q * .2)} ${f1(-q * .8)}M${f1(x)} ${f1(y)}v${f1(-q)}M${f1(x + q * .5)} ${f1(y)}q${f1(-q * .1)} ${f1(-q * .5)} ${f1(-q * .2)} ${f1(-q * .8)}`); }
      }
    }
    let hills = ''; for (const [x, y] of [[3050, 1140], [3180, 1340], [3320, 1520], [3640, 1400], [3660, 1560], [3580, 1240], [2240, 1190], [1650, 1120], [1120, 1160], [1680, 1440]]) { if (!g.inLand(x, y) || near(x, y, v(13))) continue; const q = v(11); hills += `M${f1(x - q)} ${y}Q${x} ${f1(y - q * 1.1)} ${f1(x + q)} ${y}`; }
    s += tuft.svg() + fl.svg() + `<path d="${hills}" fill="#8db25c" stroke="#55793c" stroke-width="${f1(v(11) * .12)}"/>` + tr.svg();
    s += `<path d="${g.outlineD}" fill="none" stroke="#355a2d" stroke-width="${u(1.9)}" stroke-linejoin="round"/>`;
    const lake = (la, lo, rx, ry, rot) => { const [x, y] = g.proj(la, lo); return `<ellipse cx="${f1(x)}" cy="${f1(y)}" rx="${u(rx)}" ry="${u(ry)}" transform="rotate(${rot} ${f1(x)} ${f1(y)})" fill="#fbe3ec" stroke="#d27aa0" stroke-width="${u(1.2)}"/><ellipse cx="${f1(x - v(rx * .2))}" cy="${f1(y - v(ry * .15))}" rx="${u(rx * .55)}" ry="${u(ry * .5)}" transform="rotate(${rot} ${f1(x)} ${f1(y)})" fill="#fff"/>`; };
    s += lake(46.08, 33.72, 11, 5.5, -30) + lake(45.92, 33.84, 8, 4, 20) + lake(45.2, 33.45, 9, 4.5, 15);
    for (const rv of g.rivers) { const w = rv.big ? 5.4 : ['Дністер', 'Південний Буг', 'Сіверський Донець', 'Десна'].includes(rv.name) ? 3.2 : 2.4; s += `<path d="${rv.d}" fill="none" stroke="#1672bf" stroke-width="${u(w + 1.6)}" stroke-linejoin="round" stroke-linecap="round" opacity=".92"/><path d="${rv.d}" fill="none" stroke="#3db3f0" stroke-width="${u(w)}" stroke-linejoin="round" stroke-linecap="round"/>`; }
    const mts = []; CARP.forEach(([a, b], i) => { const [x, y] = g.proj(a, b); if (g.inLand(x, y) && !near(x, y, v(11))) mts.push([x, y + v(7), v(13.5 + (i % 3) * 2)]); });
    CRM.forEach(([a, b], i) => { const [x, y] = g.proj(a, b); if (g.inLand(x, y) && !near(x, y, v(9))) mts.push([x, y + v(6), v(12.5 + (i % 2) * 2)]); });
    mts.sort((a, b) => a[1] - b[1]);
    s += mts.map(([x, y, q]) => `<use href="#${p}pk" transform="translate(${f1(x)} ${f1(y)}) scale(${f1(q)})"/>`).join('');
    // ── надруковане на полі: моря, компас, човен, рибки, віз, табличка очок ──
    const rl = (t, x, y, rot, z) => `<text x="${x}" y="${y}" transform="rotate(${rot} ${x} ${y})" text-anchor="middle" font-family="${SANS}" font-size="${u(z)}" font-weight="700" font-style="italic" fill="#0d5d9e" stroke="#eaf8d2" stroke-width="${u(2.6)}" paint-order="stroke" stroke-linejoin="round">${t}</text>`;
    s += rl('Дніпро', 2740, 1636, -12, 10.5) + rl('Дністер', 1300, 1420, 32, 10.5) + rl('Півд. Буг', 1660, 1460, 60, 10.5) + rl('Десна', 2230, 280, 40, 10.5) + rl('Сів. Донець', 3560, 1040, 26, 10.5);
    s += `<text x="2010" y="2395" text-anchor="middle" font-family="${SANS}" font-size="${u(20)}" font-weight="800" font-style="italic" fill="#fff" opacity=".95" letter-spacing="${u(2.2)}">Чорне море</text><text x="3190" y="2190" text-anchor="middle" font-family="${SANS}" font-size="${u(14.5)}" font-weight="800" font-style="italic" fill="#fff" opacity=".93" letter-spacing="${u(1.6)}">Азовське море</text>`;
    const q1 = v(1);
    s += bCompass(2375, 2570, v(32));
    s += `<g transform="translate(1700 2520) scale(${f1(q1)})">${bBoat}</g>`;
    for (const [x, y, k, r0] of [[1500, 2660, 1.1, 10], [3280, 2330, 1, -14], [2120, 2640, 1, 16]]) s += `<g transform="translate(${x} ${y}) rotate(${r0}) scale(${f1(v(k))})">${bFish}</g>`;
    s += `<g transform="translate(1400 1610) scale(${f1(v(.82))})">${bVignette}</g>`;
    const pts = [[1, 1], [2, 2], [3, 4], [4, 7], [6, 15]];
    s += `<g class="sol2-nm" transform="translate(3240 54)"><rect x="${u(3)}" y="${u(4)}" width="745" height="${u(92)}" rx="${u(9)}" fill="#3a2410" opacity=".35"/><rect width="745" height="${u(92)}" rx="${u(9)}" fill="#fff6da" stroke="${BI}" stroke-width="${u(1.6)}"/><rect x="${u(3.5)}" y="${u(3.5)}" width="${f1(745 - 2 * v(3.5))}" height="${u(85)}" rx="${u(6)}" fill="none" stroke="#c5392b" stroke-width="${u(1.2)}" stroke-dasharray="${u(3)} ${u(2.4)}"/><g font-family="${SANS}"><text x="372" y="${u(34)}" text-anchor="middle" font-size="${u(23.5)}" font-weight="900" fill="#b3261e">Соляний шлях</text><text x="372" y="${u(51)}" text-anchor="middle" font-size="${u(9)}" font-weight="700" fill="#6e5440">чумацька настілка Дядька Глека</text><g transform="translate(${f1(372 - v(87))} ${u(70)})">`
      + pts.map(([k, val], i) => `<g transform="translate(${f1(i * v(35.8))} 0)"><rect x="${u(-1)}" y="${u(-9.5)}" width="${u(14)}" height="${u(13.5)}" rx="${u(3)}" fill="#d9cfb6" stroke="${BI}" stroke-width="${u(.9)}"/><text x="${u(6)}" y="${u(1.4)}" text-anchor="middle" font-size="${u(10)}" font-weight="900" fill="${BI}">${k}</text><text x="${u(14.8)}" y="${u(1.4)}" font-size="${u(10)}" font-weight="700" fill="#6e5440">→</text><text x="${u(25)}" y="${u(1.4)}" font-size="${u(11)}" font-weight="900" fill="#b3261e">${val}</text></g>`).join('')
      + `</g></g></g>`;
    return { defs, body: s };
  },
  halos() { return ''; },
  slotsD(R, W = this.slotW) { return R.slots.map((sl) => rrectD(sl.x, sl.y, sl.a, sl.len, W, W * .2)).join(''); },
  route(R, ctx) { return `<path d="${R.spanD}" fill="none" stroke="#fff8e2" stroke-width="${this.slotW + 12}" stroke-linecap="round" opacity=".55"/>` + this.tiles(R, ctx, this.slotW, 3.5); },
  tiles(R, ctx, W, sw) {
    const grey = R.cargo === 'grey', T = W;
    let hl = '', inner = '', ic = '';
    for (const sl of R.slots) {
      const c = Math.cos(sl.a), s = Math.sin(sl.a), L = sl.len, nx = -s * (-T / 2 + 5.4), ny = c * (-T / 2 + 5.4), h = L / 2 - T * .18;
      hl += `M${f1(sl.x + nx - c * h)} ${f1(sl.y + ny - s * h)}L${f1(sl.x + nx + c * h)} ${f1(sl.y + ny + s * h)}`;
      if (grey) inner += rrectD(sl.x, sl.y, sl.a, L - T * .4, T * .48, T * .14);
      const q = Math.min(T * .84, L * .85);
      ic += `<use href="#${ctx.p}ic-${R.cargo}" transform="translate(${f1(sl.x)} ${f1(sl.y)}) scale(${f1(q / 20)})"/>`;
    }
    return `<path d="${this.slotsD(R, W)}" fill="${grey ? '#d9cfb6' : `url(#${ctx.p}pt-${R.cargo})`}" stroke="${BI}" stroke-width="${sw}"/><path d="${hl}" fill="none" stroke="#fff" stroke-width="3.3" opacity="${grey ? .75 : .42}" stroke-linecap="round"/>${inner ? `<path d="${inner}" fill="none" stroke="#8a7a58" stroke-width="3.3" stroke-dasharray="8 6.6"/>` : ''}${ic}`;
  },
  claimed(R, seat, ctx) {
    let s = `<path d="${R.spanD}" fill="none" stroke="#fff8e2" stroke-width="${this.slotW + 12}" stroke-linecap="round" opacity=".55"/>`;
    for (const sl of R.slots) s += this.cartOne(R, sl, seat, ctx);
    return s;
  },
  cartOne(R, sl, seat, ctx) {
    const sx = Math.max(sl.len, this.slotW * 1.1) / 100, sy = this.slotW * 1.08 / 36;
    return `<use href="#${ctx.p}cart-${seat}" transform="translate(${f1(sl.x)} ${f1(sl.y)}) rotate(${f1(sl.a * 180 / Math.PI)}) scale(${f1(sx * 100) / 100} ${f1(sy * 100) / 100})"/><use href="#${ctx.p}mk-${seat}" transform="translate(${f1(sl.x)} ${f1(sl.y)})"/>`;
  },
  sel(R, ctx, claimedHtml) {
    return `<path d="${R.spanD}" fill="none" stroke="#1a0d05" stroke-width="${this.selW + 22}" stroke-linecap="round" opacity=".9"/><path d="${R.spanD}" fill="none" stroke="#ffe066" stroke-width="${this.selW + 13}" stroke-linecap="round"/>`
      + (claimedHtml || this.tiles(R, ctx, this.selW, 5));
  },
  nodeR: (c) => c.r * .3 + 17,
  city(c) {
    const r = this.nodeR(c);
    return `<circle cx="${f1(c.x + 2)}" cy="${f1(c.y + 4)}" r="${f1(r + 4)}" fill="#1b0d03" opacity=".45"/><circle cx="${c.x}" cy="${c.y}" r="${f1(r)}" fill="#fff8e6" stroke="${BI}" stroke-width="6.3"/><circle cx="${c.x}" cy="${c.y}" r="${f1(r * .38)}" fill="${BI}"/>`;
  },
  rim(c, col) { return `<circle cx="${c.x}" cy="${c.y}" r="${f1(this.nodeR(c) + 14)}" fill="${col || '#ffe066'}" stroke="#1a0d05" stroke-width="4.6"/>`; },
  ring(c) { const r = this.nodeR(c); return `<circle cx="${c.x}" cy="${c.y}" r="${f1(r * .42)}" fill="#c0392b"/><circle cx="${c.x}" cy="${c.y}" r="${f1(r + 8)}" fill="none" stroke="#c0392b" stroke-width="6"/>`; },
  ringExtra: 8,
  plate: true,
  font: { family: SANS, weight: () => 700 },
  labelPx(c, phone) { return phone ? 12.4 : 11; },
  label(L) {
    const pad = L.u;
    let s = `<rect x="${f1(L.x)}" y="${f1(L.y)}" width="${f1(L.w)}" height="${f1(L.h)}" rx="${f1(3 * pad)}" fill="${L.hot ? '#ffe066' : '#fff8e6'}" stroke="${L.ord ? '#c0392b' : '#6b5335'}" stroke-width="${f1((L.ord ? 1.3 : .7) * pad)}"/>`;
    L.lines.forEach((t, i) => { s += `<text x="${f1(L.cx)}" y="${f1(L.y + L.pady + L.lh * (i + .8))}" text-anchor="middle" font-family="${SANS}" font-size="${f1(L.fs)}" font-weight="${L.hot || L.c.name === 'Київ' ? 800 : 700}" fill="${BI}">${nice(t)}</text>`; });
    return s;
  },
};

// ═════════════════════════════ «Петриківка» ═════════════════════════════
const KP = { red: '#d9301f', red2: '#ff6a3d', yel: '#f6bf26', yel2: '#ffe08a', org: '#ef7d1a', grn: '#2f9e4a', grn2: '#7ad36b', blu: '#2a6fd0', cream: '#fbf1da', ink: '#0a0812' };
const FIELDS = ['#561a2c', '#14463a', '#3c1f5c', '#5a3c12', '#114a57', '#622914'];
const petalD = (L, W, b = 0) => `M0 0C${f1(L * .3)} ${f1(-W * .16 + b * .4)} ${f1(L * .55)} ${f1(-W * .62 + b)} ${f1(L * .8)} ${f1(-W * .5 + b)}C${f1(L * 1.03)} ${f1(-W * .36 + b)} ${f1(L * 1.03)} ${f1(W * .38 + b)} ${f1(L * .8)} ${f1(W * .5 + b)}C${f1(L * .55)} ${f1(W * .62 + b)} ${f1(L * .3)} ${f1(W * .16 + b * .4)} 0 0Z`;
function kPetal(B, x, y, deg, L, W, fill, hi) {
  B.fill(fill, xf(petalD(L, W), x, y, 1, deg));
  if (hi) B.fill(hi, xf(`M${f1(L * .28)} ${f1(-W * .02)}C${f1(L * .5)} ${f1(-W * .34)} ${f1(L * .72)} ${f1(-W * .38)} ${f1(L * .9)} ${f1(-W * .18)}C${f1(L * .72)} ${f1(-W * .18)} ${f1(L * .5)} ${f1(-W * .14)} ${f1(L * .28)} ${f1(-W * .02)}Z`, x, y, 1, deg));
}
function kBezAt(p0, p1, p2, p3, t) { const u = 1 - t, x = u * u * u * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t * t * t * p3[0], y = u * u * u * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t * t * t * p3[1], dx = 3 * u * u * (p1[0] - p0[0]) + 6 * u * t * (p2[0] - p1[0]) + 3 * t * t * (p3[0] - p2[0]), dy = 3 * u * u * (p1[1] - p0[1]) + 6 * u * t * (p2[1] - p1[1]) + 3 * t * t * (p3[1] - p2[1]); return [x, y, Math.atan2(dy, dx) * 180 / Math.PI, dx, dy]; }
function kBrush(B, p0, p1, p2, p3, w0, w1, fill) {
  const n = 20, L = [], Q = [];
  for (let i = 0; i <= n; i++) { const t = i / n, [x, y, , dx, dy] = kBezAt(p0, p1, p2, p3, t), l = Math.hypot(dx, dy) || 1, w = (w0 + (w1 - w0) * t) / 2; L.push([x - dy / l * w, y + dx / l * w]); Q.unshift([x + dy / l * w, y - dx / l * w]); }
  B.fill(fill, P(L.concat(Q)) + 'Z');
}
function kFlower(B, x, y, r, pal = {}) {
  const a = pal.a || KP.red, ah = pal.ah || KP.red2, b = pal.b || KP.yel, bh = pal.bh || KP.yel2, c = pal.c || KP.ink, dot = pal.dot || KP.cream, n = pal.n || 11;
  for (let i = 0; i < n; i++) kPetal(B, x + Math.cos(i / n * 6.283) * r * .2, y + Math.sin(i / n * 6.283) * r * .2, i / n * 360, r * .82, r * .46, a, ah);
  const m = Math.round(n * .75);
  for (let i = 0; i < m; i++) kPetal(B, x, y, (i + .5) / m * 360, r * .56, r * .26, b, bh);
  B.fill(c, circ(x, y, r * .19));
  for (let i = 0; i < 7; i++) B.fill(dot, circ(x + Math.cos(i / 7 * 6.283) * r * .115, y + Math.sin(i / 7 * 6.283) * r * .115, r * .03));
}
function kSunflower(B, x, y, r, pal = {}) {
  const a = pal.a || KP.yel, ah = pal.ah || KP.yel2, b = pal.b || KP.org, disc = pal.disc || '#2a1408', seed = pal.seed || KP.yel, n = pal.n || 19;
  for (let i = 0; i < n; i++) B.fill(b, xf(`M${f1(r * .42)} ${f1(-r * .13)}Q${f1(r * .8)} ${f1(-r * .19)} ${f1(r * 1.02)} 0Q${f1(r * .8)} ${f1(r * .19)} ${f1(r * .42)} ${f1(r * .13)}Z`, x, y, 1, (i + .5) / n * 360));
  for (let i = 0; i < n; i++) { const d = i / n * 360; B.fill(a, xf(`M${f1(r * .4)} ${f1(-r * .12)}Q${f1(r * .7)} ${f1(-r * .17)} ${f1(r * .88)} 0Q${f1(r * .7)} ${f1(r * .17)} ${f1(r * .4)} ${f1(r * .12)}Z`, x, y, 1, d)); B.fill(ah, xf(`M${f1(r * .46)} ${f1(-r * .05)}Q${f1(r * .66)} ${f1(-r * .09)} ${f1(r * .78)} 0Q${f1(r * .66)} ${f1(-r * .03)} ${f1(r * .46)} ${f1(r * .02)}Z`, x, y, 1, d)); }
  B.fill(disc, circ(x, y, r * .46));
  for (let i = 1; i < 90; i++) { const rr = r * .43 * Math.sqrt(i / 90), t = i * 2.39996; B.add(`fill="${i < 26 ? (pal.seed2 || KP.org) : seed}" class="sd"`, circ(x + Math.cos(t) * rr, y + Math.sin(t) * rr, r * .012 + r * .012 * i / 90)); }
}
function kKalyna(B, x, y, r, seed = 3, pal = {}) {
  const Rn = rng(seed), a = pal.a || KP.red, hi = pal.hi || KP.red2, d = pal.d || KP.ink, pts = []; let tries = 0;
  while (pts.length < (pal.n || 13) && tries++ < 400) { const t = Rn() * 6.283, q = Math.sqrt(Rn()) * r * .78, px = x + Math.cos(t) * q, py = y + Math.sin(t) * q * .86; if (pts.every((p) => Math.hypot(p[0] - px, p[1] - py) > r * .34)) pts.push([px, py]); }
  for (const [px, py] of pts) { const b = r * .19; B.fill(a, circ(px, py, b)); B.line(hi, b * .28, `M${f1(px - b * .6)} ${f1(py - b * .2)}A${f1(b * .7)} ${f1(b * .7)} 0 0 1 ${f1(px + b * .1)} ${f1(py - b * .65)}`); B.fill(d, circ(px + b * .3, py + b * .35, b * .15)); }
}
function kLeaf(B, x, y, deg, L, W, a = KP.grn, b = KP.grn2) {
  B.fill(a, xf(petalD(L, W), x, y, 1, deg));
  let v = `M${f1(L * .1)} 0L${f1(L * .84)} 0`;
  for (let i = 1; i <= 4; i++) { const t = .22 + i * .14; v += `M${f1(L * t)} 0L${f1(L * (t + .1))} ${f1(-W * .36)}`; }
  B.line(b, W * .06, xf(v, x, y, 1, deg));
}
function kSprig(B, p0, p1, p2, p3, o = {}) {
  const { w = 12, n = 9, L = 90, W = 34, a = KP.grn, h = KP.grn2, stem = KP.grn, curl = true } = o;
  kBrush(B, p0, p1, p2, p3, w, w * .25, stem);
  for (let i = 0; i < n; i++) { const t = (i + .6) / (n + .6), [x, y, d] = kBezAt(p0, p1, p2, p3, t), k = 1 - t * .62; kPetal(B, x, y, d - 42, L * k, W * k, a, h); kPetal(B, x, y, d + 42, L * k * .92, W * k * .92, a, h); }
  if (curl) { const [x, y, d] = kBezAt(p0, p1, p2, p3, 1); kPetal(B, x, y, d, L * .34, W * .34, h); }
}
function kPoly(ll, proj) { const p = ll.map(([a, b]) => proj(a, b)), n = p.length, r1 = Math.round; let d = ''; for (let i = 0; i < n; i++) { const a = p[i], b = p[(i + 1) % n], mx = (a[0] + b[0]) / 2, my = (a[1] + b[1]) / 2; d += (i ? 'Q' + r1(a[0]) + ' ' + r1(a[1]) + ' ' : 'M') + r1(mx) + ' ' + r1(my); } const a = p[0], b = p[1]; return d + 'Q' + r1(a[0]) + ' ' + r1(a[1]) + ' ' + r1((a[0] + b[0]) / 2) + ' ' + r1((a[1] + b[1]) / 2) + 'Z'; }

const petryk = {
  bg: '#08070f', slotW: 32, selW: 40,
  geo: {},
  base(ctx) {
    const g = ctx.geo, p = ctx.p, adj = g.adj, regions = g.regions;
    const order = regions.map((_, i) => i).sort((a, b) => adj[b].size - adj[a].size), fieldIdx = {}, use = FIELDS.map(() => 0);
    for (const i of order) { const bad = new Set([...adj[i]].map((j) => fieldIdx[j])); let best = -1; for (let c = 0; c < FIELDS.length; c++) if (!bad.has(c) && (best < 0 || use[c] < use[best])) best = c; fieldIdx[i] = best; use[best]++; }
    const black = kPoly([[45.6, 29.75], [45.17, 29.75], [44.75, 29.0], [44.2, 28.68], [43.4, 28.5], [42.5, 27.7], [41.6, 28.2], [41.3, 31], [41.9, 34], [41.9, 37], [41.2, 41], [42.5, 41.5], [43.6, 39.7], [44.1, 38.9], [44.7, 37.8], [44.92, 37.3], [45.1, 36.75], [45.15, 36.5], [45.32, 36.2], [45.5, 35], [45.9, 34.2], [46.3, 33.4], [46.75, 32.3], [46.75, 31.2], [46.35, 30.3]], g.proj);
    const azov = kPoly([[45.42, 36.72], [45.45, 36.1], [45.6, 35.3], [46.2, 34.9], [46.65, 35.5], [46.95, 36.8], [47.3, 37.9], [47.32, 38.3], [47.22, 38.92], [47.27, 39.3], [47.05, 39.25], [46.95, 38.9], [46.72, 38.2], [46.42, 37.95], [46.05, 38.15], [45.75, 37.75], [45.35, 37.38]], g.proj);
    const fills = {}, inks = {}; for (const k in TEX) { fills[k] = ctx.pal.cargo[k]; inks[k] = ctx.pal.cargoInk[k]; }
    let defs = `<clipPath id="${p}seaclip"><path d="${black}${azov}"/></clipPath><g id="${p}ros"><circle r="38" fill="${KP.ink}"/>`;
    const ros = new Bucket(); for (let i = 0; i < 8; i++) ros.fill('#fbf1da', xf(petalD(34, 19), 0, 0, 1, i * 45 + 22.5));
    defs += ros.svg() + `<circle r="10" fill="#d9301f"/></g>` + texDefs(p, fills, inks, .26);
    for (const k in K_GLYPH) defs += `<g id="${p}ic-${k}">${K_GLYPH[k]}</g>`;
    defs += `<g id="${p}ic-grey"><circle r="4" fill="#3a352c"/></g>`;
    ctx.pal.seat.forEach((c, i) => {
      const lt = mix(c, '#ffffff', .16), dk = mix(c, '#ffffff', .05);
      defs += `<g id="${p}cart-${i}"><g stroke="#0a0812" stroke-width="3.4" stroke-linejoin="round"><circle cx="-25" cy="17" r="11.5" fill="#fff4dc"/><circle cx="25" cy="17" r="11.5" fill="#fff4dc"/><path d="M-45 2H45L39 14H-39Z" fill="${dk}"/><path d="M-39 3C-39 -34 39 -34 39 3Z" fill="${lt}"/></g><path d="M-21 -20V3M21 -20V3" stroke="#fff4dc" stroke-width="2.4" opacity=".75"/><g transform="translate(0 -9)" fill="#fff4dc">${SEAT_MARK[i](10.5)}</g><circle cx="-25" cy="17" r="3.2" fill="#0a0812"/><circle cx="25" cy="17" r="3.2" fill="#0a0812"/></g>`;
    });

    let s = `<rect x="-1800" y="-1200" width="8300" height="5600" fill="#08070f"/><path d="${black}${azov}" fill="#10255f"/>`;
    const Rn = rng(11), wt = new Bucket(), t1 = mix('#10255f', '#6f9bff', .22), t2 = mix('#10255f', '#9fc0ff', .32);
    for (let y = 1900; y < 2760; y += 44) { const off = (y / 44 % 2) * 60; for (let x = 1300 + off; x < 4050; x += 120) { const L = 52 + Rn() * 26, dy = (Rn() - .5) * 8; if (Rn() < .22) continue; wt.fill(Rn() < .25 ? t2 : t1, `M${f1(x)} ${f1(y + dy)}q${f1(L * .5)} -15 ${f1(L)} 0q${f1(-L * .5)} -6 ${f1(-L)} 0Z`); } }
    s += `<g clip-path="url(#${p}seaclip)">${wt.svg()}</g>`;
    const fb = new Bucket(); regions.forEach((r, i) => fb.fill(FIELDS[fieldIdx[i]], r.d)); s += fb.svg();
    // тон-у-тон розпис області: ледь світліше за поле, не бореться зі шляхами
    regions.forEach((r, i) => {
      const f = FIELDS[fieldIdx[i]], t = mix(f, '#ffffff', .085), t2c = mix(f, '#ffffff', .15), m = Math.min(r.bb[2] - r.bb[0], r.bb[3] - r.bb[1]), cx = r.cx, cy = r.cy, K = i % 4, B = new Bucket();
      if (m < 90) return;
      if (K === 0) kSunflower(B, cx, cy, m * .44, { a: t, ah: t2c, b: t, disc: f, seed: t2c, seed2: t2c });
      else if (K === 1) { kFlower(B, cx, cy, m * .42, { a: t, ah: t2c, b: t2c, bh: t, c: f, dot: t2c }); kSprig(B, [cx - m * .55, cy + m * .5], [cx - m * .2, cy + m * .4], [cx, cy + m * .55], [cx + m * .55, cy + m * .35], { L: m * .17, W: m * .07, w: m * .02, n: 6, a: t, h: t2c, stem: t }); }
      else if (K === 2) { kKalyna(B, cx, cy, m * .34, i + 5, { a: t2c, hi: f, d: f, n: 14 }); kLeaf(B, cx - m * .25, cy - m * .25, -135, m * .4, m * .22, t, t2c); kLeaf(B, cx + m * .25, cy - m * .2, -48, m * .4, m * .22, t, t2c); kLeaf(B, cx + m * .1, cy + m * .3, 70, m * .38, m * .2, t, t2c); }
      else kSprig(B, [cx - m * .6, cy + m * .3], [cx - m * .2, cy - m * .3], [cx + m * .2, cy + m * .3], [cx + m * .6, cy - m * .3], { L: m * .24, W: m * .09, w: m * .03, n: 8, a: t, h: t2c, stem: t });
      defs += `<clipPath id="${p}rc${i}"><path d="${r.d}"/></clipPath>`;
      s += `<g clip-path="url(#${p}rc${i})">${B.svg()}</g>`;
    });
    s += `<path d="${g.bordersD}" fill="none" stroke="#0a0812" stroke-width="7" stroke-linejoin="round"/>`;
    const rb = g.rivers.filter((x) => x.big).map((x) => x.d).join(''), rs = g.rivers.filter((x) => !x.big).map((x) => x.d).join('');
    s += `<g fill="none" stroke-linecap="round" stroke-linejoin="round"><path d="${rb}" stroke="#0a0812" stroke-width="22" opacity=".85"/><path d="${rs}" stroke="#0a0812" stroke-width="13" opacity=".85"/><path d="${rb}" stroke="#63d4e6" stroke-width="13"/><path d="${rs}" stroke="#63d4e6" stroke-width="6.5"/></g>`;
    s += `<g fill="none" stroke-linejoin="round"><path d="${g.outlineD}" stroke="#0a0812" stroke-width="20"/><path d="${g.outlineD}" stroke="#f6bf26" stroke-width="9"/><path d="${g.outlineD}" stroke="#d9301f" stroke-width="9" stroke-dasharray="17 17"/></g>`;
    // флора поза кордоном
    const F = new Bucket();
    kSprig(F, [4010, 900], [3990, 700], [3900, 560], [3800, 340], { L: 110, W: 42, w: 18, n: 7, curl: false });
    kBrush(F, [3890, 520], [3760, 470], [3620, 430], [3500, 300], 12, 5, KP.grn);
    kLeaf(F, 3660, 440, 120, 96, 42); kLeaf(F, 3580, 372, -128, 80, 34);
    s += F.svg();
    const F2 = new Bucket(); kKalyna(F2, 3468, 252, 76, 8); kSunflower(F2, 3790, 250, 225); s += F2.svg();
    const F3 = new Bucket(); kSprig(F3, [1400, 2080], [1450, 1950], [1400, 1810], [1445, 1640], { L: 74, W: 30, w: 10, n: 6, curl: false }); kKalyna(F3, 1445, 1590, 58, 21, { n: 9 }); s += F3.svg();
    const F4 = new Bucket(); kSprig(F4, [1440, 2740], [1500, 2640], [1590, 2600], [1700, 2640], { L: 66, W: 28, w: 9, n: 5, curl: false }); kFlower(F4, 1720, 2650, 96); s += F4.svg();
    s += `<text class="sol2-nm" x="2610" y="125" font-family="${SANS}" font-size="116" font-weight="900" fill="#ffe08a" letter-spacing="-2">Соляний шлях</text><g transform="translate(2960 150) scale(1.85)">${K_OXCART}</g><path d="M2930 306q210 -16 450 -2q-220 -2 -450 2z" fill="#f6bf26"/>`;
    const rl = (t, x, y, rot) => `<text transform="translate(${x} ${y}) rotate(${rot})" text-anchor="middle" font-family="${SANS}" font-size="33" font-weight="600" font-style="italic" fill="#8fe4f2" stroke="#08070f" stroke-width="9" paint-order="stroke" stroke-linejoin="round">${t}</text>`;
    s += rl('Дніпро', 2740, 1636, -12) + rl('Дністер', 1190, 1395, 28) + rl('Десна', 1990, 225, 62) + rl('Прип’ять', 1120, 240, -6);
    s += `<g font-family="${SANS}" font-size="66" font-weight="800" fill="#7fa7ff" letter-spacing="26" text-anchor="middle"><text x="2060" y="2520">ЧОРНЕ</text><text x="2060" y="2610">МОРЕ</text><text x="3120" y="2200" font-size="50" letter-spacing="18">АЗОВСЬКЕ МОРЕ</text></g>`;
    return { defs, body: s };
  },
  halos() { return ''; },
  slotsD(R, W = this.slotW) { return R.slots.map((sl) => rrectD(sl.x, sl.y, sl.a, sl.len, W, 9)).join(''); },
  route(R, ctx) { return this.tiles(R, ctx, this.slotW, W_SCALE(this.slotW)); },
  tiles(R, ctx, W, ks) {
    const tar = R.cargo === 'tar';
    return `<path d="${this.slotsD(R, W)}" fill="${R.cargo === 'grey' ? ctx.pal.cargo.grey : `url(#${ctx.p}pt-${R.cargo})`}" stroke="${tar ? '#fbf1da' : '#0a0812'}" stroke-width="${tar ? 3.6 : 3}"/>`
      + R.slots.map((sl) => `<use href="#${ctx.p}ic-${R.cargo}" transform="translate(${f1(sl.x)} ${f1(sl.y)}) scale(${ks})"/>`).join('');
  },
  claimed(R, seat, ctx) {
    const c = ctx.pal.seat[seat] || '#888';
    let s = `<path d="${R.spanD}" fill="none" stroke="#0a0812" stroke-width="58" stroke-linecap="round"/><path d="${R.spanD}" fill="none" stroke="${mix(c, '#ffffff', .12)}" stroke-width="50"/><path d="${R.spanD}" fill="none" stroke="${mix(c, '#000000', .62)}" stroke-width="38" stroke-linecap="round"/>`;
    for (const sl of R.slots) s += this.cartOne(R, sl, seat, ctx);
    return s;
  },
  cartOne(R, sl, seat, ctx) { const q = Math.min(.9, Math.max(.56, (sl.len - 8) / 96)); return `<use href="#${ctx.p}cart-${seat}" transform="translate(${f1(sl.x)} ${f1(sl.y)}) rotate(${f1(upright(sl.a))}) scale(${f1(q * 100) / 100})"/>`; },
  sel(R, ctx, claimedHtml) {
    return `<path d="${R.spanD}" fill="none" stroke="#ffd34d" stroke-width="112" stroke-linecap="round" opacity=".12"/><path d="${R.spanD}" fill="none" stroke="#ffd34d" stroke-width="92" stroke-linecap="round" opacity=".2"/><path d="${R.spanD}" fill="none" stroke="#0a0812" stroke-width="72" stroke-linecap="round"/><path d="${R.spanD}" fill="none" stroke="#ffd34d" stroke-width="62" stroke-linecap="round"/><path d="${R.spanD}" fill="none" stroke="#0a0812" stroke-width="50" stroke-linecap="round"/>`
      + (claimedHtml || this.tiles(R, ctx, this.selW, W_SCALE(this.selW)));
  },
  nodeR: (c) => Math.min(c.r, 38) * .98,
  city(c, ctx) { return `<use href="#${ctx.p}ros" transform="translate(${c.x} ${c.y}) scale(${(this.nodeR(c) / 38).toFixed(2)})"/>`; },
  rim(c, col) { return `<circle cx="${c.x}" cy="${c.y}" r="${f1(this.nodeR(c) + 12)}" fill="${col || '#ffd34d'}" stroke="#0a0812" stroke-width="5"/>`; },
  ring(c, i) { const q = 36, L = 'АБВГҐДЕЄЖЗ'[i % 10]; return `<g transform="translate(${c.x} ${c.y})"><circle r="${f1(q * 1.04)}" fill="#0a0812"/><circle r="${f1(q * .88)}" fill="#ffd34d"/><circle r="${f1(q * .68)}" fill="none" stroke="#7a4d08" stroke-width="${f1(q * .05)}" stroke-dasharray="${f1(q * .1)} ${f1(q * .1)}"/><text y="${f1(q * .34)}" text-anchor="middle" font-family="${SANS}" font-size="${f1(q * .94)}" font-weight="900" fill="#1b0f06">${L}</text></g>`; },
  ringExtra: 4,
  font: { family: SANS, weight: () => 700 },
  labelPx(c, phone) { const big = c.lvl === 1 ? 1.14 : 1; return (phone ? 12.4 : 12.6) * big; },
  label(L) {
    return L.lines.map((t, i) => `<text x="${f1(L.cx)}" y="${f1(L.y + L.lh * (i + .82))}" text-anchor="middle" font-family="${SANS}" font-size="${f1(L.fs)}" font-weight="${L.hot ? 800 : 700}" fill="${L.hot ? '#ffe08a' : '#fbf1da'}" stroke="#08070f" stroke-width="${f1(L.fs * .3)}" stroke-linejoin="round" paint-order="stroke">${nice(t)}</text>`).join('');
  },
};
const W_SCALE = (W) => f1(W / 26);

const ARTS = { parchment, board, petryk };
export const artOf = (id) => ARTS[id] || parchment;
export const labelLines = (name) => BREAK[name] || [name];
