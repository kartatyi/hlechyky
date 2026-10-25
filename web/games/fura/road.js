// Дорога 2.0 (STYLE.md §5): дорога будується функцією від «ребра графа з атрибутами» — клас, смуги, ширина, oneway,
// міст, пара проспектів — і не знає, звідки граф: край (kray.js) чи Житомир з OSM (fromOsm нижче).
//
// Два кроки:
//   planRoads(net)  — перехрестя (обрізки ребер, латки з радіусами), площина кожного перехрестя й підгонка висот
//                     ребер до неї (до того, як рельєф «вріжуть» під дороги: фізика й око бачать той самий профіль);
//   drawRoads(...)  — геометрія в пакет по чанках (batch.js): смуги, узбіччя, розмітка ДСТУ, латки перехресть,
//                     розширення з'їздів, відбійники (+ колайдери, які створює викликач), мости з парапетами.
//
// Осі ті самі, що в truck.js: x, z — план, y — вгору. Курс (tx, tz); ПРАВОРУЧ від курсу — (−tz, tx) (рука водія,
// що стоїть уздовж +y). Зсув o > 0 — праворуч від напрямку a → b. Рух правосторонній.
//
// Ребро net.edges[i]:
//   { id, cls: 'highway'|'village'|'dirt'|'yard'|'street', a, b (id вершин; b може не бути — глухий кінець),
//     pts: [{x, z, y, tx, tz, s}] (згладжена вісь з кроком ~4 м, s — від a),
//     lanes?, width? (проїзна частина, м), shoulder? (узбіччя, м), oneway?, barrier?: 'auto'|'none'|'both',
//     bridges?: [[s0, s1]] (настил), shift? (зсув осі проїзної частини від осі ребра — для пар проспектів),
//     median? (ширина розділювача, коли ребро — половина пари), flareB?: {len, extra} (розширення на кінці b — заїзд
//     у двір), endB? (s, де стрічка закінчується, якщо раніше за кінець), rank? (вага в перехресті) }
// Вершина net.nodes[id]: { x, z }.

import { PAL } from './materials.js';

export const LIFT = 0.04;          // асфальт над профілем (рельєф під ним — на −0,02; решту робить polygonOffset)
const MARK = LIFT + 0.008;         // розмітка над асфальтом

/** Класи — STYLE §5.1 (ДБН В.2.3-4 з пам'яті, округлено). Напівширини — від осі. */
export const CLASSES = {
  // траса II кат.: 2 × 3,75, укріплена смуга, щебеневе узбіччя, осьова 1.5 (3/9), крайові 1.2
  highway: { lane: 3.75, lanes: 2, paved: 0.75, shoulder: 2.5, shoulderCol: 'gravel', asphalt: 'asphalt', center: 'dash', edge: true, barrier: 'auto', R: 15, rank: 3 },
  // обласна/сільська: 2 × 3,5, трав'яне узбіччя, осьова блідо 3/9, без відбійників
  village: { lane: 3.5, lanes: 2, paved: 0, shoulder: 1.5, shoulderCol: 'verge', asphalt: 'asphaltOld', center: 'faded', edge: false, barrier: 'none', R: 12, rank: 2 },
  // міська вулиця: бордюр і тротуар
  street: { lane: 3.5, lanes: 2, paved: 0, curb: 0.15, sidewalk: 2.25, asphalt: 'asphalt', center: 'dash', edge: false, barrier: 'none', R: 10, rank: 2 },
  // ґрунтовка: колії, без розмітки
  dirt: { lane: 3, lanes: 2, paved: 0, shoulder: 0.8, shoulderCol: 'verge', surface: 'dirt', ruts: true, center: 'none', edge: false, barrier: 'none', R: 10, rank: 1 },
  // під'їзд до двору: одна широка смуга, R 15 — щоб фура з причепом вписалась
  yard: { lane: 4.5, lanes: 2, paved: 0, shoulder: 1.0, shoulderCol: 'gravel', asphalt: 'asphaltOld', center: 'none', edge: false, barrier: 'none', R: 15, rank: 0 },
};

const PATTERN = { dash: [3, 9], approach: [6, 2], solid: null, faded: [3, 9], mouth: [1, 3], lane: [3, 9] };

// ---------------------------------------------------------------- лінії
/** Точка полілінії на відстані s (лінійно між зразками): {x, z, y, tx, tz, s}. */
export function at(pts, s) {
  const n = pts.length;
  if (s <= pts[0].s) return pts[0];
  if (s >= pts[n - 1].s) return pts[n - 1];
  let lo = 0, hi = n - 1;
  while (hi - lo > 1) { const m = (lo + hi) >> 1; if (pts[m].s <= s) lo = m; else hi = m; }
  const a = pts[lo], b = pts[hi], t = (s - a.s) / (b.s - a.s || 1);
  let tx = a.tx + (b.tx - a.tx) * t, tz = a.tz + (b.tz - a.tz) * t;
  const l = Math.hypot(tx, tz) || 1;
  return { x: a.x + (b.x - a.x) * t, z: a.z + (b.z - a.z) * t, y: a.y + (b.y - a.y) * t, tx: tx / l, tz: tz / l, s };
}
/** Зразки між s0 і s1 включно з точними кінцями. */
function stations(pts, s0, s1) {
  const out = [at(pts, s0)];
  for (const p of pts) if (p.s > s0 + 0.05 && p.s < s1 - 0.05) out.push(p);
  out.push(at(pts, s1));
  return out;
}
/** Полілінія [{x, z, y}] → зразки з курсом і s (для розмітки по латці перехрестя); y — поверхня латки (з LIFT). */
function toStations(poly) {
  let s = 0;
  poly = poly.map((p) => ({ x: p.x, z: p.z, y: p.y - LIFT }));
  return poly.map((p, i) => {
    const a = poly[Math.max(0, i - 1)], b = poly[Math.min(poly.length - 1, i + 1)];
    const l = Math.hypot(b.x - a.x, b.z - a.z) || 1;
    if (i) s += Math.hypot(p.x - poly[i - 1].x, p.z - poly[i - 1].z);
    return { x: p.x, z: p.z, y: p.y, tx: (b.x - a.x) / l, tz: (b.z - a.z) / l, s };
  });
}
const side = (p, o, dy, tilt) => {
  const x = p.x - p.tz * o, z = p.z + p.tx * o;
  return { x, y: p.y + dy + (tilt ? tilt(p, x, z) : 0), z };
};
const smooth = (a, b, x) => { const t = Math.max(0, Math.min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

// ---------------------------------------------------------------- переріз
/**
 * Переріз ребра: напівширини проїзної частини праворуч/ліворуч від осі (wr, wl), корідор із узбіччям (cw), смуги,
 * кольори й розмітка. Ширина з атрибута перекриває клас (смуги = ширина / кількість), укріплена смуга — залишок.
 */
export function section(e) {
  const C = CLASSES[e.cls] || CLASSES.village;
  const lanes = e.lanes || C.lanes;
  let lane = C.lane, paved = C.paved || 0;
  if (e.width) {
    if (paved || C.edge) paved = Math.max(0, (e.width - lane * lanes) / 2);
    else lane = e.width / lanes;
  }
  const W = lane * lanes + 2 * paved, shift = e.shift || 0;
  const sh = e.shoulder != null ? e.shoulder : (C.shoulder || 0);
  const walk = C.curb ? C.curb + (C.sidewalk || 0) : 0;
  const wr = W / 2 + shift, wl = W / 2 - shift;
  return {
    C, lanes, lane, paved, W, shift, sh, walk, wr, wl, hw: Math.max(wr, wl),
    cw: Math.max(wr, wl) + sh + walk,
    oneway: !!e.oneway, median: e.median || 0,
    asphalt: PAL[C.asphalt] || PAL.asphalt, surface: C.surface === 'dirt' ? PAL.dirt : null,
    shoulderCol: PAL[C.shoulderCol] || PAL.verge,
    center: e.oneway ? 'none' : C.center, edge: C.edge, rank: e.rank != null ? e.rank : C.rank, R: C.R,
    barrier: e.barrier || C.barrier,
  };
}

// ---------------------------------------------------------------- план: перехрестя
/**
 * Перехрестя: для кожної вершини з ≥ 2 кінцями ребер — обрізки (де закінчується стрічка ребра), латка (віяло від
 * центру), кути із заокругленням R (fillet, STYLE §5.3), головна пара (найвищий клас і найпряміше) і площина латки.
 * Висоти ребер біля вершини підганяються під площину — латка рівна, а рельєф (і фізика) під нею без сходинок.
 */
export function planRoads(net) {
  const edges = net.edges;
  for (const e of edges) {
    e.sec = section(e);
    e.L = e.pts[e.pts.length - 1].s;
    e.trimA = 0; e.trimB = 0;
    if (e.endB != null) e.trimB = Math.max(0, e.L - e.endB);
  }
  const arms = new Map();
  const arm = (id, e, end) => { if (id == null || !net.nodes[id]) return; if (!arms.has(id)) arms.set(id, []); arms.get(id).push({ e, end }); };
  for (const e of edges) { arm(e.a, e, 'a'); arm(e.b, e, 'b'); }
  const junctions = [];
  for (const [id, list] of arms) {
    if (list.length < 2) continue;
    junctions.push(junction(id, net.nodes[id], list));
  }
  // підгонка висот ребер до площини перехрестя (до обрізку — уся, далі плавно за 15 м): вісь — на площину, а
  // поперек — нахил площини (e.tilt): латка, стрічка рукава й рельєф під ними — одна поверхня без сходинок
  for (const e of edges) e.tilts = [];
  for (const J of junctions) {
    for (const A of J.arms) {
      const e = A.e, reach = A.trim + BLEND;
      e.tilts.push({ end: A.end, trim: A.trim, plane: J.plane });
      for (const p of e.pts) {
        const t = A.end === 'a' ? p.s : e.L - p.s;
        if (t > reach) continue;
        p.y += (J.plane(p.x, p.z) - p.y) * (1 - smooth(A.trim, reach, t));
      }
    }
  }
  for (const e of edges) {
    const T = e.tilts;
    /** Поправка висоти точки (x, z) збоку від осі в точці p (s): нахил площин перехресть на кінцях ребра. */
    e.tilt = !T.length ? () => 0 : (p, x, z) => {
      let d = 0;
      for (const q of T) {
        const t = q.end === 'a' ? p.s : e.L - p.s;
        if (t >= q.trim + BLEND) continue;
        d += (q.plane(x, z) - q.plane(p.x, p.z)) * (1 - smooth(q.trim, q.trim + BLEND, t));
      }
      return d;
    };
  }
  return { edges, junctions, nodes: net.nodes };
}
const BLEND = 15;              // м за обрізком, на яких ребро переходить з площини перехрестя на свій профіль

/** Площа вершини: віяло латки, обрізки рукавів, кути. */
function junction(id, node, list) {
  const arms = list.map(({ e, end }) => {
    const L = e.L, probe = Math.min(12, L / 3);
    const p0 = end === 'a' ? e.pts[0] : e.pts[e.pts.length - 1];
    const q = at(e.pts, end === 'a' ? probe : L - probe);
    const l = Math.hypot(q.x - node.x, q.z - node.z) || 1;
    const u = { x: (q.x - node.x) / l, z: (q.z - node.z) / l };
    // ліва/права напівширина в напрямку «від вершини»: для кінця b напрям протилежний a → b
    const wR = end === 'a' ? e.sec.wr : e.sec.wl, wL = end === 'a' ? e.sec.wl : e.sec.wr;
    return { e, end, u, yaw: Math.atan2(u.x, u.z), wR, wL, rank: e.sec.rank, y0: p0.y, trim: 2 };
  });
  arms.sort((a, b) => a.yaw - b.yaw);          // за зростанням курсу = проти годинникової (наліво)
  const n = arms.length;
  // головна пара: найвищі класи, що йдуть майже навпроти
  let main = null, best = -Infinity;
  for (let i = 0; i < n; i++) for (let j = i + 1; j < n; j++) {
    const d = arms[i].u.x * arms[j].u.x + arms[i].u.z * arms[j].u.z;
    if (d > -0.5) continue;
    const sc = (arms[i].rank + arms[j].rank) * 10 - d;
    if (sc > best) { best = sc; main = [i, j]; }
  }
  const isMain = (k) => main && (k === main[0] || k === main[1]);
  const rankMain = main ? Math.min(arms[main[0]].rank, arms[main[1]].rank) : -1;
  // кути між сусідами (i → j проти годинникової): лівий край i і правий край j
  const corners = [];
  for (let i = 0; i < n; i++) {
    const j = (i + 1) % n, A = arms[i], Bm = arms[j];
    let phi = Bm.yaw - A.yaw; if (j === 0) phi += 2 * Math.PI;
    if (n === 1) phi = 2 * Math.PI;
    const straight = (isMain(i) && isMain(j)) || phi > 2.97;           // > 170° — без радіуса
    const R = Math.max(A.e.sec.R, Bm.e.sec.R);
    let c = { i, j, phi, straight, R };
    if (!straight) {
      // центр дуги — на R від обох країв: wL_i + R ліворуч від i, wR_j + R праворуч від j
      const Li = { x: A.u.z, z: -A.u.x }, Rj = { x: -Bm.u.z, z: Bm.u.x };      // ліво i, право j (у площині)
      const dx = Rj.x * (Bm.wR + R) - Li.x * (A.wL + R), dz = Rj.z * (Bm.wR + R) - Li.z * (A.wL + R);
      const det = A.u.x * -Bm.u.z - A.u.z * -Bm.u.x;
      if (Math.abs(det) < 1e-3) c.straight = true;
      else {
        let t1 = (dx * -Bm.u.z - dz * -Bm.u.x) / det, t2 = (A.u.x * dz - A.u.z * dx) / det;
        t1 = Math.max(0.5, Math.min(40, t1)); t2 = Math.max(0.5, Math.min(40, t2));
        c.t1 = t1; c.t2 = t2;
        c.C = { x: node.x + Li.x * (A.wL + R) + A.u.x * t1, z: node.z + Li.z * (A.wL + R) + A.u.z * t1 };
        A.trim = Math.max(A.trim, t1); Bm.trim = Math.max(Bm.trim, t2);
      }
    }
    corners.push(c);
  }
  for (const A of arms) {
    A.trim = Math.min(A.trim, A.e.L * 0.45);
    if (A.end === 'a') A.e.trimA = Math.max(A.e.trimA, A.trim); else A.e.trimB = Math.max(A.e.trimB, A.trim);
    A.minor = main && !isMain(arms.indexOf(A)) && A.rank < rankMain;
  }
  // площина: уздовж головної пари (її ухил), без пари — рівна на висоті вершини
  let plane;
  const y0 = arms.reduce((s, A) => s + A.y0, 0) / n;
  if (main) {
    const A = arms[main[0]], Bm = arms[main[1]];
    const pa = at(A.e.pts, A.end === 'a' ? A.trim : A.e.L - A.trim), pb = at(Bm.e.pts, Bm.end === 'a' ? Bm.trim : Bm.e.L - Bm.trim);
    const vx = pb.x - pa.x, vz = pb.z - pa.z, v2 = vx * vx + vz * vz || 1, dy = pb.y - pa.y;
    plane = (x, z) => pa.y + dy * ((x - pa.x) * vx + (z - pa.z) * vz) / v2;
  } else plane = () => y0;
  return { id, node, arms, corners, main, plane };
}

// ---------------------------------------------------------------- малювання
/**
 * Уся мережа в пакет. opts: { ground0(x, z) — рельєф до доріг (насипи → відбійники), ground(x, z) — рельєф після
 * (опори й устої мостів) }. Повертає { guards: [{x, y, z, yaw, pitch, hx, hy, hz}] } — колайдери відбійників.
 */
export function drawRoads(THREE, plan, batch, opts = {}) {
  const out = { guards: [], stats: { guardM: 0, bridges: 0, junctions: plan.junctions.length } };
  const col = (hex, f = 1) => new THREE.Color(hex).multiplyScalar(f);
  const J = new Map();          // вершина → перехрестя (для розмітки підходів)
  for (const j of plan.junctions) J.set(j.id, j);

  for (const e of plan.edges) {
    const s0 = e.trimA, s1 = e.L - e.trimB;
    if (s1 - s0 < 0.5) continue;
    ribbon(e, s0, s1);
    marks(e, s0, s1);
    for (const [b0, b1] of e.bridges || []) bridge(e, b0, b1);
    guardrails(e, s0, s1);
  }
  for (const j of plan.junctions) patch(j);
  return out;

  // ---- стрічка: проїзна частина смугами (колії темніші), узбіччя; на мосту — без узбіч
  function ribbon(e, s0, s1) {
    const S = e.sec, st = stations(e.pts, s0, s1), tl = e.tilt;
    const asph = S.surface || S.asphalt;
    const bands = [];              // [o0, o1, dy0, dy1, key, color]
    const c0 = S.shift - S.W / 2;                                       // лівий край проїзної частини
    // колії: у кожній смузі два сліди коліс ±0,9 м від центру смуги
    const trackCol = S.surface ? col(PAL.rut) : col(asph, 0.92), baseCol = col(asph), midCol = S.surface ? col(PAL.dirt, 1.06) : col(asph, 0.97);
    let o = c0;
    const push = (o1, c) => { if (o1 > o + 0.01) bands.push([o, o1, LIFT, LIFT, 'road', c]); o = Math.max(o, o1); };
    if (S.paved) push(c0 + S.paved, col(asph, 1.05));
    for (let k = 0; k < S.lanes; k++) {
      const lc = c0 + S.paved + S.lane * (k + 0.5);
      push(lc - 1.25, baseCol); push(lc - 0.6, trackCol); push(lc + 0.6, midCol); push(lc + 1.25, trackCol);
      push(c0 + S.paved + S.lane * (k + 1), baseCol);
    }
    push(c0 + S.W, col(asph, 1.05));
    // узбіччя / бордюр із тротуаром
    const outer = [];
    if (S.walk) {
      const cb = S.C.curb;
      outer.push([0, 0.001, LIFT, LIFT + cb, 'matte', col(PAL.curb)], [0.001, cb, LIFT + cb, LIFT + cb, 'matte', col(PAL.curb)],
        [cb, S.walk, LIFT + cb, LIFT + cb, 'road', col(PAL.asphaltOld)]);
    } else if (S.sh > 0) outer.push([0, S.sh, LIFT - 0.004, LIFT - 0.018, 'road', col(S.shoulderCol)]);
    const onBridge = (s) => (e.bridges || []).some(([b0, b1]) => s >= b0 - 0.1 && s <= b1 + 0.1);
    const inner = S.median && S.shift ? -Math.sign(S.shift) : 0;
    const flare = e.flareB;
    // розширення з'їзду: краї проїзної частини розходяться дугою (як заокруглення R = len), праворуч і ліворуч — свої
    const ext = (s, sg) => {
      if (!flare) return 0;
      const t = (s - (e.L - e.trimB - flare.len)) / flare.len;
      const x = sg > 0 ? (flare.extraR ?? flare.extra) : (flare.extraL ?? flare.extra);
      return t <= 0 ? 0 : x * (1 - Math.sqrt(Math.max(0, 1 - Math.min(1, t) ** 2)));
    };
    const map = (p, oo) => {
      const c = S.shift, h = S.W / 2, d = oo - c, x = ext(p.s, Math.sign(d));
      if (!x) return oo;
      return Math.abs(d) <= h ? c + d * (h + x) / h : c + d + Math.sign(d) * x;
    };
    for (let i = 1; i < st.length; i++) {
      const a = st[i - 1], b = st[i];
      for (const [o0, o1, d0, d1, key, c] of bands) {
        batch.quad(key, side(a, map(a, o0), d0, tl), side(b, map(b, o0), d0, tl), side(b, map(b, o1), d1, tl), side(a, map(a, o1), d1, tl), c);
      }
      if (onBridge((a.s + b.s) / 2)) continue;
      for (const sg of [1, -1]) {
        if (sg === inner) continue;            // половина пари: з боку розділювача — ні узбіччя, ні тротуару
        const edgeO = sg > 0 ? S.shift + S.W / 2 : S.shift - S.W / 2;
        for (const [q0, q1, d0, d1, key, c] of outer) {
          const A0 = map(a, edgeO) + sg * q0, A1 = map(a, edgeO) + sg * q1, B0 = map(b, edgeO) + sg * q0, B1 = map(b, edgeO) + sg * q1;
          batch.quad(key, side(a, A0, d0, tl), side(b, B0, d0, tl), side(b, B1, d1, tl), side(a, A1, d1, tl), c);
        }
      }
    }
    e._map = map;
  }

  // ---- розмітка ребра
  function marks(e, s0, s1) {
    const S = e.sec, st = stations(e.pts, s0, s1), map = e._map || ((p, o) => o), tl = e.tilt;
    if (S.edge) for (const sg of [1, -1]) {
      const o = S.shift + sg * (S.lane * S.lanes / 2 + 0.1 + 0.075);
      line(st, (p) => map(p, o), 0.15, null, PAL.lineWhite, s0, s1, tl);
    }
    // смуги одного напрямку — переривчаста 3/9
    const perDir = S.oneway ? S.lanes : S.lanes / 2;
    if (perDir > 1 || S.oneway) {
      const c0 = S.shift - S.lane * S.lanes / 2;
      for (let k = 1; k < S.lanes; k++) {
        if (!S.oneway && k === S.lanes / 2) continue;
        line(st, (p) => map(p, c0 + S.lane * k), 0.12, PATTERN.lane, PAL.lineWhite, s0, s1, tl);
      }
    }
    if (S.center === 'none') return;
    // осьова: за класом; перед перехрестям, де ребро головне, — 1.6 (6/2) на 60 м; де другорядне — 1.1 на 20 м
    const spans = [];
    const endKind = (node, t) => {
      const j = J.get(node);
      if (!j) return null;
      const A = j.arms.find((x) => x.e === e && x.end === t);
      if (!A) return null;
      if (A.minor) return ['solid', 20];
      if (j.main && j.arms.some((x) => x.minor) && S.center === 'dash') return ['approach', 60];
      return null;
    };
    const ka = endKind(e.a, 'a'), kb = endKind(e.b, 'b');
    let a = s0, b = s1;
    if (ka) { spans.push([s0, Math.min(s1, s0 + ka[1]), ka[0]]); a = s0 + ka[1]; }
    if (kb) { spans.push([Math.max(a, s1 - kb[1]), s1, kb[0]]); b = s1 - kb[1]; }
    if (b > a) spans.push([a, b, S.center]);
    for (const [x0, x1, k] of spans) {
      line(st, (p) => map(p, S.shift), 0.12, PATTERN[k], k === 'faded' ? PAL.lineFaded : PAL.lineWhite, x0, x1, tl);
    }
  }

  /** Лінія розмітки вздовж зразків: зсув of(p), ширина w, штрих/проміжок (null — суцільна), s0…s1. */
  function line(st, of, w, pat, color, s0, s1, tl) {
    s0 = Math.max(s0, st[0].s); s1 = Math.min(s1, st[st.length - 1].s);
    if (s1 - s0 < 0.2) return;
    const c = col(color), pieces = [];
    if (!pat) pieces.push([s0, s1]);
    else for (let s = s0; s < s1 - 0.1; s += pat[0] + pat[1]) pieces.push([s, Math.min(s1, s + pat[0])]);
    for (const [a0, a1] of pieces) {
      const k = Math.max(1, Math.ceil((a1 - a0) / 4));
      for (let i = 0; i < k; i++) {
        const p = at(st, a0 + (a1 - a0) * i / k), q = at(st, a0 + (a1 - a0) * (i + 1) / k);
        const op = of(p), oq = of(q);
        batch.quad('mark', side(p, op - w / 2, MARK, tl), side(q, oq - w / 2, MARK, tl), side(q, oq + w / 2, MARK, tl), side(p, op + w / 2, MARK, tl), c);
      }
    }
  }

  // ---- латка перехрестя: віяло від центру, кути з радіусом, узбіччя довкола, розмітка
  function patch(j) {
    const { node, arms, corners } = j;
    const top = arms.reduce((m, A) => (A.rank > m.rank ? A : m), arms[0]);
    const asph = col(top.e.sec.surface || top.e.sec.asphalt), shCol = col(top.e.sec.shoulderCol);
    const Y = (x, z) => j.plane(x, z) + LIFT;
    // точки рукава: на відстані t від вершини, зсув вбік (wR праворуч / wL ліворуч у напрямку «від вершини»)
    const armPt = (A, t, sideSg, wOver) => {
      const e = A.e, p = at(e.pts, A.end === 'a' ? t : e.L - t);
      const fx = A.end === 'a' ? p.tx : -p.tx, fz = A.end === 'a' ? p.tz : -p.tz;     // від вершини
      const w = wOver != null ? wOver : sideSg > 0 ? A.wR : A.wL;
      const x = p.x - fz * w * sideSg, z = p.z + fx * w * sideSg;
      return { x, z, y: Y(x, z), ax: p.x, az: p.z, ay: Y(p.x, p.z) };
    };
    const ring = [], chains = [];
    corners.forEach((c) => {
      const A = arms[c.i], Bm = arms[c.j];
      // рукав i: правий і лівий край на обрізку, далі кут до правого краю наступного рукава j
      ring.push(armPt(A, A.trim, 1), armPt(A, A.trim, -1));
      const chain = [armPt(A, A.trim, -1)];
      if (!c.straight) {
        if (c.t1 < A.trim - 0.3) chain.push(armPt(A, c.t1, -1));
        // дуга від дотику до лівого краю i до дотику до правого краю j навколо C (коротшим шляхом)
        const T1 = chain[chain.length - 1], T2 = armPt(Bm, c.t2, 1);
        let a1 = Math.atan2(T1.z - c.C.z, T1.x - c.C.x), a2 = Math.atan2(T2.z - c.C.z, T2.x - c.C.x);
        let da = a2 - a1; while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI;
        const nSeg = Math.max(3, Math.ceil(Math.abs(da) / (Math.PI / 16)));
        for (let q = 1; q < nSeg; q++) {
          const a = a1 + da * q / nSeg, rr = Math.hypot(T1.x - c.C.x, T1.z - c.C.z) * (1 - q / nSeg) + Math.hypot(T2.x - c.C.x, T2.z - c.C.z) * (q / nSeg);
          const x = c.C.x + Math.cos(a) * rr, z = c.C.z + Math.sin(a) * rr;
          chain.push({ x, z, y: Y(x, z) });
        }
        chain.push(T2);
        if (c.t2 < Bm.trim - 0.3) chain.push(armPt(Bm, Bm.trim, 1));
      } else chain.push(armPt(Bm, Bm.trim, 1));
      for (let q = 1; q < chain.length - 1; q++) ring.push(chain[q]);
      chains.push({ c, chain });
    });
    // віяло
    const C0 = { x: node.x, z: node.z, y: Y(node.x, node.z) };
    for (let q = 0; q < ring.length; q++) {
      const a = ring[q], b = ring[(q + 1) % ring.length];
      batch.tri('road', C0, a, b, asph);
    }
    // узбіччя довкола кутів (назовні від центру)
    for (const { c, chain } of chains) {
      const A = arms[c.i], Bm = arms[c.j];
      const sh = Math.min(A.e.sec.sh, Bm.e.sec.sh);
      if (sh <= 0 || A.e.sec.walk || Bm.e.sec.walk) continue;
      const nrm = chain.map((p, q) => {
        const a = chain[Math.max(0, q - 1)], b = chain[Math.min(chain.length - 1, q + 1)];
        let nx = -(b.z - a.z), nz = b.x - a.x; const l = Math.hypot(nx, nz) || 1; nx /= l; nz /= l;
        if (nx * (p.x - node.x) + nz * (p.z - node.z) < 0) { nx = -nx; nz = -nz; }
        return [nx, nz];
      });
      for (let q = 1; q < chain.length; q++) {
        const a = chain[q - 1], b = chain[q], na = nrm[q - 1], nb = nrm[q];
        batch.quad('road', { x: a.x, y: a.y - 0.004, z: a.z }, { x: b.x, y: b.y - 0.004, z: b.z },
          { x: b.x + nb[0] * sh, y: b.y - 0.018, z: b.z + nb[1] * sh }, { x: a.x + na[0] * sh, y: a.y - 0.018, z: a.z + na[1] * sh }, shCol);
      }
      // крайова лінія траси вздовж «глухого» боку головної
      if (c.straight && A.e.sec.edge && Bm.e.sec.edge) {
        const inset = A.e.sec.paved - 0.1 - 0.075;
        const poly = chain.map((p, q) => ({ x: p.x - nrm[q][0] * inset, z: p.z - nrm[q][1] * inset, y: p.y }));
        line(toStations(poly), () => 0, 0.15, null, PAL.lineWhite, 0, 1e9);
      }
    }
    if (!j.main) return;
    // головна крізь латку: осьова (6/2 для траси) і крайова через гирло (1/3) з боку другорядних
    const A = arms[j.main[0]], Bm = arms[j.main[1]], S = A.e.sec;
    if (S.center !== 'none') {
      const pa = armPt(A, A.trim, 0), pb = armPt(Bm, Bm.trim, 0);
      const poly = [{ x: pa.ax, z: pa.az, y: pa.ay }, { x: node.x, z: node.z, y: C0.y }, { x: pb.ax, z: pb.az, y: pb.ay }];
      const k = arms.some((x) => x.minor) && S.center === 'dash' ? 'approach' : S.center;
      line(toStations(poly), () => 0, 0.12, PATTERN[k], k === 'faded' ? PAL.lineFaded : PAL.lineWhite, 0, 1e9);
    }
    if (S.edge) {
      // з якого боку (від A до B проти годинникової чи навпаки) є другорядні рукави
      const ia = j.main[0], ib = j.main[1];
      const between = (from, to) => { const r = []; for (let k = (from + 1) % arms.length; k !== to; k = (k + 1) % arms.length) r.push(k); return r; };
      for (const [from, to] of [[ia, ib], [ib, ia]]) {
        if (!between(from, to).length) continue;
        // лівий край «from» і правий край «to» (у напрямку від вершини) — гирло
        // лінія 1.2 іде на paved − 0,175 від краю проїзної частини всередину
        const F = arms[from], Tt = arms[to], d = F.e.sec.paved - 0.175;
        const pF = armPt(F, F.trim, -1, F.wL - d), pT = armPt(Tt, Tt.trim, 1, Tt.wR - d);
        const mx = (pF.x + pT.x) / 2 - node.x, mz = (pF.z + pT.z) / 2 - node.z, ml = Math.hypot(mx, mz) || 1;
        const wE = (F.wL + Tt.wR) / 2 - d;
        const mid = { x: node.x + mx / ml * wE, z: node.z + mz / ml * wE, y: C0.y };
        line(toStations([pF, mid, pT]), () => 0, 0.15, PATTERN.mouth, PAL.lineWhite, 0, 1e9);
      }
    }
    // «зубці» 1.13 на в'їзді другорядної сільської (не на ґрунтовці й не у двір)
    for (const M of arms) {
      if (!M.minor || M.e.sec.center === 'none') continue;
      const e = M.e, t = M.trim + 0.5;
      const p = at(e.pts, M.end === 'a' ? t : e.L - t);
      const fx = M.end === 'a' ? p.tx : -p.tx, fz = M.end === 'a' ? p.tz : -p.tz;
      // в'їзна смуга — ліворуч від напрямку «від вершини» (рух правосторонній)
      const w = M.wL, nT = Math.floor((w - 0.3) / 0.9);
      for (let k = 0; k < nT; k++) {
        const o = -(0.45 + k * 0.9);
        const P = (oo, tt) => { const x = p.x + fx * tt - fz * oo, z = p.z + fz * tt + fx * oo; return { x, y: p.y + e.tilt(p, x, z) + MARK, z }; };
        batch.tri('mark', P(o - 0.3, 0), P(o + 0.3, 0), P(o, 0.6), col(PAL.lineWhite));
      }
    }
  }

  // ---- відбійники: насип > 2 м, мости ±50 м, зовнішній бік крутих поворотів над насипом
  function guardrails(e, s0, s1) {
    const S = e.sec;
    if (S.barrier === 'none' || !opts.ground0) return;
    const bridges = e.bridges || [];
    const near = (s, d) => bridges.some(([b0, b1]) => s > b0 - d && s < b1 + d);
    const on = (s) => bridges.some(([b0, b1]) => s >= b0 && s <= b1);
    const step = 2, from = s0 + 12, to = s1 - 12;
    for (const sg of [1, -1]) {
      const need = [];
      for (let s = from; s <= to; s += step) {
        if (on(s)) { need.push(false); continue; }
        const p = at(e.pts, s), o = sg * (S.cw + 5);
        const drop = p.y - opts.ground0(p.x - p.tz * o, p.z + p.tx * o);
        // кривина: зміна курсу на 30 м; зовнішній бік — той, від якого дорога відвертає
        const q = at(e.pts, Math.min(e.L, s + 15)), r = at(e.pts, Math.max(0, s - 15));
        const turn = (r.tx * q.tz - r.tz * q.tx);                  // > 0 — праворуч від курсу? знак перевіряє outer
        const outer = Math.sign(-turn) === sg;
        const curvy = Math.abs(Math.asin(Math.max(-1, Math.min(1, turn)))) > 30 / 250;   // R < 250 м
        need.push(S.barrier === 'both' || near(s, 50) || drop > 2 || (curvy && outer && drop > 0.8));
      }
      // відрізки підряд; короткі (< 24 м) — геть, щілини < 16 м — заповнити
      let runs = [];
      need.forEach((v, k) => { const s = from + k * step; if (!v) return; const r = runs[runs.length - 1]; if (r && s - r[1] <= 16) r[1] = s; else runs.push([s, s]); });
      runs = runs.filter(([a, b]) => b - a >= 24 || near(a, 50));
      for (const [a, b] of runs) {
        // біля мосту відбійник підходить до парапету
        const off = (s) => {
          const base = S.hw + S.sh - 0.5, par = S.hw + 0.45;
          let d = Infinity; for (const [b0, b1] of bridges) d = Math.min(d, Math.abs(s - b0), Math.abs(s - b1));
          return d > 12 ? base : par + (base - par) * smooth(2, 12, d);
        };
        rail(e, sg, a, b, off);
      }
    }
  }

  function rail(e, sg, a, b, off) {
    let s = a;
    const ln = [];
    for (; s <= b + 0.01; s += 2) ln.push(at(e.pts, s));
    if (ln.length < 2) return;
    const Pt = (p, o, dy) => side(p, sg * o, dy);
    const metal = col(PAL.guardrail), post = col(PAL.post), refl = col(PAL.reflector);
    for (let k = 1; k < ln.length; k++) {
      const p = ln[k - 1], q = ln[k], op = off(p.s), oq = off(q.s);
      // W-профіль: три смуги лиця з легким зламом, зворот і верх
      for (const [y0, y1, d] of [[0.6, 0.7, 0], [0.7, 0.8, 0.04], [0.8, 0.9, 0]]) {
        batch.quad('metal', Pt(p, op + d, y0), Pt(q, oq + d, y0), Pt(q, oq + d, y1), Pt(p, op + d, y1), metal);
      }
      batch.quad('metal', Pt(p, op + 0.08, 0.6), Pt(q, oq + 0.08, 0.6), Pt(q, oq + 0.08, 0.9), Pt(p, op + 0.08, 0.9), col(PAL.guardrail, 0.8));
      batch.quad('metal', Pt(p, op, 0.9), Pt(q, oq, 0.9), Pt(q, oq + 0.08, 0.9), Pt(p, op + 0.08, 0.9), metal);
    }
    // стійки через 2 м за стрічкою, світлоповертач на кожній другій
    ln.forEach((p, k) => {
      const o = off(p.s) + 0.14, w = 0.06;
      const f = (oo, dd, y) => { const c = side(p, sg * oo, y); return { x: c.x + p.tx * dd, y: c.y, z: c.z + p.tz * dd }; };
      for (const [o0, o1, d0, d1] of [[o - w, o + w, -w, -w], [o + w, o + w, -w, w], [o + w, o - w, w, w], [o - w, o - w, w, -w]]) {
        batch.quad('metal', f(o0, d0, -0.3), f(o1, d1, -0.3), f(o1, d1, 0.82), f(o0, d0, 0.82), post);
      }
      if (k % 2 === 0) {
        const r0 = off(p.s) - 0.006;
        batch.quad('reflect', f(r0, -0.05, 0.71), f(r0, 0.05, 0.71), f(r0, 0.05, 0.79), f(r0, -0.05, 0.79), refl);
      }
    });
    // колайдери: коробки по ≤ 8 м від −0,25 до 1,55 над профілем — вище за брус (0,9): низ кабіни фури на ~0,88 м,
    // і з колайдером до 0,95 кабіна перелазила, а колеса-промені «їхали» по ньому (25.10, автопілот у відбійник)
    for (let s0 = a; s0 < b - 0.5; s0 += 8) {
      const s1 = Math.min(b, s0 + 8), p = at(e.pts, s0), q = at(e.pts, s1);
      const P = side(p, sg * (off(s0) + 0.05), 0), Q = side(q, sg * (off(s1) + 0.05), 0);
      const len = Math.hypot(Q.x - P.x, Q.z - P.z);
      out.guards.push({ x: (P.x + Q.x) / 2, y: (P.y + Q.y) / 2 + 0.65, z: (P.z + Q.z) / 2, yaw: Math.atan2(Q.x - P.x, Q.z - P.z), pitch: Math.atan2(Q.y - P.y, len), hx: 0.1, hy: 0.9, hz: len / 2 + 0.05 });
    }
    out.stats.guardM += b - a;
  }

  // ---- міст: настил (боки, низ, балка), бетонні парапети, устої й опори (колайдери настилу — у викликача, як були)
  function bridge(e, b0, b1) {
    out.stats.bridges++;
    const S = e.sec, st = stations(e.pts, b0, b1), hw = S.W / 2 + 0.5;
    const conc = col(PAL.bridge), dark = col(PAL.bridgeDark), parC = col(PAL.concrete);
    const P = (p, o, dy) => side(p, o + S.shift, dy);
    for (let i = 1; i < st.length; i++) {
      const a = st[i - 1], b = st[i];
      const Q = (o0, d0, o1, d1, c) => batch.quad('concrete', P(a, o0, d0), P(b, o0, d0), P(b, o1, d1), P(a, o1, d1), c);
      Q(-hw, 0, -hw, -0.6, conc); Q(hw, 0, hw, -0.6, conc);             // боки настилу
      Q(-hw, -0.6, hw, -0.6, dark);                                    // низ
      Q(-hw + 1.2, -0.6, -hw + 1.2, -1.5, dark); Q(hw - 1.2, -0.6, hw - 1.2, -1.5, dark); Q(-hw + 1.2, -1.5, hw - 1.2, -1.5, dark);
      Q(-hw, LIFT, -S.W / 2, LIFT, conc); Q(S.W / 2, LIFT, hw, LIFT, conc);     // кромки настилу до парапетів
      for (const sg of [1, -1]) {                                       // парапет 0,3 × 0,95
        const i0 = sg * (S.W / 2 + 0.3), i1 = sg * (S.W / 2 + 0.6);
        Q(i0, 0, i0, 0.95, parC); Q(i1, 0.95, i1, -0.6, parC); Q(i0, 0.95, i1, 0.95, col(PAL.concrete, 1.08));
      }
    }
    // торці парапетів і устої: стіна від настилу до землі
    const ground = opts.ground || (() => -1e9);
    for (const p of [st[0], st[st.length - 1]]) {
      const g = Math.min(ground(p.x, p.z), ground(p.x - p.tz * hw, p.z + p.tx * hw), ground(p.x + p.tz * hw, p.z - p.tx * hw)) - 1;
      const dyB = g - p.y;
      if (dyB < -0.7) for (const dd of [-0.8, 0.8]) {
        const q = { x: p.x + p.tx * dd, z: p.z + p.tz * dd, y: p.y, tx: p.tx, tz: p.tz };
        batch.quad('concrete', P(q, -hw - 0.5, -0.6), P(q, hw + 0.5, -0.6), P(q, hw + 0.5, dyB), P(q, -hw - 0.5, dyB), conc);
      }
      for (const sg of [1, -1]) {
        const i0 = sg * (S.W / 2 + 0.3), i1 = sg * (S.W / 2 + 0.6);
        batch.quad('concrete', P(p, i0, 0), P(p, i1, 0), P(p, i1, 0.95), P(p, i0, 0.95), parC);
      }
    }
    // опори-стіни через ≤ 25 м
    const span = b1 - b0, nP = Math.max(1, Math.round(span / 25) - 0);
    for (let k = 1; k <= nP; k++) {
      const s = b0 + span * k / (nP + 1), p = at(e.pts, s);
      const g = ground(p.x, p.z) - 1, h = p.y - 1.5 - g;
      if (h < 0.5) continue;
      const m = new THREE.Matrix4().compose(new THREE.Vector3(p.x, g + h / 2, p.z),
        new THREE.Quaternion().setFromEuler(new THREE.Euler(0, Math.atan2(p.tx, p.tz), 0)), new THREE.Vector3(S.W - 1, h, 1.2));
      batch.add('concrete', BOX(THREE), m, PAL.bridge);
    }
  }
}

let _box = null;
const BOX = (THREE) => _box || (_box = new THREE.BoxGeometry(1, 1, 1));

// ---------------------------------------------------------------- Житомир: ребра OSM → ребра дороги 2.0
/** Клас OSM `highway` (+ «в місті?») → клас дороги 2.0 (STYLE §5.4). */
export function osmClass(cls, urban) {
  const c = String(cls || '').replace(/_link$/, '');
  if (c === 'motorway' || c === 'trunk') return 'highway';
  if (c === 'primary') return urban ? 'street' : 'highway';
  if (c === 'secondary' || c === 'tertiary' || c === 'unclassified' || c === 'road') return urban ? 'street' : 'village';
  if (c === 'residential' || c === 'living_street') return urban ? 'street' : 'village';
  if (c === 'service') return 'yard';
  if (c === 'track') return 'dirt';
  return urban ? 'street' : 'village';
}

/**
 * Граф формату `fura-osm/1` (tools/osm/FORMAT.md) → мережа для planRoads/drawRoads. Не вмикається в грі — це вхід
 * для наступної сесії (рендер Житомира). opts: { urban(x, z) → bool, step (м, крок вісі, 4), median (м, 1) }.
 * Пари проспектів (`dual` + `pair`): кожна проїзна частина лишається своїм одностороннім ребром, але малюється
 * зсунутою назовні від середини пари (shift) — дві «злиплі» лінії OSM за 5 м стають дорогою з розділювачем.
 */
export function fromOsm(osm, opts = {}) {
  const step = opts.step || 4, urban = opts.urban || (() => true), median = opts.median != null ? opts.median : 1;
  const nodes = {};
  for (const [id, [x, z]] of Object.entries(osm.nodes || {})) nodes[id] = { x, z };
  const byId = new Map();
  const edges = [];
  for (const o of osm.edges || []) {
    if (o.tunnel && o.tunnel !== 'no') continue;
    const pts = resample(o.pts, step);
    const mid = pts[pts.length >> 1];
    const cls = osmClass(o.cls, urban(mid.x, mid.z));
    const e = { id: o.id, cls, a: o.a, b: o.b, pts, lanes: o.lanes, width: o.width, oneway: !!o.oneway, osm: o };
    if (o.bridge && o.bridge !== 'no' && o.bridge !== 'culvert') e.bridges = [[0, pts[pts.length - 1].s]];
    byId.set(o.id, e); edges.push(e);
  }
  // пари: зсув осі проїзної частини назовні на (dual − ширина) / 2 + розділювач / 2
  for (const e of edges) {
    const o = e.osm;
    if (!o.dual || !o.pair || !o.pair.length) continue;
    const other = byId.get(o.pair[0]);
    if (!other) continue;
    const S = section(e), p = e.pts[e.pts.length >> 1];
    let best = null, bd = Infinity;
    for (const q of other.pts) { const d = (q.x - p.x) ** 2 + (q.z - p.z) ** 2; if (d < bd) { bd = d; best = q; } }
    const right = ((best.x - p.x) * -p.tz + (best.z - p.z) * p.tx) > 0;       // пара праворуч від курсу?
    const away = right ? -1 : 1;                                               // зсув — від пари
    e.shift = away * Math.max(0, S.W / 2 + median / 2 - o.dual / 2);
    e.median = median;
  }
  return { edges, nodes };
}

/** [[x, z, y]] → згладжена вісь із кроком ~step м: [{x, z, y, tx, tz, s}] (Catmull-Rom, висота — лінійно). */
export function resample(raw, step) {
  const P = raw.map((p) => (Array.isArray(p) ? { x: p[0], z: p[1], y: p[2] || 0 } : p));
  const out = [];
  for (let i = 0; i < P.length - 1; i++) {
    const p0 = P[Math.max(0, i - 1)], p1 = P[i], p2 = P[i + 1], p3 = P[Math.min(P.length - 1, i + 2)];
    const n = Math.max(1, Math.ceil(Math.hypot(p2.x - p1.x, p2.z - p1.z) / step));
    for (let k = 0; k < n; k++) {
      const t = k / n, t2 = t * t, t3 = t2 * t;
      const f = (a, b, c, d) => 0.5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (-a + 3 * b - 3 * c + d) * t3);
      out.push({ x: f(p0.x, p1.x, p2.x, p3.x), z: f(p0.z, p1.z, p2.z, p3.z), y: p1.y + (p2.y - p1.y) * t });
    }
  }
  const L = P[P.length - 1];
  out.push({ x: L.x, z: L.z, y: L.y });
  let s = 0;
  out.forEach((p, i) => {
    const a = out[Math.max(0, i - 1)], b = out[Math.min(out.length - 1, i + 1)];
    const l = Math.hypot(b.x - a.x, b.z - a.z) || 1;
    p.tx = (b.x - a.x) / l; p.tz = (b.z - a.z) / l;
    if (i) s += Math.hypot(p.x - out[i - 1].x, p.z - out[i - 1].z);
    p.s = s;
  });
  return out;
}
