// «Стара мапа» — геометрія й малювання самої мапи (сітка 4000×2730).
import { readFileSync } from 'node:fs';
import { INK, SEPIA, CARGO, mark, TOWN, cart, PMARK, serpent, whale, chaika, compass } from './art.mjs';

const SP = '/private/tmp/claude-501/-Users-admin-olimp-glechyk/a2ec8d08-6007-4765-b570-a92fbb040f3d/scratchpad/sol';
export const fx = JSON.parse(readFileSync(SP + '/fixture.json', 'utf8'));
const geo = JSON.parse(readFileSync('/Users/admin/olimp/glechyk/web/games/geo-map.json', 'utf8'));

// проєкція Альберса (data/geo/build-map.py) — щоб дорисувати чужі береги Чорного моря й Азова
const r_ = (d) => d * Math.PI / 180;
const { phi1, phi2, phi0, lam0, x0, y0, k: K } = geo.proj;
const N = (Math.sin(r_(phi1)) + Math.sin(r_(phi2))) / 2;
const C = Math.cos(r_(phi1)) ** 2 + 2 * N * Math.sin(r_(phi1));
const RHO0 = Math.sqrt(C - 2 * N * Math.sin(r_(phi0))) / N;
export function project(lat, lon) {
  const rho = Math.sqrt(C - 2 * N * Math.sin(r_(lat))) / N, th = N * r_(lon - lam0);
  return [(rho * Math.sin(th) - x0) * K, (y0 - (RHO0 - rho * Math.cos(th))) * K];
}

function rng(seed) { let a = seed >>> 0; return () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
const f1 = (v) => (Math.round(v * 10) / 10);

const chaikin = (pts) => {
  if (pts.length < 3) return pts;
  const o = [pts[0]];
  for (let i = 0; i < pts.length - 1; i++) {
    const [a, b] = [pts[i], pts[i + 1]];
    if (i > 0) o.push([a[0] * .75 + b[0] * .25, a[1] * .75 + b[1] * .25]);
    if (i < pts.length - 2) o.push([a[0] * .25 + b[0] * .75, a[1] * .25 + b[1] * .75]);
  }
  o.push(pts[pts.length - 1]);
  return o;
};
const P = (pts) => 'M' + pts.map((p) => f1(p[0]) + ' ' + f1(p[1])).join('L');

// ─── дуги → контури ─────────────────────────────────────────────────────────────────────────────────
const arcs = geo.arcs.map((a) => { const p = []; for (let i = 0; i < a.length; i += 2) p.push([a[i], a[i + 1]]); return chaikin(p); });
const refs = new Int32Array(arcs.length);
for (const r of geo.regions) for (const ring of r.rings) for (const id of ring) refs[Math.abs(id) - 1]++;
const regionRings = geo.regions.map((r) => ({ name: r.name, rings: r.rings.map((ring) => {
  const out = [];
  ring.forEach((id, n) => {
    const a = arcs[Math.abs(id) - 1];
    const seq = id > 0 ? a : [...a].reverse();
    for (let k = n ? 1 : 0; k < seq.length; k++) out.push(seq[k]);
  });
  return out;
}) }));
const regionD = regionRings.map((r) => ({ name: r.name, d: r.rings.map((x) => P(x) + 'Z').join('') }));
export const landD = regionD.map((r) => r.d).join('');
const outlineD = arcs.filter((_, i) => refs[i] === 1).map(P).join('');
const bordersD = arcs.filter((_, i) => refs[i] > 1).map(P).join('');
const riversBig = geo.rivers.filter((r) => r.big).flatMap((r) => r.lines);
const riversOther = geo.rivers.filter((r) => !r.big).flatMap((r) => r.lines);
const lineD = (l) => { const p = []; for (let i = 0; i < l.length; i += 2) p.push([l[i], l[i + 1]]); return P(chaikin(p)); };
const riverBigD = riversBig.map(lineD).join('');
const riverOtherD = riversOther.map(lineD).join('');
function inLand(x, y) {
  for (const r of regionRings) for (const ring of r.rings) {
    let c = false;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      const [xi, yi] = ring[i], [xj, yj] = ring[j];
      if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) c = !c;
    }
    if (c) return r.name;
  }
  return null;
}

// ─── чужі береги (за справжніми координатами) і море ────────────────────────────────────────────────
const romaniaCoast = [[42.4, 27.7], [43.2, 27.95], [43.8, 28.6], [44.17, 28.66], [44.68, 29.0], [44.89, 29.6], [45.16, 29.75], [45.42, 29.72]].map(([a, b]) => project(a, b));
const russiaCoast = [[47.1, 38.2], [47.2, 38.9], [47.15, 39.3], [46.95, 39.0], [46.7, 38.3], [46.35, 37.9], [46.05, 38.15], [45.7, 37.7], [45.35, 37.4], [45.36, 36.95], [45.2, 36.65], [44.95, 37.25], [44.65, 37.85], [44.1, 39.05], [43.6, 39.7]].map(([a, b]) => project(a, b));
const inlandSea = [[45.42, 29.72], [45.9, 30.2], [46.6, 30.6], [47.0, 31.3], [47.0, 32.4], [46.8, 33.4], [46.7, 34.6], [47.1, 35.6], [47.4, 36.6], [47.4, 37.7], [47.1, 38.2]].map(([a, b]) => project(a, b));
const seaPts = [...chaikin(chaikin(romaniaCoast)), ...inlandSea, ...chaikin(chaikin(russiaCoast)), [6000, 4200], [-200, 4200]];
const seaD = P(seaPts) + 'Z';
const foreignCoastD = P(chaikin(chaikin(romaniaCoast))) + P(chaikin(chaikin(russiaCoast)));

// ─── шляхи: крива, слоти ────────────────────────────────────────────────────────────────────────────
const city = Object.fromEntries(fx.cities.map((c) => [c.name, c]));
const owner = {};
for (const [pi, ids] of Object.entries(fx.mock.claimed)) for (const id of ids) owner[id] = +pi;
export const owners = owner;

function curvePts(a, b, c, n = 60) {
  const dx = b.x - a.x, dy = b.y - a.y, L = Math.hypot(dx, dy);
  const cx = (a.x + b.x) / 2 - dy / L * c * L, cy = (a.y + b.y) / 2 + dx / L * c * L;
  const pts = [];
  for (let i = 0; i <= n; i++) {
    const t = i / n, u = 1 - t;
    pts.push([u * u * a.x + 2 * u * t * cx + t * t * b.x, u * u * a.y + 2 * u * t * cy + t * t * b.y]);
  }
  return pts;
}
const R = rng(77);
const routes = fx.routes.map((r) => ({ ...r, A: city[r.a], B: city[r.b], c: (R() < .5 ? -1 : 1) * .07 }));
// жадібно підбираємо кривизну: не лізти в чужі міста і подалі від сусідніх шляхів
const CH = [-.2, -.15, -.1, -.06, .06, .1, .15, .2];
for (const r of routes) r.pts = curvePts(r.A, r.B, r.c);
const cost = (r, pts) => {
  let v = Math.abs(Math.abs(r.c) - .08) * 5;
  for (const c of fx.cities) {
    if (c === r.A || c === r.B) continue;
    for (const p of pts) { const d = Math.hypot(p[0] - c.x, p[1] - c.y); if (d < 95) v += (95 - d) * 2.4; }
  }
  for (const o of routes) {
    if (o === r) continue;
    const shared = o.A === r.A || o.A === r.B || o.B === r.A || o.B === r.B;
    for (let i = 0; i < pts.length; i += 3) for (let j = 0; j < o.pts.length; j += 3) {
      const d = Math.hypot(pts[i][0] - o.pts[j][0], pts[i][1] - o.pts[j][1]);
      if (d < 56) {
        if (shared) {
          const near = Math.min(Math.hypot(pts[i][0] - r.A.x, pts[i][1] - r.A.y), Math.hypot(pts[i][0] - r.B.x, pts[i][1] - r.B.y));
          if (near < 100) continue;
        }
        v += (56 - d) * .9;
      }
    }
  }
  return v;
};
for (let pass = 0; pass < 5; pass++) for (const r of routes) {
  let best = null, bv = 1e18;
  for (const c of CH) {
    const saved = r.c; r.c = c;
    const pts = curvePts(r.A, r.B, c);
    const v = cost(r, pts);
    if (v < bv) { bv = v; best = c; }
    r.c = saved;
  }
  r.c = best; r.pts = curvePts(r.A, r.B, best);
}

function arc(pts) { const s = [0]; for (let i = 1; i < pts.length; i++) s.push(s[i - 1] + Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1])); return s; }
function at(pts, s, d) {
  let i = 1; while (i < s.length - 1 && s[i] < d) i++;
  const t = (d - s[i - 1]) / (s[i] - s[i - 1] || 1);
  return { x: pts[i - 1][0] + (pts[i][0] - pts[i - 1][0]) * t, y: pts[i - 1][1] + (pts[i][1] - pts[i - 1][1]) * t, tx: pts[i][0] - pts[i - 1][0], ty: pts[i][1] - pts[i - 1][1] };
}
const TOWN_R = (n) => (n === 'Київ' ? 62 : BIG.has(n) ? 50 : CAP.has(n) ? 42 : 36);
const BIG = new Set(['Харків', 'Одеса', 'Львів', 'Дніпро']);
const CAP = new Set(['Чернігів', 'Суми', 'Полтава', 'Луганськ', 'Донецьк', 'Запоріжжя', 'Черкаси', 'Кропивницький', 'Миколаїв', 'Херсон', 'Сімферополь', 'Вінниця', 'Житомир', 'Рівне', 'Хмельницький', 'Чернівці', 'Івано-Франківськ', 'Тернопіль', 'Луцьк', 'Ужгород', 'Кам\'янець-Подільський', 'Ізмаїл', 'Севастополь', 'Маріуполь', 'Перекоп', 'Опішня']);
const TIER = (n) => (n === 'Київ' ? 0 : BIG.has(n) ? 1 : CAP.has(n) ? 2 : 3);
const TOWN_KIND = {
  'Київ': 'kyiv', 'Харків': 'major', 'Львів': 'castle', 'Одеса': 'port', 'Дніпро': 'major',
  'Кам\'янець-Подільський': 'castle', 'Ізмаїл': 'castle', 'Луцьк': 'castle', 'Ужгород': 'castle', 'Перекоп': 'gate', 'Опішня': 'pot',
  'Севастополь': 'port', 'Маріуполь': 'port', 'Бердянськ': 'port', 'Миколаїв': 'port', 'Херсон': 'port',
};
const kindOf = (n) => TOWN_KIND[n] || (CAP.has(n) ? 'capital' : 'village');

for (const r of routes) {
  const s = arc(r.pts), L = s[s.length - 1];
  const m0 = TOWN_R(r.a) * .78, m1 = TOWN_R(r.b) * .78;
  const free = L - m0 - m1;
  const gap = Math.min(9, free / r.len * .12);
  const sl = (free - gap * (r.len - 1)) / r.len;
  r.slots = [];
  for (let i = 0; i < r.len; i++) {
    const d0 = m0 + i * (sl + gap), d1 = d0 + sl;
    const ring = [];
    const HW = 18;
    const steps = 4;
    const left = [], right = [];
    for (let k = 0; k <= steps; k++) {
      const p = at(r.pts, s, d0 + (d1 - d0) * k / steps);
      const n = Math.hypot(p.tx, p.ty) || 1;
      left.push([p.x - p.ty / n * HW, p.y + p.tx / n * HW]);
      right.push([p.x + p.ty / n * HW, p.y - p.tx / n * HW]);
    }
    const c = at(r.pts, s, (d0 + d1) / 2);
    r.slots.push({ d: P([...left, ...right.reverse()]) + 'Z', x: c.x, y: c.y, tx: c.tx, ty: c.ty, len: d1 - d0 });
  }
  r.sampled = [];
  for (let d = 0; d <= L; d += 22) { const p = at(r.pts, s, d); r.sampled.push([p.x, p.y]); }
  r.mid = at(r.pts, s, L / 2);
}
export const routesOut = routes;

const SLOT = (r, i, phone) => {
  const sl = r.slots[i];
  const col = r.color;
  const fill = CARGO[col].soft;
  const glyphCol = CARGO[col].on;
  const k = 1.42;
  if (col === 'any') {
    return `<path d="${sl.d}" fill="#f8efd6" stroke="${INK}" stroke-width="2.800" stroke-dasharray="8 5"/>`;
  }
  const g = `<g transform="translate(${f1(sl.x)} ${f1(sl.y)}) scale(${k})">${mark(col, glyphCol)}</g>`;
  return `<path d="${sl.d}" fill="${fill}" stroke="${INK}" stroke-width="2.600" stroke-linejoin="round"/>${g}`;
};

// ─── допоміжне: написи ──────────────────────────────────────────────────────────────────────────────
const LABEL_BREAK = { 'Кам\'янець-Подільський': ['Кам\'янець-', 'Подільський'], 'Івано-Франківськ': ['Івано-', 'Франківськ'] };
const ORDER_C = new Set(fx.mock.myTickets.filter((t) => !t.done).flatMap((t) => [t.a, t.b]));
function placeLabels(sizePx, s, vp) {
  // size у пікселях екрана → одиниці сітки: size/s
  const placed = [];
  const cw = .575;
  const allPts = routes.flatMap((r) => r.sampled.map((p) => ({ x: p[0], y: p[1], r })));
  for (let pass = 0; pass < 3; pass++) {
    for (const c of fx.cities) {
      const t = TIER(c.name);
      const fs = sizePx[t] / s;
      const lines = LABEL_BREAK[c.name] || [c.name];
      const w = Math.max(...lines.map((l) => l.length)) * fs * cw, h = lines.length * fs * 1.04;
      const R0 = TOWN_R(c.name) + 2 + (ORDER_C.has(c.name) ? 18 : 0);
      const cand = [
        ['W', -R0 - w, -h / 2, 'end'], ['E', R0, -h / 2, 'start'], ['S', -w / 2, R0 + 2, 'middle'], ['N', -w / 2, -R0 - h - 16, 'middle'],
        ['SE', R0 * .55, R0 * .55, 'start'], ['SW', -w - R0 * .55, R0 * .55, 'end'], ['NE', R0 * .55, -R0 - h, 'start'], ['NW', -w - R0 * .55, -R0 - h, 'end'],
      ];
      let best = null, bv = 1e18;
      for (const [nm, dx, dy, anch] of cand) {
        const X = c.x + dx, Y = c.y + dy;
        let v = nm === 'E' ? 0 : nm === 'W' ? 2 : nm === 'S' ? 3 : nm === 'N' ? 5 : 6;
        for (const p of allPts) if (p.x > X - 12 && p.x < X + w + 12 && p.y > Y - 12 && p.y < Y + h + 12) v += 8;
        for (const o of fx.cities) if (o !== c) {
          const rr = TOWN_R(o.name);
          if (o.x + rr > X && o.x - rr < X + w && o.y + rr > Y && o.y - rr < Y + h) v += 120;
        }
        for (const q of placed) if (q.c !== c && X < q.X + q.w && X + w > q.X && Y < q.Y + q.h && Y + h > q.Y) v += 150;
        if (vp && c.x > vp.x && c.x < vp.x + vp.w && c.y > vp.y && c.y < vp.y + vp.h) {
          if (X < vp.x + 6 || X + w > vp.x + vp.w - 6 || Y < vp.y + 6 || Y + h > vp.y + vp.h - 6) v += 500;
          if (vp.avoid) for (const a of vp.avoid) if (X < a[0] + a[2] && X + w > a[0] && Y < a[1] + a[3] && Y + h > a[1]) v += 300;
        }
        if (v < bv) { bv = v; best = { X, Y, w, h, anch, c, fs, lines }; }
      }
      const idx = placed.findIndex((q) => q.c === c);
      if (idx >= 0) placed[idx] = best; else placed.push(best);
    }
  }
  return placed;
}
const JIT = rng(5);
const jitter = (str, a = 1.2) => Array.from(str).map(() => f1((JIT() - .5) * 2 * a)).join(' ');

function labelSvg(l, fontPx, s) {
  const tier = TIER(l.c.name);
  const lh = l.fs * 1.04;
  const fx0 = l.anch === 'end' ? l.X + l.w : l.anch === 'middle' ? l.X + l.w / 2 : l.X;
  return l.lines.map((t, i) => `<text x="${f1(fx0)}" y="${f1(l.Y + lh * (i + .82))}" text-anchor="${l.anch}" class="cname t${tier}" font-size="${f1(l.fs)}" stroke-width="${f1(l.fs * .3)}">${t.replace(/'/g, '’')}</text>`).join('');
}

// ─── Терен ───────────────────────────────────────────────────────────────────────────────────────────
function terrain(blocked) {
  const r = rng(2024);
  let mtn = '', tree = '', grass = '', mound = '';
  const free = (x, y, d) => !blocked(x, y, d);
  // Карпати
  const arcC = [[110, 1040], [260, 1120], [420, 1240], [570, 1370], [720, 1490], [860, 1540]];
  const pk = [];
  for (let i = 0; i < 260; i++) {
    const t = r() * (arcC.length - 1), a = Math.floor(t), f = t - a;
    const x = arcC[a][0] + (arcC[a + 1][0] - arcC[a][0]) * f + (r() - .5) * 200, y = arcC[a][1] + (arcC[a + 1][1] - arcC[a][1]) * f + (r() - .5) * 200;
    const k = .75 + r() * .55;
    if (!inLand(x, y) || !free(x, y, 30 * k)) continue;
    if (pk.some((q) => Math.hypot(q[0] - x, (q[1] - y) * 1.5) < 52)) continue;
    pk.push([x, y, k]);
  }
  pk.sort((a, b) => a[1] - b[1]);
  for (const [x, y, k] of pk) mtn += peak(x, y, k);
  // Кримські гори
  for (const [x, y, k] of [[2640, 2610, .8], [2720, 2590, .9], [2800, 2570, .95], [2880, 2540, .85], [2960, 2510, .8], [2770, 2525, .7], [2860, 2490, .7]]) if (free(x, y, 26)) mtn += peak(x, y, k * .9);
  // Донецький кряж — пагорби
  for (const [x, y] of [[3560, 1330], [3640, 1400], [3700, 1330], [3590, 1500], [3300, 1560]]) if (free(x, y, 26)) mound += hill(x, y);
  // Полісся — ліс
  for (let i = 0; i < 620 && tree.length < 60000; i++) {
    const x = 500 + r() * 2300, y = 120 + r() * 560;
    if (!inLand(x, y) || y > 280 + 330 * Math.sin((x / 2800) * 3.1) + 100) continue;
    if (x > 2200 && y > 420) continue;
    if (!free(x, y, 28)) continue;
    tree += treeG(x, y, r);
  }
  // Степ — кущики трави
  for (let i = 0; i < 480; i++) {
    const x = 1500 + r() * 2300, y = 1450 + r() * 760;
    if (!inLand(x, y) || !free(x, y, 22)) continue;
    grass += `<path d="M${f1(x)} ${f1(y)}q-6-9-11-11M${f1(x)} ${f1(y)}q-1-11-1-16M${f1(x)} ${f1(y)}q5-9 10-12" />`;
  }
  // кургани зі стовпом
  for (const [x, y] of [[3120, 1680], [2750, 1990], [2120, 1520], [3220, 1100], [3030, 1980]]) if (free(x, y, 32)) mound += kurgan(x, y);
  return `<g class="terrain"><g fill="#3f2a14" fill-opacity=".62" stroke="none">${tree}</g>
    <g fill="none" stroke="#6b4f27" stroke-width="2.200" stroke-linecap="round" opacity=".55">${grass}</g>
    ${mound}${mtn}</g>`;
}
function peak(x, y, k) {
  const w = 34 * k, h = 42 * k;
  return `<g transform="translate(${f1(x)} ${f1(y)})"><path d="M${-w} 0C${-w * .6} ${-h * .3} ${-w * .3} ${-h * .8} 0 ${-h}C${w * .25} ${-h * .8} ${w * .55} ${-h * .3} ${w} 0" fill="#e8d7ac" stroke="#3a2412" stroke-width="2.800" stroke-linejoin="round"/>
    <path d="M0 ${-h}C${w * .25} ${-h * .8} ${w * .55} ${-h * .3} ${w} 0H${w * .1}C${w * .15} ${-h * .4} ${w * .06} ${-h * .7} 0 ${-h}Z" fill="#8f6d3f" fill-opacity=".5"/>
    <path d="M${w * .2} ${-h * .55}L${w * .05} -3M${w * .38} ${-h * .32}L${w * .25} -2M${w * .56} ${-h * .18}L${w * .45} -2" stroke="#3a2412" stroke-width="1.700"/></g>`;
}
function hill(x, y) { return `<path d="M${x - 34} ${y}Q${x} ${y - 34} ${x + 34} ${y}" fill="#e6d4a6" stroke="#4a3018" stroke-width="2.500"/><path d="M${x + 8} ${y - 14}l-5 12M${x + 18} ${y - 8}l-4 8" stroke="#4a3018" stroke-width="1.700"/>`; }
function kurgan(x, y) { return `<g transform="translate(${x} ${y})"><path d="M-30 0Q0-22 30 0" fill="#e6d4a6" stroke="#4a3018" stroke-width="2.300"/><path d="M-3.500-13V-27A3.500 3.500 0 0 1 3.500-27V-13Z" fill="#7a5a33" stroke="#2b1a0d" stroke-width="1.700"/><path d="M10-7l-4 6M18-4l-3 4" stroke="#4a3018" stroke-width="1.500"/></g>`; }
function treeG(x, y, r) {
  const k = .85 + r() * .5;
  const conifer = r() < .62;
  if (conifer) return `<path transform="translate(${f1(x)} ${f1(y)}) scale(${f1(k)})" d="M-1.500 0V-5H-9L-4-13H-7.500L-3-21H-5L0-30 5-21H3L7.500-13H4L9-5H1.500V0Z"/>`;
  return `<path transform="translate(${f1(x)} ${f1(y)}) scale(${f1(k)})" d="M-1.800 0V-7C-11-7-13-16-8-20-9-27 2-30 6-24 13-23 13-11 7-8.500L1.800-7V0Z"/>`;
}

// ─── Складання SVG мапи ─────────────────────────────────────────────────────────────────────────────
export function buildMap({ s, vb, sizes, vp, phone = false, deco = {}, selectedBanner = {}, id = 'm' }) {
  const labels = placeLabels(sizes, s, vp);
  const allPts = routes.flatMap((r) => r.sampled);
  const blocked = (x, y, d) => {
    for (const p of allPts) if (Math.abs(p[0] - x) < d + 20 && Math.abs(p[1] - y) < d + 20) return true;
    for (const c of fx.cities) if (Math.hypot(c.x - x, c.y - y) < d + TOWN_R(c.name) + 6) return true;
    for (const l of labels) if (x > l.X - d && x < l.X + l.w + d && y > l.Y - d - 6 && y < l.Y + l.h + d + 8) return true;
    return false;
  };

  const tints = ['#c9a24a', '#8fa35a', '#c77e55', '#7f9a8a', '#b98c6a'];
  // простий 5-кольоровий розподіл по індексу (ледь помітні акварельні заливки)
  const regionFill = regionD.map((r, i) => `<path d="${r.d}" fill="${tints[(i * 7 + 2) % 5]}" fill-opacity=".17"/>`).join('');

  const waterlines = (() => {
    const steps = [130, 96, 68, 44, 25, 10];
    let o = '';
    for (const d of steps) {
      o += `<path d="${outlineD}${foreignCoastD}" fill="none" stroke="#54665a" stroke-opacity="${(.24 + (130 - d) / 130 * .42).toFixed(2)}" stroke-width="${2 * d + 3.2}" stroke-linejoin="round"/>`;
      o += `<path d="${outlineD}${foreignCoastD}" fill="none" stroke="#bdcab2" stroke-width="${2 * d - 1}" stroke-linejoin="round"/>`;
    }
    return o;
  })();

  // ряботиння хвиль
  const rr = rng(99);
  let ripples = '';
  for (let i = 0; i < 90; i++) {
    const x = 1400 + rr() * 2800, y = 2120 + rr() * 650;
    if (inLand(x, y)) continue;
    const seaLat = x < 2400 || y > 2300 || x > 3000;
    if (!seaLat) continue;
    ripples += `M${f1(x)} ${f1(y)}q9-7 18 0t18 0`;
  }

  const seaFill = phone ? '#bdcab2' : '#bdcab2';

  // ── румби від рожі вітрів ──
  const rose = deco.rose;
  let rhumbs = '';
  if (rose) for (let i = 0; i < 16; i++) { const A = i * 22.5 * Math.PI / 180; rhumbs += `M${rose[0]} ${rose[1]}L${f1(rose[0] + 4200 * Math.cos(A))} ${f1(rose[1] + 4200 * Math.sin(A))}`; }

  // ── шляхи ──
  const selId = fx.mock.selectedRoute;
  let roadLines = '', slotsFree = '', slotsTaken = '', slotHalos = '';
  for (const r of routes) {
    if (owner[r.id] != null) {
      const col = fx.mock.players[owner[r.id]].color;
      for (const sl of r.slots) {
        let ang = Math.atan2(sl.ty, sl.tx) * 180 / Math.PI;
        if (ang > 90 || ang < -90) ang += 180;
        const flip = (r.id % 2);
        const hw = Math.min(sl.len * .42, 58);
        slotsTaken += `<g transform="translate(${f1(sl.x)} ${f1(sl.y)}) rotate(${f1(ang)}) ${flip ? 'scale(-1 1)' : ''}">${cart(hw, col, PMARK[owner[r.id]])}</g>`;
      }
      // пляма під возами — колія
      roadLines += `<path d="${P(r.pts)}" fill="none" stroke="${col}" stroke-opacity=".55" stroke-width="14"/>`;
    } else if (r.id !== selId) {
      roadLines += `<path d="${P(r.pts)}" fill="none" stroke="${SEPIA}" stroke-opacity=".5" stroke-width="2.600" stroke-dasharray="3 9" stroke-linecap="round"/>`;
      r.slots.forEach((sl, i) => {
        slotHalos += `<path d="${sl.d}" fill="#f6ebca" stroke="#f6ebca" stroke-width="9" stroke-opacity=".9" stroke-linejoin="round"/>`;
        slotsFree += SLOT(r, i);
      });
    }
  }
  // обраний шлях — золотий контур поверх усього
  const sel = routes[selId];
  const selHalo = sel.slots.map((sl) => `<path d="${sl.d}" fill="#ffe28a" stroke="#ffcf3f" stroke-width="26" stroke-linejoin="round"/>`).join('')
    + sel.slots.map((sl) => `<path d="${sl.d}" fill="#fff6cf" stroke="#fff6cf" stroke-width="10" stroke-linejoin="round"/>`).join('');
  const selSlots = sel.slots.map((_, i) => SLOT(sel, i)).join('');
  // банер ціни на обраному шляху
  const bn = selectedBanner;
  const m = sel.mid;
  const bw = bn.w || 330, bh = bn.h || 74;
  const bx = m.x + (bn.dx || 0), by = m.y + (bn.dy || -86);
  const selBanner = `<g transform="translate(${f1(bx)} ${f1(by)}) rotate(${bn.rot ?? 15})">
      <path d="M${-bw / 2} ${-bh / 2}H${bw / 2}L${bw / 2 + 26} ${-bh / 2 + 12}L${bw / 2 + 8} 0L${bw / 2 + 26} ${bh / 2 - 12}L${bw / 2} ${bh / 2}H${-bw / 2}L${-bw / 2 - 26} ${bh / 2 - 12}L${-bw / 2 - 8} 0L${-bw / 2 - 26} ${-bh / 2 + 12}Z" fill="#fff6d8" stroke="${INK}" stroke-width="3.400" stroke-linejoin="round"/>
      <g transform="translate(${-bw / 2 + 40} 0) scale(2.100)"><circle r="12.500" fill="#2c7a3a" stroke="${INK}" stroke-width="1.300"/>${mark('green', '#fff7e6')}</g>
      <text x="${-bw / 2 + 78}" y="${bh * .19}" class="selcost" font-size="${bn.fs || 49}">6 × збіжжя</text>
      <path d="M${bw / 2 - 118} ${-bh / 2 + 8}V${bh / 2 - 8}" stroke="${INK}" stroke-width="2.200" stroke-dasharray="3 5" opacity=".7"/>
      ${bn.noPts ? '' : `<text x="${bw / 2 - 10}" y="${bh * .19}" class="selpts" font-size="${bn.fs || 49}" text-anchor="end">+15</text>`}
    </g>`;

  // ── міста ──
  const orderCities = new Set(fx.mock.myTickets.filter((t) => !t.done).flatMap((t) => [t.a, t.b]));
  let towns = '';
  for (const c of fx.cities) {
    const rad = TOWN_R(c.name);
    const onSel = c.name === sel.a || c.name === sel.b;
    towns += `<g transform="translate(${c.x} ${c.y})">
      ${onSel ? `<circle r="${rad + 14}" fill="#ffd95c" stroke="#ffcf3f" stroke-width="6"/>` : ''}
      <circle r="${rad}" fill="#f6ebc9" stroke="${INK}" stroke-width="2.800"/>
      <circle r="${rad - 6}" fill="none" stroke="${INK}" stroke-opacity=".35" stroke-width="1.100"/>
      <g transform="scale(${(rad / 44).toFixed(2)})">${TOWN[kindOf(c.name)]()}</g></g>`;
    if (orderCities.has(c.name)) {
      // кола червоним чорнилом: міста моїх замовлень
      const rr2 = rad + (onSel ? 26 : 16);
      towns += `<path transform="translate(${c.x} ${c.y}) rotate(${(c.x % 90) - 45})" d="M${-rr2} 2C${-rr2} ${-rr2 * .62} ${-rr2 * .6} ${-rr2} 2 ${-rr2}C${rr2 * .66} ${-rr2} ${rr2 + 2} ${-rr2 * .55} ${rr2} 3C${rr2 - 2} ${rr2 * .64} ${rr2 * .6} ${rr2 + 2} -3 ${rr2}C${-rr2 * .7} ${rr2} ${-rr2 - 3} ${rr2 * .5} ${-rr2 - 6} -6" fill="none" stroke="#b3261e" stroke-width="5" stroke-linecap="round"/>`;
    }
  }
  const lblSvg = labels.map((l) => labelSvg(l)).join('');

  const landNames = (deco.lands || []).map(([t, x, y, rot, size]) => `<text transform="translate(${x} ${y}) rotate(${rot || 0})" class="land" font-size="${size || 44}" letter-spacing="${(size || 44) * .28}">${t}</text>`).join('');
  const foreign = (deco.foreign || []).map(([t, x, y, rot, size]) => `<text transform="translate(${x} ${y}) rotate(${rot || 0})" class="foreign" font-size="${size || 54}" letter-spacing="${(size || 54) * .22}">${t}</text>`).join('');
  const seaNames = (deco.seas || []).map(([t, x, y, rot, size]) => `<text transform="translate(${x} ${y}) rotate(${rot || 0})" class="seaname" font-size="${size || 80}" letter-spacing="${(size || 80) * .3}" rotate="${jitter(t, 1.6)}">${t}</text>`).join('');
  const rivNames = (deco.rivers || []).map(([t, x, y, rot, size]) => `<text transform="translate(${x} ${y}) rotate(${rot || 0})" class="rivname" font-size="${size || 38}" stroke-width="${(size || 38) * .22}">${t}</text>`).join('');
  const creatures = (deco.creatures || []).map(([art, x, y, k, flip]) => `<g transform="translate(${x} ${y}) scale(${flip ? -k : k} ${k})">${{ serpent, whale, chaika }[art]}</g>`).join('');

  return `<svg class="mapsvg" viewBox="${vb.join(' ')}" xmlns="http://www.w3.org/2000/svg" aria-label="Мапа чумацьких шляхів України">
    <defs>
      <clipPath id="${id}land"><path d="${landD}"/></clipPath>
      <clipPath id="${id}sea"><path d="${seaD}" clip-rule="evenodd"/></clipPath>
      <filter id="${id}rough" x="-2%" y="-2%" width="104%" height="104%"><feTurbulence type="fractalNoise" baseFrequency=".035" numOctaves="2" seed="7" result="n"/><feDisplacementMap in="SourceGraphic" in2="n" scale="5"/></filter>
      <filter id="${id}blur"><feGaussianBlur stdDeviation="14"/></filter>
      <pattern id="${id}hatch" width="20" height="20" patternUnits="userSpaceOnUse" patternTransform="rotate(38)"><path d="M0 4H20" stroke="#6d4f2a" stroke-width="1.900" stroke-opacity=".3"/></pattern>
      <pattern id="${id}hatch2" width="13" height="13" patternUnits="userSpaceOnUse" patternTransform="rotate(-48)"><path d="M0 3H13" stroke="#6d4f2a" stroke-width="2" stroke-opacity=".36"/></pattern>
    </defs>
    <!-- суша за межами -->
    <rect x="-1500" y="-800" width="8000" height="5000" fill="#d9c493"/>
    <rect x="-1500" y="-800" width="8000" height="5000" fill="url(#${id}hatch)"/>
    <!-- море -->
    <path d="${seaD}" fill="${seaFill}"/>
    <g clip-path="url(#${id}sea)">${waterlines}
      <path d="${ripples}" fill="none" stroke="#54665a" stroke-opacity=".5" stroke-width="2.200" stroke-linecap="round"/>
    </g>
    ${rhumbs ? `<path d="${rhumbs}" fill="none" stroke="#7a3322" stroke-opacity=".2" stroke-width="2"/>` : ''}
    <path d="${foreignCoastD}" fill="none" stroke="${INK}" stroke-width="3.400" filter="url(#${id}rough)"/>
    <!-- штрих за контуром -->
    <path d="${outlineD}" fill="none" stroke="url(#${id}hatch2)" stroke-width="64"/>
    <!-- суходіл -->
    <path d="${landD}" fill="#f1e3bd"/>
    <g>${regionFill}</g>
    <g clip-path="url(#${id}land)"><path d="${outlineD}" fill="none" stroke="#b56a35" stroke-opacity=".55" stroke-width="40" filter="url(#${id}blur)"/></g>
    <path d="${bordersD}" fill="none" stroke="#6d4f2a" stroke-opacity=".5" stroke-width="2.400" stroke-dasharray="9 7 2 7"/>
    <g filter="url(#${id}rough)">
      <path d="${riverOtherD}" fill="none" stroke="#3d6d7e" stroke-width="5.500" stroke-linecap="round" stroke-linejoin="round" stroke-opacity=".75"/>
      <path d="${riverBigD}" fill="none" stroke="#2f5a68" stroke-width="15" stroke-linecap="round" stroke-linejoin="round" stroke-opacity=".9"/>
      <path d="${riverBigD}" fill="none" stroke="#bdd0c6" stroke-width="8" stroke-linecap="round" stroke-linejoin="round"/>
      <path d="${outlineD}" fill="none" stroke="${INK}" stroke-width="4.600" stroke-linejoin="round"/>
      ${terrain(blocked)}
    </g>
    <path d="M0 0" />
    ${landNames}${foreign}${seaNames}${rivNames}${creatures}
    ${deco.extra || ''}
    <!-- шляхи -->
    <g>${roadLines}</g>
    <g>${slotHalos}</g>
    <g>${slotsFree}</g>
    <g>${slotsTaken}</g>
    <g>${selHalo}${selSlots}</g>
    <!-- міста й написи -->
    <g>${towns}</g>
    <g>${lblSvg}</g>
    ${selBanner}
  </svg>`;
}
export { compass, kindOf, TOWN_R, city };

// ─── Підказки до замовлень: найкоротший шлях із зайнятими мною (0), чужі — закриті ─────────────────
export function orderHint(a, b) {
  const adj = {};
  for (const r of routes) {
    const o = owner[r.id];
    if (o != null && o !== 0) continue;
    (adj[r.a] = adj[r.a] || []).push([r.b, o === 0 ? 0 : r.len, r]);
    (adj[r.b] = adj[r.b] || []).push([r.a, o === 0 ? 0 : r.len, r]);
  }
  const dist = { [a]: 0 }, prev = {}, seen = new Set();
  while (true) {
    let u = null, bd = 1e9;
    for (const k in dist) if (!seen.has(k) && dist[k] < bd) { bd = dist[k]; u = k; }
    if (!u) break; seen.add(u);
    for (const [v, w, r] of adj[u] || []) if (dist[u] + w < (dist[v] ?? 1e9)) { dist[v] = dist[u] + w; prev[v] = [u, r]; }
  }
  const chain = []; let cur = b;
  while (prev[cur]) { chain.unshift(prev[cur][1]); cur = prev[cur][0]; }
  return { wagons: dist[b], left: chain.filter((r) => owner[r.id] !== 0).length, done: chain.filter((r) => owner[r.id] === 0).length, chain };
}
